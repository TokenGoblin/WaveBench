<#
.SYNOPSIS
    Build the WaveBench installer packages.

.DESCRIPTION
    Publishes the desktop app once, then builds an MSI (WiX), an MSIX
    (makeappx), or both.

    MSI is the default, and the reason is signing: an MSIX will not install
    without a signature Windows already trusts, so it is unusable until
    somebody holds a code-signing certificate. An MSI installs unsigned - the
    user gets a SmartScreen prompt and decides - which makes it the format that
    actually ships.

    Every path here is relative to the repository root, derived from this
    script's own location. Nothing is hard-coded to a machine.

.PARAMETER Configuration
    Build configuration. Release by default.

.PARAMETER Version
    Four-part version. Written into both packages for this build only; the
    files on disk are left alone.

.PARAMETER Format
    Msi, Msix, or Both.

.PARAMETER CertificatePath
    A .pfx to sign the output with. Optional. See packaging/README.md for what
    unsigned means for each format - they are not the same.

.PARAMETER CertificatePassword
    Password for the .pfx, as a SecureString. Prompted for if the certificate
    is given without it. Never pass this on a command line you keep.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. A signature WITHOUT a timestamp stops validating
    the day the certificate expires; with one it stays valid for the life of
    the timestamp, which is the whole point of signing a release rather than a
    build. Pass an empty string only for a deliberately throwaway signature.

    This is the one network call anywhere in this repository, it happens at
    BUILD time, and it does not touch the runtime claim on the front of the
    README.

.EXAMPLE
    powershell -File packaging/Package.ps1 -Version 1.0.0.0

.EXAMPLE
    powershell -File packaging/Package.ps1 -Version 1.0.0.0 -Format Both
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0.0',
    [ValidateSet('Msi', 'Msix', 'Both')]
    [string]$Format = 'Msi',
    [string]$CertificatePath,
    [System.Security.SecureString]$CertificatePassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

$packaging = $PSScriptRoot
$root = Split-Path -Parent $packaging
$publish = Join-Path $packaging 'publish'
$staging = Join-Path $packaging 'staging'
$output = Join-Path $packaging 'out'

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must be four parts, e.g. 1.0.0.0 -- got '$Version'."
}

$wantMsi = $Format -in @('Msi', 'Both')
$wantMsix = $Format -in @('Msix', 'Both')
$artefacts = @()

# ---- Locate the SDK tools --------------------------------------------------
#
# Highest SDK version wins. Sorting the directory names as STRINGS would put
# 10.0.9 above 10.0.26100, so they are compared as versions.
function Find-SdkTool([string]$name) {
    $bin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $bin)) {
        throw "Windows SDK not found. Install the Windows 10/11 SDK."
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

# ---- Publish ---------------------------------------------------------------
foreach ($directory in @($publish, $staging)) {
    if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
}

New-Item -ItemType Directory -Path $publish | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

# BOTH the app and the CLI, into one folder.
#
# The user guide documents `wavebench sweep`, `wavebench report` and the rest
# throughout, and "everything that matters runs without a window" is a headline
# claim. An installer that delivered only the GUI would make every one of those
# commands a lie for anybody who installed rather than built from source.
$projects = @(
    (Join-Path $root 'src\WaveBench.App\WaveBench.App.csproj'),
    (Join-Path $root 'src\WaveBench.Cli\WaveBench.Cli.csproj')
)

foreach ($project in $projects) {
    Write-Host "publishing $(Split-Path -Leaf $project) ($Configuration)..."
    dotnet publish $project -c $Configuration -r win-x64 --self-contained false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with $LASTEXITCODE on $project." }
}

# Debug symbols out of the payload. They are worth keeping for diagnosing a
# crash from a stack trace somebody sends in - they stay in each project's own
# bin/ - but shipping them to every user is a third of the package for
# something none of them will open.
#
# Done here rather than in the .wxs because WiX 5's Files element harvests with
# Include and has no Exclude.
Get-ChildItem $publish -Filter *.pdb -Recurse | Remove-Item -Force

# ---- MSI -------------------------------------------------------------------
if ($wantMsi) {
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
        throw "The WiX toolset is not installed. Run: dotnet tool install --global wix"
    }

    # The licence shown in the installer is the repository's own, converted
    # rather than kept as a second copy that could disagree with it.
    $licenseRtf = Join-Path $output 'License.rtf'
    $licenseText = Get-Content (Join-Path $root 'LICENSE') -Raw
    $escaped = $licenseText.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')
    $escaped = $escaped -replace "`r`n", '\par ' -replace "`n", '\par '
    Set-Content $licenseRtf ("{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18 " + $escaped + "}") -Encoding ASCII

    $msi = Join-Path $output "WaveBench-$Version.msi"
    Write-Host "building $msi..."

    & wix build (Join-Path $packaging 'WaveBench.wxs') `
        -arch x64 `
        -d "Version=$Version" `
        -d "PublishDir=$publish" `
        -d "LicenseRtf=$licenseRtf" `
        -ext WixToolset.UI.wixext `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw "wix build failed with $LASTEXITCODE." }

    Write-Host "packed: $msi"
    $artefacts += $msi
}

