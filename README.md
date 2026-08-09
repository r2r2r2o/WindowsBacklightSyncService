# BacklightSyncService

A **.NET 10 (LTS)** Windows service that **synchronizes the display backlight level across all Windows power plans** — whenever the backlight changes (brightness keys, the Settings slider, or another app), the new level is written to the *AC* and *DC* brightness values of **every** power scheme. No matter which plan you switch to, the brightness stays where you set it.

## How it works

```
┌────────────────────────────────────────────────────────────────────────────┐
│  Brightness change (brightness key / slider / app)                         │
│        │                                                                   │
│        ▼                                                                   │
│  WMI event: WmiMonitorBrightnessEvent (root\wmi)   ◄── primary signal      │
│  + polling fallback: WmiMonitorBrightness (every N s)                      │
│        │                                                                   │
│        ▼                                                                   │
│  Debounce (500 ms) — slider drags fire many events; collapse to one sync   │
│        │                                                                   │
│        ▼                                                                   │
│  For each power scheme (PowerEnumerate):                                   │
│    PowerWriteACValueIndex(SUB_VIDEO, VIDEONORMALLEVEL, level)              │
│    PowerWriteDCValueIndex(SUB_VIDEO, VIDEONORMALLEVEL, level)              │
│        │                                                                   │
│        ▼                                                                   │
│  PowerSetActiveScheme(active) — re-apply so the value takes effect now     │
└────────────────────────────────────────────────────────────────────────────┘
```

Equivalent `powercfg` commands (the service does the same thing via the native `powrprof.dll` API instead of spawning processes):

```bat
:: for each scheme GUID from "powercfg /list":
powercfg /setacvalueindex <scheme> SUB_VIDEO aded5e82-b909-4619-9949-f5d71dac0bcb <level>
powercfg /setdcvalueindex <scheme> SUB_VIDEO aded5e82-b909-4619-9949-f5d71dac0bcb <level>
powercfg /setactive SCHEME_CURRENT
```

### Key identifiers

| Item | GUID / alias | Notes |
|---|---|---|
| Display subgroup | `7516b95f-f776-4464-8c53-06167f40cc99` (`SUB_VIDEO`) | |
| Display brightness level | `aded5e82-b909-4619-9949-f5d71dac0bcb` (`VIDEONORMALLEVEL`) | Hidden in `powercfg /q` by default, range 0–100 % |

### All power plans — including user-created custom ones

The service enumerates **every** power scheme registered in Windows (`PowerEnumerate`, `ACCESS_SCHEME`) — the default plans *and* custom plans created by the user (Control Panel → Power Options → *Create a power plan*, or `powercfg -duplicatescheme <existing-plan>`). There is no allow-list: any plan that exists on the machine gets its AC/DC brightness synchronized.

- Plans created **while the service is running** are picked up automatically on the next brightness change and logged as `New power scheme detected (user-created custom plan?): "..."`.
- Plans that are deleted stop being synchronized (also logged).
- `--check` lists every plan the service will synchronize — run it after creating a custom plan to verify it is included.

### Change detection & safety

