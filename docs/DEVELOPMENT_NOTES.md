# Ninth Pillar Handoff

Working folder:

`<game folder>`

## ⛔ IN-PROCESS PROXY-DLL APPROACH IS A DEAD END — 2026-07-04

**Do not retry DLL injection into SRX.exe.** Any DLL dropped in the game folder
to be loaded by SRX (winmm.dll proxy) triggers Steam anti-tamper: the game boots
to a FRESH profile (EULA prompt, "no saves") because it stops resolving the
Steam user ID, and writes a blank fallback save to `%APPDATA%\SRX\savegame.dat`
(instead of the real `%APPDATA%\SRX\<SteamID>\savegame.dat`).

Proven by A/B test (2026-07-04):
- winmm.dll present (full mod OR a do-nothing pass-through STUB that only
  forwards audio, no thread/patching) → empty/EULA boot. USER-CONFIRMED on both.
- winmm.dll absent → boots correctly with real save. USER-CONFIRMED ("Save is
  back!").
=> It is the DLL's mere presence (not our code) that trips it. The stub is
`mods\NinthPillar\src\stub_winmm.c`. Conclusion: proxy-DLL / in-process injection
is not viable for this game. Other proxy names (xinput/version/dinput) would very
likely trip the same check — not worth testing against the user's live saves.

No save data was ever lost — the real save stayed intact the whole time; the
game just wasn't reading it. Junk fallback copies were parked as
`%APPDATA%\SRX\savegame.dat.*` (safe to delete).

### In-game rendering CONFIRMED WORKING (2026-07-04)

The remaster still calls the original debug text renderer every frame. Live test
(USER-CONFIRMED on screen): setting `gameTrackerX.debugFlags` bit `0x4` or
`0x4000000` made `DEBUG_DisplayStatus` draw the FRTE/INS stats via `FONT_Print`.
=> We can draw our own on-screen overlay with the game's own text functions. In
REMASTERED graphics mode it uses the nice HD font automatically (the ugly font
only appears in "old graphics" mode). Reverted cleanly after the test.

Menu/overlay addresses (all sr1.dll RVAs, this session):
- `FONT_Print(const char* fmt, ...)` = **0x4FB30** (printf-style; `$@xx` in the
  string are position/color control codes, e.g. "$@EF", "$@KG"). Companions from
  decomp FONT.H: FONT_Print2, FONT_SetCursor(short x,short y),
  FONT_SetColorIndex(int), FONT_SetColorIndexCol(int,r,g,b) — RVAs TBD when needed.
- `debugFlags`  = **0x2A89430** (gameTrackerX+0x170). bits: 0x4 SHORT STATS,
  0x4000000 SHORT SHORT STATS, 0x40000000 EVENT_PrintVars.
- `debugFlags2` = 0x2A89430 region (same base word group; GHOST CAMERA 0x10000).
- `streamFlags` = 0x2A89510 (gameTrackerX+0x250).
- `gameMode`    = 0x2A89536 (u16, gameTrackerX+0x276).
- `currentMenu` (DebugMenuLine*) = **0x221220**; `standardMenu[12]` = **0x221230**
  (DebugMenuLine x64 stride 40: type@0, lower@4, upper@8, text@16(ptr),
  var_address@24(ptr), bit_mask@32). Remaster reordered standardMenu; dumped.
- `DEBUG_DisplayStatus` ~0x38E12 (needs exact start via .pdata before hooking).
- gameTrackerX base = 0x2A892C0.

PLANNED OVERLAY (build via the external helper, memory patch only — no DLL):
cave hooked into the per-frame `DEBUG_DisplayStatus` that calls `FONT_Print` on a
text buffer the helper fills each frame with our menu (turbo/footsteps/reaver/
invuln/mana). Helper must escape/avoid `%` in the buffer (FONT_Print is printf).
Then make the helper console-less (+ optional auto-start with SRX).

PIVOT: stay with the EXTERNAL helper (NinthPillar.exe, ReadProcessMemory/
WriteProcessMemory — never touches the game folder, proven safe all along). To
hit the two goals without injection:
- "no dumb terminal window" → run the helper as a hidden/background process
  (no console) and optionally auto-start it when SRX launches.
- "in-game menu" → the helper can STILL drive the game's built-in debug menu
  from outside via WriteProcessMemory (poke gameMode=4 + currentMenu, splice
  entries). Confirmed this session: gameMode is a u16 at sr1.dll RVA 0x2A89536
  (gameTrackerX base 0x2A892C0, field +0x276), verified live (reads 0 in play;
  neighbors StreamUnitID/WarpIndex/baseAreaName all match the decomp). DEBUG_Menu
  derefs the global `currentMenu` immediately, so set it (or trigger the native
  open path) before/with gameMode=4 or it null-derefs.

The abandoned in-process source stays in `mods\NinthPillar\src\` (ninthpillar.c,
stub_winmm.c, build.ps1) for reference only. The C ports of every hook (turbo
cave, sfx-limiter cave, reaver, invuln, health, mana) in ninthpillar.c are still
correct and reusable if a non-DLL in-process route ever appears.

--- (superseded plan below) ---

## NOW AN IN-PROCESS MOD (winmm.dll proxy) — 2026-07-04

The mod no longer needs the external console helper. It ships as a proxy
`winmm.dll` next to `SRX.exe` and runs inside the game at startup. **No console
window.** Install = copy `winmm.dll`; uninstall = delete it.

- DLL source: `mods\NinthPillar\src\ninthpillar.c`
- Build script: `mods\NinthPillar\build.ps1` (uses VS2022 BuildTools `vcvars64.bat`
  → `cl /O2 /MT /LD`; builds in a temp dir, copies `winmm.dll` to game root).
- Installed proxy: `<game folder>\winmm.dll`
- Runtime log: `mods\NinthPillar\ninthpillar.log` (attach + patch status; no console).

Why winmm: `SRX.exe` **statically** imports winmm (10 funcs: timeGetDevCaps,
timeGetTime, waveOutWrite, timeBeginPeriod, waveOutUnprepareHeader, waveOutClose,
waveOutReset, waveOutOpen, waveOutPrepareHeader, timeEndPeriod). `sr1.dll`/
`sr2.dll` import ONLY kernel32, so winmm is used exclusively by SRX — clean, tiny
proxy surface, loads at process start (earlier/more reliable than xinput1_3,
which is only LoadLibrary'd later; steam_api64/EOSSDK are delay-loaded). The
proxy loads the real `%WINDIR%\System32\winmm.dll` by full path and forwards all
10 (verified no self-reference: real winmm resolves to a different module base).

Note: Steam relaunches SRX once at startup (SteamAPI_RestartAppIfNecessary), so
`winmm.dll` attaches twice; the surviving process applies the patches.

VERIFIED WORKING 2026-07-04: game launches, no crash, single SRX process, all
hooks live in memory (timemult write site = `E8..90` call-to-cave; sfx table
entry → cave; reaver branch = `7D`; gameMode reads 0 at 0x2A89536). All patch
logic ported from the old C# helper to direct in-process memory writes.

Controls (keyboard, global via GetAsyncKeyState — F10 toggles menu-active):
F10 menu on/off; Up/Down turbo factor; Left/Right reaver threshold; PgUp/PgDn
footsteps/sec; I invulnerable; E infinite mana. Status shown in the game window
TITLE BAR (windowed) — the on-screen menu is Phase 2 (see below).

Not yet ported from the C# helper: background-mute (WASAPI/COM) and the on-screen
menu. gameMode debug-menu experiment (RVA 0x2A89536, base gameTrackerX=0x2A892C0)
is Phase 2 — needs `currentMenu`/`standardMenu` set to avoid a null deref.

Legacy (superseded, kept for reference): `mods\NinthPillar\NinthPillar.cs` / `.exe`
(external console helper). Do NOT run it alongside the proxy — double-patching
corrupts the hooks.

---

Important files:

- Helper source: `<game folder>\mods\NinthPillar\NinthPillar.cs`
- Helper exe: `<game folder>\mods\NinthPillar\NinthPillar.exe`
- Toggle installer: `<game folder>\install_or_remove.cmd`
- Installer script: `<game folder>\install_or_remove.ps1`
- Launcher shim source: `<game folder>\mods\NinthPillar\NinthPillarLauncher.cs`
- Launcher shim exe: `<game folder>\mods\NinthPillar\NinthPillarLauncher.exe`
- Patched game DLL: `<game folder>\1\sr1.dll`
- Original DLL backup: `<game folder>\1\sr1.dll.bak-20260704-072106`
- Decomp/reference clone used during debugging: `<decomp reference clone>`

## User Goal

Mods for Soul Reaver 1 Remastered:

- R2 turbo time acceleration while moving.
- Turbo only works when input order is left stick first, then R2. R2 first, then stick is aim mode and must not turbo.
- F10 helper menu for turbo factor, 1x-10x.
- Rate-limit footstep sound during turbo. CONFIRMED WORKING by user.
  Now adjustable: "allowed footsteps per second" menu setting, default 4/sec.
- Full health instead of 3/4 when falling from living world into spectral.
- Full health instead of 3/4 when true death respawns at Elder God.
- Full-health behavior is now menu-toggleable in the helper, default ON:
  `Spectral HP` and `Revive HP`.
- Reaver threshold menu options: 100%, 75%, 50%, 25%, always.
- "Sound in background disabled" menu option (mute game when unfocused).
- Invulnerable toggle (menu key I, default off).
- Infinite Eldritch Energy / glyph mana toggle (menu key E, default off).
- WANTED NEXT: convert helper to an installable in-process mod with an
  in-game F10 menu (see "Installable Mod Conversion Plan").

## Current Status

### Multi-controller turbo fix — 2026-07-04

Symptom: R2 gave no turbo with a DualSense connected. NOT a controller-type
issue and NOT the WMI spawn (a normal vs WMI-spawned probe read identically).

Root cause: `IsR2Moving()` looped XInput slots and `return`ed on the FIRST
connected slot. This machine has TWO XInput pads present — the DualSense (BT)
is exposed via Steam Input / ViGEm as emulated "Xbox 360 Controller" devices
(`USB\VID_045E&PID_028E`, driven by Valve `VID_28DE`). If slot 0 is the idle
phantom and the live input is on slot 1, the helper read the empty pad forever.
(Also explains "worked before": single controller = only slot 0.)

Fix (in `NinthPillar.cs`):
- `ReadAllPads()` aggregates the MAX right-trigger and MAX left-stick magnitude
  across ALL XInput slots AND all `Windows.Gaming.Input` gamepads, instead of
  reading one slot. Turbo fires if any pad has R2 held + stick pushed.
- `ReadAllButtons()` does the same (OR of button bits) for menu navigation, with
  WGI buttons mapped into the XInput bit layout.
- Added `Windows.Gaming.Input` (WinRT) as a second source so a DualSense in
  native mode (no XInput emulation) also works. All WGI calls are try/catch'd so
  a WGI hiccup can never break turbo; XInput alone still suffices for the
  emulated-pad case. Build refs added in `buildhelper.ps1` (Windows.winmd +
  .NET WinRT facades; pick newest versioned SDK dir, NOT the Facade stub).
- Verified: helper builds and runs with WinRT (no crash). Live in-game R2 test
  is the user's natural test; the overlay/title shows `R2=` / `Stick=` (now the
  max across pads) for immediate feedback.

### Stuck-turbo auto-reset — 2026-07-04

Symptom: game stuck at turbo speed after holding R2 as a cutscene ended.

Cause: `GAMELOOP_DoTimeProcess` (which our multiply hook lives inside) is
skipped entirely when `gameFlags & 0x10000000` is set (GAMELOOP.C:2101) — e.g.
during cutscenes/transitions. If `timeMult` was left inflated, it freezes there
and `factor=1` never gets applied, because the code that reads the factor isn't
running. Normal `timeMult=(lastLoopTime<<12)/33`: 4096==1x, max 8192 at the 66ms
frame cap; no feedback/compounding.

Fix (`NinthPillar.cs`, main loop, when NOT in turbo): directly write
`gameTrackerX.timeMult` (RVA 0x2A89628) back to 4096 — one-shot on the turbo
release edge (covers any factor) plus an ongoing guard that resets any value
> 8192 while idle. Safe during live play: the game's own time loop overwrites it
with the correct value next frame; the direct write only "sticks" when the loop
is dormant (the stuck case). Consts `NormalTimeMult=4096`, `MaxNormalTimeMult=8192`.

Working:

- Turbo works (now controller-agnostic; see multi-controller fix above).
- Releasing R2 always drops back to 1x, even if a cutscene froze the time loop.
- Input order rule works. User confirmed: "Order of operations works!"
- F10/menu works.
- Health patches appear installed.
- Reaver threshold patch/menu is installed.
- `install_or_remove.cmd` toggle installer exists. Install mode renames the
  real `SRX.exe` to `SRX.ninthpillar.original.exe`, copies the launcher shim to
  `SRX.exe`, and writes `mods\NinthPillar\installed.marker`. Uninstall mode
  moves the shim aside into `mods\NinthPillar\SRX.ninthpillar.launcher.removed-*.exe`
  and restores `SRX.ninthpillar.original.exe` to `SRX.exe`.
- The launcher shim starts `NinthPillar.exe`, starts the real game exe, waits for
  the game to close, then closes the helper.
- Installer path handling uses `-LiteralPath`, strips only one matching outer
  quote pair from pasted paths, accepts either the game folder or `SRX.exe`, and
  prompts if it cannot find the path automatically.

### Steam-launch fix — 2026-07-04 (verified end-to-end)

Symptom: launched through Steam, the game booted with NO turbo / F10 menu /
effects. Two bugs, both fixed:

1. **Process-name mismatch (the real bug).** The helper attached to process
   name `"SRX"` via `Process.GetProcessesByName`. After install the real game
   runs as the RENAMED `SRX.ninthpillar.original.exe` → process name
   `"SRX.ninthpillar.original"`, which never matched, so the helper never attached.
   Worse, the launcher STUB itself runs as `"SRX"` (Steam launches the stub)
   but has no sr1.dll. Fix in `NinthPillar.cs`: `FindProcess()` now walks the
   candidate names {`SRX`, `SRX.ninthpillar.original`} and returns the one that
   actually has `sr1.dll` loaded (`HasModule`), so it ignores the stub and
   picks the real game regardless of rename. This is THE fix for the reported
   symptom.

2. **Restart-through-Steam (robustness).** With no `steam_appid.txt`, the
   renamed exe launched outside Steam calls `SteamAPI_RestartAppIfNecessary`
   and bounces, escaping the stub. Through real Steam the stub inherits
   `SteamAppId` from the environment and passes it to the child, so this only
   bit direct launches — but it also blocked local verification. Added
   `steam_appid.txt` = `2521380` (managed by the installer: created on install,
   removed on uninstall if it still matches). Standard modding practice; makes
   the child-process launch reliable however it's started.

The process-name fix and `steam_appid.txt` are necessary but were NOT sufficient
for the actual Steam launch — see the overlay-injection hang below.

### Steam overlay deadlocks .NET processes — LAUNCHER NOW NATIVE (2026-07-04)

Symptom after the above fixes: launched through Steam, "game running" in Steam
but nothing visible; no turbo/menu. Found: the process Steam launched (the C#
launcher stub, `SRX.exe`) was HUNG — 3 threads, `gameoverlayrenderer64.dll`
injected, `clr.dll` loaded but `Main` never ran; it spawned neither the game nor
the helper. Launching the same C# stub directly (not via Steam) worked fine.

Root cause: **Steam injects `gameoverlayrenderer64.dll` into the exe it launches,
and injecting into a managed (.NET) process during CLR startup deadlocks it.**
This also hit the HELPER: even after making the launcher work, a helper spawned
as a normal child of the (injected) launcher was itself overlay-injected and hung
(~4 threads, ~0 CPU, never hooked).

Two-part fix (both verified end-to-end via `steam://rungameid/2521380`, memory-
probed the sfx table = HOOKED):

