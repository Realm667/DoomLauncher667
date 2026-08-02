param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

$ErrorActionPreference = 'Stop'
$resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$marker = Join-Path $resolvedRoot 'Tools\reset-instance.marker'
if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
    throw 'Reset marker missing. No files were changed.'
}

$launcherPath = Join-Path $resolvedRoot 'WinUI\DoomLauncher.WinUI.exe'
Get-Process -Name 'DoomLauncher.WinUI' -ErrorAction SilentlyContinue |
    Where-Object {
        try {
            [string]::Equals(
                [IO.Path]::GetFullPath($_.Path),
                $launcherPath,
                [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $false
        }
    } |
    ForEach-Object {
        if ($_.MainWindowHandle.ToInt64() -ne 0) {
            [void]$_.CloseMainWindow()
            try {
                $_.WaitForExit(5000)
            }
            catch {
            }
        }
        if (-not $_.HasExited) {
            $_.Kill()
            $_.WaitForExit()
        }
    }

$targets = @(
    (Join-Path $resolvedRoot 'DoomLauncher.sqlite'),
    (Join-Path $resolvedRoot 'DoomLauncher.sqlite-shm'),
    (Join-Path $resolvedRoot 'DoomLauncher.sqlite-wal'),
    (Join-Path $resolvedRoot 'UserData'),
    (Join-Path $resolvedRoot 'Data'),
    (Join-Path $resolvedRoot 'TileImages'),
    (Join-Path $resolvedRoot 'Backups')
)
$rootPrefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
foreach ($target in $targets) {
    $resolvedTarget = [IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith(
            $rootPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe reset target rejected: $resolvedTarget"
    }
}

foreach ($target in $targets) {
    if (Test-Path -LiteralPath $target) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

foreach ($directory in @(
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
        'Backups')) {
    [void](New-Item -ItemType Directory -Path (
        Join-Path $resolvedRoot $directory) -Force)
}

Write-Host 'Reset completed. No database or user state remains.'
