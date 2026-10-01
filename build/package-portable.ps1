param(
    [string]$InputDir = (Join-Path $PSScriptRoot 'Release'),
    [string]$OutputZip = (Join-Path $PSScriptRoot '..\dist\HoleInOne-portable.zip')
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $InputDir).Path
foreach ($required in @('Playnite.DesktopApp.exe', 'Playnite.FullscreenApp.exe', 'Playnite.dll', 'Playnite.BrowserProcess.exe', 'Playnite.BrowserProcess.exe.config', 'libcef.dll', 'CefSharp.BrowserSubprocess.exe', 'Microsoft.Web.WebView2.Core.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "Missing release file: $required" }
}
foreach ($library in @('SteamLibrary', 'EpicLibrary')) {
    foreach ($required in @("Extensions\${library}_Builtin\extension.yaml", "Extensions\${library}_Builtin\$library.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "Missing bundled library: $required" }
    }
}
$modSource = Join-Path $PSScriptRoot '..\..\GameLoader\dist\GameLoader'
foreach ($file in @('GameLoader.dll', 'GameLoader.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $modSource $file))) { throw "Build GameLoader first: $file" }
}
# Only the loader's two runtime files are added; static media assets are hosted by the backend.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$destination = [IO.Path]::GetFullPath($OutputZip)
if ($destination.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP must be outside input directory.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
$temporary = $destination + '.tmp'
if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
$exclude = @('PlayniteInstaller.exe', 'PlayniteInstaller.exe.config', 'System.Management.Automation.dll', 'Windows.winmd')
$archive = [IO.Compression.ZipFile]::Open($temporary, 'Create')
try {
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $relative = $file.FullName.Substring($source.Length).TrimStart('\', '/') -replace '\\', '/'
        if ($file.Extension -in @('.pdb', '.log') -or $file.Name -in $exclude -or $relative.StartsWith('GameLink/', [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($relative.Length -gt 180) { throw "Portable path too long: $relative" }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    foreach ($file in @('GameLoader.dll', 'GameLoader.json')) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $modSource $file), "GameLink/GameLoader/$file", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
catch { $archive.Dispose(); if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }; throw }
finally { $archive.Dispose() }
if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($temporary, $destination, $destination + '.previous') } else { [IO.File]::Move($temporary, $destination) }
Get-Item -LiteralPath $destination | Select-Object FullName, Length
Get-FileHash -LiteralPath $destination -Algorithm SHA256 | Select-Object Hash
