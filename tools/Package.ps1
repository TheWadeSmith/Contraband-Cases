[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$StagePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot "..\dist"))
$packageBaseName = "ContrabandCases-0.4.9-SPT4.1.5"
$packageTimestampUtc = [DateTime]::SpecifyKind([DateTime]"2026-09-07T00:00:00", [DateTimeKind]::Utc)
$canonicalStagePath = [IO.Path]::GetFullPath((Join-Path $distRoot "stage"))
$canonicalArchivePath = [IO.Path]::GetFullPath((Join-Path $distRoot "$packageBaseName.zip"))
$canonicalHashPath = [IO.Path]::GetFullPath((Join-Path $distRoot "$packageBaseName-SHA256.txt"))

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
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (!$Condition) {
        throw "Packaging failed: $Message"
    }
}

function Convert-ToPackagePath {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.Replace("\", "/")
}

function Sort-Ordinal {
    param([string[]]$Values)

    $copy = [string[]]@($Values)
    [Array]::Sort($copy, [StringComparer]::Ordinal)
    return $copy
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
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

function Get-ProjectBuildInputs {
    param(
        [Parameter(Mandatory = $true)][string[]]$ProjectDirectories,
        [Parameter(Mandatory = $true)][string[]]$AdditionalInputs
    )

    $inputs = @()
    foreach ($projectDirectory in $ProjectDirectories) {
        Assert-Condition (Test-Path -LiteralPath $projectDirectory -PathType Container) "required project input directory '$projectDirectory' is missing."
        $inputs += @(
            Get-ChildItem -LiteralPath $projectDirectory -Force -Recurse -File |
                Where-Object { $_.FullName -notmatch "[\\/](bin|obj)[\\/]" }
        )
    }

    foreach ($additionalInput in $AdditionalInputs) {
        Assert-Condition (Test-Path -LiteralPath $additionalInput -PathType Leaf) "required shared build input '$additionalInput' is missing."
        $inputs += Get-Item -LiteralPath $additionalInput -Force
    }

    Assert-Condition ($inputs.Count -gt 0) "no source inputs were found for the release build freshness check."
    return @($inputs)
}

function Get-ReleaseBuildSpecifications {
    return @(
        [pscustomobject]@{ Name = "Client"; Artifact = Join-Path $projectRoot "Client\bin\$Configuration\netstandard2.1\ContrabandCases.Client.dll"; Projects = @((Join-Path $projectRoot "Client"), (Join-Path $projectRoot "Shared")) },
        [pscustomobject]@{ Name = "Shared"; Artifact = Join-Path $projectRoot "Shared\bin\$Configuration\netstandard2.1\ContrabandCases.Shared.dll"; Projects = @((Join-Path $projectRoot "Shared")) },
        [pscustomobject]@{ Name = "Server"; Artifact = Join-Path $projectRoot "Server\bin\$Configuration\net10.0\ContrabandCases.Server.dll"; Projects = @((Join-Path $projectRoot "Server"), (Join-Path $projectRoot "Shared")) }
    )
}

function Get-ReleaseInputs {
    return @(
        [pscustomobject]@{ Source = Join-Path $projectRoot "Client\bin\$Configuration\netstandard2.1\ContrabandCases.Client.dll"; Destination = "BepInEx/plugins/ContrabandCases/ContrabandCases.Client.dll" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "Shared\bin\$Configuration\netstandard2.1\ContrabandCases.Shared.dll"; Destination = "BepInEx/plugins/ContrabandCases/ContrabandCases.Shared.dll" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "Server\bin\$Configuration\net10.0\ContrabandCases.Server.dll"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/ContrabandCases.Server.dll" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "Shared\bin\$Configuration\netstandard2.1\ContrabandCases.Shared.dll"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/ContrabandCases.Shared.dll" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "README.md"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/README.md" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "LICENSE.md"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/LICENSE.md" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "bundles.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "bundles\contrabandcases\br12_case.bundle"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles/contrabandcases/br12_case.bundle" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "bundles\contrabandcases\br12_key.bundle"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/bundles/contrabandcases/br12_key.bundle" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\config.jsonc"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/config.jsonc" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\rewards.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/rewards.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\amonya.arcane-cache.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/amonya.arcane-cache.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\core.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/core.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\eco-attachment.elite-optics.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/eco-attachment.elite-optics.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\eco-attachment.field-cache.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/eco-attachment.field-cache.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\eco-ww2.relic-cache.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/eco-ww2.relic-cache.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\isb-aishi.elite-armory.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/isb-aishi.elite-armory.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\isb-aishi.field-armory.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/isb-aishi.field-armory.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\krackasourus.anime-cards.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/krackasourus.anime-cards.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\krackasourus.pokemon-cards.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/krackasourus.pokemon-cards.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\krackasourus.yugioh-cards.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/krackasourus.yugioh-cards.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\natalya.elite-armor.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/natalya.elite-armor.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\natalya.field-gear.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/natalya.field-gear.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\sjx.combat-chemistry.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/sjx.combat-chemistry.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\vault.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/vault.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\vultify.cooler-stims.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/vultify.cooler-stims.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\wtt-contentbackport.elite-optics.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/wtt-contentbackport.elite-optics.json" },
        [pscustomobject]@{ Source = Join-Path $projectRoot "config\reward-packs\wtt-contentbackport.field-resupply.json"; Destination = "SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/wtt-contentbackport.field-resupply.json" }
    )
}

function Assert-ReleaseBuildsAreFresh {
    $commonInputs = @(Join-Path $projectRoot "Directory.Build.props")
    foreach ($build in @(Get-ReleaseBuildSpecifications)) {
        Assert-Condition (Test-Path -LiteralPath $build.Artifact -PathType Leaf) "required $($build.Name) release DLL '$($build.Artifact)' is missing. Build all projects in Release first."
        $artifact = Get-Item -LiteralPath $build.Artifact -Force
        $projectDirectories = @($build.Projects)
        $inputs = Get-ProjectBuildInputs $projectDirectories $commonInputs
        $newestInput = $inputs | Sort-Object -Property LastWriteTimeUtc -Descending | Select-Object -First 1
        Assert-Condition ($artifact.LastWriteTimeUtc -ge $newestInput.LastWriteTimeUtc) "$($build.Name) release DLL '$($build.Artifact)' predates build input '$($newestInput.FullName)' ($($newestInput.LastWriteTimeUtc.ToString('o'))). Rebuild Release before packaging."
    }
}

function Get-PackageInputState {
    param([Parameter(Mandatory = $true)][object[]]$ReleaseInputs)

    $paths = @($ReleaseInputs | ForEach-Object { $_.Source })
    $commonInputs = @(Join-Path $projectRoot "Directory.Build.props")
    foreach ($build in @(Get-ReleaseBuildSpecifications)) {
        $paths += $build.Artifact
        $projectDirectories = @($build.Projects)
        $paths += @(Get-ProjectBuildInputs $projectDirectories $commonInputs | ForEach-Object { $_.FullName })
    }

    $records = @()
    foreach ($path in @($paths | Sort-Object -Unique)) {
        Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "packaging input '$path' disappeared while its state was captured."
        $item = Get-Item -LiteralPath $path -Force
        $records += [pscustomobject]@{ Path = $item.FullName; Length = $item.Length; LastWriteTimeUtc = $item.LastWriteTimeUtc.Ticks; Hash = Get-Sha256 $item.FullName }
    }
    return @($records)
}

function Assert-PackageInputStateUnchanged {
    param(
        [Parameter(Mandatory = $true)][object[]]$Snapshot,
        [Parameter(Mandatory = $true)][object[]]$ReleaseInputs
    )

    $current = @(Get-PackageInputState $ReleaseInputs)
    Assert-Condition ($Snapshot.Count -eq $current.Count) "packaging input set changed during staging or validation."
    for ($index = 0; $index -lt $Snapshot.Count; $index++) {
        $before = $Snapshot[$index]
        $after = $current[$index]
        Assert-Condition ([string]::Equals($before.Path, $after.Path, [StringComparison]::OrdinalIgnoreCase) -and $before.Length -eq $after.Length -and $before.LastWriteTimeUtc -eq $after.LastWriteTimeUtc -and [string]::Equals($before.Hash, $after.Hash, [StringComparison]::Ordinal)) "packaging input '$($before.Path)' changed during staging or validation."
    }
}

function Assert-SafeDistributionPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateSet("Any", "File", "Directory")][string]$ExpectedKind = "Any",
        [bool]$MustExist = $false
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    $distPrefix = $distRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Assert-Condition ($resolved.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)) "distribution output '$resolved' is outside '$distRoot'."

    if (!(Test-Path -LiteralPath $resolved)) {
        Assert-Condition (!$MustExist) "required distribution output '$resolved' is missing."
        return
    }

    $item = Get-Item -LiteralPath $resolved -Force
    Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "distribution output '$resolved' must not be a reparse point."
    if ($ExpectedKind -eq "File") {
        Assert-Condition (!$item.PSIsContainer) "distribution output '$resolved' must be a file."
    }
    elseif ($ExpectedKind -eq "Directory") {
        Assert-Condition ($item.PSIsContainer) "distribution output '$resolved' must be a directory."
    }

    if ($item.PSIsContainer) {
        $reparseItems = @(
            Get-ChildItem -LiteralPath $resolved -Force -Recurse |
                Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
        )
        $reparseNames = @($reparseItems | ForEach-Object { $_.FullName }) -join ", "
        Assert-Condition ($reparseItems.Count -eq 0) "distribution directory '$resolved' contains reparse points: '$reparseNames'."
    }
}

