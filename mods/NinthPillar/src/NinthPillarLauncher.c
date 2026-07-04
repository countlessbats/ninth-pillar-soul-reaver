// Ninth Pillar native launcher stub.
//
// Replaces the game's SRX.exe. Starts the NinthPillar helper, then launches the
// real (renamed) game as a child, waits for it to exit, and stops the helper.
//
// Why native (not the earlier C# stub): Steam injects gameoverlayrenderer64.dll
// into whatever exe it launches. Injecting into a managed (.NET) launcher during
// CLR startup deadlocks it (observed 2026-07-04: the C# stub hung with 3 threads,
// Main never ran, game never started). A native stub takes the overlay fine, the
// same way every native game exe does.
//
// Build: mods\NinthPillar\buildlauncher.ps1

#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#include <windows.h>
#include <shlwapi.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <wbemidl.h>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "wbemuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")

static const wchar_t* kRealExeName   = L"SRX.ninthpillar.original.exe";
static const wchar_t* kHelperRelPath = L"mods\\NinthPillar\\NinthPillar.exe";
static const wchar_t* kHelperExeName = L"NinthPillar.exe";

// TerminateProcess every running copy of the helper. Called on game exit so the
// helper never lingers, regardless of how many instances exist.
static void StopAllHelpers(void)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE)
        return;

    PROCESSENTRY32W pe = { sizeof(pe) };
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            if (_wcsicmp(pe.szExeFile, kHelperExeName) == 0)
            {
                HANDLE h = OpenProcess(PROCESS_TERMINATE, FALSE, pe.th32ProcessID);
                if (h)
                {
                    TerminateProcess(h, 0);
                    CloseHandle(h);
                }
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
}

// Spawn the helper via WMI (Win32_Process.Create). The new process is created
// by WmiPrvSE.exe, NOT as a child of this (Steam-overlay-injected) launcher, so
// gameoverlayrenderer64.dll is never injected into it. That matters because the
// helper is a managed (.NET) exe, and the Steam overlay deadlocks .NET processes
// during CLR startup (same failure that killed the old .NET launcher stub). A
// plain CreateProcess here would put the helper back inside the injected tree
// and hang it. Verified 2026-07-04: WMI-spawned helper attaches and hooks; a
// child-spawned helper hangs at ~4 threads / 0 CPU.
static BOOL SpawnHelperViaWmi(const wchar_t* commandLine, const wchar_t* currentDir)
{
    BOOL ok = FALSE;
    BOOL didInit = FALSE;
    IWbemLocator* loc = NULL;
    IWbemServices* svc = NULL;
    IWbemClassObject* cls = NULL;
    IWbemClassObject* inDef = NULL;
    IWbemClassObject* in = NULL;
    IWbemClassObject* out = NULL;
    BSTR ns = NULL, clsName = NULL, method = NULL;
    HRESULT hr;

    hr = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    if (SUCCEEDED(hr))
        didInit = TRUE;

    hr = CoCreateInstance(&CLSID_WbemLocator, NULL, CLSCTX_INPROC_SERVER,
                          &IID_IWbemLocator, (void**)&loc);
    if (FAILED(hr) || !loc)
        goto done;

    ns = SysAllocString(L"ROOT\\CIMV2");
    hr = IWbemLocator_ConnectServer(loc, ns, NULL, NULL, NULL, 0, NULL, NULL, &svc);
    if (FAILED(hr) || !svc)
        goto done;

    hr = CoSetProxyBlanket((IUnknown*)svc, RPC_C_AUTHN_WINNT, RPC_C_AUTHZ_NONE, NULL,
                           RPC_C_AUTHN_LEVEL_CALL, RPC_C_IMP_LEVEL_IMPERSONATE,
                           NULL, EOAC_NONE);
    if (FAILED(hr))
        goto done;

    clsName = SysAllocString(L"Win32_Process");
    hr = IWbemServices_GetObject(svc, clsName, 0, NULL, &cls, NULL);
    if (FAILED(hr) || !cls)
        goto done;

    method = SysAllocString(L"Create");
    hr = IWbemClassObject_GetMethod(cls, method, 0, &inDef, NULL);
    if (FAILED(hr) || !inDef)
        goto done;

    hr = IWbemClassObject_SpawnInstance(inDef, 0, &in);
    if (FAILED(hr) || !in)
        goto done;

    {
        VARIANT v;
        VariantInit(&v);
        v.vt = VT_BSTR;
        v.bstrVal = SysAllocString(commandLine);
        IWbemClassObject_Put(in, L"CommandLine", 0, &v, 0);
        VariantClear(&v);

        VariantInit(&v);
        v.vt = VT_BSTR;
        v.bstrVal = SysAllocString(currentDir);
        IWbemClassObject_Put(in, L"CurrentDirectory", 0, &v, 0);
        VariantClear(&v);
    }

    hr = IWbemServices_ExecMethod(svc, clsName, method, 0, NULL, in, &out, NULL);
    if (SUCCEEDED(hr))
        ok = TRUE;

done:
    if (out) IWbemClassObject_Release(out);
    if (in) IWbemClassObject_Release(in);
    if (inDef) IWbemClassObject_Release(inDef);
    if (cls) IWbemClassObject_Release(cls);
    if (svc) IWbemServices_Release(svc);
    if (loc) IWbemLocator_Release(loc);
    if (ns) SysFreeString(ns);
    if (clsName) SysFreeString(clsName);
    if (method) SysFreeString(method);
    if (didInit) CoUninitialize();
    return ok;
}

