param(
    [string]$Tag,
    [string]$Repository,
    [string]$CommitSha,
    [string]$ProjectName,
    [string]$PluginVersion,
    [string]$PluginId,
    [string]$Platform,
    [string]$Rid,
    [string]$SdkAbi,
    [string]$ZipPath,
    [string]$ManifestPath,
    [string]$RegistryWorktreePath = 'gh-pages-work',
    [ValidateRange(1, 20)]
    [int]$MaxPushAttempts = 5
)

function Invoke-GhCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $PSNativeCommandUseErrorActionPreference = $false
    $output = @(& gh @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE

    if (-not $AllowFailure -and $exitCode -ne 0) {
        $detail = ($output -join [Environment]::NewLine).Trim()
        throw "gh $($Arguments -join ' ') failed with exit code ${exitCode}: $detail"
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Invoke-GitCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [string]$WorkingDirectory,
        [switch]$AllowFailure
    )

    $PSNativeCommandUseErrorActionPreference = $false
    $pushedLocation = $false
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Push-Location -LiteralPath $WorkingDirectory
            $pushedLocation = $true
        }

        $output = @(& git @Arguments 2>&1 | ForEach-Object { $_.ToString() })
        $exitCode = $LASTEXITCODE
    } finally {
        if ($pushedLocation) {
            Pop-Location
        }
    }

    if (-not $AllowFailure -and $exitCode -ne 0) {
        $detail = ($output -join [Environment]::NewLine).Trim()
        throw "git $($Arguments -join ' ') failed with exit code ${exitCode}: $detail"
    }

    [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$Name,
        [object]$DefaultValue = $null
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Read-JsonObjectFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $rawJson = Get-Content -LiteralPath $Path -Raw
    $jsonDocument = $null
    try {
        $jsonDocument = [System.Text.Json.JsonDocument]::Parse($rawJson)
        if ($jsonDocument.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
            throw "JSON file must contain a top-level object: $Path"
        }
    } catch {
        if ($_.Exception.Message -like 'JSON file must contain*') {
            throw
        }
        throw "Invalid JSON in ${Path}: $($_.Exception.Message)"
    } finally {
        if ($null -ne $jsonDocument) {
            $jsonDocument.Dispose()
        }
    }

    try {
        return $rawJson | ConvertFrom-Json
    } catch {
        throw "Invalid JSON in ${Path}: $($_.Exception.Message)"
    }
}

function Assert-RequiredText {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [AllowNull()]
        [object]$Value
    )

    if ([string]::IsNullOrWhiteSpace([string]$Value)) {
        throw "$Name must not be empty."
    }
}

function Assert-ZipPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [object]$Manifest,
        [Parameter(Mandatory)]
        [string]$ExpectedPluginId,
        [Parameter(Mandatory)]
        [string]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Plugin ZIP does not exist: $Path"
    }

    $zipItem = Get-Item -LiteralPath $Path
    if ($zipItem.Length -le 0) {
        throw "Plugin ZIP is empty: $Path"
    }

    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zipItem.FullName)
        if ($archive.Entries.Count -eq 0) {
            throw "Plugin ZIP contains no entries: $Path"
        }

        foreach ($entry in $archive.Entries) {
            $normalizedName = $entry.FullName.Replace('\', '/')
            $segments = @($normalizedName.Split('/', [System.StringSplitOptions]::RemoveEmptyEntries))
            if (
                $normalizedName.StartsWith('/') -or
                $normalizedName -match '^[A-Za-z]:' -or
                $segments -contains '..'
            ) {
                throw "Plugin ZIP contains an unsafe entry path: $($entry.FullName)"
            }
        }

        $manifestEntries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'manifest.json' })
        if ($manifestEntries.Count -ne 1) {
            throw "Plugin ZIP must contain exactly one root manifest.json."
        }

        $reader = [System.IO.StreamReader]::new($manifestEntries[0].Open())
        try {
            $archiveManifest = $reader.ReadToEnd() | ConvertFrom-Json
        } catch {
            throw "Plugin ZIP contains an invalid manifest.json: $($_.Exception.Message)"
        } finally {
            $reader.Dispose()
        }

        if ([string]$archiveManifest.id -ne $ExpectedPluginId) {
            throw "Plugin ZIP manifest id '$($archiveManifest.id)' does not match '$ExpectedPluginId'."
        }
        if ([string]$archiveManifest.version -ne $ExpectedVersion) {
            throw "Plugin ZIP manifest version '$($archiveManifest.version)' does not match '$ExpectedVersion'."
        }

        $assemblyName = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'assemblyName')
        Assert-RequiredText -Name 'Manifest assemblyName' -Value $assemblyName
        if ([string]$archiveManifest.assemblyName -ne $assemblyName) {
            throw "Plugin ZIP manifest assemblyName does not match the source manifest."
        }

        $assemblyEntries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $assemblyName })
        if ($assemblyEntries.Count -ne 1 -or $assemblyEntries[0].Length -le 0) {
            throw "Plugin ZIP does not contain the expected root assembly '$assemblyName'."
        }
    } catch {
        if ($_.Exception.Message -like 'Plugin ZIP*') {
            throw
        }
        throw "Plugin ZIP could not be validated: $($_.Exception.Message)"
    } finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
    }
}