function Remove-SafeDistributionPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (!(Test-Path -LiteralPath $Path)) {
        return
    }

    Assert-SafeDistributionPath -Path $Path -MustExist $true
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Invoke-PackageValidator {
    param(
        [Parameter(Mandatory = $true)][string]$ValidatorPath,
        [Parameter(Mandatory = $true)][string]$StagePath,
        [Parameter(Mandatory = $true)][string]$ArchivePath,
        [Parameter(Mandatory = $true)][string]$HashPath
    )

    $hostExecutable = (Get-Process -Id $PID).Path
    $validatorLiteral = $ValidatorPath.Replace("'", "''")
    $stageLiteral = $StagePath.Replace("'", "''")
    $archiveLiteral = $ArchivePath.Replace("'", "''")
    $hashLiteral = $HashPath.Replace("'", "''")
    $commandText = "& '$validatorLiteral' -StagePath '$stageLiteral' -ArchivePath '$archiveLiteral' -HashPath '$hashLiteral'"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($commandText))

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $hostExecutable
    $startInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand $encodedCommand"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    $standardOutput = ""
    $standardError = ""
    try {
        [void]$process.Start()
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $standardOutput = $standardOutputTask.Result
        $standardError = $standardErrorTask.Result
        $exitCode = $process.ExitCode
    }
    catch {
        throw "Packaging failed: could not run package validator '$ValidatorPath': $($_.Exception.Message)"
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        $failureOutput = ((($standardOutput + $standardError) -replace "(?m)^\s*\|\s?", "") -replace "\s+", " ").Trim()
        throw "Packaging failed: package validator exited with code $exitCode. $failureOutput"
    }

    if (![string]::IsNullOrWhiteSpace($standardOutput)) {
        Write-Host ($standardOutput.TrimEnd())
    }
    if (![string]::IsNullOrWhiteSpace($standardError)) {
        Write-Warning ($standardError.Trim()) -WarningAction Continue
    }
}

