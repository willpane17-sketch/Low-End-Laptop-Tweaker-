# Architecture

## Goals that shaped the design

1. **Do not become the problem.** The app targets 4 GB dual-core laptops, so it must stay small and quiet: one sampler thread, no looping animations, no GPU effects, lazy pages.
2. **Every change is honest.** Analyse → propose → journal → apply → verify → record. Nothing is reported as successful unless verification confirms it.
3. **Testable without touching the machine.** All system access sits behind interfaces; the logic modules target plain `net10.0` and their tests run on Linux CI.
4. **Least privilege.** The UI never runs elevated; a tiny helper performs a closed set of administrator operations.

## Module graph

```
Tickshift.App (WPF, net10.0-windows)
 ├─ Tickshift.Trading ──────────┐
 ├─ Tickshift.Optimization ─────┼─ Tickshift.Diagnostics ─┐
 ├─ Tickshift.Hardware ─────────┤                         ├─ Tickshift.Security ─ Tickshift.Core
 ├─ Tickshift.Persistence ──────┘                         │
 └─ Tickshift.ElevatedHelper (exe, requireAdministrator) ─┘
```

* **Core** has no Windows dependencies: models, settings (with `Normalise()` clamping), error classification, `OperationResult`, and the interfaces every other module programs against (`ISystemInfoProvider`, `IThermalSensorProvider`, `IProcessProvider`, `IStartupProvider`, `IPowerPlanManager`, `IVisualEffectsManager`, `ILinkLauncher`, `ITradingSessionState`, …).
* **Hardware** implements those interfaces with documented Windows APIs. Classes are annotated `[SupportedOSPlatform("windows")]`.
* **Diagnostics / Optimization / Trading** contain the product logic and depend only on interfaces.
* **App** is the composition root (`Composition/ServiceRegistration.cs`, `ValidateOnBuild = true` so a missing or circular registration fails at start-up instead of at first use).

## Runtime data flow

```
MetricsMonitor (1 PeriodicTimer) ──samples──► PerformanceHistory (ring buffer, ~14k samples)
      │                                            │
      ├─► Dashboard / Performance / Thermal pages  │ (subscribe only while visible, redraw ≤ every 2 s)
      ├─► TradingModeService (pressure evaluator with hysteresis)
      └─► AutomationService (reads history once a minute)
```

* The sampler uses `GetSystemTimes` deltas for CPU (one syscall), `GlobalMemoryStatusEx`, and the PhysicalDisk `% Idle Time` counter. Temperature is read every 10 s. When the window is hidden or minimised the interval relaxes to ≥ 10 s.
* Diagnostics and benchmarks call `CollectAsync(window)`, which temporarily raises the sampler to 1 s resolution instead of creating a second sampler — two consumers of a delta-based CPU counter would corrupt each other's readings.
* The process list (the most expensive query) runs only while the Performance page is visible (3 s), during Trading Mode (monitor interval, default 10 s), and during scans. It uses one `EnumWindows` pass and `PROCESS_QUERY_LIMITED_INFORMATION` handles so it works as a standard user.

## Optimisation pipeline

`IOptimisation` = identity + description + preconditions + risk + privilege + reversibility + presets + uncertainty note, and four operations: `AssessAsync`, `ApplyAsync`, `VerifyAsync`, `RollbackAsync` (+ `InspectInterruptedAsync` for crash recovery).

`OptimisationEngine`:

1. **Analyse / identify** — assesses every allowlisted optimisation offered in the preset.
2. **Snapshot** — optional before-benchmark and Windows restore point. The authoritative snapshot is the change journal (below).
3. **Review** — the UI shows the plan; nothing has changed yet.
4. **Apply** — each approved item is re-assessed first (the system may have changed), then applied through an `IChangeRecorder` that writes a **Pending** journal record *before* the change.
5. **Verify** — the owning optimisation re-reads the real state. Failure → `AppliedNotVerified` / `VerificationFailed`, never success.
6. **Record** — run history; `LastOptimisedAt` updates only if something was verified.
7. **Measure** — after-benchmark and `BenchmarkService.Compare` with per-metric noise bands and caveats.

Dry-run uses a no-op recorder and never calls `ApplyAsync`.

### Allowlist

`OptimisationEngine.Allowlist` is a compile-time set. Anything registered outside it is dropped at construction. Current operations:

