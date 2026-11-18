[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ApplicationDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$ExpectSelectionFailure
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectEnvironment = Join-Path $repo 'Enter-Project.ps1'
if (Test-Path -LiteralPath $projectEnvironment) { . $projectEnvironment }
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Visual fixture must be new.' }
& dotnet run --project (Join-Path $repo 'DoomLauncher.Modern.Tests') -c Release --no-build --no-restore -p:Platform=x64 -- --prepare-performance-ui $root 65
if ($LASTEXITCODE -ne 0) { throw 'Visual fixture preparation failed.' }
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path ([IO.Path]::GetFullPath($ApplicationDirectory)) 'DoomLauncher.WinUI.exe'))
$info.UseShellExecute = $false
$info.WorkingDirectory = Split-Path $info.FileName
$info.Arguments = '--ui-refresh-visual-test'
$info.Environment['DOOMLAUNCHER_DATABASE'] = Join-Path $root 'DoomLauncher.sqlite'
$info.Environment['DOOMLAUNCHER_USER_STATE'] = Join-Path $root 'Data/UserState/state.json'
$info.Environment['DOOMLAUNCHER_DIAGNOSTIC_LOG'] = Join-Path $root 'crash.log'
$info.Environment['DOOMLAUNCHER_UI_TEST_ROOT'] = $root
$process = [Diagnostics.Process]::Start($info)
$report = Join-Path $root 'performance-report.json'
$clock = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath $report) -and $clock.Elapsed.TotalSeconds -lt 60 -and -not $process.HasExited) { Start-Sleep -Milliseconds 200 }
if (-not (Test-Path -LiteralPath $report)) { throw "Missing visual report: $root" }
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if ($result.Error) { throw $result.Error }
if ($ExpectSelectionFailure) {
    if ($result.Passed -or $result.FailedChecks.Count -ne 1 -or $result.FailedChecks[0] -ne 'Refresh retains native selection') { throw 'Stable selection failure was not reproduced.' }
} elseif (-not $result.Passed) { throw "Selection regression: $($result.FailedChecks -join '; ')" }
$screenshot = Join-Path $root 'refresh-selection.png'
if (-not (Test-Path -LiteralPath $screenshot)) { throw 'Native screenshot is missing.' }
if (-not $process.WaitForExit(5000)) { throw 'Visual fixture did not close normally.' }
Write-Output "PASS Native refresh selection visual probe; expected baseline failure: $ExpectSelectionFailure. Screenshot: $screenshot"