function Get-RegistryEntries {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $rawRegistry = Get-Content -LiteralPath $Path -Raw
    $jsonDocument = $null
    try {
        $jsonDocument = [System.Text.Json.JsonDocument]::Parse($rawRegistry)
        if ($jsonDocument.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Array) {
            throw "Registry must contain a top-level JSON array: $Path"
        }
    } catch {
        if ($_.Exception.Message -like 'Registry must contain*') {
            throw
        }
        throw "Invalid JSON in ${Path}: $($_.Exception.Message)"
    } finally {
        if ($null -ne $jsonDocument) {
            $jsonDocument.Dispose()
        }
    }

    try {
        $entries = @($rawRegistry | ConvertFrom-Json)
    } catch {
        throw "Invalid JSON in ${Path}: $($_.Exception.Message)"
    }
    $ids = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        $id = [string](Get-JsonPropertyValue -InputObject $entry -Name 'id')
        Assert-RequiredText -Name "Registry id in $Path" -Value $id
        if (-not $ids.Add($id)) {
            throw "Registry contains duplicate plugin id '$id': $Path"
        }
    }

    return $entries
}

function Write-StagedRegistry {
    param(
        [Parameter(Mandatory)]
        [string]$WorktreePath,
        [Parameter(Mandatory)]
        [object]$Manifest,
        [Parameter(Mandatory)]
        [string]$ExpectedPluginId,
        [Parameter(Mandatory)]
        [string]$ExpectedVersion,
        [Parameter(Mandatory)]
        [long]$ExpectedZipSize,
        [Parameter(Mandatory)]
        [string]$ExpectedDownloadUrl,
        [Parameter(Mandatory)]
        [string]$ExpectedSha256,
        [Parameter(Mandatory)]
        [string]$ExpectedPlatform,
        [Parameter(Mandatory)]
        [string]$ExpectedRid,
        [Parameter(Mandatory)]
        [string]$ExpectedSdkAbi,
        [Parameter(Mandatory)]
        [string]$ExpectedTimestamp,
        [Parameter(Mandatory)]
        [string]$SourceZipPath
    )

    $registryPath = Join-Path $WorktreePath 'plugins.json'
    if (-not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
        throw "Registry file is missing from gh-pages: $registryPath"
    }

    $registry = @(Get-RegistryEntries -Path $registryPath)
    $matches = @($registry | Where-Object { [string]$_.id -eq $ExpectedPluginId })
    if ($matches.Count -gt 1) {
        throw "Registry contains duplicate plugin id '$ExpectedPluginId'."
    }

    if ($matches.Count -eq 1) {
        try {
            $existingVersion = [System.Management.Automation.SemanticVersion]::new(
                [string]$matches[0].version
            )
            $incomingVersion = [System.Management.Automation.SemanticVersion]::new(
                $ExpectedVersion
            )
        } catch {
            throw "Registry version comparison requires valid SemVer values: $($_.Exception.Message)"
        }
        if ($existingVersion -gt $incomingVersion) {
            throw "Registry already contains a newer version '$($matches[0].version)' for '$ExpectedPluginId'; refusing to downgrade to '$ExpectedVersion'."
        }
    }

    # Always rebuild the complete target entry. Existing target metadata is never copied.
    $entry = [pscustomobject][ordered]@{
        id = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'id')
        name = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'name')
        version = $ExpectedVersion
        minHostVersion = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'minHostVersion')
        author = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'author')
        description = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'description')
        category = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'category')
        size = $ExpectedZipSize
        downloadUrl = $ExpectedDownloadUrl
        sha256 = $ExpectedSha256
        platform = $ExpectedPlatform
        rid = $ExpectedRid
        sdkAbi = $ExpectedSdkAbi
        timestamp = $ExpectedTimestamp
        iconSystemName = [string](Get-JsonPropertyValue -InputObject $Manifest -Name 'iconSystemName')
        requiresApiKey = [bool](Get-JsonPropertyValue -InputObject $Manifest -Name 'requiresApiKey' -DefaultValue $false)
        descriptions = Get-JsonPropertyValue -InputObject $Manifest -Name 'descriptions'
    }
    $registry = @(
        @($registry | Where-Object { [string]$_.id -ne $ExpectedPluginId }) + $entry |
            Sort-Object id
    )
    Write-Host "Staged complete registry entry for $ExpectedPluginId v$ExpectedVersion."

    ConvertTo-Json -InputObject $registry -Depth 20 |
        Set-Content -LiteralPath $registryPath -Encoding utf8NoBOM

    $pluginsDirectory = Join-Path $WorktreePath 'plugins'
    New-Item -ItemType Directory -Path $pluginsDirectory -Force | Out-Null
    $stagedZipPath = Join-Path $pluginsDirectory ([System.IO.Path]::GetFileName($SourceZipPath))
    Copy-Item -LiteralPath $SourceZipPath -Destination $stagedZipPath -Force

    $stagedRegistry = @(Get-RegistryEntries -Path $registryPath)
    $stagedMatches = @($stagedRegistry | Where-Object { [string]$_.id -eq $ExpectedPluginId })
    if ($stagedMatches.Count -ne 1) {
        throw "The staged registry does not contain exactly one '$ExpectedPluginId' entry."
    }

    $stagedEntry = $stagedMatches[0]
    $stagedTimestamp = [DateTimeOffset]$stagedEntry.timestamp
    $expectedTimestampValue = [DateTimeOffset]::Parse(
        $ExpectedTimestamp,
        [System.Globalization.CultureInfo]::InvariantCulture
    )
    if (
        [string]$stagedEntry.version -ne $ExpectedVersion -or
        [long]$stagedEntry.size -ne $ExpectedZipSize -or
        [string]$stagedEntry.downloadUrl -ne $ExpectedDownloadUrl -or
        [string]$stagedEntry.sha256 -ne $ExpectedSha256 -or
        [string]$stagedEntry.platform -ne $ExpectedPlatform -or
        [string]$stagedEntry.rid -ne $ExpectedRid -or
        [string]$stagedEntry.sdkAbi -ne $ExpectedSdkAbi -or
        $stagedTimestamp -ne $expectedTimestampValue
    ) {
        throw "The staged registry entry for '$ExpectedPluginId' failed validation."
    }

    $stagedZip = Get-Item -LiteralPath $stagedZipPath
    if ($stagedZip.Length -ne $ExpectedZipSize) {
        throw "The staged ZIP size does not match the validated source ZIP."
    }

    $sourceHash = (Get-FileHash -LiteralPath $SourceZipPath -Algorithm SHA256).Hash
    $stagedHash = (Get-FileHash -LiteralPath $stagedZipPath -Algorithm SHA256).Hash
    if ($sourceHash -ne $stagedHash) {
        throw "The staged ZIP does not match the validated source ZIP."
    }
}

