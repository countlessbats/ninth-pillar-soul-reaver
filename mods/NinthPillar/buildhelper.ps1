# Build the Ninth Pillar external helper (windowed, no console).
$ErrorActionPreference = 'Stop'
$dir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$src = Join-Path $dir 'NinthPillar.cs'
$out = Join-Path $dir 'NinthPillar.exe'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# stop a running instance so the exe isn't locked
$p = Get-Process NinthPillar -ErrorAction SilentlyContinue
if ($p) { $p | Stop-Process -Force; Start-Sleep -Milliseconds 400 }

# Windows.Gaming.Input (WinRT) lets the helper read DualSense/DualShock pads that
# run in native mode (no XInput emulation). Reference the WinRT metadata + the
# .NET Framework WinRT facades. Pick the newest available Windows.winmd.
# Only versioned SDK dirs (e.g. 10.0.26100.0) hold the full metadata; the
# 'Facade' dir has a stub Windows.WinMD that lacks Windows.Gaming.Input.
$winmd = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata\*\Windows.winmd' -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -match '^\d+\.\d+\.\d+' } |
    Sort-Object { [version]$_.Directory.Name } | Select-Object -Last 1
if (-not $winmd) { Write-Host 'Windows.winmd not found (Windows SDK required)' -ForegroundColor Red; exit 1 }
$fac = Get-ChildItem 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\*\Facades\System.Runtime.dll' -ErrorAction SilentlyContinue |
    Sort-Object FullName | Select-Object -Last 1
$facDir = Split-Path $fac.FullName -Parent
$wr = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'

& $csc /nologo /platform:x64 /target:winexe /out:"$out" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    /r:"$($winmd.FullName)" /r:"$wr" `
    /r:"$facDir\System.Runtime.dll" /r:"$facDir\System.Runtime.InteropServices.WindowsRuntime.dll" `
    "$src"
if ($LASTEXITCODE -ne 0) { Write-Host 'BUILD FAILED' -ForegroundColor Red; exit 1 }
Write-Host "Built: $out" -ForegroundColor Green
Get-Item $out | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize
