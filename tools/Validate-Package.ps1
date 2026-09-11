[CmdletBinding()]
param(
    [string]$StagePath,
    [string]$ArchivePath,
    [string]$HashPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "..\dist"))
$packageBaseName = "ContrabandCases-0.4.16-SPT4.1.5"
$packageTimestampUtc = [DateTime]::SpecifyKind([DateTime]"2026-09-11T00:00:00", [DateTimeKind]::Utc)

function Resolve-PackagePath {
    param(
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$DefaultValue
    )

    $candidate = if ([string]::IsNullOrWhiteSpace($Value)) { $DefaultValue } else { $Value }
    if (![IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $projectRoot $candidate
    }

    return [IO.Path]::GetFullPath($candidate)
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,
        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (!$Condition) {
        throw "Package validation failed: $Message"
    }
}

function Convert-ToPackagePath {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace("\", "/")
}

function Get-RelativePackagePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$FullName
    )

    $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-Condition ($FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "'$FullName' is outside '$Root'."
    return Convert-ToPackagePath $FullName.Substring($rootPrefix.Length)
}

function Sort-Ordinal {
    param([string[]]$Values)

    $copy = [string[]]@($Values)
    [Array]::Sort($copy, [StringComparer]::Ordinal)
    return $copy
}

function Get-ZipDosTimestamp {
    param([Parameter(Mandatory = $true)][DateTime]$TimestampUtc)

    $timestamp = $TimestampUtc.ToUniversalTime()
    Assert-Condition ($timestamp.Year -ge 1980 -and $timestamp.Year -le 2107) "package timestamp year must fit the ZIP DOS date range."
    Assert-Condition ($timestamp.Millisecond -eq 0 -and ($timestamp.Second % 2) -eq 0) "package timestamp must have whole, even-numbered seconds for exact ZIP DOS representation."

    return [pscustomobject]@{
        Date = [uint16]((($timestamp.Year - 1980) -shl 9) -bor ($timestamp.Month -shl 5) -bor $timestamp.Day)
        Time = [uint16](($timestamp.Hour -shl 11) -bor ($timestamp.Minute -shl 5) -bor ([int]($timestamp.Second / 2)))
    }
}

function Assert-ExactSequence {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [string[]]$Expected,
        [string[]]$Actual
    )

    $expectedSorted = @(Sort-Ordinal $Expected)
    $actualSorted = @(Sort-Ordinal $Actual)
    Assert-Condition ($expectedSorted.Count -eq $actualSorted.Count) "$Description count was $($actualSorted.Count); expected $($expectedSorted.Count). Actual: $($actualSorted -join ', ')."

    for ($index = 0; $index -lt $expectedSorted.Count; $index++) {
        Assert-Condition ([string]::Equals($expectedSorted[$index], $actualSorted[$index], [StringComparison]::Ordinal)) "$Description differed at '$($actualSorted[$index])'; expected '$($expectedSorted[$index])'."
    }
}

function Assert-SequenceInOrder {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [string[]]$Expected,
        [string[]]$Actual
    )

    $expectedValues = [string[]]@($Expected)
    $actualValues = [string[]]@($Actual)
    Assert-Condition ($expectedValues.Count -eq $actualValues.Count) "$Description count was $($actualValues.Count); expected $($expectedValues.Count)."

    for ($index = 0; $index -lt $expectedValues.Count; $index++) {
        Assert-Condition ([string]::Equals($expectedValues[$index], $actualValues[$index], [StringComparison]::Ordinal)) "$Description differed at index ${index}: '$($actualValues[$index])'; expected '$($expectedValues[$index])'."
    }
}

function Remove-JsonComments {
    param([Parameter(Mandatory = $true)][string]$Text)

    $builder = New-Object Text.StringBuilder
    $inString = $false
    $escaped = $false
    $inLineComment = $false
    $inBlockComment = $false

    for ($index = 0; $index -lt $Text.Length; $index++) {
        $character = $Text[$index]
        $next = if ($index + 1 -lt $Text.Length) { $Text[$index + 1] } else { [char]0 }

        if ($inLineComment) {
            if ($character -eq "`r" -or $character -eq "`n") {
                $inLineComment = $false
                [void]$builder.Append($character)
            }
            continue
        }

        if ($inBlockComment) {
            if ($character -eq "*" -and $next -eq "/") {
                $inBlockComment = $false
                $index++
            }
            elseif ($character -eq "`r" -or $character -eq "`n") {
                [void]$builder.Append($character)
            }
            continue
        }

        if ($inString) {
            [void]$builder.Append($character)
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq "\") {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        if ($character -eq '"') {
            $inString = $true
            [void]$builder.Append($character)
        }
        elseif ($character -eq "/" -and $next -eq "/") {
            $inLineComment = $true
            $index++
        }
        elseif ($character -eq "/" -and $next -eq "*") {
            $inBlockComment = $true
            $index++
        }
        else {
            [void]$builder.Append($character)
        }
    }

    Assert-Condition (!$inBlockComment) "JSONC contains an unterminated block comment."
    Assert-Condition (!$inString) "JSON contains an unterminated string."
    return $builder.ToString()
}

function Read-JsonDocument {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $text = [IO.File]::ReadAllText($Path)
        $withoutComments = Remove-JsonComments $text
        $document = $withoutComments | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Package validation failed: '$Path' is not parseable JSON/JSONC. $($_.Exception.Message)"
    }

    Assert-Condition ($null -ne $document) "'$Path' parsed to null."
    return $document
}