function Publish-PackageCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$CandidateStagePath,
        [Parameter(Mandatory = $true)][string]$CandidateArchivePath,
        [Parameter(Mandatory = $true)][string]$CandidateHashPath,
        [Parameter(Mandatory = $true)][string]$ValidatorPath,
        [Parameter(Mandatory = $true)][object[]]$InputSnapshot,
        [Parameter(Mandatory = $true)][object[]]$ReleaseInputs
    )

    $rollbackRoot = [IO.Path]::GetFullPath((Join-Path $distRoot (".previous-" + [Guid]::NewGuid().ToString("N"))))
    Assert-SafeDistributionPath -Path $rollbackRoot
    $outputs = @(
        [pscustomobject]@{ Candidate = $CandidateStagePath; Target = $resolvedStagePath; Backup = Join-Path $rollbackRoot "stage"; Kind = "Directory" },
        [pscustomobject]@{ Candidate = $CandidateArchivePath; Target = $resolvedArchivePath; Backup = Join-Path $rollbackRoot (Split-Path -Leaf $resolvedArchivePath); Kind = "File" },
        [pscustomobject]@{ Candidate = $CandidateHashPath; Target = $resolvedHashPath; Backup = Join-Path $rollbackRoot (Split-Path -Leaf $resolvedHashPath); Kind = "File" }
    )
    foreach ($output in $outputs) {
        Assert-SafeDistributionPath -Path $output.Candidate -ExpectedKind $output.Kind -MustExist $true
        Assert-SafeDistributionPath -Path $output.Target -ExpectedKind $output.Kind
        Assert-SafeDistributionPath -Path $output.Backup -ExpectedKind $output.Kind
    }

    [void](New-Item -ItemType Directory -Path $rollbackRoot)
    $movedOld = @()
    $movedNew = @()
    try {
        foreach ($output in $outputs) {
            if (Test-Path -LiteralPath $output.Target) {
                Move-Item -LiteralPath $output.Target -Destination $output.Backup
                $movedOld += $output
            }
        }
        foreach ($output in $outputs) {
            Move-Item -LiteralPath $output.Candidate -Destination $output.Target
            $movedNew += $output
        }

        Invoke-PackageValidator `
            -ValidatorPath $ValidatorPath `
            -StagePath $resolvedStagePath `
            -ArchivePath $resolvedArchivePath `
            -HashPath $resolvedHashPath
        Assert-PackageInputStateUnchanged $InputSnapshot $ReleaseInputs
    }
    catch {
        $publicationFailure = $_
        $rollbackFailures = @()
        for ($index = $movedNew.Count - 1; $index -ge 0; $index--) {
            $output = $movedNew[$index]
            try {
                Remove-SafeDistributionPath -Path $output.Target
            }
            catch {
                $rollbackFailures += "could not remove rejected output '$($output.Target)': $($_.Exception.Message)"
            }
        }
        for ($index = $movedOld.Count - 1; $index -ge 0; $index--) {
            $output = $movedOld[$index]
            try {
                if (Test-Path -LiteralPath $output.Backup) {
                    if (Test-Path -LiteralPath $output.Target) {
                        $rollbackFailures += "could not restore prior output '$($output.Target)' because the rejected target still exists; prior output remains at '$($output.Backup)'"
                        continue
                    }
                    Move-Item -LiteralPath $output.Backup -Destination $output.Target
                }
            }
            catch {
                $rollbackFailures += "could not restore prior output '$($output.Target)': $($_.Exception.Message)"
            }
        }

        if ($rollbackFailures.Count -gt 0) {
            throw "Packaging failed during publication and rollback was incomplete. Prior outputs remain under '$rollbackRoot'. Initial failure: $($publicationFailure.Exception.Message) Rollback failures: $($rollbackFailures -join '; ')"
        }

        try {
            Remove-SafeDistributionPath -Path $rollbackRoot
        }
        catch {
            throw "Packaging failed during publication. Prior canonical outputs were restored, but cleanup of '$rollbackRoot' failed: $($_.Exception.Message) Initial failure: $($publicationFailure.Exception.Message)"
        }
        throw $publicationFailure
    }

    try {
        Remove-SafeDistributionPath -Path $rollbackRoot
    }
    catch {
        Write-Warning "Published package validation succeeded, but cleanup of prior-output backup '$rollbackRoot' failed: $($_.Exception.Message)" -WarningAction Continue
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

$resolvedStagePath = Resolve-PackagePath $StagePath $canonicalStagePath
$resolvedArchivePath = $canonicalArchivePath
$resolvedHashPath = $canonicalHashPath

Assert-Condition ([string]::Equals($resolvedStagePath, $canonicalStagePath, [StringComparison]::OrdinalIgnoreCase)) "the stage path must be the exact canonical directory '$canonicalStagePath'."
Assert-Condition (!$resolvedArchivePath.StartsWith($resolvedStagePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) "the archive must remain outside the stage tree."
Assert-Condition (!$resolvedHashPath.StartsWith($resolvedStagePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) "the hash manifest must remain outside the stage tree."

$releaseInputs = Get-ReleaseInputs
$packageInputState = Get-PackageInputState $releaseInputs

# Capture and verify all inputs before any stage or output is removed.
Assert-ReleaseBuildsAreFresh
Assert-PackageInputStateUnchanged $packageInputState $releaseInputs

if (!(Test-Path -LiteralPath $distRoot -PathType Container)) {
    [void](New-Item -ItemType Directory -Path $distRoot)
}
$distItem = Get-Item -LiteralPath $distRoot -Force
Assert-Condition (($distItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "distribution root '$distRoot' must not be a reparse point."

$packageLockPath = [IO.Path]::GetFullPath((Join-Path $distRoot ".contrabandcases-package.lock"))
Assert-SafeDistributionPath -Path $packageLockPath -ExpectedKind "File"
$packageLock = $null
try {
    $packageLock = [IO.File]::Open(
        $packageLockPath,
        [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
}
catch {
    throw "Packaging failed: could not acquire the exclusive package lock '$packageLockPath'. Another package run may be active. $($_.Exception.Message)"
}

try {
    $transactionDebris = @(
        Get-ChildItem -LiteralPath $distRoot -Force |
            Where-Object { $_.Name -like ".candidate-*" -or $_.Name -like ".previous-*" }
    )
    $transactionDebrisNames = @($transactionDebris | ForEach-Object { $_.FullName }) -join ", "
    Assert-Condition ($transactionDebris.Count -eq 0) "distribution root contains interrupted package transaction data: '$transactionDebrisNames'. Inspect and recover or remove it before packaging again."

    Assert-SafeDistributionPath -Path $resolvedStagePath -ExpectedKind "Directory"
    Assert-SafeDistributionPath -Path $resolvedArchivePath -ExpectedKind "File"
    Assert-SafeDistributionPath -Path $resolvedHashPath -ExpectedKind "File"

$candidateRoot = [IO.Path]::GetFullPath((Join-Path $distRoot (".candidate-" + [Guid]::NewGuid().ToString("N"))))
$candidateStagePath = Join-Path $candidateRoot "stage"
$candidateArchivePath = Join-Path $candidateRoot (Split-Path -Leaf $resolvedArchivePath)
$candidateHashPath = Join-Path $candidateRoot (Split-Path -Leaf $resolvedHashPath)
Assert-SafeDistributionPath -Path $candidateRoot
[void](New-Item -ItemType Directory -Path $candidateStagePath -Force)

try {
    Assert-PackageInputStateUnchanged $packageInputState $releaseInputs

    $orderedInputs = @($releaseInputs | Sort-Object -Property Destination)
    foreach ($releaseInput in $orderedInputs) {
        Assert-Condition (Test-Path -LiteralPath $releaseInput.Source -PathType Leaf) "required release input '$($releaseInput.Source)' is missing. Build all projects in Release first."
        $sourceItem = Get-Item -LiteralPath $releaseInput.Source -Force
        Assert-Condition (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) "release input '$($releaseInput.Source)' must not be a reparse point."

        $destinationPath = Join-Path $candidateStagePath $releaseInput.Destination.Replace("/", [IO.Path]::DirectorySeparatorChar)
        $destinationDirectory = Split-Path -Parent $destinationPath
        if (!(Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
            [void](New-Item -ItemType Directory -Path $destinationDirectory -Force)
        }
        [IO.File]::Copy($releaseInput.Source, $destinationPath, $false)
        [IO.File]::SetLastWriteTimeUtc($destinationPath, $packageTimestampUtc)
    }

    Assert-PackageInputStateUnchanged $packageInputState $releaseInputs

    $relativeFiles = @($releaseInputs | ForEach-Object { Convert-ToPackagePath $_.Destination })
    New-DeterministicArchive $candidateStagePath $candidateArchivePath $relativeFiles $packageTimestampUtc

    $hashRecords = @()
    foreach ($relativePath in $relativeFiles) {
        $fullPath = Join-Path $candidateStagePath $relativePath.Replace("/", [IO.Path]::DirectorySeparatorChar)
        $hashRecords += [pscustomobject]@{ Label = "stage/$relativePath"; Hash = Get-Sha256 $fullPath }
    }
    $hashRecords += [pscustomobject]@{ Label = "archive/$(Split-Path -Leaf $candidateArchivePath)"; Hash = Get-Sha256 $candidateArchivePath }
    $orderedHashLabels = @(Sort-Ordinal @($hashRecords | ForEach-Object { $_.Label }))
    $hashLines = foreach ($label in $orderedHashLabels) {
        $record = @($hashRecords | Where-Object { [string]::Equals($_.Label, $label, [StringComparison]::Ordinal) })
        Assert-Condition ($record.Count -eq 1) "hash label '$label' is not unique."
        "$($record[0].Hash)  $label"
    }
    $hashText = ($hashLines -join "`n") + "`n"
    [IO.File]::WriteAllText($candidateHashPath, $hashText, (New-Object Text.UTF8Encoding($false)))

    $validator = Join-Path $PSScriptRoot "Validate-Package.ps1"
    Invoke-PackageValidator `
        -ValidatorPath $validator `
        -StagePath $candidateStagePath `
        -ArchivePath $candidateArchivePath `
        -HashPath $candidateHashPath
    Assert-PackageInputStateUnchanged $packageInputState $releaseInputs
    Publish-PackageCandidate `
        -CandidateStagePath $candidateStagePath `
        -CandidateArchivePath $candidateArchivePath `
        -CandidateHashPath $candidateHashPath `
        -ValidatorPath $validator `
        -InputSnapshot $packageInputState `
        -ReleaseInputs $releaseInputs
}
finally {
    try {
        Remove-SafeDistributionPath -Path $candidateRoot
    }
    catch {
        Write-Warning "Package candidate cleanup failed for '$candidateRoot': $($_.Exception.Message)" -WarningAction Continue
    }
}

    Write-Host "Created installable archive: $resolvedArchivePath"
    Write-Host "Created checksum manifest: $resolvedHashPath"
}
finally {
    if ($null -ne $packageLock) {
        $packageLock.Dispose()
    }
}
