param(
    [string]$Version = "latest",
    [string]$Repository = "Astear17/GlobalFlyouts",
    [string]$WorkDirectory = (Join-Path $env:TEMP "GlobalFlyouts-Install")
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

Write-Host "Resolving GlobalFlyouts release '$Version' from $Repository..."
$release = Get-Release -RepositoryName $Repository -RequestedVersion $Version

$assets = @($release.assets)
$asset = $assets |
    Where-Object { $_.name -match "^GlobalFlyouts-.*msixbundle.*\.zip$" } |
    Sort-Object name -Descending |
    Select-Object -First 1

if (-not $asset) {
    $asset = $assets |
        Where-Object { $_.name -match "msixbundle" -and $_.name -match "\.zip$" } |
        Sort-Object name -Descending |
        Select-Object -First 1
}

if (-not $asset) {
    throw "No GlobalFlyouts package ZIP asset was found on release '$($release.tag_name)'. Expected an asset like GlobalFlyouts-<version>-msixbundle.zip."
}

if (Test-Path $WorkDirectory) {
    Remove-Item -LiteralPath $WorkDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null

$zipPath = Join-Path $WorkDirectory $asset.name
$extractPath = Join-Path $WorkDirectory "package"

Write-Host "Downloading $($asset.name)..."
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath

Write-Host "Extracting package..."
Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

$installer = Get-ChildItem -Path $extractPath -Recurse -Filter Install.ps1 |
    Sort-Object FullName |
    Select-Object -First 1

if (-not $installer) {
    throw "The release package did not contain a generated Install.ps1."
}

Write-Host "Running package installer: $($installer.FullName)"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer.FullName -Force -SkipLoggingTelemetry
if ($LASTEXITCODE -ne 0) {
    throw "Generated package installer failed with exit code $LASTEXITCODE."
}

Write-Host "GlobalFlyouts installation completed."
