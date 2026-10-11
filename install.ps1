<#
.SYNOPSIS
    Builds Keyturn from this checkout and installs it for the current user (no administrator rights needed).

.DESCRIPTION
    Installs keyturn.exe and Keyturn.App.exe into the install folder, adds that folder to your user PATH, and creates a
    Start Menu shortcut "Keyturn" (with the logo) that opens the manage window. Your secrets and config are never
    touched. Prefer a prebuilt binary? Download the Windows archive from the Releases page instead.

.PARAMETER Prefix
    Install folder. Default: %LOCALAPPDATA%\Programs\Keyturn.

.PARAMETER Schedule
    Also run `keyturn schedule install` (the prompt appears at logon and hourly, via Task Scheduler).

.PARAMETER NoShortcut
    Don't create the Start Menu shortcut.

.PARAMETER Jit
    Build the CLI without NativeAOT (needs only the .NET SDK; larger and slower to start). This is also the automatic
    fallback when the Visual Studio C++ build tools are not installed.

.PARAMETER Uninstall
    Remove everything a previous install created.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Schedule
    .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Prefix,
    [switch]$Schedule,
    [switch]$NoShortcut,
    [switch]$Jit,
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# One-line errors instead of a PowerShell stack trace. (A try/finally below still runs first, so temp files are cleaned up.)
trap {
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    exit 1
}

$root = $PSScriptRoot
if (-not $Prefix) { $Prefix = Join-Path $env:LOCALAPPDATA 'Programs\Keyturn' }
$manifestPath = Join-Path $Prefix 'install-manifest.json'
$cliExe = Join-Path $Prefix 'keyturn.exe'
$appExe = Join-Path $Prefix 'Keyturn.App.exe'
$programsFolder = [Environment]::GetFolderPath('Programs')
$shortcutPath = if ($programsFolder) { Join-Path $programsFolder 'Keyturn.lnk' } else { $null }

function Write-Step([string]$Message) { Write-Information -MessageData $Message -InformationAction Continue }
function Fail([string]$Message) { throw $Message }

