# TICKSHIFT

**Performance, when every second matters.**

TICKSHIFT is a native Windows desktop app (C# / .NET 10 / WPF) that helps older and budget laptops stay responsive — especially those used for retail trading with MetaTrader, TradingView, broker web terminals, Edge and Chrome. It measures honestly, explains every recommendation, records every change before making it, verifies the result, and can undo it.

It improves what software can improve. It does **not** fix hardware limits, override thermal protection, weaken Windows security, or promise faster broker execution.

> This repository started as an empty project; everything here was built from scratch. The product name is a single constant (`Brand.ProductName` in `src/Tickshift.Core/Brand.cs`).

---

## Features

| Area | What it really does |
|---|---|
| **Dashboard** | Live CPU, memory, disk activity, temperature (or *"Sensor unavailable"* — never estimated), uptime, process count, free storage; performance history for 5 min / 30 min / 1 h / session; contextual greeting and overall condition. |
| **Analyse My Laptop** | A genuine scan: hardware and Windows details, a short live observation, startup entries, browsers, temporary files. Produces explained recommendations (issue · impact · suggested action · risk · reversible · *why*), hardware insights and the main bottleneck. |
| **One-Click Optimise** | Safe / Balanced / Trading presets. Analyse → recovery snapshot → review → apply → verify → record → before/after measurement. Dry-run mode. Optional Windows restore point. |
| **Trading Mode** | Protected-application list (MT4/MT5, TradingView desktop, cTrader, Edge, Chrome, Telegram, custom `.exe`), temporary reversible settings, pressure monitoring with calm alerts, close *suggestions* (never automatic), network check, **End Trading Mode** restores everything — even after a crash. |
| **Browser Booster** | Edge and Chrome analysis (processes, memory, CPU, extensions, cache, background mode via policy/Local State read-only), deep links to the exact settings pages, protected trading-website patterns with step-by-step exclusion guides, before/after browser measurement. |
| **System Cleanup** | Old temp files, old error reports, disposable browser caches (browser must be closed by you), Recycle Bin (explicit acknowledgement). Everything confined to allowlisted folders; junctions/symlinks never followed. |
| **Startup Manager** | Same mechanism as Task Manager (Explorer *StartupApproved*), publisher, path, signature status, running-memory impact, search/filter, Windows vs third-party, restore previous. Machine-wide entries via a validated UAC helper. |
| **Thermal Monitor** | ACPI thermal zones (no driver needed), passive-cooling limit and processor-performance counters, trend, processor-appropriate thresholds, calm diagnostic flow, practical cooling tips. |
| **Performance Monitor** | History charts, sortable/filterable process analyser grouped by app, graceful *ask-to-close* for ordinary apps only, repeatable before/after measurements with noise bands. |
| **Network Diagnostics** | Adapter type (Ethernet/Wi-Fi/VPN), Wi-Fi signal, DNS and router checks, ICMP latency/jitter/loss, TCP-handshake timing to your own endpoints, history. Read-only — never changes network settings. |
| **Automation** | Optional, off by default. Memory/CPU/thermal/storage alerts, daily lightweight diagnostic, login optimisation limited to individually approved low-risk reversible items. Pausable, with history. Never runs during Trading Mode. |
| **Recovery Centre** | Active changes, full history, failed operations, optimisation runs, undo last, restore everything, restore point creation, redacted diagnostic report export. |
| **Settings** | Theme (dark/light/follow Windows), compact mode, reduced motion, UI scale, launch with Windows, start minimised, refresh interval, notifications, default preset, dry-run, data retention, reset. Everything persists. |
| **Onboarding** | Eight-step first-run flow: welcome → purpose and limits → hardware detection → primary use → protected apps → safety → initial diagnostic → dashboard. |

See **[docs/FEATURES-AND-LIMITATIONS.md](docs/FEATURES-AND-LIMITATIONS.md)** for what is intentionally *not* done and what is not yet implemented.

## Safety in one paragraph

TICKSHIFT runs as a standard user. The only elevated code is `Tickshift.ElevatedHelper.exe`, which accepts exactly two strictly validated operations (create a restore point; set a machine-wide StartupApproved value for an entry that already exists) and nothing else. Every optimisation is on a compile-time allowlist, is re-assessed immediately before it runs, is journalled *before* the system is touched, is verified afterwards, and reports "not verified" rather than success if verification fails. Defender, Firewall, UAC, SmartScreen, Windows Update and System Restore are never modified. No telemetry, no network calls other than the connectivity tests you run, no credentials. Details: **[docs/SECURITY.md](docs/SECURITY.md)**.

## Getting started

### Install (end users)

1. Download `TICKSHIFT-Setup-<version>-x64.exe` from the CI artifacts / release page.
2. Run it. Setup asks for administrator approval once so it can install into *Program Files* (this protects the elevated helper from tampering). The app itself then runs without admin rights.
3. If the build is **unsigned**, SmartScreen may warn that the publisher is unknown. Do not disable SmartScreen or Smart App Control to get around this — use a signed release, or build from source yourself.

A portable zip (`TICKSHIFT-<version>-win-x64-portable.zip`) is also produced. It is self-contained (no .NET install needed). Note the helper's tamper protection relies on installing into Program Files.

**Requirements:** Windows 10 1809 (build 17763) or later, or Windows 11; x64. ~150 MB disk.

### Build from source

```powershell
# Windows, .NET 10 SDK, (optional) Inno Setup 6, Python 3 for the XAML checks
./build/publish.ps1 -Version 1.0.0
```

or step by step:

```bash
python3 build/check_xaml.py                   # static resource/binding checks for the WPF layer
dotnet test Tickshift.sln -c Release           # 207 tests; logic tests also run on Linux
dotnet publish src/Tickshift.App/Tickshift.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
```

Full instructions: **[docs/BUILDING.md](docs/BUILDING.md)** · Signing: **[docs/SIGNING.md](docs/SIGNING.md)** · Problems: **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)**.

### Command-line switches

| Switch | Effect |
|---|---|
| `--minimised` | Start in the notification area. |
| `--autostart` | Used by *Launch with Windows*; enables login automation if you approved it. |
| `--dry-run` | Turns on dry-run mode (nothing is changed anywhere). |

## Repository layout

```
src/
  Tickshift.Core          Branding, models, settings, errors, abstractions (no Windows dependencies)
  Tickshift.Security      Process protection, safe-path policy, Authenticode, log redaction, elevated-operation contract, link allowlist
  Tickshift.Persistence   Atomic JSON storage, settings store, write-ahead change journal, bounded history
  Tickshift.Hardware      Windows adapters: WMI, registry, performance counters, powrprof, SystemParametersInfo, WLAN, shell
  Tickshift.Diagnostics   Metrics sampler/history, CPU model parser, hardware interpretation, diagnostic engine,
                          recommendation rules, thermal analysis, benchmarks, network diagnostics
  Tickshift.Optimization  Optimisation contract and engine, concrete optimisations, recovery, startup manager, restore points,
                          automation, report export
  Tickshift.Trading       Protected apps, protected websites, Trading Mode service
  Tickshift.ElevatedHelper  requireAdministrator helper with a closed set of operations
  Tickshift.App           WPF shell, design system, custom controls, pages, view-models, tray icon, DI composition
tests/Tickshift.Tests     xUnit tests with fakes for every system boundary
installer/                Inno Setup script
build/                    Release script, XAML checker, icon generator
docs/                     Architecture, security, building, signing, troubleshooting, limitations
```

Architecture notes and design decisions: **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)**.

## Licence

No licence has been chosen yet; all rights reserved by the repository owner until one is added.
