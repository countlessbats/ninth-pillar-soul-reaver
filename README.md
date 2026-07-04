# Ninth Pillar

A gameplay mod / trainer for **Legacy of Kain: Soul Reaver 1 Remastered** (the
SR1 side of *Soul Reaver 1 & 2 Remastered* on Steam, app `2521380`).

It adds a controllable "turbo" time-acceleration, a configurable in-game menu,
and a few quality-of-life cheats — all as runtime patches applied by a small
external helper. No game files are modified permanently; installing is fully
reversible.

## Features

- **R2 turbo** — hold R2 while moving the left stick to speed up game time.
  Input-order aware: stick-then-R2 = turbo; R2-then-stick = aim, no turbo.
  Releasing R2 always snaps back to 1× (even if a cutscene froze the timer).
- **Footstep-sound rate limiter** — caps repeated footstep SFX during turbo so
  it doesn't turn into a machine-gun; adjustable steps/second.
- **Invulnerability** toggle.
- **Infinite Eldritch Energy** (glyph mana) toggle.
- **Full spectral health** on plane-shift and on Elder-God revive (toggles).
- **Reaver threshold** menu options.
- **Mute game audio when in the background** toggle.
- **In-game F10 menu** — navigate with the D-pad; works with Xbox and
  PlayStation (DualShock/DualSense) controllers whether seen as XInput, via
  Steam Input, or in native mode.

## Install

1. Download/clone this repo.
2. Copy `install_or_remove.cmd`, `install_or_remove.ps1`, and the `mods` folder
   into your game folder (the one containing `SRX.exe`), e.g.
   `...\steamapps\common\Soul Reaver I-II\`.
3. Double-click `install_or_remove.cmd`. It swaps in a tiny native launcher,
   backs up the real game exe, and writes a `steam_appid.txt`.
4. Launch the game from Steam as usual.

Run `install_or_remove.cmd` again to **uninstall** (restores the original
`SRX.exe`).

The prebuilt `mods/NinthPillar/NinthPillar.exe` and `NinthPillarLauncher.exe`
are included, so no compiler is needed to install.

## Controls

- **R2 + left stick** — turbo (hold).
- **F10** — open/close the in-game menu.
- With the menu open: **D-pad Up/Down** to move, **Left/Right or A** to
  adjust/toggle, **B/Circle** to close.

## How it works

Steam launches a small **native** stub (`SRX.exe`) that starts the real game
plus the **Ninth Pillar helper**, then the helper attaches to the game and
applies its patches at runtime (code caves + memory writes). The launcher is
native, and the helper is started out-of-process via WMI, specifically to avoid
the Steam overlay deadlocking a managed launcher — see
[`docs/DEVELOPMENT_NOTES.md`](docs/DEVELOPMENT_NOTES.md) for the full
reverse-engineering write-up, RVA map, and the dead-ends that were ruled out.

## Building from source

- **Helper** (`mods/NinthPillar/NinthPillar.cs`): needs the .NET Framework C#
  compiler (`csc`) and the Windows SDK WinRT metadata. Run
  `mods/NinthPillar/buildhelper.ps1`.
- **Launcher** (`mods/NinthPillar/src/NinthPillarLauncher.c`): needs MSVC Build
  Tools. Run `mods/NinthPillar/buildlauncher.ps1`.

## Credits

Made by **countlessbats**.

## Disclaimer

Not affiliated with or endorsed by Crystal Dynamics, Aspyr, or Square Enix.
"Legacy of Kain" and "Soul Reaver" are trademarks of their respective owners.
This project contains **no game assets or code** — only original tooling that
modifies the game in memory at runtime. It is intended for single-player use;
use at your own risk. Licensed under the [MIT License](LICENSE).
