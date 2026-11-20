param(
    [string]$Destination = $(Join-Path $env:LOCALAPPDATA "DockCommander"),
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"
$InstallManifestName = "DockCommander.install.json"

function Assert-InstallDestination {
    param([string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $root = [System.IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ($full -eq $root) { throw "Refusing to use a drive root: $full" }
    $blocked = @($env:USERPROFILE, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:WINDIR) |
        Where-Object { $_ } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_).TrimEnd('\') }
    if ($blocked -contains $full) { throw "Refusing to use a system or profile directory: $full" }
    return $full
}

function Resolve-RecordedPath {
    param([string]$Directory, [string]$Relative)
    $normalized = $Relative -replace '/', '\'
    return [System.IO.Path]::GetFullPath((Join-Path $Directory $normalized))
}

function Read-InstallManifest {
    param([string]$Directory)
    $manifestPath = Join-Path $Directory $InstallManifestName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "This folder has no DockCommander install manifest. Nothing was removed: $Directory"
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.product -ne "DockCommander") {
        throw "The manifest is not for DockCommander. Nothing was removed: $Directory"
    }
    $files = @($manifest.files)
    foreach ($relative in $files) {
        if ([string]::IsNullOrWhiteSpace($relative) -or [System.IO.Path]::IsPathRooted($relative) -or ($relative -split '[\\/]' | Where-Object { $_ -eq ".." })) {
            throw "The manifest contains an unsafe path. Nothing was removed: $relative"
        }
        $candidate = Resolve-RecordedPath $Directory $relative
        if (-not $candidate.StartsWith($Directory + "\", [System.StringComparison]::OrdinalIgnoreCase) -and
            -not $candidate.Equals($Directory, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The manifest escapes the install folder. Nothing was removed: $relative"
        }
    }
    return $files
}

function Remove-RecordedFiles {
    param([string]$Directory, [string[]]$Files)
    foreach ($relative in $Files) {
        $candidate = Resolve-RecordedPath $Directory $relative
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { Remove-Item -LiteralPath $candidate -Force }
    }
    do {
        $removedDirectory = $false
        $children = @(Get-ChildItem -LiteralPath $Directory -Recurse -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_ })
        foreach ($child in $children) {
            $nested = @(Get-ChildItem -LiteralPath $child.FullName -Force -ErrorAction SilentlyContinue | Where-Object { $_ })
            if ($nested.Count -eq 0) {
                Remove-Item -LiteralPath $child.FullName -Force
                $removedDirectory = $true
            }
        }
    } while ($removedDirectory)
    if ((Test-Path -LiteralPath $Directory) -and -not (Get-ChildItem -LiteralPath $Directory -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -LiteralPath $Directory -Force
    }
}

function Remove-InstallShortcut {
    param([string]$Directory)
    $shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "DockCommander.lnk"
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { return }
    $shell = New-Object -ComObject WScript.Shell
    $target = [System.IO.Path]::GetFullPath($shell.CreateShortcut($shortcut).TargetPath)
    $expected = [System.IO.Path]::GetFullPath((Join-Path $Directory "DC.exe"))
    if ($target -eq $expected) { Remove-Item -LiteralPath $shortcut -Force }
}

function Uninstall-DockCommander {
    param([string]$Path)
    $full = Assert-InstallDestination $Path
    $files = Read-InstallManifest $full
    Remove-RecordedFiles $full $files
    if (Test-Path -LiteralPath $full) { Remove-InstallShortcut $full }
    else { Remove-InstallShortcut $Path }
    Write-Host "Removed the DockCommander files recorded in $Path"
}

if ($SelfTest) {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("dc-uninstall-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "DockCommander.lnk"
    $savedShortcut = $null
    if (Test-Path -LiteralPath $shortcut) { $savedShortcut = [System.IO.File]::ReadAllBytes($shortcut) }
    try {
        $installed = Join-Path $temp "DockCommander"
        New-Item -ItemType Directory -Path (Join-Path $installed "lib") -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $installed "DC.exe") -Value "app"
        Set-Content -LiteralPath (Join-Path $installed "lib\app.dll") -Value "lib"
        Set-Content -LiteralPath (Join-Path $installed "keep.txt") -Value "keep"
        $json = '{"product":"DockCommander","files":["DC.exe","lib/app.dll","DockCommander.install.json"]}'
        Set-Content -LiteralPath (Join-Path $installed $InstallManifestName) -Value $json -Encoding ascii
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        $link.TargetPath = Join-Path $installed "DC.exe"
        $link.Save()
        Uninstall-DockCommander $installed
        if (-not (Test-Path -LiteralPath (Join-Path $installed "keep.txt"))) { throw "A file outside the manifest was removed." }
        if (Test-Path -LiteralPath (Join-Path $installed "DC.exe")) { throw "A recorded file was left behind." }
        if (Test-Path -LiteralPath (Join-Path $installed "lib")) { throw "An empty installed directory was left behind." }
        if (Test-Path -LiteralPath $shortcut) { throw "The shortcut for this installation was left behind." }
        $stranger = Join-Path $temp "stranger"
        New-Item -ItemType Directory -Path $stranger -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $stranger "DC.exe") -Value "not ours"
        $failed = $false
        try { Uninstall-DockCommander $stranger } catch { $failed = $true }
        if (-not $failed) { throw "A folder without a manifest was removed." }
        if ((Get-Content -LiteralPath (Join-Path $stranger "DC.exe") -Raw) -notmatch "not ours") { throw "An unrecorded DC.exe was changed." }
        $unsafe = Join-Path $temp "unsafe"
        New-Item -ItemType Directory -Path $unsafe -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $unsafe "keep.txt") -Value "keep"
        Set-Content -LiteralPath (Join-Path $unsafe $InstallManifestName) -Value '{"product":"DockCommander","files":["../keep-outside.txt"]}' -Encoding ascii
        $failed = $false
        try { Uninstall-DockCommander $unsafe } catch { $failed = $true }
        if (-not $failed -or -not (Test-Path -LiteralPath (Join-Path $unsafe "keep.txt"))) { throw "An unsafe manifest removed a file." }
        $otherLink = $shell.CreateShortcut($shortcut)
        $otherLink.TargetPath = Join-Path $temp "somewhere-else.exe"
        $otherLink.Save()
        Remove-InstallShortcut $installed
        if (-not (Test-Path -LiteralPath $shortcut)) { throw "A shortcut for another program was removed." }
        Remove-Item -LiteralPath $shortcut -Force
    }
    catch {
        Write-Host $_.Exception.Message
        exit 1
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
        if ($null -ne $savedShortcut) { [System.IO.File]::WriteAllBytes($shortcut, $savedShortcut) }
        elseif (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
    }
    exit 0
}

Uninstall-DockCommander $Destination
