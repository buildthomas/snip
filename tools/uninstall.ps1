# Removes Snip, its startup entry and its settings.
Get-Process Snip -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run -Name Snip -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\Snip') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Snip') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Snip removed.'
