param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $locator)) { throw 'Visual Studio Build Tools와 .NET desktop build tools가 필요함.' }
$compiler = & $locator -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$compiler) { throw 'MSBuild를 찾을 수 없음.' }
$outputDirectory = Join-Path $projectRoot 'artifacts/HoleInOne'
foreach ($project in @('Playnite.DesktopApp', 'Playnite.BrowserProcess')) {
    & $compiler (Join-Path $projectRoot "source/$project/$project.csproj") /restore /nologo /verbosity:minimal `
        "/p:Configuration=$Configuration" /p:Platform=x86 /p:PostBuildEvent= `
        "/p:OutputPath=$outputDirectory" /p:AppendTargetFrameworkToOutputPath=false /p:AppendRuntimeIdentifierToOutputPath=false
    if ($LASTEXITCODE -ne 0) { throw "$project 빌드 실패: $LASTEXITCODE" }
}
Write-Host "빌드 완료: $outputDirectory/Playnite.DesktopApp.exe"