Change detection is **layered**, so it works even when the display driver exposes no WMI brightness classes (as on the ThinkPad T520's Intel HD 3000 under Windows 10):

1. **WMI event** `WmiMonitorBrightnessEvent` — event-driven, when the driver provides it
2. **Registry watch** — Windows updates `HKCU\Software\Microsoft\Windows\CurrentVersion\Brightness` (values `AutoAdaptive`/`Value`/`SensorValue`) on every backlight change *even when the WMI classes are missing*; the service watches this key and treats it as a change signal
3. **Polling** — `WmiMonitorBrightness`, and if that's absent, the same registry values, every `PollingIntervalSeconds`

`--check` now reports the availability of all three signals explicitly.

## Project structure

```
BacklightSyncService/
├── BacklightSyncService.csproj      # net10.0-windows, Worker SDK
├── Program.cs                       # host builder, DI, Windows service wiring, --check mode
├── appsettings.json                 # all knobs + logging levels
├── BacklightSyncOptions.cs          # strongly-typed configuration
├── Services/
│   ├── BacklightSyncWorker.cs       # debounce, sync pipeline, polling fallback, heartbeat
│   ├── BrightnessWatcher.cs         # WMI event subscription + current-value query
│   ├── PowerPlanBrightnessWriter.cs # powrprof.dll P/Invoke (write/read/enumerate/activate/name)
│   ├── Diagnostics.cs               # "--check" diagnostic mode + startup environment snapshot
│   └── FileLoggerProvider.cs        # file logger (captures Debug/Trace for diagnosis)
├── scripts/
│   ├── install.ps1                  # build → copy → create & start service
│   └── uninstall.ps1                # stop & remove service
└── README.md
```

## Prerequisites

- Windows 10/11 with a controllable backlight (laptop or monitor with ACPI/DDC brightness — this is what the `root\wmi` brightness classes require). .NET 10 officially supports Windows 10 (1607, 1809, 21H2 — listed for Enterprise/LTSC editions since consumer Windows 10 is past end-of-life; it also runs fine on Home/Pro builds).
- To build: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (build machine can be Windows or Linux).
- The service must run **elevated** (LocalSystem as installed — fine). It needs access to `root\wmi` and `powrprof.dll` power policy writes.

## Build

```bat
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

`--self-contained` is recommended — the service then carries its own .NET 10 runtime and the target machine needs **no .NET installation at all**. Alternatively omit `-r win-x64 --self-contained` and install the .NET 10 Desktop Runtime on the target.

## Install

From an elevated PowerShell, in the `scripts` folder (expects `..\publish` next to it):

```powershell
.\install.ps1
```

The script is **safe to re-run for updates**: if the service is already installed and running, it stops the service, **waits until it has fully stopped and its process has exited**, then replaces the binaries (with retries in case a file handle lingers), reconfigures and restarts it. No manual `Stop-Service` needed.

Or manually:

```bat
sc create BacklightSyncService binPath= "C:\Program Files\BacklightSyncService\BacklightSyncService.exe" start= auto
sc description BacklightSyncService "Synchronizes the display backlight level across all Windows power plans."
sc start BacklightSyncService
```

The service runs as **LocalSystem** (default for `sc create` / `New-Service`).

## Uninstall

```powershell
.\uninstall.ps1        # from the scripts folder, elevated
```

## Configuration (`appsettings.json` → `BacklightSync` section)

| Setting | Default | Meaning |
|---|---|---|
| `DebounceMilliseconds` | `500` | Collapse rapid brightness changes into one sync |
| `PollingIntervalSeconds` | `10` | WMI polling fallback; `0` disables polling |
| `InitialSyncOnStart` | `true` | Sync current brightness to all plans when the service starts |
| `SyncAcValue` | `true` | Write the AC (plugged in) brightness index |
| `SyncDcValue` | `true` | Write the DC (on battery) brightness index |
| `WriteOnlyWhenChanged` | `true` | Skip plans that already store the target value |
| `ReapplyActiveScheme` | `true` | `PowerSetActiveScheme` after writing (same as `powercfg /setactive`) |
| `IgnoreAdaptiveChanges` | `false` | Ignore sensor-driven (adaptive) brightness changes |
| `SuppressEventsAfterApplyMilliseconds` | `1000` | Loop protection after each sync — only suppresses events reporting the *same* value as last applied; real user changes pass through |

Any setting can also be overridden with environment variables (`BacklightSync__DebounceMilliseconds=300`) or the registry-less service configuration of your choice.

## Verification

```powershell
# list plans
powercfg /list

# check the brightness value stored in a specific plan (should equal the current backlight %)
powercfg /getacvalueindex <scheme-guid> SUB_VIDEO aded5e82-b909-4619-9949-f5d71dac0bcb
powercfg /getdcvalueindex <scheme-guid> SUB_VIDEO aded5e82-b909-4619-9949-f5d71dac0bcb

# end-to-end check
# 1) press a brightness key or move the slider
# 2) within ~1 s, both commands above for EVERY plan (default and custom) report the new level
# 3) switch plans (powercfg /setactive <other-scheme>) — brightness must not jump
# 4) create a custom plan (powercfg -duplicatescheme SCHEME_BALANCED), change brightness,
#    and confirm the custom plan's value follows too (log shows "New power scheme detected")
```

## Hardware notes — ThinkPad T520 (Intel HD 3000 + NVIDIA NVS 4200M, Optimus)

- **Which GPU controls the backlight:** With NVIDIA Optimus the internal LCD is always wired to the **Intel HD 3000**; the NVS 4200M only renders (and drives external ports). Brightness control — the Fn brightness keys, ACPI/embedded-controller events and the `root\wmi` brightness classes — therefore goes through the **Intel graphics driver path**, which is exactly what this service listens to. Nothing special is needed for the NVIDIA GPU.
- **WMI events on Sandy Bridge:** `WmiMonitorBrightnessEvent` is provided by the Intel driver. On the stock Windows 10 driver for the HD 3000 it is **not available** — the service's **registry fallback** (`HKCU\...\CurrentVersion\Brightness`) is the change signal in that case, so sync still works.
- **Ambient light sensor:** The T520 has an ALS; if *Change brightness automatically when lighting changes* is on, sensor-driven changes are synced to all plans too (default `IgnoreAdaptiveChanges: false`). Set `"IgnoreAdaptiveChanges": true` if you don't want the sensor propagated.
- **Windows 10 + .NET 10:** Sandy Bridge x64 supports the .NET 10 x64 instruction baseline, and .NET 10 officially supports Windows 10. Deploy **self-contained win-x64** so the T520 needs no .NET runtime installation.
- **Suggested `appsettings.json` for the T520:** keep the defaults, but set `PollingIntervalSeconds` to `2`–`3` if the WMI event class turns out to be unreliable. Everything else works out of the box.

## Logging & diagnostics

Everything the service does is logged to a **file** when enabled (in addition to the Event Log and the console when run interactively).

### Feature flag — file logging is OFF by default (v1.3.5+)

Diagnostic file logging is controlled by a **single feature flag**, shipped **disabled** so the service writes nothing to disk while running in the background:

```jsonc
"Logging": {
  "File": {
    "Enabled": false   // ← false = file logging off (default); true = on
  }
}
```

To re-enable diagnostics (no recompile needed): set `"Enabled": true` in `appsettings.json`, or set the environment variable `Logging__File__Enabled=true`, then restart the service. Verbosity is controlled separately via `Logging:File:LogLevel:Default` (`Debug` = full diagnostics, `Information` = reduced noise).

| Sink | Where | Level | Feature flag |
|---|---|---|---|
| Log file | `%ProgramData%\BacklightSyncService\logs\backlight-sync.log` (falls back to `%LOCALAPPDATA%\BacklightSyncService\logs\backlight-sync.log` if not writable) | **Debug** (includes WMI events, per-plan writes, exceptions) | `Logging:File:Enabled` (default **false** — off) |
| Event Log | Application, source `BacklightSyncService` | Information+ | always on (one summary line per sync; set `Logging:EventLog:LogLevel:Default` to `Warning` to quiet it too) |
| Console | when you run the exe manually | all | always on |

**Never fatal:** the file logger never crashes the service. If the configured path cannot be opened — e.g. you run the exe manually *without elevation* while the log file was created by the LocalSystem service under `%ProgramData%`, so your user cannot append to it — the logger automatically falls back to `%LOCALAPPDATA%\BacklightSyncService\logs\backlight-sync.log` and reports the switch on the console and in the Event Log. If even that fails, file logging is disabled and the service keeps running (the Event Log still works).

The log file rotates at 5 MB (3 backups kept); path/size/enabled are configurable under `Logging:File`. The log starts with a full environment snapshot (WMI class availability, current brightness, every plan with stored AC/DC values, active plan, power state), then records every change event, poll, write decision and sync summary.

### 1) One-shot diagnostic (run elevated, from any console)

```powershell
& "C:\Program Files\BacklightSyncService\BacklightSyncService.exe" --check
```

Prints: OS/process/user/elevation, power state, which WMI brightness classes exist, current brightness, active plan, and **every plan with its stored AC/DC brightness** (marked `*` if active). All lines are also appended to the log file.

Additional flags (combine freely):

```powershell
# end-to-end write test: writes the current brightness into every plan and reads it back
& "...\BacklightSyncService.exe" --check --write-test

# listen for WMI brightness events for 10 s — press brightness keys / move the slider during it
& "...\BacklightSyncService.exe" --check --listen
```

### 2) Watch the live log while reproducing

```powershell
Get-Content "$env:ProgramData\BacklightSyncService\logs\backlight-sync.log" -Wait -Tail 50
```

Then press a brightness key / move the slider. You should see, in order:

```
DBG WMI event schema: class=WmiMonitorBrightnessEvent, properties=[Active, InstanceName, Brightness, Adaptive]
DBG WMI brightness event #1: Brightness=65 (adaptive=False).
DBG Brightness change #1: 65% (adaptive=False).
DBG Debounce scheduled: 65% in 500ms.
DBG Sync #1: target brightness 65% over 3 plan(s):
DBG   Balanced: stored AC=65 DC=30 -> wrote AC=False DC=True
DBG   High performance: stored AC=60 DC=60 -> wrote AC=True DC=True
INF Synchronized display brightness 65% across 3 power plan(s) (2 updated) — sync #1.
```

If you do **not** see these lines, the trace in the log will show exactly where it stops:
- no `WMI brightness event` lines but `Poll #N` lines → events don't fire on this driver, polling is the active path (consider `PollingIntervalSeconds: 2`)
- no `Poll #N` lines at all → brightness WMI classes unavailable; run `--check` and check the WMI lines
- `WRITE FAILED` / warnings → per-plan write errors with the exact Win32 message

## Logs

- **Log file**: `%ProgramData%\BacklightSyncService\logs\backlight-sync.log` — Debug level, this is where you diagnose. Tail it with `Get-Content ... -Wait -Tail 50`.
- **As a service**: Windows Event Log → *Application* (source `BacklightSyncService`, Information level), e.g. `Synchronized display brightness 65% across 3 power plan(s) (2 updated).`
- **As a console app** (debugging): run `BacklightSyncService.exe` directly — full console output including Debug-level WMI diagnostics. For a one-shot diagnosis use `--check` (see "Logging & diagnostics").

## Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| `--check` shows `WmiMonitorBrightness ... NOT available` | v1.3.2+ uses an accurate WMI schema check (previously a `meta_class` query that reported false negatives — the "Brightness: 100%" line proving the class exists while the check said NOT available). Re-publish and reinstall. |
| `--check` shows `Brightness registry hives: NONE found` | This T520 does not maintain the brightness registry values; change detection falls back to WMI events/polling (which work — see `Brightness: N%`). |
| `--check` shows plan names as `?` | Fixed in v1.3.2 — plan names are now read from the registry (HKLM PowerSchemes) with the native API as fallback. |
| `install.ps1` aborts with `STALE BUILD DETECTED` | The `publish` folder contains an older exe than the source — run `dotnet publish` again (the guard compares the exe's file version with the csproj `<Version>`). |
| Multiple user accounts on the machine (e.g. `Archi` + `ArchiAdmin`) | The registry watch monitors **all** user hives that contain the brightness key, so it works regardless of which account is logged on. |
| `--check` shows event class available but `--listen` receives 0 events | Driver registers the class but delivers no events; the polling fallback still syncs — set `PollingIntervalSeconds: 2`. |
| Log shows `Failed to process WMI brightness event ... Not found` | Driver-specific event property layout (Intel HD 3000). Fixed in v1.3.3: extraction tries candidate property names, parses the event's MOF text, and finally falls back to the instance query — the event is always treated as a change signal, so syncs are instant instead of waiting for the next poll. |
| Log shows `Poll #N: brightness=<unavailable>` | `WmiMonitorBrightness` itself is unavailable — same as the first row. |
| Log shows `WRITE FAILED` / `Failed to sync brightness for power scheme` | Per-plan write error with exact Win32 message — check elevation (service must run as LocalSystem/admin). |
| Log file missing/empty | Service not running (`Get-Service BacklightSyncService`), or the path was changed; run the exe manually in a console to see errors. |
| `Access is denied` on the log path when running the exe / `--check` without elevation | The log file under `%ProgramData%` was created by the service (LocalSystem) and your user cannot append to it. Run the console elevated, or just use the automatic `%LOCALAPPDATA%` fallback (the switch is reported on the console and in the Event Log). |
| Service starts but never syncs | Machine has no brightness-capable display (desktop with no DDC/ACPI brightness) → `WmiMonitorBrightness` returns nothing; nothing to do by design. |
| Brightness changes are missed or lag | On Intel Sandy Bridge machines, rely on the polling fallback: set `PollingIntervalSeconds: 2`. |
| Brightness jumps after switching plans | `ReapplyActiveScheme` disabled, or the sync did not run before the switch — check Event Log / log file. |
| Brightness flickers once when syncing | Rare race between write and re-apply; increase `SuppressEventsAfterApplyMilliseconds` / `DebounceMilliseconds`. |
| Rapid key presses ignored right after a sync | Fixed in v1.3.4 — the suppression window now only filters loop events (same value as last applied); genuine user changes are always processed. |
| Want sensor/adaptive changes ignored | Set `IgnoreAdaptiveChanges: true`. |
| A custom plan is not synchronized | Plans have no allow-list — every registered scheme is synced. Run `--check` to confirm the plan is listed; if it is, check the log for write errors for that GUID. |

## Notes & limitations

- The service writes the **stored plan values**; it never changes the actual screen brightness by itself. The current brightness stays fully under Windows'/the user's control.
- On multi-monitor setups it uses the first brightness-capable display (typically the built-in panel).
- It syncs on every real change — including adaptive-brightness events (disable via `IgnoreAdaptiveChanges`), because the stored value tracks actual behavior by design.
- The "Display brightness level" power setting is hidden in `powercfg /q` output, but `powercfg /setacvalueindex` / `PowerWriteACValueIndex` operate on it normally — this is the standard mechanism (it is the plan's baseline brightness).
