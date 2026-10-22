param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$packages = Join-Path $projectRoot '.packages'
& dotnet publish (Join-Path $projectRoot 'tools/SteamCloudBridge/SteamCloudBridge.csproj') `
    -c $Configuration -r win-x64 --self-contained true -o $destination `
    "-p:RestorePackagesPath=$packages" -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw "Steam Cloud module publish failed: $LASTEXITCODE" }

$licenses = Join-Path $destination 'Licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packages 'steamkit2/3.4.0/license.txt') -Destination (Join-Path $licenses 'SteamKit2-LGPL.txt')
Copy-Item -LiteralPath (Join-Path $packages 'steamkit2/3.4.0/3rd party.txt') -Destination (Join-Path $licenses 'SteamKit2-ThirdParty.txt')
Copy-Item -LiteralPath (Join-Path $packages 'qrcoder/1.8.0/LICENSE.txt') -Destination (Join-Path $licenses 'QRCoder-MIT.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'tools/SteamCloudBridge/README.md') -Destination $destination
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
Write-Host "Steam Cloud module: $destination/HoleInOne.SteamCloud.exe"
