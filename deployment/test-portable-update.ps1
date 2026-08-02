[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'DoomLauncher 667 update contract ' + [Guid]::NewGuid().ToString('N'))

function Write-Marker {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )
    [void](New-Item -ItemType Directory -Path (
        Split-Path -Parent $Path) -Force)
    [IO.File]::WriteAllText($Path, $Value)
}

try {
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "Package directory not found: $packageRoot"
    }

    [void](New-Item -ItemType Directory -Path $testRoot -Force)
    $database = Join-Path $testRoot 'DoomLauncher.sqlite'
    $mod = Join-Path $testRoot 'Data\Mods\existing-mod.zip'
    $iwad = Join-Path $testRoot 'Data\GameWads\DOOM2.WAD'
    $sourcePort = Join-Path $testRoot `
        'Data\Sourceports\GZDoom\gzdoom.exe'
    $screenshot = Join-Path $testRoot `
        'Data\Screenshots\existing-shot.png'
    $titleArtwork = Join-Path $testRoot `
        'Data\TitlePics\existing-title.png'
    $saveGame = Join-Path $testRoot 'Data\SaveGames\existing.zds'
    $collectionArtwork = Join-Path $testRoot `
        'Data\CollectionArtworks\existing.jpg'
    $state = Join-Path $testRoot `
        'Data\UserState\DoomLauncher.WinUI.state.json'
    $customTheme = Join-Path $testRoot 'Data\Themes\MyTheme.xml'
    Write-Marker $database 'existing-database'
    Write-Marker $mod 'existing-mod'
    Write-Marker $iwad 'existing-iwad'
    Write-Marker $sourcePort 'existing-source-port'
    Write-Marker $screenshot 'existing-screenshot'
    Write-Marker $titleArtwork 'existing-title-artwork'
    Write-Marker $saveGame 'existing-save-game'
    Write-Marker $collectionArtwork 'existing-collection-artwork'
    Write-Marker $state '{"Language":"de"}'
    Write-Marker $customTheme '<theme name="MyTheme" />'

    $before = @{}
    foreach ($path in @(
            $database,
            $mod,
            $iwad,
            $sourcePort,
            $screenshot,
            $titleArtwork,
            $saveGame,
            $collectionArtwork,
            $state,
            $customTheme)) {
        $before[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }

    Copy-Item -Path (Join-Path $packageRoot '*') `
        -Destination $testRoot `
        -Recurse `
        -Force

    foreach ($path in $before.Keys) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Update removed existing user data: $path"
        }
        $after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($after -ne $before[$path]) {
            throw "Update overwrote existing user data: $path"
        }
    }

    foreach ($requiredFile in @(
            'DoomLauncher667.exe',
            'DoomLauncher667-debug.cmd',
            'DoomLauncher667-reset.cmd',
            'WinUI\DoomLauncher.WinUI.exe')) {
        if (-not (Test-Path -LiteralPath (
                Join-Path $testRoot $requiredFile) -PathType Leaf)) {
            throw "Updated installation is missing $requiredFile."
        }
    }

    if (Test-Path -LiteralPath (
            Join-Path $packageRoot 'DoomLauncher.sqlite')) {
        throw 'Release package must never contain a user database.'
    }
    if (Get-ChildItem -LiteralPath (
            Join-Path $packageRoot 'Data\UserState') -File -ErrorAction SilentlyContinue) {
        throw 'Release package must never contain persisted user state.'
    }

    Write-Host (
        'PASS Portable update preserves database, game files, media, settings ' +
        'and custom themes.')
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
