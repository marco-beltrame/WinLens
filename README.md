<div align="center">

<img src="assets/logo.png" alt="WinLens logo" width="120" />

# WinLens

**Translate any text on your Windows screen, in place.**
A desktop take on Google Lens translate.

English · [简体中文](README.zh-CN.md)

[Explore the docs »](#usage)

[Report Bug](https://github.com/marco-beltrame/WinLens/issues/new?template=bug_report.yml) ·
[Request Feature](https://github.com/marco-beltrame/WinLens/issues/new?template=feature_request.yml) ·
[Discussions](https://github.com/marco-beltrame/WinLens/discussions)

[![Build](https://github.com/marco-beltrame/WinLens/actions/workflows/build.yml/badge.svg)](https://github.com/marco-beltrame/WinLens/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/github/license/marco-beltrame/WinLens?color=8b5cf6)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6?logo=windows&logoColor=white)](#installation)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Release](https://img.shields.io/github/v/release/marco-beltrame/WinLens?color=8b5cf6)](https://github.com/marco-beltrame/WinLens/releases)
[![Downloads](https://img.shields.io/github/downloads/marco-beltrame/WinLens/total?color=8b5cf6)](https://github.com/marco-beltrame/WinLens/releases)
[![Stars](https://img.shields.io/github/stars/marco-beltrame/WinLens?style=flat&color=8b5cf6)](https://github.com/marco-beltrame/WinLens/stargazers)

</div>

<div align="center">
<img src="assets/demo.gif" alt="WinLens translating on-screen text to English in place" width="860"/>
</div>

## Overview

WinLens translates the text on your screen and draws the translation right over the original,
matching the background and font so it reads as if the app had always been in your language.
It's the Google Lens *translate* idea, but for the Windows desktop.

Browser translators only touch web pages. WinLens reads whatever is rendered on screen, so it
also works on native apps, games, chat clients, PDFs and foreign software. It uses OCR, so the
text doesn't need to be selectable.

It sits in the system tray and is triggered by a global hotkey. Press the hotkey, read the
translation, press <kbd>Esc</kbd> to dismiss it.

## Why WinLens?

Browser extensions translate text *inside a web page*. They can't read anything else on your
screen. WinLens runs OCR on the actual pixels, so it translates things they can't:

- **Text inside images:** photos, screenshots, memes, infographics, scanned documents.
- **Games:** menus and dialogue, including imported titles.
- **Desktop apps:** installers, error dialogs, foreign software UIs.
- **PDFs and documents** in any reader.
- **Video frames and subtitles.**
- **Anything you can't select or copy.**

If a browser tab already translates it, you don't need WinLens. For everything else on screen, you do.

## Features

- Translates on-screen text in place: the translation replaces the original, with a matching background and font, instead of appearing in a separate box.
- Works on any window (apps, games, chats, documents), not just the browser.
- Reads Latin and CJK (Chinese, Japanese, Korean) on the same screen.
- Global hotkey, default <kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>T</kbd>.
- Upscales the screenshot before OCR and picks the best recognizer per text block, which helps accuracy on small text.
- Dark control panel. You can change the target language straight from the overlay.
- Right-click a block to copy the original text or the translation.
- Optional "launch at startup". Otherwise it stays out of the way in the tray.

## Control panel

A small tray app with a dark control panel. Pick the target language, set the hotkey,
choose the OCR source language, and toggle launch-at-startup.

<div align="center">
<img src="assets/settings.png" alt="WinLens control panel" width="330"/>
</div>

## Installation

### Download

1. Open the [Releases](https://github.com/marco-beltrame/WinLens/releases) page.
2. Download the latest `WinLens.exe`.
3. Run it. WinLens needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), which most Windows 10/11 machines already have.

The executable isn't code-signed yet, so on first launch Windows SmartScreen may show
"Windows protected your PC". Click **More info > Run anyway**. If you'd rather verify the
download instead, compare its checksum against the SHA256 listed on the release page:

```powershell
Get-FileHash .\WinLens.exe -Algorithm SHA256
```

### OCR language packs

WinLens uses the Windows OCR engine, so it can only read languages whose OCR pack is installed.
Most systems already have your display language. To add more (for example Chinese or Japanese):

> Settings > Time & Language > Language & region > (your language) > Language options > Optional features > add the OCR feature.

The "Add OCR languages in Windows" link in the control panel opens that page for you. Newly
installed languages show up automatically.

## Usage

1. Launch WinLens. The control panel opens the first time, then it minimizes to the tray.
2. Pick a target language in the control panel.
3. Press the hotkey (<kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>T</kbd>) anywhere in Windows.
4. The screen is translated in place. Press <kbd>Esc</kbd> or Close to dismiss.

| Action | How |
| --- | --- |
| Translate now | Hotkey, tray menu, or the control panel button |
| Open the control panel | Double-click the tray icon, or tray menu > Open WinLens |
| Change target language | Control panel, or the overlay's top bar (re-translates live) |
| Change the hotkey | Control panel > Change, then press your combination |
| Pick the source (OCR) language | Control panel > Detect text in (leave on Auto unless you need speed) |
| Copy text | Right-click a translated block > Copy original / Copy translation |
| Launch at startup | Control panel toggle |
| Quit | Tray menu > Exit |

Leaving "Detect text in" on Auto is usually best: WinLens runs every installed recognizer and
keeps the text each one reads well, so a mixed-language screen still works. Forcing a single
language only saves a little time.

## How it works

```
Hotkey > capture screen > upscale > OCR (per script) > translate > overlay in place
```

1. Capture the whole virtual screen (DPI-accurate, all monitors).
2. Upscale the image about 2x so small UI text is recognized more reliably.
3. Run every installed OCR recognizer and keep from each only the blocks whose script matches
   it (Latin from the Latin engine, CJK from the CJK engine), then drop overlapping duplicates.
4. Translate each line (Google endpoint, with a MyMemory fallback), cached per session.
5. Draw an opaque, color- and font-matched box over each original line.

## Roadmap

The [issues](https://github.com/marco-beltrame/WinLens/issues) and
[project board](https://github.com/marco-beltrame/WinLens/projects) have the full list.

- [ ] Offline translation (Argos / NLLB): no network, no rate limits, more privacy.
- [ ] PaddleOCR / ONNX engine: stronger CJK and small-text accuracy, no OS language packs.
- [ ] Cross-platform (Avalonia): macOS and Linux.
- [ ] Region capture: translate a selected area instead of the whole screen.
- [x] Animated demo (GIF) in the README.

## Building from source

```bash
git clone https://github.com/marco-beltrame/WinLens.git
cd WinLens
dotnet build -c Release
dotnet run
```

Requirements: Windows 10 (build 19041 or later) / 11, the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and the Windows Desktop
workload (WPF).

| Flag | Effect |
| --- | --- |
| `--settings` | Open the control panel on launch |
| `--translate` | Run one capture-and-translate on launch |

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the
[Code of Conduct](CODE_OF_CONDUCT.md). Issues tagged
[good first issue](https://github.com/marco-beltrame/WinLens/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22)
are a good place to start.

## License

MIT. See [LICENSE](LICENSE).

## Acknowledgements

- Translation through the Google Translate endpoint, with a [MyMemory](https://mymemory.translated.net/) fallback.
- Tray integration with [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon).
- Built with .NET 10 and WPF.
