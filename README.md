# WindowsBacklightSyncService

> **One screen brightness. Every power plan.**

Windows remembers a separate display brightness inside **every** power plan. Switch from "Balanced" to "Power saver" and your screen jumps to whatever that plan had stored — usually not what you want.

**WindowsBacklightSyncService fixes that.** It watches your brightness changes and writes the same level into *every* power plan (both the plugged-in and on-battery values). From then on, no matter which plan you activate, the brightness stays exactly where you left it.

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

The script works from **both** layouts: a repo checkout (`scripts\install.ps1` with a sibling `publish\`) and a downloaded release zip (`publish\scripts\install.ps1` with the exe in the same `publish` folder). In a release bundle the version guard is skipped (no csproj present) — the bundle is versioned by its filename.

## How it works

1. **It listens.** Brightness changes are caught two ways:
   - instant **WMI events** from the display driver (brightness keys, the slider, other apps), and
   - a **polling safety net** every 10 seconds, so nothing is ever missed even when events don't fire.
2. **It syncs.** When a change is detected, the service writes the new level into the **AC** (plugged-in) and **DC** (on-battery) brightness value of **every** power plan — the built-in ones *and* custom plans you created yourself.
3. **It writes only when needed.** Plans that already have the right value are skipped, and the active plan is re-applied so the change takes effect immediately.
4. **It's gentle with rapid changes.** A slider drag from 100% to 20% fires dozens of events; they're debounced into a single clean sync.
5. **It ignores system dimming.** Windows dims the screen by itself after inactivity and restores it when you return (and battery saver dims it too). Those changes are **not** synced — only deliberate user changes are. The service knows the difference because Windows records user-set brightness in the *active* plan's stored value, while system dimming never touches it: any level that doesn't match the active plan's stored level is treated as system-initiated and skipped. (Disable via `IgnoreSystemDimming: false`.)

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
| `IgnoreSystemDimming` | `true` | Ignores system-initiated changes (inactivity dim + restore, battery saver) — only deliberate user changes are synced |
| `SuppressEventsAfterApplyMilliseconds` | `1000` | Loop protection after a sync — only filters events with the *same* value; real changes always get through |
| `PeriodicResyncSeconds` | `60` | How often the self-healing drift check runs; `0` disables it |

## Diagnosing problems

### 1. Quick health check

```powershell
& "C:\Program Files\WindowsBacklightSyncService\WindowsBacklightSyncService.exe" --check
```

Prints a snapshot: which brightness signals are available, the current brightness, the active plan, and every plan with its stored AC/DC values. Run it elevated for the most accurate picture.

### 2. Turn on logging (it's off by default)

The service is deliberately quiet — it writes **no log files** unless you ask. To diagnose:

```powershell
# in an elevated PowerShell — enables file logging:
$env:Logging__File__Enabled = "true"
Restart-Service WindowsBacklightSyncService
```

(or set `"Enabled": true` under `Logging:File` in `appsettings.json` instead).

Then watch what happens when you press the brightness keys:

```powershell
Get-Content "$env:ProgramData\WindowsBacklightSyncService\logs\windows-backlight-sync.log" -Wait -Tail 30
```

You'll see lines like:

```
DBG WMI brightness event #1: Brightness=70 (adaptive=False).
INF Synchronized display brightness 70% across 5 power plan(s) (4 updated) — sync #2.
```

Turn it back off the same way when you're done. The **Event Log** (Event Viewer → Windows Logs → Application, source `WindowsBacklightSyncService`) always keeps one summary line per sync, so you can check there without enabling file logging.

### 3. Common issues

| Symptom | What's going on |
|---|---|
| `--check` says WMI classes "NOT available" | A driver quirk — the polling fallback still works. If brightness keys produce syncs in the log, everything is fine. |
| Nothing syncs at all | Is the service running? (`Get-Service WindowsBacklightSyncService`) Check the Event Log for errors and run `--check`. |
| `install.ps1` aborts with "STALE BUILD DETECTED" | You forgot to republish — run `dotnet publish ...` again, then reinstall. |
| Brightness jumps when switching plans | The sync is debounced by half a second — give it a second after changing brightness before switching plans. |
| Screen dims after inactivity but the plans don't follow | **By design** (`IgnoreSystemDimming: true`) — system dimming and its restore are not user changes and aren't synced. Set `IgnoreSystemDimming: false` if you want the dim level synced too. |
| A deliberate brightness change isn't synced | Windows must first record it in the active plan (it normally does within ~1 s). If it doesn't on your driver, the poll/periodic check picks it up — or set `IgnoreSystemDimming: false`. |
| Log file says "Access denied" | The file was created by the service (LocalSystem); run your console elevated, or let it fall back to `%LOCALAPPDATA%` automatically. |
| After wake-up, plans were out of sync | Shouldn't happen anymore (v1.4.0): the service re-syncs on resume and heals drift every 60 s. If you still see it, check the log for "Post-resume" lines. |

## Notes & limitations

- Works on machines with a controllable backlight (laptops, most all-in-ones). On a desktop without one there is nothing to sync, and the service simply does nothing.
- The service writes **stored plan values**; it never changes your screen brightness by itself. Windows remains in full control of the actual backlight.
- Multi-monitor setups: it uses the first brightness-capable display (usually the built-in panel).
- ThinkPad T520 notes: with Optimus, brightness control goes through the **Intel HD 3000** path (the NVIDIA GPU doesn't participate). The stock Windows 10 Intel driver may not fire WMI events for every change — that's exactly why the polling safety net exists.

## Testing

Unit tests live in `WindowsBacklightSyncService.Tests` (xUnit, no hardware or WMI needed — everything runs against in-memory fakes). Coverage includes: the decision logic (system-dimming filter, loop protection, adaptive ignore, per-plan write decisions), the worker's sync behavior (debounce collapse, force, clamping, per-index AC/DC writes, failure handling, re-apply rules), configuration binding (code defaults vs. `appsettings.json`), the file logger (writes, rotation, pruning, fallback, filtering, exception details, concurrent read), the log-filter category chain, the WMI MOF parser, and the `--check` snapshot.

```powershell
# run the tests
dotnet test WindowsBacklightSyncService.Tests\WindowsBacklightSyncService.Tests.csproj -c Release

# run with a coverage report (coverlet; produces TestResults\*\coverage.cobertura.xml)
dotnet test WindowsBacklightSyncService.Tests\WindowsBacklightSyncService.Tests.csproj -c Release --collect:"XPlat Code Coverage"
```

Both GitHub Actions workflows run the tests on every build; a release is not produced unless they pass.

## What's inside

```
WindowsBacklightSyncService/
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

## Continuous integration (GitHub Actions)

Two ready-made workflows live in `.github/workflows/`:

- **`dotnet-desktop.yml`** — CI on every push/PR to `main`: restores, builds, publishes (`dotnet publish -c Release -r win-x64 --self-contained true -o publish`), smoke-tests the binary with `--check`, and uploads the `publish` folder as the `WindowsBacklightSyncService-win-x64` artifact.
- **`release.yml`** — publishes a **GitHub Release** with the compiled binaries whenever you push a version tag.

## Making a release

Releases are **fully automatic** — two ways to trigger, pick whichever you prefer:

**A) Bump-and-push (no tags):**
```powershell
# 1. Bump the version in WindowsBacklightSyncService.csproj
#    (e.g. <Version>1.4.1</Version> — the next release's version)
# 2. Commit and push to main
git add WindowsBacklightSyncService.csproj
git commit -m "Bump version to 1.4.1"
git push origin main
```
The workflow creates a release **only if the csproj version is newer than the latest existing release** (unchanged version → run completes, skips, logs "skipping release creation").

