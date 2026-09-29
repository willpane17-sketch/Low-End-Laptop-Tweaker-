# Security

TICKSHIFT prioritises the user's security over marginal performance gains. This document describes the threat model, privilege design and data handling.

## Things TICKSHIFT never does

* Disable or reconfigure Microsoft Defender, Windows Firewall, Smart App Control, SmartScreen, UAC, exploit protection, Windows Update or System Restore.
* Add antivirus exclusions.
* Disable Windows services, apply registry "tweak packs", or manipulate working sets ("RAM cleaners").
* Execute downloaded scripts, shell commands, or any command derived from user input.
* Read or store browser passwords, cookies, history, sessions or tokens; broker credentials; trading accounts, positions or orders.
* Terminate or restart applications automatically; force-kill anything; change process priorities.
* Install services, scheduled tasks, drivers or hidden autostart entries. *Launch with Windows* is a visible HKCU Run value named `TICKSHIFT`.
* Send telemetry. The only network traffic is the connectivity tests the user starts (ICMP echo / TCP handshake to the router, `www.msftconnecttest.com` — Windows' own connectivity host — and user-chosen endpoints). No application data is sent.

## Privilege design

| Component | Runs as | Why |
|---|---|---|
| `Tickshift.exe` | Standard user (`asInvoker` manifest) | Everything else it does is per-user or read-only. |
| `Tickshift.ElevatedHelper.exe` | Administrator (`requireAdministrator`), started per action with a UAC prompt | Creating a restore point; changing a machine-wide startup entry. |
| Installer | Administrator, once | Per-machine install into Program Files so the helper binary cannot be replaced by a standard-user process. |

### Elevated helper contract

`src/Tickshift.Security/ElevatedOperations.cs` defines the complete contract; the helper and the client use the same validator.

* `restore-point` — no arguments. Description text is fixed in the helper.
* `startup-approval <Run|Run32|StartupFolder> <base64 name> <24 hex chars | delete>` — the name must be ≤ 260 characters with no path separators or control characters; the value must be exactly 12 bytes with a recognised state byte (`02/03/06/07`); the helper verifies the entry already exists in `HKLM\…\Run` (64- or 32-bit view) or the common Startup folder before writing only `HKLM\…\Explorer\StartupApproved\<list>\<name>`.

Anything else exits with `InvalidArguments`. The helper has no network access, reads no files, starts no processes, and exits after one operation. Results are conveyed only through the exit code; the unprivileged app then re-reads the state itself to verify.

The client refuses to launch the helper if it is missing or is a reparse point. Before production distribution, enable Authenticode signing so users (and SmartScreen) can verify both binaries.

## File-system safety

Every deletion passes `SafePathPolicy`:

* the path must be strictly inside an allowlisted root (per-user `%TEMP%` only if it is inside the user profile; the user's WER folders; Chromium cache folders under the browser's *User Data*);
* no segment between the root and the file may be a reparse point (junction/symlink/mount point) — defends against link-following attacks;
* Documents, Desktop, Downloads, Pictures, Music, Videos, Favorites, OneDrive, System32 and Program Files are always refused;
* read-only and in-use files are skipped.

Tests cover traversal (`..`), forbidden folders and symlink escapes.

## Process safety

`ProcessClassifier` puts processes into Critical / Windows component / Security / Protected trading / Browser / Application / Self. Only *Application* and *Browser* processes in an interactive session may be offered for closing, and only via `CloseMainWindow()` (the app can prompt to save work). `MayOfferClose` re-checks critical and security names as defence in depth.

## Data handling

* All data lives in `%LOCALAPPDATA%\Tickshift` (settings, change journal, histories, logs, reports).
* Settings contain preferences, protected app names/paths, protected website domains and optional connectivity endpoints (host + port only). Credentials in URLs are rejected at input.
* Logs and exported reports pass through `LogRedactor`, which removes the profile path, user and machine names, e-mail addresses, URL paths/queries and `key=value` secrets. Window titles are never collected.
* Logs are capped (2 MB per file) and deleted after the retention period (default 30 days). History stores are bounded.
* `DpapiSecretProtector` (current-user DPAPI) is available for any future data that needs protection at rest; TICKSHIFT currently stores nothing that requires it.

## Supply chain

* Dependencies: CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection/Logging, System.Management, System.Diagnostics.PerformanceCounter, System.Security.Cryptography.ProtectedData (runtime); xUnit (tests). All from nuget.org, versions pinned centrally in `Directory.Packages.props`.
* No kernel drivers are bundled. In particular, WinRing0-based sensor libraries are deliberately avoided because of known vulnerabilities in that driver.
* Private signing keys must never be committed (`.gitignore` excludes `*.pfx`, `*.p12`, `*.snk`). See [SIGNING.md](SIGNING.md).

## Reporting a vulnerability

Please report security issues privately to the repository owner rather than opening a public issue.
