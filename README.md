# ClippedImageToPath

ClippedImageToPath is a lightweight Windows tray app that converts copied clipboard images into PNG files and replaces the clipboard with the saved file path as text.

This solves the common issue where Windows Terminal cannot paste bitmap clipboard formats directly into CLI tools.

## Main use case: give coding agents your screenshots

Coding agents (Claude Code, Codex, etc.) can't read an image sitting on your clipboard — they need a **file path or URL**. This app bridges that gap: take a screenshot, and your clipboard instantly becomes a path you can paste straight into the agent's prompt.

The point is that it works **no matter where the agent runs**:

- **Locally on Windows** — paste the Windows path (`"C:\...\clipboard_....png"`).
- **In WSL** — turn on WSL mode and paste the `/mnt/c/...` path; the agent reads the same file.
- **On another machine** — your home PC, a remote dev server, a container — turn on **remote upload** (SFTP/FTP/FTPS). The screenshot is uploaded to that server automatically, so the agent there can open it by its remote path or URL.

So whatever agent you're coding with, and wherever it lives, you can just hit "screenshot → paste → send" and it can see exactly what you see.

## Screenshots

Lives quietly in the system tray — right-click for the menu:

![Tray menu](ClippedImageToPath-Tray.jpg)

The Settings dialog: output folder, WSL path toggle, and remote upload with named server profiles:

![Settings dialog](ClippedImageToPath-Config.jpg)

## Download

Grab the latest **ClippedImageToPath.exe** from the [Releases page](https://github.com/alexlvcom/clipped-image-to-path/releases/latest) — or directly:

**https://github.com/alexlvcom/clipped-image-to-path/releases/latest/download/ClippedImageToPath.exe**

It's a single, self-contained executable — **no .NET runtime install needed**. Just download and double-click; it starts in the system tray.

### First run: Windows SmartScreen

The exe is **unsigned** (no paid code-signing certificate for a free hobby tool), so Windows may show **"Windows protected your PC."** This is *not* a virus warning — it only means the file is new and hasn't built up download reputation yet. Click **More info → Run anyway**. Prefer to be sure? The source is right here — [build it yourself](#build).

### Verify your download (optional)

Each release lists the SHA-256 of the exe. To check the file you downloaded matches:

```powershell
Get-FileHash .\ClippedImageToPath.exe -Algorithm SHA256
```

Compare the output against the hash on that version's [release page](https://github.com/alexlvcom/clipped-image-to-path/releases/latest).

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

Server passwords in that file are **encrypted with Windows DPAPI (per-user scope)** — they can only be decrypted by the same Windows user account on the same machine, not read as plain text. (Configs from older versions that stored passwords as plain base64 are upgraded automatically on first launch.)

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
