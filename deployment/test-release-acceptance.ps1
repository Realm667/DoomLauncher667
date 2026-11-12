param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$HistoricalDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidatePattern('^v\d+\.\d+\.\d+(?:-rc\.[1-9]\d*)?$')][string[]]$HistoricalVersions=@('v0.8.1','v0.8.10','v0.9.0','v0.9.3','v1.0.0-rc.1','v1.0.0-rc.15'),
    [switch]$SkipFreshInstallation,
    [switch]$SkipLargeMigration
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package=(Resolve-Path -LiteralPath $PackageDirectory).Path
$history=(Resolve-Path -LiteralPath $HistoricalDirectory).Path
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Acceptance output must be a new isolated directory.'}
New-Item -ItemType Directory -Path $output | Out-Null
$testDll=Join-Path $repo 'DoomLauncher.Modern.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/DoomLauncher.Modern.Tests.dll'
$results=@()
function Test-Command {
    param([string[]]$Arguments)
    $previousDatabase=$env:DOOMLAUNCHER_DATABASE
    $env:DOOMLAUNCHER_DATABASE=$null
    try {
        & dotnet $testDll @Arguments
        if($LASTEXITCODE -ne 0){throw "Acceptance command failed: $Arguments"}
    }
    finally{$env:DOOMLAUNCHER_DATABASE=$previousDatabase}
}
function Test-Native {
    param([string]$Instance)
    $previous=$env:DOOMLAUNCHER_UI_TEST_ROOT
    $env:DOOMLAUNCHER_UI_TEST_ROOT=$Instance
    try{Start-Process -FilePath (Join-Path $Instance 'DoomLauncher667.exe') -ArgumentList '--release-acceptance' -WorkingDirectory $Instance -WindowStyle Hidden}
    finally{$env:DOOMLAUNCHER_UI_TEST_ROOT=$previous}
    $report=Join-Path $Instance 'release-test-report.json'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while(-not (Test-Path -LiteralPath $report) -and $watch.Elapsed.TotalSeconds -lt 360){Start-Sleep -Milliseconds 200}
    if(-not (Test-Path -LiteralPath $report)){throw "Native acceptance timed out: $Instance"}
    $data=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
    if(-not $data.Passed){throw "Native acceptance failed: $($data.Error)"}
    $exe=Join-Path $Instance 'WinUI/DoomLauncher.WinUI.exe'
    while((Get-Process DoomLauncher.WinUI -ErrorAction SilentlyContinue|Where-Object Path -eq $exe) -and $watch.Elapsed.TotalSeconds -lt 380){Start-Sleep -Milliseconds 100}
    if(Get-Process DoomLauncher.WinUI -ErrorAction SilentlyContinue|Where-Object Path -eq $exe){throw 'Test instance did not finish its normal save/close.'}
    Write-Output "PASS Native installation acceptance: $Instance ($($data.Checks.Count) checks)"
}
if(-not $SkipFreshInstallation){
$fresh=Join-Path $output 'fresh-install'
New-Item -ItemType Directory -Path $fresh | Out-Null
Copy-Item -Path (Join-Path $package '*') -Destination $fresh -Recurse
New-Item -ItemType File -Path (Join-Path $fresh '.release-test-instance') | Out-Null
New-Item -ItemType File -Path (Join-Path $fresh '.first-setup-test') | Out-Null
Test-Native $fresh
$results+=[pscustomobject]@{Case='Fresh portable installation and all four native first-setup scans';Passed=$true;Report=(Join-Path $fresh 'release-test-report.json');Source='Empty folder on existing Windows, not a clean OS'}
}
foreach($version in $HistoricalVersions){
    $extracted=Join-Path $history "$version/extracted"
    $old=(Get-ChildItem -LiteralPath $extracted -Directory|Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName 'WinUI/DoomLauncher.WinUI.dll')}).FullName
    if(@($old).Count -ne 1){throw "Original package root not found: $version"}
    $instance=Join-Path $output $version
    New-Item -ItemType Directory -Path $instance | Out-Null
    Copy-Item -Path (Join-Path $old '*') -Destination $instance -Recurse
    Test-Command @('--prepare-historical-release-test',$old,$instance)
    Copy-Item -Path (Join-Path $package '*') -Destination $instance -Recurse -Force
    Test-Native $instance
    Test-Command @('--verify-historical-release-test',$instance)
    $results+=[pscustomobject]@{Case=$version;Passed=$true;Report=(Join-Path $instance 'release-test-report.json');Source='Original published package and original compiled schema; synthetic personal data'}
    $results|ConvertTo-Json -Depth 4|Out-File -LiteralPath (Join-Path $output 'matrix.json') -Encoding utf8
}
if(-not $SkipLargeMigration){
$largeRoot=Join-Path $output 'legacy-11000'
Test-Command @('--prepare-large-migration-test',$largeRoot,'11000')
$large=Join-Path $largeRoot 'portable'
Copy-Item -Path (Join-Path $package '*') -Destination $large -Recurse -Force
Test-Native $large
$results+=[pscustomobject]@{Case='Original launcher migration: 11000 archives / 12 collections';Passed=$true;Report=(Join-Path $large 'release-test-report.json');Source='Synthetic legacy database and actual portable file migration'}
}
$results|ConvertTo-Json -Depth 4|Out-File -LiteralPath (Join-Path $output 'matrix.json') -Encoding utf8
Write-Output "PASS Historical upgrade, rollback and large-migration matrix. Clean OS testing is separate: $output"
$global:LASTEXITCODE=0
