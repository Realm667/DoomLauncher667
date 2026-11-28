[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ApplicationDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateRange(1,10)][int]$Runs=3,
    [int[]]$Sizes=@(65,11000),
    [switch]$KeepLastWindow
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $repo 'Enter-Project.ps1')
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory.'}
[void][IO.Directory]::CreateDirectory($output)
$exe=Join-Path ([IO.Path]::GetFullPath($ApplicationDirectory)) 'DoomLauncher.WinUI.exe'
$records=@()
foreach($size in $Sizes){
    foreach($run in 1..$Runs){
        $instance=Join-Path $output "$size-$run"
        & dotnet run --project (Join-Path $repo 'DoomLauncher.Modern.Tests') -c Release --no-build --no-restore -p:Platform=x64 -- --prepare-version-performance-ui $instance $size
        if($LASTEXITCODE -ne 0){throw 'Version stress fixture preparation failed.'}
        $start=[Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute=$false
        $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
        $start.WorkingDirectory=Split-Path $exe
        $start.Arguments='--ui-version-group-performance-test'
        if($KeepLastWindow -and $size -eq $Sizes[-1] -and $run -eq $Runs){$start.Arguments+=' --keep-test-window'}
        $start.Environment['DOOMLAUNCHER_DATABASE']=Join-Path $instance 'DoomLauncher.sqlite'
        $start.Environment['DOOMLAUNCHER_USER_STATE']=Join-Path $instance 'Data/UserState/state.json'
        $start.Environment['DOOMLAUNCHER_DIAGNOSTIC_LOG']=Join-Path $instance 'crash.log'
        $start.Environment['DOOMLAUNCHER_UI_TEST_ROOT']=$instance
        $process=[Diagnostics.Process]::Start($start)
        $report=Join-Path $instance 'performance-report.json'
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not(Test-Path -LiteralPath $report) -and $watch.Elapsed.TotalSeconds -lt 180 -and -not $process.HasExited){Start-Sleep -Milliseconds 200}
        if(-not(Test-Path -LiteralPath $report)){throw "Version stress report missing: $instance"}
        $result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
        if(-not $result.Passed){throw "Version stress failed: $($result.Error) $($result.FailedChecks -join '; ')"}
        $records += [pscustomobject]@{Size=$size;Run=$run;Report=$result}
        Write-Output "PASS Native version groups: $size entries / run $run"
    }
}
$records|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
