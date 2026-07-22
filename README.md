# ClippedImageToPath

ClippedImageToPath is a lightweight Windows tray app that converts copied clipboard images into PNG files and replaces the clipboard with the saved file path as text.

This solves the common issue where Windows Terminal cannot paste bitmap clipboard formats directly into CLI tools.

## What It Does

- Watches clipboard updates in the background
- Detects image content (screenshots, snips, browser images, etc.)
- Saves image as PNG with timestamp name:
  - `clipboard_yyyy-MM-dd_HH-mm-ss_fff.png`
- Replaces clipboard with quoted path text
  - Windows mode: `"C:\\...\\clipboard_....png"`
  - Optional WSL mode: `"/mnt/c/.../clipboard_....png"`
- Optional upload to a remote server after the PNG is saved, over SFTP (SSH), FTP, or FTPS (explicit/implicit TLS)
- Leaves normal text clipboard entries unchanged
- Includes loop-prevention, debounce, retry logic, and dedupe hashing

## Project Layout

- `Program.cs` - app logic, tray UI, settings, clipboard listener
- `ClippedImageToPath.csproj` - project metadata and version

## Build

```powershell
dotnet build -c Release
```

## Publish (single-file)

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Published executable:

- `bin\\Release\\net8.0-windows\\win-x64\\publish\\ClippedImageToPath.exe`

## Run

Launch the EXE. The app runs in the system tray.

Tray menu:

- `Enable Remote Upload` - checkable item; click to turn remote upload on or off (the menu stays open so you can see the checkbox change)
- `Active server` - pick which server profile uploads use (disabled while remote upload is off)
- `Open output folder`
- `Settings`
- `About`
- `Exit`

## Settings

`Settings` dialog allows:

- Output folder for saved PNG files
- Toggle `Convert clipboard path to WSL format (/mnt/c/...)`
- Toggle `Remote upload enabled`
- Manage multiple named remote server profiles (`Remote servers...`): add, edit, remove, and pick which one is active
- Each profile stores protocol (SFTP / FTP / FTPS explicit / FTPS implicit), host, port, user, password, remote directory, and passive mode
- Per-profile test connection button that verifies login and remote directory access
- Switch the active server quickly from the tray menu (`Active server` submenu); uploads use the active profile

Settings are stored at:

- `%APPDATA%\\ClippedImageToPath\\settings.json`

## About Dialog

Shows:

- App name
- Version
- Output folder
- Build date
- Copyright

## Logging

A runtime log is written to:

- `<OutputFolder>\\bridge.log`

## Typical Workflow

1. Copy image (Snipping Tool, browser, screenshot).
2. App saves PNG file.
3. Clipboard becomes quoted file path text.
4. Paste into Windows Terminal / CLI tool.

## Notes

- Works standalone; no clipboard manager is required.
- Clipboard managers (for example, Ditto) are optional and can be used alongside this app.
- If output path has spaces, quoted clipboard text prevents CLI parsing issues.
- In clipboard managers, you may see two entries for one screenshot:
  - the original image clip from your screenshot tool
  - the path-injection clip written by ClippedImageToPath
- This is expected because the app performs a second clipboard write so Terminal can paste the file path.
- If you want to hide the app-generated entry, add `ClippedImageToPath.exe` to your clipboard manager's ignore-app list.

## Troubleshooting

- If nothing happens:
  - Ensure app is running in tray.
  - Check `<OutputFolder>\\bridge.log`.
- If clipboard is busy:
  - App retries clipboard access automatically.
- If path format is unexpected:
  - Check `Settings` for WSL conversion toggle.
