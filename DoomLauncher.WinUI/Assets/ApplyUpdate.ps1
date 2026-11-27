param([Parameter(Mandatory=$true)][string]$Manifest, [Parameter(Mandatory=$true)][int]$ParentProcessId, [switch]$TestMode, [int]$TestFailAfter = -1)
$ErrorActionPreference = 'Stop'
$taskManifestPath = [IO.Path]::GetFullPath($Manifest)
$taskWork = Split-Path -Parent $taskManifestPath
$taskPlan = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
$taskRoot = [IO.Path]::GetFullPath($taskPlan.Target)
if ($taskRoot.Length -gt [IO.Path]::GetPathRoot($taskRoot).Length) { $taskRoot = $taskRoot.TrimEnd('\') }
$taskPackage = [IO.Path]::GetFullPath($taskPlan.Package).TrimEnd('\')
$taskBackup = Join-Path $taskRoot ('Backups\Program-update-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$taskChanged = [Collections.Generic.List[object]]::new()
$taskLease = $null
function Get-ProgramHash([string]$path) {
    $taskInput = [IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read,1048576,[IO.FileOptions]::SequentialScan)
    $taskHasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($taskHasher.ComputeHash($taskInput)).Replace('-','') }
    finally { $taskHasher.Dispose(); $taskInput.Dispose() }
}
function Resolve-ProgramFile([string]$basePath, [string]$relative) {
    $taskNormalized = $relative.Replace('/','\')
    $taskParts = $taskNormalized.Split('\')
    if ([IO.Path]::IsPathRooted($taskNormalized) -or $taskParts -contains '..' -or $taskParts -contains '.' -or $taskNormalized.Contains(':')) { throw 'Invalid update path.' }
    if ($taskParts.Count -eq 1) {
        if ($taskNormalized -notin @('DoomLauncher667.exe','DoomLauncher667-debug.cmd','DoomLauncher667-reset.cmd','VERSION.txt','README.txt','LICENSE','THIRD-PARTY-NOTICES.md')) { throw 'Non-program root file in update.' }
    } elseif ($taskParts[0] -notin @('WinUI','Licenses','Documentation','Tools')) { throw 'User data is forbidden in updates.' }
    $taskFull = [IO.Path]::GetFullPath((Join-Path $basePath $taskNormalized))
    if (-not $taskFull.StartsWith($basePath.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escapes root.' }
    # A portable installation may contain links. Never follow them outside its program tree.
    $taskCheck = Split-Path -Parent $taskFull
    while ($taskCheck.Length -gt $basePath.Length) {
        if ((Test-Path -LiteralPath $taskCheck) -and ((Get-Item -LiteralPath $taskCheck -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked update directory rejected.' }
        $taskCheck = Split-Path -Parent $taskCheck
    }
    if ((Test-Path -LiteralPath $taskFull) -and ((Get-Item -LiteralPath $taskFull -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked update file rejected.' }
    return $taskFull
}
try {
    if ($TestMode -and -not (Test-Path -LiteralPath (Join-Path $taskRoot '.dl667-update-test'))) { throw 'Marked isolated test installation required.' }
    if (-not $TestMode -and ($TestFailAfter -ge 0 -or $ParentProcessId -le 0)) { throw 'Invalid update process.' }
    if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'DoomLauncher.sqlite'))) { throw 'Portable database missing.' }
    if ($taskPlan.Files.Count -eq 0) { throw 'Empty update.' }
    $taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($taskFile in $taskPlan.Files) {
        if (-not $taskSeen.Add($taskFile.Path)) { throw 'Duplicate update path.' }
        $taskInput = Resolve-ProgramFile $taskPackage $taskFile.Path
        $null = Resolve-ProgramFile $taskRoot $taskFile.Path
        if ((Get-ProgramHash $taskInput) -ne $taskFile.Sha256) { throw 'Update content checksum mismatch.' }
    }
    [IO.File]::WriteAllText((Join-Path $taskWork 'helper-ready'), 'ready')
    if (-not $TestMode) {
        $taskParent = Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
        if ($taskParent -and -not $taskParent.WaitForExit(60000)) { throw 'Launcher did not close; no files updated.' }
    }
    $taskLock = Join-Path $taskRoot 'Data\UserState\installation.lock'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskLock))
    $taskLease = [IO.FileStream]::new($taskLock,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    foreach ($taskFile in $taskPlan.Files) {
        $taskDestination = Resolve-ProgramFile $taskRoot $taskFile.Path
        $taskSaved = Join-Path $taskBackup $taskFile.Path
        if (Test-Path -LiteralPath $taskDestination) {
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskSaved))
            Copy-Item -LiteralPath $taskDestination -Destination $taskSaved
        }
    }
    foreach ($taskFile in $taskPlan.Files) {
        $taskDestination = Resolve-ProgramFile $taskRoot $taskFile.Path
        $taskSaved = Join-Path $taskBackup $taskFile.Path
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $taskDestination))
        $taskChanged.Add([pscustomobject]@{Path=$taskDestination;Backup=$taskSaved;Existed=(Test-Path -LiteralPath $taskDestination)})
        Copy-Item -LiteralPath (Resolve-ProgramFile $taskPackage $taskFile.Path) -Destination $taskDestination -Force
        if ((Get-ProgramHash $taskDestination) -ne $taskFile.Sha256) { throw 'Installed checksum mismatch.' }
        if ($TestMode -and $TestFailAfter -ge 0 -and $taskChanged.Count -ge $TestFailAfter) { throw 'Injected update failure.' }
    }
    [IO.File]::WriteAllText((Join-Path $taskWork 'update-result.json'),'{"Passed":true}')
    $taskLease.Dispose(); $taskLease = $null
    if (-not $TestMode) { Start-Process -FilePath (Join-Path $taskRoot 'DoomLauncher667.exe') -WorkingDirectory $taskRoot -WindowStyle Normal }
    exit 0
} catch {
    $taskFailure = $_.Exception.Message
    $taskRecoveryErrors = [Collections.Generic.List[string]]::new()
    for ($taskIndex=$taskChanged.Count-1; $taskIndex -ge 0; $taskIndex--) {
        $taskChange = $taskChanged[$taskIndex]
        try {
            if ($taskChange.Existed) { Copy-Item -LiteralPath $taskChange.Backup -Destination $taskChange.Path -Force }
            elseif (Test-Path -LiteralPath $taskChange.Path) { Remove-Item -LiteralPath $taskChange.Path -Force }
        } catch { $taskRecoveryErrors.Add($_.Exception.Message) }
    }
    $taskResult = @{Passed=$false;Error=$taskFailure;RecoveryErrors=@($taskRecoveryErrors);Backup=$taskBackup} | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText((Join-Path $taskWork 'update-result.json'),$taskResult)
    if (-not $TestMode) {
        Add-Type -AssemblyName PresentationFramework
        [void][Windows.MessageBox]::Show("Update failed: $taskFailure`nBackup: $taskBackup`nDetails: $taskWork",'Doom Launcher 667')
    }
    exit 1
} finally { if ($taskLease) { $taskLease.Dispose() } }