function Sync-RegistryWorktree {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$WorktreePath
    )

    Invoke-GitCommand -WorkingDirectory $RepositoryRoot -Arguments @('fetch', 'origin', 'gh-pages') | Out-Null

    if (-not (Test-Path -LiteralPath $WorktreePath)) {
        Invoke-GitCommand -WorkingDirectory $RepositoryRoot -Arguments @(
            'worktree',
            'add',
            '-B',
            'gh-pages',
            $WorktreePath,
            'origin/gh-pages'
        ) | Out-Null
    } else {
        $worktreeCheck = Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @(
            'rev-parse',
            '--is-inside-work-tree'
        ) -AllowFailure
        if ($worktreeCheck.ExitCode -ne 0 -or ($worktreeCheck.Output -join '').Trim() -ne 'true') {
            throw "Registry worktree path is not a Git worktree: $WorktreePath"
        }
    }

    Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @('fetch', 'origin', 'gh-pages') | Out-Null
    Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @('reset', '--hard', 'origin/gh-pages') | Out-Null
}

function Get-TagCommitSha {
    param(
        [Parameter(Mandatory)]
        [string]$Repository,
        [Parameter(Mandatory)]
        [string]$Tag
    )

    $encodedTag = [Uri]::EscapeDataString($Tag)
    $result = Invoke-GhCommand -Arguments @(
        'api',
        '--method',
        'GET',
        "repos/$Repository/commits/$encodedTag",
        '--jq',
        '.sha'
    )

    $sha = @($result.Output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[-1].Trim()
    if ($sha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "GitHub returned an invalid commit SHA for tag '$Tag'."
    }

    return $sha
}

function Get-ReleaseForTag {
    param(
        [Parameter(Mandatory)]
        [string]$Repository,
        [Parameter(Mandatory)]
        [string]$Tag
    )

    $encodedTag = [Uri]::EscapeDataString($Tag)
    $result = Invoke-GhCommand -Arguments @(
        'api',
        '--include',
        '--method',
        'GET',
        "repos/$Repository/releases/tags/$encodedTag"
    ) -AllowFailure

    $statusCode = $null
    foreach ($line in $result.Output) {
        if ($line.Trim() -match '^HTTP/\S+\s+([0-9]{3})(?:\s|$)') {
            $statusCode = [int]$Matches[1]
        }
    }

    if ($null -eq $statusCode) {
        $detail = ($result.Output -join [Environment]::NewLine).Trim()
        throw "Release query for '$Tag' did not return an HTTP status: $detail"
    }

    if ($statusCode -eq 404 -and $result.ExitCode -ne 0) {
        return $null
    }

    if ($result.ExitCode -ne 0 -or $statusCode -lt 200 -or $statusCode -ge 300) {
        $detail = ($result.Output -join [Environment]::NewLine).Trim()
        throw "Release query for '$Tag' failed with HTTP $statusCode and exit code $($result.ExitCode): $detail"
    }

    $bodyStart = -1
    for ($i = 0; $i -lt $result.Output.Count; $i++) {
        if ($result.Output[$i].TrimStart().StartsWith('{')) {
            $bodyStart = $i
            break
        }
    }
    if ($bodyStart -lt 0) {
        throw "Release query for '$Tag' returned no JSON body."
    }

    try {
        $body = $result.Output[$bodyStart..($result.Output.Count - 1)] -join [Environment]::NewLine
        return $body | ConvertFrom-Json
    } catch {
        throw "Release query for '$Tag' returned invalid JSON: $($_.Exception.Message)"
    }
}

function Assert-ReleaseTag {
    param(
        [Parameter(Mandatory)]
        [object]$Release,
        [Parameter(Mandatory)]
        [string]$ExpectedTag,
        [Parameter(Mandatory)]
        [string]$ResolvedTagSha,
        [Parameter(Mandatory)]
        [string]$ExpectedCommitSha,
        [switch]$RequirePinnedTarget
    )

    if ([string]$Release.tag_name -ne $ExpectedTag) {
        throw "Release tag '$($Release.tag_name)' does not match '$ExpectedTag'."
    }
    if ($ResolvedTagSha -ne $ExpectedCommitSha) {
        throw "Tag '$ExpectedTag' resolves to '$ResolvedTagSha', not '$ExpectedCommitSha'."
    }
    if ($RequirePinnedTarget -and [string]$Release.target_commitish -ne $ExpectedCommitSha) {
        throw "Draft release target '$($Release.target_commitish)' is not pinned to '$ExpectedCommitSha'."
    }
}

function Assert-ReleaseAsset {
    param(
        [Parameter(Mandatory)]
        [object]$Release,
        [Parameter(Mandatory)]
        [string]$ExpectedAssetName,
        [Parameter(Mandatory)]
        [long]$ExpectedAssetSize
    )

    $assets = @(Get-JsonPropertyValue -InputObject $Release -Name 'assets' -DefaultValue @())
    $matches = @($assets | Where-Object { [string]$_.name -eq $ExpectedAssetName })
    if ($matches.Count -ne 1) {
        throw "Release must contain exactly one asset named '$ExpectedAssetName'."
    }

    $asset = $matches[0]
    if ([long]$asset.size -ne $ExpectedAssetSize) {
        throw "Release asset '$ExpectedAssetName' has size $($asset.size), expected $ExpectedAssetSize."
    }

    $state = [string](Get-JsonPropertyValue -InputObject $asset -Name 'state')
    if (-not [string]::IsNullOrWhiteSpace($state) -and $state -ne 'uploaded') {
        throw "Release asset '$ExpectedAssetName' is not fully uploaded (state: $state)."
    }
}

function Get-PublishedAssetSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Repository,
        [Parameter(Mandatory)]
        [string]$Tag,
        [Parameter(Mandatory)]
        [string]$AssetName,
        [Parameter(Mandatory)]
        [object]$Manifest,
        [Parameter(Mandatory)]
        [string]$PluginId,
        [Parameter(Mandatory)]
        [string]$PluginVersion,
        [Parameter(Mandatory)]
        [long]$ExpectedSize,
        [Parameter(Mandatory)]
        [string]$LocalSha256
    )

    $downloadDirectory = Join-Path ([System.IO.Path]::GetTempPath()) (
        'plugin-release-verify-' + [Guid]::NewGuid().ToString('N')
    )
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    try {
        Invoke-GhCommand -Arguments @(
            'release',
            'download',
            $Tag,
            '--repo',
            $Repository,
            '--pattern',
            $AssetName,
            '--dir',
            $downloadDirectory
        ) | Out-Null

        $downloadedPath = Join-Path $downloadDirectory $AssetName
        if (-not (Test-Path -LiteralPath $downloadedPath -PathType Leaf)) {
            throw "Published asset '$AssetName' could not be downloaded for verification."
        }

        $downloadedSize = (Get-Item -LiteralPath $downloadedPath).Length
        if ($downloadedSize -ne $ExpectedSize) {
            throw "Published asset '$AssetName' is $downloadedSize bytes, expected $ExpectedSize."
        }

        Assert-ZipPackage `
            -Path $downloadedPath `
            -Manifest $Manifest `
            -ExpectedPluginId $PluginId `
            -ExpectedVersion $PluginVersion

        $publishedSha256 = (Get-FileHash -LiteralPath $downloadedPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($publishedSha256 -ne $LocalSha256) {
            Write-Host "The published asset is not byte-identical to this rebuild; recording its own SHA-256."
        }

        return $publishedSha256
    } finally {
        Remove-Item -LiteralPath $downloadDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Set-DraftReleaseAsset {
    param(
        [Parameter(Mandatory)]
        [object]$Release,
        [Parameter(Mandatory)]
        [string]$Repository,
        [Parameter(Mandatory)]
        [string]$Tag,
        [Parameter(Mandatory)]
        [string]$ZipPath
    )

    $assetName = [System.IO.Path]::GetFileName($ZipPath)
    $assets = @(Get-JsonPropertyValue -InputObject $Release -Name 'assets' -DefaultValue @())
    $sameNameAssets = @($assets | Where-Object { [string]$_.name -eq $assetName })
    if ($sameNameAssets.Count -gt 1) {
        throw "Draft release contains duplicate assets named '$assetName'; refusing to clobber."
    }

    $arguments = @('release', 'upload', $Tag, $ZipPath, '--repo', $Repository)
    if ($sameNameAssets.Count -eq 1) {
        $arguments += '--clobber'
        Write-Host "Replacing the controlled draft asset '$assetName'."
    } else {
        Write-Host "Uploading draft asset '$assetName'."
    }

    Invoke-GhCommand -Arguments $arguments | Out-Null
}

function Push-StagedRegistry {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory)]
        [string]$WorktreePath,
        [Parameter(Mandatory)]
        [object]$Manifest,
        [Parameter(Mandatory)]
        [string]$PluginId,
        [Parameter(Mandatory)]
        [string]$PluginVersion,
        [Parameter(Mandatory)]
        [long]$ZipSize,
        [Parameter(Mandatory)]
        [string]$DownloadUrl,
        [Parameter(Mandatory)]
        [string]$Sha256,
        [Parameter(Mandatory)]
        [string]$Platform,
        [Parameter(Mandatory)]
        [string]$Rid,
        [Parameter(Mandatory)]
        [string]$SdkAbi,
        [Parameter(Mandatory)]
        [string]$Timestamp,
        [Parameter(Mandatory)]
        [string]$ZipPath,
        [Parameter(Mandatory)]
        [string]$ProjectName,
        [Parameter(Mandatory)]
        [int]$MaxAttempts
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            if ($attempt -gt 1) {
                Sync-RegistryWorktree -RepositoryRoot $RepositoryRoot -WorktreePath $WorktreePath
            }

            # Stage on every attempt rather than inheriting the pre-flight staging: the
            # caller can only resolve the authoritative SHA-256 after probing the release.
            Write-StagedRegistry `
                -WorktreePath $WorktreePath `
                -Manifest $Manifest `
                -ExpectedPluginId $PluginId `
                -ExpectedVersion $PluginVersion `
                -ExpectedZipSize $ZipSize `
                -ExpectedDownloadUrl $DownloadUrl `
                -ExpectedSha256 $Sha256 `
                -ExpectedPlatform $Platform `
                -ExpectedRid $Rid `
                -ExpectedSdkAbi $SdkAbi `
                -ExpectedTimestamp $Timestamp `
                -SourceZipPath $ZipPath

            Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @('add', '--all') | Out-Null
            Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @(
                'config',
                'user.name',
                'github-actions[bot]'
            ) | Out-Null
            Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @(
                'config',
                'user.email',
                'github-actions[bot]@users.noreply.github.com'
            ) | Out-Null

            $diff = Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @(
                'diff',
                '--cached',
                '--quiet'
            ) -AllowFailure
            if ($diff.ExitCode -gt 1) {
                throw "git diff failed with exit code $($diff.ExitCode)."
            }

            if ($diff.ExitCode -eq 1) {
                Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @(
                    'commit',
                    '-m',
                    "Update $ProjectName to v$PluginVersion"
                ) | Out-Null
                Invoke-GitCommand -WorkingDirectory $WorktreePath -Arguments @(
                    'push',
                    'origin',
                    'gh-pages'
                ) | Out-Null
                Write-Host "Successfully pushed the plugin registry (attempt $attempt)."
            } else {
                Write-Host "The plugin registry already contains the staged release."
            }

            return
        } catch {
            if ($attempt -eq $MaxAttempts) {
                throw "Registry push failed after $MaxAttempts attempt(s): $($_.Exception.Message)"
            }

            Write-Warning "Registry push failed on attempt $attempt; retrying after ${attempt}s."
            Start-Sleep -Seconds $attempt
        }
    }
}

