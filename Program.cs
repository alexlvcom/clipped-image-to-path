using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private const int ClipboardRestoreAfterKeyUpMs = 100;
    private const int ClipboardRestoreSafetyTimeoutMs = 2000;
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
    private readonly System.Windows.Forms.Timer _clipboardRestoreTimer;
    private readonly System.Windows.Forms.Timer _uploadStatusTimer;
    private readonly System.Windows.Forms.Timer _remoteUploadReminderTimer;
    private readonly GlobalPasteMonitor? _pasteMonitor;
    private string? _pendingClipboardText;
    private Bitmap? _pendingClipboardImage;
    private string? _lastInjectedClipboardText;
    private string? _lastInjectedImageSha256;
    private string? _activeClipboardPath;
    private Bitmap? _activeClipboardImage;
    private string? _activeClipboardImageSha256;
    private uint _activeClipboardSequence;
    private uint _temporaryPathSequence;
    private ToolStripMenuItem? _remoteUploadStatusMenuItem;
    private ToolStripMenuItem? _activeServerMenuItem;
    private bool _suppressMenuCloseOnce;
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

        _clipboardRestoreTimer = new System.Windows.Forms.Timer
        {
            Interval = ClipboardRestoreAfterKeyUpMs,
        };
        _clipboardRestoreTimer.Tick += (_, _) => RestoreImageClipboardAfterTerminalPaste();

        _uploadStatusTimer = new System.Windows.Forms.Timer
        {
            Interval = 100,
        };
        _uploadStatusTimer.Tick += (_, _) => UpdateUploadTrayStatus();

        _remoteUploadReminderTimer = new System.Windows.Forms.Timer
        {
            Interval = GetRemoteUploadReminderIntervalMs(),
        };
        _remoteUploadReminderTimer.Tick += (_, _) => ShowRemoteUploadReminder();
        UpdateRemoteUploadReminderTimer();

        _listenerWindow = new ClipboardListenerWindow(HandleClipboardUpdate);
        try
        {
            _pasteMonitor = new GlobalPasteMonitor(HandlePasteShortcut, HandlePasteShortcutReleased);
        }
        catch (Exception ex)
        {
            _pasteMonitor = null;
            Log($"smart paste unavailable: {ex.Message}");
        }
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
            _clipboardRestoreTimer.Stop();
            _clipboardRestoreTimer.Dispose();
            _uploadStatusTimer.Stop();
            _uploadStatusTimer.Dispose();
            _remoteUploadReminderTimer.Stop();
            _remoteUploadReminderTimer.Dispose();
            _pasteMonitor?.Dispose();
            _pendingClipboardImage?.Dispose();
            _activeClipboardImage?.Dispose();
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

        _remoteUploadStatusMenuItem = new ToolStripMenuItem("Enable Remote Upload")
        {
            CheckOnClick = false,
        };
        _remoteUploadStatusMenuItem.Click += (_, _) => ToggleRemoteUpload();
        UpdateRemoteUploadStatusMenuItem();
        menu.Items.Add(_remoteUploadStatusMenuItem);

        _activeServerMenuItem = new ToolStripMenuItem("Active server");
        menu.Items.Add(_activeServerMenuItem);
        menu.Opening += (_, _) => RebuildActiveServerMenu();

        // Keep the menu open when the user toggles remote upload, so they can see the state flip.
        menu.Closing += (_, e) =>
        {
            if (_suppressMenuCloseOnce && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
            {
                e.Cancel = true;
            }

            _suppressMenuCloseOnce = false;
        };

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
            ClientSize = new Size(620, 315),
        };

        using var settingsToolTip = new ToolTip
        {
            InitialDelay = 400,
            ReshowDelay = 100,
            AutoPopDelay = 10000,
            ShowAlways = true,
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
            Location = new Point(16, 148),
            Checked = _settings.RemoteUploadEnabled,
        };

        var smartPasteCheck = new CheckBox
        {
            Text = "Paste saved image path with Shift+Insert",
            AutoSize = true,
            Location = new Point(16, 116),
            Checked = _settings.SmartPasteEnabled,
            Enabled = _pasteMonitor is not null,
        };

        var uploadReminderCheck = new CheckBox
        {
            Text = "Show remote upload notification every",
            AutoSize = true,
            Location = new Point(16, 180),
            Checked = _settings.RemoteUploadReminderEnabled,
        };

        var uploadReminderMinutes = new NumericUpDown
        {
            Location = new Point(260, 177),
            Size = new Size(70, 26),
            Minimum = AppSettings.MinRemoteUploadReminderMinutes,
            Maximum = AppSettings.MaxRemoteUploadReminderMinutes,
            Value = _settings.RemoteUploadReminderMinutes,
            TextAlign = HorizontalAlignment.Right,
        };

        var uploadReminderMinutesLabel = new Label
        {
            Text = "minutes",
            AutoSize = true,
            Location = new Point(337, 181),
        };

        void UpdateUploadReminderControls()
        {
            uploadReminderCheck.Enabled = uploadCheck.Checked;
            uploadReminderMinutes.Enabled = uploadCheck.Checked && uploadReminderCheck.Checked;
            uploadReminderMinutesLabel.Enabled = uploadReminderMinutes.Enabled;
        }

        uploadCheck.CheckedChanged += (_, _) => UpdateUploadReminderControls();
        uploadReminderCheck.CheckedChanged += (_, _) => UpdateUploadReminderControls();
        UpdateUploadReminderControls();

        var sshButton = new Button
        {
            Text = "Remote servers...",
            Location = new Point(16, 216),
            Size = new Size(150, 30),
        };
        sshButton.Click += (_, _) => ShowRemoteServersDialog(settingsForm);

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(440, 265),
            Size = new Size(80, 30),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(525, 265),
            Size = new Size(80, 30),
        };

        const string outputFolderHelp = "Folder where copied clipboard images are saved as PNG files.";
        settingsToolTip.SetToolTip(folderLabel, outputFolderHelp);
        settingsToolTip.SetToolTip(folderText, outputFolderHelp);
        settingsToolTip.SetToolTip(browseButton, "Choose the folder where copied clipboard images are saved.");
        settingsToolTip.SetToolTip(wslCheck, "Copy saved paths in /mnt/... format for use in WSL terminals.");
        settingsToolTip.SetToolTip(smartPasteCheck, "Keep screenshots available for normal Ctrl+V, and paste the saved file path with Shift+Insert.");
        settingsToolTip.SetToolTip(uploadCheck, "Upload each saved clipboard image to the selected remote server when enabled.");
        settingsToolTip.SetToolTip(uploadReminderCheck, "Remind you that remote upload is still on so you can disable it when no longer needed.");
        settingsToolTip.SetToolTip(uploadReminderMinutes, "Choose how often the remote upload notification appears, from 1 to 1,440 minutes.");
        settingsToolTip.SetToolTip(uploadReminderMinutesLabel, "Choose how often the remote upload notification appears, from 1 to 1,440 minutes.");
        settingsToolTip.SetToolTip(sshButton, "Add, edit, remove, select, and test remote server profiles.");
        settingsToolTip.SetToolTip(saveButton, "Save these settings and apply them immediately.");
        settingsToolTip.SetToolTip(cancelButton, "Close without saving changes.");

        settingsForm.Controls.Add(folderLabel);
        settingsForm.Controls.Add(folderText);
        settingsForm.Controls.Add(browseButton);
        settingsForm.Controls.Add(wslCheck);
        settingsForm.Controls.Add(smartPasteCheck);
        settingsForm.Controls.Add(uploadCheck);
        settingsForm.Controls.Add(uploadReminderCheck);
        settingsForm.Controls.Add(uploadReminderMinutes);
        settingsForm.Controls.Add(uploadReminderMinutesLabel);
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
            _settings.SmartPasteEnabled = smartPasteCheck.Checked;
            _settings.RemoteUploadEnabled = uploadCheck.Checked;
            _settings.RemoteUploadReminderEnabled = uploadReminderCheck.Checked;
            _settings.RemoteUploadReminderMinutes = Decimal.ToInt32(uploadReminderMinutes.Value);
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

    private void ShowRemoteServersDialog(IWin32Window owner)
    {
        using var form = new Form
        {
            Text = "Remote Servers",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(540, 360),
        };

        var hint = new Label
        {
            Text = "The server marked ● is used for uploads. Double-click to edit.",
            AutoSize = true,
            Location = new Point(16, 12),
        };

        var list = new ListBox
        {
            Location = new Point(16, 36),
            Size = new Size(380, 300),
            IntegralHeight = false,
        };

        void Reload(int selectIndex)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var server in _settings.RemoteServers)
            {
                var active = string.Equals(server.Name, _settings.ActiveServer, StringComparison.Ordinal);
                list.Items.Add((active ? "● " : "    ") + server.DisplayLine());
            }

            list.EndUpdate();

            if (list.Items.Count == 0)
            {
                return;
            }

            list.SelectedIndex = Math.Clamp(selectIndex, 0, list.Items.Count - 1);
        }

        RemoteServer? Selected() =>
            list.SelectedIndex >= 0 && list.SelectedIndex < _settings.RemoteServers.Count
                ? _settings.RemoteServers[list.SelectedIndex]
                : null;

        const int buttonX = 408;
        const int buttonWidth = 116;

        var addButton = new Button { Text = "Add...", Location = new Point(buttonX, 36), Size = new Size(buttonWidth, 30) };
        var editButton = new Button { Text = "Edit...", Location = new Point(buttonX, 72), Size = new Size(buttonWidth, 30) };
        var removeButton = new Button { Text = "Remove", Location = new Point(buttonX, 108), Size = new Size(buttonWidth, 30) };
        var activeButton = new Button { Text = "Set as active", Location = new Point(buttonX, 158), Size = new Size(buttonWidth, 30) };
        var testButton = new Button { Text = "Test", Location = new Point(buttonX, 194), Size = new Size(buttonWidth, 30) };
        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.OK, Location = new Point(buttonX, 306), Size = new Size(buttonWidth, 30) };

        addButton.Click += (_, _) =>
        {
            var server = new RemoteServer
            {
                Name = SuggestServerName(),
                Protocol = RemoteProtocol.Sftp.ToString(),
                Port = DefaultPortFor(RemoteProtocol.Sftp),
            };
            if (EditRemoteServer(form, server, isNew: true))
            {
                _settings.RemoteServers.Add(server);
                if (_settings.RemoteServers.Count == 1)
                {
                    _settings.ActiveServer = server.Name;
                }

                _settings.Save();
                RefreshIdleTrayIcon();
                Log($"remote server added: {server.Name}");
                Reload(_settings.RemoteServers.Count - 1);
            }
        };

        void EditSelected()
        {
            var server = Selected();
            if (server is null)
            {
                return;
            }

            var previousName = server.Name;
            var index = list.SelectedIndex;
            if (EditRemoteServer(form, server, isNew: false))
            {
                // Keep the active pointer aligned if the active server was renamed.
                if (string.Equals(_settings.ActiveServer, previousName, StringComparison.Ordinal))
                {
                    _settings.ActiveServer = server.Name;
                }

                _settings.Save();
                RefreshIdleTrayIcon();
                Log($"remote server updated: {server.Name}");
                Reload(index);
            }
        }

        editButton.Click += (_, _) => EditSelected();
        list.DoubleClick += (_, _) => EditSelected();

        removeButton.Click += (_, _) =>
        {
            var server = Selected();
            if (server is null)
            {
                return;
            }

            var confirm = MessageBox.Show(
                $"Remove server '{server.Name}'?",
                AppName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            var index = list.SelectedIndex;
            var wasActive = string.Equals(_settings.ActiveServer, server.Name, StringComparison.Ordinal);
            _settings.RemoteServers.Remove(server);
            if (wasActive)
            {
                _settings.ActiveServer = _settings.RemoteServers.Count > 0 ? _settings.RemoteServers[0].Name : string.Empty;
            }

            _settings.Save();
            RefreshIdleTrayIcon();
            Log($"remote server removed: {server.Name}");
            Reload(index);
        };

        activeButton.Click += (_, _) =>
        {
            var server = Selected();
            if (server is null)
            {
                return;
            }

            _settings.ActiveServer = server.Name;
            _settings.Save();
            RefreshIdleTrayIcon();
            Log($"active server -> {server.Name}");
            Reload(list.SelectedIndex);
        };

        testButton.Click += async (_, _) =>
        {
            var server = Selected();
            if (server is null)
            {
                return;
            }

            if (!server.TryGetConfig(out var config, out var error))
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
                testButton.Text = "Test";
                testButton.Enabled = true;
            }
        };

        form.Controls.Add(hint);
        form.Controls.Add(list);
        form.Controls.Add(addButton);
        form.Controls.Add(editButton);
        form.Controls.Add(removeButton);
        form.Controls.Add(activeButton);
        form.Controls.Add(testButton);
        form.Controls.Add(closeButton);
        form.AcceptButton = closeButton;
        form.CancelButton = closeButton;

        Reload(_settings.RemoteServers.FindIndex(s => string.Equals(s.Name, _settings.ActiveServer, StringComparison.Ordinal)));
        form.ShowDialog(owner);
        RefreshIdleTrayIcon();
    }

    private string SuggestServerName()
    {
        for (var i = 1; ; i++)
        {
            var candidate = $"Server {i}";
            if (!_settings.RemoteServers.Any(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    // Edits the given server in place. Returns true only when the user saves valid values.
    private bool EditRemoteServer(IWin32Window owner, RemoteServer server, bool isNew)
    {
        using var sshForm = new Form
        {
            Text = isNew ? "Add Remote Server" : "Edit Remote Server",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(460, 394),
        };

        var nameText = AddLabeledTextBox(sshForm, "Name:", server.Name, 16, false);
        var protocolCombo = AddLabeledComboBox(sshForm, "Protocol:", ProtocolLabels, 58);
        var currentProtocol = server.GetProtocol();
        protocolCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ProtocolOrder, currentProtocol));

        var hostText = AddLabeledTextBox(sshForm, "Host:", server.Host, 100, false);
        var portText = AddLabeledTextBox(sshForm, "Port:", server.Port.ToString(), 142, false);
        var userText = AddLabeledTextBox(sshForm, "User:", server.User, 184, false);
        var passwordText = AddLabeledTextBox(sshForm, "Password:", server.GetPassword(), 226, true);
        var remoteDirectoryText = AddLabeledTextBox(sshForm, "Remote directory:", server.RemoteDirectory, 268, false);

        var passiveCheck = new CheckBox
        {
            Text = "Use passive mode (FTP/FTPS)",
            AutoSize = true,
            Location = new Point(140, 314),
            Checked = server.PassiveMode,
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
            Location = new Point(16, 350),
            Size = new Size(120, 30),
        };

        var saveButton = new Button
        {
            Text = "Save",
            Location = new Point(275, 350),
            Size = new Size(80, 30),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(365, 350),
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

        saveButton.Click += (_, _) =>
        {
            var name = nameText.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Name cannot be empty.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_settings.RemoteServers.Any(s => !ReferenceEquals(s, server) && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"A server named '{name}' already exists.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!TryReadSshDialogValues(SelectedProtocol(), passiveCheck.Checked, hostText, portText, userText, passwordText, remoteDirectoryText, out var config, out var error))
            {
                MessageBox.Show(error, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            server.Name = name;
            server.SetProtocol(config.Protocol);
            server.PassiveMode = config.PassiveMode;
            server.Host = config.Host;
            server.Port = config.Port;
            server.User = config.User;
            server.SetPassword(config.Password);
            server.RemoteDirectory = config.RemoteDirectory;
            sshForm.DialogResult = DialogResult.OK;
        };

        sshForm.Controls.Add(testButton);
        sshForm.Controls.Add(saveButton);
        sshForm.Controls.Add(cancelButton);
        sshForm.AcceptButton = saveButton;
        sshForm.CancelButton = cancelButton;

        return sshForm.ShowDialog(owner) == DialogResult.OK;
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
        AddAboutRow(details, 2, "Build summary", "Clear, configurable remote upload reminders and Settings help.");
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
        UpdateRemoteUploadReminderTimer();
        if (_activeUploadCount == 0)
        {
            _notifyIcon.Icon = GetIdleTrayIcon();
            _notifyIcon.Text = GetIdleTrayText();
        }
    }

    private string GetIdleTrayText()
    {
        if (!_settings.RemoteUploadEnabled)
        {
            return AppName;
        }

        var active = _settings.GetActiveServer();
        var text = active is null
            ? $"{AppName} - Remote upload enabled"
            : $"{AppName} - Upload to {active.Name}";
        return text.Length <= 63 ? text : text[..63];
    }

    private void ToggleRemoteUpload()
    {
        _settings.RemoteUploadEnabled = !_settings.RemoteUploadEnabled;
        _settings.Save();
        _suppressMenuCloseOnce = true;
        RefreshIdleTrayIcon();
        RebuildActiveServerMenu();
        Log($"remote upload {(_settings.RemoteUploadEnabled ? "enabled" : "disabled")}");
    }

    private void UpdateRemoteUploadReminderTimer()
    {
        if (!_settings.RemoteUploadEnabled || !_settings.RemoteUploadReminderEnabled)
        {
            _remoteUploadReminderTimer.Stop();
            return;
        }

        var interval = GetRemoteUploadReminderIntervalMs();
        if (_remoteUploadReminderTimer.Interval != interval)
        {
            _remoteUploadReminderTimer.Stop();
            _remoteUploadReminderTimer.Interval = interval;
        }

        if (!_remoteUploadReminderTimer.Enabled)
        {
            _remoteUploadReminderTimer.Start();
        }
    }

    private int GetRemoteUploadReminderIntervalMs()
    {
        return _settings.RemoteUploadReminderMinutes * 60 * 1000;
    }

    private void ShowRemoteUploadReminder()
    {
        if (!_settings.RemoteUploadEnabled || !_settings.RemoteUploadReminderEnabled)
        {
            _remoteUploadReminderTimer.Stop();
            return;
        }

        _notifyIcon.ShowBalloonTip(
            5000,
            "Remote upload reminder",
            "Remote upload is still enabled. Disable it when you no longer need it.",
            ToolTipIcon.Info);
    }

    private void UpdateRemoteUploadStatusMenuItem()
    {
        if (_remoteUploadStatusMenuItem is not null)
        {
            _remoteUploadStatusMenuItem.Checked = _settings.RemoteUploadEnabled;
        }
    }

    private void RebuildActiveServerMenu()
    {
        if (_activeServerMenuItem is null)
        {
            return;
        }

        // Active server can only be changed while remote upload is enabled.
        _activeServerMenuItem.Enabled = _settings.RemoteUploadEnabled && _settings.RemoteServers.Count > 0;

        _activeServerMenuItem.DropDownItems.Clear();

        if (_settings.RemoteServers.Count == 0)
        {
            _activeServerMenuItem.Text = "Active server: (none)";
            _activeServerMenuItem.DropDownItems.Add(new ToolStripMenuItem("(no servers configured)") { Enabled = false });
            return;
        }

        var active = _settings.GetActiveServer();
        _activeServerMenuItem.Text = $"Active server: {active?.Name ?? "(none)"}";

        foreach (var server in _settings.RemoteServers)
        {
            var captured = server;
            var item = new ToolStripMenuItem(server.Name)
            {
                Checked = ReferenceEquals(server, active),
            };
            item.Click += (_, _) =>
            {
                _settings.ActiveServer = captured.Name;
                _settings.Save();
                RefreshIdleTrayIcon();
                Log($"active server -> {captured.Name}");
            };
            _activeServerMenuItem.DropDownItems.Add(item);
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

        _activeClipboardImage?.Dispose();
        _activeClipboardImage = new Bitmap(image);
        _activeClipboardPath = text;
        _activeClipboardImageSha256 = _lastInjectedImageSha256;
        _activeClipboardSequence = NativeMethods.GetClipboardSequenceNumber();

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

        if (_temporaryPathSequence != 0)
        {
            _clipboardInjectTimer.Start();
            return;
        }

        if (_pendingClipboardImage is null || string.IsNullOrWhiteSpace(_pendingClipboardText))
        {
            return;
        }

        try
        {
            if (_activeClipboardImage is null)
            {
                _activeClipboardImage = new Bitmap(_pendingClipboardImage);
                _activeClipboardPath = _pendingClipboardText;
                _activeClipboardImageSha256 = ComputeSha256Hex(EncodePng(_pendingClipboardImage));
            }

            var activePath = _activeClipboardPath ?? _pendingClipboardText;
            var clipboardSet = _settings.SmartPasteEnabled && _pasteMonitor is not null
                ? TrySetClipboardImage(_activeClipboardImage)
                : TrySetClipboardContent(activePath, _activeClipboardImage);
            if (!clipboardSet)
            {
                Log("error IOException: delayed clipboard injection failed");
                ClearActiveClipboardPayload();
            }
            else
            {
                _activeClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            }
        }
        finally
        {
            _pendingClipboardImage.Dispose();
            _pendingClipboardImage = null;
            _pendingClipboardText = null;
        }
    }

    private void HandlePasteShortcut()
    {
        if (!_settings.SmartPasteEnabled)
        {
            return;
        }

        if (_activeClipboardImage is null || string.IsNullOrWhiteSpace(_activeClipboardPath))
        {
            return;
        }

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (sequence != _activeClipboardSequence && sequence != _temporaryPathSequence && !ClipboardContainsActiveImage())
        {
            ClearActiveClipboardPayload();
            return;
        }

        if (sequence == _temporaryPathSequence)
        {
            return;
        }

        _lastInjectedClipboardText = _activeClipboardPath;
        _lastInjectedUtc = DateTime.UtcNow;
        if (!TrySetClipboardText(_activeClipboardPath))
        {
            return;
        }

        _temporaryPathSequence = NativeMethods.GetClipboardSequenceNumber();
        _clipboardRestoreTimer.Stop();
        _clipboardRestoreTimer.Interval = ClipboardRestoreSafetyTimeoutMs;
        _clipboardRestoreTimer.Start();
    }

    private void HandlePasteShortcutReleased()
    {
        if (_temporaryPathSequence == 0)
        {
            return;
        }

        _clipboardRestoreTimer.Stop();
        _clipboardRestoreTimer.Interval = ClipboardRestoreAfterKeyUpMs;
        _clipboardRestoreTimer.Start();
    }

    private bool ClipboardContainsActiveImage()
    {
        if (string.IsNullOrWhiteSpace(_activeClipboardImageSha256))
        {
            return false;
        }

        using var image = TryGetClipboardImage();
        if (image is null)
        {
            return false;
        }

        var hash = ComputeSha256Hex(EncodePng(image));
        if (!string.Equals(hash, _activeClipboardImageSha256, StringComparison.Ordinal))
        {
            return false;
        }

        _activeClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
        return true;
    }

    private void RestoreImageClipboardAfterTerminalPaste()
    {
        _clipboardRestoreTimer.Stop();
        if (_activeClipboardImage is null || NativeMethods.GetClipboardSequenceNumber() != _temporaryPathSequence)
        {
            return;
        }

        _lastInjectedImageSha256 = ComputeSha256Hex(EncodePng(_activeClipboardImage));
        _lastInjectedUtc = DateTime.UtcNow;
        if (TrySetClipboardImage(_activeClipboardImage))
        {
            _activeClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            _temporaryPathSequence = 0;
        }
    }

    private void ClearActiveClipboardPayload()
    {
        _clipboardRestoreTimer.Stop();
        _activeClipboardImage?.Dispose();
        _activeClipboardImage = null;
        _activeClipboardPath = null;
        _activeClipboardImageSha256 = null;
        _activeClipboardSequence = 0;
        _temporaryPathSequence = 0;
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

    private static bool TrySetClipboardImage(Image image)
    {
        for (var i = 0; i < ClipboardRetryCount; i++)
        {
            try
            {
                var data = new DataObject();
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

    private static bool TrySetClipboardText(string text)
    {
        for (var i = 0; i < ClipboardRetryCount; i++)
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
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
    internal const int DefaultRemoteUploadReminderMinutes = 30;
    internal const int MinRemoteUploadReminderMinutes = 1;
    internal const int MaxRemoteUploadReminderMinutes = 24 * 60;

    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "ClipboardImages");

    public bool ConvertToWslPath { get; set; }

    public bool SmartPasteEnabled { get; set; } = true;

    public bool RemoteUploadEnabled { get; set; }

    public bool RemoteUploadReminderEnabled { get; set; } = true;

    public int RemoteUploadReminderMinutes { get; set; } = DefaultRemoteUploadReminderMinutes;

    // Named remote server profiles. The one whose Name matches ActiveServer is used for uploads.
    public List<RemoteServer> RemoteServers { get; set; } = new();

    // Name of the currently selected server profile.
    public string ActiveServer { get; set; } = string.Empty;

    // --- Legacy single-server fields (nullable, read only for one-time migration into RemoteServers).
    // They are set to null after migration and omitted from the saved file.
    public string? Protocol { get; set; }

    public bool? PassiveMode { get; set; }

    public string? SshHost { get; set; }

    public int? SshPort { get; set; }

    public string? SshUser { get; set; }

    public string? SshPasswordBase64 { get; set; }

    public string? RemoteDirectory { get; set; }

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
            parsed.RemoteServers ??= new List<RemoteServer>();
            parsed.NormalizeRemoteUploadReminder();
            parsed.MigrateLegacyServer();
            parsed.NormalizeServers();
            var passwordsUpgraded = parsed.UpgradePasswordStorage();

            if (parsed.RemoteServers.Count > 0 &&
                !parsed.RemoteServers.Any(s => string.Equals(s.Name, parsed.ActiveServer, StringComparison.Ordinal)))
            {
                parsed.ActiveServer = parsed.RemoteServers[0].Name;
            }

            if (passwordsUpgraded)
            {
                // Re-encrypt legacy plaintext-base64 passwords with DPAPI and drop the old field.
                try
                {
                    parsed.Save();
                }
                catch
                {
                    // Best-effort upgrade; GetPassword still reads the in-memory value this session.
                }
            }

            return parsed;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void NormalizeRemoteUploadReminder()
    {
        if (RemoteUploadReminderMinutes < MinRemoteUploadReminderMinutes ||
            RemoteUploadReminderMinutes > MaxRemoteUploadReminderMinutes)
        {
            RemoteUploadReminderMinutes = DefaultRemoteUploadReminderMinutes;
        }
    }

    // Fold a pre-1.3 single-server configuration into the RemoteServers list.
    private void MigrateLegacyServer()
    {
        if (RemoteServers.Count == 0 && !string.IsNullOrWhiteSpace(SshHost))
        {
            var migrated = new RemoteServer
            {
                Name = SshHost!,
                Protocol = string.IsNullOrWhiteSpace(Protocol) ? "Sftp" : Protocol!,
                PassiveMode = PassiveMode ?? true,
                Host = SshHost!,
                Port = (SshPort is > 0 and <= 65535) ? SshPort.Value : 22,
                User = SshUser ?? string.Empty,
                PasswordBase64 = SshPasswordBase64 ?? string.Empty,
                RemoteDirectory = string.IsNullOrWhiteSpace(RemoteDirectory) ? "." : RemoteDirectory!,
            };
            RemoteServers.Add(migrated);
            if (string.IsNullOrWhiteSpace(ActiveServer))
            {
                ActiveServer = migrated.Name;
            }
        }

        // Drop legacy fields so they no longer serialize.
        Protocol = null;
        PassiveMode = null;
        SshHost = null;
        SshPort = null;
        SshUser = null;
        SshPasswordBase64 = null;
        RemoteDirectory = null;
    }

    private void NormalizeServers()
    {
        foreach (var server in RemoteServers)
        {
            server.Name ??= string.Empty;
            if (server.Port <= 0 || server.Port > 65535)
            {
                server.Port = 22;
            }

            if (string.IsNullOrWhiteSpace(server.RemoteDirectory))
            {
                server.RemoteDirectory = ".";
            }

            if (string.IsNullOrWhiteSpace(server.Protocol))
            {
                server.Protocol = "Sftp";
            }
        }
    }

    // Re-encrypt any legacy plaintext-base64 passwords with DPAPI.
    // Returns true if any server's stored password representation changed.
    private bool UpgradePasswordStorage()
    {
        var changed = false;
        foreach (var server in RemoteServers)
        {
            if (server.UpgradePasswordStorage())
            {
                changed = true;
            }
        }

        return changed;
    }

    internal void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(SettingsFilePath, json);
    }

    internal RemoteServer? GetActiveServer()
    {
        if (RemoteServers.Count == 0)
        {
            return null;
        }

        return RemoteServers.FirstOrDefault(s => string.Equals(s.Name, ActiveServer, StringComparison.Ordinal))
            ?? RemoteServers[0];
    }

    internal bool TryGetRemoteUploadConfig(out RemoteUploadConfig config, out string error)
    {
        config = default;
        error = string.Empty;

        var server = GetActiveServer();
        if (server is null)
        {
            error = "no remote server is configured";
            return false;
        }

        return server.TryGetConfig(out config, out error);
    }
}

internal sealed class RemoteServer
{
    public string Name { get; set; } = string.Empty;

    // One of: "Sftp", "Ftp", "FtpsExplicit", "FtpsImplicit".
    public string Protocol { get; set; } = "Sftp";

    public bool PassiveMode { get; set; } = true;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string User { get; set; } = string.Empty;

    // DPAPI-encrypted password (Windows CurrentUser scope), base64-encoded and tagged with a
    // scheme prefix. Only decryptable by the same Windows user account that saved it.
    public string Password { get; set; } = string.Empty;

    // Legacy (<= 1.3.3): password stored as plain base64 (encoding, not encryption).
    // Read for backward compatibility, then migrated into Password and dropped on save.
    public string? PasswordBase64 { get; set; }

    public string RemoteDirectory { get; set; } = ".";

    private const string DpapiScheme = "DPAPI:";

    internal string GetPassword()
    {
        if (!string.IsNullOrWhiteSpace(Password))
        {
            return DecodeStoredPassword(Password);
        }

        // Legacy plaintext-base64 fallback (pre-1.3.4 settings not yet migrated).
        if (!string.IsNullOrWhiteSpace(PasswordBase64))
        {
            return DecodeLegacyBase64(PasswordBase64);
        }

        return string.Empty;
    }

    internal void SetPassword(string password)
    {
        Password = EncodePassword(password);
        PasswordBase64 = null;
    }

    // Upgrade a legacy plaintext-base64 password to DPAPI encryption and drop the old field.
    // Returns true if the stored representation changed (so the caller can persist it).
    internal bool UpgradePasswordStorage()
    {
        if (string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(PasswordBase64))
        {
            Password = EncodePassword(DecodeLegacyBase64(PasswordBase64));
        }

        if (PasswordBase64 is null)
        {
            return false;
        }

        // Remove the legacy field so plaintext base64 no longer persists to disk.
        PasswordBase64 = null;
        return true;
    }

    private static string EncodePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return DpapiScheme + Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            // DPAPI should always be available on Windows; degrade to base64 if it ever isn't.
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        }
    }

    private static string DecodeStoredPassword(string stored)
    {
        if (stored.StartsWith(DpapiScheme, StringComparison.Ordinal))
        {
            try
            {
                var blob = Convert.FromBase64String(stored[DpapiScheme.Length..]);
                var bytes = ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                // Wrong Windows user, corrupted blob, or a tampered settings file.
                return string.Empty;
            }
        }

        // Untagged value: treat as legacy plaintext base64.
        return DecodeLegacyBase64(stored);
    }

    private static string DecodeLegacyBase64(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch
        {
            return string.Empty;
        }
    }

    internal RemoteProtocol GetProtocol()
    {
        return Enum.TryParse<RemoteProtocol>(Protocol, ignoreCase: true, out var parsed)
            ? parsed
            : RemoteProtocol.Sftp;
    }

    internal void SetProtocol(RemoteProtocol protocol)
    {
        Protocol = protocol.ToString();
    }

    internal string DisplayLine()
    {
        var protocolLabel = GetProtocol() switch
        {
            RemoteProtocol.Sftp => "SFTP",
            RemoteProtocol.Ftp => "FTP",
            RemoteProtocol.FtpsExplicit => "FTPS",
            RemoteProtocol.FtpsImplicit => "FTPS(implicit)",
            _ => "SFTP",
        };
        return $"{Name}  —  {protocolLabel} {User}@{Host}:{Port}";
    }

    internal bool TryGetConfig(out RemoteUploadConfig config, out string error)
    {
        config = default;
        error = string.Empty;

        var password = GetPassword();
        if (string.IsNullOrWhiteSpace(Host))
        {
            error = $"host is not configured for server '{Name}'";
            return false;
        }

        if (Port <= 0 || Port > 65535)
        {
            error = $"port is invalid for server '{Name}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(User))
        {
            error = $"user is not configured for server '{Name}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            error = $"password is not configured for server '{Name}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(RemoteDirectory))
        {
            error = $"remote directory is not configured for server '{Name}'";
            return false;
        }

        config = new RemoteUploadConfig(GetProtocol(), Host, Port, User, password, RemoteDirectory, PassiveMode);
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

internal sealed class GlobalPasteMonitor : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly Action _onPaste;
    private readonly Action _onPasteReleased;
    private IntPtr _hook;
    private bool _pasteKeyDown;

    internal GlobalPasteMonitor(Action onPaste, Action onPasteReleased)
    {
        _onPaste = onPaste;
        _onPasteReleased = onPasteReleased;
        _callback = HookCallback;
        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to install the keyboard paste monitor.");
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var key = Marshal.PtrToStructure<NativeMethods.KbdLlHookStruct>(lParam).VirtualKeyCode;
            if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                if (key == NativeMethods.VK_INSERT)
                {
                    var wasPasteKeyDown = _pasteKeyDown;
                    _pasteKeyDown = false;
                    if (wasPasteKeyDown)
                    {
                        try
                        {
                            _onPasteReleased();
                        }
                        catch
                        {
                            // Never let clipboard failures escape through the native hook callback.
                        }
                    }
                }
            }
            else if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                var isPaste = key == NativeMethods.VK_INSERT && NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
                if (isPaste && !_pasteKeyDown)
                {
                    _pasteKeyDown = true;
                    try
                    {
                        _onPaste();
                    }
                    catch
                    {
                        // Never let clipboard failures escape through the native hook callback.
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
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
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;
    internal const uint VK_SHIFT = 0x10;
    internal const uint VK_INSERT = 0x2D;

    internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct KbdLlHookStruct
    {
        internal uint VirtualKeyCode;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    internal static bool IsKeyDown(uint key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyIcon(IntPtr hIcon);
}