# Runs a native command and returns its exit code. Native stderr output must not trip $ErrorActionPreference='Stop'.
function Invoke-Native([string]$File, [string[]]$Arguments, [string]$LogPath) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $File @Arguments *> $LogPath
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Test-FolderOnPath([string]$PathValue, [string]$Folder) {
    $wanted = $Folder.TrimEnd('\')
    foreach ($entry in ($PathValue -split ';')) {
        if ($entry.TrimEnd('\') -ieq $wanted) { return $true }
    }
    return $false
}

# ---- uninstall -------------------------------------------------------------------------------------------------
if ($Uninstall) {
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        Fail "no install record at $manifestPath. Nothing to uninstall (or it was installed another way)."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

    # Remove the scheduled prompt first, while the CLI that knows how still exists.
    if ($manifest.schedule -and (Test-Path -LiteralPath $cliExe)) {
        & $cliExe schedule uninstall
        if ($LASTEXITCODE -ne 0) { Write-Warning "could not remove the scheduled prompt; run 'keyturn schedule uninstall' yourself" }
    }

    foreach ($file in @($manifest.files)) {
        if (Test-Path -LiteralPath $file) {
            Remove-Item -LiteralPath $file -Force
            Write-Step "removed $file"
        }
    }
    if ($manifest.shortcut -and (Test-Path -LiteralPath $manifest.shortcut)) {
        Remove-Item -LiteralPath $manifest.shortcut -Force
        Write-Step "removed $($manifest.shortcut)"
    }
    if ($manifest.pathAdded) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath) {
            $kept = @($userPath -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ine $Prefix.TrimEnd('\')) })
            [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
            Write-Step "removed $Prefix from your user PATH"
        }
    }
    Remove-Item -LiteralPath $manifestPath -Force
    if ((Test-Path -LiteralPath $Prefix) -and -not (Get-ChildItem -LiteralPath $Prefix -Force)) {
        Remove-Item -LiteralPath $Prefix -Force
    }
    Write-Step "Uninstalled. Your secrets and config are untouched (config: $env:APPDATA\keyturn)."
    return
}

# ---- prerequisites ---------------------------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail '.NET SDK not found. Install the .NET 10 SDK: https://dotnet.microsoft.com/download'
}
$sdk = (& dotnet --version).Trim()
if ([int]($sdk.Split('.')[0]) -lt 10) { Fail ".NET SDK $sdk is too old; Keyturn needs 10 or newer." }

switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()) {
    'X64'   { $rid = 'win-x64';   $vcComponent = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    'Arm64' { $rid = 'win-arm64'; $vcComponent = 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }
    default { Fail "unsupported CPU architecture '$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture)'." }
}

# The NativeAOT CLI needs the Visual Studio C++ build tools. Without them, fall back to the self-contained build.
$cliProfile = 'release'
if ($Jit) {
    $cliProfile = 'jit'
}
else {
    $hasVcTools = $false
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ($programFilesX86) {
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $found = & $vswhere -latest -products '*' -requires $vcComponent -property installationPath
            if ($found) { $hasVcTools = $true }
        }
    }
    if (-not $hasVcTools) {
        $cliProfile = 'jit'
        Write-Warning 'the Visual Studio C++ build tools are not installed, which the NativeAOT build needs.'
        Write-Warning 'falling back to a self-contained CLI (~15 MB, ~70 ms startup instead of ~3 MB and ~3 ms).'
        Write-Warning 'for the small, fast build, install the "Desktop development with C++" workload from the Visual Studio Installer, then run this again.'
    }
}

# ---- version ---------------------------------------------------------------------------------------------------
$version = '0.0.0-src'
if (Get-Command git -ErrorAction SilentlyContinue) {
    $inRepo = (& git -C $root rev-parse --git-dir 2>$null)
    if ($LASTEXITCODE -eq 0 -and $inRepo) {
        $tag = (& git -C $root describe --tags --exact-match 2>$null)
        if ($LASTEXITCODE -eq 0 -and $tag -match '^v?(\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?)$') {
            $version = $Matches[1]
        }
        else {
            $sha = (& git -C $root rev-parse --short HEAD 2>$null)
            if ($LASTEXITCODE -eq 0 -and $sha) { $version = "0.0.0-src.$sha" }
        }
    }
}

# ---- build -----------------------------------------------------------------------------------------------------
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("keyturn-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Step "Building Keyturn $version for $rid (CLI: $cliProfile)..."

    $log = Join-Path $tmp 'cli.log'
    $code = Invoke-Native -File 'dotnet' -Arguments @('publish', (Join-Path (Join-Path $root 'src') 'Keyturn.Cli'), '-r', $rid, "-p:PublishProfile=$cliProfile", "-p:Version=$version", '--nologo', '-v', 'q', '-o', (Join-Path $tmp 'cli')) -LogPath $log
    if ($code -ne 0) { Get-Content -LiteralPath $log | ForEach-Object { Write-Step $_ }; Fail 'building the CLI failed.' }

    $log = Join-Path $tmp 'app.log'
    $code = Invoke-Native -File 'dotnet' -Arguments @('publish', (Join-Path (Join-Path $root 'src') 'Keyturn.App'), '-r', $rid, '-p:PublishProfile=release', "-p:Version=$version", '--nologo', '-v', 'q', '-o', (Join-Path $tmp 'app')) -LogPath $log
    if ($code -ne 0) { Get-Content -LiteralPath $log | ForEach-Object { Write-Step $_ }; Fail 'building the app failed.' }

    # ---- install -----------------------------------------------------------------------------------------------
    New-Item -ItemType Directory -Force -Path $Prefix | Out-Null

    # A running exe can be renamed but not overwritten: move the old one aside, then copy the new one in.
    function Install-File([string]$Source, [string]$Destination) {
        if (Test-Path -LiteralPath $Destination) {
            $aside = "$Destination.old"
            if (Test-Path -LiteralPath $aside) { Remove-Item -LiteralPath $aside -Force -ErrorAction SilentlyContinue }
            Move-Item -LiteralPath $Destination -Destination $aside -Force
        }
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
        Remove-Item -LiteralPath "$Destination.old" -Force -ErrorAction SilentlyContinue
    }

    Install-File (Join-Path (Join-Path $tmp 'cli') 'keyturn.exe') $cliExe
    Install-File (Join-Path (Join-Path $tmp 'app') 'Keyturn.App.exe') $appExe

    # The shortcut's icon comes from the icon embedded in Keyturn.App.exe.
    $shortcut = $null
    if (-not $NoShortcut -and $shortcutPath) {
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcutPath)
        $link.TargetPath = $appExe
        $link.Arguments = '--manage'
        $link.WorkingDirectory = $Prefix
        $link.IconLocation = "$appExe,0"
        $link.Description = 'Rotating secrets in your OS credential store'
        $link.Save()
        $shortcut = $shortcutPath
    }

    $pathAdded = $false
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not (Test-FolderOnPath $userPath $Prefix)) {
        $newPath = if ($userPath) { $userPath.TrimEnd(';') + ';' + $Prefix } else { $Prefix }
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        $sessionPath = $env:Path
        $env:Path = if ($sessionPath) { $sessionPath.TrimEnd(';') + ';' + $Prefix } else { $Prefix }
        $pathAdded = $true
    }

    # Record exactly what was installed, so -Uninstall removes that and nothing else.
    $record = [ordered]@{
        version   = $version
        files     = @($cliExe, $appExe)
        shortcut  = $shortcut
        pathAdded = $pathAdded
        schedule  = [bool]$Schedule
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    if ($Schedule) {
        & $cliExe schedule install
        if ($LASTEXITCODE -ne 0) { Write-Warning "could not install the scheduled prompt; run 'keyturn schedule install' yourself" }
    }
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Step ''
Write-Step "Installed Keyturn ${version}:"
Write-Step "  $cliExe"
Write-Step "  $appExe"
if ($shortcut) { Write-Step '  Start Menu shortcut "Keyturn" (opens the manage window)' }
if ($pathAdded) { Write-Step "  added $Prefix to your user PATH (open a new terminal to pick it up)" }
Write-Step ''
Write-Step 'Next: keyturn add vpn daily@06:00 "Corporate VPN password"   (see the README''s Quickstart)'
if (-not $Schedule) { Write-Step '      keyturn schedule install   to have the prompt appear when a secret comes due' }