function Invoke-PluginReleaseTransaction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Tag,
        [Parameter(Mandatory)]
        [string]$Repository,
        [Parameter(Mandatory)]
        [string]$CommitSha,
        [Parameter(Mandatory)]
        [string]$ProjectName,
        [Parameter(Mandatory)]
        [string]$PluginVersion,
        [Parameter(Mandatory)]
        [string]$PluginId,
        [Parameter(Mandatory)]
        [string]$Platform,
        [Parameter(Mandatory)]
        [string]$Rid,
        [Parameter(Mandatory)]
        [string]$SdkAbi,
        [Parameter(Mandatory)]
        [string]$ZipPath,
        [Parameter(Mandatory)]
        [string]$ManifestPath,
        [string]$RegistryWorktreePath = 'gh-pages-work',
        [ValidateRange(1, 20)]
        [int]$MaxPushAttempts = 5
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $requiredValues = [ordered]@{
        Tag = $Tag
        Repository = $Repository
        CommitSha = $CommitSha
        ProjectName = $ProjectName
        PluginVersion = $PluginVersion
        PluginId = $PluginId
        Platform = $Platform
        Rid = $Rid
        SdkAbi = $SdkAbi
        ZipPath = $ZipPath
        ManifestPath = $ManifestPath
        RegistryWorktreePath = $RegistryWorktreePath
    }
    foreach ($requiredValue in $requiredValues.GetEnumerator()) {
        Assert-RequiredText -Name $requiredValue.Key -Value $requiredValue.Value
    }

    if ($Repository -notmatch '^[^/\s]+/[^/\s]+$') {
        throw "Repository must use the 'owner/name' format."
    }
    if ($CommitSha -notmatch '^[0-9a-fA-F]{40}$') {
        throw "CommitSha must be a full 40-character commit SHA."
    }

    $repositoryRoot = (Get-Location).ProviderPath
    $resolvedZipPath = (Resolve-Path -LiteralPath $ZipPath).ProviderPath
    $resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).ProviderPath
    $resolvedWorktreePath = if ([System.IO.Path]::IsPathRooted($RegistryWorktreePath)) {
        [System.IO.Path]::GetFullPath($RegistryWorktreePath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $RegistryWorktreePath))
    }

    $manifest = Read-JsonObjectFile -Path $resolvedManifestPath
    if ([string](Get-JsonPropertyValue -InputObject $manifest -Name 'id') -ne $PluginId) {
        throw "Manifest id does not match PluginId '$PluginId'."
    }
    if ([string](Get-JsonPropertyValue -InputObject $manifest -Name 'version') -ne $PluginVersion) {
        throw "Manifest version does not match PluginVersion '$PluginVersion'."
    }

    $expectedZipName = "$PluginId-$PluginVersion.zip"
    if ([System.IO.Path]::GetFileName($resolvedZipPath) -ne $expectedZipName) {
        throw "ZIP name must be '$expectedZipName'."
    }

    Assert-ZipPackage `
        -Path $resolvedZipPath `
        -Manifest $manifest `
        -ExpectedPluginId $PluginId `
        -ExpectedVersion $PluginVersion

    $zipSize = (Get-Item -LiteralPath $resolvedZipPath).Length
    $zipSha256 = (Get-FileHash -LiteralPath $resolvedZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $registryTimestamp = [DateTimeOffset]::UtcNow.ToString('O')
    $encodedTag = [Uri]::EscapeDataString($Tag)
    $encodedZipName = [Uri]::EscapeDataString($expectedZipName)
    $downloadUrl = "https://github.com/$Repository/releases/download/$encodedTag/$encodedZipName"

    Sync-RegistryWorktree -RepositoryRoot $repositoryRoot -WorktreePath $resolvedWorktreePath
    Write-StagedRegistry `
        -WorktreePath $resolvedWorktreePath `
        -Manifest $manifest `
        -ExpectedPluginId $PluginId `
        -ExpectedVersion $PluginVersion `
        -ExpectedZipSize $zipSize `
        -ExpectedDownloadUrl $downloadUrl `
        -ExpectedSha256 $zipSha256 `
        -ExpectedPlatform $Platform `
        -ExpectedRid $Rid `
        -ExpectedSdkAbi $SdkAbi `
        -ExpectedTimestamp $registryTimestamp `
        -SourceZipPath $resolvedZipPath

    Write-Host "Validated the plugin ZIP and prospective registry before release mutation."

    $resolvedTagSha = Get-TagCommitSha -Repository $Repository -Tag $Tag
    if ($resolvedTagSha -ne $CommitSha) {
        throw "Tag '$Tag' resolves to '$resolvedTagSha', not '$CommitSha'."
    }

    $release = Get-ReleaseForTag -Repository $Repository -Tag $Tag
    $draftTransaction = $false

    if ($null -eq $release) {
        Write-Host "No release exists for '$Tag'; creating a draft pinned to $CommitSha."
        Invoke-GhCommand -Arguments @(
            'release',
            'create',
            $Tag,
            '--repo',
            $Repository,
            '--draft',
            '--target',
            $CommitSha,
            '--verify-tag',
            '--title',
            "$ProjectName v$PluginVersion",
            '--notes',
            ''
        ) | Out-Null

        $release = Get-ReleaseForTag -Repository $Repository -Tag $Tag
        if ($null -eq $release) {
            throw "Draft release creation completed but the release cannot be queried."
        }
        $draftTransaction = $true
    } elseif ([bool]$release.draft) {
        Write-Host "Reusing the existing draft release for '$Tag'."
        $draftTransaction = $true
    } else {
        Write-Host "A public release already exists for '$Tag'; entering registry-repair mode."
    }

    Assert-ReleaseTag `
        -Release $release `
        -ExpectedTag $Tag `
        -ResolvedTagSha $resolvedTagSha `
        -ExpectedCommitSha $CommitSha `
        -RequirePinnedTarget:$draftTransaction

    if ($draftTransaction) {
        Set-DraftReleaseAsset `
            -Release $release `
            -Repository $Repository `
            -Tag $Tag `
            -ZipPath $resolvedZipPath

        $release = Get-ReleaseForTag -Repository $Repository -Tag $Tag
        if ($null -eq $release -or -not [bool]$release.draft) {
            throw "Release '$Tag' is no longer a draft after asset upload."
        }
        Assert-ReleaseTag `
            -Release $release `
            -ExpectedTag $Tag `
            -ResolvedTagSha $resolvedTagSha `
            -ExpectedCommitSha $CommitSha `
            -RequirePinnedTarget
        Assert-ReleaseAsset `
            -Release $release `
            -ExpectedAssetName $expectedZipName `
            -ExpectedAssetSize $zipSize
    } else {
        Assert-ReleaseAsset `
            -Release $release `
            -ExpectedAssetName $expectedZipName `
            -ExpectedAssetSize $zipSize

        # A published asset is immutable and a rebuild is not byte-reproducible, so the
        # registry has to carry the hash of the released bytes rather than of this ZIP;
        # equal sizes are no evidence that the two are identical.
        $zipSha256 = Get-PublishedAssetSha256 `
            -Repository $Repository `
            -Tag $Tag `
            -AssetName $expectedZipName `
            -Manifest $manifest `
            -PluginId $PluginId `
            -PluginVersion $PluginVersion `
            -ExpectedSize $zipSize `
            -LocalSha256 $zipSha256
    }

    Push-StagedRegistry `
        -RepositoryRoot $repositoryRoot `
        -WorktreePath $resolvedWorktreePath `
        -Manifest $manifest `
        -PluginId $PluginId `
        -PluginVersion $PluginVersion `
        -ZipSize $zipSize `
        -DownloadUrl $downloadUrl `
        -Sha256 $zipSha256 `
        -Platform $Platform `
        -Rid $Rid `
        -SdkAbi $SdkAbi `
        -Timestamp $registryTimestamp `
        -ZipPath $resolvedZipPath `
        -ProjectName $ProjectName `
        -MaxAttempts $MaxPushAttempts

    if ($draftTransaction) {
        $release = Get-ReleaseForTag -Repository $Repository -Tag $Tag
        if ($null -eq $release) {
            throw "Draft release disappeared after the registry push."
        }

        Assert-ReleaseTag `
            -Release $release `
            -ExpectedTag $Tag `
            -ResolvedTagSha $resolvedTagSha `
            -ExpectedCommitSha $CommitSha `
            -RequirePinnedTarget
        Assert-ReleaseAsset `
            -Release $release `
            -ExpectedAssetName $expectedZipName `
            -ExpectedAssetSize $zipSize

        if ([bool]$release.draft) {
            Write-Host "Registry push succeeded; publishing draft release '$Tag'."
            Invoke-GhCommand -Arguments @(
                'release',
                'edit',
                $Tag,
                '--repo',
                $Repository,
                '--draft=false'
            ) | Out-Null
        } else {
            Write-Host "Release '$Tag' was already published after the registry push."
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-PluginReleaseTransaction @PSBoundParameters
}
