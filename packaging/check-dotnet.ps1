param(
    [switch]$SelfTest,
    [string]$Listing,
    [string]$SharedRoot,
    [string]$DotnetPath
)

function Test-DotNet10Runtime {
    param([string]$Text, [string]$Root, [string]$RuntimePath)
    $listed = $false
    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -match '^Microsoft\.NETCore\.App\s+10\.') { $listed = $true }
    }
    $folder = $false
    if ($Root -and (Test-Path -LiteralPath $Root)) {
        $match = Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '10.*' }
        if ($match) { $folder = $true }
    }
    $x64Listing = $listed -and $RuntimePath -and ($RuntimePath -notlike '* (x86)*')
    return [bool]($folder -or $x64Listing)
}

if ($SelfTest) {
    if (Test-DotNet10Runtime -Text "Microsoft.NETCore.App 8.0.11 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]" -RuntimePath "C:\Program Files\dotnet\dotnet.exe") { exit 1 }
    if (-not (Test-DotNet10Runtime -Text "Microsoft.NETCore.App 10.0.1 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]" -RuntimePath "C:\Program Files\dotnet\dotnet.exe")) { exit 1 }
    if (Test-DotNet10Runtime -Text "Microsoft.NETCore.App 10.0.1 [C:\Program Files (x86)\dotnet\shared\Microsoft.NETCore.App]" -RuntimePath "C:\Program Files (x86)\dotnet\dotnet.exe") { exit 1 }
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("dc-runtime-" + [guid]::NewGuid().ToString("n"))
    New-Item -ItemType Directory -Path (Join-Path $temp "10.0.1") -Force | Out-Null
    try {
        if (-not (Test-DotNet10Runtime -Text "" -Root $temp)) { exit 1 }
    }
    finally { Remove-Item -LiteralPath $temp -Recurse -Force }
    exit 0
}

if (-not [Environment]::Is64BitOperatingSystem) {
    Write-Host "DockCommander needs 64-bit Windows and the .NET 10 x64 runtime."
    exit 1
}

$listingText = $Listing
$runtimePath = $DotnetPath
if (-not $PSBoundParameters.ContainsKey("Listing")) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $runtimePath = $dotnet.Source
        $listingText = (& dotnet --list-runtimes 2>$null | Out-String)
    }
}
$root = if ($SharedRoot) { $SharedRoot } elseif ($env:ProgramFiles) { Join-Path $env:ProgramFiles "dotnet\shared\Microsoft.NETCore.App" } else { "" }
if (Test-DotNet10Runtime -Text $listingText -Root $root -RuntimePath $runtimePath) { exit 0 }

Write-Host "DockCommander needs the .NET 10 x64 runtime (Microsoft.NETCore.App 10)."
Write-Host "Install the .NET 10 Desktop Runtime x64: https://dotnet.microsoft.com/download/dotnet/10.0"
exit 1
