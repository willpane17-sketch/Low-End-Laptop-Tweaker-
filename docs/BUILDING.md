# Building, testing and releasing

## Prerequisites

| Tool | Needed for |
|---|---|
| .NET SDK 10.0.1xx | Everything (`global.json` rolls forward to the latest 10.0 feature band) |
| Windows 10/11 | Running the app; building the installer |
| Python 3 | `build/check_xaml.py` static checks |
| Inno Setup 6.3+ | Installer (optional; a portable zip is always produced) |
| Windows SDK (signtool) | Authenticode signing (optional) |

The solution also builds on Linux/macOS (`EnableWindowsTargeting=true`), which is how CI runs the logic tests quickly. The WPF app can only *run* on Windows.

## Commands

```bash
dotnet build Tickshift.sln -c Release
python3 build/check_xaml.py
dotnet test Tickshift.sln -c Release
dotnet publish src/Tickshift.App/Tickshift.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
```

Or the whole pipeline on Windows:

```powershell
./build/publish.ps1 -Version 1.2.0 [-CertThumbprint <sha1>] [-SkipTests]
```

Output in `artifacts/installer/`:

* `TICKSHIFT-Setup-<version>-x64.exe` — installer (if Inno Setup is installed)
* `TICKSHIFT-<version>-win-x64-portable.zip` — self-contained portable build

The publish output contains `Tickshift.exe` (GUI, asInvoker) and `Tickshift.ElevatedHelper.exe` (requireAdministrator), both self-contained and ReadyToRun-compiled. The version is set with `-p:Version=…` and appears in file properties, the About section and diagnostic reports.

32-bit Windows: `-Runtime win-x86` produces a portable build; the installer script targets x64.

## Continuous integration

`.github/workflows/build.yml`:

* **Logic tests (Linux)** — XAML checks + test suite.
* **Windows** — XAML checks, tests, self-contained publish, Inno Setup installer, uploads the installer and portable zip as the `tickshift-windows-x64` artifact.

## What the tests cover

`tests/Tickshift.Tests` (xUnit, 207 tests). Every system boundary is faked (`Fakes/Fakes.cs`); the only real file-system operations happen in a per-test sandbox directory under the OS temp folder.

| Area | Examples |
|---|---|
| Hardware interpretation | 20+ CPU brand strings (1st–12th gen i3, N-series, Celeron/Pentium, Ryzen, Core Ultra), memory tiers, HDD/unknown media, bottleneck scoring |
| Recommendation rules | documented startup example, critical storage ranking, trading deferral, no thermal rules without a sensor, faulty-rule isolation |
| Optimisation applicability | presets, uncertain items not pre-selected in Safe, explicit-approval items, power plan on battery/Modern Standby/custom plan |
| Engine | allowlist, verification failure never reported as success, dry run writes nothing, classified failures, re-assessment, cancellation |
| Recovery & rollback | LIFO undo, conflict → superseded, irreversible changes, interrupted-change recovery, journal persistence, pruning |
| Protected apps | name/path matching, disabled entries, classification, never-closeable categories |
| Trading Mode | activation/end restore, dry run, never closes apps, one notification per episode, double activation, crash recovery |
| Configuration persistence | round trip, corrupt file → backup, corrupt without backup → defaults, clamping, reset |
| Error handling | classifier mapping, friendly messages, failure results |
| Security | safe-path policy incl. symlink escape, elevated-argument validator rejects injection, log redaction |
| Unsupported conditions | missing temperature sensor, failing providers, old Windows build, no-reply network hosts |

### Manual verification checklist (Windows)

Automated tests cannot exercise real Windows APIs safely, so run these on a test machine (ideally a VM snapshot) before each release:

- [ ] Windows 10 22H2 and Windows 11 23H2/24H2, standard (non-admin) account
- [ ] 4 GB RAM VM; HDD-backed VM (media type detection)
- [ ] Laptop with and without ACPI thermal zones (dashboard shows *Sensor unavailable* correctly)
- [ ] No network connection (Network page, Trading Mode test)
- [ ] MetaTrader / browser running during Trading Mode; End Trading Mode restores power plan and animations
- [ ] Kill `Tickshift.exe` from Task Manager during Trading Mode → relaunch restores settings
- [ ] Cancel One-Click Optimise mid-run → Recovery Centre shows completed items as undoable
- [ ] Disable/enable a per-user and a machine-wide startup entry (UAC prompt), then *Restore previous*
- [ ] Create restore point twice within 24 h → second reports the frequency limit
- [ ] 1366×768 at 100 % and 125 % scaling; UI scale 150 %; compact mode; light theme; Follow Windows while switching Windows theme
- [ ] Memory: idle working set after 10 minutes on the dashboard (target < 150 MB); CPU < 1 % with the window hidden
- [ ] Installer: install, launch, uninstall (Run value removed; data-folder prompt)
