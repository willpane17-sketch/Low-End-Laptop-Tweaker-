# Troubleshooting

Logs are in `%LOCALAPPDATA%\Tickshift\logs` (redacted, one file per day). *Recovery Centre › Export diagnostic report* produces a shareable summary without personal details.

| Symptom | Explanation / fix |
|---|---|
| **"Sensor unavailable" for CPU temperature** | The laptop firmware does not expose an ACPI thermal zone to Windows. Many laptops only report CPU temperature through the manufacturer's own driver. TICKSHIFT deliberately does not estimate it or install a kernel driver. Heat symptoms (sustained load, reduced processor speed, passive cooling limit) are still analysed. |
| **Temperature looks low compared with other tools** | ACPI thermal zones are often motherboard/chassis sensors. The Thermal Monitor labels the source and adjusts thresholds. |
| **Disk activity shows "—"** | The Windows `PhysicalDisk` performance counter is disabled or corrupted. Rebuilding counters (`lodctr /r` in an admin prompt) usually fixes it; TICKSHIFT will not do this for you. |
| **Drive type "not reported"** | Windows' Storage Management API returned no media type (common on older drivers and some VMs). TICKSHIFT does not guess SSD vs HDD. |
| **"High performance plan is not available"** | Modern Standby laptops only expose Balanced. Use Windows' power mode slider (Settings › System › Power) for "Best performance". |
| **Power plan change "not verified"** | An OEM utility or group policy reverted it. The journal records this; nothing else is changed. |
| **UAC prompt appears when disabling a startup app** | The entry applies to all users (HKLM or the common Startup folder). Declining leaves it unchanged. |
| **"The administrator helper is not installed"** | `Tickshift.ElevatedHelper.exe` is missing next to `Tickshift.exe` (e.g. a partial copy of the portable build). Reinstall or re-extract the full folder. |
| **Restore point "skipped"** | Windows allows one automatic restore point per 24 hours by default, or System Protection is off (System Properties › System Protection). TICKSHIFT's own recovery records still apply. |
| **Browser cache cleanup not available** | Close Edge/Chrome completely (including background apps in the notification area). TICKSHIFT never closes your browser. |
| **Browser settings shown as "check in the browser"** | Browsers keep these settings in protected files. TICKSHIFT only reads enterprise policies and the Local State file and never edits them. |
| **Settings page opens but a browser page does not** | Some browser versions ignore internal `edge://`/`chrome://` URLs from the command line. Copy the address from the tip into the browser's address bar. |
| **SmartScreen warning when installing** | The build is unsigned. Use a signed release or build from source. Do not disable SmartScreen. |
| **App does not start a second time** | TICKSHIFT is single-instance; a second launch brings the running window to the front. Look for the icon in the notification area. |
| **App crashed during Trading Mode** | Relaunch: temporary Trading Mode settings are restored automatically and you are notified. |
| **Settings reset unexpectedly** | The settings file was corrupt; TICKSHIFT restored the last good backup or safe defaults and kept the corrupt copy as `settings.json.corrupt-<time>` for inspection. |
| **Want to remove TICKSHIFT cleanly** | Recovery Centre › *Restore everything*, then uninstall from Settings › Apps. The uninstaller removes the Launch-with-Windows entry and offers to delete the data folder. |

## Resetting completely

1. Recovery Centre › *Restore everything*.
2. Exit TICKSHIFT (notification-area icon › Exit).
3. Delete `%LOCALAPPDATA%\Tickshift`.
