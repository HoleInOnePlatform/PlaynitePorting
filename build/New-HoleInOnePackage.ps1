param(
    [Parameter(Mandatory = $true)][string]$ApplicationDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$OutputZip,
    [switch]$TeamTest
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) { throw '대상 폴더가 이미 존재함. 새 폴더를 지정해야 함.' }
if ($destination.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '배포 폴더는 원본 앱 폴더 밖에 생성해야 함.'
}
foreach ($file in @('HoleInOne.exe', 'HoleInOne.BrowserProcess.exe', 'Playnite.dll', 'libcef.dll',
    'Tools/SteamLink/HoleInOne.SteamLink.exe', 'Tools/SteamLink/artifact-manifest.json', 'Tools/Legendary/legendary.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "필수 파일 누락: $file" }
}
foreach ($name in @('UserData', 'ExtensionsData', 'holeinone.development.json', 'Tools/SteamCloud', 'holeinone.development-address.json', 'appsettings.Auth.json', 'appsettings.Local.json')) {
    if (Test-Path -LiteralPath (Join-Path $source $name)) { throw "개인 데이터 포함: $name" }
}
if (Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object {
    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $_.Extension -in @('.pem','.hioauth') -or $_.Name -in @('AuthStateImport','login.dat','local.vdf','local.vdf.orig','loginusers.vdf','token_plaintext.bin','token_meta.json') -or $_.Name -like 'ssfn*'
}) { throw '인증 자료, 키 또는 링크가 포함되어 있습니다. 깨끗한 빌드 폴더만 배포하세요.' }
if ($OutputZip) {
    $archive = [IO.Path]::GetFullPath($OutputZip)
    if (Test-Path -LiteralPath $archive) { throw '기존 ZIP을 덮어쓰지 않음.' }
    foreach ($directory in @($source, $destination)) {
        if ($archive.StartsWith($directory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'ZIP은 앱 및 배포 폴더 밖에 생성해야 함.'
        }
    }
}

$app = Join-Path $destination 'App'
$docs = Join-Path $destination 'Docs'
$diagnostics = Join-Path $destination 'Diagnostics'
New-Item -ItemType Directory -Path $app, $docs, $diagnostics | Out-Null
# Preserve runtime-relative paths, including native browser dependencies and licenses.
foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
    if ($item.Name -in @('Start-HoleInOne.cmd', 'Safe Mode.bat', 'Test-Backend.ps1')) { continue }
    $target = if ($item.Name -in @('READ-ME.txt', 'STEAM-CLOUD-READ-ME.txt')) { $docs } else { $app }
    Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse -Force
}
$arguments = ''
if ($TeamTest) { $arguments = ' --userdatadir "%LOCALAPPDATA%\HoleInOne-TeamTest" --nolibupdate' }
@"
@echo off
start "" /D "%~dp0App" "%~dp0App\HoleInOne.exe"$arguments %*
"@ | Set-Content -LiteralPath (Join-Path $destination 'Start-HoleInOne.cmd') -Encoding ASCII
@'
@echo off
call "%~dp0..\Start-HoleInOne.cmd" --safestartup %*
'@ | Set-Content -LiteralPath (Join-Path $diagnostics 'Safe Mode.cmd') -Encoding ASCII
if (Test-Path -LiteralPath (Join-Path $source 'Test-Backend.ps1')) {
    $check = Get-Content -LiteralPath (Join-Path $source 'Test-Backend.ps1') -Raw
    $check = $check.Replace("Join-Path `$PSScriptRoot 'holeinone.development.json'", "Join-Path `$PSScriptRoot '../App/holeinone.service.json'")
    $check | Set-Content -LiteralPath (Join-Path $diagnostics 'Test-Backend.ps1') -Encoding UTF8
}
@'
HoleInOne

ZIP 전체를 새 폴더에 압축 해제하고 Start-HoleInOne.cmd를 실행하세요.
App: 실행에 필요한 파일입니다. 개별 파일을 옮기거나 삭제하지 마세요.
Docs: 기존 사용 안내와 이번 빌드의 기능 설명입니다.
Diagnostics: 안전 모드 실행과 서버 연결 확인 도구입니다.

기존 팀 테스트 빌드의 사용자 데이터와 서버 주소 설정은 그대로 사용합니다.
Docs의 안내에서 Test-Backend.ps1은 Diagnostics 폴더의 파일을 뜻합니다.
기존 홀인원을 종료한 뒤 실행하세요.
'@ | Set-Content -LiteralPath (Join-Path $destination 'READ-ME.txt') -Encoding UTF8
if ($OutputZip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($destination, $archive, [IO.Compression.CompressionLevel]::Fastest, $false)
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($archive) |
        Set-Content -LiteralPath ($archive + '.sha256') -Encoding ASCII
}
Write-Host "정리된 배포 폴더: $destination"
