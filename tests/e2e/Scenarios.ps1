# Extra end-to-end scenarios: menu capture, click-to-select window, save dialog + remembered folder.
# Drives the real mouse and keyboard. Usage: Scenarios.ps1 -Exe <Snip.exe> -OutDir <folder>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Input.ps1')
function Cleanup {
    if ($script:notepad -and -not $script:notepad.HasExited) { $script:notepad.Kill() }
    if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill() }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$saveDir = Join-Path $OutDir 'saved'
New-Item -ItemType Directory -Force $saveDir | Out-Null
Get-ChildItem $saveDir | Remove-Item -Force
Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
$script:proc = Start-Process $Exe -PassThru
# A throwaway window with a text box (it has a right-click menu). Notepad on Windows 11 is a store app whose
# process can't be tracked reliably, and it might be the user's own open Notepad.
$targetScript = Join-Path $OutDir 'target.ps1'
Set-Content -Encoding utf8 $targetScript @'
Add-Type -AssemblyName System.Windows.Forms
$f = New-Object System.Windows.Forms.Form
$f.Text = 'Snip E2E target'
$t = New-Object System.Windows.Forms.TextBox
$t.Multiline = $true; $t.Dock = 'Fill'; $t.Text = 'Snip end-to-end test window'
$f.Controls.Add($t)
[System.Windows.Forms.Application]::Run($f)
'@
$script:notepad = Start-Process powershell -ArgumentList '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass', '-File', $targetScript -PassThru -WindowStyle Normal
$np = [IntPtr]::Zero
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($np -eq [IntPtr]::Zero -and $sw.ElapsedMilliseconds -lt 10000) { Start-Sleep -Milliseconds 100; $np = [In]::FindWindow([NullString]::Value, 'Snip E2E target') }
if ($np -eq [IntPtr]::Zero) { Fail 'test window not found' }
$p = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
[void][In]::MoveWindow($np, $p.X + 700, $p.Y + 250, 900, 600, $true)
[void][In]::SetForegroundWindow($np)
Start-Sleep -Milliseconds 400

foreach ($hotkey in 'PrtSc', 'WinShiftS') {
    Write-Host "--- context menu + $hotkey"
    [In]::SetCursorPos($p.X + 1000, $p.Y + 500) | Out-Null
    [In]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); [In]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    $menuClass = ForegroundClass
    Trigger $hotkey
    $ms = WaitOverlay
    Write-Host "overlay after $ms ms (foreground before: $menuClass)"
    Start-Sleep -Milliseconds 300
    Shot "menu-$hotkey.png"
    [In]::Tap(0x1B)
    Start-Sleep -Milliseconds 400
    [In]::Tap(0x1B)
    Start-Sleep -Milliseconds 300
}

Write-Host '--- click selects window'
[void][In]::SetForegroundWindow($np)
Start-Sleep -Milliseconds 300
$r = New-Object In+RECT
[void][In]::DwmGetWindowAttribute($np, 9, [ref]$r, 16)
$expected = "$($r.R - $r.L) x $($r.B - $r.T)"
[System.Windows.Forms.Clipboard]::Clear()
Trigger 'PrtSc'
WaitOverlay | Out-Null
Start-Sleep -Milliseconds 200
[In]::Click($p.X + 1100, $p.Y + 500)
Start-Sleep -Milliseconds 200
Shot 'window-selected.png'
Ctrl 0x43
Start-Sleep -Milliseconds 500
$img = [System.Windows.Forms.Clipboard]::GetImage()
if (-not $img) { Fail 'no clipboard image after window click' }
$got = "$($img.Width) x $($img.Height)"
Write-Host "window frame $expected, clipboard $got"
if ($got -ne $expected) { Fail 'window selection size mismatch' }

Write-Host '--- save dialog'
$first = Join-Path $saveDir 'first.png'
Trigger 'PrtSc'
WaitOverlay | Out-Null
[In]::Drag($p.X + 200, $p.Y + 200, $p.X + 500, $p.Y + 400)
Ctrl 0x53
Start-Sleep -Milliseconds 1200
Write-Host "dialog foreground class: $(ForegroundClass)"
Shot 'save-dialog.png'
[System.Windows.Forms.SendKeys]::SendWait('^a')
[System.Windows.Forms.SendKeys]::SendWait($first)
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Milliseconds 800
if (-not (Test-Path $first)) { Fail "file not saved: $first" }
if ((Overlay) -ne [IntPtr]::Zero) { Fail 'overlay still open after save' }
$saved = [System.Drawing.Image]::FromFile($first); Write-Host "saved $first ($($saved.Width) x $($saved.Height))"; $saved.Dispose()
$settings = Get-Content (Join-Path $env:APPDATA 'Snip\settings.json') -Raw | ConvertFrom-Json
Write-Host "LastSaveDirectory = $($settings.LastSaveDirectory)"

Trigger 'PrtSc'
WaitOverlay | Out-Null
[In]::Drag($p.X + 200, $p.Y + 200, $p.X + 300, $p.Y + 300)
Ctrl 0x53
Start-Sleep -Milliseconds 1200
[System.Windows.Forms.SendKeys]::SendWait('^a')
[System.Windows.Forms.SendKeys]::SendWait('second.jpg')
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Milliseconds 800
$second = Join-Path $saveDir 'second.jpg'
if (-not (Test-Path $second)) { Fail "second save did not default to last folder ($second missing)" }
Write-Host "second save landed in remembered folder: $second"

Cleanup
Write-Host 'PASS'
