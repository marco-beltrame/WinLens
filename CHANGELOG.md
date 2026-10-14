# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project follows
[Semantic Versioning](https://semver.org/).

## [1.3.0] - 2026-10-14

### Added
- Region capture: a second shortcut (default <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>R</kbd>)
  lets you drag a rectangle and translate just that area.
- Choice of translation engine. Google stays the default and still needs no setup; you can
  now use your own LibreTranslate server or a local AI model (Ollama, LM Studio, llama.cpp),
  so your text never leaves your PC. "Test connection" checks the settings, and API keys are
  stored encrypted for your Windows account.
- Shortcuts are edited inline in the settings window, with a clear message when a
  combination is reserved by Windows or already taken.

### Changed
- The translate shortcut reads only the monitor your cursor is on, so your other screens stay
  usable while a translation is open.
- A whole screen is translated in one request instead of one request per line: faster, and far
  fewer "too many requests" errors from Google. Translations are the same or slightly better,
  since each line keeps its neighbours as context.
- OCR picks recognizers by the script they actually read (Cyrillic, Greek, Arabic, Hebrew,
  Devanagari, Thai and CJK), instead of only Latin vs CJK.
- The first-run target language follows the Windows display language.
- A tidier settings window: shorter engine names and less explanatory text.

### Fixed
- Overlays, the region selector and the spinner stay on the right screen, at the right size, on
  multi-monitor setups with different scaling.
- Switching the overlay language twice in a row no longer mixes the two languages, and an error
  there no longer closes the app.
- MyMemory quota and error messages are no longer shown as if they were translations.
- A settings file briefly locked by antivirus or OneDrive is no longer reset to defaults, and a
  damaged one is kept as `settings.json.bad`.
- Global shortcuts no longer stay disabled after leaving the shortcut editor half-way.
- "Launch at startup" shows the real state, including when it was disabled in Task Manager.
- "Translate now" in the settings window brings the window back afterwards.
- After a failed translation, the spinner no longer covers the error message.
- Memory used for the screenshot is fully returned after each translation.
- Scrolling the settings with the pointer over a dropdown no longer changes its selection.

## [1.2.0] - 2026-05-25

### Added
- Full set of around 100 translation target languages (was 12). Pick any of them directly;
  the dropdown supports type-to-jump.
- Your most-recently-used target languages are pinned to the top of the picker for quick switching.

### Changed
- The overlay's right-click menu now matches the dark theme, and the text-selection highlight
  is clearer so you can see what you're selecting.

### Removed
- The translation-provider note in the settings footer.

## [1.1.1] - 2026-05-25

### Performance
- Memory is now released after each translation. The overlay frees the full-screen screenshot
  and per-line boxes on close, the reclaim runs once the window is torn down, and the working
  set is trimmed so the process returns to a few MB at idle instead of lingering around 200 MB.

## [1.1.0] - 2026-05-25

### Added
- "Translating…" spinner while a capture is being processed, so it's clear work is happening.
- The app logo is now used as the brand mark in the control panel and the translation overlay.

### Changed
- OCR recognizers now run in parallel instead of one after another, so a multi-language screen
  translates noticeably faster.

### Performance
- Idle memory dropped from about 350 MB to about 94 MB, and the per-translation spike is lower.
  The screenshot is now copied once on its way to the OCR engine instead of several times, and
  the large buffers from each cycle are reclaimed afterwards.

## [1.0.0] - 2026-05-25

First public release.

### Added
- In-place screen translation: press a global hotkey and every on-screen line is translated
  over the original, with a matched background and an auto-fit font.
- System-tray app with a dark control panel (target language, hotkey, source language,
  launch at startup, translate now).
- Translation overlay with a live target-language picker, and copy original / translation.
- Windows OCR pipeline with a multi-script auto mode: it runs every installed recognizer and
  keeps the blocks each one reads well (Latin and CJK on the same screen), then drops overlaps.
- 2x upscaling before OCR for better accuracy on small UI text.
- "Add OCR languages in Windows" link; installed packs are detected live.
- Per-monitor-v2 DPI awareness and multi-monitor virtual-screen capture.
- `--settings` and `--translate` command-line flags.

[1.3.0]: https://github.com/marco-beltrame/WinLens/releases/tag/v1.3.0
[1.2.0]: https://github.com/marco-beltrame/WinLens/releases/tag/v1.2.0
[1.1.1]: https://github.com/marco-beltrame/WinLens/releases/tag/v1.1.1
[1.1.0]: https://github.com/marco-beltrame/WinLens/releases/tag/v1.1.0
[1.0.0]: https://github.com/marco-beltrame/WinLens/releases/tag/v1.0.0
