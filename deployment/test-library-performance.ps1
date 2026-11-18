[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ApplicationDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(3,10)][int]$Runs = 3,
    [switch]$KeepLastWindow,
    [switch]$RecordFailures
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectEnvironment = Join-Path $repo 'Enter-Project.ps1'
if (Test-Path -LiteralPath $projectEnvironment) { . $projectEnvironment }
$application = Join-Path ([IO.Path]::GetFullPath($ApplicationDirectory)) 'DoomLauncher.WinUI.exe'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Use a new output directory: $output" }
[void][IO.Directory]::CreateDirectory($output)
$reports = @()
foreach ($size in @(65,11000)) {
    for ($run=1; $run -le $Runs; $run++) {
        $root = Join-Path $output "$size-$run"
        & dotnet run --project (Join-Path $repo 'DoomLauncher.Modern.Tests') -c Release --no-build --no-restore -p:Platform=x64 -- --prepare-performance-ui $root $size
        if ($LASTEXITCODE -ne 0) { throw 'Fixture preparation failed.' }
        $info = [Diagnostics.ProcessStartInfo]::new($application)
        $info.WorkingDirectory = Split-Path $application
        $info.UseShellExecute = $false
        $info.Arguments = '--ui-performance-test'
        if ($KeepLastWindow -and $size -eq 11000 -and $run -eq $Runs) { $info.Arguments += ' --keep-test-window' }
        $info.Environment['DOOMLAUNCHER_DATABASE'] = Join-Path $root 'DoomLauncher.sqlite'
        $info.Environment['DOOMLAUNCHER_USER_STATE'] = Join-Path $root 'Data/UserState/state.json'
        $info.Environment['DOOMLAUNCHER_DIAGNOSTIC_LOG'] = Join-Path $root 'crash.log'
        $info.Environment['DOOMLAUNCHER_UI_TEST_ROOT'] = $root
        $process = [Diagnostics.Process]::Start($info)
        $report = Join-Path $root 'performance-report.json'
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $report) -and $clock.Elapsed.TotalSeconds -lt 180 -and -not $process.HasExited) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $report)) { throw "Missing UI report: $root" }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if (-not $result.Passed -and -not $RecordFailures) { throw "UI performance check failed: $($result.Error) $($result.FailedChecks -join '; '). Fixture: $root" }
        $keepWarmWindow = $KeepLastWindow -and $size -eq 11000 -and $run -eq $Runs
        if ($keepWarmWindow -and -not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { throw "Test window did not close normally: $root" }
        }
        if ($process.HasExited -or $process.WaitForExit(5000)) {
            Move-Item -LiteralPath $report -Destination (Join-Path $root 'performance-full.json')
            $info.Arguments = '--ui-startup-test'
            if ($keepWarmWindow) { $info.Arguments += ' --keep-test-window' }
            $warmProcess = [Diagnostics.Process]::Start($info)
            $warmClock = [Diagnostics.Stopwatch]::StartNew()
            while (-not (Test-Path -LiteralPath $report) -and $warmClock.Elapsed.TotalSeconds -lt 60 -and -not $warmProcess.HasExited) { Start-Sleep -Milliseconds 200 }
            if (-not (Test-Path -LiteralPath $report)) { throw "Missing warm startup report: $root" }
            $warm = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            if (-not $warm.Passed) { throw "Warm startup failed: $($warm.Error)" }
            $result.MeasurementsMs | Add-Member -NotePropertyName 'warm-process-start-to-library' -NotePropertyValue $warm.MeasurementsMs.'process-start-to-library'
        }
        $reports += [pscustomobject]@{ Size=$size; Run=$run; Report=$result }
        Write-Output "MEASURE native performance $size / $run; checks passed: $($result.Passed)"
    }
}
$reports | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