1. **Native launcher.** Rewrote the stub in C: `mods\NinthPillar\src\NinthPillarLauncher.c`,
   built by `mods\NinthPillar\buildlauncher.ps1` with MSVC (BuildTools 2022/2019,
   `/MT` static CRT, `/SUBSYSTEM:WINDOWS`). Steam injects the overlay into it
   fine (native exes take the overlay normally). It replaces `SRX.exe`. The old
   C# stub is kept as `SRX.ninthpillar.csharp-stub.bak.exe` for reference; DO NOT
   deploy it as SRX.exe.

2. **Helper spawned via WMI, not CreateProcess.** The launcher starts the helper
   through `Win32_Process.Create` (COM, `SpawnHelperViaWmi`). WmiPrvSE.exe creates
   it, so it is NOT a child of the injected launcher and the overlay never
   injects it. A managed helper outside the injection tree runs normally and
   hooks the game. (Also `HelperAlreadyRunning` dedups; `StopAllHelpers`
   terminates every NinthPillar.exe on game exit.)

Live install now carries all of it: `SRX.exe` = native launcher (136704 bytes),
helper rebuilt with the process-match fix, `steam_appid.txt` present. Installer
(`install_or_remove.ps1`) updated to build/deploy the native launcher via
`buildlauncher.ps1`. Just launch through Steam.