function Read-CanonicalRewardPack {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedProviderId,
        [Parameter(Mandatory = $true)][string]$ExpectedPackVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedDisplayLabel,
        [Parameter(Mandatory = $true)][double]$ExpectedProviderWeight,
        [Parameter(Mandatory = $true)][int]$ExpectedRequiredTemplateCount,
        [Parameter(Mandatory = $true)][int]$ExpectedRequiredPresetCount,
        [Parameter(Mandatory = $true)][int]$ExpectedRequiredBundleCount,
        [Parameter(Mandatory = $true)][int]$ExpectedLotCount,
        [int]$ExpectedRetiredLotCount = 0,
        [Parameter(Mandatory = $true)][bool]$IsOptional
    )

    $pack = Read-JsonDocument $Path
    $expectedProperties = @(
        "displayLabel",
        "lots",
        "packVersion",
        "providerId",
        "providerWeight",
        "requiredBundleKeys",
        "requiredPresetIds",
        "requiredTemplateIds",
        "schemaVersion"
    )
    if ($ExpectedRetiredLotCount -gt 0) { $expectedProperties += "retiredLotIds" }
    Assert-ExactSequence "reward pack '$ExpectedProviderId' property set" $expectedProperties @($pack.PSObject.Properties.Name)
    $schemaVersionIsInteger = $pack.schemaVersion -is [int] -or $pack.schemaVersion -is [long]
    Assert-Condition ($schemaVersionIsInteger -and $pack.schemaVersion -eq 1) "reward pack '$ExpectedProviderId' schemaVersion must be integer 1."
    Assert-Condition ($pack.providerId -is [string] -and [string]::Equals($pack.providerId, $ExpectedProviderId, [StringComparison]::Ordinal)) "reward pack '$ExpectedProviderId' must declare canonical providerId '$ExpectedProviderId'."
    Assert-Condition ([string]::Equals([IO.Path]::GetFileNameWithoutExtension($Path), $ExpectedProviderId, [StringComparison]::Ordinal)) "reward pack file name must match providerId '$ExpectedProviderId'."
    Assert-Condition ($pack.packVersion -is [string] -and [string]::Equals($pack.packVersion, $ExpectedPackVersion, [StringComparison]::Ordinal)) "reward pack '$ExpectedProviderId' packVersion must be '$ExpectedPackVersion'."
    Assert-Condition ($pack.displayLabel -is [string] -and [string]::Equals($pack.displayLabel, $ExpectedDisplayLabel, [StringComparison]::Ordinal)) "reward pack '$ExpectedProviderId' displayLabel must be '$ExpectedDisplayLabel'."
    $providerWeightIsNumber = $pack.providerWeight -is [double] -or $pack.providerWeight -is [decimal] -or $pack.providerWeight -is [int] -or $pack.providerWeight -is [long]
    Assert-Condition ($providerWeightIsNumber -and [double]$pack.providerWeight -eq $ExpectedProviderWeight) "reward pack '$ExpectedProviderId' providerWeight must be '$ExpectedProviderWeight'."
    foreach ($collectionName in @("requiredTemplateIds", "requiredPresetIds", "requiredBundleKeys", "lots")) {
        Assert-Condition ($pack.$collectionName -is [object[]]) "reward pack '$ExpectedProviderId' property '$collectionName' must be an array."
    }

    $expectedCollectionCounts = @{
        requiredTemplateIds = $ExpectedRequiredTemplateCount
        requiredPresetIds = $ExpectedRequiredPresetCount
        requiredBundleKeys = $ExpectedRequiredBundleCount
        lots = $ExpectedLotCount
    }
    foreach ($collectionName in @("requiredTemplateIds", "requiredPresetIds", "requiredBundleKeys", "lots")) {
        $actualCount = @($pack.$collectionName).Count
        Assert-Condition ($actualCount -eq $expectedCollectionCounts[$collectionName]) "reward pack '$ExpectedProviderId' property '$collectionName' contains $actualCount entries; expected $($expectedCollectionCounts[$collectionName])."
    }

    foreach ($collectionName in @("requiredTemplateIds", "requiredPresetIds", "requiredBundleKeys")) {
        $values = @($pack.$collectionName)
        foreach ($value in $values) {
            Assert-Condition ($value -is [string] -and ![string]::IsNullOrWhiteSpace($value)) "reward pack '$ExpectedProviderId' property '$collectionName' must contain only non-empty strings."
        }
        Assert-Condition (@($values | Group-Object | Where-Object { $_.Count -ne 1 }).Count -eq 0) "reward pack '$ExpectedProviderId' property '$collectionName' contains a duplicate value."
    }

    if ($IsOptional) {
        Assert-Condition (@($pack.requiredTemplateIds).Count -gt 0) "optional reward pack '$ExpectedProviderId' must declare requiredTemplateIds so it fails closed when its provider mod is absent."
    }

    $expectedLotProperties = @("anchorTemplateId", "displayName", "familyId", "lotId", "purpose", "recipe", "trackId", "usePath", "weight")
    $lotIds = @()
    foreach ($lot in @($pack.lots)) {
        $lotProperties = @($expectedLotProperties)
        if ($lot.PSObject.Properties.Name -contains "roubleBonus") {
            $lotProperties += "roubleBonus"
            Assert-Condition (($lot.roubleBonus -is [int] -or $lot.roubleBonus -is [long]) -and $lot.roubleBonus -in @(3000000, 5000000) -and $lot.lotId.EndsWith(".jackpot-v2", [StringComparison]::Ordinal)) "reward pack '$ExpectedProviderId' lot '$($lot.lotId)' has an invalid jackpot bonus."
        }
        elseif ($lot.lotId.EndsWith(".jackpot-v2", [StringComparison]::Ordinal)) {
            throw "Package validation failed: jackpot '$($lot.lotId)' is missing its bonus."
        }
        Assert-ExactSequence "reward pack '$ExpectedProviderId' lot property set" $lotProperties @($lot.PSObject.Properties.Name)
        foreach ($propertyName in @("anchorTemplateId", "displayName", "familyId", "lotId", "purpose", "trackId")) {
            Assert-Condition ($lot.$propertyName -is [string] -and ![string]::IsNullOrWhiteSpace($lot.$propertyName)) "reward pack '$ExpectedProviderId' lot property '$propertyName' must be a non-empty string."
        }
        $lotWeightIsNumber = $lot.weight -is [double] -or $lot.weight -is [decimal] -or $lot.weight -is [int] -or $lot.weight -is [long]
        Assert-Condition ($lotWeightIsNumber -and [double]$lot.weight -gt 0.0 -and ![double]::IsInfinity([double]$lot.weight)) "reward pack '$ExpectedProviderId' lot '$($lot.lotId)' must have a positive finite numeric weight."
        Assert-Condition ($lot.recipe -is [object[]] -and @($lot.recipe).Count -gt 0) "reward pack '$ExpectedProviderId' lot '$($lot.lotId)' must have a non-empty recipe array."
        Assert-Condition ($null -ne $lot.usePath -and $lot.usePath -isnot [string]) "reward pack '$ExpectedProviderId' lot '$($lot.lotId)' must have a usePath object."
        $lotIds += [string]$lot.lotId
    }
    Assert-Condition (@($lotIds | Group-Object | Where-Object { $_.Count -ne 1 }).Count -eq 0) "reward pack '$ExpectedProviderId' contains a duplicate lotId."
    if ($ExpectedRetiredLotCount -gt 0) {
        Assert-Condition ($pack.retiredLotIds -is [object[]] -and @($pack.retiredLotIds).Count -eq $ExpectedRetiredLotCount) "reward pack '$ExpectedProviderId' retiredLotIds must contain exactly $ExpectedRetiredLotCount entries."
        $seenRetiredIds = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($retiredId in $pack.retiredLotIds) {
            Assert-Condition ($retiredId -is [string] -and ![string]::IsNullOrWhiteSpace($retiredId) -and $lotIds -ccontains $retiredId) "reward pack '$ExpectedProviderId' retiredLotIds must reference existing lot IDs."
            Assert-Condition ($seenRetiredIds.Add($retiredId)) "reward pack '$ExpectedProviderId' retiredLotIds contains a duplicate value."
        }
    }
    return $pack
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-ProjectBuildInputs {
    param([Parameter(Mandatory = $true)][string[]]$ProjectDirectories)

    $inputs = @()
    foreach ($projectDirectory in $ProjectDirectories) {
        Assert-Condition (Test-Path -LiteralPath $projectDirectory -PathType Container) "required project input directory '$projectDirectory' is missing."
        $inputs += @(Get-ChildItem -LiteralPath $projectDirectory -Force -Recurse -File | Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" })
    }
    $commonInput = Join-Path $projectRoot "Directory.Build.props"
    Assert-Condition (Test-Path -LiteralPath $commonInput -PathType Leaf) "required shared build input '$commonInput' is missing."
    $inputs += Get-Item -LiteralPath $commonInput -Force
    Assert-Condition ($inputs.Count -gt 0) "no source inputs were found for the release build freshness check."
    return @($inputs)
}

function Get-ReleaseBuildSpecifications {
    return @(
        [pscustomobject]@{ Name = "Client"; Artifact = Join-Path $projectRoot "Client\bin\Release\netstandard2.1\ContrabandCases.Client.dll"; Projects = @((Join-Path $projectRoot "Client"), (Join-Path $projectRoot "Shared")) },
        [pscustomobject]@{ Name = "Shared"; Artifact = Join-Path $projectRoot "Shared\bin\Release\netstandard2.1\ContrabandCases.Shared.dll"; Projects = @((Join-Path $projectRoot "Shared")) },
        [pscustomobject]@{ Name = "Server"; Artifact = Join-Path $projectRoot "Server\bin\Release\net10.0\ContrabandCases.Server.dll"; Projects = @((Join-Path $projectRoot "Server"), (Join-Path $projectRoot "Shared")) }
    )
}

function Assert-ReleaseBuildsAreFresh {
    foreach ($build in @(Get-ReleaseBuildSpecifications)) {
        Assert-Condition (Test-Path -LiteralPath $build.Artifact -PathType Leaf) "required $($build.Name) release DLL '$($build.Artifact)' is missing. Build all projects in Release first."
        $artifact = Get-Item -LiteralPath $build.Artifact -Force
        $projectDirectories = @($build.Projects)
        $newestInput = @(Get-ProjectBuildInputs -ProjectDirectories $projectDirectories) | Sort-Object -Property LastWriteTimeUtc -Descending | Select-Object -First 1
        Assert-Condition ($artifact.LastWriteTimeUtc -ge $newestInput.LastWriteTimeUtc) "$($build.Name) release DLL '$($build.Artifact)' predates build input '$($newestInput.FullName)' ($($newestInput.LastWriteTimeUtc.ToString('o'))). Rebuild Release before validation."
    }
}

function Get-ValidationSourceState {
    param([Parameter(Mandatory = $true)][object[]]$ReleaseArtifactMappings)

    $paths = @($ReleaseArtifactMappings | ForEach-Object { $_.Source })
    foreach ($build in @(Get-ReleaseBuildSpecifications)) {
        $paths += $build.Artifact
        $projectDirectories = @($build.Projects)
        $paths += @(Get-ProjectBuildInputs -ProjectDirectories $projectDirectories | ForEach-Object { $_.FullName })
    }

    $records = @()
    foreach ($path in @($paths | Sort-Object -Unique)) {
        Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "validation source input '$path' disappeared while its state was captured."
        $item = Get-Item -LiteralPath $path -Force
        $records += [pscustomobject]@{ Path = $item.FullName; Length = $item.Length; LastWriteTimeUtc = $item.LastWriteTimeUtc.Ticks; Hash = Get-Sha256 $item.FullName }
    }
    return @($records)
}

function Assert-ValidationSourceStateUnchanged {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
        [Parameter(Mandatory = $true)][object[]]$ReleaseArtifactMappings
    )

    $current = @(Get-ValidationSourceState $ReleaseArtifactMappings)
    Assert-Condition ($Snapshot.Count -eq $current.Count) "validation source input set changed while validation was running."
    for ($index = 0; $index -lt $Snapshot.Count; $index++) {
        $before = $Snapshot[$index]
        $after = $current[$index]
        Assert-Condition ([string]::Equals($before.Path, $after.Path, [StringComparison]::OrdinalIgnoreCase) -and $before.Length -eq $after.Length -and $before.LastWriteTimeUtc -eq $after.LastWriteTimeUtc -and [string]::Equals($before.Hash, $after.Hash, [StringComparison]::Ordinal)) "validation source input '$($before.Path)' changed while validation was running."
    }
}

