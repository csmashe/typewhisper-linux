[CmdletBinding()]
param(
    [ValidateSet('Validate', 'ReleaseMatrix', 'DeployMap')]
    [string] $View = 'Validate',
    [string] $Platform,
    [string] $Rid,
    [string] $WorkspaceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = Split-Path $PSScriptRoot -Parent
}

$workspacePath = [System.IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
)
$catalogPath = Join-Path $workspacePath 'plugins/catalog.json'
$allowedPlatforms = @('linux', 'windows', 'macos')
$ridPlatforms = [ordered]@{
    'linux-x64' = 'linux'
    'linux-arm64' = 'linux'
    'win-x64' = 'windows'
    'win-arm64' = 'windows'
    'osx-x64' = 'macos'
    'osx-arm64' = 'macos'
}
# The dictionary itself matches keys case-insensitively, but every consumer selects RIDs
# case-sensitively, so a 'Linux-X64' entry would validate and then vanish from DeployMap.
$knownRids = @($ridPlatforms.Keys)

function Assert-Text {
    param(
        [Parameter(Mandatory)]
        [string] $Name,
        [AllowNull()]
        [object] $Value
    )

    if ([string]::IsNullOrWhiteSpace([string] $Value)) {
        throw "$Name must be a non-empty string."
    }
}

function Assert-ExactProperties {
    param(
        [Parameter(Mandatory)]
        [object] $InputObject,
        [Parameter(Mandatory)]
        [string[]] $Required,
        [string[]] $Optional = @(),
        [Parameter(Mandatory)]
        [string] $Context
    )

    $actual = @($InputObject.PSObject.Properties.Name)
    foreach ($name in $Required) {
        if ($actual -cnotcontains $name) {
            throw "$Context is missing required property '$name'."
        }
    }

    $allowed = @($Required) + @($Optional)
    foreach ($name in $actual) {
        if ($allowed -cnotcontains $name) {
            throw "$Context contains unsupported property '$name'."
        }
    }
}

function Assert-UniqueStrings {
    param(
        [Parameter(Mandatory)]
        [object[]] $Values,
        [Parameter(Mandatory)]
        [string] $Context
    )

    if ($Values.Count -eq 0) {
        throw "$Context must contain at least one value."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal
    )
    foreach ($value in $Values) {
        Assert-Text -Name $Context -Value $value
        if (-not $seen.Add([string] $value)) {
            throw "$Context contains duplicate value '$value'."
        }
    }
}

function Assert-ExactRelativePath {
    param(
        [Parameter(Mandatory)]
        [string] $Root,
        [Parameter(Mandatory)]
        [string] $RelativePath
    )

    $current = $Root
    foreach ($segment in $RelativePath.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -in '.', '..') {
            throw "Catalog projectPath is not canonical: $RelativePath"
        }

        # Not $matches: that is PowerShell's automatic -match capture variable.
        $segmentMatches = @(
            Get-ChildItem -LiteralPath $current -Force |
                Where-Object { $_.Name -ceq $segment }
        )
        if ($segmentMatches.Count -ne 1) {
            throw "Catalog projectPath does not exist with exact casing: $RelativePath"
        }
        $current = $segmentMatches[0].FullName
    }

    return $current
}

function Read-JsonObject {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description does not exist: $Path"
    }

    try {
        $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        throw "Invalid JSON in ${Path}: $($_.Exception.Message)"
    }

    if ($null -eq $json -or $json -is [System.Array]) {
        throw "$Description must contain a top-level JSON object: $Path"
    }
    return $json
}