Key lesson for future work: NEVER put a .NET exe where Steam's overlay will
inject it (the Steam-launched target, or any child of it). Native for anything
in that path; spawn managed helpers out-of-tree via WMI.

- Footstep/SFX limiter v2 via the AAD sfx command dispatch table (see below).
  USER CONFIRMED WORKING ("THE FOOTSTEP SUPPRESSOR WORKS!"). Old broken hook
  attempts have been removed; helper restores their original bytes on attach.
- Adjustable rate: F10 menu PageUp/PageDown cycles allowed repeats/sec
  through {1,2,3,4,5,6,8,10,15,20}, default 4/sec. The cave reads the window
  in ms from `cave+0x604` (`cmp r9d, [rax+4]` where rax = realTick slot), so
  the helper just writes `1000/rate` there and it applies instantly.

Known side effect:

- At high turbo, player can fall through world. Likely due to large per-frame movement/collision/streaming step size. Practical mitigation is lower turbo factor; robust fix would require movement/collision sub-stepping rather than simple time scaling.

## Build/Run Helper

Compile/restart:

```powershell
$p = Get-Process NinthPillar -ErrorAction SilentlyContinue
if ($p) { $p | Stop-Process -Force; Start-Sleep -Milliseconds 300 }
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /platform:x64 /out:'<game folder>\mods\NinthPillar\NinthPillar.exe' '<game folder>\mods\NinthPillar\NinthPillar.cs'
Start-Process -FilePath '<game folder>\mods\NinthPillar\NinthPillar.exe' -WorkingDirectory '<game folder>\mods\NinthPillar'
```

