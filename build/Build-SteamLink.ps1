param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$BackendDirectory = (Join-Path $PSScriptRoot '../../Backend'),
    [switch]$NoRestore,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$packages = Join-Path $projectRoot '.packages'
$coreRoot = Join-Path ([IO.Path]::GetFullPath($BackendDirectory)) 'tools/SteamLink.Core'
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'steam-link-core.lock.json') -Raw | ConvertFrom-Json
foreach ($file in $lock.files) {
    if (!(Test-Path -LiteralPath (Join-Path $coreRoot $file.path))) {
        throw "Missing SteamLink.Core/$($file.path). Checkout $($lock.repository) revision $($lock.revision) into $BackendDirectory (requires Backend read access), or pass -BackendDirectory. See build/DEPENDENCIES.md."
    }
    if ((Get-FileHash -LiteralPath (Join-Path $coreRoot $file.path) -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "SteamLink.Core source differs from locked revision $($lock.revision). Review and update the lock before building."
    }
}
$coreProject = Join-Path $coreRoot 'SteamLink.Core.csproj'
$restoreArgs = @()
if ($NoRestore) { $restoreArgs = @('--no-restore') }
& dotnet publish (Join-Path $projectRoot 'tools/SteamLink/SteamLink.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $destination `
    "-p:RestorePackagesPath=$packages" "-p:SteamLinkCoreProject=$coreProject" -p:PublishSingleFile=false @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "Steam link module publish failed: $LASTEXITCODE" }

$licenses = Join-Path $destination 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packages 'steamkit2/3.4.0/license.txt') -Destination (Join-Path $licenses 'SteamKit2-LGPL.txt')
Copy-Item -LiteralPath (Join-Path $packages 'steamkit2/3.4.0/3rd party.txt') -Destination (Join-Path $licenses 'SteamKit2-ThirdParty.txt')
Copy-Item -LiteralPath (Join-Path $packages 'qrcoder/1.8.0/LICENSE.txt') -Destination (Join-Path $licenses 'QRCoder-MIT.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools/SteamLink/README.md') -Destination $destination
$sourceArchive = Join-Path $packages 'steamkit2/3.4.0/steamkit2-3.4.0-source.zip'
$sourceHash = '8E9F5451AE97A2AE0885E44EC7872EF19948EAC8D0A677B7307C6F87AB4049C5'
if (!(Test-Path -LiteralPath $sourceArchive)) {
    Invoke-WebRequest 'https://github.com/SteamRE/SteamKit/archive/refs/tags/3.4.0.zip' -OutFile $sourceArchive
}
if ((Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash -ne $sourceHash) { throw 'SteamKit2 source archive hash mismatch.' }
Copy-Item -LiteralPath $sourceArchive -Destination $licenses
@'
SteamKit2 3.4.0: https://github.com/SteamRE/SteamKit/tree/3.4.0
Source: https://github.com/SteamRE/SteamKit/archive/refs/tags/3.4.0.zip
QRCoder 1.8.0: https://github.com/codebude/QRCoder
SteamKit2 and its dependencies remain separate, replaceable assemblies.
'@ | Set-Content -LiteralPath (Join-Path $licenses 'Sources.txt') -Encoding UTF8
Write-Host "Steam link module: $destination/HoleInOne.SteamLink.exe"

$artifactFiles = Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object Name -ne 'artifact-manifest.json' | ForEach-Object {
    @{ path=$_.FullName.Substring($destination.TrimEnd('\').Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
@{ contractVersion=2; coreRevision=$lock.revision; files=@($artifactFiles) } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $destination 'artifact-manifest.json') -Encoding UTF8
