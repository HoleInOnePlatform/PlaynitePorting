param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$backend = Join-Path (Split-Path -Parent $root) 'Backend'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/SteamAuthProbe-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -or (Test-Path -LiteralPath ($OutputDirectory + '.zip'))) { throw 'Output already exists; select a new directory.' }
& dotnet run --project (Join-Path $backend 'tests-steam-auth-probe/SteamAuthProbe.Checks.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Protocol checks failed.' }
& dotnet publish (Join-Path $root 'tools/SteamAuthProbe/SteamAuthProbe.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=false' -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$licenses = Join-Path $OutputDirectory 'Licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $nuget 'steamkit2/3.4.0/license.txt') -Destination (Join-Path $licenses 'SteamKit2-license.txt')
Copy-Item -LiteralPath (Join-Path $nuget 'steamkit2/3.4.0/3rd party.txt') -Destination (Join-Path $licenses 'SteamKit2-third-party.txt')
Copy-Item -LiteralPath (Join-Path $nuget 'qrcoder/1.8.0/LICENSE.txt') -Destination (Join-Path $licenses 'QRCoder-license.txt')
Copy-Item -Path (Join-Path $root 'tools/SteamAuthProbe/Licenses/*.txt') -Destination $licenses
$runtime = Get-Content -LiteralPath (Join-Path $OutputDirectory 'HoleInOne.SteamAuthProbe.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $package = Join-Path $nuget ($framework.name.ToLowerInvariant() + '.runtime.win-x64/' + $framework.version)
    Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenses ($framework.name + '-' + $_.Name))
    }
}
Copy-Item -LiteralPath (Join-Path $root 'tools/SteamAuthProbe/README.md') -Destination (Join-Path $OutputDirectory 'READ-ME.md')
Copy-Item -LiteralPath (Join-Path $root 'tools/SteamAuthProbe/THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
$source = Join-Path $OutputDirectory 'Source'
foreach ($item in @(
    @{ Path=(Join-Path $root 'tools/SteamAuthProbe'); Name='PlaynitePorting/tools/SteamAuthProbe' },
    @{ Path=(Join-Path $backend 'tools/SteamAuthProbe.Protocol'); Name='Backend/tools/SteamAuthProbe.Protocol' },
    @{ Path=(Join-Path $backend 'tests-steam-auth-probe'); Name='Backend/tests-steam-auth-probe' }
)) {
    $destination = Join-Path $source $item.Name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $item.Path -File | Where-Object { $_.Extension -in @('.cs', '.csproj', '.md') } |
        Copy-Item -Destination $destination
}
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath ($OutputDirectory + '.zip')
Write-Host "Created $OutputDirectory.zip. Real Windows/Steam/GameLift validation is still required."
