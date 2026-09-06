param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(\d+\.\d+\.\d+)(?:-(rc|beta)\.([1-9]\d*))?$') {
    throw "Unsupported release version: $Version"
}
$numeric = $Matches[1]
$suffix = $Matches[2]
$channel = if ($suffix -eq 'rc') { 'Release Candidate' } elseif ($suffix -eq 'beta' -or $numeric.StartsWith('0.')) { 'Beta' } else { 'Stable' }
$legacyBeta = $channel -eq 'Beta' -and -not $suffix
[pscustomobject]@{
    Version = $Version
    FileVersion = "$numeric.0"
    Channel = $channel
    Prerelease = $channel -ne 'Stable'
    PackageName = "DoomLauncher667-$Version$(if ($legacyBeta) { '-beta' })-win-x64"
    Title = "Doom Launcher 667 $Version$(if ($channel -ne 'Stable') { " $channel" })"
}