## Implemented Patches

Direct `sr1.dll` health patches (on-disk file patches):

- RVA `0x0FE5BF`: `83334` -> `100000` for `razSpectralShift`.
- RVA `0x0E5E28`: `50000` -> `100000` for spectral death/underworld path.
- RVA `0x0E939D`: `50000` -> `100000` for `HealthInstantDeath`.

Helper runtime health toggles (2026-07-04 refresh):

- The helper now reapplies these values in process memory on attach and when
  toggled in the F10 menu.
- Menu labels:
  - `Spectral HP`: writes live RVA `0x0FF1BF` to `100000` when ON, `83334`
    when off. This corresponds to file offset `0x0FE5BF`.
  - `Revive HP`: writes live RVAs `0x0E6A28` and `0x0E9F9D` to `100000` when
    ON, `50000` when off. These correspond to file offsets `0x0E5E28` and
    `0x0E939D`.
- Both toggles default ON. Because `sr1.dll` is already patched on disk, OFF
  means "restore original value in live memory while the helper is running";
  restart without the helper will use whatever is in the DLL file.
- Crash note: an earlier helper build mistakenly treated the old file offsets
  as RVAs and wrote to `0x0FE5BF`, `0x0E5E28`, and `0x0E939D` in live memory;
  that corrupted active code and crashed when the user was hit by an enemy.
  Corrected in `NinthPillar.cs` at 2026-07-04 12:59. Use `WriteCode`, not plain
  `WriteInt`, for these code-section immediates.

