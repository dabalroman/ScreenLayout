# ScreenLayout

One-key switching between monitor layouts on Windows. Press `Ctrl+Alt+1` for both monitors,
`Ctrl+Alt+2` for just the main one, `Ctrl+Alt+3` for the projector, and so on. A small toast shows
the new mode, and the layout **stays** that way across reboots, sleep, and monitors being switched
off and on.

- Turns whole monitors on and off, and sets resolution, refresh rate, position and the primary
  monitor.
- Generates a starting config from the monitors you have (`ScreenLayout.exe init`).
- Runs as a single small background process with no tray icon, no installer, and no .NET SDK.
- Builds with the C# compiler that ships with every Windows 10/11, and needs nothing else.

## Why it exists

Windows keeps one display layout **per set of connected monitors** and restores it on boot,
logon and resume. A layout you apply while a monitor is off, or while its cable link is down, only
updates the entry for that set. When the monitor comes back, Windows brings back whatever it had
stored for the other set, so your layout seems to "reset" on its own.

`ScreenLayout.exe watch` remembers the mode you chose. Whenever the connected monitors change, it
applies that mode again. This also writes the mode into Windows' database for the new set, so
even the logon screen follows it.

## Requirements

Windows 10 or 11. The .NET Framework 4.x it uses is part of the OS.

## Setup

### 1. Get and build it

```powershell
git clone https://github.com/dabalroman/ScreenLayout.git
cd ScreenLayout
.\build.cmd
```

This produces `ScreenLayout.exe` next to the sources. Keep the folder where it is: the exe reads
and writes its files next to itself.

### 2. Generate your layouts

Connect and switch on **all** your monitors, then run:

```powershell
.\ScreenLayout.exe init
```

This writes `layouts.ini` for the monitors it finds and opens it in Notepad. You get:

- **All monitors**: the monitors that are on stay where they are now, and the others are placed to
  the right,
- **Current layout**: only generated when just some of the monitors are on,
- **one "only" mode per monitor**,

with hotkeys `Ctrl+Alt+1`, `Ctrl+Alt+2`, ... in that order. Rename the modes, fix the positions or
refresh rates, and change the hotkeys as you like. The format is described [below](#layoutsini).
`init` never overwrites an existing `layouts.ini`. To start over, delete the file and run `init`
again.

### 3. Start the watcher at logon

The watcher owns the hotkeys and keeps the chosen layout applied. To start it with Windows, run
this in PowerShell from the ScreenLayout folder:

```powershell
$exe = (Resolve-Path .\ScreenLayout.exe).Path
$lnk = (New-Object -ComObject WScript.Shell).CreateShortcut("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\ScreenLayout.lnk")
$lnk.TargetPath = $exe; $lnk.Arguments = 'watch'; $lnk.WorkingDirectory = Split-Path $exe; $lnk.Save()
Start-Process $exe watch
```

Then press `Ctrl+Alt+1`. Each switch makes the screens flicker for a few seconds while Windows
reconfigures them, which is normal.

To uninstall, delete the `ScreenLayout.lnk` shortcut from the Startup folder
(`shell:startup`), stop `ScreenLayout.exe` in Task Manager, and delete the folder.

## Usage

```
ScreenLayout.exe set <mode>      switch to a mode (what the hotkeys run)
ScreenLayout.exe watch           background watcher (one instance; started at logon)
ScreenLayout.exe init            generate layouts.ini from the connected monitors
ScreenLayout.exe preview <mode>  show the toast without switching anything
ScreenLayout.exe status          write the current monitor state to the log
```

`<mode>` is a section name from `layouts.ini`, e.g. `set all`. You can also make Start Menu or
desktop shortcuts that run `ScreenLayout.exe set <mode>`. **Don't assign them a shortcut key**,
though: Explorer would take that key and the watcher couldn't register it.

## layouts.ini

```ini
[monitors]
IVM7628 = iiyama PL3466WQ
DELA0D9 = Dell P2217H

[both]
name = Both
hotkey = Ctrl+Alt+1
IVM7628 = 3440x1440@144 0,0 primary
DELA0D9 = 1920x1080@60 -1920,0

[main]
name = Main only
hotkey = Ctrl+Alt+2
IVM7628 = 3440x1440@144 0,0 primary
```

- **`[monitors]`** maps each monitor's **Short Monitor ID** to a display name. The ID is the
  manufacturer and model code from the monitor's EDID, e.g. `DELA0D9`; `init` fills these in for
  you. The toast draws the monitors left to right in this order.
- **Every other section is a mode.** The section name is what you pass to `set`.
  - `name`: the title shown in the toast.
  - `hotkey`: a combination of `Ctrl`, `Alt`, `Shift`, `Win` and a key (a letter, a digit, or a
    .NET `Keys` name such as `F5`, `NumPad1`, `Home`).
  - `<Short Monitor ID> = <width>x<height>@<Hz> <x>,<y> [primary]`: one line for each monitor that
    is on in this mode. Positions are in pixels relative to the primary monitor at `0,0`. A
    monitor to the left of the primary has a negative `x`.
- A monitor that appears in some mode but isn't listed in this one is **turned off**.
- If a monitor of the mode isn't connected, the rest of the mode is still applied.

The watcher reads the hotkeys only when it starts. After changing them, stop `ScreenLayout.exe`
and start `ScreenLayout.exe watch` again. Changes to the monitor lines take effect on the next
switch. [`layouts.example.ini`](layouts.example.ini) is a complete example.

## Troubleshooting

Everything is logged to `ScreenLayout.log` next to the exe. Every switch writes the state before
and after, ending in `ok` or `MISMATCH`.

- **A hotkey does nothing.** Another program holds it. The log says `is taken by another program
  (error 1409)`, and the watcher retries every 30 s. Pick another combination, or free it in
  the other program.
- **The wrong refresh rate is used** (e.g. 29 Hz on a TV). Set the right `@<Hz>` in the mode line;
  `init` takes the monitor's preferred mode, which isn't always the one you want.
- **Two identical monitors.** They share a Short Monitor ID, so ScreenLayout can't tell them
  apart.
- **The layout keeps getting re-applied** after you change it in Windows Settings. That's what
  the watcher is for. Change it in `layouts.ini` instead, or switch modes with a hotkey.

## How it works

- Monitors are identified by Short Monitor ID, read from the display target's device path. These
  IDs stay the same when monitors are disabled or ports renumber, unlike `\\.\DISPLAYn`.
- Which monitors are on is set through the Windows CCD API (`QueryDisplayConfig` /
  `SetDisplayConfig`, saved to the display database). Resolution, refresh rate, position and
  primary are then set with `ChangeDisplaySettingsEx`. Its `\\.\DISPLAYn` name for each monitor
  is looked up from the monitor's ID right before every switch.
- The watcher listens for display, device, resume and unlock events. It waits for things to
  settle, then re-applies the chosen mode (stored in `mode.txt`) if the current state differs.
  Loop guards stop it from fighting a layout that can't be reached.

## Files

| File | |
|---|---|
| `ScreenLayout.cs` | the whole tool |
| `build.cmd` | builds `ScreenLayout.exe` with the Windows-bundled `csc.exe` |
| `layouts.example.ini` | example configuration |
| `layouts.ini`, `mode.txt`, `ScreenLayout.log` | per-PC files next to the exe, not in git |

## License

[MIT](LICENSE)
