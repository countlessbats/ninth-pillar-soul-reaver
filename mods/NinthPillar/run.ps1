$ErrorActionPreference = "Stop"

$modDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $modDir "NinthPillar.exe"
$src = Join-Path $modDir "NinthPillar.cs"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (!(Test-Path $exe) -or (Get-Item $src).LastWriteTimeUtc -gt (Get-Item $exe).LastWriteTimeUtc) {
    & $csc /nologo /optimize+ /platform:x64 /target:exe /out:$exe $src
}

& $exe
