BeforeAll {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
    $catalogScript = Join-Path $repositoryRoot 'scripts/plugin-catalog.ps1'

    function New-PluginCatalogFixture {
        param(
            [Parameter(Mandatory)]
            [string] $Root
        )

        $projectName = 'TypeWhisper.Plugin.Example'
        $projectDirectory = Join-Path $Root "plugins/$projectName"
        New-Item -ItemType Directory -Path $projectDirectory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectDirectory "$projectName.csproj") -Value '<Project />'
        [ordered]@{
            id = 'com.typewhisper.example'
            version = '1.2.3'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectDirectory 'manifest.json')

        $catalog = [ordered]@{
            schemaVersion = 1
            plugins = @(
                [ordered]@{
                    id = 'com.typewhisper.example'
                    projectPath = "plugins/$projectName/$projectName.csproj"
                    releaseSlug = 'example'
                    platforms = @('linux')
                    rids = @('linux-x64')
                    sdkAbi = 'net10.0'
                }
            )
        }
        $catalog | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Root 'plugins/catalog.json')
    }
}

Describe 'Plugin catalog generated views' {
    It 'validates all bundled plugins and emits the complete release matrix' {
        (& $catalogScript -View Validate -WorkspaceRoot $repositoryRoot) |
            Should -Be 'Validated 32 plugin catalog entries.'

        $matrix = & $catalogScript -View ReleaseMatrix -WorkspaceRoot $repositoryRoot |
            ConvertFrom-Json
        @($matrix.include).Count | Should -Be 32
        @($matrix.include.releaseSlug | Select-Object -Unique).Count | Should -Be 32
        @($matrix.include[0].PSObject.Properties.Name) | Should -Not -Contain 'version'
    }

    It 'emits a complete deterministic linux-x64 deploy map' {
        $map = @(
            & $catalogScript `
                -View DeployMap `
                -Platform linux `
                -Rid linux-x64 `
                -WorkspaceRoot $repositoryRoot
        )

        @($map | Where-Object { $_ -match "^  \['com\.typewhisper\." }).Count |
            Should -Be 32
        $map[0] | Should -Be 'declare -A PLUGINS=('
        $map[-1] | Should -Be ')'
    }
}

Describe 'Plugin catalog fail-closed validation' {
    BeforeEach {
        $fixtureRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
        New-PluginCatalogFixture -Root $fixtureRoot
    }

    It 'rejects a plugin project missing from the catalog' {
        $extraDirectory = Join-Path $fixtureRoot 'plugins/TypeWhisper.Plugin.Extra'
        New-Item -ItemType Directory -Path $extraDirectory -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $extraDirectory 'TypeWhisper.Plugin.Extra.csproj') -Value '<Project />'

        { & $catalogScript -View Validate -WorkspaceRoot $fixtureRoot } |
            Should -Throw '*catalog and filesystem projects disagree*'
    }

    It 'rejects an unknown RID' {
        $catalogPath = Join-Path $fixtureRoot 'plugins/catalog.json'
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
        $catalog.plugins[0].rids = @('linux-amd64')
        $catalog | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $catalogPath

        { & $catalogScript -View Validate -WorkspaceRoot $fixtureRoot } |
            Should -Throw '*Unknown RID*linux-amd64*'
    }

    It 'rejects a RID whose casing the deploy map would not select' {
        $catalogPath = Join-Path $fixtureRoot 'plugins/catalog.json'
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
        $catalog.plugins[0].rids = @('Linux-X64')
        $catalog | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $catalogPath

        { & $catalogScript -View Validate -WorkspaceRoot $fixtureRoot } |
            Should -Throw '*Unknown RID*Linux-X64*'
    }
}