function Test-ByteSequence {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Haystack,
        [Parameter(Mandatory = $true)][byte[]]$Needle
    )

    if ($Needle.Length -eq 0 -or $Needle.Length -gt $Haystack.Length) {
        return $false
    }

    for ($offset = 0; $offset -le $Haystack.Length - $Needle.Length; $offset++) {
        $matched = $true
        for ($index = 0; $index -lt $Needle.Length; $index++) {
            if ($Haystack[$offset + $index] -ne $Needle[$index]) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            return $true
        }
    }

    return $false
}

function Test-BinaryContainsText {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $content = [IO.File]::ReadAllBytes($Path)
    $asciiNeedle = [Text.Encoding]::UTF8.GetBytes($Value)
    $utf16Needle = [Text.Encoding]::Unicode.GetBytes($Value)
    return (Test-ByteSequence $content $asciiNeedle) -or (Test-ByteSequence $content $utf16Needle)
}

function Convert-SecretLiteralToHexPattern {
    param(
        [Parameter(Mandatory = $true)][string]$Literal,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][bool]$BigEndian,
        [bool]$IgnoreCase = $false
    )

    $parts = @()
    foreach ($character in $Literal.ToCharArray()) {
        $characters = if ($IgnoreCase -and [char]::IsLetter($character)) { @([char]::ToUpperInvariant($character), [char]::ToLowerInvariant($character)) } else { @($character) }
        $encoded = @($characters | ForEach-Object {
            $hex = "{0:X2}" -f [int][char]$_
            if ($Width -eq 1) { $hex } elseif ($BigEndian) { "00$hex" } else { "$hex`00" }
        } | Select-Object -Unique)
        $parts += if ($encoded.Count -eq 1) { $encoded[0] } else { "(?:$($encoded -join '|'))" }
    }

    return ($parts -join '')
}

function Convert-SecretCharacterSetToHexPattern {
    param(
        [Parameter(Mandatory = $true)][string]$Characters,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][bool]$BigEndian
    )

    $encoded = @($Characters.ToCharArray() | Select-Object -Unique | ForEach-Object {
        $hex = "{0:X2}" -f [int][char]$_
        if ($Width -eq 1) { $hex } elseif ($BigEndian) { "00$hex" } else { "$hex`00" }
    })
    return "(?:$($encoded -join '|'))"
}

function Test-BinaryContainsSecret {
    param([Parameter(Mandatory = $true)][string]$Path)

    $content = [IO.File]::ReadAllBytes($Path)
    # Hex is an exact byte representation, avoiding text decoding of arbitrary DLL or bundle bytes.
    $hex = [BitConverter]::ToString($content).Replace("-", "")
    foreach ($encoding in @(
        [pscustomobject]@{ Width = 1; BigEndian = $false },
        [pscustomobject]@{ Width = 2; BigEndian = $false },
        [pscustomobject]@{ Width = 2; BigEndian = $true }
    )) {
        $word = Convert-SecretCharacterSetToHexPattern "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_" $encoding.Width $encoding.BigEndian
        $alphaNumeric = Convert-SecretCharacterSetToHexPattern "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789" $encoding.Width $encoding.BigEndian
        $upperOrSpace = Convert-SecretCharacterSetToHexPattern "ABCDEFGHIJKLMNOPQRSTUVWXYZ " $encoding.Width $encoding.BigEndian
        $awsPrefix = Convert-SecretLiteralToHexPattern "AKIA" $encoding.Width $encoding.BigEndian $true
        $githubPrefix = (Convert-SecretLiteralToHexPattern "gh" $encoding.Width $encoding.BigEndian $true) + (Convert-SecretCharacterSetToHexPattern "pousrPOUSR" $encoding.Width $encoding.BigEndian) + (Convert-SecretLiteralToHexPattern "_" $encoding.Width $encoding.BigEndian)
        $openAiPrefix = Convert-SecretLiteralToHexPattern "sk-" $encoding.Width $encoding.BigEndian $true
        $privatePrefix = Convert-SecretLiteralToHexPattern "-----BEGIN " $encoding.Width $encoding.BigEndian
        $privateSuffix = Convert-SecretLiteralToHexPattern "PRIVATE KEY-----" $encoding.Width $encoding.BigEndian

        if ([Text.RegularExpressions.Regex]::IsMatch($hex, "$privatePrefix(?:$upperOrSpace)*$privateSuffix") -or
            [Text.RegularExpressions.Regex]::IsMatch($hex, "(?<!$word)$awsPrefix(?:$alphaNumeric){16}(?!$word)") -or
            [Text.RegularExpressions.Regex]::IsMatch($hex, "(?<!$word)$githubPrefix(?:$alphaNumeric){20,}(?!$word)") -or
            [Text.RegularExpressions.Regex]::IsMatch($hex, "(?<!$word)$openAiPrefix(?:$alphaNumeric|$(Convert-SecretLiteralToHexPattern '_' $encoding.Width $encoding.BigEndian)|$(Convert-SecretLiteralToHexPattern '-' $encoding.Width $encoding.BigEndian)){20,}(?!$word)")) {
            return $true
        }
    }

    return $false
}

function Get-AssemblyReport {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedName,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetFramework
    )

    try {
        $assemblyName = [Reflection.AssemblyName]::GetAssemblyName($Path)
        $assembly = [Reflection.Assembly]::LoadFile($Path)
        $references = @($assembly.GetReferencedAssemblies())
        $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).FileVersion
    }
    catch {
        throw "Package validation failed: '$Path' is not a readable managed assembly. $($_.Exception.Message)"
    }

    Assert-Condition ([string]::Equals($assemblyName.Name, $ExpectedName, [StringComparison]::Ordinal)) "'$Path' has assembly name '$($assemblyName.Name)'; expected '$ExpectedName'."
    Assert-Condition ($assemblyName.Version.ToString() -eq "0.4.16.0") "'$ExpectedName' assembly version is '$($assemblyName.Version)'; expected '0.4.16.0'."
    Assert-Condition ($fileVersion -eq "0.4.16.0") "'$ExpectedName' file version is '$fileVersion'; expected '0.4.16.0'."
    Assert-Condition (Test-BinaryContainsText $Path $ExpectedTargetFramework) "'$ExpectedName' does not embed target framework '$ExpectedTargetFramework'."

    return [pscustomobject]@{
        Name = $assemblyName.Name
        Version = $assemblyName.Version.ToString()
        References = $references
    }
}

function Read-UnityFsHeader {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    try {
        $buffer = New-Object byte[] 7
        $read = $stream.Read($buffer, 0, $buffer.Length)
        Assert-Condition ($read -eq 7) "bundle '$Path' is too short to contain a UnityFS header."
        return [Text.Encoding]::ASCII.GetString($buffer)
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Crc32 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $table = New-Object uint32[] 256
    for ($tableIndex = 0; $tableIndex -lt 256; $tableIndex++) {
        $value = [uint32]$tableIndex
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($value -band 1) -ne 0) {
                $value = [uint32](($value -shr 1) -bxor [uint32]3988292384)
            }
            else {
                $value = [uint32]($value -shr 1)
            }
        }
        $table[$tableIndex] = $value
    }

    $crc = [uint32]::MaxValue
    foreach ($byte in $Bytes) {
        $lookupIndex = [int](($crc -bxor [uint32]$byte) -band 255)
        $crc = [uint32](($crc -shr 8) -bxor $table[$lookupIndex])
    }
    return [uint32]($crc -bxor [uint32]::MaxValue)
}