| ID | Change | Reversible | Default in |
|---|---|---|---|
| `cleanup.user-temp` | Delete %TEMP% files older than 48 h | No (disposable) | Safe, Balanced |
| `cleanup.error-reports` | Delete user WER reports older than 7 days | No (disposable) | Safe, Balanced |
| `cleanup.browser-cache` | Delete Chromium Cache_Data / Code Cache / GPUCache (browser closed) | No (disposable) | Never pre-selected; explicit approval |
| `power.balanced-on-mains` | Power saver → Balanced while plugged in | Yes | All presets |
| `power.high-performance-on-mains` | Balanced/Power saver → High performance while plugged in | Yes | Optional (uncertain benefit) |
| `ui.reduce-animations` | Six documented `SystemParametersInfo` animation flags off | Yes | Balanced, Trading |

Startup-entry changes are made by `StartupManagerService`, which journals and verifies in the same way.

## Recovery model

* `ChangeJournal` is a write-ahead log persisted with atomic writes (temp file + write-through + replace, `.bak` kept, corrupt files preserved).
* At start-up `RecoveryService.RecoverInterruptedAsync` finds records still **Pending** (crash/power loss) and asks the owner to inspect the real state: *Applied*, *NotApplied* (marked failed, nothing changed) or *Indeterminate*.
* Rollback is newest-first. Each owner checks for **conflicts**: if the setting no longer has the value TICKSHIFT set, it is left alone and marked *Superseded* rather than overwritten.
* Pruning never removes records needed to undo an active change.
* Trading Mode writes its session file before applying anything; if the app exits unexpectedly, the next launch restores the temporary changes automatically.

## Recommendation engine

Rule classes implement `IRecommendationRule` and return at most one `Recommendation` with measured `Evidence`. Ranking = 0.6 × relevance + 0.25 × safety + 0.15 × reversibility, minus a penalty for items deferred during Trading Mode. A failing rule is isolated and cannot break the scan. No cloud or AI service is involved.

## Hardware interpretation

`CpuModelParser` turns brand strings into vendor/family/tier/generation/segment (e.g. i3-7020U → 7th gen, low-power mobile; i3-1005G1 → 10th gen; i3 M 350 → 1st gen). `HardwareInterpreter` produces insights (memory tier, HDD/eMMC, dual-core ULP, old generation) and `AssessLimitation` scores processor/memory/storage/thermal pressure from measurements only.

## UI

* WPF, MVVM with CommunityToolkit.Mvvm source generators, Microsoft.Extensions.DependencyInjection.
* Design tokens in `Themes/Colors.Dark.xaml` / `Colors.Light.xaml` (identical keys, enforced by `build/check_xaml.py`), density tokens in `Density.*.xaml`, all referenced with `DynamicResource` so theme and compact mode switch live.
* Custom lightweight controls: `PerformanceChart`, `RingGauge`, `ComparisonBars` (all `OnRender`, frozen geometry, no animation), `MetricCard`, `StatusBadge` (always text + colour), `Icon` (original 24×24 stroke icons), `ResponsiveGrid` (column count adapts to width — no horizontal scrolling at 1366×768).
* Custom window chrome, collapsible sidebar (auto-compact below ~1120 px), keyboard shortcuts Ctrl+1…9, visible focus rings, `AutomationProperties` names/live regions, UI scale 80–150 %.
* Tray icon uses `Shell_NotifyIcon` directly to avoid loading WinForms.

## Key decisions

| Decision | Why |
|---|---|
| WPF over WinUI 3 / Electron | Lowest memory and dependency footprint on Windows 10 1809+, no Windows App SDK runtime, no Chromium. |
| No bundled kernel driver for CPU temperature | Common open-source sensor drivers (e.g. WinRing0) have known privilege-escalation vulnerabilities. TICKSHIFT uses ACPI thermal zones and labels them honestly. `IThermalSensorProvider` is the extension point for a properly licensed, signed provider. |
| StartupApproved values rather than deleting Run entries | Same mechanism as Task Manager; fully reversible; nothing is lost. |
| Browser settings are *guided*, not edited | Browsers protect their preference files (MAC-signed "secure preferences"); editing them is fragile and can be treated as tampering. Policies and Local State are read-only inputs. |
| No RAM "cleaner", no service disabling, no registry tweak packs | These trade stability for benchmark numbers or have no verified benefit. |
| Self-contained publish | Old laptops rarely have the .NET 10 Desktop Runtime; self-contained avoids an extra download. ReadyToRun improves start-up on slow CPUs. |
| Per-machine installer | Keeps the elevated helper in an admin-protected directory. |
