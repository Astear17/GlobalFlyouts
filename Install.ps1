param(
    [string]$Version = "latest",
    [string]$Repository = "Astear17/GlobalFlyouts",
    [string]$WorkDirectory = (Join-Path $env:TEMP "GlobalFlyouts-Install"),
    [switch]$SkipCertificateTrust
)

$ErrorActionPreference = "Stop"

function Get-Release {
    param(
        [string]$RepositoryName,
        [string]$RequestedVersion
    )

    $headers = @{
        "User-Agent" = "GlobalFlyouts installer"
        "Accept" = "application/vnd.github+json"
    }

    if ($RequestedVersion -ieq "latest") {
        return Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$RepositoryName/releases/latest"
    }

    $tagCandidates = @($RequestedVersion)
    if ($RequestedVersion -notmatch "^v") {
        $tagCandidates += "v$RequestedVersion"
    } else {
        $tagCandidates += ($RequestedVersion -replace "^v", "")
    }

    foreach ($tag in $tagCandidates | Select-Object -Unique) {
        try {
            return Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$RepositoryName/releases/tags/$tag"
        } catch {
            $lastError = $_
        }
    }

    throw $lastError
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-CertificateTrusted {
    param([string]$CertificatePath)

    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificatePath)
    $thumbprint = $certificate.Thumbprint

    $rootTrusted = Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $thumbprint }
    $peopleTrusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $thumbprint }

    return [bool]($rootTrusted -and $peopleTrusted)
}

function Ensure-CertificateTrusted {
    param([string]$CertificatePath)

    if ($SkipCertificateTrust) {
        Write-Host "Skipping certificate trust step."
        return
    }

    if (Test-CertificateTrusted -CertificatePath $CertificatePath) {
        Write-Host "Signing certificate is already trusted."
        return
    }

    if (Test-IsAdministrator) {
        Import-Certificate -FilePath $CertificatePath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Import-Certificate -FilePath $CertificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        return
    }

    Write-Host "Trusting the signing certificate requires elevation. Approve the Windows prompt to continue."
    $command = @"
`$ErrorActionPreference = 'Stop'
Import-Certificate -FilePath '$CertificatePath' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath '$CertificatePath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
"@

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $process = Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-EncodedCommand",
        $encoded
    ) -Wait -PassThru

    if ($process.ExitCode -ne 0) {
        throw "Certificate trust step failed with exit code $($process.ExitCode)."
    }
}

function Download-Asset {
    param(
        [object]$Asset,
        [string]$DestinationDirectory
    )

    $destination = Join-Path $DestinationDirectory $Asset.name
    Write-Host "Downloading $($Asset.name)..."
    Invoke-WebRequest -Uri $Asset.browser_download_url -OutFile $destination
    return $destination
}

Write-Host "Resolving GlobalFlyouts release '$Version' from $Repository..."
$release = Get-Release -RepositoryName $Repository -RequestedVersion $Version
$assets = @($release.assets)

$zipAsset = $assets |
    Where-Object { $_.name -match "^GlobalFlyouts-.*msixbundle.*\.zip$" } |
    Sort-Object name -Descending |
    Select-Object -First 1

if (Test-Path $WorkDirectory) {
    Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$extractPath = Join-Path $WorkDirectory "package"
New-Item -ItemType Directory -Path $extractPath -Force | Out-Null

if ($zipAsset) {
    $zipPath = Download-Asset -Asset $zipAsset -DestinationDirectory $WorkDirectory
    Write-Host "Extracting package..."
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
} else {
    $bundleAsset = $assets |
        Where-Object { $_.name -match "\.msixbundle$" } |
        Sort-Object name -Descending |
        Select-Object -First 1
    $certificateAsset = $assets |
        Where-Object { $_.name -match "\.cer$" } |
        Sort-Object name -Descending |
        Select-Object -First 1
    $installerAsset = $assets |
        Where-Object { $_.name -match "Install\.ps1$" } |
        Sort-Object name -Descending |
        Select-Object -First 1

    if (-not $bundleAsset) {
        throw "No GlobalFlyouts package ZIP or standalone .msixbundle asset was found on release '$($release.tag_name)'."
    }

    Download-Asset -Asset $bundleAsset -DestinationDirectory $extractPath | Out-Null
    if ($certificateAsset) {
        Download-Asset -Asset $certificateAsset -DestinationDirectory $extractPath | Out-Null
    }
    if ($installerAsset) {
        Download-Asset -Asset $installerAsset -DestinationDirectory $extractPath | Out-Null
    }
}

$certificate = Get-ChildItem -Path $extractPath -Recurse -Filter "*.cer" | Select-Object -First 1
if ($certificate) {
    Ensure-CertificateTrusted -CertificatePath $certificate.FullName
} else {
    Write-Host "No .cer asset found. Continuing without certificate trust changes."
}

$installer = Get-ChildItem -Path $extractPath -Recurse -Filter "Install.ps1" |
    Sort-Object FullName |
    Select-Object -First 1

if ($installer) {
    Write-Host "Running package installer: $($installer.FullName)"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer.FullName -Force -SkipLoggingTelemetry
    if ($LASTEXITCODE -ne 0) {
        throw "Generated package installer failed with exit code $LASTEXITCODE."
    }
} else {
    $bundle = Get-ChildItem -Path $extractPath -Recurse -Filter "*.msixbundle" | Select-Object -First 1
    if (-not $bundle) {
        throw "The release package did not contain Install.ps1 or an .msixbundle."
    }

    Write-Host "Installing bundle: $($bundle.FullName)"
    Add-AppxPackage -Path $bundle.FullName
}

Write-Host "GlobalFlyouts installation completed."
