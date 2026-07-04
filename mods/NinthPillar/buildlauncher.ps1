# Build the native Ninth Pillar launcher stub (replaces SRX.exe).
# Native so Steam's overlay injection does not deadlock it (the old C# stub hung).
$ErrorActionPreference = 'Stop'
$dir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$src = Join-Path $dir 'src\NinthPillarLauncher.c'
$out = Join-Path $dir 'NinthPillarLauncher.exe'
$vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
if (!(Test-Path -LiteralPath $vcvars)) {
    $vcvars = 'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
}
if (!(Test-Path -LiteralPath $vcvars)) { throw "vcvars64.bat not found" }

# cl writes .obj files into the current dir; build from a temp dir so nothing
# lands in the game folder. /MT static CRT = no VC runtime dependency.
# GUI subsystem = no console window.
$work = Join-Path $env:TEMP 'ninthpillar_launcher_build'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$cmd = "call `"$vcvars`" >nul && cd /d `"$work`" && cl /nologo /O1 /MT /W3 `"$src`" /Fe:`"$out`" /link /SUBSYSTEM:WINDOWS user32.lib shlwapi.lib"
& cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) { Write-Host 'LAUNCHER BUILD FAILED' -ForegroundColor Red; exit 1 }
Write-Host "Built: $out" -ForegroundColor Green
Get-Item $out | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
