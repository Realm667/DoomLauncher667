$ErrorActionPreference = 'Stop'

$template = Join-Path $PSScriptRoot 'reset-instance'
$testRoot = Join-Path (
    [IO.Path]::GetTempPath()) (
    'DoomLauncher 667 reset contract ' + [Guid]::NewGuid().ToString('N'))

function Add-TestData {
    param([Parameter(Mandatory = $true)][string]$Root)

    $mods = Join-Path $Root 'Data\Mods'
    [void](New-Item -ItemType Directory -Path $mods -Force)
    [IO.File]::WriteAllText(
        (Join-Path $Root 'DoomLauncher.sqlite'),
        'reset-contract-database')
    [IO.File]::WriteAllText(
        (Join-Path $mods 'reset-contract.wad'),
        'reset-contract-mod')
}

function Assert-ResetState {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (Test-Path -LiteralPath (Join-Path $Root 'DoomLauncher.sqlite')) {
        throw 'The portable database was not removed.'
    }
    if (Test-Path -LiteralPath (Join-Path $Root 'Data\Mods\reset-contract.wad')) {
        throw 'Mutable managed data was not removed.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'Data\Mods') -PathType Container)) {
        throw 'The empty portable data layout was not recreated.'
    }
    if (-not (Test-Path -LiteralPath (
            Join-Path $Root 'Tools\reset-instance.marker') -PathType Leaf)) {
        throw 'The guarded reset marker was not preserved.'
    }
}

try {
    Copy-Item -LiteralPath $template -Destination $testRoot -Recurse
    Add-TestData -Root $testRoot

    & (Join-Path $testRoot 'DoomLauncher667-reset.cmd') /Y
    if ($LASTEXITCODE -ne 0) {
        throw "The reset wrapper exited with code $LASTEXITCODE."
    }
    Assert-ResetState -Root $testRoot

    Add-TestData -Root $testRoot
    & (Join-Path $testRoot 'Tools\Reset-Instance.ps1') -Root ($testRoot + '"')
    Assert-ResetState -Root $testRoot

    Write-Host 'PASS Portable reset wrapper and guarded root normalization.'
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
