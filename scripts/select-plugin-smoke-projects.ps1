[CmdletBinding()]
param(
  [string[]] $ChangedFiles = @(),
  [Parameter(Mandatory = $true)]
  [string] $EventName,
  [Parameter(Mandatory = $true)]
  [string] $WorkspaceRoot
)

$workspacePath = [System.IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd(
  [System.IO.Path]::DirectorySeparatorChar,
  [System.IO.Path]::AltDirectorySeparatorChar
)
$pluginsPath = Join-Path $workspacePath 'plugins'
$allProjects = @(Get-ChildItem -LiteralPath $pluginsPath -Recurse -Filter *.csproj | Sort-Object FullName)
if ($allProjects.Count -eq 0) {
  throw "No plugin projects found under plugins/."
}

$catalogPath = Join-Path $pluginsPath 'catalog.json'
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
  throw "Plugin catalog not found: $catalogPath"
}
try {
  $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
} catch {
  throw "Invalid plugin catalog JSON at ${catalogPath}: $($_.Exception.Message)"
}

$filesystemProjectPaths = @(
  $allProjects | ForEach-Object {
    $_.FullName.Replace($workspacePath + [System.IO.Path]::DirectorySeparatorChar, '').Replace('\', '/')
  }
)
$catalogProjectPaths = @($catalog.plugins | ForEach-Object { [string] $_.projectPath } | Sort-Object)
# Case-sensitive, matching the -CaseSensitive parity comparison below: paths differing only in
# casing are two distinct entries there, so they must count as two here as well. Pin the comparer
# explicitly rather than depending on a default that differs between the -Unique implementations.
$uniqueCatalogProjectPaths = @($catalogProjectPaths | Sort-Object -CaseSensitive -Unique)
if ($catalogProjectPaths.Count -ne $uniqueCatalogProjectPaths.Count) {
  throw 'Plugin catalog contains duplicate projectPath values.'
}
$catalogDifference = @(
  Compare-Object `
    -ReferenceObject $catalogProjectPaths `
    -DifferenceObject $filesystemProjectPaths `
    -CaseSensitive
)
if ($catalogDifference.Count -gt 0) {
  $details = $catalogDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
  throw "Plugin catalog and filesystem projects disagree:`n$($details -join [Environment]::NewLine)"
}

$runAll = $EventName -ne 'pull_request'
$selectedPluginDirs = [ordered]@{}
$changedFiles = @($ChangedFiles | ForEach-Object { ([string] $_).Replace('\', '/') })

if (-not $runAll) {
  foreach ($file in $changedFiles) {
    if (
      $file -eq '.github/workflows/plugins-smoke.yml' -or
      # This selector used to live inside plugins-smoke.yml, where editing it
      # implicitly forced a full run. Keep that self-coverage now that it is a
      # separate file.
      $file -eq 'scripts/select-plugin-smoke-projects.ps1' -or
      $file -eq 'Directory.Build.props' -or
      $file -eq 'plugins/Directory.Build.props' -or
      $file -eq 'plugins/catalog.json' -or
      $file -eq 'scripts/plugin-catalog.ps1' -or
      $file -like 'src/TypeWhisper.PluginSDK/*' -or
      $file -like 'plugins/Shared/*'
    ) {
      $runAll = $true
      break
    }

    if ($file -match '^plugins/([^/]+)/') {
      $selectedPluginDirs[$Matches[1]] = $true
    }
  }
}

# Flat string array of plugin .csproj paths. We used to emit objects
# ({name, path}) here, but GitHub Actions' matrix expression engine
# silently dropped most entries when the matrix value was an
# [ordered]-hashtable-derived JSON object — only one matrix job got
# created and ${{ matrix.plugin.path }} rendered as empty in run:
# blocks. A flat string matrix sidesteps that entirely.
$projects = @()
foreach ($project in $allProjects) {
  $relativePath = $project.FullName.Replace($workspacePath + [System.IO.Path]::DirectorySeparatorChar, '').Replace('\', '/')
  $parts = $relativePath.Split('/')
  $pluginDir = if ($parts.Count -ge 2) { $parts[1] } else { '' }

  if ($runAll -or $selectedPluginDirs.Contains($pluginDir)) {
    $projects += $relativePath
  }
}

$scanMode = if ($runAll) { 'all' } elseif ($projects.Count -gt 0) { 'changed' } else { 'none' }
# ConvertTo-Json has two traps here we have to avoid:
#   1. `-InputObject $projects -AsArray` wraps everything in an extra
#      outer array (`[["a","b"]]`), which the matrix engine then
#      treats as one sequence-typed value instead of an array of
#      strings to fan out over. This was the original bug.
#   2. Piping a single-element array unwraps it, so `@("a") | ...`
#      serialises as `"a"`, not `["a"]`.
# Building the JSON by hand for these flat strings sidesteps both.
$hasProjects = if ($projects.Count -gt 0) { 'true' } else { 'false' }
if ($projects.Count -gt 0) {
  $escaped = $projects | ForEach-Object {
    '"' + ($_ -replace '\\', '\\\\' -replace '"', '\"') + '"'
  }
  $json = '[' + ($escaped -join ',') + ']'
} else {
  $json = '[]'
}

Write-Host "Plugin smoke scan mode: $scanMode"
if ($changedFiles.Count -gt 0) {
  Write-Host "Changed files:"
  $changedFiles | ForEach-Object { Write-Host "  $_" }
}
if ($projects.Count -gt 0) {
  Write-Host "Selected plugin projects:"
  $projects | ForEach-Object { Write-Host "  $_" }
} else {
  Write-Host "No plugin projects selected for this pull request."
}

[pscustomobject]@{
  Projects = $json
  HasProjects = $hasProjects
  ScanMode = $scanMode
}
