$ErrorActionPreference = 'Stop'

$RealExeName = 'SRX.ninthpillar.original.exe'
$LauncherName = 'NinthPillarLauncher.exe'
$MarkerName = 'installed.marker'
$SteamAppId = '2521380'

function Get-ParentDir([string] $path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    return [IO.Path]::GetDirectoryName($path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
}

function Normalize-PastedPath([string] $raw) {
    if ($null -eq $raw) { return $null }
    $s = $raw.Trim()

    # Drag/drop and copy/paste often include wrapping quotes. Strip only one
    # matching outer pair; keep all internal characters literal.
    if ($s.Length -ge 2) {
        if (($s[0] -eq '"' -and $s[$s.Length - 1] -eq '"') -or
            ($s[0] -eq "'" -and $s[$s.Length - 1] -eq "'")) {
            $s = $s.Substring(1, $s.Length - 2)
        }
    }

    if ($s.StartsWith('file:///', [StringComparison]::OrdinalIgnoreCase)) {
        try { $s = ([Uri]$s).LocalPath } catch {}
    }

    return $s
}

function Test-GameDir([string] $dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir '1\sr1.dll')) -and
           ((Test-Path -LiteralPath (Join-Path $dir 'SRX.exe')) -or
            (Test-Path -LiteralPath (Join-Path $dir $RealExeName)))
}

function Resolve-GameDir([string] $candidate) {
    # Never throws: any bad input just yields $null so the caller can keep asking.
    try {
        $p = Normalize-PastedPath $candidate
        if ([string]::IsNullOrWhiteSpace($p)) { return $null }

        # If they handed us a FILE (SRX.exe, the backup exe, install_or_remove.*,
        # or anything else inside the folder), use its containing folder.
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            $p = Get-ParentDir $p
        }
        if ([string]::IsNullOrWhiteSpace($p)) { return $null }

        # -LiteralPath throughout: spaces, parentheses and [] are all taken
        # literally (no globbing), so those paths work fine.
        $resolved = Resolve-Path -LiteralPath $p -ErrorAction Stop
        $p = $resolved.ProviderPath

        if (Test-GameDir $p) { return $p }
        return $null
    } catch {
        return $null
    }
}

