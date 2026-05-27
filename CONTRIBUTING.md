# Contributing to WinLens

Contributions of all sizes are welcome: bug reports, ideas, docs, and code.

## Ways to help

- Report a bug: open a [bug report](https://github.com/marco-beltrame/WinLens/issues/new?template=bug_report.yml).
- Suggest a feature: open a [feature request](https://github.com/marco-beltrame/WinLens/issues/new?template=feature_request.yml).
- Ask or discuss something: use [Discussions](https://github.com/marco-beltrame/WinLens/discussions).
- Send a pull request (see below).

If you're looking for somewhere to start, check the
[good first issue](https://github.com/marco-beltrame/WinLens/issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22)
label.

## Development setup

Requirements: Windows 10 (build 19041 or later) / 11, the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) with the Windows Desktop workload.

```bash
git clone https://github.com/marco-beltrame/WinLens.git
cd WinLens
dotnet build -c Release
dotnet run                 # run the app
dotnet run -- --settings   # open the control panel
dotnet run -- --translate  # one capture-and-translate
```

### Project layout

| Path | Contents |
| --- | --- |
| `src/Native/` | Screen capture, global hotkey, startup registration |
| `src/Services/` | OCR (`OcrService`), translation (`TranslationService`), settings |
| `src/Models/` | Settings and OCR data types, language list |
| `src/Views/` | Tray host, translation overlay, control panel |
| `src/Theme/` | Dark theme resource dictionary |

## Pull requests

1. Fork and branch off `main`: `git checkout -b feat/my-change`.
2. Keep each PR focused on one thing.
3. Make sure it builds with no warnings: `dotnet build -c Release`.
4. Use [Conventional Commit](https://www.conventionalcommits.org/) messages
   (`feat:`, `fix:`, `docs:`, `refactor:`, `chore:`).
5. Update the README or docs if behaviour changes.
6. Open the PR against `main` and fill in the template.

## Code style

- Match the existing style: file-scoped namespaces, `var` where the type is obvious, XML docs on public members.
- Prefer small, well-named private methods.
- Please discuss before adding a new third-party dependency.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
