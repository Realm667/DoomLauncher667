$ErrorActionPreference = 'Stop'
foreach ($case in @(
    @('0.9.3', '0.9.3.0', 'DoomLauncher667-0.9.3-beta-win-x64', $true),
    @('1.0.0-rc.1', '1.0.0.0', 'DoomLauncher667-1.0.0-rc.1-win-x64', $true),
    @('1.0.0-beta.2', '1.0.0.0', 'DoomLauncher667-1.0.0-beta.2-win-x64', $true),
    @('1.0.0', '1.0.0.0', 'DoomLauncher667-1.0.0-win-x64', $false)
)) {
    $actual = & "$PSScriptRoot/release-version.ps1" -Version $case[0]
    if ($actual.FileVersion -ne $case[1] -or $actual.PackageName -ne $case[2] -or $actual.Prerelease -ne $case[3]) { throw "Version contract failed: $($case[0])" }
    $expectedFlag = if ($case[3]) { '--prerelease' } else { '--latest' }
    if ($actual.PublishFlag -ne $expectedFlag) { throw "Invalid publish flag: $($actual.PublishFlag)" }
    # Pass the flag as a single argument, never splat a scalar string into characters.
    function Get-TestArguments { return ,$args }
    $forwarded = Get-TestArguments $actual.PublishFlag
    if ($forwarded.Count -ne 1 -or $forwarded[0] -ne $expectedFlag) { throw 'Publish flag was split.' }
}
foreach ($invalid in @('1.0', '1.0.0-rc.0', '1.0.0-rc.01', 'v1.0.0', '../1.0.0', '1.0.0-unknown.1')) {
    $rejected = $false
    try { & "$PSScriptRoot/release-version.ps1" -Version $invalid | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid version accepted: $invalid" }
}
Write-Output 'PASS Beta, RC and stable version/package contracts; invalid versions rejected.'
