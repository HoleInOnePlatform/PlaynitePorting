param(
    [Parameter(Mandatory = $true)][string]$ApplicationDirectory,
    [Parameter(Mandatory = $true)][string]$ConfigurationFile,
    [Parameter(Mandatory = $true)][string]$OutputZip
)
$ErrorActionPreference = 'Stop'
$applicationPath = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
if (Get-ChildItem -LiteralPath $applicationPath -Filter 'Playnite.*.exe') {
    throw 'Playnite 이름의 이전 실행 파일이 포함됨. 새 폴더에 HoleInOne을 빌드해야 함.'
}
$configurationPath = (Resolve-Path -LiteralPath $ConfigurationFile).Path
$archivePath = [IO.Path]::GetFullPath($OutputZip)
$packageDirectory = Join-Path ([IO.Path]::GetDirectoryName($archivePath)) ([IO.Path]::GetFileNameWithoutExtension($archivePath) + '-Package')
if (Test-Path -LiteralPath $packageDirectory) { throw '배포 폴더가 이미 존재함. 새 ZIP 파일명을 지정해야 함.' }
if (Test-Path -LiteralPath $archivePath) { throw '기존 ZIP을 덮어쓰지 않음. 새 파일명을 지정해야 함.' }
if ($archivePath.StartsWith($applicationPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ZIP은 앱 폴더 밖에 생성해야 함.'
}
$settings = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$address = [Uri]$settings.BackendUrl
if (!$address.IsAbsoluteUri -or $address.Scheme -ne 'https' -or $address.IsLoopback -or $address.AbsolutePath -ne '/' -or
    $address.UserInfo -or $address.Query -or $address.Fragment -or $settings.PSObject.Properties.Name -contains 'BearerToken') {
    throw '토큰 없는 공개 HTTPS 서비스 주소 설정이 필요함.'
}
foreach ($file in @('HoleInOne.exe','Playnite.dll','HoleInOne.BrowserProcess.exe','libcef.dll','Tools/Legendary/legendary.exe','Tools/Legendary/LICENSE')) {
    if (!(Test-Path -LiteralPath (Join-Path $applicationPath $file))) { throw "필수 배포 파일 누락: $file" }
}
foreach ($name in @('UserData','ExtensionsData','holeinone.service.json','holeinone.development.json','holeinone.development-address.json','appsettings.Auth.json','appsettings.Local.json')) {
    if (Test-Path -LiteralPath (Join-Path $applicationPath $name)) { throw "개인 데이터 또는 잘못된 설정 포함: $name" }
}
if (Get-ChildItem -LiteralPath $applicationPath -Recurse -File -Filter '*.pem') { throw 'SSH 키는 배포할 수 없음.' }
$targetSettings = Join-Path $applicationPath 'holeinone.service.json'
@{ BackendUrl=$address.AbsoluteUri } | ConvertTo-Json |
    Set-Content -LiteralPath $targetSettings -Encoding UTF8
@'
@echo off
start "" "%~dp0HoleInOne.exe" --userdatadir "%LOCALAPPDATA%\HoleInOne-TeamTest" --nolibupdate
'@ | Set-Content -LiteralPath (Join-Path $applicationPath 'Start-HoleInOne.cmd') -Encoding ASCII
@'
$ErrorActionPreference = 'Stop'
try {
    $settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'holeinone.service.json') -Raw | ConvertFrom-Json
    $savedAddressPath = Join-Path $env:LOCALAPPDATA 'HoleInOne-TeamTest/holeinone.development-address.json'
    if (Test-Path -LiteralPath $savedAddressPath) {
        $savedAddress = Get-Content -LiteralPath $savedAddressPath -Raw | ConvertFrom-Json
        $settings.BackendUrl = $savedAddress.BackendUrl
    }
    $health = Invoke-RestMethod -Uri ($settings.BackendUrl.TrimEnd('/') + '/health') -TimeoutSec 12
    if ($health.status -ne 'ok' -or $health.service -ne 'holeinone' -or $health.authentication -ne 'accounts' -or $health.contractVersion -ne 2) { throw 'Unexpected server response.' }
    Write-Host ('Backend connection OK. Release: ' + $health.release)
} catch {
    Write-Host 'Backend connection failed. Check the HTTPS service address and network connection.'
    exit 1
}
'@ | Set-Content -LiteralPath (Join-Path $applicationPath 'Test-Backend.ps1') -Encoding UTF8
@"
# HoleInOne 팀 개발 테스트

Windows에서 ZIP 전체를 풀고 Start-HoleInOne.cmd를 실행합니다.
HoleInOne 회원으로 로그인한 뒤 본인의 Steam/Epic 계정을 연결합니다.
서버 주소: $($address.AbsoluteUri)
앱 사용자 데이터: %LOCALAPPDATA%\HoleInOne-TeamTest
서버를 변경하려면 설정에서 HTTPS 주소를 저장하고 새 서버에 로그인합니다.
Test-Backend.ps1은 토큰 없이 서비스 계약만 확인합니다.
개인 인증자료와 세이브를 배포물에 넣지 마세요.
"@ | Set-Content -LiteralPath (Join-Path $applicationPath 'READ-ME.txt') -Encoding UTF8
& (Join-Path $PSScriptRoot 'New-HoleInOnePackage.ps1') -ApplicationDirectory $applicationPath -OutputDirectory $packageDirectory -OutputZip $archivePath -TeamTest
Write-Host "팀 개발용 ZIP 생성 완료: $archivePath"
