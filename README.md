# BacklightSyncService

> **One screen brightness. Every power plan.**

Windows remembers a separate display brightness inside **every** power plan. Switch from "Balanced" to "Power saver" and your screen jumps to whatever that plan had stored — usually not what you want.

**BacklightSyncService fixes that.** It watches your brightness changes and writes the same level into *every* power plan (both the plugged-in and on-battery values). From then on, no matter which plan you activate, the brightness stays exactly where you left it.

Built for a ThinkPad T520 (Intel HD 3000 + Optimus) on Windows 10, targeting .NET 10 (LTS).

---

## Quick start

```powershell
# 1. Build (creates the publish\ folder)
dotnet publish -c Release -r win-x64 --self-contained true -o publish

# 2. Install — elevated PowerShell, from the repo root
.\scripts\install.ps1
```

That's it. The service installs itself, starts automatically at boot, and runs as LocalSystem. No tray icon, no UI — it just works in the background.

**Updating later is the same command.** The script stops the service, waits until it has *really* stopped, replaces the binaries, and starts it again. It also double-checks that the build you're installing matches the source code version — if you forgot to republish, it stops and tells you instead of silently installing an old exe.

## How it works

1. **It listens.** Brightness changes are caught two ways:
   - instant **WMI events** from the display driver (brightness keys, the slider, other apps), and
   - a **polling safety net** every 10 seconds, so nothing is ever missed even when events don't fire.
2. **It syncs.** When a change is detected, the service writes the new level into the **AC** (plugged-in) and **DC** (on-battery) brightness value of **every** power plan — the built-in ones *and* custom plans you created yourself.
3. **It writes only when needed.** Plans that already have the right value are skipped, and the active plan is re-applied so the change takes effect immediately.
4. **It's gentle with rapid changes.** A slider drag from 100% to 20% fires dozens of events; they're debounced into a single clean sync.

## Sleep & wake — handled

Laptops sleep. The service is ready for it:

- **On wake-up**, the service immediately re-establishes its change-detection signals (WMI subscriptions can silently die during sleep on some drivers) and **re-syncs** the brightness to all plans. If Windows changed the brightness while the lid was closed — say, you unplugged the charger during sleep — every plan is corrected within a couple of seconds of waking.
- **Self-healing safety net:** every 60 seconds the service checks whether the screen brightness and the stored plan values have drifted apart, and fixes it if they have. A dead WMI subscription is detected and restarted automatically on the next poll.

## What about custom plans?

Everything. The service enumerates **all** power schemes registered in Windows — there is no allow-list and nothing to configure. Plans you create later (Control Panel → Power Options → *Create a power plan*, or `powercfg -duplicatescheme`) are picked up automatically, and plans you delete stop being synced. The `--check` command shows exactly which plans the service sees.

## Configuration

All settings live in `appsettings.json` under the `BacklightSync` section (or as environment variables with the `BacklightSync__` prefix). The defaults are fine for most people.

| Setting | Default | What it does |
|---|---|---|
| `DebounceMilliseconds` | `500` | Collapses rapid changes (slider drags, key mashing) into one sync |
| `PollingIntervalSeconds` | `10` | How often the safety-net poll runs; `0` disables polling |
| `InitialSyncOnStart` | `true` | Syncs the current brightness to all plans when the service starts |
| `SyncAcValue` / `SyncDcValue` | `true` / `true` | Writes the plugged-in / on-battery brightness values |
| `WriteOnlyWhenChanged` | `true` | Skips plans that already store the target value |
| `ReapplyActiveScheme` | `true` | Re-applies the active plan after writing (like `powercfg /setactive`) |
| `IgnoreAdaptiveChanges` | `false` | Ignores sensor-driven (auto) brightness changes |
| `SuppressEventsAfterApplyMilliseconds` | `1000` | Loop protection after a sync — only filters events with the *same* value; real changes always get through |
| `PeriodicResyncSeconds` | `60` | How often the self-healing drift check runs; `0` disables it |

## Diagnosing problems

### 1. Quick health check

```powershell
& "C:\Program Files\BacklightSyncService\BacklightSyncService.exe" --check
```

Prints a snapshot: which brightness signals are available, the current brightness, the active plan, and every plan with its stored AC/DC values. Run it elevated for the most accurate picture.

### 2. Turn on logging (it's off by default)

The service is deliberately quiet — it writes **no log files** unless you ask. To diagnose:

```powershell
# in an elevated PowerShell — enables file logging:
$env:Logging__File__Enabled = "true"
Restart-Service BacklightSyncService
```

(or set `"Enabled": true` under `Logging:File` in `appsettings.json` instead).

Then watch what happens when you press the brightness keys:

```powershell
Get-Content "$env:ProgramData\BacklightSyncService\logs\backlight-sync.log" -Wait -Tail 30
```

You'll see lines like:

```
DBG WMI brightness event #1: Brightness=70 (adaptive=False).
INF Synchronized display brightness 70% across 5 power plan(s) (4 updated) — sync #2.
```

Turn it back off the same way when you're done. The **Event Log** (Event Viewer → Windows Logs → Application, source `BacklightSyncService`) always keeps one summary line per sync, so you can check there without enabling file logging.

### 3. Common issues

| Symptom | What's going on |
|---|---|
| `--check` says WMI classes "NOT available" | A driver quirk — the polling fallback still works. If brightness keys produce syncs in the log, everything is fine. |
| Nothing syncs at all | Is the service running? (`Get-Service BacklightSyncService`) Check the Event Log for errors and run `--check`. |
| `install.ps1` aborts with "STALE BUILD DETECTED" | You forgot to republish — run `dotnet publish ...` again, then reinstall. |
| Brightness jumps when switching plans | The sync is debounced by half a second — give it a second after changing brightness before switching plans. |
| Log file says "Access denied" | The file was created by the service (LocalSystem); run your console elevated, or let it fall back to `%LOCALAPPDATA%` automatically. |
| After wake-up, plans were out of sync | Shouldn't happen anymore (v1.4.0): the service re-syncs on resume and heals drift every 60 s. If you still see it, check the log for "Post-resume" lines. |

## Notes & limitations

- Works on machines with a controllable backlight (laptops, most all-in-ones). On a desktop without one there is nothing to sync, and the service simply does nothing.
- The service writes **stored plan values**; it never changes your screen brightness by itself. Windows remains in full control of the actual backlight.
- Multi-monitor setups: it uses the first brightness-capable display (usually the built-in panel).
- ThinkPad T520 notes: with Optimus, brightness control goes through the **Intel HD 3000** path (the NVIDIA GPU doesn't participate). The stock Windows 10 Intel driver may not fire WMI events for every change — that's exactly why the polling safety net exists.

## What's inside

```
BacklightSyncService/
├── Program.cs                       # entry point, DI, --check mode
├── appsettings.json                 # all configuration
├── BacklightSyncOptions.cs          # strongly-typed configuration
├── Services/
│   ├── BacklightSyncWorker.cs       # the sync loop, debounce, sleep/wake handling
│   ├── BrightnessWatcher.cs         # WMI events + registry + polling detection
│   ├── PowerEventMonitor.cs         # hidden window that hears sleep/wake broadcasts
│   ├── PowerPlanBrightnessWriter.cs # writes brightness into every power plan
│   ├── Diagnostics.cs               # --check health snapshot
│   └── FileLoggerProvider.cs        # optional file logging (off by default)
├── scripts/
│   ├── install.ps1                  # install / update (stops service, waits, swaps files)
│   └── uninstall.ps1                # remove
└── README.md
```

## Uninstalling

```powershell
.\scripts\uninstall.ps1   # elevated
```
