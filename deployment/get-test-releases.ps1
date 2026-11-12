param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach($version in @('v0.8.1','v0.8.10','v0.9.0','v0.9.3','v1.0.0-rc.1','v1.0.0-rc.15')){
    $target=Join-Path $output $version
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    gh release download $version --repo Realm667/DoomLauncher667 --pattern '*.zip' --pattern '*.sha256' --dir $target --skip-existing
    if($LASTEXITCODE -ne 0){throw "Historical download failed: $version"}
    $archive=@(Get-ChildItem -LiteralPath $target -Filter '*.zip' -File)
    if($archive.Count -ne 1){throw "Expected one original package: $version"}
    $checksum=(Get-Content -LiteralPath ($archive[0].FullName+'.sha256') -Raw).Split(' ')[0].Trim()
    if((Get-FileHash -LiteralPath $archive[0].FullName -Algorithm SHA256).Hash -ne $checksum){throw "Historical checksum mismatch: $version"}
    $extracted=Join-Path $target 'extracted'
    if(-not (Test-Path -LiteralPath $extracted)){Expand-Archive -LiteralPath $archive[0].FullName -DestinationPath $extracted}
    Write-Output "PASS Original package and checksum: $version"
}
$global:LASTEXITCODE=0
