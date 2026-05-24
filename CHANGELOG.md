# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and the project follows
[Semantic Versioning](https://semver.org/).

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

[1.0.0]: https://github.com/marco-beltrame/WinLens/releases/tag/v1.0.0
