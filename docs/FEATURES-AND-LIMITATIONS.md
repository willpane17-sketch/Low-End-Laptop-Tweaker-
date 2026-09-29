# Feature status and limitations

This page is deliberately candid. "Implemented" means real code that performs the operation; nothing here is a mock-up.

## Implemented

* Live monitoring (CPU, memory, disk activity, ACPI temperature where available, uptime, processes, storage) with bounded session history and charts.
* Diagnostic scan, hardware interpretation (model- and generation-specific), bottleneck assessment, 17 recommendation rules.
* One-Click Optimise with three presets, dry run, restore point, verification, history, before/after benchmark.
* Six allowlisted optimisations (see ARCHITECTURE.md), startup-entry enable/disable (per-user and machine-wide via the helper), Recycle Bin emptying with acknowledgement.
* Recovery: write-ahead journal, crash recovery of interrupted changes, conflict-aware rollback, undo last, restore everything, failed-operation list, run history, redacted report export.
* Trading Mode: protected apps, temporary reversible settings, hysteresis pressure alerts, close suggestions (manual, graceful), network check, crash-safe restore.
* Browser Booster: read-only analysis, deep links, protected website patterns and guides, before/after measurement.
* Thermal Monitor, Performance Monitor with process analyser, Network Diagnostics, Automation, Settings, onboarding, tray icon, single instance, themes, compact mode, UI scaling, keyboard navigation.
* Installer (Inno Setup), release script with optional Authenticode signing, CI.

## Intentionally not done (by design)

| Not done | Reason |
|---|---|
| Automatic browser preference edits (Memory Saver, Sleeping tabs, exclusions, background mode) | No supported API for third-party apps; preference files are integrity-protected. TICKSHIFT gives exact guidance and deep links instead. |
| CPU temperature via kernel driver | Security risk (vulnerable drivers) and licensing concerns. Extension point: `IThermalSensorProvider`. |
| Fan control, power-limit or undervolting changes | Can bypass firmware protection; out of scope for safety. |
| Process priority / EcoQoS manipulation of other apps | Unjustified priority changes can starve other work; Realtime priority is dangerous. |
| Service disabling, registry tweak packs, RAM cleaners, network/TCP tweaks, DNS changes | Unverified benefits, stability or security risk. |
| Automatic app termination | Could lose unsaved work or interrupt trading. |
| Network reset/repair | Disconnects the user; only a guarded link to Windows settings is offered, and never during Trading Mode. |

## Known limitations / not yet implemented

* **Fan speed** is not read: most laptops do not expose it to Windows without vendor drivers. The UI says so.
* **Battery health** relies on the `root\wmi` battery classes; some firmware does not populate them, in which case nothing is shown.
* **Chrome/Edge efficiency feature state** is shown only when set by enterprise policy; the user's own toggle lives in a protected preferences file and is not read.
* **Signature status** covers embedded Authenticode signatures; catalog-signed Windows binaries show "No embedded signature".
* **Startup impact** is shown as the memory the program is using right now (if running); Windows' own "startup impact" rating is not exposed through a public API.
* **Toast notifications** use the notification-area balloon API (shown as standard Windows notifications); there are no actionable toast buttons.
* **Scheduled tasks and services** that start programs are not listed in Startup Manager (read-only listing is a candidate for a future version; changing them is intentionally out of scope).
* **Localisation**: English (UK) only.
* **32-bit installer**: a portable x86 build can be produced; the installer is x64 only.
* **ARM64**: not built or tested.
* The WPF UI is verified by static checks (`build/check_xaml.py`) and a manual checklist (BUILDING.md); there are no automated UI tests.