function New-DeterministicArchive {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string[]]$RelativeFiles,
        [Parameter(Mandatory = $true)][DateTime]$TimestampUtc
    )

    $orderedFiles = @(Sort-Ordinal $RelativeFiles)
    $dosTimestamp = Get-ZipDosTimestamp $TimestampUtc
    $output = [IO.File]::Open($DestinationPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = New-Object IO.BinaryWriter($output, [Text.Encoding]::UTF8, $true)
        try {
            $centralRecords = @()
            foreach ($relativePath in $orderedFiles) {
                $entryName = Convert-ToPackagePath $relativePath
                $sourcePath = Join-Path $SourceRoot $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
                $entryNameBytes = [Text.Encoding]::UTF8.GetBytes($entryName)
                $content = [IO.File]::ReadAllBytes($sourcePath)
                Assert-Condition ($entryNameBytes.Length -le [uint16]::MaxValue) "archive entry name '$entryName' is too long."
                Assert-Condition ($content.LongLength -le [uint32]::MaxValue) "archive entry '$entryName' is too large for deterministic ZIP32 output."
                Assert-Condition ($writer.BaseStream.Position -le [uint32]::MaxValue) "archive exceeds deterministic ZIP32 offsets."

                $crc = Get-Crc32 $content
                $localOffset = [uint32]$writer.BaseStream.Position
                $size = [uint32]$content.Length

                $writer.Write([uint32]0x04034b50)
                $writer.Write([uint16]20)
                $writer.Write([uint16]0)
                $writer.Write([uint16]0)
                $writer.Write([uint16]$dosTimestamp.Time)
                $writer.Write([uint16]$dosTimestamp.Date)
                $writer.Write([uint32]$crc)
                $writer.Write($size)
                $writer.Write($size)
                $writer.Write([uint16]$entryNameBytes.Length)
                $writer.Write([uint16]0)
                $writer.Write($entryNameBytes)
                $writer.Write($content)

                $centralRecords += [pscustomobject]@{
                    NameBytes = $entryNameBytes
                    Crc = $crc
                    Size = $size
                    LocalOffset = $localOffset
                }
            }

            Assert-Condition ($centralRecords.Count -le [uint16]::MaxValue) "archive has too many entries for deterministic ZIP32 output."
            Assert-Condition ($writer.BaseStream.Position -le [uint32]::MaxValue) "archive central directory offset exceeds ZIP32."
            $centralOffset = [uint32]$writer.BaseStream.Position

            foreach ($record in $centralRecords) {
                $writer.Write([uint32]0x02014b50)
                $writer.Write([uint16]20)
                $writer.Write([uint16]20)
                $writer.Write([uint16]0)
                $writer.Write([uint16]0)
                $writer.Write([uint16]$dosTimestamp.Time)
                $writer.Write([uint16]$dosTimestamp.Date)
                $writer.Write([uint32]$record.Crc)
                $writer.Write([uint32]$record.Size)
                $writer.Write([uint32]$record.Size)
                $writer.Write([uint16]$record.NameBytes.Length)
                $writer.Write([uint16]0)
                $writer.Write([uint16]0)
                $writer.Write([uint16]0)
                $writer.Write([uint16]0)
                $writer.Write([uint32]0)
                $writer.Write([uint32]$record.LocalOffset)
                $writer.Write([byte[]]$record.NameBytes)
            }

            $centralSize = $writer.BaseStream.Position - $centralOffset
            Assert-Condition ($centralSize -le [uint32]::MaxValue) "archive central directory exceeds ZIP32."
            $writer.Write([uint32]0x06054b50)
            $writer.Write([uint16]0)
            $writer.Write([uint16]0)
            $writer.Write([uint16]$centralRecords.Count)
            $writer.Write([uint16]$centralRecords.Count)
            $writer.Write([uint32]$centralSize)
            $writer.Write([uint32]$centralOffset)
            $writer.Write([uint16]0)
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $output.Dispose()
    }
}

$resolvedStagePath = Resolve-PackagePath $StagePath (Join-Path $distRoot "stage")
$resolvedArchivePath = Resolve-PackagePath $ArchivePath (Join-Path $distRoot "$packageBaseName.zip")
$resolvedHashPath = Resolve-PackagePath $HashPath (Join-Path $distRoot "$packageBaseName-SHA256.txt")

