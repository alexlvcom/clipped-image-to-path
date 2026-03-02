using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

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
    private static readonly int KeepLatestN = 500;
    private static readonly bool WriteLog = true;

    // State
    private DateTime _lastHandledUtc = DateTime.MinValue;
    private string? _lastImageSha256;
    private bool _isHandling;
    private int _deferredAttemptsRemaining;

    private readonly AppSettings _settings;
    private readonly ClipboardListenerWindow _listenerWindow;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _trayIcon;
    private readonly System.Windows.Forms.Timer _deferredProcessTimer;
    private readonly System.Windows.Forms.Timer _clipboardInjectTimer;
    private string? _pendingClipboardText;
    private Bitmap? _pendingClipboardImage;

    internal ClipboardBridgeContext()
    {
        _settings = AppSettings.Load();
        EnsureOutputDirectoryExists();

        _trayIcon = TrayIconFactory.Create();
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = AppName,
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
            _pendingClipboardImage?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _trayIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
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
            ClientSize = new Size(620, 190),
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

        var saveButton = new Button
        {
            Text = "Save",
            DialogResult = DialogResult.OK,
            Location = new Point(440, 140),
            Size = new Size(80, 30),
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(525, 140),
            Size = new Size(80, 30),
        };

        settingsForm.Controls.Add(folderLabel);
        settingsForm.Controls.Add(folderText);
        settingsForm.Controls.Add(browseButton);
        settingsForm.Controls.Add(wslCheck);
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
            _settings.Save();
            Log("settings updated");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save settings: {ex.Message}", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowAboutDialog()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        using var about = new Form
        {
            Text = $"About {AppName}",
            StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(480, 190),
        };

        var body =
            $"{AppName}\r\n" +
            $"Version: {version}\r\n" +
            $"Output folder: {_settings.OutputDirectory}\r\n" +
            $"Build date: {File.GetLastWriteTime(Application.ExecutablePath):yyyy-MM-dd HH:mm:ss}\r\n" +
            $"Copyright (c) Alex LV";

        var label = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            Text = body,
        };

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            Size = new Size(80, 28),
            Location = new Point(about.ClientSize.Width - 96, about.ClientSize.Height - 44),
        };

        about.Controls.Add(label);
        about.Controls.Add(close);
        about.AcceptButton = close;

        _ = about.ShowDialog();
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
            if (_lastImageSha256 == imageHash)
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
            if (_lastImageSha256 == imageHash)
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

    private void ScheduleClipboardInject(string text, Image image)
    {
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
    internal static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(0, 120, 80));
            FillRoundedRectangle(g, bgBrush, 2, 2, 28, 28, 7);

            using var pen = new Pen(Color.White, 2f);
            g.DrawRectangle(pen, 8, 7, 12, 10);
            g.DrawLine(pen, 12, 20, 24, 20);
            g.DrawLine(pen, 24, 20, 24, 12);
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
