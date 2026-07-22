using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using FluentFTP;
using Renci.SshNet;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, name: "ClippedImageToPath.Singleton", createdNew: out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new ClipboardBridgeContext();
        Application.Run(context);
    }
}

internal sealed class ClipboardBridgeContext : ApplicationContext
{
    private const string AppName = "ClippedImageToPath";

    // Config
    private const string FilePrefix = "clipboard_";
    private const string FileExtension = ".png";
    private const int DebounceMs = 300;
    private const int ClipboardRetryCount = 8;
    private const int ClipboardRetryDelayMs = 40;
    private const int DeferredRetryIntervalMs = 150;
    private const int DeferredRetryMaxAttempts = 12;
    private const int ClipboardInjectDelayMs = 500;
    private const int SelfInjectIgnoreWindowMs = 3000;
    private static readonly int KeepLatestN = 500;
    private static readonly bool WriteLog = true;

    // State
    private DateTime _lastHandledUtc = DateTime.MinValue;
    private string? _lastImageSha256;
    private bool _isHandling;
    private int _deferredAttemptsRemaining;

    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly ClipboardListenerWindow _listenerWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly Icon _remoteUploadTrayIcon;
    private readonly Icon _uploadingTrayIcon;
    private readonly System.Windows.Forms.Timer _deferredProcessTimer;
    private readonly System.Windows.Forms.Timer _clipboardInjectTimer;
    private readonly System.Windows.Forms.Timer _uploadStatusTimer;
    private string? _pendingClipboardText;
    private Bitmap? _pendingClipboardImage;
    private string? _lastInjectedClipboardText;
    private string? _lastInjectedImageSha256;
    private ToolStripMenuItem? _remoteUploadStatusMenuItem;
    private DateTime _lastInjectedUtc = DateTime.MinValue;
    private int _activeUploadCount;
    private DateTime _uploadStartedUtc;

    internal ClipboardBridgeContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = AppSettings.Load();
        EnsureOutputDirectoryExists();

        _trayIcon = TrayIconFactory.CreateNormal();
        _remoteUploadTrayIcon = TrayIconFactory.CreateRemoteUploadEnabled();
        _uploadingTrayIcon = TrayIconFactory.CreateUploading();
        _notifyIcon = new NotifyIcon
        {
            Icon = GetIdleTrayIcon(),
            Text = GetIdleTrayText(),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _deferredProcessTimer = new System.Windows.Forms.Timer
        {
            Interval = DeferredRetryIntervalMs,
        };
        _deferredProcessTimer.Tick += (_, _) => OnDeferredRetryTick();

        _clipboardInjectTimer = new System.Windows.Forms.Timer
        {
            Interval = ClipboardInjectDelayMs,
        };
        _clipboardInjectTimer.Tick += (_, _) => OnClipboardInjectTick();

        _uploadStatusTimer = new System.Windows.Forms.Timer
        {
            Interval = 100,
        };
        _uploadStatusTimer.Tick += (_, _) => UpdateUploadTrayStatus();

        _listenerWindow = new ClipboardListenerWindow(HandleClipboardUpdate);
        Log("started");
    }

    private string LogFilePath => Path.Combine(_settings.OutputDirectory, "bridge.log");

    private void EnsureOutputDirectoryExists()
    {
        Directory.CreateDirectory(_settings.OutputDirectory);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _listenerWindow.Dispose();
            _deferredProcessTimer.Stop();
            _deferredProcessTimer.Dispose();
            _clipboardInjectTimer.Stop();
            _clipboardInjectTimer.Dispose();
            _uploadStatusTimer.Stop();
            _uploadStatusTimer.Dispose();
            _pendingClipboardImage?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _trayIcon.Dispose();
            _remoteUploadTrayIcon.Dispose();
            _uploadingTrayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        _remoteUploadStatusMenuItem = new ToolStripMenuItem
        {
            Enabled = false,
        };
        UpdateRemoteUploadStatusMenuItem();
        menu.Items.Add(_remoteUploadStatusMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open output folder", null, (_, _) =>
        {
            try
            {
                EnsureOutputDirectoryExists();
                _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _settings.OutputDirectory,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Ignore.
            }
        });
        menu.Items.Add("Settings", null, (_, _) => ShowSettingsDialog());
        menu.Items.Add("About", null, (_, _) => ShowAboutDialog());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowSettingsDialog()
    {
        using var settingsForm = new Form
        {
            Text = $"{AppName} Settings",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(620, 245),
        };

        var folderLabel = new Label
        {
            Text = "Output folder:",
            AutoSize = true,
            Location = new Point(16, 20),
        };

        var folderText = new TextBox
        {
            Text = _settings.OutputDirectory,
            Location = new Point(16, 42),
            Size = new Size(500, 26),
        };

        var browseButton = new Button
        {
            Text = "Browse...",
            Location = new Point(525, 40),
            Size = new Size(80, 28),
        };
        browseButton.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Choose where clipboard images will be saved",
                SelectedPath = folderText.Text,
                UseDescriptionForTitle = true,
            };
            if (picker.ShowDialog(settingsForm) == DialogResult.OK && !string.IsNullOrWhiteSpace(picker.SelectedPath))
            {
                folderText.Text = picker.SelectedPath;
            }
        };

