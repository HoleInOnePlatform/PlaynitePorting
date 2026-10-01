param([Parameter(Mandatory=$true)][string]$InputDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $InputDir 'Playnite.dll'))
$viewType = $assembly.GetType('Playnite.GameLink.InstantPlayWebView', $true)
$view = [Activator]::CreateInstance($viewType, $true)
$window = $view.WindowHost
$window.WindowState = 'Normal'
$window.Width = 800
$window.Height = 600
$window.Left = -10000
$window.Top = -10000
$browser = $viewType.GetField('browser', [Reflection.BindingFlags]'Instance,NonPublic').GetValue($view)
function Invoke-UiPump {
    $window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Start-Sleep -Milliseconds 10
}
try {
    $view.Navigate('about:blank')
    $view.Open()
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while (($null -eq $browser.CoreWebView2 -or $browser.CoreWebView2.Source -ne 'about:blank') -and [DateTime]::UtcNow -lt $deadline) { Invoke-UiPump }
    if ($null -eq $browser.CoreWebView2) { throw 'WebView2 initialization timed out.' }
    $browser.CoreWebView2.NavigateToString('<html><body style="background:white;color:black">WebView rendering check</body></html>')
    $check = $browser.CoreWebView2.ExecuteScriptAsync('JSON.stringify({width:innerWidth,height:innerHeight,codecs:RTCRtpReceiver.getCapabilities("video").codecs.map(c=>c.mimeType)})')
    while (-not $check.IsCompleted -and [DateTime]::UtcNow -lt $deadline) { Invoke-UiPump }
    if (-not $check.IsCompleted) { throw 'Script execution timed out.' }
    $result = ($check.GetAwaiter().GetResult() | ConvertFrom-Json) | ConvertFrom-Json
    if ($result.width -lt 100 -or $result.height -lt 100) { throw 'The browser has no usable viewport.' }
    if ($result.codecs -notcontains 'video/H264') { throw 'H.264 is unavailable.' }
    $result | ConvertTo-Json -Compress
}
finally { $view.Dispose() }