$catalog = Read-JsonObject -Path $catalogPath -Description 'Plugin catalog'
Assert-ExactProperties `
    -InputObject $catalog `
    -Required @('schemaVersion', 'plugins') `
    -Context 'Plugin catalog'
if ($catalog.schemaVersion -ne 1) {
    throw "Unsupported plugin catalog schemaVersion '$($catalog.schemaVersion)'."
}

$plugins = @($catalog.plugins)
if ($plugins.Count -eq 0) {
    throw 'Plugin catalog must contain at least one plugin.'
}

$ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$slugs = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$projects = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

foreach ($plugin in $plugins) {
    Assert-ExactProperties `
        -InputObject $plugin `
        -Required @('id', 'projectPath', 'releaseSlug', 'platforms', 'rids', 'sdkAbi') `
        -Optional @('nativeRuntimes') `
        -Context 'Plugin catalog entry'

    $id = [string] $plugin.id
    $projectPath = [string] $plugin.projectPath
    $releaseSlug = [string] $plugin.releaseSlug
    $sdkAbi = [string] $plugin.sdkAbi
    Assert-Text -Name 'Plugin id' -Value $id
    Assert-Text -Name "projectPath for '$id'" -Value $projectPath
    Assert-Text -Name "releaseSlug for '$id'" -Value $releaseSlug
    Assert-Text -Name "sdkAbi for '$id'" -Value $sdkAbi

    if ($id -cnotmatch '^com\.typewhisper\.[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw "Plugin id is not lowercase canonical form: $id"
    }
    if ($releaseSlug -cnotmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') {
        throw "releaseSlug is not lowercase canonical form for '$id': $releaseSlug"
    }
    if ($projectPath -cnotmatch '^plugins/TypeWhisper\.Plugin\.[A-Za-z0-9]+/TypeWhisper\.Plugin\.[A-Za-z0-9]+\.csproj$') {
        throw "projectPath is not canonical for '$id': $projectPath"
    }
    if ($sdkAbi -cne 'net10.0') {
        throw "Unsupported sdkAbi for '$id': $sdkAbi"
    }

    if (-not $ids.Add($id)) {
        throw "Duplicate plugin id in catalog: $id"
    }
    if (-not $slugs.Add($releaseSlug)) {
        throw "Duplicate releaseSlug in catalog: $releaseSlug"
    }
    if (-not $projects.Add($projectPath)) {
        throw "Duplicate projectPath in catalog: $projectPath"
    }

    $platforms = @($plugin.platforms)
    $rids = @($plugin.rids)
    Assert-UniqueStrings -Values $platforms -Context "platforms for '$id'"
    Assert-UniqueStrings -Values $rids -Context "rids for '$id'"

    foreach ($entryPlatform in $platforms) {
        if ($allowedPlatforms -cnotcontains [string] $entryPlatform) {
            throw "Unknown platform for '$id': $entryPlatform"
        }
    }
    foreach ($entryRid in $rids) {
        if ($knownRids -cnotcontains [string] $entryRid) {
            throw "Unknown RID for '$id': $entryRid"
        }
        $ridPlatform = [string] $ridPlatforms[[string] $entryRid]
        if ($platforms -cnotcontains $ridPlatform) {
            throw "RID '$entryRid' requires platform '$ridPlatform' for '$id'."
        }
    }

    if ($plugin.PSObject.Properties.Name -ccontains 'nativeRuntimes') {
        $nativeRuntimes = @($plugin.nativeRuntimes)
        Assert-UniqueStrings -Values $nativeRuntimes -Context "nativeRuntimes for '$id'"
        foreach ($nativeRid in $nativeRuntimes) {
            if ($knownRids -cnotcontains [string] $nativeRid) {
                throw "Unknown native runtime RID for '$id': $nativeRid"
            }
            if ($rids -cnotcontains [string] $nativeRid) {
                throw "Native runtime RID '$nativeRid' is not a supported RID for '$id'."
            }
        }
    }

    $fullProjectPath = Assert-ExactRelativePath -Root $workspacePath -RelativePath $projectPath
    if (-not (Test-Path -LiteralPath $fullProjectPath -PathType Leaf)) {
        throw "Catalog projectPath is not a file: $projectPath"
    }
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($fullProjectPath)
    $projectDirectoryName = Split-Path (Split-Path $fullProjectPath -Parent) -Leaf
    if ($projectName -cne $projectDirectoryName) {
        throw "Project file name must exactly match its directory for '$id': $projectPath"
    }

    $manifestPath = Join-Path (Split-Path $fullProjectPath -Parent) 'manifest.json'
    $manifest = Read-JsonObject -Path $manifestPath -Description "Manifest for '$id'"
    if ([string] $manifest.id -cne $id) {
        throw "Catalog id '$id' does not match manifest id '$($manifest.id)'."
    }
}

$pluginsPath = Join-Path $workspacePath 'plugins'
$filesystemProjects = @(
    Get-ChildItem -LiteralPath $pluginsPath -Directory -Filter 'TypeWhisper.Plugin.*' |
        ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.csproj' } |
        ForEach-Object {
            [System.IO.Path]::GetRelativePath($workspacePath, $_.FullName).Replace('\', '/')
        } |
        Sort-Object
)
$catalogProjects = @($projects | Sort-Object)
$projectDifference = @(
    Compare-Object -ReferenceObject $catalogProjects -DifferenceObject $filesystemProjects -CaseSensitive
)
if ($projectDifference.Count -gt 0) {
    $details = $projectDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
    throw "Plugin catalog and filesystem projects disagree:`n$($details -join [Environment]::NewLine)"
}

switch ($View) {
    'Validate' {
        Write-Output "Validated $($plugins.Count) plugin catalog entries."
    }
    'ReleaseMatrix' {
        $include = @(
            $plugins |
                Sort-Object releaseSlug |
                ForEach-Object {
                    $projectName = [System.IO.Path]::GetFileNameWithoutExtension(
                        [string] $_.projectPath
                    )
                    [pscustomobject][ordered]@{
                        id = [string] $_.id
                        releaseSlug = [string] $_.releaseSlug
                        projectPath = [string] $_.projectPath
                        projectName = $projectName
                        projectDirectory = [System.IO.Path]::GetDirectoryName(
                            [string] $_.projectPath
                        ).Replace('\', '/')
                        platforms = @($_.platforms)
                        rids = @($_.rids)
                        sdkAbi = [string] $_.sdkAbi
                    }
                }
        )
        [pscustomobject][ordered]@{ include = $include } |
            ConvertTo-Json -Depth 6 -Compress
    }
    'DeployMap' {
        Assert-Text -Name 'Platform' -Value $Platform
        Assert-Text -Name 'Rid' -Value $Rid
        if ($allowedPlatforms -cnotcontains $Platform) {
            throw "Unknown deploy platform: $Platform"
        }
        if ($knownRids -cnotcontains $Rid) {
            throw "Unknown deploy RID: $Rid"
        }
        if ([string] $ridPlatforms[$Rid] -cne $Platform) {
            throw "Deploy RID '$Rid' does not belong to platform '$Platform'."
        }

        $deployPlugins = @(
            $plugins |
                Where-Object {
                    @($_.platforms) -ccontains $Platform -and @($_.rids) -ccontains $Rid
                } |
                Sort-Object id
        )
        if ($deployPlugins.Count -eq 0) {
            throw "No plugins support $Platform/$Rid."
        }

        Write-Output 'declare -A PLUGINS=('
        foreach ($plugin in $deployPlugins) {
            Write-Output "  ['$([string] $plugin.id)']='$([string] $plugin.projectPath)'"
        }
        Write-Output ')'
        Write-Output 'declare -a PLUGIN_IDS=('
        foreach ($plugin in $deployPlugins) {
            Write-Output "  '$([string] $plugin.id)'"
        }
        Write-Output ')'
    }
}
