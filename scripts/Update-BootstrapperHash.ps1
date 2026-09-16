[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'

$resolvedInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
if (-not (Test-Path -LiteralPath $resolvedInstaller -PathType Leaf)) {
    throw "Installer file was not found: $InstallerPath"
}

$scriptRoot = Split-Path -Parent $PSScriptRoot
$bootstrapperSource = Join-Path $scriptRoot 'src\AppGuardian.Bootstrapper\BootstrapperWindow.xaml.cs'
if (-not (Test-Path -LiteralPath $bootstrapperSource -PathType Leaf)) {
    throw "Bootstrapper source was not found: $bootstrapperSource"
}

$hash = (Get-FileHash -LiteralPath $resolvedInstaller -Algorithm SHA256).Hash.ToUpperInvariant()
$source = Get-Content -LiteralPath $bootstrapperSource -Raw
$pattern = 'private const string ExpectedHash = "(?:[A-Fa-f0-9]{64}|__RELEASE_SHA256__)";'
$replacement = "private const string ExpectedHash = `"$hash`";"

if ($source -notmatch $pattern) {
    throw 'ExpectedHash constant was not found or has an unexpected format. Refusing to patch source.'
}

$updated = [regex]::Replace($source, $pattern, $replacement, 1)
[System.IO.File]::WriteAllText(
    $bootstrapperSource,
    $updated,
    [System.Text.UTF8Encoding]::new($false))

Write-Output "Updated bootstrapper SHA-256: $hash"
