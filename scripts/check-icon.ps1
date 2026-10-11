<#
.SYNOPSIS
    Checks that a Windows executable has an application icon embedded, and lists its sizes.
.DESCRIPTION
    Reads the PE resource table directly (no Windows APIs, so it also runs under PowerShell on Linux) and looks for
    an RT_GROUP_ICON resource. Exits 0 and prints the sizes if there is one, exits 1 if not.
.EXAMPLE
    .\scripts\check-icon.ps1 .\keyturn.exe
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path))
function Get-U16([int]$Offset) { [int][BitConverter]::ToUInt16($bytes, $Offset) }
function Get-U32([int]$Offset) { [long][BitConverter]::ToUInt32($bytes, $Offset) }   # 64-bit: resource entries use the top bit as a flag

$pe = Get-U32 0x3c
if ($bytes[$pe] -ne 0x50 -or $bytes[$pe + 1] -ne 0x45) { throw "$Path is not a PE file" }

$sectionCount = Get-U16 ($pe + 6)
$optionalSize = Get-U16 ($pe + 20)
$optional = $pe + 24
$directories = $optional + $(if ((Get-U16 $optional) -eq 0x20b) { 112 } else { 96 })   # PE32+ vs PE32
$resourceRva = Get-U32 ($directories + 2 * 8)                                          # directory 2 = resources
if ($resourceRva -eq 0) { Write-Output "no resource table: no icon"; exit 1 }

# Map a relative virtual address to a file offset through the section table.
function ConvertTo-FileOffset([int]$Rva) {
    for ($i = 0; $i -lt $sectionCount; $i++) {
        $section = $optional + $optionalSize + 40 * $i
        $virtualSize = Get-U32 ($section + 8); $virtualAddress = Get-U32 ($section + 12); $rawSize = Get-U32 ($section + 16); $rawOffset = Get-U32 ($section + 20)
        if ($Rva -ge $virtualAddress -and $Rva -lt $virtualAddress + [Math]::Max($virtualSize, $rawSize)) { return $Rva - $virtualAddress + $rawOffset }
    }
    throw "RVA $Rva is outside every section"
}

$base = ConvertTo-FileOffset $resourceRva
function Get-DirectoryEntry([int]$Offset) {
    $count = (Get-U16 ($Offset + 12)) + (Get-U16 ($Offset + 14))
    for ($i = 0; $i -lt $count; $i++) { [pscustomobject]@{ Id = Get-U32 ($Offset + 16 + 8 * $i); Target = (Get-U32 ($Offset + 20 + 8 * $i)) -band 0x7fffffff } }
}

$RT_GROUP_ICON = 14
$group = Get-DirectoryEntry $base | Where-Object { $_.Id -eq $RT_GROUP_ICON } | Select-Object -First 1
if (-not $group) { Write-Output "no RT_GROUP_ICON resource: no application icon"; exit 1 }

# type -> name -> language -> data entry
$name = Get-DirectoryEntry ($base + $group.Target) | Select-Object -First 1
$language = Get-DirectoryEntry ($base + $name.Target) | Select-Object -First 1
$data = $base + $language.Target
$icons = ConvertTo-FileOffset (Get-U32 $data)
$count = Get-U16 ($icons + 4)
$sizes = for ($i = 0; $i -lt $count; $i++) { $w = [int]$bytes[$icons + 6 + 14 * $i]; if ($w -eq 0) { 256 } else { $w } }
Write-Output ("icon sizes: " + (($sizes | Sort-Object) -join ', '))
exit 0