$rewardPackSpecifications = @(
    [pscustomobject]@{ FileName = "black-site.loadouts.json"; ProviderId = "black-site.loadouts"; PackVersion = "1.0.0"; DisplayLabel = "Black Site Loadouts"; ProviderWeight = 0.1; RequiredTemplateCount = 33; RequiredPresetCount = 12; RequiredBundleCount = 0; LotCount = 8; RetiredLotCount = 0; IsOptional = $true; Sha256 = "5649BCAFEF023801E92D81DDA2CDFAF5E0A2BE82B61A91586C5002F1DE55E568" },
    [pscustomobject]@{ FileName = "cnn-containers.storage.json"; ProviderId = "cnn-containers.storage"; PackVersion = "1.0.0"; DisplayLabel = "CNN Storage"; ProviderWeight = 0.06; RequiredTemplateCount = 2; RequiredPresetCount = 0; RequiredBundleCount = 0; LotCount = 2; RetiredLotCount = 0; IsOptional = $true; Sha256 = "045EC4142925326AA06A62544A09C533E551C60217F5C8C499706525BCCF3F67" },
    [pscustomobject]@{ FileName = "more-cases.storage.json"; ProviderId = "more-cases.storage"; PackVersion = "1.0.0"; DisplayLabel = "More Cases Storage"; ProviderWeight = 0.04; RequiredTemplateCount = 9; RequiredPresetCount = 0; RequiredBundleCount = 0; LotCount = 9; RetiredLotCount = 0; IsOptional = $true; Sha256 = "31A1CD202BE3CE434AF3D08F09FC5F340296122A63DE6A59E3DA76F645DA57B9" },
    [pscustomobject]@{ FileName = "amonya.arcane-cache.json"; ProviderId = "amonya.arcane-cache"; PackVersion = "1.0.0"; DisplayLabel = "Amonya Arcane Cache"; ProviderWeight = 0.05; RequiredTemplateCount = 12; RequiredPresetCount = 0; RequiredBundleCount = 0; LotCount = 15; RetiredLotCount = 6; IsOptional = $true; Sha256 = "5158B74132FC7FFF2BC0713488B26E6628160D64ED1CE7152229C9148C8352BF" },
    [pscustomobject]@{ FileName = "core.json"; ProviderId = "core"; PackVersion = "0.3.3"; DisplayLabel = "Contraband Cases Core"; ProviderWeight = 1.0; RequiredTemplateCount = 62; RequiredPresetCount = 16; RequiredBundleCount = 0; LotCount = 202; RetiredLotCount = 0; IsOptional = $false; Sha256 = "1D4D0B7A8A9B78F83263B6172E5A2EA556DABEAC44901F80C8CA492EF90C1EE6" },
    [pscustomobject]@{ FileName = "eco-attachment.elite-optics.json"; ProviderId = "eco-attachment.elite-optics"; PackVersion = "1.0.0"; DisplayLabel = "Eco Attachment Emporium Elite Optics"; ProviderWeight = 0.06; RequiredTemplateCount = 10; RequiredPresetCount = 2; RequiredBundleCount = 0; LotCount = 14; RetiredLotCount = 0; IsOptional = $true; Sha256 = "740017AB6800CA679B54216A93EC5BE1443ED743021A40416A0288082BB1CF5F" },
    [pscustomobject]@{ FileName = "eco-attachment.field-cache.json"; ProviderId = "eco-attachment.field-cache"; PackVersion = "1.0.0"; DisplayLabel = "Eco Attachment Emporium Field Cache"; ProviderWeight = 0.16; RequiredTemplateCount = 8; RequiredPresetCount = 1; RequiredBundleCount = 0; LotCount = 13; RetiredLotCount = 0; IsOptional = $true; Sha256 = "3466D7CF8F104D5F11CB99294A75DCCE7F8B7FC48E25DC8F7D7550413A611F0E" },
    [pscustomobject]@{ FileName = "eco-ww2.relic-cache.json"; ProviderId = "eco-ww2.relic-cache"; PackVersion = "1.0.0"; DisplayLabel = "Eco WW2 Pack Relic Cache"; ProviderWeight = 0.05; RequiredTemplateCount = 12; RequiredPresetCount = 0; RequiredBundleCount = 0; LotCount = 8; RetiredLotCount = 0; IsOptional = $true; Sha256 = "38C651D486C4AABC24A68D47666B441B42F59C7AA5B5C965818B9D1199A3F088" },
    [pscustomobject]@{ FileName = "isb-aishi.elite-armory.json"; ProviderId = "isb-aishi.elite-armory"; PackVersion = "1.0.0"; DisplayLabel = "ISB Aishi Elite Armory"; ProviderWeight = 0.07; RequiredTemplateCount = 10; RequiredPresetCount = 3; RequiredBundleCount = 0; LotCount = 20; RetiredLotCount = 8; IsOptional = $true; Sha256 = "70DF3910A3CDFA646FF8451BB1163CF0F789F2B3C04B4CE55E5A9441122CD2C8" },
    [pscustomobject]@{ FileName = "isb-aishi.field-armory.json"; ProviderId = "isb-aishi.field-armory"; PackVersion = "1.0.0"; DisplayLabel = "ISB Aishi Field Armory"; ProviderWeight = 0.18; RequiredTemplateCount = 13; RequiredPresetCount = 1; RequiredBundleCount = 0; LotCount = 12; RetiredLotCount = 0; IsOptional = $true; Sha256 = "70395FD8D5C21F1CBBB7201685DCA80B23F3B8B036BE8CEF50643B31FBA11150" },
    [pscustomobject]@{ FileName = "krackasourus.anime-cards.json"; ProviderId = "krackasourus.anime-cards"; PackVersion = "1.5.2"; DisplayLabel = "Krackasourus Anime Cards"; ProviderWeight = 0.12; RequiredTemplateCount = 22; RequiredPresetCount = 0; RequiredBundleCount = 0; LotCount = 33; RetiredLotCount = 0; IsOptional = $true; Sha256 = "55094C0A933C5D5CD50A78394E9C6899E34B953BB0BB9282C9F1D5F8D2591095" },
    [pscustomobject]@{ FileName = "krackasourus.pokemon-cards.json"; ProviderId = "krackasourus.pokemon-cards"; PackVersion = "1.1.2"; DisplayLabel = "Krackasourus Pokemon Cards"; ProviderWeight = 0.12; RequiredTemplateCount = 18; RequiredPresetCount = 0; RequiredBundleCount = 0; LotCount = 33; RetiredLotCount = 0; IsOptional = $true; Sha256 = "494185C1C13E4CE45B8FC88E21AD615D427AEEBE9FDEC5C52C6B771B702FD548" },
    [pscustomobject]@{ FileName = "krackasourus.yugioh-cards.json"; ProviderId = "krackasourus.yugioh-cards"; PackVersion = "0.1.2"; DisplayLabel = "Krackasourus Yu-Gi-Oh Cards"; ProviderWeight = 0.12; RequiredTemplateCount = 18; RequiredPresetCount = 0; RequiredBundleCount = 0; LotCount = 33; RetiredLotCount = 0; IsOptional = $true; Sha256 = "2C15F4FAEDA2C94F38E0DEC20EC86931815C1893E00CBB8B49A9C318805E081F" },
    [pscustomobject]@{ FileName = "natalya.elite-armor.json"; ProviderId = "natalya.elite-armor"; PackVersion = "1.0.0"; DisplayLabel = "Natalya Elite Armor"; ProviderWeight = 0.06; RequiredTemplateCount = 8; RequiredPresetCount = 3; RequiredBundleCount = 0; LotCount = 15; RetiredLotCount = 6; IsOptional = $true; Sha256 = "CEBE707FA9A2973FFDB878D10B939513160BE7A18DDDA9BAFCC7CDE55DCB8636" },
    [pscustomobject]@{ FileName = "natalya.field-gear.json"; ProviderId = "natalya.field-gear"; PackVersion = "1.0.0"; DisplayLabel = "Natalya Field Gear"; ProviderWeight = 0.22; RequiredTemplateCount = 11; RequiredPresetCount = 1; RequiredBundleCount = 0; LotCount = 12; RetiredLotCount = 0; IsOptional = $true; Sha256 = "8FD0E5450DA4A4D80C5F4668D1B1ABFA72796A486805E59FBC21401B3419DB84" },
    [pscustomobject]@{ FileName = "sjx.combat-chemistry.json"; ProviderId = "sjx.combat-chemistry"; PackVersion = "1.0.2"; DisplayLabel = "SJX Combat Chemistry"; ProviderWeight = 0.35; RequiredTemplateCount = 24; RequiredPresetCount = 3; RequiredBundleCount = 0; LotCount = 20; RetiredLotCount = 0; IsOptional = $true; Sha256 = "DBC866D4F09F1264036E185696D34E22F0D8BF388063AA6EEA241A453811EA8D" },
    [pscustomobject]@{ FileName = "vault.json"; ProviderId = "vault"; PackVersion = "1.0.0"; DisplayLabel = "Contraband Vault"; ProviderWeight = 0.05; RequiredTemplateCount = 19; RequiredPresetCount = 8; RequiredBundleCount = 0; LotCount = 12; RetiredLotCount = 0; IsOptional = $true; Sha256 = "6C57A8FD621DAE3C9085088A2D381A9A8279B419E9EE26057E9249F2CC940280" },
    [pscustomobject]@{ FileName = "vultify.cooler-stims.json"; ProviderId = "vultify.cooler-stims"; PackVersion = "2.0.2"; DisplayLabel = "Vultify CoolerStims"; ProviderWeight = 0.25; RequiredTemplateCount = 21; RequiredPresetCount = 6; RequiredBundleCount = 0; LotCount = 32; RetiredLotCount = 0; IsOptional = $true; Sha256 = "111EA716AE732681C435CE43BB0C35035F3DA1FE35C53311EAE7E0AE3354C726" },
    [pscustomobject]@{ FileName = "wtt-contentbackport.elite-optics.json"; ProviderId = "wtt-contentbackport.elite-optics"; PackVersion = "1.0.0"; DisplayLabel = "WTT Content Backport Elite Optics"; ProviderWeight = 0.07; RequiredTemplateCount = 10; RequiredPresetCount = 3; RequiredBundleCount = 0; LotCount = 15; RetiredLotCount = 6; IsOptional = $true; Sha256 = "962A29B7B873C9304DEFC10FCB4F4EED284CF99CC67E9AC565581A9D3E979CE0" },
    [pscustomobject]@{ FileName = "wtt-contentbackport.field-resupply.json"; ProviderId = "wtt-contentbackport.field-resupply"; PackVersion = "1.0.0"; DisplayLabel = "WTT Content Backport Field Resupply"; ProviderWeight = 0.2; RequiredTemplateCount = 13; RequiredPresetCount = 1; RequiredBundleCount = 0; LotCount = 12; RetiredLotCount = 0; IsOptional = $true; Sha256 = "CD0A5B60D924E15D115F1853BF722D965AC945B8C1DC823F780A3953C239951D" }
)

$expectedFiles = @(
    "BepInEx/plugins/ContrabandCases/ContrabandCases.Client.dll",
    "BepInEx/plugins/ContrabandCases/ContrabandCases.Shared.dll",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/ContrabandCases.Server.dll",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/ContrabandCases.Shared.dll",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/LICENSE.md",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/README.md",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles.json",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles/contrabandcases/br12_case.bundle",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles/contrabandcases/br12_key.bundle",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/config/config.jsonc",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/config/rewards.json"
)
$expectedFiles += @(
    $rewardPackSpecifications |
        ForEach-Object { "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/$($_.FileName)" }
)

$expectedDirectories = @(
    "BepInEx",
    "BepInEx/plugins",
    "BepInEx/plugins/ContrabandCases",
    "SPT_Runtime",
    "SPT_Runtime/user",
    "SPT_Runtime/user/mods",
    "SPT_Runtime/user/mods/Wade-ContrabandCases",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles/contrabandcases",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/config",
    "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs"
)

