$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$metadata = Join-Path ([IO.Path]::GetTempPath()) ('ClockIt-Build-' + [guid]::NewGuid() + '.cs')
$buildStamp = [DateTime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", [Globalization.CultureInfo]::InvariantCulture)
try {
    '[assembly: System.Reflection.AssemblyMetadata("BuildUtc", "{0}")]' -f $buildStamp |
        Set-Content -LiteralPath $metadata -Encoding UTF8
    & $compiler /nologo /target:winexe /optimize+ "/out:$PSScriptRoot\ClockIt!.exe" "/win32icon:$PSScriptRoot\ClockIt.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll /reference:System.Security.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\TimeClock.cs" "$PSScriptRoot\PayrollPdf.cs" "$PSScriptRoot\FishbowlClock.cs" "$PSScriptRoot\ClockItUpdater.cs" $metadata
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Remove-Item -LiteralPath $metadata -ErrorAction SilentlyContinue
}
