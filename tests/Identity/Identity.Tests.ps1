param([Parameter(Mandatory = $true)][string]$ApplicationDirectory)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$exe = Join-Path $app 'HoleInOne.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Build HoleInOne.exe first.' }
if (Get-Process -Name HoleInOne,'HoleInOne.Fullscreen' -ErrorAction SilentlyContinue) {
    throw 'Close HoleInOne before running the isolated smoke test.'
}
$testData = Join-Path (Split-Path $app -Parent) ('IdentitySmoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testData | Out-Null
$oldProcesses = @(Get-Process -Name Playnite.DesktopApp,Playnite.FullscreenApp -ErrorAction SilentlyContinue)
$oldProtocol = (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\Playnite\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
$oldExtension = (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\Playnite.ext\shell\open\command' -ErrorAction SilentlyContinue).'(default)'
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'Playnite.lnk'
$oldStartup = if (Test-Path -LiteralPath $startupLink) { (Get-FileHash -LiteralPath $startupLink).Hash } else { $null }
$legacyMutex = [Threading.Mutex]::new($false, 'PlayniteInstaceMutex')
$main = $null
try {
    $args = @('--userdatadir', ('"' + $testData + '"'), '--nolibupdate', '--hidesplashscreen')
    $main = Start-Process -FilePath $exe -ArgumentList $args -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        $main.Refresh()
        if ($main.HasExited) { throw 'HoleInOne exited while the legacy Playnite mutex existed.' }
        $logPath = Join-Path $testData 'playnite.log'
        $log = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' }
    } until (($main.MainWindowTitle -eq 'HoleInOne' -and $log -match 'Application .* started') -or [DateTime]::UtcNow -gt $deadline)
    if ($main.MainWindowTitle -ne 'HoleInOne' -or $log -notmatch 'Application .* started') { throw "Startup did not finish. See $logPath" }
    if ($log -match 'ERROR|FATAL|Failed to start pipe') { throw "Startup logged an error. See $logPath" }
    Write-Host 'PASS: main window and libraries start with the Playnite mutex present.'
    if ($main.ProcessName -ne 'HoleInOne' -or (Get-Item -LiteralPath $exe).VersionInfo.ProductName -ne 'HoleInOne') { throw 'Wrong executable identity.' }
    Write-Host 'PASS: process and Windows product metadata identify HoleInOne.'
    $second = Start-Process -FilePath $exe -ArgumentList $args -PassThru -WindowStyle Hidden
    if (!$second.WaitForExit(15000)) { throw 'Second launch did not forward to the existing HoleInOne instance.' }
    $main.Refresh()
    if ($main.HasExited -or @(Get-Process -Name HoleInOne).Count -ne 1) { throw 'Single-instance routing failed.' }
    Write-Host 'PASS: second launch focuses the existing HoleInOne instance.'
    $newProtocol = (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\holeinone\shell\open\command').'(default)'
    if (!$newProtocol.Contains($exe)) { throw 'HoleInOne URI protocol points to the wrong executable.' }
    if ((Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\Playnite\shell\open\command' -ErrorAction SilentlyContinue).'(default)' -ne $oldProtocol -or
        (Get-ItemProperty -LiteralPath 'Registry::HKEY_CURRENT_USER\Software\Classes\Playnite.ext\shell\open\command' -ErrorAction SilentlyContinue).'(default)' -ne $oldExtension) { throw 'Playnite registration changed.' }
    $newStartup = if (Test-Path -LiteralPath $startupLink) { (Get-FileHash -LiteralPath $startupLink).Hash } else { $null }
    if ($newStartup -ne $oldStartup) { throw 'Playnite startup shortcut changed.' }
    Write-Host 'PASS: HoleInOne registers its own links and preserves Playnite registrations and startup shortcut.'
    $shutdown = Start-Process -FilePath $exe -ArgumentList @('--shutdown','--userdatadir',('"' + $testData + '"'),'--hidesplashscreen') -PassThru -WindowStyle Hidden
    if (!$shutdown.WaitForExit(15000) -or !$main.WaitForExit(15000)) { throw 'HoleInOne shutdown routing failed.' }
    foreach ($old in $oldProcesses) { $old.Refresh(); if ($old.HasExited) { throw 'A pre-existing Playnite process exited.' } }
    Write-Host 'PASS: shutdown closes HoleInOne while pre-existing Playnite processes remain running.'
    Write-Host "Smoke test logs: $testData"
} finally {
    if ($main -and !$main.HasExited) { Stop-Process -Id $main.Id -Force }
    $legacyMutex.Dispose()
}