        var wslCheck = new CheckBox
        {
            Text = "Convert clipboard path to WSL format (/mnt/c/...)",
            AutoSize = true,
            Location = new Point(16, 84),
            Checked = _settings.ConvertToWslPath,
        };

        var uploadCheck = new CheckBox
        {
            Text = "Remote upload enabled",
            AutoSize = true,
            Location = new Point(16, 116),
            Checked = _settings.RemoteUploadEnabled,
        };

        var sshButton = new Button
        {
            Text = "Remote credentials...",
            Location = new Point(16, 150),
            Size = new Size(150, 30),
        };
        sshButton.Click += (_, _) => ShowSshSettingsDialog(settingsForm);

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(440, 195),
            Size = new Size(80, 30),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(525, 195),
            Size = new Size(80, 30),
        };

        settingsForm.Controls.Add(folderLabel);
        settingsForm.Controls.Add(folderText);
        settingsForm.Controls.Add(browseButton);
        settingsForm.Controls.Add(wslCheck);
        settingsForm.Controls.Add(uploadCheck);
        settingsForm.Controls.Add(sshButton);
        settingsForm.Controls.Add(saveButton);
        settingsForm.Controls.Add(cancelButton);
        settingsForm.AcceptButton = saveButton;
        settingsForm.CancelButton = cancelButton;

        if (settingsForm.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var selectedFolder = folderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(selectedFolder))
        {
            MessageBox.Show("Output folder cannot be empty.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var fullFolder = Path.GetFullPath(selectedFolder);
            Directory.CreateDirectory(fullFolder);
            _settings.OutputDirectory = fullFolder;
            _settings.ConvertToWslPath = wslCheck.Checked;
            _settings.RemoteUploadEnabled = uploadCheck.Checked;
            _settings.Save();
            RefreshIdleTrayIcon();
            Log("settings updated");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save settings: {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static readonly RemoteProtocol[] ProtocolOrder =
    {
        RemoteProtocol.Sftp,
        RemoteProtocol.Ftp,
        RemoteProtocol.FtpsExplicit,
        RemoteProtocol.FtpsImplicit,
    };

    private static readonly string[] ProtocolLabels =
    {
        "SFTP (SSH)",
        "FTP (plain)",
        "FTPS (explicit TLS)",
        "FTPS (implicit TLS)",
    };

    private static int DefaultPortFor(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.Sftp => 22,
        RemoteProtocol.Ftp => 21,
        RemoteProtocol.FtpsExplicit => 21,
        RemoteProtocol.FtpsImplicit => 990,
        _ => 22,
    };

    private void ShowSshSettingsDialog(IWin32Window owner)
    {
        using var sshForm = new Form
        {
            Text = "Remote Credentials",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 352),
        };

        var protocolCombo = AddLabeledComboBox(sshForm, "Protocol:", ProtocolLabels, 16);
        var currentProtocol = _settings.GetRemoteProtocol();
        protocolCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ProtocolOrder, currentProtocol));

        var hostText = AddLabeledTextBox(sshForm, "Host:", _settings.SshHost, 58, false);
        var portText = AddLabeledTextBox(sshForm, "Port:", _settings.SshPort.ToString(), 100, false);
        var userText = AddLabeledTextBox(sshForm, "User:", _settings.SshUser, 142, false);
        var passwordText = AddLabeledTextBox(sshForm, "Password:", _settings.GetSshPassword(), 184, true);
        var remoteDirectoryText = AddLabeledTextBox(sshForm, "Remote directory:", _settings.RemoteDirectory, 226, false);

        var passiveCheck = new CheckBox
        {
            Text = "Use passive mode (FTP/FTPS)",
            AutoSize = true,
            Location = new Point(140, 272),
            Checked = _settings.PassiveMode,
        };
        sshForm.Controls.Add(passiveCheck);

        // When the user switches protocol, follow that protocol's conventional port if the
        // field still holds another protocol's default (i.e. it was never customized).
        var lastProtocol = currentProtocol;
        protocolCombo.SelectedIndexChanged += (_, _) =>
        {
            var newProtocol = ProtocolOrder[protocolCombo.SelectedIndex];
            if (int.TryParse(portText.Text.Trim(), out var currentPort) && currentPort == DefaultPortFor(lastProtocol))
            {
                portText.Text = DefaultPortFor(newProtocol).ToString();
            }

            lastProtocol = newProtocol;
        };

        RemoteProtocol SelectedProtocol() => ProtocolOrder[protocolCombo.SelectedIndex];

        var testButton = new Button
        {
            Text = "Test connection",
            Location = new Point(16, 308),
            Size = new Size(120, 30),
        };

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(275, 308),
            Size = new Size(80, 30),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(365, 308),
            Size = new Size(80, 30),
        };

        testButton.Click += async (_, _) =>
        {
            if (!TryReadSshDialogValues(SelectedProtocol(), passiveCheck.Checked, hostText, portText, userText, passwordText, remoteDirectoryText, out var config, out var error))
            {
                MessageBox.Show(error, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            testButton.Enabled = false;
            testButton.Text = "Testing...";
            try
            {
                await Task.Run(() => RemoteUploader.TestConnection(config));
                MessageBox.Show("Connection succeeded and the remote directory is available.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection test failed: {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                testButton.Text = "Test connection";
                testButton.Enabled = true;
            }
        };

        sshForm.Controls.Add(testButton);
        sshForm.Controls.Add(saveButton);
        sshForm.Controls.Add(cancelButton);
        sshForm.AcceptButton = saveButton;
        sshForm.CancelButton = cancelButton;

        if (sshForm.ShowDialog(owner) != DialogResult.OK)
        {
            return;
        }

        if (!TryReadSshDialogValues(SelectedProtocol(), passiveCheck.Checked, hostText, portText, userText, passwordText, remoteDirectoryText, out var sshConfig, out var validationError))
        {
            MessageBox.Show(validationError, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.SetRemoteProtocol(sshConfig.Protocol);
        _settings.PassiveMode = sshConfig.PassiveMode;
        _settings.SshHost = sshConfig.Host;
        _settings.SshPort = sshConfig.Port;
        _settings.SshUser = sshConfig.User;
        _settings.SetSshPassword(sshConfig.Password);
        _settings.RemoteDirectory = sshConfig.RemoteDirectory;
        _settings.Save();
        Log("remote credentials updated");
    }

    private static ComboBox AddLabeledComboBox(Form form, string labelText, string[] items, int y)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Location = new Point(16, y + 4),
        };

        var combo = new ComboBox
        {
            Location = new Point(140, y),
            Size = new Size(305, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        combo.Items.AddRange(items);

        form.Controls.Add(label);
        form.Controls.Add(combo);
        return combo;
    }

    private static TextBox AddLabeledTextBox(Form form, string labelText, string value, int y, bool usePasswordChar)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Location = new Point(16, y + 4),
        };

        var textBox = new TextBox
        {
            Text = value,
            Location = new Point(140, y),
            Size = new Size(305, 26),
            UseSystemPasswordChar = usePasswordChar,
        };

        form.Controls.Add(label);
        form.Controls.Add(textBox);
        return textBox;
    }

    private static bool TryReadSshDialogValues(
        RemoteProtocol protocol,
        bool passiveMode,
        TextBox hostText,
        TextBox portText,
        TextBox userText,
        TextBox passwordText,
        TextBox remoteDirectoryText,
        out RemoteUploadConfig config,
        out string error)
    {
        config = default;
        error = string.Empty;

        var host = hostText.Text.Trim();
        var user = userText.Text.Trim();
        var password = passwordText.Text;
        var remoteDirectory = remoteDirectoryText.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Host cannot be empty.";
            return false;
        }

        if (!int.TryParse(portText.Text.Trim(), out var port) || port <= 0 || port > 65535)
        {
            error = "Port must be a number from 1 to 65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(user))
        {
            error = "User cannot be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Password cannot be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteDirectory))
        {
            error = "Remote directory cannot be empty.";
            return false;
        }

        config = new RemoteUploadConfig(protocol, host, port, user, password, remoteDirectory, passiveMode);
        return true;
    }

    private void ShowAboutDialog()
    {
        var version = GetDisplayVersion();

        using var about = new Form
        {
            Text = $"About {AppName}",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(600, 260),
        };

        var title = new Label
        {
            AutoSize = false,
            Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
            Location = new Point(18, 18),
            Size = new Size(about.ClientSize.Width - 36, 24),
            Text = AppName,
        };

        var details = new TableLayoutPanel
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = true,
            ColumnCount = 2,
            Location = new Point(18, 54),
            RowCount = 4,
            Width = about.ClientSize.Width - 36,
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddAboutRow(details, 0, "Version", version);
        AddAboutRow(details, 1, "Build date", File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm:ss"));
        AddAboutRow(details, 2, "Build summary", "Added FTP/FTPS upload support alongside SFTP.");
        AddAboutRow(details, 3, "Copyright", $"(c) {DateTime.Now.Year} Alex LV");

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Size = new Size(80, 28),
            Location = new Point(about.ClientSize.Width - 96, about.ClientSize.Height - 44),
        };

        about.Controls.Add(title);
        about.Controls.Add(details);
        about.Controls.Add(close);
        about.AcceptButton = close;

        _ = about.ShowDialog();
    }

    private static string GetDisplayVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        var suffixIndex = version.IndexOf('+');
        return suffixIndex >= 0 ? version[..suffixIndex] : version;
    }

    private static void AddAboutRow(TableLayoutPanel table, int row, string label, string value)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        table.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 0, 12, 8),
            Text = $"{label}:",
        }, 0, row);

        table.Controls.Add(new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 0, 0, 8),
            MaximumSize = new Size(460, 0),
            Text = value,
        }, 1, row);
    }

    private void HandleClipboardUpdate()
    {
        MaybeCancelPendingClipboardInject();

        if (_isHandling)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastHandledUtc).TotalMilliseconds < DebounceMs)
        {
            return;
        }

        _isHandling = true;
        try
        {
            _lastHandledUtc = nowUtc;
            EnsureOutputDirectoryExists();
            var outputDirectory = _settings.OutputDirectory;

            var existingText = TryGetClipboardText();
            if (IsRecentOwnInjectedClipboardText(existingText))
            {
                return;
            }

            if (IsOwnOutputPathText(existingText, outputDirectory))
            {
                return;
            }

            using var image = TryGetClipboardImage();
            if (image is null)
            {
                if (!TryClipboardHasText())
                {
                    StartDeferredRetry();
                }
                return;
            }

            StopDeferredRetry();

            var pngBytes = EncodePng(image);
            var imageHash = ComputeSha256Hex(pngBytes);
            if (_lastImageSha256 == imageHash || IsRecentOwnInjectedImageHash(imageHash))
            {
                return;
            }

            var filePath = BuildUniqueFilePath(outputDirectory);
            File.WriteAllBytes(filePath, pngBytes);

            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0)
            {
                throw new IOException($"Saved file is missing or empty: {filePath}");
            }

            var clipboardPath = _settings.ConvertToWslPath
                ? ConvertWindowsPathToWsl(filePath)
                : filePath;
            var quotedPath = QuoteForClipboard(clipboardPath);
            ScheduleClipboardInject(quotedPath, image);

            _lastImageSha256 = imageHash;

            CleanupOldFilesIfNeeded(outputDirectory);
            Log($"saved {filePath}");
            QueueRemoteUploadIfEnabled(filePath);
        }
        catch (Exception ex)
        {
            Log($"error {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isHandling = false;
        }
    }

    private void OnDeferredRetryTick()
    {
        if (_deferredAttemptsRemaining <= 0)
        {
            StopDeferredRetry();
            return;
        }

        _deferredAttemptsRemaining--;
        HandleClipboardUpdateIgnoringDebounce();
    }

    private void HandleClipboardUpdateIgnoringDebounce()
    {
        if (_isHandling)
        {
            return;
        }

        _isHandling = true;
        try
        {
            EnsureOutputDirectoryExists();
            var outputDirectory = _settings.OutputDirectory;

            var existingText = TryGetClipboardText();
            if (IsRecentOwnInjectedClipboardText(existingText))
            {
                StopDeferredRetry();
                return;
            }

            if (IsOwnOutputPathText(existingText, outputDirectory))
            {
                StopDeferredRetry();
                return;
            }

            using var image = TryGetClipboardImage();
            if (image is null)
            {
                if (_deferredAttemptsRemaining <= 0)
                {
                    StopDeferredRetry();
                }
                return;
            }

            StopDeferredRetry();

            var pngBytes = EncodePng(image);
            var imageHash = ComputeSha256Hex(pngBytes);
            if (_lastImageSha256 == imageHash || IsRecentOwnInjectedImageHash(imageHash))
            {
                return;
            }

            var filePath = BuildUniqueFilePath(outputDirectory);
            File.WriteAllBytes(filePath, pngBytes);

            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length == 0)
            {
                throw new IOException($"Saved file is missing or empty: {filePath}");
            }

            var clipboardPath = _settings.ConvertToWslPath
                ? ConvertWindowsPathToWsl(filePath)
                : filePath;
            var quotedPath = QuoteForClipboard(clipboardPath);
            ScheduleClipboardInject(quotedPath, image);

            _lastImageSha256 = imageHash;
            CleanupOldFilesIfNeeded(outputDirectory);
            Log($"saved {filePath}");
            QueueRemoteUploadIfEnabled(filePath);
        }
        catch (Exception ex)
        {
            Log($"error {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _isHandling = false;
        }
    }

    private void StartDeferredRetry()
    {
        if (_deferredAttemptsRemaining < DeferredRetryMaxAttempts)
        {
            _deferredAttemptsRemaining = DeferredRetryMaxAttempts;
        }

        if (!_deferredProcessTimer.Enabled)
        {
            _deferredProcessTimer.Start();
        }
    }

    private void StopDeferredRetry()
    {
        _deferredAttemptsRemaining = 0;
        if (_deferredProcessTimer.Enabled)
        {
            _deferredProcessTimer.Stop();
        }
    }

    private void QueueRemoteUploadIfEnabled(string filePath)
    {
        if (!_settings.RemoteUploadEnabled)
        {
            return;
        }

        if (!_settings.TryGetRemoteUploadConfig(out var config, out var error))
        {
            Log($"upload skipped: {error}");
            return;
        }

        BeginUploadStatus();
        _ = Task.Run(() =>
        {
            try
            {
                var remotePath = RemoteUploader.UploadFile(filePath, config);
                Log($"uploaded {filePath} -> {remotePath}");
            }
            catch (Exception ex)
            {
                Log($"upload error {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                FinishUploadStatus();
            }
        });
    }

    private void BeginUploadStatus()
    {
        _uiContext.Post(_ =>
        {
            _activeUploadCount++;
            if (_activeUploadCount == 1)
            {
                _uploadStartedUtc = DateTime.UtcNow;
                _notifyIcon.Icon = _uploadingTrayIcon;
                _uploadStatusTimer.Start();
            }

            UpdateUploadTrayStatus();
        }, null);
    }

    private void FinishUploadStatus()
    {
        _uiContext.Post(_ =>
        {
            if (_activeUploadCount > 0)
            {
                _activeUploadCount--;
            }

            if (_activeUploadCount == 0)
            {
                _uploadStatusTimer.Stop();
                _notifyIcon.Icon = GetIdleTrayIcon();
                _notifyIcon.Text = GetIdleTrayText();
            }
        }, null);
    }

    private Icon GetIdleTrayIcon()
    {
        return _settings.RemoteUploadEnabled ? _remoteUploadTrayIcon : _trayIcon;
    }

    private void RefreshIdleTrayIcon()
    {
        UpdateRemoteUploadStatusMenuItem();
        if (_activeUploadCount == 0)
        {
            _notifyIcon.Icon = GetIdleTrayIcon();
            _notifyIcon.Text = GetIdleTrayText();
        }
    }

    private string GetIdleTrayText()
    {
        return _settings.RemoteUploadEnabled ? $"{AppName} - Remote upload enabled" : AppName;
    }

    private void UpdateRemoteUploadStatusMenuItem()
    {
        if (_remoteUploadStatusMenuItem is not null)
        {
            var status = _settings.RemoteUploadEnabled ? "enabled" : "disabled";
            _remoteUploadStatusMenuItem.Text = $"Remote upload: {status}";
        }
    }

    private void UpdateUploadTrayStatus()
    {
        if (_activeUploadCount <= 0)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _uploadStartedUtc;
        var text = $"{AppName} uploading {elapsed.TotalSeconds:0.0}s";
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private void ScheduleClipboardInject(string text, Image image)
    {
        _lastInjectedClipboardText = text;
        _lastInjectedImageSha256 = ComputeSha256Hex(EncodePng(image));
        _lastInjectedUtc = DateTime.UtcNow;

        _pendingClipboardImage?.Dispose();
        _pendingClipboardImage = new Bitmap(image);
        _pendingClipboardText = text;
        _clipboardInjectTimer.Stop();
        _clipboardInjectTimer.Start();
    }

    private void CancelPendingClipboardInject()
    {
        _clipboardInjectTimer.Stop();
        _pendingClipboardImage?.Dispose();
        _pendingClipboardImage = null;
        _pendingClipboardText = null;
    }

    private void MaybeCancelPendingClipboardInject()
    {
        if (_pendingClipboardImage is null || string.IsNullOrWhiteSpace(_pendingClipboardText))
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsImage())
            {
                return;
            }

            var text = TryGetClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (IsOwnOutputPathText(text, _settings.OutputDirectory))
            {
                return;
            }

            // Clipboard changed to other text content before delayed injection fired.
            CancelPendingClipboardInject();
        }
        catch (ExternalException)
        {
            // Keep pending state and try again on next update.
        }
    }

    private bool IsRecentOwnInjectedClipboardText(string? text)
    {
        if ((DateTime.UtcNow - _lastInjectedUtc).TotalMilliseconds > SelfInjectIgnoreWindowMs)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_lastInjectedClipboardText))
        {
            return false;
        }

        return string.Equals(text.Trim(), _lastInjectedClipboardText, StringComparison.Ordinal);
    }

    private bool IsRecentOwnInjectedImageHash(string imageHash)
    {
        if ((DateTime.UtcNow - _lastInjectedUtc).TotalMilliseconds > SelfInjectIgnoreWindowMs)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_lastInjectedImageSha256))
        {
            return false;
        }

        return string.Equals(imageHash, _lastInjectedImageSha256, StringComparison.Ordinal);
    }

    private void OnClipboardInjectTick()
    {
        _clipboardInjectTimer.Stop();

        if (_pendingClipboardImage is null || string.IsNullOrWhiteSpace(_pendingClipboardText))
        {
            return;
        }

        try
        {
            if (!TrySetClipboardContent(_pendingClipboardText, _pendingClipboardImage))
            {
                Log("error IOException: delayed clipboard injection failed");
            }
        }
        finally
        {
            _pendingClipboardImage.Dispose();
            _pendingClipboardImage = null;
            _pendingClipboardText = null;
        }
    }

    private static bool TryClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    private static string BuildUniqueFilePath(string outputDirectory)
    {
        var baseName = $"{FilePrefix}{DateTime.Now:yyyy-MM-dd_HH-mm-ss_fff}";
        var path = Path.Combine(outputDirectory, baseName + FileExtension);

        if (!File.Exists(path))
        {
            return path;
        }

        var suffix = Guid.NewGuid().ToString("N")[..6];
        return Path.Combine(outputDirectory, $"{baseName}_{suffix}{FileExtension}");
    }

    private static byte[] EncodePng(Image image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static string ComputeSha256Hex(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            _ = sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    private static string QuoteForClipboard(string path) => $"\"{path}\"";

    private static string ConvertWindowsPathToWsl(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return full.Replace('\\', '/');
        }

        var drive = char.ToLowerInvariant(root[0]);
        var tail = full[root.Length..].Replace('\\', '/');
        return $"/mnt/{drive}/{tail}";
    }

    private static bool IsOwnOutputPathText(string? text, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        if (!trimmed.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var full = Path.GetFullPath(trimmed);
        var target = Path.GetFullPath(outputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return File.Exists(full);
    }

    private static Image? TryGetClipboardImage()
    {
        for (var i = 0; i < ClipboardRetryCount; i++)
        {
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    return null;
                }

                var img = Clipboard.GetImage();
                if (img is null)
                {
                    return null;
                }

                return new Bitmap(img);
            }
            catch (ExternalException)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return null;
    }

    private static string? TryGetClipboardText()
    {
        for (var i = 0; i < ClipboardRetryCount; i++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return null;
    }

    private static bool TrySetClipboardContent(string text, Image image)
    {
        for (var i = 0; i < ClipboardRetryCount; i++)
        {
            try
            {
                // Keep image data so clipboard managers can still treat this as an image clip,
                // and add text so terminals paste the saved file path.
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                data.SetText(text, TextDataFormat.Text);

                using var bmp = new Bitmap(image);
                data.SetImage(bmp);

                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return false;
    }

    private static void CleanupOldFilesIfNeeded(string outputDirectory)
    {
        if (KeepLatestN <= 0)
        {
            return;
        }

        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        var files = new DirectoryInfo(outputDirectory)
            .GetFiles($"{FilePrefix}*{FileExtension}", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();

        if (files.Count <= KeepLatestN)
        {
            return;
        }

        foreach (var file in files.Skip(KeepLatestN))
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private void Log(string line)
    {
        if (!WriteLog)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.OutputDirectory);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(LogFilePath, $"[{stamp}] {line}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures.
        }
    }
}

internal sealed class AppSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "ClipboardImages");

    public bool ConvertToWslPath { get; set; }

    public bool RemoteUploadEnabled { get; set; }

    // Persisted as a string for forward/backward compatibility. One of:
    // "Sftp", "Ftp", "FtpsExplicit", "FtpsImplicit".
    public string Protocol { get; set; } = "Sftp";

    public bool PassiveMode { get; set; } = true;

    public string SshHost { get; set; } = string.Empty;

    public int SshPort { get; set; } = 22;

    public string SshUser { get; set; } = string.Empty;

    public string SshPasswordBase64 { get; set; } = string.Empty;

    public string RemoteDirectory { get; set; } = ".";

    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClippedImageToPath");

    private static string SettingsFilePath => Path.Combine(SettingsDirectory, "settings.json");

    internal static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsFilePath);
            var parsed = JsonSerializer.Deserialize<AppSettings>(json);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.OutputDirectory))
            {
                return new AppSettings();
            }

            parsed.OutputDirectory = Path.GetFullPath(parsed.OutputDirectory);
            if (parsed.SshPort <= 0 || parsed.SshPort > 65535)
            {
                parsed.SshPort = 22;
            }

            if (string.IsNullOrWhiteSpace(parsed.RemoteDirectory))
            {
                parsed.RemoteDirectory = ".";
            }

            return parsed;
        }
        catch
        {
            return new AppSettings();
        }
    }

    internal void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
    }

    internal string GetSshPassword()
    {
        if (string.IsNullOrWhiteSpace(SshPasswordBase64))
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(SshPasswordBase64));
        }
        catch
        {
            return string.Empty;
        }
    }

    internal void SetSshPassword(string password)
    {
        SshPasswordBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
    }

    internal RemoteProtocol GetRemoteProtocol()
    {
        return Enum.TryParse<RemoteProtocol>(Protocol, ignoreCase: true, out var parsed)
            ? parsed
            : RemoteProtocol.Sftp;
    }

    internal void SetRemoteProtocol(RemoteProtocol protocol)
    {
        Protocol = protocol.ToString();
    }

    internal bool TryGetRemoteUploadConfig(out RemoteUploadConfig config, out string error)
    {
        config = default;
        error = string.Empty;

        var password = GetSshPassword();
        if (string.IsNullOrWhiteSpace(SshHost))
        {
            error = "SSH host is not configured";
            return false;
        }

        if (SshPort <= 0 || SshPort > 65535)
        {
            error = "SSH port is invalid";
            return false;
        }

        if (string.IsNullOrWhiteSpace(SshUser))
        {
            error = "SSH user is not configured";
            return false;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            error = "SSH password is not configured";
            return false;
        }

        if (string.IsNullOrWhiteSpace(RemoteDirectory))
        {
            error = "remote directory is not configured";
            return false;
        }

        config = new RemoteUploadConfig(GetRemoteProtocol(), SshHost, SshPort, SshUser, password, RemoteDirectory, PassiveMode);
        return true;
    }
}

