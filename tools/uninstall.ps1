# Removes Snip completely: the app, Start menu shortcut, startup entry, Installed apps entry and settings.
# Also installed next to Snip.exe, where Settings > Apps > Installed apps runs it to uninstall.
$target = Join-Path $env:LOCALAPPDATA 'Programs\Snip'
Set-Location $env:TEMP

# Files can stay locked for a moment after Snip exits (or while antivirus scans them), so retry briefly.
function Remove-WithRetry([string] $path) {
    for ($i = 0; $i -lt 10 -and (Test-Path $path); $i++) {
        try { Remove-Item $path -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 250 }
    }
    if (Test-Path $path) { Write-Warning "Could not remove $path" }
}

Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process Snip -ErrorAction SilentlyContinue | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
Remove-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name Snip -ErrorAction SilentlyContinue
Remove-WithRetry (Join-Path ([Environment]::GetFolderPath('Programs')) 'Snip.lnk')
Remove-WithRetry 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Snip'
Remove-WithRetry $target
Remove-WithRetry (Join-Path $env:APPDATA 'Snip')
Remove-WithRetry (Join-Path ([IO.Path]::GetTempPath()) 'Snip')
Write-Host 'Snip removed.'
