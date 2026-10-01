param(
    [Parameter(Mandatory=$true)][string]$InputDir,
    [string]$DataDir = (Join-Path $env:TEMP ('HoleInOne-CefCheck-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $InputDir).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$probe = Join-Path $source ('.cef-startup-check-' + [Guid]::NewGuid().ToString('N') + '.exe')
$config = $probe + '.config'
foreach ($file in @('Playnite.BrowserProcess.exe','Playnite.BrowserProcess.exe.config','Playnite.dll','CefSharp.dll','CefSharp.Core.dll','CefSharp.OffScreen.dll','Playnite.DesktopApp.exe.config')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file))) { throw "Missing startup dependency: $file" }
}
try {
    & $compiler /nologo /target:exe /platform:x86 "/out:$probe" "/r:$source\Playnite.dll" "/r:$source\CefSharp.dll" "/r:$source\CefSharp.Core.dll" "/r:$source\CefSharp.OffScreen.dll" (Join-Path $PSScriptRoot 'CefStartupProbe.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Startup probe compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $source 'Playnite.DesktopApp.exe.config') -Destination $config
    & $probe ([IO.Path]::GetFullPath($DataDir))
    if ($LASTEXITCODE -ne 0) { throw 'Production CEF startup check failed.' }
}
finally {
    foreach ($file in @($probe,$config)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
}
# Isolated cache/CEF logs are retained in DataDir for diagnosis; this does not test the full UI or GameLift.
