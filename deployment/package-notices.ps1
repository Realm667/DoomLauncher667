param([Parameter(Mandatory=$true)][string]$RepositoryRoot, [Parameter(Mandatory=$true)][string]$PackageRoot)
$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'DoomLauncher.WinUI/obj/project.assets.json') -Raw | ConvertFrom-Json
$folders = @($assets.packageFolders.PSObject.Properties.Name)
$packages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Value.path })
foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
    foreach ($download in $framework.downloadDependencies) {
        $number = $download.version.Trim('[', ']').Split(',')[0].Trim()
        $packages += "$($download.name.ToLowerInvariant())/$number"
    }
}
$noticeRoot = Join-Path $PackageRoot 'Licenses'
[void](New-Item -ItemType Directory -Path $noticeRoot -Force)
Copy-Item -Path (Join-Path $RepositoryRoot 'deployment/licenses/*') -Destination $noticeRoot -Recurse -Force
$inventory = @('# Restored dependency notices', '', 'Includes runtime dependencies and build-time tooling; not every listed component is shipped as an executable.', '', '| Package | License declared by package | Copyright |', '| --- | --- | --- |')
foreach ($package in ($packages | Sort-Object -Unique)) {
    $source = $folders | ForEach-Object { Join-Path $_ $package } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $source) { throw "Restored package unavailable for notices: $package" }
    $nuspec = Get-ChildItem -LiteralPath $source -Filter '*.nuspec' -File | Select-Object -First 1
    [xml]$spec = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $spec.SelectSingleNode('//*[local-name()="metadata"]')
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    $copyrightNode = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    $license = if ($licenseNode) { $licenseNode.InnerText } else { 'See package license/notice' }
    $copyright = if ($copyrightNode) { $copyrightNode.InnerText.Replace('|', '/') } else { '' }
    $inventory += "| $package | $license | $copyright |"
    $destination = Join-Path $noticeRoot ($package.Replace('/', '-'))
    [void](New-Item -ItemType Directory -Path $destination -Force)
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination
    $texts = @(Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object {
        $_.Name -match '(?i)(licen[sc]e|notice|copying)' -and $_.Extension -in @('', '.txt', '.md', '.rtf', '.html', '.htm')
    })
    foreach ($file in $texts) {
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $target = Join-Path $destination $relative
        [void](New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force)
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    if ($licenseNode -and $licenseNode.GetAttribute('type') -eq 'file') {
        $original = Join-Path $source $license
        if (-not (Test-Path -LiteralPath $original)) { throw "Missing declared license file: $package/$license" }
        Copy-Item -LiteralPath $original -Destination (Join-Path $destination 'DECLARED-LICENSE.txt')
    }
}
[IO.File]::WriteAllLines((Join-Path $noticeRoot 'DEPENDENCIES.md'), $inventory, [Text.UTF8Encoding]::new($false))
