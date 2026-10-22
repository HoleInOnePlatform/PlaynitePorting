param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/HoleInOne')
)
$ErrorActionPreference = 'Stop'
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$toolDirectory = Join-Path $outputDirectory 'Tools/Legendary'
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
$executable = Join-Path $toolDirectory 'legendary.exe'
$sha256 = '6be77857dc0a6dd33ea7442ce8b5291944ec6da53757f81ca95b57ce794baf5b'
if (!(Test-Path -LiteralPath $executable) -or (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $sha256) {
    $download = Join-Path $toolDirectory 'legendary.download'
    Invoke-WebRequest 'https://github.com/legendary-gl/legendary/releases/download/0.21.1/legendary_windows_x64.exe' -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $sha256) { throw 'Legendary SHA256 불일치.' }
    Move-Item -LiteralPath $download -Destination $executable -Force
}
# Ship the unmodified tool with its corresponding source and license.
$sourceArchive = Join-Path $toolDirectory 'legendary-0.21.1-source.zip'
if (!(Test-Path -LiteralPath $sourceArchive)) {
    Invoke-WebRequest 'https://github.com/legendary-gl/legendary/archive/refs/tags/0.21.1.zip' -OutFile $sourceArchive
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($sourceArchive)
try {
    $license = $archive.Entries | Where-Object { $_.FullName -match '/LICENSE$' } | Select-Object -First 1
    if (!$license) { throw 'Legendary 라이선스 파일을 찾지 못함.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($license, (Join-Path $toolDirectory 'LICENSE'), $true)
} finally { $archive.Dispose() }
Write-Host 'HoleInOne Legendary 도구 준비 완료.'
