# Builds Keyturn from this checkout and installs it for the current user (no administrator rights needed).
#
#   .\install.ps1              build and install, and start the background watcher
#   .\install.ps1 -Uninstall   uninstall
#
# Installs keyturn.exe and Keyturn.App.exe into %LOCALAPPDATA%\Programs\Keyturn, adds that folder to your user PATH,
# and creates a Start Menu shortcut "Keyturn" (with the logo) that opens the manage window. It also sets up the
# background watcher that pops the prompt up when a secret expires (a Task Scheduler task that starts at logon) and
# starts it. Your secrets and config are never touched. Prefer a prebuilt binary? Download the Windows archive from the Releases page.
param(
    [string]$Configuration = "Release",
    [switch]$Uninstall,
    [switch]$Help
)

if ($Help) {
    Write-Output "Usage: .\install.ps1 [-Configuration <config>] [-Uninstall] [-Help]"
    Write-Output ""
    Write-Output "Options:"
    Write-Output "  -Configuration  Build configuration (default: Release)"
    Write-Output "  -Uninstall      Uninstall"
    Write-Output "  -Help           Show this help message"
    Write-Output ""
    Write-Output "Examples:"
    Write-Output "  .\install.ps1"
    Write-Output "  .\install.ps1 -Uninstall"
    exit 0
}

$ErrorActionPreference = 'Stop'

# One-line errors instead of a PowerShell stack trace (the temp-folder cleanup below still runs first).
trap {
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    exit 1
}

function Say([string]$Message) { Write-Information -MessageData $Message -InformationAction Continue }

$version = "0.0.0-src"
$installDir = Join-Path (Join-Path $env:LOCALAPPDATA "Programs") "Keyturn"
$cliExe = Join-Path $installDir "keyturn.exe"
$appExe = Join-Path $installDir "Keyturn.App.exe"
$programs = [Environment]::GetFolderPath("Programs")
$shortcut = if ($programs) { Join-Path $programs "Keyturn.lnk" } else { $null }

# Remove (or check for) the install folder as one exact entry of a PATH value, never a substring match.
function Get-PathEntry([string]$PathValue) {
    @($PathValue -split ";" | Where-Object { $_ })
}

if ($Uninstall) {
    # Stop the watcher and remove it from the login items first, while the CLI that knows how still exists.
    if (Test-Path -LiteralPath $cliExe) {
        try { & $cliExe watch uninstall 2>&1 | Out-Null } catch { Write-Verbose "no watcher to remove: $_" }
    }
    if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }
    if ($shortcut -and (Test-Path -LiteralPath $shortcut)) { Remove-Item -LiteralPath $shortcut -Force }

    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath) {
        $kept = Get-PathEntry $userPath | Where-Object { $_.TrimEnd("\") -ine $installDir }
        [Environment]::SetEnvironmentVariable("Path", ($kept -join ";"), "User")
    }
    Say "Uninstalled Keyturn. Your secrets and config are untouched."
    exit 0 # not "return": the last native command above may have left a non-zero $LASTEXITCODE for the caller to trip over
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "the .NET 10 SDK is required: https://dotnet.microsoft.com/download"
}

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$rid = switch ($arch) {
    "X64"   { "win-x64" }
    "Arm64" { "win-arm64" }
    default { throw "unsupported CPU architecture '$arch'" }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("keyturn-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    foreach ($project in "Keyturn.Cli", "Keyturn.App") {
        Say "Building $project..."
        $csproj = Join-Path (Join-Path (Join-Path $PSScriptRoot "src") $project) "$project.csproj"
        dotnet publish $csproj -c $Configuration -r $rid "-p:PublishProfile=release" "-p:Version=$version" --nologo -v q -o (Join-Path $tmp $project)
        if ($LASTEXITCODE -ne 0) { throw "building $project failed" }
    }

    # A running watcher would keep running the old version after an upgrade, so stop it before replacing anything.
    if (Test-Path -LiteralPath $cliExe) {
        try { & $cliExe watch stop 2>&1 | Out-Null } catch { Write-Verbose "no watcher to stop: $_" }
    }

    # A running exe can be renamed but not overwritten: move the old one aside, then copy the new one in.
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    foreach ($pair in @(@("Keyturn.Cli", "keyturn.exe", $cliExe), @("Keyturn.App", "Keyturn.App.exe", $appExe))) {
        $source = Join-Path (Join-Path $tmp $pair[0]) $pair[1]
        $destination = $pair[2]
        if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination "$destination.old" -Force }
        Copy-Item -LiteralPath $source -Destination $destination -Force
        Remove-Item -LiteralPath "$destination.old" -Force -ErrorAction SilentlyContinue
    }
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# Start Menu shortcut; its icon comes from the one embedded in Keyturn.App.exe.
if ($shortcut) {
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $link.TargetPath = $appExe
    $link.Arguments = "--manage"
    $link.WorkingDirectory = $installDir
    $link.IconLocation = "$appExe,0"
    $link.Description = "Rotating secrets in your OS credential store"
    $link.Save()
}

$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (@(Get-PathEntry $userPath | Where-Object { $_.TrimEnd("\") -ieq $installDir }).Count -eq 0) {
    $entries = @(Get-PathEntry $userPath) + $installDir
    [Environment]::SetEnvironmentVariable("Path", ($entries -join ";"), "User")
    Say "Added $installDir to your PATH (open a new terminal for it to take effect)."
}
else {
    Say "$installDir is already on your PATH."
}

# Set up the watcher (a logon task) and start it. Not fatal: Keyturn works without it, it just can't prompt by itself.
$watcherSetUp = $false
try {
    & $cliExe watch install 2>&1 | ForEach-Object { Say "$_" }
    $watcherSetUp = ($LASTEXITCODE -eq 0)
}
catch { Write-Warning "couldn't set up the background watcher: $_" }
if (-not $watcherSetUp) { Write-Warning "couldn't set up the background watcher. Try again later with: keyturn watch install" }

Say ""
Say "Keyturn installed:"
Say "  $cliExe"
Say "  $appExe (and a `"Keyturn`" Start Menu shortcut)"
Say ""
if ($watcherSetUp) { Say "The background watcher is set up: Keyturn will pop up as soon as a secret expires. See it with: keyturn watch status" }
Say "Next: keyturn add vpn daily@06:00 `"Corporate VPN password`"   (see the README's Quickstart)"

# Reaching here means the install worked. A failed watcher setup only warns, but it would leave a non-zero
# $LASTEXITCODE behind that CI (and anything else checking it) would read as a failed install.
exit 0
