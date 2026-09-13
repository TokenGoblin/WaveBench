<#
.SYNOPSIS
    Build the WaveBench MSIX package.

.DESCRIPTION
    Publishes the desktop app, stages it with the manifest and logo set, and
    calls makeappx from the Windows SDK. Signing is a separate, optional step:
    see -CertificatePath.

    Every path here is relative to the repository root, which is derived from
    this script's own location. Nothing is hard-coded to a machine.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER Version
    Four-part package version. Overwrites the manifest's Identity/@Version for
    this build only; the file on disk is left alone.

.PARAMETER CertificatePath
    A .pfx to sign with. Without it the package is built UNSIGNED, which
    Windows will not install without developer mode and a trusted certificate --
    see packaging/README.md.

.PARAMETER CertificatePassword
    Password for the .pfx, as a SecureString. Prompted for if the certificate
    is given without it. Never pass this on a command line you keep.

.EXAMPLE
    pwsh packaging/Package.ps1 -Version 1.0.0.0
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0.0',
    [string]$CertificatePath,
    [System.Security.SecureString]$CertificatePassword
)

$ErrorActionPreference = 'Stop'

$packaging = $PSScriptRoot
$root = Split-Path -Parent $packaging
$staging = Join-Path $packaging 'staging'
$output = Join-Path $packaging 'out'

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must be four parts, e.g. 1.0.0.0 -- got '$Version'."
}

# ---- Locate makeappx -------------------------------------------------------
#
# Highest SDK version wins. Sorting the directory names as STRINGS would put
# 10.0.9 above 10.0.26100, so they are compared as versions.
function Find-SdkTool([string]$name) {
    $bin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $bin)) {
        throw "Windows SDK not found. Install the Windows 10/11 SDK to build an MSIX."
    }

    # @() around the pipeline: a pipeline that yields ONE item yields it as a
    # scalar, and $candidates[0] on a bare string is its first CHARACTER --
    # which made this return "C" and fail with "the term 'C' is not
    # recognized".
    $candidates = @(Get-ChildItem $bin -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } |
        Where-Object { Test-Path $_ })

    if (-not $candidates) {
        throw "$name not found under $bin. Install the Windows SDK signing tools."
    }

    return $candidates[0]
}

$makeappx = Find-SdkTool 'makeappx.exe'
Write-Host "makeappx: $makeappx"

# ---- Publish ---------------------------------------------------------------
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

$app = Join-Path $root 'src\WaveBench.App\WaveBench.App.csproj'
Write-Host "publishing $Configuration..."
dotnet publish $app -c $Configuration -r win-x64 --self-contained false -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE." }

# ---- Stage the manifest and assets -----------------------------------------
Copy-Item (Join-Path $packaging 'assets') (Join-Path $staging 'assets') -Recurse

$manifestPath = Join-Path $staging 'AppxManifest.xml'
$manifest = [xml](Get-Content (Join-Path $packaging 'AppxManifest.xml') -Raw)
$manifest.Package.Identity.Version = $Version

if ($CertificatePath) {
    if (-not $CertificatePassword) {
        $CertificatePassword = Read-Host 'Certificate password' -AsSecureString
    }

    # The manifest's Publisher must EQUAL the certificate subject or Windows
    # refuses to install the package -- and the error it gives says nothing
    # about which of the two is wrong. Taking it from the certificate removes
    # the chance of them disagreeing.
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path $CertificatePath), $CertificatePassword)
    $manifest.Package.Identity.Publisher = $certificate.Subject
    Write-Host "publisher from certificate: $($certificate.Subject)"
}

$manifest.Save($manifestPath)

# ---- Pack ------------------------------------------------------------------
$msix = Join-Path $output "WaveBench-$Version.msix"
& $makeappx pack /d $staging /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with $LASTEXITCODE." }

Write-Host "packed: $msix"

# ---- Sign ------------------------------------------------------------------
if ($CertificatePath) {
    $signtool = Find-SdkTool 'signtool.exe'
    $plain = [System.Net.NetworkCredential]::new('', $CertificatePassword).Password
    & $signtool sign /fd SHA256 /a /f (Resolve-Path $CertificatePath) /p $plain $msix
    $signed = $LASTEXITCODE
    $plain = $null
    if ($signed -ne 0) { throw "signtool failed with $signed." }
    Write-Host "signed: $msix"
}
else {
    Write-Warning ("Built UNSIGNED. Windows will not install this without a trusted certificate -- " +
                   "see packaging/README.md.")
}
