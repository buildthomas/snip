# Screen recording flow: Record button, stop by hotkey, Copy as file; Discard from the bar; Stop button and the
# discard prompt. Drives the real mouse and keyboard; assumes 100% scaling on the primary display.
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
function ResultWindow {
    $h = [In]::FindWindow([NullString]::Value, 'Snip recording')
    if ($h -ne [IntPtr]::Zero -and [In]::IsWindowVisible($h)) { $h } else { [IntPtr]::Zero }
}
function WaitFor([scriptblock] $cond, [string] $what, [int] $ms = 5000) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $cond)) { if ($sw.ElapsedMilliseconds -gt $ms) { Fail "timed out waiting for $what" }; Start-Sleep -Milliseconds 50 }
}

function Find-RecordButton {
    $bmp = New-Object System.Drawing.Bitmap 760, 80
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x0 - 60, $y0 + 400, 0, 0, $bmp.Size)
    $g.Dispose()
    try {
        for ($y = 0; $y -lt 80; $y += 2) { for ($x = 0; $x -lt 760; $x += 2) {
            $c = $bmp.GetPixel($x, $y)
            if ($c.R -eq 230 -and $c.G -eq 40 -and $c.B -eq 40) { return [Drawing.Point]::new($x0 - 60 + $x + 3, $y0 + 400 + $y + 3) }
        } }
    } finally { $bmp.Dispose() }
    Fail 'Record button not found'
}

function Start-Recording([string] $label) {
    Start-Process $Exe -ArgumentList '--capture' | Out-Null
    [void](WaitOverlay)
    Start-Sleep -Milliseconds 300
    [In]::Drag($x0, $y0, $x0 + 640, $y0 + 400)
    Start-Sleep -Milliseconds 200
    $p = Find-RecordButton
    [In]::Click($p.X, $p.Y)
    WaitFor { (Overlay) -eq [IntPtr]::Zero } 'overlay to close'
    Start-Sleep -Milliseconds 800
    Write-Host "ok: recording - $label"
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
$script:proc = Start-Process $Exe -PassThru
Start-Sleep -Milliseconds 1500
$p = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$x0 = $p.X + 600; $y0 = $p.Y + 300
$before = (Mp4s).Count

# 1. Record, move the pointer, stop with the hotkey, Copy.
Start-Recording 'stop with hotkey'
for ($i = 0; $i -lt 20; $i++) { [void][In]::SetCursorPos($x0 + 100 + $i * 20, $y0 + 200); Start-Sleep -Milliseconds 100 }
Trigger 'WinShiftS'
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
Copy-Item $f.FullName (Join-Path $OutDir 'rec-copied.mp4')

# 2. Discard from the bar: no result window, file removed.
$count = (Mp4s).Count
Start-Recording 'discard from the bar'
[In]::Click($x0 + 640 - 18, $y0 + 400 + 8 + 18)
Start-Sleep -Milliseconds 1200
if ((ResultWindow) -ne [IntPtr]::Zero) { Fail 'result window appeared after Discard' }
if ((Mp4s).Count -ne $count) { Fail 'discarded recording left a file behind' }
Write-Host 'ok: discarded, no file left'

# 3. Stop button, then closing asks first; No keeps it, Yes deletes it.
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

Cleanup
Write-Host 'PASS'