function Find-GameDir {
    # Build the auto-detect list defensively so a missing env var (e.g. a null
    # ProgramFiles(x86)) can never throw before we get a chance to prompt.
    $scriptDir = Get-ParentDir $PSCommandPath
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($scriptDir) { $candidates.Add($scriptDir) }
    try { $candidates.Add((Get-Location).ProviderPath) } catch {}
    $parent = Get-ParentDir $scriptDir
    if ($parent) { $candidates.Add($parent) }
    foreach ($pf in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($pf) { $candidates.Add((Join-Path $pf 'Steam\steamapps\common\Soul Reaver I-II')) }
    }

    foreach ($candidate in $candidates) {
        $found = Resolve-GameDir $candidate
        if ($found) { return $found }
    }

    while ($true) {
        Write-Host ''
        Write-Host 'Could not find Soul Reaver I-II automatically.'
        Write-Host 'Paste the game folder path (or the path to SRX.exe). Quotes are OK;'
        Write-Host 'spaces and parentheses are fine. Type Q to cancel.'
        $pasted = Read-Host 'Path'
        if ($null -ne $pasted -and $pasted.Trim().Equals('Q', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Cancelled: no game folder provided.'
        }
        $found = Resolve-GameDir $pasted
        if ($found) { return $found }
        Write-Host 'That is not the Soul Reaver I-II folder (needs 1\sr1.dll and SRX.exe). Try again.' -ForegroundColor Yellow
    }
}

function Ensure-NotRunning {
    while ($true) {
        $running = Get-Process SRX,NinthPillar -ErrorAction SilentlyContinue
        if (!$running) { return }

        Write-Host ''
        Write-Host 'Please close Soul Reaver and NinthPillar before installing/removing.' -ForegroundColor Yellow
        $running | Select-Object Id,ProcessName,Path | Format-Table -AutoSize
        $answer = Read-Host 'Close them, then press Enter to continue; type Q to cancel'
        if ($answer.Trim().Equals('Q', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Cancelled because game/helper is running.'
        }
    }
}

function Ensure-Artifacts([string] $gameDir) {
    $modDir = Join-Path $gameDir 'mods\NinthPillar'
    $helperBuild = Join-Path $modDir 'buildhelper.ps1'
    $launcherBuild = Join-Path $modDir 'buildlauncher.ps1'
    $helperOut = Join-Path $modDir 'NinthPillar.exe'
    $launcherOut = Join-Path $modDir $LauncherName

    # Prefer building fresh from source (picks up any source edits) when the
    # toolchains are present, but tolerate a missing/failed build and fall back
    # to the prebuilt exes shipped in the package. That keeps the installer
    # usable on machines without the .NET/MSVC build tools.
    #
    # The launcher MUST end up a NATIVE exe: a .NET launcher hangs when Steam
    # injects its overlay at CLR startup (see the handoff notes). buildlauncher
    # .ps1 compiles src\NinthPillarLauncher.c with MSVC; the shipped prebuilt
    # NinthPillarLauncher.exe is already native.
    if (Test-Path -LiteralPath $helperBuild) {
        try { & $helperBuild | Out-Host } catch { Write-Host "Helper build skipped: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    if (Test-Path -LiteralPath $launcherBuild) {
        try { & $launcherBuild | Out-Host } catch { Write-Host "Launcher build skipped: $($_.Exception.Message)" -ForegroundColor Yellow }
    }

    if (!(Test-Path -LiteralPath $helperOut)) {
        throw "Helper NinthPillar.exe is missing and could not be built: $helperOut"
    }
    if (!(Test-Path -LiteralPath $launcherOut)) {
        throw "Launcher is missing and could not be built: $launcherOut"
    }

    return $launcherOut
}

function Install-NinthPillar([string] $gameDir) {
    Ensure-NotRunning

    $modDir = Join-Path $gameDir 'mods\NinthPillar'
    $srx = Join-Path $gameDir 'SRX.exe'
    $real = Join-Path $gameDir $RealExeName
    $marker = Join-Path $modDir $MarkerName

    if ((Test-Path -LiteralPath $real) -and (Test-Path -LiteralPath $srx)) {
        throw "Already installed: $real exists. Run install_or_remove again to uninstall."
    }
    if (!(Test-Path -LiteralPath $real) -and !(Test-Path -LiteralPath $srx)) {
        throw "Could not find SRX.exe at: $srx"
    }

    $launcher = Ensure-Artifacts $gameDir

    if (!(Test-Path -LiteralPath $real)) {
        Move-Item -LiteralPath $srx -Destination $real
    }
    Copy-Item -LiteralPath $launcher -Destination $srx

    # The launcher runs the renamed game as a child process. Steam sets
    # SteamAppId in the environment only for the exe IT launches (the stub),
    # which the child inherits, so that path works. steam_appid.txt makes the
    # renamed game skip Steam's "restart through Steam" check unconditionally,
    # so the child-process launch is reliable however it is started.
    $appIdFile = Join-Path $gameDir 'steam_appid.txt'
    if (!(Test-Path -LiteralPath $appIdFile)) {
        [System.IO.File]::WriteAllText($appIdFile, $SteamAppId)
    }

    $installedAt = Get-Date -Format o
    Set-Content -LiteralPath $marker -Value @(
        'Ninth Pillar launcher installed',
        "InstalledAt=$installedAt",
        "GameDir=$gameDir",
        "OriginalExe=$real"
    )

    Write-Host ''
    Write-Host 'Installed. Steam will now start NinthPillar with Soul Reaver.' -ForegroundColor Green
}

function Uninstall-NinthPillar([string] $gameDir) {
    Ensure-NotRunning

    $modDir = Join-Path $gameDir 'mods\NinthPillar'
    $srx = Join-Path $gameDir 'SRX.exe'
    $real = Join-Path $gameDir $RealExeName
    $marker = Join-Path $modDir $MarkerName

    if (!(Test-Path -LiteralPath $real)) {
        throw "Not installed: backup executable not found at $real"
    }

    if (Test-Path -LiteralPath $srx) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $oldLauncher = Join-Path $modDir ("SRX.ninthpillar.launcher.removed-$stamp.exe")
        Move-Item -LiteralPath $srx -Destination $oldLauncher
    }

    Move-Item -LiteralPath $real -Destination $srx
    if (Test-Path -LiteralPath $marker) {
        Remove-Item -LiteralPath $marker -Force
    }

    # Remove the appid file we created (only if it still matches our appid, so
    # a hand-placed one for other purposes is left alone).
    $appIdFile = Join-Path $gameDir 'steam_appid.txt'
    if (Test-Path -LiteralPath $appIdFile) {
        $content = (Get-Content -LiteralPath $appIdFile -Raw).Trim()
        if ($content -eq $SteamAppId) {
            Remove-Item -LiteralPath $appIdFile -Force
        }
    }

    Write-Host ''
    Write-Host 'Uninstalled. Steam will now start Soul Reaver normally.' -ForegroundColor Green
}

try {
    $gameDir = Find-GameDir
    $real = Join-Path $gameDir $RealExeName
    $srx = Join-Path $gameDir 'SRX.exe'

    Write-Host "Soul Reaver folder: $gameDir"
    if ((Test-Path -LiteralPath $real) -and (Test-Path -LiteralPath $srx)) {
        Write-Host 'Existing install detected; uninstalling...'
        Uninstall-NinthPillar $gameDir
    } else {
        Write-Host 'No install detected; installing...'
        Install-NinthPillar $gameDir
    }
} catch {
    Write-Host ''
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'No changes completed.' -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host 'Done. Press Enter to close.'
[void][Console]::ReadLine()