**B) Tag (classic, always releases):**
```powershell
git tag v1.4.1 && git push origin v1.4.1
```
A tag push always creates the release for that version (and validates it matches the csproj version). Both `v1.4.1` and bare `1.4.1` tags are accepted.

> ⚠️ If a release was created but shows **only source code archives** (no zips/checksums), the Release workflow didn't run for it — usually because the tag didn't match the trigger (e.g. a bare `1.4.0` tag on an older workflow, or a release created manually from the UI). Fix: delete that release **and its tag**, then re-trigger properly:
> ```powershell
> gh release delete 1.4.0 --yes --cleanup-tag
> git push origin v1.4.0     # after pushing the fixed workflow
> ```

Either way the `Release` workflow builds **both** win-x64 variants, smoke-tests them with `--check`, and creates a GitHub Release with auto-generated notes, both zips and their SHA-256 checksums attached. A manual run from the Actions tab uses the csproj version (same skip logic as A).

### Which zip should I download?

| Zip | Size | Requires | Use when |
|---|---|---|---|
| `...-selfcontained.zip` | ~70 MB | nothing | The default — works on any Windows 10/11, no .NET installed needed. **Pick this for the T520.** |
| `...-frameworkdependent.zip` | ~200 KB | .NET 10 Runtime on the target | The machine already has .NET 10, or you manage runtimes centrally (e.g. via WSUS/Intune). |

Both install identically:

```powershell
Expand-Archive WindowsBacklightSyncService-v1.4.1-win-x64-selfcontained.zip -DestinationPath .
.\publish\scripts\install.ps1   # elevated
```

(Verify with `Get-FileHash ... -Algorithm SHA256` against the matching `.sha256` file attached to the release.)
