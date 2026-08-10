# Changelog

## 1.4.0 - 2026-08-10

- Keep copied screenshots as real image clipboard content for normal `Ctrl+V` pastes in Teams, Gmail, and other GUI applications.
- Paste the saved, quoted PNG path with `Shift+Insert`, including in terminals hosted inside editors or other applications.
- Make the path available as soon as the screenshot is saved and restore the image only after the paste shortcut is released.
- Preserve newer clipboard content and recover safely if a key-release event is missed.
