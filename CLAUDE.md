# screen-layout

Windows tray-less tool: one-key monitor layout switching plus a background watcher (`watch`,
started from the user's Startup folder) that owns the global hotkeys (each runs
`ScreenLayout.exe set <mode>`) and keeps the chosen mode applied across reboot, sleep and monitor
hotplug. See README.md for usage.

## Build / run

- `build.cmd` - compiles with the .NET Framework 4 `csc.exe` that ships with Windows. No SDK, no
  project file. **C# 5 only**: no string interpolation, `?.`, expression-bodied members, `out var`,
  `nameof`.
- The watcher holds `ScreenLayout.exe` open: `Stop-Process -Name ScreenLayout` before building,
  then restart it with `ScreenLayout.exe watch`.
- Per-PC files next to the exe (gitignored): `layouts.ini`, `mode.txt` (chosen mode),
  `ScreenLayout.log`. The repo is used on more than one PC with different monitors, so nothing
  PC-specific goes into tracked files; `layouts.example.ini` is the committed example.
- Debugging: `ScreenLayout.exe status` logs the current state; `preview <mode>` shows the toast
  without switching. Every apply logs before/after state and `ok`/`MISMATCH`.
- Every apply makes the screens flicker for several seconds - warn the user before running
  `set` in a test loop.

## How it works / gotchas learned the hard way

- **Windows CCD database is keyed by the set of connected monitors.** Boot, logon and resume
  restore the entry for the current set. Applying a layout only writes the entry for the set
  connected at that moment - that's why layouts "reset" and why the watcher exists.
- **Which monitors are on is set via CCD directly** (`Ccd.SetActive`: QueryDisplayConfig
  ALL_PATHS, pick one available path per wanted monitor with distinct sources, invalidate mode
  indices, SetDisplayConfig with SAVE_TO_DATABASE). Monitors are identified by Short Monitor ID
  parsed from the target device path (`\\?\DISPLAY#IVM7628#...`).
- **Resolution, Hz, position and primary of the monitors that are on are set by `Gdi.SetModes`**
  (ChangeDisplaySettingsEx per monitor with CDS_UPDATEREGISTRY | CDS_NORESET, primary with
  CDS_SET_PRIMARY at 0,0, then one apply call; Hz snapped to what EnumDisplaySettings lists, since
  59.94 Hz modes show up as 59). It needs `\\.\DISPLAYn` names, which shift when monitors are
  turned on/off: `Ccd.GdiNames` resolves them from the Short ID (GET_SOURCE_NAME) right before
  use. Never store a `\\.\DISPLAYn` mapping - an earlier version used NirSoft MultiMonitorTool
  `.cfg` files keyed by them, and with 3 monitors they applied one monitor's settings to another
  output. The tool has no external dependencies; keep it that way.
- Struct offsets in `Ccd` are hand-coded (DISPLAYCONFIG_PATH_INFO = 72 bytes, MODE_INFO = 64,
  TARGET_DEVICE_NAME = 420 with friendly name at 36 and device path at 164, TARGET_PREFERRED_MODE
  = 80 with vSyncFreq at 48, SOURCE_DEVICE_NAME = 84 with the GDI name at 20; DEVMODEW = 220).
  Change them only together with the struct layout. Device info types: GET_SOURCE_NAME = 1,
  GET_TARGET_NAME = 2, GET_TARGET_PREFERRED_MODE = 3 (7 is a different request, rc 87).
- Watcher loop guards: 3 s debounce, 8 s quiet period after its own apply, at most 2 applies per
  mode + connected-set signature (reset on unlock/logon). Keep these when touching the watcher:
  each apply fires WM_DISPLAYCHANGE / WM_DEVICECHANGE itself.
- If a mode's monitor isn't connected, the rest of the mode is applied (positions shifted so the
  effective primary is at 0,0); if none is connected, nothing is done.
- The toast is painted by hand (GDI+, DPI-scaled, non-activating, DWM rounded corners). To check
  it visually, run `preview` and screenshot from a process that called `SetProcessDPIAware` first.

## Layouts

`layouts.ini` (per PC; `ScreenLayout.exe init` generates it from the connected monitors, and the
watcher does so on start if it's missing): `[monitors]` gives display names (and the left-to-right order in the toast's
diagram); each other section is a mode with `name`, `hotkey` (registered by the watcher, and
shown in the toast) and `<ShortID> = WxH@Hz x,y [primary]` lines. A monitor listed in some mode
but not in the current one is turned off. The watcher reads the hotkeys only at startup, so
restart it after editing them. Optionally add a Start Menu shortcut in
`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Screens\` (target `ScreenLayout.exe`, arguments
`set <mode>`, icon `imageres.dll,104`), **without a hotkey**.

## Hotkeys

- Registered with `RegisterHotKey` in the watcher, not as .lnk hotkeys: Explorer stopped firing
  the shortcut hotkeys after the .lnk files were recreated, and it keeps holding a shortcut's old
  hotkey until it restarts. A hotkey another program holds fails with error 1409 and is retried
  every 30 s (see the log).
- Never put the hotkeys back on the shortcuts: Explorer would grab them and the watcher couldn't
  register them.
