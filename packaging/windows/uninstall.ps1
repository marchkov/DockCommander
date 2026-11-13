param(
    [string]$Destination = $(Join-Path $env:LOCALAPPDATA "DockCommander")
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $Destination "DC.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "DC.exe was not found in $Destination. Pass -Destination if DockCommander was installed elsewhere."
}
Remove-Item -LiteralPath $Destination -Recurse -Force
$shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "DockCommander.lnk"
if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
Write-Host "Removed $Destination"
