[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$OutputDirectory,

    [string]$PublishDirectory,

    [string]$BootstrapPublishDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packageName = "DoomLauncher667-$Version-beta-win-x64"
$packageRoot = Join-Path $outputRoot $packageName
$archivePath = Join-Path $outputRoot "$packageName.zip"
$checksumPath = "$archivePath.sha256"
$outputPrefix = $outputRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar

foreach ($generatedPath in @($packageRoot, $archivePath, $checksumPath)) {
    $resolvedGeneratedPath = [IO.Path]::GetFullPath($generatedPath)
    if (-not $resolvedGeneratedPath.StartsWith(
            $outputPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe package target rejected: $resolvedGeneratedPath"
    }
    if (Test-Path -LiteralPath $resolvedGeneratedPath) {
        Remove-Item -LiteralPath $resolvedGeneratedPath -Recurse -Force
    }
}

[void](New-Item -ItemType Directory -Path $outputRoot -Force)
[void](New-Item -ItemType Directory -Path $packageRoot -Force)

if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $PublishDirectory = Join-Path $outputRoot "publish-$Version-win-x64"
    $resolvedPublish = [IO.Path]::GetFullPath($PublishDirectory)
    if (-not $resolvedPublish.StartsWith(
            $outputPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe publish target rejected: $resolvedPublish"
    }
    if (Test-Path -LiteralPath $resolvedPublish) {
        Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
    }

    & dotnet publish (
        Join-Path $repositoryRoot 'DoomLauncher.WinUI\DoomLauncher.WinUI.csproj') `
        -c Release `
        -p:Platform=x64 `
        --no-restore `
        -o $resolvedPublish
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
    $PublishDirectory = $resolvedPublish
}
else {
    $PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
}

$launcherExecutable = Join-Path $PublishDirectory 'DoomLauncher.WinUI.exe'
if (-not (Test-Path -LiteralPath $launcherExecutable -PathType Leaf)) {
    throw "Published launcher not found: $launcherExecutable"
}
$launcherVersion = (Get-Item -LiteralPath `
    $launcherExecutable).VersionInfo.FileVersion
if ($launcherVersion -ne "$Version.0") {
    throw "WinUI version $launcherVersion does not match package version $Version.0."
}

$manifestPath = Join-Path $repositoryRoot `
    'DoomLauncher.WinUI\Package.appxmanifest'
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifestVersion = [string]$manifest.Package.Identity.Version
if ($manifestVersion -ne "$Version.0") {
    throw "App manifest version $manifestVersion does not match package version $Version.0."
}

if ([string]::IsNullOrWhiteSpace($BootstrapPublishDirectory)) {
    $BootstrapPublishDirectory = Join-Path $outputRoot "launcher-$Version-win-x64"
    $resolvedBootstrapPublish = [IO.Path]::GetFullPath(
        $BootstrapPublishDirectory)
    if (-not $resolvedBootstrapPublish.StartsWith(
            $outputPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe bootstrap target rejected: $resolvedBootstrapPublish"
    }
    if (Test-Path -LiteralPath $resolvedBootstrapPublish) {
        Remove-Item -LiteralPath $resolvedBootstrapPublish -Recurse -Force
    }

    & dotnet publish (
        Join-Path $repositoryRoot `
            'DoomLauncher.Bootstrap\DoomLauncher.Bootstrap.csproj') `
        -c Release `
        --no-restore `
        -o $resolvedBootstrapPublish
    if ($LASTEXITCODE -ne 0) {
        throw "DoomLauncher667 bootstrap publish failed with exit code $LASTEXITCODE."
    }
    $bootstrapExecutable = Join-Path `
        $resolvedBootstrapPublish 'DoomLauncher667.exe'
    $compiledVersion = (Get-Item -LiteralPath `
        $bootstrapExecutable).VersionInfo.FileVersion
    if ($compiledVersion -ne "$Version.0") {
        throw "Bootstrap version $compiledVersion does not match package version $Version.0."
    }
    $BootstrapPublishDirectory = $resolvedBootstrapPublish
}
else {
    $BootstrapPublishDirectory = [IO.Path]::GetFullPath(
        $BootstrapPublishDirectory)
}

$bootstrapExecutable = Join-Path $BootstrapPublishDirectory 'DoomLauncher667.exe'
if (-not (Test-Path -LiteralPath $bootstrapExecutable -PathType Leaf)) {
    throw "Published bootstrap not found: $bootstrapExecutable"
}

$templateRoot = Join-Path $repositoryRoot 'deployment\reset-instance'
Copy-Item -Path (Join-Path $templateRoot '*') `
    -Destination $packageRoot `
    -Recurse `
    -Force

foreach ($directory in @(
        'Backups',
        'Data',
        'Data\GameWads',
        'Data\Sourceports',
        'Data\Mods',
        'Data\Screenshots',
        'Data\TitlePics',
        'Data\SaveGames',
        'Data\Demos',
        'Data\Temp',
        'Data\Themes',
        'Data\TileImages',
        'Data\CollectionArtworks',
        'Data\UserState',
        'WinUI')) {
    [void](New-Item -ItemType Directory -Path (
        Join-Path $packageRoot $directory) -Force)
}

Copy-Item -Path (Join-Path $PublishDirectory '*') `
    -Destination (Join-Path $packageRoot 'WinUI') `
    -Recurse `
    -Force
Copy-Item -LiteralPath $bootstrapExecutable `
    -Destination (Join-Path $packageRoot 'DoomLauncher667.exe') `
    -Force

$themeSource = Join-Path $repositoryRoot 'DoomLauncher.WinUI\Assets\Themes'
Copy-Item -Path (Join-Path $themeSource '*') `
    -Destination (Join-Path $packageRoot 'Data\Themes') `
    -Recurse `
    -Force

$tileImageSource = Join-Path $repositoryRoot 'DoomLauncher\TileImages'
Copy-Item -Path (Join-Path $tileImageSource '*') `
    -Destination (Join-Path $packageRoot 'Data\TileImages') `
    -Recurse `
    -Force

$revision = 'unknown'
try {
    $revision = (& git -C $repositoryRoot rev-parse --short=12 HEAD).Trim()
}
catch {
    $revision = 'unknown'
}
[IO.File]::WriteAllLines(
    (Join-Path $packageRoot 'VERSION.txt'),
    @(
        "Doom Launcher 667 $Version Beta",
        'Platform: Windows x64',
        "Source revision: $revision"
    ),
    [Text.UTF8Encoding]::new($false))

Compress-Archive `
    -LiteralPath $packageRoot `
    -DestinationPath $archivePath `
    -CompressionLevel Optimal

$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    $checksumPath,
    "$checksum  $([IO.Path]::GetFileName($archivePath))`n",
    [Text.UTF8Encoding]::new($false))

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "archive=$archivePath"
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "checksum=$checksumPath"
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "package=$packageRoot"
}

Write-Host "Package: $archivePath"
Write-Host "Checksum: $checksum"
