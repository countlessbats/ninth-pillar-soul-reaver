using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Windows.Gaming.Input;

internal static class NinthPillar
{
    // The game may run under its real name ("SRX") in dev mode, or under the
    // installer's renamed backup ("SRX.ninthpillar.original") when the launcher-
    // stub install is active. When installed, the launcher stub ALSO runs as
    // "SRX" but has no sr1.dll loaded, so we select by loaded module, not name.
    private static readonly string[] ProcessNameCandidates = { "SRX", "SRX.ninthpillar.original" };
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint PAGE_READWRITE = 0x04;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;

    private const int TimerGetMillisecondsRva = 0x119400;
    private const int GameTimeMultRva = 0x2A89628;
    // Game's timeMult = (lastLoopTime<<12)/33: 4096 == 1x (30fps); slow frames
    // cap at 66ms => 8192. Anything above 8192 while not in turbo is a stuck
    // (turbo-inflated) value that the multiply hook can't clear on its own when
    // the game's time loop is dormant (e.g. during/after a cutscene).
    private const int NormalTimeMult = 4096;
    private const int MaxNormalTimeMult = 8192;

    // In-game overlay: DEBUG_DisplayStatus (RVA 0x38040, verified true entry via
    // .pdata unwind chain) is called every frame in play and only draws debug
    // text (all gated behind debugFlags that are normally off), so we replace it
    // wholesale with a cave that draws OUR menu buffer via FONT_Print (RVA
    // 0x4FB30) and returns. FONT_Print is printf-style: our text uses a leading
    // "$@EF\n" position code (proven to render on screen) and %% for a literal %.
    private const int DebugDisplayStatusRva = 0x38040;
    private const int FontPrintRva = 0x4FB30;

    // gameTrackerX.controlCommand[2][5] + controlData[2][5] (active-high processed
    // input, verified empirically at gameTrackerX+0x98, 0 when idle). Blanked
    // while the menu is open so D-pad navigation doesn't drive Raziel. Stops
    // before the active-low raw pad at +0xE8 (which must stay 0xFFFF, not 0).
    private const int ControlBlockRva = 0x2A89358; // gameTrackerX(0x2A892C0)+0x98
    private const int ControlBlockLen = 0x50;      // through +0xE7
    private static readonly byte[] InputZero = new byte[ControlBlockLen];
    private const int TimeMultWrite1Rva = 0x06D827;
    private const int TimeMultWrite2Rva = 0x06EED1;
    private const int ReaverBranchRva = 0x0FDDC3;
    private const int ReaverMaterialImmRva = 0x0FDDB9;
    // These were originally discovered as file offsets. Convert .text file
    // offset -> RVA by subtracting raw 0x400 and adding virtual 0x1000.
    private const int SpectralShiftHealthRva = 0x0FF1BF; // file offset 0x0FE5BF
    private const int ReviveHealthRva = 0x0E6A28;        // file offset 0x0E5E28
    private const int InstantDeathHealthRva = 0x0E9F9D;  // file offset 0x0E939D
    private const int FullHealth = 100000;
    private const int SpectralShiftDefaultHealth = 83334;
    private const int ReviveDefaultHealth = 50000;

    // AAD sfx command dispatch (aadExecuteSfxCommand at 0x0CEB50 does
    // `jmp [sfxCmdFunction + statusByte*8]`). Entry 0 is sfxCmdPlayTone,
    // the single function every sfx play goes through. rcx = AadSfxCommand*,
    // word [rcx+8] = toneID.
    private const int SfxCmdTableEntry0Rva = 0x229EF0;
    private const int SfxCmdPlayToneRva = 0x0CE3F0;

    // HEALTH.C functions (verified by disasm: ControlFlag/invincibleTimer/525/
    // 20000/122880 fingerprint at 0x0E6050; /4096 + CurrentPlane +
    // healthMaterialRate shape at 0x0E53A0). Both start with `sub rsp, 28h`
    // (first byte 0x48). Invulnerable = 0xC3 (ret) at entry.
    // HealthInstantDeath is inlined into a larger state handler and is NOT
    // patched, so abyss/instant-death still applies.
    private const int LoseHealthRva = 0x0E6050;
    private const int DrainHealthRva = 0x0E53A0;
    private const byte HealthFuncFirstByte = 0x48;

    // Raziel globals (verified via RAZIEL_DebugManaSetMax/SetMana disasm).
    private const int GlyphManaBallsRva = 0x2A88CE8; // unsigned short
    private const int GlyphManaMaxRva = 0x2A88CEA;   // unsigned short

    // Raziel globals (verified live). Abilities holds ability bits; 0x40 is the
    // "SHIFT ANY TIME" ability, 0x10 is swim (RAZIEL_OkToShift requires both to
    // allow a spectral->material shift without a portal). HitPoints/invincible-
    // Timer/CurrentPlane drive death; streamFlags 0x80000 makes the game loop
    // run UNDERWORLD_StartProcess (Elder-God death) next frame.
    private const int AbilitiesRva = 0x2A88CDC;
    private const int HitPointsRva = 0x2A88CD0;
    private const int InvincibleTimerRva = 0x2A88CD8;
    private const int CurrentPlaneRva = 0x2A88D1C;
    private const int StreamFlagsRva = 0x2A89510;
    private const int ShiftAnyTimeMask = 0x50;    // shift-anytime (0x40) + swim (0x10)
    private const int UnderworldStreamFlag = 0x80000;

    // Old diagnostic hook sites from earlier attempts; restored to original
    // bytes on attach so a game still carrying them gets cleaned up.
    private const int OldSndPlayVolPanRva = 0x0DC780;
    private const int OldSoundPlay3dRva = 0x108950;
    private static readonly int[] OldCallSiteRvas = {
        0x0E5379, 0x0E538C,
        0x0E9E1F, 0x0E9E2C,
        0x0EB2CF, 0x0EB2DC,
        0x0EC063, 0x0EC070,
        0x0E54C9, 0x0E54DC
    };