Reaver helper patch (runtime):

- `ReaverBranchRva = 0x0FDDC3`, byte `0x74` -> `0x7D` so active path is taken at or above threshold.
- `ReaverMaterialImmRva = 0x0FDDB9`, writes threshold as `100000`, `75000`, `50000`, `25000`, or `0`.

Turbo (runtime):

- Correct gameplay `gameTrackerX.timeMult` is `GameTimeMultRva = 0x2A89628`.
- Writer sites in `GAMELOOP_DoTimeProcess`:
  - `TimeMultWrite1Rva = 0x06D827`
  - `TimeMultWrite2Rva = 0x06EED1`
- Helper allocates a nearby code cave and patches both writer sites with `call cave; nop`.
- Cave multiplies `eax` by current turbo factor and writes to `gameTrackerX.timeMult`.
- Default turbo factor currently `5x` (`turboIndex = 4`).

Input order:

- Current `IsR2Moving()` uses a small state machine.
- Stick moving threshold: `mag2 > 12000 * 12000`.
- On R2 rising edge: `turboArmed = wasStickMoving`; `aimBlocked = !wasStickMoving`.
- While held: turbo is `turboArmed && !aimBlocked && stickMoving`.

## SFX Limiter v2 (current implementation)

Root cause of earlier failures: all previous hooks were at guessed wrapper /
mixer entry points that the audible SFX path does not go through (or not
exclusively). The remaster plays ALL original sfx via the AAD library / SPU
emulator statically linked in `sr1.dll` (menu string "SHOW SPU EMULATOR
STATS"; original `SFX.WAS`/`BIGFILE.DAT` data; no remastered sfx files exist).

Verified static analysis of `sr1.dll` (backup copy, all offsets confirmed
against live DLL):

- Decomp reference: `KAIN2\Game\PSX\AADSFX.C`. Every sfx play is queued as a
  command and executed by `aadExecuteSfxCommand` through a function pointer
  table `sfxCmdFunction[]`; entry 0 is `sfxCmdPlayTone`, the single function
  where any sfx voice starts. `SndPlayVolPan` -> `aadPlaySfx` -> queue ->
  `sfxCmdPlayTone`.
- `aadExecuteSfxCommand` = RVA `0x0CEB50`:
  `movzx eax, byte [rcx]` / `cmp al, 10` / `jae ret` /
  `lea rdx, [rip+...]` -> table / `jmp [rdx+rax*8]` (tail dispatch).
- `sfxCmdFunction` table = **.data RVA `0x229EF0`**, 10 entries (remaster has
  one more command than the PSX decomp's 9):
  - `[0] 0x0CE3F0 sfxCmdPlayTone` (verified: reads handle from `[rcx+8]`, toneID = low word)
  - `[1] 0x0CE550 sfxCmdStopTone` (verified: voiceID 208 scan loop)
  - `[7] 0x0CEB00 sfxCmdSetVoiceKeyOn` (byte-for-byte match to decomp)
  - `[8] 0x0CEB20 sfxCmdSetVoiceKeyOff` (byte-for-byte match)
- Zero direct `call`/`jmp` references to `sfxCmdPlayTone` in .text; the table
  is the only dispatch. The only code xref to the table is the dispatcher.
- `AadSfxCommand` layout (x64 remaster): statusByte at `+0`,
  `ulongParam` (handle; low word = toneID) at `+8`.
- `aadMem` pointer lives at RVA `0x2E90D8` (not needed by the hook, noted for
  future work).

Hook mechanism (helper, runtime, no code patching):

- `SfxCmdTableEntry0Rva = 0x229EF0`, `SfxCmdPlayToneRva = 0x0CE3F0`.
- On attach the helper sanity-reads the table entry; it must equal
  `sr1.dll base + 0x0CE3F0` (or point outside the module = a previous cave).
  If it points elsewhere inside the module, the binary doesn't match and the
  hook is not installed (`Sfx limiter: MISMATCH` in the console).
- Helper allocates a 0x40000-byte per-toneID timestamp table (64K dwords)
  plus a small cave at `timeMultCave+0xB00`, then swaps the 8-byte table
  pointer to the cave.
- Cave logic: count hit; if turbo factor <= 1, tail-jmp real `sfxCmdPlayTone`.
  Otherwise read toneID from `[rcx+8]`, compare helper-maintained wall-clock
  tick (cave+0x600, written every helper loop from `Environment.TickCount`;
  game time is accelerated so `gameTrackerX.currentTime` must NOT be used)
  against `lastPlay[toneID]`; if the delta is below the window at cave+0x604
  (1000/allowed-per-sec, live-tunable via menu), increment `limited` counter
  and plain `ret` (dispatcher tail-jumped, so ret returns to its caller and
  the play is silently dropped). Else record time and tail-jmp real PlayTone.
- Per-toneID limiting means each distinct sound is capped at 1/sec during
  turbo; different sounds don't steal each other's budget. If footsteps
  alternate 2-3 material tone IDs the effective rate can be 2-3/sec; if the
  user wants stricter, group footstep tone IDs into one shared slot.
- Console line: `Sfx limiter: installed tones=<n> limited=<n>`. `tones` must
  rise whenever any sfx plays (proves table hook is live); `limited` rises
  only during turbo when repeats are dropped.

Old diagnostic hooks (SndPlayVolPan `0x0DC780` 14-byte jmp, suspected
SoundPlay3d `0x108950` 14-byte jmp, 8 play + 2 stop call sites) are gone from
the helper; on attach it restores original bytes at all those sites from the
`sr1.dll.bak-*` file so a still-running game gets cleaned up.

## Invulnerable / Infinite Mana Toggles

Both default OFF, toggled in the F10 helper menu (I and E).

Invulnerable (runtime code patch, reversible):

- `LoseHealthRva = 0x0E6050`, `DrainHealthRva = 0x0E53A0` (HEALTH.C).
  Verified by disasm fingerprints: LoseHealth tests `ControlFlag &
  0x1000000` (ControlFlag global = RVA `0x2A88568`), `invincibleTimer`
  (= RVA `0x2A88CD8`), `cmp HitPoints, 525`, `imul 20000`, `imul 122880`;
  DrainHealth has the `/4096` + `CurrentPlane==1` (RVA `0x2A88D1C`) +
  `PlayerData->healthMaterialRate` shape. `Raziel.HitPoints` = RVA
  `0x2A88CD0`. Both funcs start `sub rsp,28h`; toggle writes `0xC3`/`0x48`
  at entry. 8 call sites hit LoseHealth, 2 hit DrainHealth; all damage
  funnels through them.
- `HealthInstantDeath` is INLINED into a big multi-case state handler
  (pdata 0x0E8F10/0x0E8F4F region, the 50000-imm anchor at 0x0E939D lives
  there; zero direct calls). Ret-patching would kill unrelated cases, so
  abyss/instant-death still works while invulnerable. Acceptable.
- Remaster oddity: both health funcs begin with `cmp word [rva 0x2A89537],
  11; je skip` — some remaster-added mode check; unidentified, harmless.

Infinite Eldritch Energy (data poke, no code patch):

- `Raziel.GlyphManaBalls` = RVA `0x2A88CE8` (u16),
  `Raziel.GlyphManaMax` = RVA `0x2A88CEA` (u16). Verified via
  RAZIEL_DebugManaFillUp (`movzx ax,[Max]; mov [Balls],ax; ret` at
  0x0E8090) and RAZIEL_DebugManaSetMax (0x0E80A0; remaster clamps Max to
  52). While toggle on, helper writes Balls=Max every loop; skips when
  Max==0 (no capacity yet).

## Installable Mod Conversion Plan (researched, not started)

Goal: no external console; F10 opens an IN-GAME menu.

Facts established:

- SRX.exe imports (from its import strings): gdi32, hid, kernel32,
  opengl32 (renderer is OpenGL), ossdk-win64-shipping, setupapi, shcore,
  sr1.dll, sr2.dll, steam_api64, user32, winmm, xinput1_3, xinput9_1_0.
- Best in-process loader: proxy DLL in the game folder. `xinput1_3.dll` is
  ideal (not a KnownDLL, tiny export surface, game already calls it for
  pads — we also get controller input for free). `winmm.dll` is the
  fallback. Install = drop DLL next to SRX.exe; uninstall = delete.
- Everything the helper does ports to in-process trivially (direct memory
  writes instead of WriteProcessMemory; same RVAs off GetModuleHandle
  ("sr1.dll")). Needs a native toolchain (MSVC or MinGW) — csc cannot
  build native-export DLLs.
- In-game menu: the remaster still ships the ENTIRE original debug menu
  system in sr1.dll — DEBUG_Process/DEBUG_Menu code plus all menu strings
  ("MAIN MENU...", "RAZIEL MENU...", "GOODIES MENU...", "INVINCIBLE",
  "GAME TIME MULT", "SFX VOLUME", area warps, etc. at .rdata ~0x1A0500+).
  DEBUG_Menu runs when gameTracker gameMode == 4 (DEBUG.C:2029). Plan:
  find gameMode/cheatMode field in remaster gameTrackerX (struct base
  discoverable from timeMult at 0x2A89628 minus its struct offset), poke
  gameMode=4 as an experiment to see if the original debug menu still
  renders under the new renderer. If it renders, F10 handler = set/unset
  gameMode, and custom entries (turbo factor, footstep rate, reaver
  threshold) can be spliced in as DebugMenuLine arrays in a cave — the
  menu system is fully data-driven.
- If the debug menu doesn't render, fallback: draw our own overlay via
  the game's own text draw (DrawTile/FONT functions) or an OpenGL
  swap-buffer hook. More work; assess only if needed.

## Background Mute Option

"Sound in background: disabled/enabled", toggled with `B` while the F10 menu
is open. Default: disabled (game muted when unfocused).

- Implemented entirely in the helper via WASAPI. IMPORTANT lesson: the SRX
  audio session is NOT on the default render endpoint on this machine
  (observed live: default device had no SRX session; SRX was active on a
  different render device). First implementation searched only the default
  endpoint and silently never muted. Current implementation enumerates ALL
  active render devices (`EnumAudioEndpoints(eRender, ACTIVE)`) and calls
  `ISimpleAudioVolume.SetMute` on EVERY session whose PID is SRX.
- Foreground check by PID (`GetForegroundWindow` +
  `GetWindowThreadProcessId`); the helper's own console counts as focused so
  opening the F10 menu doesn't mute the game.
- No session caching: the full enumeration runs on each mute/unmute
  transition (rare — focus changes only), which also survives output device
  switches mid-game. Failed attempts (session not created yet) retry at most
  every 500 ms.
- Windows persists session mute across the helper's lifetime, so the helper
  unmutes on Ctrl+C exit if it left the game muted. If the helper is
  force-killed while the game is muted in the background, the game stays
  muted; restart the helper (or unmute SRX in the Windows volume mixer).

## Testing Checklist

1. Start game + helper, confirm console shows `Sfx limiter: installed`.
2. Walk around normally: `tones` counter should rise with footsteps and other
   sfx; sounds must be unaffected (no missing audio) since factor == 1.
3. Hold stick + R2 (turbo): footsteps should drop to ~1/sec per material,
   `limited` should rise steadily during movement.
4. Release turbo: normal footstep rate returns immediately.
5. Sanity: menu sounds, reaver sounds, combat all normal outside turbo.

## Important Cautions

- Use real-time tick, not `gameTrackerX.currentTime`, for any limiter. Game
  time accelerates under turbo and invalidates wall-clock rate limits.
- The sfx table entry swap must be undone (or the game restarted) before the
  helper's cave memory could ever be freed — the helper never frees it, so
  closing the helper mid-game is safe (hook stays, factor freezes at last
  written value; if it froze at >1 the limiter would keep limiting, but the
  helper writes 1 whenever turbo is off, so normally it freezes at 1).
- RVA mapping for `sr1.dll` .text: file offset = RVA - 0x1000 + 0x400.
  .data: file offset = RVA - 0x221000 + 0x220000. Image base 0x180000000.

## Restore Notes

- Original `sr1.dll` backup is `1\sr1.dll.bak-20260704-072106`.
- Avoid overwriting user changes without checking current helper source first.
