param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/HoleInOne'),
    [string]$ServiceUrl,
    [switch]$Distribution
)

$ErrorActionPreference = 'Stop'
if ($Distribution -and (!$ServiceUrl -or $Configuration -ne 'Release')) {
    throw '외부 배포는 -Configuration Release -Distribution -ServiceUrl <실제 HTTPS 주소>가 필요함.'
}
if ($ServiceUrl) {
    $serviceAddress = [Uri]$ServiceUrl
    if (!$serviceAddress.IsAbsoluteUri -or $serviceAddress.Scheme -ne 'https' -or $serviceAddress.IsLoopback -or $serviceAddress.AbsolutePath -ne '/' -or $serviceAddress.UserInfo -or $serviceAddress.Query -or $serviceAddress.Fragment) {
        throw '시연 배포에는 공개 HTTPS 서비스 주소가 필요함.'
    }
}
$projectRoot = Split-Path $PSScriptRoot -Parent
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $locator)) { throw 'Visual Studio Build Tools와 .NET desktop build tools가 필요함.' }
$compiler = & $locator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$compiler) { throw 'MSBuild를 찾을 수 없음.' }
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath (Join-Path $outputDirectory 'Tools/SteamCloud')) {
    throw 'Steam Cloud 업로드 도구가 남아 있는 이전 배포 폴더임. -OutputDirectory로 새 폴더를 지정해야 함.'
}
if (Test-Path -LiteralPath (Join-Path $outputDirectory 'Playnite.DesktopApp.exe')) {
    throw 'Playnite 이름의 이전 빌드가 있는 폴더임. -OutputDirectory로 새 폴더를 지정해야 함.'
}
foreach ($project in @('Playnite.DesktopApp', 'Playnite.BrowserProcess')) {
    & $compiler (Join-Path $projectRoot "source/$project/$project.csproj") /restore /nologo /verbosity:minimal `
        "/p:Configuration=$Configuration" /p:Platform=x86 /p:PostBuildEvent= `
        "/p:OutputPath=$outputDirectory" "/p:HoleInOneDistribution=$($Distribution.IsPresent.ToString().ToLowerInvariant())" /p:AppendTargetFrameworkToOutputPath=false /p:AppendRuntimeIdentifierToOutputPath=false
    if ($LASTEXITCODE -ne 0) { throw "$project 빌드 실패: $LASTEXITCODE" }
}
& (Join-Path $PSScriptRoot 'Build-SteamLink.ps1') -Configuration $Configuration -OutputDirectory (Join-Path $outputDirectory 'Tools/SteamLink')
Write-Host "빌드 완료: $outputDirectory/HoleInOne.exe"
if ($ServiceUrl) {
    @{ BackendUrl = $serviceAddress.AbsoluteUri.TrimEnd('/') + '/' } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $outputDirectory 'holeinone.service.json') -Encoding UTF8
    Write-Host '사용자 로그인 배포 설정 완료. 서버 주소만 포함되며 비밀번호나 공용 토큰은 포함되지 않음.'
}