    private static readonly int[] TurboFactors = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
    private static readonly int[] ReaverPercents = { 100, 75, 50, 25, 0 };
    private static readonly int[] SfxPerSecond = { 1, 2, 3, 4, 5, 6, 8, 10, 15, 20 };
    private static int turboIndex = 4;
    private static int reaverIndex = 2;
    private static int sfxRateIndex = 3; // 4 per second
    private static bool menuOpen;
    private const int MenuCount = 10;     // Turbo, Footsteps, Reaver, Invuln, Mana, BgSound, SpectralHP, ReviveHP, ShiftAny, Die
    private static int menuSel;           // highlighted option
    private static ushort prevPad;        // for D-pad edge detection
    // XInput button bits
    private const int PAD_UP = 0x0001, PAD_DOWN = 0x0002, PAD_LEFT = 0x0004,
                      PAD_RIGHT = 0x0008, PAD_A = 0x1000, PAD_B = 0x2000;
    private static bool running = true;
    private static bool lastTurbo;
    private static byte lastR2;
    private static int lastStickMagnitude;
    private static int loopCounter;
    private static IntPtr turboFactorAddress;
    private static IntPtr realTickAddress;
    private static IntPtr sfxWindowAddress;
    private static IntPtr toneHitsAddress;
    private static IntPtr toneSkipsAddress;
    private static IntPtr timeMultCave;
    private static IntPtr toneTableAddress;
    private static IntPtr overlayCave;
    private static IntPtr overlayDrawFlag;
    private static IntPtr overlayBuffer;
    private static bool overlayInstalled;
    private static string lastOverlayText = "";
    private static int toneHits;
    private static int toneSkips;
    private static string sfxHookStatus = "not installed";
    private static bool invulnerable;
    private static bool infiniteMana;
    private static bool shiftAnywhere;
    // Mutes ONLY the game's own audio session while the game is unfocused
    // (verified: never touches other apps). On by default; toggle in F10 menu.
    private static bool muteInBackground = true;
    private static bool fullHealthOnSpectral = true;
    private static bool fullHealthOnRevive = true;
    private static bool gameMuted;
    private static int mutedPid;
    private static int lastMuteAttemptTick;
    private static readonly int helperPid = Process.GetCurrentProcess().Id;
    private static bool wasR2Down;
    private static bool wasStickMoving;
    private static bool turboArmed;
    private static bool aimBlocked;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string text);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState14(int index, out XINPUT_STATE state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState910(int index, out XINPUT_STATE state);

    // WASAPI audio session interop, used to mute the game's audio session
    // while its window is in the background. Only the vtable slots up to the
    // last method actually called are declared where the tail is unused.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out int pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    private static volatile bool uiAttached;

    [STAThread]
    private static int Main(string[] args)
    {
        Thread worker = new Thread(WorkerLoop) { IsBackground = true, Name = "NinthPillar" };
        worker.Start();
        Application.EnableVisualStyles();
        Application.Run(new StatusForm());
        running = false;          // stop the worker on window close
        worker.Join(1000);
        return 0;
    }

    private static void WorkerLoop()
    {
        Process proc = null;
        IntPtr handle = IntPtr.Zero;
        IntPtr sr1Base = IntPtr.Zero;
        string sr1Path = null;
        long sr1Size = 0;
        string lastStatus = "Waiting for SRX.exe...";

        while (running)
        {
            if (proc == null || proc.HasExited || handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                    handle = IntPtr.Zero;
                }

                proc = FindProcess();
                if (proc != null)
                {
                    handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, proc.Id);
                    sr1Base = FindModuleBase(proc, "sr1.dll", out sr1Path, out sr1Size);
                    if (handle != IntPtr.Zero && sr1Base != IntPtr.Zero)
                    {
                        turboFactorAddress = IntPtr.Zero;
                        realTickAddress = IntPtr.Zero;
                        sfxWindowAddress = IntPtr.Zero;
                        toneHitsAddress = IntPtr.Zero;
                        toneSkipsAddress = IntPtr.Zero;
                        timeMultCave = IntPtr.Zero;
                        toneTableAddress = IntPtr.Zero;
                        overlayCave = IntPtr.Zero;
                        overlayDrawFlag = IntPtr.Zero;
                        overlayBuffer = IntPtr.Zero;
                        overlayInstalled = false;
                        lastOverlayText = "";
                        RestoreTimerWrapper(handle, sr1Base);
                        RestoreOldSoundHooks(handle, sr1Base, sr1Path);
                        ApplyTimeMultHook(handle, sr1Base);
                        ApplySfxLimiterHook(handle, sr1Base, sr1Size);
                        ApplyReaverPatch(handle, sr1Base);
                        ApplyInvulnerablePatch(handle, sr1Base);
                        ApplyFullHealthPatches(handle, sr1Base);
                        ApplyOverlayHook(handle, sr1Base);
                        lastStatus = "Attached to SRX.exe.";
                        uiAttached = true;
                    }
                }
            }

            HandleKeys(handle, sr1Base);

            if (handle != IntPtr.Zero && sr1Base != IntPtr.Zero)
            {
                bool turbo = IsR2Moving();
                bool wasTurbo = lastTurbo;
                lastTurbo = turbo;
                if (turboFactorAddress != IntPtr.Zero)
                {
                    WriteInt(handle, turboFactorAddress, turbo ? TurboFactors[turboIndex] : 1);
                    if (realTickAddress != IntPtr.Zero)
                        WriteInt(handle, realTickAddress, Environment.TickCount);
                    ReadHookCounters(handle);

                    // Proactively clear stuck turbo. The multiply hook only fixes
                    // timeMult on frames the game's time loop runs; a cutscene can
                    // leave it dormant with an inflated value latched. So when not
                    // in turbo: on the release edge force 1x once (covers any
                    // factor), and as an ongoing guard reset any clearly-stuck
                    // (>1x-max) value back to normal.
                    if (!turbo)
                    {
                        IntPtr tmAddr = Add(sr1Base, GameTimeMultRva);
                        if (wasTurbo)
                            WriteInt(handle, tmAddr, NormalTimeMult);
                        else if (ReadInt(handle, tmAddr) > MaxNormalTimeMult)
                            WriteInt(handle, tmAddr, NormalTimeMult);
                    }
                }

                if (infiniteMana)
                    RefillMana(handle, sr1Base);

                if (shiftAnywhere)
                    ApplyShiftAnywhere(handle, sr1Base);

                HandleMenuNav(handle, sr1Base);
                UpdateOverlay(handle);
            }
            else
            {
                uiAttached = false;
            }

            if ((loopCounter++ & 31) == 0)
            {
                UpdateBackgroundMute(proc);
                if (proc != null && !proc.HasExited && proc.MainWindowHandle != IntPtr.Zero)
                    SetWindowText(proc.MainWindowHandle, BuildTitle());
            }

            Thread.Sleep(1);
        }

        // Session mute persists in Windows after the helper exits; never leave
        // the game muted behind us.
        if (gameMuted && mutedPid != 0)
            SetMuteAll(mutedPid, false);

        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    private static void UpdateBackgroundMute(Process proc)
    {
        if (proc == null || proc.HasExited)
        {
            gameMuted = false;
            return;
        }

        bool shouldMute = false;
        if (muteInBackground)
        {
            int fgPid;
            GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            shouldMute = fgPid != proc.Id && fgPid != helperPid;
        }

        if (shouldMute == gameMuted)
            return;

        // The session list is enumerated fresh on every transition (rare:
        // focus changes), so device switches mid-game are handled. Throttle
        // retries for when the session doesn't exist yet.
        int now = Environment.TickCount;
        if (now - lastMuteAttemptTick < 500)
            return;
        lastMuteAttemptTick = now;

        if (SetMuteAll(proc.Id, shouldMute))
        {
            gameMuted = shouldMute;
            mutedPid = proc.Id;
        }
    }

    // The game's session is not necessarily on the default endpoint (observed
    // live: SRX active on a non-default device), and stale sessions can linger
    // on other devices. Walk every active render device and mute every session
    // belonging to the process.
    private static bool SetMuteAll(int pid, bool mute)
    {
        bool any = false;
        // Never operate on pseudo-PIDs (0 = System Idle / system-sounds owner,
        // 4 = System). A real game PID is always well above these; guarding here
        // makes it impossible to mute the shared system-sounds session even if a
        // caller ever passed a bad pid.
        if (pid <= 4)
            return false;
        try
        {
            IMMDeviceEnumerator deviceEnumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            IMMDeviceCollection devices;
            if (deviceEnumerator.EnumAudioEndpoints(0 /*eRender*/, 1 /*DEVICE_STATE_ACTIVE*/, out devices) != 0)
                return false;

            int deviceCount;
            devices.GetCount(out deviceCount);
            for (int d = 0; d < deviceCount; d++)
            {
                IMMDevice device;
                if (devices.Item(d, out device) != 0)
                    continue;

                Guid iidManager = typeof(IAudioSessionManager2).GUID;
                object managerObj;
                if (device.Activate(ref iidManager, 0x17 /*CLSCTX_ALL*/, IntPtr.Zero, out managerObj) != 0)
                    continue;

                IAudioSessionEnumerator sessions;
                if (((IAudioSessionManager2)managerObj).GetSessionEnumerator(out sessions) != 0)
                    continue;

                int count;
                sessions.GetCount(out count);
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl2 session;
                    if (sessions.GetSession(i, out session) != 0)
                        continue;
                    // Only the game's own session: the PID filter below already
                    // excludes the system-sounds session (pid 0) and every other
                    // app, and the pid<=4 guard above backstops a bad pid.
                    int sessionPid;
                    if (session.GetProcessId(out sessionPid) != 0 || sessionPid != pid)
                        continue;
                    try
                    {
                        Guid ctx = Guid.Empty;
                        if (((ISimpleAudioVolume)session).SetMute(mute, ref ctx) == 0)
                            any = true;
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
        return any;
    }

    private static Process FindProcess()
    {
        // Return a candidate-named process that actually has sr1.dll loaded.
        // GetProcessesByName returns instantly (no module walk) when nothing
        // matches, so this stays cheap while the game is not yet running; the
        // module walk only fires for the 1-2 processes named like the game.
        foreach (string name in ProcessNameCandidates)
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                if (HasModule(p, "sr1.dll"))
                    return p;
            }
        }
        return null;
    }

    private static bool HasModule(Process p, string moduleName)
    {
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                if (string.Equals(Path.GetFileName(m.FileName), moduleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static IntPtr FindModuleBase(Process p, string moduleName, out string modulePath, out long moduleSize)
    {
        modulePath = null;
        moduleSize = 0;
        try
        {
            foreach (ProcessModule m in p.Modules)
            {
                if (string.Equals(Path.GetFileName(m.FileName), moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    modulePath = m.FileName;
                    moduleSize = m.ModuleMemorySize;
                    return m.BaseAddress;
                }
            }
        }
        catch
        {
        }
        return IntPtr.Zero;
    }

    // F10 is the only key: open/close the in-game menu. Everything else is
    // navigated with the controller (see HandleMenuNav).
    private static void HandleKeys(IntPtr handle, IntPtr sr1Base)
    {
        if (Pressed(0x79)) // F10
            menuOpen = !menuOpen;
    }

    // Controller navigation of the on-screen menu. D-pad Up/Down moves the
    // highlight; Left/Right (or A) adjusts/toggles the highlighted option.
    // Edge-detected so one press = one step. The game keeps running (no pause).
    private static void HandleMenuNav(IntPtr handle, IntPtr sr1Base)
    {
        if (!menuOpen)
        {
            prevPad = 0;
            return;
        }

        ushort buttons = ReadAllButtons();

        ushort pressed = (ushort)(buttons & ~prevPad);
        prevPad = buttons;

        if ((pressed & PAD_B) != 0) // right face button (B / Circle) closes
        {
            menuOpen = false;
            return;
        }
        if ((pressed & PAD_UP) != 0)
            menuSel = (menuSel + MenuCount - 1) % MenuCount;
        if ((pressed & PAD_DOWN) != 0)
            menuSel = (menuSel + 1) % MenuCount;
        if ((pressed & PAD_LEFT) != 0)
            AdjustSelected(-1, handle, sr1Base);
        if ((pressed & (PAD_RIGHT | PAD_A)) != 0)
            AdjustSelected(+1, handle, sr1Base);
    }

    private static void AdjustSelected(int dir, IntPtr handle, IntPtr sr1Base)
    {
        switch (menuSel)
        {
            case 0:
                turboIndex = Clamp(turboIndex + dir, 0, TurboFactors.Length - 1);
                break;
            case 1:
                sfxRateIndex = Clamp(sfxRateIndex + dir, 0, SfxPerSecond.Length - 1);
                WriteSfxWindow(handle);
                break;
            case 2:
                reaverIndex = Clamp(reaverIndex + dir, 0, ReaverPercents.Length - 1);
                ApplyReaverPatch(handle, sr1Base);
                break;
            case 3:
                invulnerable = !invulnerable;
                ApplyInvulnerablePatch(handle, sr1Base);
                break;
            case 4:
                infiniteMana = !infiniteMana;
                break;
            case 5:
                muteInBackground = !muteInBackground;
                break;
            case 6:
                fullHealthOnSpectral = !fullHealthOnSpectral;
                ApplyFullHealthPatches(handle, sr1Base);
                break;
            case 7:
                fullHealthOnRevive = !fullHealthOnRevive;
                ApplyFullHealthPatches(handle, sr1Base);
                break;
            case 8:
                shiftAnywhere = !shiftAnywhere;
                if (!shiftAnywhere)
                {
                    // Turn off: drop the shift-anytime bit (leave swim alone).
                    int ab = ReadInt(handle, Add(sr1Base, AbilitiesRva));
                    WriteInt(handle, Add(sr1Base, AbilitiesRva), ab & ~0x40);
                }
                break;
            case 9:
                // Die is an action, not a toggle: only fire on activate
                // (Right / A), never on Left, to avoid accidental deaths.
                if (dir > 0)
                    DoDie(handle, sr1Base);
                break;
        }
    }

    // "SHIFT ANY TIME": pin the shift-anytime (0x40) + swim (0x10) ability bits
    // so RAZIEL_OkToShift permits a spectral->material shift with no portal. The
    // player still triggers the shift with the normal plane-shift input.
    private static void ApplyShiftAnywhere(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            return;
        int ab = ReadInt(handle, Add(sr1Base, AbilitiesRva));
        if ((ab & ShiftAnyTimeMask) != ShiftAnyTimeMask)
            WriteInt(handle, Add(sr1Base, AbilitiesRva), ab | ShiftAnyTimeMask);
    }

    // Die: physical -> spectral (drop HitPoints below the material floor so the
    // game's ProcessHealth plane-shifts next frame); spectral -> true death
    // (set streamFlags 0x80000 so the game loop runs UNDERWORLD_StartProcess,
    // i.e. respawn at the Elder God). Clears invincibleTimer so the shift isn't
    // blocked. Anti-softlock escape hatch.
    private static void DoDie(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            return;
        int plane = ReadInt(handle, Add(sr1Base, CurrentPlaneRva));
        if (plane == 2)
        {
            int sf = ReadInt(handle, Add(sr1Base, StreamFlagsRva));
            WriteInt(handle, Add(sr1Base, StreamFlagsRva), sf | UnderworldStreamFlag);
        }
        else if (plane == 1)
        {
            WriteInt(handle, Add(sr1Base, InvincibleTimerRva), 0);
            WriteInt(handle, Add(sr1Base, HitPointsRva), 1);
        }
    }

    private static int Clamp(int v, int lo, int hi)
    {
        return v < lo ? lo : (v > hi ? hi : v);
    }

    private static void WriteSfxWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || sfxWindowAddress == IntPtr.Zero)
            return;
        WriteInt(handle, sfxWindowAddress, 1000 / SfxPerSecond[sfxRateIndex]);
    }

    private static bool Pressed(int vk)
    {
        return (GetAsyncKeyState(vk) & 1) != 0;
    }

    private static bool IsR2Moving()
    {
        // Aggregate every connected pad instead of locking onto the first
        // connected slot. A DualSense via Steam Input / DS4Windows shows up as
        // one or more emulated Xbox pads AND (in native mode) a Windows.Gaming
        // .Input device; the live input may be on slot 1 while slot 0 is an idle
        // phantom. Taking the max R2 / max stick across all sources means turbo
        // fires no matter which controller (or emulated slot) carries the input.
        int maxTrigger;   // 0-255
        int maxStickMag;  // 0-32767
        bool anyPad = ReadAllPads(out maxTrigger, out maxStickMag);

        lastR2 = (byte)maxTrigger;
        lastStickMagnitude = maxStickMag;

        if (!anyPad)
        {
            wasR2Down = false;
            wasStickMoving = false;
            turboArmed = false;
            aimBlocked = false;
            return false;
        }

        bool r2Down = maxTrigger > 40;
        bool stickMoving = maxStickMag > 12000;

        if (!r2Down)
        {
            wasR2Down = false;
            wasStickMoving = stickMoving;
            turboArmed = false;
            aimBlocked = false;
            return false;
        }

        if (!wasR2Down)
        {
            turboArmed = wasStickMoving;
            aimBlocked = !wasStickMoving;
        }

        wasR2Down = true;
        wasStickMoving = stickMoving;
        return turboArmed && !aimBlocked && stickMoving;
    }

    // Fills the max right-trigger (0-255) and max left-stick magnitude (0-32767)
    // seen across all XInput slots and all Windows.Gaming.Input gamepads.
    // Returns false only if no pad was found on any API.
    private static bool ReadAllPads(out int maxTrigger, out int maxStickMag)
    {
        maxTrigger = 0;
        maxStickMag = 0;
        bool any = false;

        for (int i = 0; i < 4; i++)
        {
            XINPUT_STATE state;
            if (TryXInput(i, out state) == 0)
            {
                any = true;
                if (state.Gamepad.bRightTrigger > maxTrigger)
                    maxTrigger = state.Gamepad.bRightTrigger;
                int lx = state.Gamepad.sThumbLX;
                int ly = state.Gamepad.sThumbLY;
                int mag = (int)Math.Sqrt((double)lx * lx + (double)ly * ly);
                if (mag > maxStickMag)
                    maxStickMag = mag;
            }
        }

        // Windows.Gaming.Input covers DualSense/DualShock running in native mode
        // (no XInput emulation). Defensive: never let a WGI hiccup break turbo.
        try
        {
            var pads = Gamepad.Gamepads;
            for (int i = 0; i < pads.Count; i++)
            {
                GamepadReading r = pads[i].GetCurrentReading();
                any = true;
                int trig = (int)(r.RightTrigger * 255.0);
                if (trig > maxTrigger)
                    maxTrigger = trig;
                double m = Math.Sqrt(r.LeftThumbstickX * r.LeftThumbstickX +
                                     r.LeftThumbstickY * r.LeftThumbstickY);
                int mag = (int)(m * 32767.0);
                if (mag > maxStickMag)
                    maxStickMag = mag;
            }
        }
        catch
        {
        }

        return any;
    }

    private static int TryXInput(int i, out XINPUT_STATE state)
    {
        try { return XInputGetState14(i, out state); }
        catch { return XInputGetState910(i, out state); }
    }

    // OR the button bits across every connected pad (XInput slots + WGI), so
    // menu navigation works no matter which controller/slot is live. WGI buttons
    // are mapped into the XInput bit layout used by the PAD_* constants.
    private static ushort ReadAllButtons()
    {
        ushort buttons = 0;
        XINPUT_STATE st;
        for (int i = 0; i < 4; i++)
        {
            if (TryXInput(i, out st) == 0)
                buttons |= st.Gamepad.wButtons;
        }

        try
        {
            var pads = Gamepad.Gamepads;
            for (int i = 0; i < pads.Count; i++)
            {
                GamepadButtons b = pads[i].GetCurrentReading().Buttons;
                if ((b & GamepadButtons.DPadUp) != 0) buttons |= PAD_UP;
                if ((b & GamepadButtons.DPadDown) != 0) buttons |= PAD_DOWN;
                if ((b & GamepadButtons.DPadLeft) != 0) buttons |= PAD_LEFT;
                if ((b & GamepadButtons.DPadRight) != 0) buttons |= PAD_RIGHT;
                if ((b & GamepadButtons.A) != 0) buttons |= PAD_A;
                if ((b & GamepadButtons.B) != 0) buttons |= PAD_B;
            }
        }
        catch
        {
        }

        return buttons;
    }

    // Invulnerable = `ret` at the entry of LoseHealth and DrainHealth; every
    // combat and environmental damage path goes through them. Instant-death
    // (abyss, crushers) is inlined elsewhere and intentionally left alive.
    private static void ApplyInvulnerablePatch(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            return;

        byte b = invulnerable ? (byte)0xC3 : HealthFuncFirstByte;
        WriteCode(handle, Add(sr1Base, LoseHealthRva), new byte[] { b });
        WriteCode(handle, Add(sr1Base, DrainHealthRva), new byte[] { b });
    }

    // Pin GlyphManaBalls to GlyphManaMax. Runs every helper loop while the
    // toggle is on; out-paces any DrainMana by orders of magnitude.
    private static void RefillMana(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            return;

        byte[] max = new byte[2];
        IntPtr read;
        if (!ReadProcessMemory(handle, Add(sr1Base, GlyphManaMaxRva), max, 2, out read) || read.ToInt64() != 2)
            return;
        if (max[0] == 0 && max[1] == 0)
            return; // no glyph energy capacity yet
        WriteBytes(handle, Add(sr1Base, GlyphManaBallsRva), max);
    }

    private static void ApplyReaverPatch(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            return;

        int percent = ReaverPercents[reaverIndex];
        int multiplier = percent == 0 ? 0 : (100000 * percent) / 100;

        WriteBytes(handle, Add(sr1Base, ReaverBranchRva), new byte[] { 0x7D }); // jge active path
        WriteInt(handle, Add(sr1Base, ReaverMaterialImmRva), multiplier);
    }

    private static void ApplyFullHealthPatches(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            return;

        WriteCode(handle, Add(sr1Base, SpectralShiftHealthRva),
            BitConverter.GetBytes(fullHealthOnSpectral ? FullHealth : SpectralShiftDefaultHealth));

        int reviveHealth = fullHealthOnRevive ? FullHealth : ReviveDefaultHealth;
        WriteCode(handle, Add(sr1Base, ReviveHealthRva), BitConverter.GetBytes(reviveHealth));
        WriteCode(handle, Add(sr1Base, InstantDeathHealthRva), BitConverter.GetBytes(reviveHealth));
    }

    private static void RestoreTimerWrapper(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero)
            return;

        byte[] original = {
            0x48, 0x8B, 0x05, 0xA9, 0x83, 0xC2, 0x05,
            0xFF, 0xA0, 0xA8, 0x00, 0x00, 0x00, 0xCC
        };
        WriteCode(handle, Add(sr1Base, TimerGetMillisecondsRva), original);
    }

    // Earlier debugging sessions patched jumps/calls into these sites. If the
    // helper restarts against a game that still has them, put the original
    // code back (sourced from the on-disk backup DLL, .text is unmodified there).
    private static void RestoreOldSoundHooks(IntPtr handle, IntPtr sr1Base, string sr1Path)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero || sr1Path == null)
            return;

        string dir = Path.GetDirectoryName(sr1Path);
        string bak = null;
        try
        {
            string[] candidates = Directory.GetFiles(dir, "sr1.dll.bak-*");
            if (candidates.Length > 0)
                bak = candidates[0];
        }
        catch
        {
        }
        if (bak == null)
            return;

        byte[] file;
        try { file = File.ReadAllBytes(bak); }
        catch { return; }

        RestoreFromFile(handle, sr1Base, file, OldSndPlayVolPanRva, 14);
        RestoreFromFile(handle, sr1Base, file, OldSoundPlay3dRva, 14);
        foreach (int rva in OldCallSiteRvas)
            RestoreFromFile(handle, sr1Base, file, rva, 5);
    }

    private static void RestoreFromFile(IntPtr handle, IntPtr sr1Base, byte[] file, int rva, int length)
    {
        // .text section: RVA 0x1000 maps to file offset 0x400.
        int fileOffset = rva - 0x1000 + 0x400;
        if (fileOffset < 0 || fileOffset + length > file.Length)
            return;
        byte[] original = new byte[length];
        Array.Copy(file, fileOffset, original, 0, length);
        WriteCode(handle, Add(sr1Base, rva), original);
    }

    private static void ApplyTimeMultHook(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero || timeMultCave != IntPtr.Zero)
            return;

        timeMultCave = AllocNear(handle, sr1Base.ToInt64(), 0x1000);
        if (timeMultCave == IntPtr.Zero)
            return;

        long cave = timeMultCave.ToInt64();
        long factor = cave + 0x200;
        turboFactorAddress = new IntPtr(factor);

        byte[] code = BuildTimeMultHookCode(sr1Base.ToInt64() + GameTimeMultRva, factor);
        WriteBytes(handle, timeMultCave, code);
        WriteInt(handle, turboFactorAddress, 1);

        PatchCall6(handle, Add(sr1Base, TimeMultWrite1Rva), cave);
        PatchCall6(handle, Add(sr1Base, TimeMultWrite2Rva), cave);
    }

    // In-game overlay. Replace DEBUG_DisplayStatus (drawn every frame, only ever
    // renders debug text we don't want) with a cave that FONT_Prints our menu
    // buffer and returns. The helper fills the buffer + a draw flag each loop.
    private static void ApplyOverlayHook(IntPtr handle, IntPtr sr1Base)
    {
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero || overlayInstalled)
            return;

        overlayCave = AllocNear(handle, sr1Base.ToInt64(), 0x1000);
        if (overlayCave == IntPtr.Zero)
            return;

        long cave = overlayCave.ToInt64();
        overlayDrawFlag = new IntPtr(cave + 0x100);
        overlayBuffer = new IntPtr(cave + 0x110);
        long fontPrint = sr1Base.ToInt64() + FontPrintRva;

        byte[] code = BuildOverlayCaveCode(fontPrint, overlayDrawFlag.ToInt64(), overlayBuffer.ToInt64());
        WriteBytes(handle, overlayCave, code);
        WriteBytes(handle, overlayDrawFlag, new byte[] { 0 });
        WriteBytes(handle, overlayBuffer, new byte[] { 0 });

        // jmp cave at DEBUG_DisplayStatus entry (E9 rel32). We never return into
        // the original, so no stolen-byte trampoline is needed.
        IntPtr entry = Add(sr1Base, DebugDisplayStatusRva);
        long rel = cave - (entry.ToInt64() + 5);
        byte[] jmp = new byte[5];
        jmp[0] = 0xE9;
        Array.Copy(BitConverter.GetBytes((int)rel), 0, jmp, 1, 4);
        WriteCode(handle, entry, jmp);
        overlayInstalled = true;
    }

    private static byte[] BuildOverlayCaveCode(long fontPrintVa, long drawFlagAddr, long bufferAddr)
    {
        MemoryStream s = new MemoryStream();
        EmitMovRaxImm64(s, drawFlagAddr);            // mov rax, &drawFlag
        Emit(s, 0x80, 0x38, 0x00);                   // cmp byte [rax], 0
        long jeSkip = EmitJump8(s, 0x74);            // je skip
        Emit(s, 0x48, 0x83, 0xEC, 0x28);             // sub rsp, 0x28  (shadow + align)
        Emit(s, 0x48, 0xB9);                         // mov rcx, imm64
        byte[] b = BitConverter.GetBytes(bufferAddr); s.Write(b, 0, b.Length);
        EmitMovRaxImm64(s, fontPrintVa);             // mov rax, FONT_Print
        Emit(s, 0xFF, 0xD0);                         // call rax
        Emit(s, 0x48, 0x83, 0xC4, 0x28);             // add rsp, 0x28
        long skip = s.Position;
        PatchJump8(s, jeSkip, skip);
        Emit(s, 0xC3);                               // ret
        return s.ToArray();
    }

    private static void UpdateOverlay(IntPtr handle)
    {
        if (!overlayInstalled)
            return;
        WriteBytes(handle, overlayDrawFlag, new byte[] { (byte)(menuOpen ? 1 : 0) });
        if (!menuOpen)
            return;
        string text = BuildOverlayText();
        if (text == lastOverlayText)
            return;
        lastOverlayText = text;
        byte[] ascii = Encoding.ASCII.GetBytes(text);
        byte[] buf = new byte[ascii.Length + 1]; // null-terminated
        Array.Copy(ascii, buf, ascii.Length);
        WriteBytes(handle, overlayBuffer, buf);
    }

    // FONT_Print is printf-style: "$@EF\n" is a position control code (proven to
    // render), and a literal % must be doubled to %%.
    private static string BuildOverlayText()
    {
        string rv = ReaverPercents[reaverIndex] == 0 ? "Always" : ReaverPercents[reaverIndex] + "%%";
        string[] lines =
        {
            "Turbo       " + TurboFactors[turboIndex] + "x",
            "Footsteps   " + SfxPerSecond[sfxRateIndex] + "/s",
            "Reaver      " + rv,
            "Invuln      " + (invulnerable ? "ON" : "off"),
            "Mana        " + (infiniteMana ? "ON" : "off"),
            "Bg sound    " + (muteInBackground ? "muted" : "on"),
            "Spectral HP " + (fullHealthOnSpectral ? "full" : "base"),
            "Revive HP   " + (fullHealthOnRevive ? "full" : "base"),
            "Shift Any   " + (shiftAnywhere ? "ON" : "off"),
            "Die         (A)",
        };
        StringBuilder sb = new StringBuilder();
        sb.Append("$@EF\n");
        sb.Append("   == NINTH PILLAR ==\n");
        for (int i = 0; i < MenuCount; i++)
            sb.Append((i == menuSel ? " > " : "   ") + lines[i] + "\n");
        sb.Append("   D-pad move/adjust   (B/O) or F10 close\n");
        return sb.ToString();
    }

    // Replaces sfxCmdFunction[0] (data pointer, no code patch) with a cave that
    // rate-limits each toneID to one play per FootstepLimitMs during turbo,
    // then tail-jumps to the real sfxCmdPlayTone.
    private static void ApplySfxLimiterHook(IntPtr handle, IntPtr sr1Base, long sr1Size)
    {
        sfxHookStatus = "not installed";
        if (handle == IntPtr.Zero || sr1Base == IntPtr.Zero || timeMultCave == IntPtr.Zero || turboFactorAddress == IntPtr.Zero)
            return;

        long baseVa = sr1Base.ToInt64();
        IntPtr entryAddress = Add(sr1Base, SfxCmdTableEntry0Rva);
        long expected = baseVa + SfxCmdPlayToneRva;

        long current = ReadLong(handle, entryAddress);
        bool insideModule = current >= baseVa && current < baseVa + sr1Size;
        if (current != expected && insideModule)
        {
            // Points at some other code in sr1.dll: offsets don't match this
            // binary. Do not touch.
            sfxHookStatus = "MISMATCH (table entry 0x" + current.ToString("X") + ")";
            return;
        }

        toneTableAddress = VirtualAllocEx(handle, IntPtr.Zero, new UIntPtr(0x40000u), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (toneTableAddress == IntPtr.Zero)
        {
            sfxHookStatus = "tone table alloc failed";
            return;
        }

        long realTick = timeMultCave.ToInt64() + 0x600;
        long tones = timeMultCave.ToInt64() + 0x610;
        long skips = timeMultCave.ToInt64() + 0x61C;
        long cave = timeMultCave.ToInt64() + 0xB00;
        realTickAddress = new IntPtr(realTick);
        sfxWindowAddress = new IntPtr(realTick + 4); // cave reads window from [realTick+4]
        toneHitsAddress = new IntPtr(tones);
        toneSkipsAddress = new IntPtr(skips);

        WriteInt(handle, realTickAddress, Environment.TickCount);
        WriteSfxWindow(handle);
        WriteInt(handle, toneHitsAddress, 0);
        WriteInt(handle, toneSkipsAddress, 0);

        byte[] code = BuildPlayToneLimiterCode(
            turboFactorAddress.ToInt64(),
            realTick,
            toneTableAddress.ToInt64(),
            expected,
            tones,
            skips);
        WriteBytes(handle, new IntPtr(cave), code);

        byte[] pointer = BitConverter.GetBytes(cave);
        uint oldProtect;
        VirtualProtectEx(handle, entryAddress, new UIntPtr(8u), PAGE_READWRITE, out oldProtect);
        WriteBytes(handle, entryAddress, pointer);
        if (oldProtect != 0)
            VirtualProtectEx(handle, entryAddress, new UIntPtr(8u), oldProtect, out oldProtect);

        sfxHookStatus = "installed";
    }

    private static byte[] BuildTimeMultHookCode(long timeMultAddress, long factorAddress)
    {
        MemoryStream s = new MemoryStream();
        Emit(s, 0x50);                                             // push rax
        Emit(s, 0x52);                                             // push rdx
        EmitMovRdxImm64(s, factorAddress);
        Emit(s, 0x0F, 0xAF, 0x02);                                 // imul eax, [rdx]
        EmitMovRdxImm64(s, timeMultAddress);
        Emit(s, 0x89, 0x02);                                       // mov [rdx], eax
        Emit(s, 0x5A);                                             // pop rdx
        Emit(s, 0x58);                                             // pop rax
        Emit(s, 0xC3);                                             // ret
        return s.ToArray();
    }

    // Entered via `jmp [sfxCmdFunction + rax*8]` from aadExecuteSfxCommand:
    // rcx = AadSfxCommand* (must be preserved for sfxCmdPlayTone), the return
    // address on the stack belongs to the dispatcher's caller, so a plain ret
    // skips the play. word [rcx+8] is the toneID.
    private static byte[] BuildPlayToneLimiterCode(long factorAddress, long realTickAddress, long toneTableAddress, long playToneVa, long hitCounterAddress, long skipCounterAddress)
    {
        MemoryStream s = new MemoryStream();

        EmitIncDwordPtrR11(s, hitCounterAddress);
        EmitMovRaxImm64(s, factorAddress);
        Emit(s, 0x83, 0x38, 0x01);                                 // cmp dword [rax], 1
        long jlePass = EmitJump8(s, 0x7E);                          // jle pass

        Emit(s, 0x0F, 0xB7, 0x51, 0x08);                           // movzx edx, word [rcx+8]  (toneID)
        EmitMovRaxImm64(s, toneTableAddress);
        Emit(s, 0x48, 0x8D, 0x14, 0x90);                           // lea rdx, [rax+rdx*4]     (slot for this toneID)
        EmitMovRaxImm64(s, realTickAddress);
        Emit(s, 0x44, 0x8B, 0x00);                                 // mov r8d, [rax]           (wall-clock ms)
        Emit(s, 0x45, 0x8B, 0xC8);                                 // mov r9d, r8d
        Emit(s, 0x44, 0x2B, 0x0A);                                 // sub r9d, [rdx]
        Emit(s, 0x44, 0x3B, 0x48, 0x04);                           // cmp r9d, [rax+4]         (limit window ms, live-tunable)
        long jbSkip = EmitJump8(s, 0x72);                           // jb skip
        Emit(s, 0x44, 0x89, 0x02);                                 // mov [rdx], r8d           (record play time)

        long pass = s.Position;
        PatchJump8(s, jlePass, pass);
        EmitMovRaxImm64(s, playToneVa);
        Emit(s, 0xFF, 0xE0);                                       // jmp rax

        long skip = s.Position;
        PatchJump8(s, jbSkip, skip);
        EmitIncDwordPtrR11(s, skipCounterAddress);
        Emit(s, 0xC3);                                             // ret

        return s.ToArray();
    }

    private static void PatchCall6(IntPtr handle, IntPtr source, long target)
    {
        long next = source.ToInt64() + 5;
        int rel = checked((int)(target - next));
        byte[] patch = new byte[] { 0xE8, 0, 0, 0, 0, 0x90 };
        byte[] relBytes = BitConverter.GetBytes(rel);
        Array.Copy(relBytes, 0, patch, 1, 4);
        WriteCode(handle, source, patch);
    }

    private static void WriteCode(IntPtr handle, IntPtr address, byte[] bytes)
    {
        uint oldProtect;
        VirtualProtectEx(handle, address, new UIntPtr((uint)bytes.Length), PAGE_EXECUTE_READWRITE, out oldProtect);
        WriteBytes(handle, address, bytes);
        if (oldProtect != 0)
            VirtualProtectEx(handle, address, new UIntPtr((uint)bytes.Length), oldProtect, out oldProtect);
    }

    private static IntPtr AllocNear(IntPtr handle, long nearAddress, int size)
    {
        const long Step = 0x10000;
        const long Range = 0x70000000;

        for (long delta = 0; delta < Range; delta += Step)
        {
            long down = nearAddress - delta;
            if (down > 0x10000)
            {
                IntPtr p = VirtualAllocEx(handle, new IntPtr(down), new UIntPtr((uint)size), MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                if (p != IntPtr.Zero)
                    return p;
            }

            long up = nearAddress + delta;
            if (up > 0)
            {
                IntPtr p = VirtualAllocEx(handle, new IntPtr(up), new UIntPtr((uint)size), MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                if (p != IntPtr.Zero)
                    return p;
            }
        }

        return IntPtr.Zero;
    }

    private static void EmitMovRdxImm64(Stream s, long value)
    {
        Emit(s, 0x48, 0xBA);
        byte[] b = BitConverter.GetBytes(value);
        s.Write(b, 0, b.Length);
    }

    private static void EmitIncDwordPtrR11(Stream s, long address)
    {
        Emit(s, 0x49, 0xBB);
        byte[] b = BitConverter.GetBytes(address);
        s.Write(b, 0, b.Length);
        Emit(s, 0x41, 0xFF, 0x03);                                 // inc dword [r11]
    }

    private static void EmitMovRaxImm64(Stream s, long value)
    {
        Emit(s, 0x48, 0xB8);
        byte[] b = BitConverter.GetBytes(value);
        s.Write(b, 0, b.Length);
    }

    private static void Emit(Stream s, params byte[] bytes)
    {
        s.Write(bytes, 0, bytes.Length);
    }

    private static long EmitJump8(MemoryStream s, byte opcode)
    {
        s.WriteByte(opcode);
        long operandPosition = s.Position;
        s.WriteByte(0);
        return operandPosition;
    }

    private static void PatchJump8(MemoryStream s, long operandPosition, long targetPosition)
    {
        long saved = s.Position;
        int relative = (int)(targetPosition - (operandPosition + 1));
        if (relative < -128 || relative > 127)
            throw new InvalidOperationException("Generated hook jump is out of range.");

        s.Position = operandPosition;
        s.WriteByte((byte)(relative & 0xFF));
        s.Position = saved;
    }

    // Small status window replacing the old console. A timer polls the shared
    // worker state; the worker never touches these controls directly.
    private sealed class StatusForm : Form
    {
        private readonly Label status = new Label();
        private readonly Label detail = new Label();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public StatusForm()
        {
            Text = "Ninth Pillar";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 264);

            status.SetBounds(16, 14, 368, 28);
            status.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            Controls.Add(status);

            detail.SetBounds(16, 50, 372, 168);
            detail.Font = new Font("Segoe UI", 9.5f);
            Controls.Add(detail);

            Button close = new Button { Text = "Close" };
            close.SetBounds(294, 228, 90, 28);
            close.Click += (s, e) => Close();
            Controls.Add(close);

            timer.Interval = 300;
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Tick();
        }

        private void Tick()
        {
            if (uiAttached)
            {
                status.Text = "Soul Reaver: FOUND";
                status.ForeColor = Color.ForestGreen;
            }
            else
            {
                status.Text = "Soul Reaver: searching... (launch the game)";
                status.ForeColor = Color.DarkOrange;
            }

            string rv = ReaverPercents[reaverIndex] == 0 ? "Always" : ReaverPercents[reaverIndex] + "%";
            detail.Text =
                "Press F10 in-game to open/close the mod menu.\n" +
                "With it open, use the controller D-pad:\n" +
                "   Up/Down = move    Left/Right (or A) = adjust/toggle\n\n" +
                "Turbo:  push the left stick to move, then hold R2.\n\n" +
                "Now:  Turbo " + TurboFactors[turboIndex] + "x - Footsteps " + SfxPerSecond[sfxRateIndex] +
                "/s - Reaver " + rv + "\n" +
                "          Invuln " + (invulnerable ? "ON" : "off") + " - Mana " + (infiniteMana ? "ON" : "off") +
                " - SpectralHP " + (fullHealthOnSpectral ? "full" : "base") +
                " - ReviveHP " + (fullHealthOnRevive ? "full" : "base") +
                (menuOpen ? "   [MENU OPEN]" : "") + "\n\n" +
                "Keep this window open while playing. Close to exit the mod.";
        }
    }

    private static string BuildTitle()
    {
        string rv = ReaverPercents[reaverIndex] == 0 ? "Always" : ReaverPercents[reaverIndex] + "%";
        return "Legacy of Kain Soul Reaver | Ninth Pillar " +
            (lastTurbo ? "TURBO " : "") +
            TurboFactors[turboIndex] + "x Reaver " + rv +
            " R2=" + lastR2 +
            " Stick=" + lastStickMagnitude +
            " Sfx=" + toneHits + "/" + toneSkips + "@" + SfxPerSecond[sfxRateIndex] + "/s" +
            (invulnerable ? " INVULN" : "") +
            (infiniteMana ? " MANA" : "") +
            (fullHealthOnSpectral ? " SPECTRALHP" : "") +
            (fullHealthOnRevive ? " REVIVEHP" : "") +
            (gameMuted ? " MUTED" : "") +
            (aimBlocked ? " AIM" : "") +
            (menuOpen ? " MENU" : "");
    }

    private static IntPtr Add(IntPtr ptr, int offset)
    {
        return new IntPtr(ptr.ToInt64() + offset);
    }

    private static void WriteInt(IntPtr handle, IntPtr address, int value)
    {
        WriteBytes(handle, address, BitConverter.GetBytes(value));
    }

    private static int ReadInt(IntPtr handle, IntPtr address)
    {
        if (handle == IntPtr.Zero || address == IntPtr.Zero)
            return 0;
        byte[] buffer = new byte[4];
        IntPtr read;
        if (!ReadProcessMemory(handle, address, buffer, buffer.Length, out read) || read.ToInt64() != buffer.Length)
            return 0;
        return BitConverter.ToInt32(buffer, 0);
    }

    private static long ReadLong(IntPtr handle, IntPtr address)
    {
        if (handle == IntPtr.Zero || address == IntPtr.Zero)
            return 0;
        byte[] buffer = new byte[8];
        IntPtr read;
        if (!ReadProcessMemory(handle, address, buffer, buffer.Length, out read) || read.ToInt64() != buffer.Length)
            return 0;
        return BitConverter.ToInt64(buffer, 0);
    }

    private static void ReadHookCounters(IntPtr handle)
    {
        toneHits = ReadInt(handle, toneHitsAddress);
        toneSkips = ReadInt(handle, toneSkipsAddress);
    }

    private static void WriteBytes(IntPtr handle, IntPtr address, byte[] bytes)
    {
        IntPtr written;
        WriteProcessMemory(handle, address, bytes, bytes.Length, out written);
    }
}
