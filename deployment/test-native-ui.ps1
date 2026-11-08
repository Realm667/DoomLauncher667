param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path ([IO.Path]::GetTempPath()) ('DoomLauncher-native-ui-' + [Guid]::NewGuid().ToString('N'))
& dotnet run --project (Join-Path $repo 'DoomLauncher.Modern.Tests/DoomLauncher.Modern.Tests.csproj') -c Release --no-restore -- --prepare-ui-fixture $root
if ($LASTEXITCODE -ne 0) { throw 'UI fixture preparation failed.' }
Copy-Item -LiteralPath (Join-Path $PackageDirectory 'WinUI') -Destination $root -Recurse
Copy-Item -LiteralPath (Join-Path $PackageDirectory 'DoomLauncher667.exe') -Destination $root
$before = $env:DOOMLAUNCHER_UI_TEST_ROOT
$env:DOOMLAUNCHER_UI_TEST_ROOT = $root
try { Start-Process -FilePath (Join-Path $root 'DoomLauncher667.exe') -ArgumentList '--ui-smoke-test' -WindowStyle Hidden }
finally { $env:DOOMLAUNCHER_UI_TEST_ROOT = $before }
$report = Join-Path $root 'ui-test-report.json'
$timeout = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath $report) -and $timeout.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 200 }
if (-not (Test-Path -LiteralPath $report)) { throw "Native UI tests timed out. Fixture: $root" }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
foreach ($check in $result.Checks) { Write-Output "PASS $check" }
if (-not $result.Passed) { throw "Native UI failure: $($result.Error). Fixture: $root" }
Write-Output "PASS Native UI integration. Report: $report"
$global:LASTEXITCODE = 0