# ---- MSIX ------------------------------------------------------------------
if ($wantMsix) {
    $makeappx = Find-SdkTool 'makeappx.exe'
    Copy-Item $publish $staging -Recurse
    Copy-Item (Join-Path $packaging 'assets') (Join-Path $staging 'assets') -Recurse

    $manifestPath = Join-Path $staging 'AppxManifest.xml'
    $manifest = [xml](Get-Content (Join-Path $packaging 'AppxManifest.xml') -Raw)
    $manifest.Package.Identity.Version = $Version

    if ($CertificatePath) {
        if (-not $CertificatePassword) {
            $CertificatePassword = Read-Host 'Certificate password' -AsSecureString
        }

        # The manifest's Publisher must EQUAL the certificate subject or
        # Windows refuses to install the package -- and the error it gives says
        # nothing about which of the two is wrong. Taking it from the
        # certificate removes the chance of them disagreeing.
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
            (Resolve-Path $CertificatePath), $CertificatePassword)
        $manifest.Package.Identity.Publisher = $certificate.Subject
        Write-Host "publisher from certificate: $($certificate.Subject)"
    }

    $manifest.Save($manifestPath)

    $msix = Join-Path $output "WaveBench-$Version.msix"
    & $makeappx pack /d $staging /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with $LASTEXITCODE." }

    Write-Host "packed: $msix"
    $artefacts += $msix
}

# ---- Sign ------------------------------------------------------------------
if ($CertificatePath) {
    if (-not $CertificatePassword) {
        $CertificatePassword = Read-Host 'Certificate password' -AsSecureString
    }

    $signtool = Find-SdkTool 'signtool.exe'
    $arguments = @('sign', '/fd', 'SHA256', '/f', (Resolve-Path $CertificatePath).Path)

    if ($TimestampUrl) {
        # /tr is the RFC 3161 form; /t is the older Authenticode one and does
        # not carry a SHA-256 digest. /td must accompany /tr or the timestamp
        # is taken at SHA-1.
        $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    else {
        Write-Warning ("Signing WITHOUT a timestamp: this signature stops validating when the " +
                       "certificate expires.")
    }

    $plain = [System.Net.NetworkCredential]::new('', $CertificatePassword).Password
    try {
        foreach ($artefact in $artefacts) {
            & $signtool @arguments '/p' $plain $artefact
            if ($LASTEXITCODE -ne 0) { throw "signtool failed with $LASTEXITCODE on $artefact." }
        }
    }
    finally {
        # Out of the variable either way, including on a throw.
        $plain = $null
    }

    # Signing something and never checking it verifies is how a broken release
    # gets published. /pa uses the default authenticode policy, which is what
    # Windows itself applies on install.
    foreach ($artefact in $artefacts) {
        & $signtool verify /pa /v $artefact
        if ($LASTEXITCODE -ne 0) { throw "$artefact did not verify after signing." }
        Write-Host "signed and verified: $artefact"
    }
}
else {
    if ($wantMsi) {
        Write-Host ("MSI built unsigned. It INSTALLS -- the user gets a SmartScreen prompt naming an " +
                    "unknown publisher and chooses.")
    }

    if ($wantMsix) {
        Write-Warning ("MSIX built unsigned. Windows will NOT install it without a trusted certificate " +
                       "-- see packaging/README.md.")
    }
}