static BOOL HelperAlreadyRunning(void)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE)
        return FALSE;

    BOOL found = FALSE;
    PROCESSENTRY32W pe = { sizeof(pe) };
    if (Process32FirstW(snap, &pe))
    {
        do
        {
            if (_wcsicmp(pe.szExeFile, kHelperExeName) == 0)
            {
                found = TRUE;
                break;
            }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return found;
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, LPWSTR lpCmdLine, int nShow)
{
    (void)hInst; (void)hPrev; (void)nShow;

    wchar_t dir[MAX_PATH];
    GetModuleFileNameW(NULL, dir, MAX_PATH);
    PathRemoveFileSpecW(dir);

    wchar_t realExe[MAX_PATH];
    wcscpy_s(realExe, MAX_PATH, dir);
    PathAppendW(realExe, kRealExeName);

    if (!PathFileExistsW(realExe))
    {
        MessageBoxW(NULL,
            L"Ninth Pillar is installed, but the original game executable was not found:\n\n"
            L"SRX.ninthpillar.original.exe\n\nRun install_or_remove again to repair or uninstall.",
            L"Ninth Pillar Launcher", MB_OK | MB_ICONERROR);
        return 2;
    }

    // Start the helper unless one is already running. Spawn via WMI so it lands
    // outside the Steam overlay's injection tree (see SpawnHelperViaWmi).
    wchar_t helperExe[MAX_PATH];
    wcscpy_s(helperExe, MAX_PATH, dir);
    PathAppendW(helperExe, kHelperRelPath);
    if (PathFileExistsW(helperExe) && !HelperAlreadyRunning())
    {
        wchar_t helperDir[MAX_PATH];
        wcscpy_s(helperDir, MAX_PATH, helperExe);
        PathRemoveFileSpecW(helperDir);

        wchar_t helperCmd[MAX_PATH + 4];
        swprintf_s(helperCmd, MAX_PATH + 4, L"\"%s\"", helperExe);

        SpawnHelperViaWmi(helperCmd, helperDir);
    }

    // Launch the real game as a child (inherits environment, incl. Steam's
    // SteamAppId, and the working directory).
    wchar_t cmd[32768];
    if (lpCmdLine && lpCmdLine[0])
        swprintf_s(cmd, 32768, L"\"%s\" %s", realExe, lpCmdLine);
    else
        swprintf_s(cmd, 32768, L"\"%s\"", realExe);

    int rc = 0;
    STARTUPINFOW gsi = { sizeof(gsi) };
    PROCESS_INFORMATION gpi;
    ZeroMemory(&gpi, sizeof(gpi));
    if (CreateProcessW(NULL, cmd, NULL, NULL, FALSE, 0, NULL, dir, &gsi, &gpi))
    {
        WaitForSingleObject(gpi.hProcess, INFINITE);
        DWORD code = 0;
        GetExitCodeProcess(gpi.hProcess, &code);
        rc = (int)code;
        CloseHandle(gpi.hThread);
        CloseHandle(gpi.hProcess);
    }
    else
    {
        MessageBoxW(NULL, L"Could not launch Soul Reaver.",
            L"Ninth Pillar Launcher", MB_OK | MB_ICONERROR);
        rc = 4;
    }

    StopAllHelpers();
    return rc;
}
