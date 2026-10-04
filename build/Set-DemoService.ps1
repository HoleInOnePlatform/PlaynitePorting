param(
    [string]$ServiceUrl,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/HoleInOne-Login')
)
$ErrorActionPreference = 'Stop'
if (!$ServiceUrl) { $ServiceUrl = Read-Host 'CloudFront 배포 도메인 주소 (https:// 포함)' }
$serviceAddress = [Uri]$ServiceUrl
if (!$serviceAddress.IsAbsoluteUri -or $serviceAddress.Scheme -ne 'https' -or $serviceAddress.IsLoopback -or $serviceAddress.UserInfo -or $serviceAddress.Query -or $serviceAddress.Fragment -or $serviceAddress.AbsolutePath -ne '/') {
    throw '경로·사용자 정보가 없는 HTTPS 서버 주소가 필요함.'
}
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (!(Test-Path -LiteralPath (Join-Path $outputDirectory 'HoleInOne.exe'))) { throw '먼저 HoleInOne 앱 빌드 필요.' }
$health = Invoke-RestMethod -Uri ($serviceAddress.AbsoluteUri + 'health') -Method Get -TimeoutSec 20
if ($health.status -ne 'ok' -or $health.service -ne 'holeinone' -or $health.contractVersion -ne 1 -or $health.authentication -ne 'accounts') {
    throw '로그인 가능한 HoleInOne 서버인지 확인 실패. 서비스 버전과 계정 인증 설정 확인 필요.'
}
@{ BackendUrl = $serviceAddress.AbsoluteUri } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $outputDirectory 'holeinone.service.json') -Encoding UTF8
Write-Host "공개 주소와 로그인 화면 설정 완료: $outputDirectory"
Write-Host '이 폴더 전체를 노트북으로 복사한 뒤 HoleInOne.exe를 실행하면 됨.'
