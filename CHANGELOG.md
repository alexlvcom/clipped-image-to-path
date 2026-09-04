# Changelog

## 1.6.0 - 2026-09-04

- Add settings to turn remote upload notifications on or off and choose their interval in minutes.
- Keep notifications enabled at a 30-minute interval by default.
- Disable notification controls while remote upload is turned off.
- Describe every Settings option with a tooltip when the pointer hovers over it.
- Explain that remote upload notifications are reminders to disable uploading when it is no longer needed.
- Describe remote uploads without implying that source images must already be PNG files.

## 1.5.0 - 2026-09-04

- Show a brief Windows notification every 30 minutes while remote upload is enabled.

## 1.4.0 - 2026-08-10

- Keep copied screenshots as real image clipboard content for normal `Ctrl+V` pastes in Teams, Gmail, and other GUI applications.
- Paste the saved, quoted PNG path with `Shift+Insert`, including in terminals hosted inside editors or other applications.
- Make the path available as soon as the screenshot is saved and restore the image only after the paste shortcut is released.
- Preserve newer clipboard content and recover safely if a key-release event is missed.
