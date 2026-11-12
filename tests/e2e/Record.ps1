# Screen recording flow: Win+Shift+R, drag an area over the live screen, Enter; stop with Win+Shift+R and Copy the file;
# Discard from the bar; Stop button and the discard prompt; a recording with system sound (S key; never the
# microphone). Drives the real mouse and keyboard; assumes 100% scaling on the primary display.
# Usage: powershell -STA -File tests\e2e\Record.ps1 -Exe <Snip.exe> -OutDir <dir>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Input.ps1')
function Cleanup { if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill() } }
$tempDir = Join-Path ([IO.Path]::GetTempPath()) 'Snip'
function Mp4s { @(Get-ChildItem $tempDir -Filter *.mp4 -ErrorAction SilentlyContinue) }
function Visible([string] $title) {
    $h = [In]::FindWindow([NullString]::Value, $title)
    if ($h -ne [IntPtr]::Zero -and [In]::IsWindowVisible($h)) { $h } else { [IntPtr]::Zero }
}
function ResultWindow { Visible 'Snip recording' }
function RecordSelect { Visible 'Snip record' }
function WaitFor([scriptblock] $cond, [string] $what, [int] $ms = 5000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $cond)) { if ($sw.ElapsedMilliseconds -gt $ms) { Fail "timed out waiting for $what" }; Start-Sleep -Milliseconds 50 }
}
function WinShiftR { [In]::Key(0x5B, $false); [In]::Key(0x10, $false); [In]::Tap(0x52); [In]::Key(0x10, $true); [In]::Key(0x5B, $true) }

function Select-Area {
    WinShiftR
    WaitFor { (RecordSelect) -ne [IntPtr]::Zero } 'the record selection overlay'
    Start-Sleep -Milliseconds 400
    [In]::Drag($x0, $y0, $x0 + 640, $y0 + 400)
    Start-Sleep -Milliseconds 300
}
function Start-Recording([string] $label) {
    Select-Area
    [In]::Tap(0x0D)
    WaitFor { (RecordSelect) -eq [IntPtr]::Zero } 'the selection overlay to close'
    Start-Sleep -Milliseconds 800
    Write-Host "ok: recording - $label"
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
$script:proc = Start-Process $Exe -PassThru
Start-Sleep -Milliseconds 1500
$p = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$x0 = $p.X + 600; $y0 = $p.Y + 300

# 1. Record, move the pointer, stop with Win+Shift+R, Copy.
Start-Recording 'stop with Win+Shift+R'
for ($i = 0; $i -lt 20; $i++) { [void][In]::SetCursorPos($x0 + 100 + $i * 20, $y0 + 200); Start-Sleep -Milliseconds 100 }
WinShiftR
WaitFor { (ResultWindow) -ne [IntPtr]::Zero } 'result window'
Start-Sleep -Milliseconds 500
if ([In]::GetForegroundWindow() -ne (ResultWindow)) { Fail "result window not in front ($(ForegroundClass))" }
Ctrl 0x43
WaitFor { (ResultWindow) -eq [IntPtr]::Zero } 'result window to close after Ctrl+C'
$files = [System.Windows.Forms.Clipboard]::GetFileDropList()
if ($files.Count -ne 1 -or -not $files[0].EndsWith('.mp4')) { Fail "clipboard has no mp4 file ($($files.Count) files)" }
$f = Get-Item $files[0]
$head = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($f.FullName), 4, 4)
if ($head -ne 'ftyp') { Fail "copied file isn't an MP4 ($head)" }
Write-Host ("ok: copied {0} ({1:N0} KB)" -f $f.Name, ($f.Length / 1KB))

# 2. Esc in the selection overlay records nothing.
Select-Area
[In]::Tap(0x1B)
WaitFor { (RecordSelect) -eq [IntPtr]::Zero } 'the selection overlay to close on Esc'
Start-Sleep -Milliseconds 500
if ((ResultWindow) -ne [IntPtr]::Zero) { Fail 'Esc started a recording' }
Write-Host 'ok: Esc cancels the selection'

# 3. Discard from the bar: no result window, file removed.
$count = (Mp4s).Count
Start-Recording 'discard from the bar'
[In]::Click($x0 + 640 - 18, $y0 + 400 + 8 + 18)
Start-Sleep -Milliseconds 1200
if ((ResultWindow) -ne [IntPtr]::Zero) { Fail 'result window appeared after Discard' }
if ((Mp4s).Count -ne $count) { Fail 'discarded recording left a file behind' }
Write-Host 'ok: discarded, no file left'

# 4. Stop button, then closing asks first; No keeps it, Yes deletes it.
Start-Recording 'stop with the bar button'
[In]::Click($x0 + 590, $y0 + 426)
WaitFor { (ResultWindow) -ne [IntPtr]::Zero } 'result window after Stop'
Start-Sleep -Milliseconds 400
if ([In]::GetForegroundWindow() -ne (ResultWindow)) { Fail "result window not in front after Stop ($(ForegroundClass))" }
[In]::Tap(0x1B)
Start-Sleep -Milliseconds 600
[In]::Tap(0x0D)
Start-Sleep -Milliseconds 400
if ((ResultWindow) -eq [IntPtr]::Zero) { Fail 'result window closed although the prompt was answered No' }
Write-Host 'ok: No keeps the window'
[In]::Tap(0x1B)
Start-Sleep -Milliseconds 600
[In]::Tap(0x59)
WaitFor { (ResultWindow) -eq [IntPtr]::Zero } 'result window to close after Yes'
if ((Mp4s).Count -ne $count) { Fail 'recording discarded via the prompt left a file behind' }
Write-Host 'ok: Yes discards'

# 5. System sound on (S), the MP4 gets an AAC track; switched off again after.
Select-Area
[In]::Tap(0x53)
[In]::Tap(0x0D)
WaitFor { (RecordSelect) -eq [IntPtr]::Zero } 'the selection overlay to close'
Start-Sleep -Milliseconds 2000
WinShiftR
WaitFor { (ResultWindow) -ne [IntPtr]::Zero } 'result window (with sound)'
Start-Sleep -Milliseconds 400
Ctrl 0x43
WaitFor { (ResultWindow) -eq [IntPtr]::Zero } 'result window to close'
$files = [System.Windows.Forms.Clipboard]::GetFileDropList()
$text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($files[0]))
if (-not $text.Contains('mp4a')) { Fail 'recording with system sound has no AAC track' }
Write-Host 'ok: system sound track recorded'
Select-Area
[In]::Tap(0x53)
[In]::Tap(0x1B)
WaitFor { (RecordSelect) -eq [IntPtr]::Zero } 'the selection overlay to close'
Write-Host 'ok: system sound switched off again'

Cleanup
Write-Host 'PASS'
