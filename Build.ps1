$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ "/out:$PSScriptRoot\ClockIt!.exe" "/win32icon:$PSScriptRoot\ClockIt.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Security.dll "$PSScriptRoot\TimeClock.cs" "$PSScriptRoot\FishbowlClock.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