Assert-Condition (Test-Path -LiteralPath $resolvedStagePath -PathType Container) "stage directory '$resolvedStagePath' is missing."
$stageItem = Get-Item -LiteralPath $resolvedStagePath -Force
Assert-Condition (($stageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "stage directory must not be a reparse point."

$allStageItems = @(Get-ChildItem -LiteralPath $resolvedStagePath -Force -Recurse)
$reparseItems = @($allStageItems | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
$reparseNames = @($reparseItems | ForEach-Object { $_.FullName }) -join ", "
Assert-Condition ($reparseItems.Count -eq 0) "stage contains reparse points: '$reparseNames'."

$actualFiles = @(
    Get-ChildItem -LiteralPath $resolvedStagePath -Force -Recurse -File |
        ForEach-Object { Get-RelativePackagePath $resolvedStagePath $_.FullName }
)
$actualDirectories = @(
    Get-ChildItem -LiteralPath $resolvedStagePath -Force -Recurse -Directory |
        ForEach-Object { Get-RelativePackagePath $resolvedStagePath $_.FullName }
)
Assert-ExactSequence "staged file set" $expectedFiles $actualFiles
Assert-ExactSequence "staged directory set" $expectedDirectories $actualDirectories
foreach ($relativePath in $actualFiles) {
    $stagedPath = Join-Path $resolvedStagePath $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
    $actualTimestampUtc = (Get-Item -LiteralPath $stagedPath -Force).LastWriteTimeUtc
    Assert-Condition ($actualTimestampUtc.Ticks -eq $packageTimestampUtc.Ticks) "staged file '$relativePath' has timestamp '$($actualTimestampUtc.ToString('o'))'; expected the 0.4.16 release timestamp '$($packageTimestampUtc.ToString('o'))'."
}

$releaseArtifactMappings = @(
    [pscustomobject]@{ Source = Join-Path $projectRoot "Client\bin\Release\netstandard2.1\ContrabandCases.Client.dll"; Destination = "BepInEx/plugins/ContrabandCases/ContrabandCases.Client.dll" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "Shared\bin\Release\netstandard2.1\ContrabandCases.Shared.dll"; Destination = "BepInEx/plugins/ContrabandCases/ContrabandCases.Shared.dll" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "Server\bin\Release\net10.0\ContrabandCases.Server.dll"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/ContrabandCases.Server.dll" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "Shared\bin\Release\netstandard2.1\ContrabandCases.Shared.dll"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/ContrabandCases.Shared.dll" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "README.md"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/README.md" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "LICENSE.md"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/LICENSE.md" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "bundles.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles.json" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "bundles\contrabandcases\br12_case.bundle"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles/contrabandcases/br12_case.bundle" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "bundles\contrabandcases\br12_key.bundle"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles/contrabandcases/br12_key.bundle" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "config\config.jsonc"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/config.jsonc" },
    [pscustomobject]@{ Source = Join-Path $projectRoot "config\rewards.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/rewards.json" }
)
$releaseArtifactMappings += @(
    $rewardPackSpecifications |
        ForEach-Object {
            [pscustomobject]@{
                Source = Join-Path $projectRoot "config\reward-packs\$($_.FileName)"
                Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/$($_.FileName)"
            }
        }
)
$presentSourceArtifacts = @($releaseArtifactMappings | Where-Object { Test-Path -LiteralPath $_.Source -PathType Leaf })
$sourceProjectSentinels = @(
    (Join-Path $projectRoot "Client"),
    (Join-Path $projectRoot "Shared"),
    (Join-Path $projectRoot "Server"),
    (Join-Path $projectRoot "Directory.Build.props"),
    (Join-Path $projectRoot "ContrabandCases.sln")
)
$isSourceProject = @($sourceProjectSentinels | Where-Object { Test-Path -LiteralPath $_ }).Count -gt 0
$rootItems = @(Get-ChildItem -LiteralPath $projectRoot -Force)
$toolsRoot = Join-Path $projectRoot "tools"
$toolsItems = @()
if (Test-Path -LiteralPath $toolsRoot -PathType Container) {
    $toolsItems += @(Get-ChildItem -LiteralPath $toolsRoot -Force)
}
$isStandaloneExtraction = $rootItems.Count -eq 1 -and $rootItems[0].Name -eq "tools" -and $toolsItems.Count -eq 1 -and $toolsItems[0].Name -eq "Validate-Package.ps1" -and (Test-Path -LiteralPath (Join-Path $toolsRoot "Validate-Package.ps1") -PathType Leaf)
Assert-Condition ($isSourceProject -or $isStandaloneExtraction) "validation root is neither a complete source project nor a standalone extracted package."
if ($isSourceProject) {
    Assert-Condition ($presentSourceArtifacts.Count -eq $releaseArtifactMappings.Count) "release source inputs are incomplete for this source project; provide all mapped release inputs or validate a true standalone extracted package."
    $validationSourceState = Get-ValidationSourceState $releaseArtifactMappings
    Assert-ReleaseBuildsAreFresh
    foreach ($mapping in $releaseArtifactMappings) {
        $stagedPath = Join-Path $resolvedStagePath $mapping.Destination.Replace("/", [IO.Path]::DirectorySeparatorChar)
        Assert-Condition ((Get-Sha256 $stagedPath) -eq (Get-Sha256 $mapping.Source)) "staged artifact '$($mapping.Destination)' differs from current release input '$($mapping.Source)'. Re-run Package.ps1."
    }
}

$serverRoot = Join-Path $resolvedStagePath "SPT_Runtime\user\mods\Wade-ContrabandCases"
$manifestPath = Join-Path $serverRoot "bundles.json"
$configPath = Join-Path $serverRoot "config\config.jsonc"
$rewardsPath = Join-Path $serverRoot "config\rewards.json"
$manifest = Read-JsonDocument $manifestPath
$config = Read-JsonDocument $configPath
$rewards = Read-JsonDocument $rewardsPath
foreach ($packSpecification in $rewardPackSpecifications) {
    $packPath = Join-Path $serverRoot "config\reward-packs\$($packSpecification.FileName)"
    [void](Read-CanonicalRewardPack `
        -Path $packPath `
        -ExpectedProviderId $packSpecification.ProviderId `
        -ExpectedPackVersion $packSpecification.PackVersion `
        -ExpectedDisplayLabel $packSpecification.DisplayLabel `
        -ExpectedProviderWeight $packSpecification.ProviderWeight `
        -ExpectedRequiredTemplateCount $packSpecification.RequiredTemplateCount `
        -ExpectedRequiredPresetCount $packSpecification.RequiredPresetCount `
        -ExpectedRequiredBundleCount $packSpecification.RequiredBundleCount `
        -ExpectedLotCount $packSpecification.LotCount `
        -ExpectedRetiredLotCount $packSpecification.RetiredLotCount `
        -IsOptional $packSpecification.IsOptional)
    $actualPackHash = Get-Sha256 $packPath
    Assert-Condition ([string]::Equals($actualPackHash, $packSpecification.Sha256, [StringComparison]::Ordinal)) "reward pack '$($packSpecification.ProviderId)' SHA-256 '$actualPackHash' does not match accepted hash '$($packSpecification.Sha256)'."
}

$manifestEntries = @($manifest.manifest)
Assert-Condition ($manifestEntries.Count -eq 2) "bundles.json must contain exactly two manifest entries."
$expectedBundleKeys = @("contrabandcases/br12_case.bundle", "contrabandcases/br12_key.bundle")
$expectedBundleHashes = @{
    "contrabandcases/br12_case.bundle" = "086E3CED4C4FC379223F2C419CD8E64E4B0FA568A08373C33B471DF466834884"
    "contrabandcases/br12_key.bundle" = "46D975BA9EE55D07722A4DFB1A1F1E322A1D3B1780AC925B41FD280C5DD77200"
}
$actualBundleKeys = @()
foreach ($entry in $manifestEntries) {
    Assert-Condition ($null -ne $entry.PSObject.Properties["key"]) "a bundle manifest entry is missing 'key'."
    Assert-Condition ($null -ne $entry.PSObject.Properties["dependencyKeys"]) "bundle '$($entry.key)' is missing 'dependencyKeys'."
    $actualBundleKeys += [string]$entry.key
    Assert-Condition (@($entry.dependencyKeys).Count -eq 0) "bundle '$($entry.key)' must have no dependencies."

    $physicalBundle = Join-Path (Join-Path $serverRoot "bundles") ([string]$entry.key).Replace("/", [IO.Path]::DirectorySeparatorChar)
    Assert-Condition (Test-Path -LiteralPath $physicalBundle -PathType Leaf) "manifest key '$($entry.key)' has no matching physical bundle."
}
Assert-ExactSequence "bundle manifest keys" $expectedBundleKeys $actualBundleKeys

$configPropertyNames = @($config.PSObject.Properties.Name)
$expectedConfigProperties = @("animationDurationSeconds", "caseLootWeightPercent", "caseStock", "debugLogging", "fixedCasePrice", "keyLootWeightPercent", "reducedMotionDefault", "testingInventoryGrantsEnabled", "therapistSellPriceCase", "therapistSellPriceKey")
Assert-ExactSequence "config property set" $expectedConfigProperties $configPropertyNames
Assert-Condition ($config.caseStock -eq 5) "canonical config caseStock must be 5."
Assert-Condition ($config.keyLootWeightPercent -eq 2.2) "canonical config keyLootWeightPercent must be 2.2."
Assert-Condition ($config.caseLootWeightPercent -eq 1.1) "canonical config caseLootWeightPercent must be 1.1."
Assert-Condition ($config.therapistSellPriceKey -eq 75000) "canonical config therapistSellPriceKey must be 75000."
Assert-Condition ($config.testingInventoryGrantsEnabled -eq $false) "canonical config testingInventoryGrantsEnabled must be false."

$rewardEntries = @($rewards.rewards)
Assert-Condition ($rewardEntries.Count -eq 12) "rewards.json must contain exactly twelve rewards."
$rewardIds = @($rewardEntries | ForEach-Object { [string]$_.id })
Assert-Condition (@($rewardIds | Group-Object | Where-Object { $_.Count -ne 1 }).Count -eq 0) "rewards.json contains a duplicate reward id."

$nativeMongoIds = @()
$weightTotal = 0.0
foreach ($reward in $rewardEntries) {
    foreach ($propertyName in @("id", "displayName", "weaponTemplateId", "presetId", "rarity", "weight")) {
        Assert-Condition ($null -ne $reward.PSObject.Properties[$propertyName]) "reward '$($reward.id)' is missing '$propertyName'."
    }

    foreach ($mongoProperty in @("weaponTemplateId", "presetId")) {
        $mongoId = [string]$reward.$mongoProperty
        Assert-Condition ($mongoId -match "\A[0-9a-f]{24}\z") "reward '$($reward.id)' has invalid $mongoProperty '$mongoId'."
        $nativeMongoIds += $mongoId
    }

    $weight = [double]$reward.weight
    Assert-Condition (![double]::IsNaN($weight) -and ![double]::IsInfinity($weight) -and $weight -gt 0.0) "reward '$($reward.id)' has an invalid weight."
    $weightTotal += $weight
}
Assert-Condition ([Math]::Abs($weightTotal - 1.0) -le 1e-9) "reward weights total '$weightTotal'; expected 1.0."

$fixedIds = @(
    [pscustomobject]@{ Name = "CaseTemplateId"; Value = "66d000000000000000000001" },
    [pscustomobject]@{ Name = "KeyTemplateId"; Value = "66d000000000000000000002" },
    [pscustomobject]@{ Name = "MechanicCaseAssortRootId"; Value = "66d000000000000000000003" }
)
$fixedIdValues = @($fixedIds | ForEach-Object { $_.Value })
Assert-Condition (@($fixedIdValues | Group-Object | Where-Object { $_.Count -ne 1 }).Count -eq 0) "fixed Mongo IDs are not unique."
foreach ($fixedId in $fixedIds) {
    Assert-Condition ($fixedId.Value -match "\A[0-9a-f]{24}\z") "fixed Mongo ID '$($fixedId.Value)' is invalid."
    Assert-Condition ($nativeMongoIds -notcontains $fixedId.Value) "fixed Mongo ID '$($fixedId.Value)' collides with a reward template or preset ID."
}

$clientDll = Join-Path $resolvedStagePath "BepInEx\plugins\ContrabandCases\ContrabandCases.Client.dll"
$clientSharedDll = Join-Path $resolvedStagePath "BepInEx\plugins\ContrabandCases\ContrabandCases.Shared.dll"
$serverDll = Join-Path $serverRoot "ContrabandCases.Server.dll"
$serverSharedDll = Join-Path $serverRoot "ContrabandCases.Shared.dll"
Assert-Condition ((Get-Sha256 $clientSharedDll) -eq (Get-Sha256 $serverSharedDll)) "client and server copies of ContrabandCases.Shared.dll differ."

$clientAssembly = Get-AssemblyReport $clientDll "ContrabandCases.Client" ".NETStandard,Version=v2.1"
$sharedAssembly = Get-AssemblyReport $clientSharedDll "ContrabandCases.Shared" ".NETStandard,Version=v2.1"
$serverAssembly = Get-AssemblyReport $serverDll "ContrabandCases.Server" ".NETCoreApp,Version=v10.0"

foreach ($assemblyReport in @($clientAssembly, $sharedAssembly)) {
    $forbiddenReferences = @($assemblyReport.References | Where-Object { $_.Name -match "(?i)^spt" })
    $forbiddenReferenceNames = @($forbiddenReferences | ForEach-Object { $_.Name }) -join ", "
    Assert-Condition ($forbiddenReferences.Count -eq 0) "'$($assemblyReport.Name)' references forbidden client/shared SPT assemblies: $forbiddenReferenceNames."
}

$sharedUnexpectedReferences = @($sharedAssembly.References | Where-Object { $_.Name -ne "netstandard" })
$sharedUnexpectedReferenceNames = @($sharedUnexpectedReferences | ForEach-Object { $_.Name }) -join ", "
Assert-Condition ($sharedUnexpectedReferences.Count -eq 0) "shared DLL has unpackaged runtime references: $sharedUnexpectedReferenceNames."

$allowedClientReferences = @(
    "0Harmony",
    "Assembly-CSharp",
    "BepInEx",
    "Comfort",
    "ContrabandCases.Shared",
    "netstandard",
    "Newtonsoft.Json",
    "UnityEngine.AudioModule",
    "UnityEngine.CoreModule",
    "UnityEngine.IMGUIModule",
    "UnityEngine.InputLegacyModule",
    "UnityEngine.TextRenderingModule",
    "UnityEngine.UI",
    "UnityEngine.UIModule"
)
$unexpectedClientReferences = @($clientAssembly.References | Where-Object { $allowedClientReferences -notcontains $_.Name })
$unexpectedClientReferenceNames = @($unexpectedClientReferences | ForEach-Object { $_.Name }) -join ", "
Assert-Condition ($unexpectedClientReferences.Count -eq 0) "client DLL has unexpected runtime references: $unexpectedClientReferenceNames."
$clientNewtonsoftReference = @($clientAssembly.References | Where-Object { $_.Name -eq "Newtonsoft.Json" })
Assert-Condition ($clientNewtonsoftReference.Count -eq 1 -and $clientNewtonsoftReference[0].Version.ToString() -eq "13.0.0.0") "client must reference the Newtonsoft.Json 13.0.0.0 assembly supplied by the SPT 4.1.3 game runtime."

$allowedServerReferences = @(
    "ContrabandCases.Shared",
    "Microsoft.AspNetCore.Http.Abstractions",
    "SemanticVersioning",
    "SPTarkov.Common",
    "SPTarkov.DI",
    "SPTarkov.Server.Core"
)
$unexpectedServerReferences = @(
    $serverAssembly.References |
        Where-Object { !$_.Name.StartsWith("System.", [StringComparison]::Ordinal) -and $allowedServerReferences -notcontains $_.Name }
)
$unexpectedServerReferenceNames = @($unexpectedServerReferences | ForEach-Object { $_.Name }) -join ", "
Assert-Condition ($unexpectedServerReferences.Count -eq 0) "server DLL has unpackaged runtime references: $unexpectedServerReferenceNames."
Assert-Condition (@($serverAssembly.References | Where-Object { $_.Name -eq "Newtonsoft.Json" }).Count -eq 0) "server DLL must not reference Newtonsoft.Json; the clean SPT server does not supply it."
$serverAspNetCoreHttpReference = @($serverAssembly.References | Where-Object { $_.Name -eq "Microsoft.AspNetCore.Http.Abstractions" })
Assert-Condition ($serverAspNetCoreHttpReference.Count -eq 1 -and $serverAspNetCoreHttpReference[0].Version.ToString() -eq "10.0.0.0") "server must reference the Microsoft.AspNetCore.Http.Abstractions 10.0.0.0 assembly supplied by the SPT 4.1.3 server runtime exactly once."

$clientSharedReference = @($clientAssembly.References | Where-Object { $_.Name -eq "ContrabandCases.Shared" })
Assert-Condition ($clientSharedReference.Count -eq 1 -and $clientSharedReference[0].Version.ToString() -eq "0.4.16.0") "client must reference ContrabandCases.Shared 0.4.16.0 exactly once."
$serverSharedReference = @($serverAssembly.References | Where-Object { $_.Name -eq "ContrabandCases.Shared" })
Assert-Condition ($serverSharedReference.Count -eq 1 -and $serverSharedReference[0].Version.ToString() -eq "0.4.16.0") "server must reference ContrabandCases.Shared 0.4.16.0 exactly once."
foreach ($expectedSptReference in @("SPTarkov.Common", "SPTarkov.DI", "SPTarkov.Server.Core")) {
    $matches = @($serverAssembly.References | Where-Object { $_.Name -eq $expectedSptReference })
    Assert-Condition ($matches.Count -eq 1 -and $matches[0].Version.ToString() -eq "4.1.3.0") "server reference '$expectedSptReference' must be version 4.1.3.0 exactly once."
}

foreach ($fixedId in $fixedIds) {
    Assert-Condition (Test-BinaryContainsText $clientSharedDll $fixedId.Name) "shared DLL does not contain constant field '$($fixedId.Name)'."
    Assert-Condition (Test-BinaryContainsText $clientSharedDll $fixedId.Value) "shared DLL does not contain fixed Mongo ID '$($fixedId.Value)'."
}
foreach ($bundleKey in $expectedBundleKeys) {
    Assert-Condition (Test-BinaryContainsText $clientSharedDll $bundleKey) "shared DLL does not contain bundle key '$bundleKey'."
}
Assert-Condition (Test-BinaryContainsText $clientSharedDll "Wade-ContrabandCases") "shared DLL does not contain the fixed runtime folder name."

foreach ($bundleKey in $expectedBundleKeys) {
    $bundlePath = Join-Path (Join-Path $serverRoot "bundles") $bundleKey.Replace("/", [IO.Path]::DirectorySeparatorChar)
    Assert-Condition ((Read-UnityFsHeader $bundlePath) -eq "UnityFS") "bundle '$bundleKey' does not begin with the UnityFS header."
    $actualBundleHash = Get-Sha256 $bundlePath
    $expectedBundleHash = $expectedBundleHashes[$bundleKey]
    Assert-Condition ([string]::Equals($actualBundleHash, $expectedBundleHash, [StringComparison]::Ordinal)) "bundle '$bundleKey' SHA-256 '$actualBundleHash' does not match accepted hash '$expectedBundleHash'."
}

$forbiddenPathPatterns = @(
    "(?i)(^|/)(Unity|Art|SDK|Logs?)(/|$)",
    "(?i)\.(blend|blend1|cs|csproj|fs|fsproj|vb|vbproj|sln|pdb|mdb|ps1|unity|prefab|meta|fbx|psd|asmdef|log|tmp|bak)$",
    "(?i)(^|/)(\.env($|\.)|credentials?($|\.)|secrets?($|\.)|id_rsa|id_ed25519|.*\.(pem|pfx|key))$"
)
foreach ($relativePath in $actualFiles) {
    foreach ($pattern in $forbiddenPathPatterns) {
        Assert-Condition ($relativePath -notmatch $pattern) "forbidden release artifact '$relativePath' was staged."
    }
}

foreach ($relativePath in $actualFiles) {
    $fullPath = Join-Path $resolvedStagePath $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
    Assert-Condition (!(Test-BinaryContainsSecret $fullPath)) "possible secret material was found in '$relativePath'."
}

Assert-Condition (Test-Path -LiteralPath $resolvedArchivePath -PathType Leaf) "archive '$resolvedArchivePath' is missing."
Assert-Condition (Test-Path -LiteralPath $resolvedHashPath -PathType Leaf) "hash manifest '$resolvedHashPath' is missing."

Add-Type -AssemblyName System.IO.Compression
$roundTripRoot = Join-Path ([IO.Path]::GetTempPath()) ("ContrabandCases-package-validation-" + [Guid]::NewGuid().ToString("N"))
[void](New-Item -ItemType Directory -Path $roundTripRoot)
try {
    $archiveStream = [IO.File]::OpenRead($resolvedArchivePath)
    try {
        $zip = New-Object IO.Compression.ZipArchive($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $false)
        try {
            $entries = @($zip.Entries)
            $entryNames = @($entries | ForEach-Object { $_.FullName })
            Assert-ExactSequence "archive entry set" $expectedFiles $entryNames
            Assert-SequenceInOrder "archive entry order" (Sort-Ordinal $expectedFiles) $entryNames
            Assert-Condition (@($entryNames | Group-Object | Where-Object { $_.Count -ne 1 }).Count -eq 0) "archive contains a duplicate entry."

            for ($index = 0; $index -lt $entries.Count; $index++) {
                $entry = $entries[$index]
                $entryName = $entry.FullName
                Assert-Condition (!$entryName.Contains("\")) "archive entry '$entryName' uses a backslash."
                Assert-Condition (!$entryName.StartsWith("/", [StringComparison]::Ordinal) -and $entryName -notmatch "(^|/)\.\.(/|$)" -and $entryName -notmatch "^[A-Za-z]:") "archive entry '$entryName' is unsafe."
                Assert-Condition ($entry.LastWriteTime.Year -eq $packageTimestampUtc.Year -and $entry.LastWriteTime.Month -eq $packageTimestampUtc.Month -and $entry.LastWriteTime.Day -eq $packageTimestampUtc.Day -and $entry.LastWriteTime.Hour -eq $packageTimestampUtc.Hour -and $entry.LastWriteTime.Minute -eq $packageTimestampUtc.Minute -and $entry.LastWriteTime.Second -eq $packageTimestampUtc.Second) "archive entry '$entryName' does not have the 0.4.16 release timestamp."

                $outputPath = [IO.Path]::GetFullPath((Join-Path $roundTripRoot $entryName.Replace("/", [IO.Path]::DirectorySeparatorChar)))
                $roundTripPrefix = $roundTripRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
                Assert-Condition ($outputPath.StartsWith($roundTripPrefix, [StringComparison]::OrdinalIgnoreCase)) "archive entry '$entryName' escapes the extraction root."
                $outputDirectory = Split-Path -Parent $outputPath
                if (!(Test-Path -LiteralPath $outputDirectory -PathType Container)) {
                    [void](New-Item -ItemType Directory -Path $outputDirectory -Force)
                }

                $entryInput = $entry.Open()
                try {
                    $entryOutput = [IO.File]::Open($outputPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try {
                        $entryInput.CopyTo($entryOutput)
                    }
                    finally {
                        $entryOutput.Dispose()
                    }
                }
                finally {
                    $entryInput.Dispose()
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $archiveStream.Dispose()
    }

    foreach ($relativePath in $expectedFiles) {
        $stageFile = Join-Path $resolvedStagePath $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
        $roundTripFile = Join-Path $roundTripRoot $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
        Assert-Condition ((Get-Sha256 $stageFile) -eq (Get-Sha256 $roundTripFile)) "archive round-trip changed '$relativePath'."
    }

    $rebuiltArchive = Join-Path ([IO.Path]::GetTempPath()) ("ContrabandCases-package-rebuilt-" + [Guid]::NewGuid().ToString("N") + ".zip")
    try {
        New-DeterministicArchive $roundTripRoot $rebuiltArchive $expectedFiles $packageTimestampUtc
        Assert-Condition ((Get-Sha256 $resolvedArchivePath) -eq (Get-Sha256 $rebuiltArchive)) "archive is not reproducible from its extracted stage tree."
    }
    finally {
        if (Test-Path -LiteralPath $rebuiltArchive -PathType Leaf) {
            Remove-Item -LiteralPath $rebuiltArchive -Force
        }
    }
}
finally {
    if (Test-Path -LiteralPath $roundTripRoot -PathType Container) {
        Remove-Item -LiteralPath $roundTripRoot -Recurse -Force
    }
}

$hashRecords = @()
foreach ($relativePath in $expectedFiles) {
    $fullPath = Join-Path $resolvedStagePath $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
    $hashRecords += [pscustomobject]@{ Label = "stage/$relativePath"; Hash = Get-Sha256 $fullPath }
}
$hashRecords += [pscustomobject]@{ Label = "archive/$(Split-Path -Leaf $resolvedArchivePath)"; Hash = Get-Sha256 $resolvedArchivePath }
$orderedHashLabels = @(Sort-Ordinal @($hashRecords | ForEach-Object { $_.Label }))
$expectedHashLines = foreach ($label in $orderedHashLabels) {
    $record = @($hashRecords | Where-Object { [string]::Equals($_.Label, $label, [StringComparison]::Ordinal) })
    Assert-Condition ($record.Count -eq 1) "hash label '$label' is not unique."
    "$($record[0].Hash)  $label"
}
$expectedHashText = ($expectedHashLines -join "`n") + "`n"
$actualHashText = [IO.File]::ReadAllText($resolvedHashPath)
Assert-Condition ([string]::Equals($actualHashText, $expectedHashText, [StringComparison]::Ordinal)) "hash manifest content does not exactly match the staged files and archive."

if ($isSourceProject) {
    Assert-ValidationSourceStateUnchanged $validationSourceState $releaseArtifactMappings
}

Write-Host "Validated Contraband Cases 0.4.16 package:"
Write-Host "  Stage:   $resolvedStagePath"
Write-Host "  Archive: $resolvedArchivePath"
Write-Host "  SHA-256: $(Get-Sha256 $resolvedArchivePath)"
