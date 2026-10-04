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
if (Test-Path -LiteralPath $archivePath) { throw '기존 ZIP을 덮어쓰지 않음. 새 파일명을 지정해야 함.' }
if ($archivePath.StartsWith($applicationPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ZIP은 앱 폴더 밖에 생성해야 함.'
}
$settings = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
$address = [Uri]$settings.BackendUrl
if (!$address.IsAbsoluteUri -or $address.Scheme -notin @('http','https') -or $address.AbsolutePath -ne '/' -or
    $address.UserInfo -or $address.Query -or $address.Fragment -or !$settings.BearerToken -or $settings.BearerToken.Length -lt 32 -or $settings.BearerToken -match '[\r\n]') {
    throw '올바른 개발 서버 주소와 인증 설정이 필요함.'
}
foreach ($file in @('HoleInOne.exe','Playnite.dll','HoleInOne.BrowserProcess.exe','libcef.dll','Tools/Legendary/legendary.exe','Tools/Legendary/LICENSE')) {
    if (!(Test-Path -LiteralPath (Join-Path $applicationPath $file))) { throw "필수 배포 파일 누락: $file" }
}
foreach ($name in @('UserData','ExtensionsData','holeinone.service.json','holeinone.development-address.json','appsettings.Auth.json','appsettings.Local.json')) {
    if (Test-Path -LiteralPath (Join-Path $applicationPath $name)) { throw "개인 데이터 또는 잘못된 설정 포함: $name" }
}
if (Get-ChildItem -LiteralPath $applicationPath -Recurse -File -Filter '*.pem') { throw 'SSH 키는 배포할 수 없음.' }
$targetSettings = Join-Path $applicationPath 'holeinone.development.json'
@{ BackendUrl=$address.AbsoluteUri; BearerToken=$settings.BearerToken } | ConvertTo-Json |
    Set-Content -LiteralPath $targetSettings -Encoding UTF8
@'
@echo off
start "" "%~dp0HoleInOne.exe" --userdatadir "%LOCALAPPDATA%\HoleInOne-TeamTest" --nolibupdate
'@ | Set-Content -LiteralPath (Join-Path $applicationPath 'Start-HoleInOne.cmd') -Encoding ASCII
@'
$ErrorActionPreference = 'Stop'
try {
    $settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'holeinone.development.json') -Raw | ConvertFrom-Json
    $savedAddressPath = Join-Path $env:LOCALAPPDATA 'HoleInOne-TeamTest/holeinone.development-address.json'
    if (Test-Path -LiteralPath $savedAddressPath) {
        $savedAddress = Get-Content -LiteralPath $savedAddressPath -Raw | ConvertFrom-Json
        $settings.BackendUrl = $savedAddress.BackendUrl
    }
    $health = Invoke-RestMethod -Uri ($settings.BackendUrl.TrimEnd('/') + '/health') -Headers @{Authorization=('Bearer ' + $settings.BearerToken)} -TimeoutSec 12
    if ($health.status -ne 'ok' -or $health.service -ne 'holeinone' -or $health.authentication -ne 'development') { throw 'Unexpected server response.' }
    Write-Host ('Backend connection OK. Release: ' + $health.release)
} catch {
    Write-Host 'Backend connection failed. Check EC2 power, its current IP, and TCP 5010 security group rules.'
    exit 1
}
'@ | Set-Content -LiteralPath (Join-Path $applicationPath 'Test-Backend.ps1') -Encoding UTF8
@"
# HoleInOne 팀 개발 테스트

1. Windows 10/11 x64 PC에서 ZIP 전체를 압축 해제함. ZIP 안에서 바로 실행하지 않음.
2. Start-HoleInOne.cmd를 실행함. 개발 도구·AWS 키·Backend 설치는 필요 없음.
3. Steam 또는 Epic을 본인 계정으로 연동하고 라이브러리를 새로고침함.
4. 현재 클라우드 테스트 지원 게임은 Slay the Spire 2(steam-2868840)임. 다운로드와 전환을 시험하려면 미설치 상태에서 시작함. 기존 게임·세이브를 임의 삭제하지 않음.

서버 주소: $($address.AbsoluteUri)
앱 사용자 데이터: %LOCALAPPDATA%\HoleInOne-TeamTest
HoleInOne 자체 회원 로그인은 사용하지 않음. 스토어 로그인은 각 팀원이 진행함.
연결 확인: PowerShell에서 .\Test-Backend.ps1 실행.

EC2와 게임 스트리밍 용량이 켜져 있어야 함. TCP 5010에 접속하는 팀원 PC의 공인 IP를 EC2 보안 그룹에서 허용해야 함.
EC2 퍼블릭 IP가 변경되면 서버는 시작 시 현재 IP를 자동 반영함.
앱 메뉴 → 라이브러리 → HoleInOne 설정 → 개발 서버 주소에서 새 IP 입력 → 연결 확인 → 주소 저장.
IP만 입력하면 http://IP:5010/으로 처리함. 저장한 주소는 다음 게임 실행부터 사용하며 앱을 다시 켜도 유지됨.
클라우드 게임 창이 열려 있으면 닫은 뒤 주소를 저장함. 서버 주소는 각 PC의 사용자 데이터에 저장되며 접속 토큰은 그대로 유지됨.
이 폴더에는 공동 개발용 접속 토큰이 포함됨. 지정된 테스트 팀원에게만 공유하고 저장소나 공개 링크에 올리지 않음.
HTTP 개발 연결은 암호화되지 않음. 실제 고객 서비스용 배포본이 아니며 기존 라이브러리 보안 경고가 남아 있음.
이 빌드의 이전 버전 복귀는 기존 배포 폴더로 실행하면 됨. 실제 게임 세이브의 복구와는 별개임.
"@ | Set-Content -LiteralPath (Join-Path $applicationPath 'READ-ME.txt') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($applicationPath, $archivePath, [IO.Compression.CompressionLevel]::Fastest, $false)
Write-Host "팀 개발용 ZIP 생성 완료: $archivePath"