internal enum RemoteProtocol
{
    Sftp,
    Ftp,
    FtpsExplicit,
    FtpsImplicit,
}

internal readonly record struct RemoteUploadConfig(
    RemoteProtocol Protocol,
    string Host,
    int Port,
    string User,
    string Password,
    string RemoteDirectory,
    bool PassiveMode);

internal static class RemoteUploader
{
    internal static void TestConnection(RemoteUploadConfig config)
    {
        if (config.Protocol == RemoteProtocol.Sftp)
        {
            SftpTestConnection(config);
        }
        else
        {
            FtpTestConnection(config);
        }
    }

    internal static string UploadFile(string localFilePath, RemoteUploadConfig config)
    {
        return config.Protocol == RemoteProtocol.Sftp
            ? SftpUploadFile(localFilePath, config)
            : FtpUploadFile(localFilePath, config);
    }

    private static void SftpTestConnection(RemoteUploadConfig config)
    {
        using var client = CreateSftpClient(config);
        client.Connect();
        try
        {
            if (!client.Exists(config.RemoteDirectory))
            {
                throw new DirectoryNotFoundException($"Remote directory does not exist: {config.RemoteDirectory}");
            }
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static string SftpUploadFile(string localFilePath, RemoteUploadConfig config)
    {
        var fileName = Path.GetFileName(localFilePath);
        var remotePath = CombineRemotePath(config.RemoteDirectory, fileName);

        using var client = CreateSftpClient(config);
        client.Connect();
        try
        {
            if (!client.Exists(config.RemoteDirectory))
            {
                throw new DirectoryNotFoundException($"Remote directory does not exist: {config.RemoteDirectory}");
            }

            using var stream = File.OpenRead(localFilePath);
            client.UploadFile(stream, remotePath, true);
            return remotePath;
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static SftpClient CreateSftpClient(RemoteUploadConfig config)
    {
        var client = new SftpClient(config.Host, config.Port, config.User, config.Password)
        {
            OperationTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        };
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static void FtpTestConnection(RemoteUploadConfig config)
    {
        using var client = CreateFtpClient(config);
        client.Connect();
        try
        {
            if (!client.DirectoryExists(config.RemoteDirectory))
            {
                throw new DirectoryNotFoundException($"Remote directory does not exist: {config.RemoteDirectory}");
            }
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static string FtpUploadFile(string localFilePath, RemoteUploadConfig config)
    {
        var fileName = Path.GetFileName(localFilePath);
        var remotePath = CombineRemotePath(config.RemoteDirectory, fileName);

        using var client = CreateFtpClient(config);
        client.Connect();
        try
        {
            if (!client.DirectoryExists(config.RemoteDirectory))
            {
                throw new DirectoryNotFoundException($"Remote directory does not exist: {config.RemoteDirectory}");
            }

            var status = client.UploadFile(localFilePath, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: false);
            if (status == FtpStatus.Failed)
            {
                throw new IOException($"FTP upload failed: {remotePath}");
            }

            return remotePath;
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static FtpClient CreateFtpClient(RemoteUploadConfig config)
    {
        var client = new FtpClient(config.Host, config.User, config.Password, config.Port);
        client.Config.EncryptionMode = config.Protocol switch
        {
            RemoteProtocol.FtpsExplicit => FtpEncryptionMode.Explicit,
            RemoteProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        };

        // Local FileZilla servers typically use a self-signed certificate, so trust any presented cert.
        client.Config.ValidateAnyCertificate = true;
        client.Config.DataConnectionType = config.PassiveMode
            ? FtpDataConnectionType.AutoPassive
            : FtpDataConnectionType.AutoActive;
        client.Config.ConnectTimeout = 15000;
        client.Config.ReadTimeout = 30000;
        client.Config.DataConnectionConnectTimeout = 15000;
        client.Config.DataConnectionReadTimeout = 30000;
        return client;
    }

    private static string CombineRemotePath(string directory, string fileName)
    {
        var trimmed = directory.TrimEnd('/');
        return string.IsNullOrEmpty(trimmed) ? fileName : $"{trimmed}/{fileName}";
    }
}

internal sealed class ClipboardListenerWindow : NativeWindow, IDisposable
{
    private readonly Action _onClipboardUpdate;

    internal ClipboardListenerWindow(Action onClipboardUpdate)
    {
        _onClipboardUpdate = onClipboardUpdate;

        CreateHandle(new CreateParams
        {
            Caption = "ClippedImageToPathListener",
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            Style = 0,
        });

        if (!NativeMethods.AddClipboardFormatListener(Handle))
        {
            throw new InvalidOperationException("Failed to register clipboard listener.");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            _onClipboardUpdate();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            NativeMethods.RemoveClipboardFormatListener(Handle);
            DestroyHandle();
        }
    }
}

internal static class TrayIconFactory
{
    internal static Icon CreateNormal()
    {
        return Create(Color.FromArgb(0, 120, 80), Color.White, drawUploadBadge: false);
    }

    internal static Icon CreateRemoteUploadEnabled()
    {
        return Create(Color.FromArgb(0, 120, 80), Color.FromArgb(255, 210, 0), drawUploadBadge: false);
    }

    internal static Icon CreateUploading()
    {
        return Create(Color.FromArgb(0, 95, 170), Color.White, drawUploadBadge: true);
    }

    private static Icon Create(Color backgroundColor, Color foregroundColor, bool drawUploadBadge)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(backgroundColor);
            FillRoundedRectangle(g, bgBrush, 2, 2, 28, 28, 7);

            using var fillBrush = new SolidBrush(Color.FromArgb(60, foregroundColor));
            g.FillRectangle(fillBrush, 9, 8, 10, 8);
            g.FillRectangle(fillBrush, 13, 18, 8, 3);

            using var pen = new Pen(foregroundColor, 2f);
            g.DrawRectangle(pen, 8, 7, 12, 10);
            g.DrawLine(pen, 12, 20, 24, 20);
            g.DrawLine(pen, 24, 20, 24, 12);

            if (drawUploadBadge)
            {
                using var badgeBrush = new SolidBrush(Color.FromArgb(255, 190, 45));
                g.FillEllipse(badgeBrush, 17, 3, 12, 12);
                using var badgePen = new Pen(Color.White, 1.6f);
                g.DrawLine(badgePen, 23, 12, 23, 6);
                g.DrawLine(badgePen, 20, 9, 23, 6);
                g.DrawLine(badgePen, 26, 9, 23, 6);
            }
        }

        var hIcon = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static void FillRoundedRectangle(Graphics g, Brush brush, int x, int y, int width, int height, int radius)
    {
        using var path = new GraphicsPath();
        path.AddArc(x, y, radius, radius, 180, 90);
        path.AddArc(x + width - radius, y, radius, radius, 270, 90);
        path.AddArc(x + width - radius, y + height - radius, radius, radius, 0, 90);
        path.AddArc(x, y + height - radius, radius, radius, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}

internal static class NativeMethods
{
    internal const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
