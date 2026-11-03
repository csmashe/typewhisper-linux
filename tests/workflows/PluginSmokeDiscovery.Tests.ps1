$ErrorActionPreference = 'Stop'

$selectorPath = Join-Path $PSScriptRoot '../../scripts/select-plugin-smoke-projects.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("plugin-smoke-discovery-{0}" -f [guid]::NewGuid())
$workspaceRoot = Join-Path $testRoot 'workspace'
$projectPaths = @(
  'plugins/TypeWhisper.Plugin.SherpaOnnx/TypeWhisper.Plugin.SherpaOnnx.csproj'
  'plugins/TypeWhisper.Plugin.WhisperCpp/TypeWhisper.Plugin.WhisperCpp.csproj'
)

function Assert-Equal {
  param(
    [Parameter(Mandatory = $true)] $Expected,
    [Parameter(Mandatory = $true)] $Actual,
    [Parameter(Mandatory = $true)] [string] $Message
  )

  if ($Expected -cne $Actual) {
    throw "$Message`nExpected: $Expected`nActual:   $Actual"
  }
}

function Invoke-DiscoveryCase {
  param(
    [Parameter(Mandatory = $true)] [string] $Name,
    [string[]] $ChangedFiles = @(),
    [string] $EventName = 'pull_request',
    [Parameter(Mandatory = $true)] [string] $ExpectedProjects,
    [Parameter(Mandatory = $true)] [string] $ExpectedScanMode
  )

  $result = & $selectorPath `
    -ChangedFiles $ChangedFiles `
    -EventName $EventName `
    -WorkspaceRoot $workspaceRoot

  Assert-Equal $ExpectedProjects $result.Projects "$Name returned the wrong projects JSON."
  Assert-Equal $ExpectedScanMode $result.ScanMode "$Name returned the wrong scan mode."
  $expectedHasProjects = if ($ExpectedProjects -eq '[]') { 'false' } else { 'true' }
  Assert-Equal $expectedHasProjects $result.HasProjects "$Name returned the wrong has_projects value."
  Write-Host "PASS: $Name"
}

try {
  foreach ($projectPath in $projectPaths) {
    $fullPath = Join-Path $workspaceRoot $projectPath
    New-Item -ItemType Directory -Path (Split-Path $fullPath -Parent) -Force | Out-Null
    Set-Content -LiteralPath $fullPath -Value '<Project />'
  }

  $catalogPath = Join-Path $workspaceRoot 'plugins/catalog.json'
  $catalog = [ordered]@{
    schemaVersion = 1
    plugins = @(
      $projectPaths | ForEach-Object { [ordered]@{ projectPath = $_ } }
    )
  }
  $catalog | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $catalogPath

  $allProjectsJson = '["plugins/TypeWhisper.Plugin.SherpaOnnx/TypeWhisper.Plugin.SherpaOnnx.csproj","plugins/TypeWhisper.Plugin.WhisperCpp/TypeWhisper.Plugin.WhisperCpp.csproj"]'
  $whisperCppJson = '["plugins/TypeWhisper.Plugin.WhisperCpp/TypeWhisper.Plugin.WhisperCpp.csproj"]'

  Invoke-DiscoveryCase `
    -Name 'Shared-only change selects all projects' `
    -ChangedFiles 'plugins/Shared/Audio/Decoder.cs' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Single-plugin change selects only that plugin projects' `
    -ChangedFiles 'plugins/TypeWhisper.Plugin.WhisperCpp/WhisperPlugin.cs' `
    -ExpectedProjects $whisperCppJson `
    -ExpectedScanMode 'changed'

  Invoke-DiscoveryCase `
    -Name 'PluginSDK change selects all projects' `
    -ChangedFiles 'src/TypeWhisper.PluginSDK/Audio/ITranscriber.cs' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Workflow-file change selects all projects' `
    -ChangedFiles '.github/workflows/plugins-smoke.yml' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Selector-script change selects all projects' `
    -ChangedFiles 'scripts/select-plugin-smoke-projects.ps1' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Catalog-tool change selects all projects' `
    -ChangedFiles 'scripts/plugin-catalog.ps1' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Catalog change selects all projects' `
    -ChangedFiles 'plugins/catalog.json' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Plugin build props change selects all projects' `
    -ChangedFiles 'plugins/Directory.Build.props' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Non-plugin change selects no projects' `
    -ChangedFiles 'docs/plugin-development.md' `
    -ExpectedProjects '[]' `
    -ExpectedScanMode 'none'

  Invoke-DiscoveryCase `
    -Name 'Push event selects all projects' `
    -EventName 'push' `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  Invoke-DiscoveryCase `
    -Name 'Mixed Shared and plugin change selects all projects' `
    -ChangedFiles @(
      'plugins/TypeWhisper.Plugin.WhisperCpp/WhisperPlugin.cs'
      'plugins/Shared/Interop/NativeMethods.cs'
    ) `
    -ExpectedProjects $allProjectsJson `
    -ExpectedScanMode 'all'

  $incompleteCatalog = [ordered]@{
    schemaVersion = 1
    plugins = @([ordered]@{ projectPath = $projectPaths[0] })
  }
  $incompleteCatalog | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $catalogPath
  $catalogMismatchRejected = $false
  try {
    & $selectorPath `
      -ChangedFiles @() `
      -EventName 'push' `
      -WorkspaceRoot $workspaceRoot | Out-Null
  } catch {
    if ($_.Exception.Message -notlike '*catalog and filesystem projects disagree*') {
      throw
    }
    $catalogMismatchRejected = $true
  }
  Assert-Equal $true $catalogMismatchRejected 'Catalog/filesystem mismatch was not rejected.'
  Write-Host 'PASS: Catalog/filesystem mismatch fails closed'

  Write-Host 'All 12 plugin smoke discovery tests passed.'
} finally {
  if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
  }
}
