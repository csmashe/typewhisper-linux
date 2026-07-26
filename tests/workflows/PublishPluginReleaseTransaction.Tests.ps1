BeforeAll {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
    . (Join-Path $repositoryRoot 'scripts/publish-plugin-release.ps1')

    function New-CommandResult {
        param(
            [int]$ExitCode = 0,
            [string[]]$Output = @()
        )

        [pscustomobject]@{
            ExitCode = $ExitCode
            Output = $Output
        }
    }
}

Describe 'Publish plugin release transaction' {
    BeforeEach {
        $script:tag = 'plugin-example-v2.0.0'
        $script:repository = 'example/typewhisper'
        $script:commitSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $script:projectName = 'TypeWhisper.Plugin.Example'
        $script:pluginId = 'example'
        $script:pluginVersion = '2.0.0'
        $script:releaseState = 'missing'
        $script:releaseTarget = $script:commitSha
        $script:releaseAssets = @()
        $script:releaseQueryError = $false
        $script:remainingPushFailures = 0
        $script:ghCalls = [System.Collections.Generic.List[string]]::new()
        $script:gitCalls = [System.Collections.Generic.List[string]]::new()
        $script:invocations = [System.Collections.Generic.List[string]]::new()
        $script:events = [System.Collections.Generic.List[string]]::new()

        $script:fixtureRoot = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $script:packageRoot = Join-Path $script:fixtureRoot 'package'
        $script:worktreePath = Join-Path $script:fixtureRoot 'gh-pages-work'
        New-Item -ItemType Directory -Path $script:packageRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $script:worktreePath -Force | Out-Null

        $manifest = [ordered]@{
            id = $script:pluginId
            name = 'Example'
            version = $script:pluginVersion
            assemblyName = 'TypeWhisper.Plugin.Example.dll'
            minHostVersion = '1.0.0'
            author = 'TypeWhisper'
            description = 'Example plugin'
            category = 'Utility'
            iconSystemName = 'PuzzlePiece'
            requiresApiKey = $false
            descriptions = [ordered]@{
                en = 'Example plugin'
            }
        }

        $script:manifestPath = Join-Path $script:fixtureRoot 'manifest.json'
        ConvertTo-Json -InputObject $manifest -Depth 10 |
            Set-Content -LiteralPath $script:manifestPath -Encoding utf8NoBOM
        Copy-Item -LiteralPath $script:manifestPath -Destination (Join-Path $script:packageRoot 'manifest.json')
        Set-Content `
            -LiteralPath (Join-Path $script:packageRoot 'TypeWhisper.Plugin.Example.dll') `
            -Value 'deterministic test assembly' `
            -Encoding utf8NoBOM

        $script:zipPath = Join-Path $script:fixtureRoot "$($script:pluginId)-$($script:pluginVersion).zip"
        Compress-Archive -Path (Join-Path $script:packageRoot '*') -DestinationPath $script:zipPath
        $script:zipSize = (Get-Item -LiteralPath $script:zipPath).Length

        $oldRegistry = @(
            [ordered]@{
                id = $script:pluginId
                name = 'Example'
                version = '1.0.0'
                size = 10
                downloadUrl = 'https://example.invalid/old.zip'
            }
        )
        ConvertTo-Json -InputObject $oldRegistry -Depth 10 |
            Set-Content -LiteralPath (Join-Path $script:worktreePath 'plugins.json') -Encoding utf8NoBOM

        Mock Invoke-GitCommand {
            param(
                [string[]]$Arguments,
                [string]$WorkingDirectory,
                [switch]$AllowFailure
            )

            $command = $Arguments -join ' '
            $script:gitCalls.Add($command)
            $script:invocations.Add("git $command")

            if ($command -eq 'rev-parse --is-inside-work-tree') {
                return New-CommandResult -Output @('true')
            }
            if ($command -eq 'diff --cached --quiet') {
                return New-CommandResult -ExitCode 1
            }
            if ($command -eq 'push origin gh-pages') {
                if ($script:remainingPushFailures -gt 0) {
                    $script:remainingPushFailures--
                    $script:events.Add('registry-push-failed')
                    throw 'simulated registry push failure'
                }

                $script:events.Add('registry-pushed')
            }

            return New-CommandResult
        }

        Mock Invoke-GhCommand {
            param(
                [string[]]$Arguments,
                [switch]$AllowFailure
            )

            $command = $Arguments -join ' '
            $script:ghCalls.Add($command)
            $script:invocations.Add("gh $command")

            if ($command -match '^api --method GET repos/.+/commits/.+ --jq \.sha$') {
                return New-CommandResult -Output @($script:commitSha)
            }

            if ($command -match '^api --include --method GET repos/.+/releases/tags/') {
                if ($script:releaseQueryError) {
                    return New-CommandResult `
                        -ExitCode 1 `
                        -Output @('HTTP/2 503 Service Unavailable', '', '{"message":"unavailable"}')
                }
                if ($script:releaseState -eq 'missing') {
                    return New-CommandResult `
                        -ExitCode 1 `
                        -Output @('HTTP/2 404 Not Found', '', '{"message":"Not Found"}')
                }

                $releaseJson = [ordered]@{
                    id = 42
                    tag_name = $script:tag
                    target_commitish = $script:releaseTarget
                    draft = $script:releaseState -eq 'draft'
                    assets = @($script:releaseAssets)
                } | ConvertTo-Json -Depth 10 -Compress
                return New-CommandResult -Output @('HTTP/2 200 OK', '', $releaseJson)
            }

            if ($command -match '^release create ') {
                if ($script:releaseState -ne 'missing') {
                    throw 'duplicate release creation'
                }

                $script:releaseState = if ($Arguments -contains '--draft') { 'draft' } else { 'public' }
                $script:releaseTarget = $script:commitSha
                $script:releaseAssets = @()
                $script:events.Add("$($script:releaseState)-created")
                return New-CommandResult
            }

            if ($command -match '^release upload ') {
                if ($script:releaseState -ne 'draft') {
                    throw 'asset upload attempted for a non-draft release'
                }

                $script:releaseAssets = @(
                    [ordered]@{
                        name = [System.IO.Path]::GetFileName($script:zipPath)
                        size = $script:zipSize
                        state = 'uploaded'
                    }
                )
                $script:events.Add('asset-uploaded')
                return New-CommandResult
            }

            if ($command -match '^release edit ') {
                if ($script:releaseState -ne 'draft') {
                    throw 'publication attempted for a non-draft release'
                }

                $script:releaseState = 'public'
                $script:events.Add('release-published')
                return New-CommandResult
            }

            throw "Unexpected gh command: $command"
        }

        $script:transactionParameters = @{
            Tag = $script:tag
            Repository = $script:repository
            CommitSha = $script:commitSha
            ProjectName = $script:projectName
            PluginVersion = $script:pluginVersion
            PluginId = $script:pluginId
            ZipPath = $script:zipPath
            ManifestPath = $script:manifestPath
            RegistryWorktreePath = $script:worktreePath
            MaxPushAttempts = 1
        }
    }

    It 'resumes a draft after a registry push failure and publishes only after repair' {
        $script:remainingPushFailures = 1

        { Invoke-PluginReleaseTransaction @script:transactionParameters } |
            Should -Throw '*Registry push failed*'

        $script:releaseState | Should -Be 'draft'
        $createCalls = @($script:ghCalls | Where-Object { $_ -match '^release create ' })
        $createCalls.Count | Should -Be 1
        $createCalls[0] | Should -Match ' --draft(?: |$)'
        $createCalls[0] | Should -Match " --target $([regex]::Escape($script:commitSha))(?: |$)"
        $createCalls[0] | Should -Match ' --verify-tag(?: |$)'
        @($script:ghCalls | Where-Object { $_ -match '^release edit .* --draft=false(?: |$)' }).Count |
            Should -Be 0

        Invoke-PluginReleaseTransaction @script:transactionParameters

        $script:releaseState | Should -Be 'public'
        @($script:ghCalls | Where-Object { $_ -match '^release create ' }).Count | Should -Be 1
        @($script:ghCalls | Where-Object { $_ -match '^release upload .* --clobber$' }).Count | Should -Be 1
        $publishCall = "gh release edit $($script:tag) --repo $($script:repository) --draft=false"
        @($script:invocations | Where-Object { $_ -eq $publishCall }).Count | Should -Be 1
        $successfulPushIndex = $script:invocations.LastIndexOf('git push origin gh-pages')
        $publishIndex = $script:invocations.IndexOf($publishCall)
        $successfulPushIndex | Should -BeGreaterThan -1
        $publishIndex | Should -BeGreaterThan $successfulPushIndex
        $script:events[-2] | Should -Be 'registry-pushed'
        $script:events[-1] | Should -Be 'release-published'

        $registry = Get-Content -LiteralPath (Join-Path $script:worktreePath 'plugins.json') -Raw |
            ConvertFrom-Json
        $registry[0].version | Should -Be $script:pluginVersion
        [long]$registry[0].size | Should -Be $script:zipSize
    }

    It 'repairs the registry for a verified existing public release without recreating it' {
        $script:releaseState = 'public'
        $script:releaseTarget = 'linux'
        $script:releaseAssets = @(
            [ordered]@{
                name = [System.IO.Path]::GetFileName($script:zipPath)
                size = $script:zipSize
                state = 'uploaded'
            }
        )

        Invoke-PluginReleaseTransaction @script:transactionParameters

        $script:events[-1] | Should -Be 'registry-pushed'
        @($script:ghCalls | Where-Object { $_ -match '^release create ' }).Count | Should -Be 0
        @($script:ghCalls | Where-Object { $_ -match '^release upload ' }).Count | Should -Be 0
        @($script:ghCalls | Where-Object { $_ -match '^release edit ' }).Count | Should -Be 0
    }

    It 'refuses to downgrade the registry when a newer version is already published' {
        $newerRegistry = @(
            [ordered]@{
                id = $script:pluginId
                name = 'Example'
                version = '3.0.0'
                size = 20
                downloadUrl = 'https://example.invalid/new.zip'
            }
        )
        ConvertTo-Json -InputObject $newerRegistry -Depth 10 |
            Set-Content -LiteralPath (Join-Path $script:worktreePath 'plugins.json') -Encoding utf8NoBOM

        { Invoke-PluginReleaseTransaction @script:transactionParameters } |
            Should -Throw '*refusing to downgrade*'

        @($script:ghCalls | Where-Object { $_ -match '^release create ' }).Count | Should -Be 0
        @($script:gitCalls | Where-Object { $_ -eq 'push origin gh-pages' }).Count | Should -Be 0
        @($script:ghCalls | Where-Object { $_ -match '^release edit ' }).Count | Should -Be 0

        $registry = Get-Content -LiteralPath (Join-Path $script:worktreePath 'plugins.json') -Raw |
            ConvertFrom-Json
        $registry[0].version | Should -Be '3.0.0'
    }

    It 'fails closed when the release query errors' {
        $script:releaseQueryError = $true

        { Invoke-PluginReleaseTransaction @script:transactionParameters } |
            Should -Throw '*Release query*failed*'

        $script:releaseState | Should -Be 'missing'
        @($script:ghCalls | Where-Object { $_ -match '^release create ' }).Count | Should -Be 0
        @($script:gitCalls | Where-Object { $_ -eq 'push origin gh-pages' }).Count | Should -Be 0
        @($script:ghCalls | Where-Object { $_ -match '^release edit ' }).Count | Should -Be 0
    }
}
