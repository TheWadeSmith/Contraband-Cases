[CmdletBinding()]
param(
    [string]$ProjectRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$packageTimestampUtc = [DateTime]::SpecifyKind([DateTime]"2026-09-07T00:00:00", [DateTimeKind]::Utc)
$packageDosDate = [uint16]((($packageTimestampUtc.Year - 1980) -shl 9) -bor ($packageTimestampUtc.Month -shl 5) -bor $packageTimestampUtc.Day)
$packageDosTime = [uint16](($packageTimestampUtc.Hour -shl 11) -bor ($packageTimestampUtc.Minute -shl 5) -bor ([int]($packageTimestampUtc.Second / 2)))

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Assert-Condition {
    param([bool]$Condition, [string]$Message)
    if (!$Condition) { throw "Package gate regression failed: $Message" }
}

function Get-DirectoryByteSnapshot {
    param([string]$Path)

    $resolvedRoot = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    $records = @(
        Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse |
            ForEach-Object {
                $relativePath = $_.FullName.Substring($prefix.Length).Replace("\", "/")
                "$relativePath|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
            }
    )
    [Array]::Sort($records, [StringComparer]::Ordinal)
    return ($records -join "`n")
}

function Get-ArchiveHeaderTimestamps {
    param([string]$ArchivePath)

    $stream = [IO.File]::OpenRead($ArchivePath)
    try {
        $reader = New-Object IO.BinaryReader($stream, [Text.Encoding]::UTF8, $true)
        try {
            Assert-Condition ($reader.ReadUInt32() -eq [uint32]0x04034b50) "archive does not begin with a local ZIP header."
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            $localTime = $reader.ReadUInt16()
            $localDate = $reader.ReadUInt16()

            Assert-Condition ($stream.Length -ge 22) "archive is too short to contain a ZIP end record."
            [void]$stream.Seek(-22, [IO.SeekOrigin]::End)
            Assert-Condition ($reader.ReadUInt32() -eq [uint32]0x06054b50) "archive does not end with the expected ZIP end record."
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt32()
            $centralOffset = $reader.ReadUInt32()

            [void]$stream.Seek($centralOffset, [IO.SeekOrigin]::Begin)
            Assert-Condition ($reader.ReadUInt32() -eq [uint32]0x02014b50) "archive central directory does not begin with a central ZIP header."
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            [void]$reader.ReadUInt16()
            $centralTime = $reader.ReadUInt16()
            $centralDate = $reader.ReadUInt16()

            return [pscustomobject]@{
                LocalDate = $localDate
                LocalTime = $localTime
                CentralDate = $centralDate
                CentralTime = $centralTime
            }
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function New-TemporaryProjectClone {
    param([string]$Label)

    $root = Join-Path ([IO.Path]::GetTempPath()) ("contraband-package-gate-$Label-" + [Guid]::NewGuid().ToString("N"))
    $clone = Join-Path $root "ContrabandCases"
    [void](New-Item -ItemType Directory -Path $clone -Force)
    foreach ($name in @("Client", "Shared", "Server", "bundles", "config")) {
        Copy-Item -LiteralPath (Join-Path $ProjectRoot $name) -Destination $clone -Recurse -Force
    }
    # Diagnostic tools have their own build output/dependencies; they are not
    # packaging inputs and must not be replicated into every isolated fixture.
    $cloneTools = Join-Path $clone "tools"
    [void](New-Item -ItemType Directory -Path $cloneTools)
    foreach ($name in @("Package.ps1", "Validate-Package.ps1", "Package-Gate-Regression.ps1")) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $ProjectRoot "tools") $name) -Destination $cloneTools
    }
    foreach ($name in @("README.md", "LICENSE.md", "bundles.json", "Directory.Build.props")) {
        Copy-Item -LiteralPath (Join-Path $ProjectRoot $name) -Destination $clone -Force
    }

    $fresh = [DateTime]::UtcNow.AddMinutes(5)
    foreach ($dll in @(
        "Client\bin\Release\netstandard2.1\ContrabandCases.Client.dll",
        "Shared\bin\Release\netstandard2.1\ContrabandCases.Shared.dll",
        "Server\bin\Release\net10.0\ContrabandCases.Server.dll"
    )) {
        (Get-Item -LiteralPath (Join-Path $clone $dll)).LastWriteTimeUtc = $fresh
    }
    return $root
}

function Invoke-ProductionScript {
    param([string]$ScriptPath)

    $hostExecutable = (Get-Process -Id $PID).Path
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $hostExecutable
    $startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`""
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
    finally {
        $process.Dispose()
    }

    $script:LastProductionOutput = $standardOutput + $standardError
    return $exitCode
}

function Invoke-ExpectFailure {
    param([string]$ScriptPath, [string]$Description, [string[]]$ExpectedMessagePatterns)

    $exitCode = Invoke-ProductionScript $ScriptPath
    Assert-Condition ($exitCode -ne 0) "$Description unexpectedly succeeded."
    $normalizedOutput = (($script:LastProductionOutput -replace "(?m)^\s*\|\s?", "") -replace "\s+", " ").Trim()
    foreach ($expectedMessagePattern in $ExpectedMessagePatterns) {
        $normalizedPattern = ($expectedMessagePattern -replace "\s+", " ").Trim()
        Assert-Condition ([Text.RegularExpressions.Regex]::IsMatch($normalizedOutput, $normalizedPattern)) "$Description failed without expected message pattern '$expectedMessagePattern'. Actual output: $normalizedOutput"
    }
}

$temporaryRoots = @()
try {
    # Package must preserve the pre-clear snapshot when a source artifact changes after deletion begins.
    $packageRoot = New-TemporaryProjectClone "package-race"
    $temporaryRoots += $packageRoot
    $packageScript = Join-Path $packageRoot "ContrabandCases\tools\Package.ps1"
    $legacyArchivePath = Join-Path $packageRoot "dist\ContrabandCases-0.2.0-SPT4.1.3.zip"
    $legacyHashPath = Join-Path $packageRoot "dist\ContrabandCases-0.2.0-SPT4.1.3-SHA256.txt"
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $legacyArchivePath) -Force)
    [IO.File]::WriteAllBytes($legacyArchivePath, [byte[]](0x50, 0x4B, 0x03, 0x04, 0x02))
    [IO.File]::WriteAllText($legacyHashPath, "legacy checksum sentinel`n", (New-Object Text.UTF8Encoding($false)))
    $legacyArchiveHash = (Get-FileHash -LiteralPath $legacyArchivePath -Algorithm SHA256).Hash
    $legacyHashManifestHash = (Get-FileHash -LiteralPath $legacyHashPath -Algorithm SHA256).Hash
    Assert-Condition ((Invoke-ProductionScript $packageScript) -eq 0) "clean package setup failed."
    Assert-Condition ((Get-FileHash -LiteralPath $legacyArchivePath -Algorithm SHA256).Hash -eq $legacyArchiveHash) "packaging changed or removed the preserved 0.2.0 archive."
    Assert-Condition ((Get-FileHash -LiteralPath $legacyHashPath -Algorithm SHA256).Hash -eq $legacyHashManifestHash) "packaging changed or removed the preserved 0.2.0 checksum manifest."
    $stage = Join-Path $packageRoot "dist\stage"
    $validationScript = Join-Path $packageRoot "ContrabandCases\tools\Validate-Package.ps1"
    $stagedClientDll = Join-Path $stage "BepInEx\plugins\ContrabandCases\ContrabandCases.Client.dll"
    $archivePath = Join-Path $packageRoot "dist\ContrabandCases-0.4.8-SPT4.1.5.zip"
    $hashPath = Join-Path $packageRoot "dist\ContrabandCases-0.4.8-SPT4.1.5-SHA256.txt"

    # The dist-scoped lock must reject a concurrent package run without touching canonical outputs.
    $lockedStageSnapshot = Get-DirectoryByteSnapshot $stage
    $lockedArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    $lockedManifestHash = (Get-FileHash -LiteralPath $hashPath -Algorithm SHA256).Hash
    $packageLockPath = Join-Path $packageRoot "dist\.contrabandcases-package.lock"
    $heldPackageLock = [IO.File]::Open($packageLockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Invoke-ExpectFailure $packageScript "concurrent package lock" @("Packaging failed: could not acquire the exclusive package lock '.+\.contrabandcases-package\.lock'\. Another package run may be active\.")
    }
    finally {
        $heldPackageLock.Dispose()
    }
    Assert-Condition ([string]::Equals((Get-DirectoryByteSnapshot $stage), $lockedStageSnapshot, [StringComparison]::Ordinal)) "concurrent package rejection changed the canonical stage tree."
    Assert-Condition ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -eq $lockedArchiveHash) "concurrent package rejection changed the canonical archive."
    Assert-Condition ((Get-FileHash -LiteralPath $hashPath -Algorithm SHA256).Hash -eq $lockedManifestHash) "concurrent package rejection changed the canonical checksum manifest."

    # Interrupted publication state must fail closed before a new candidate is created.
    $interruptedTransactionPath = Join-Path $packageRoot "dist\.previous-interrupted-regression"
    [void](New-Item -ItemType Directory -Path $interruptedTransactionPath)
    Invoke-ExpectFailure $packageScript "interrupted package transaction" @("Packaging failed: distribution root contains interrupted package transaction data: '.+\.previous-interrupted-regression'\. Inspect and recover or remove it before packaging again\.")
    Assert-Condition ([string]::Equals((Get-DirectoryByteSnapshot $stage), $lockedStageSnapshot, [StringComparison]::Ordinal)) "interrupted-transaction rejection changed the canonical stage tree."
    Assert-Condition ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -eq $lockedArchiveHash) "interrupted-transaction rejection changed the canonical archive."
    Assert-Condition ((Get-FileHash -LiteralPath $hashPath -Algorithm SHA256).Hash -eq $lockedManifestHash) "interrupted-transaction rejection changed the canonical checksum manifest."
    Remove-Item -LiteralPath $interruptedTransactionPath -Recurse -Force

    Assert-Condition ((Get-Item -LiteralPath $stagedClientDll).LastWriteTimeUtc.Ticks -eq $packageTimestampUtc.Ticks) "packaging did not stamp staged files with the 0.4.8 release timestamp."
    $headerTimestamps = Get-ArchiveHeaderTimestamps $archivePath
    Assert-Condition ($headerTimestamps.LocalDate -eq $packageDosDate -and $headerTimestamps.LocalTime -eq $packageDosTime) "local ZIP header does not contain the 0.4.8 release timestamp."
    Assert-Condition ($headerTimestamps.CentralDate -eq $packageDosDate -and $headerTimestamps.CentralTime -eq $packageDosTime) "central ZIP header does not contain the 0.4.8 release timestamp."
    (Get-Item -LiteralPath $stagedClientDll).LastWriteTimeUtc = [DateTime]::SpecifyKind([DateTime]"1980-01-01T00:00:00", [DateTimeKind]::Utc)
    Invoke-ExpectFailure $validationScript "staged release timestamp mutation" @("Package validation failed: staged file '.+ContrabandCases\.Client\.dll' has timestamp '.+'; expected the 0\.4\.8 release timestamp '.+'\.")
    (Get-Item -LiteralPath $stagedClientDll).LastWriteTimeUtc = $packageTimestampUtc
    Assert-Condition ((Invoke-ProductionScript $validationScript) -eq 0) "restored release timestamp did not pass validation."

    $stagedPackRoot = Join-Path $stage "SPT_Runtime\user\mods\Wade-ContrabandCases\config\reward-packs"
    $extraPackPath = Join-Path $stagedPackRoot "unexpected.json"
    [IO.File]::WriteAllText($extraPackPath, "{}", (New-Object Text.UTF8Encoding($false)))
    (Get-Item -LiteralPath $extraPackPath).LastWriteTimeUtc = $packageTimestampUtc
    Invoke-ExpectFailure $validationScript "extra staged reward pack" @("Package validation failed: staged file set count was 29; expected 28\.")
    Remove-Item -LiteralPath $extraPackPath -Force

    $stagedSjxPack = Join-Path $stagedPackRoot "sjx.combat-chemistry.json"
    $sjxPackBytes = [IO.File]::ReadAllBytes($stagedSjxPack)
    Remove-Item -LiteralPath $stagedSjxPack -Force
    Invoke-ExpectFailure $validationScript "missing staged reward pack" @("Package validation failed: staged file set count was 27; expected 28\.")
    [IO.File]::WriteAllBytes($stagedSjxPack, $sjxPackBytes)
    (Get-Item -LiteralPath $stagedSjxPack).LastWriteTimeUtc = $packageTimestampUtc

    $stagedCorePack = Join-Path $stagedPackRoot "core.json"
    $corePackBytes = [IO.File]::ReadAllBytes($stagedCorePack)
    $corePackStream = [IO.File]::Open($stagedCorePack, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $corePackStream.WriteByte(0x20) } finally { $corePackStream.Dispose() }
    (Get-Item -LiteralPath $stagedCorePack).LastWriteTimeUtc = $packageTimestampUtc
    Invoke-ExpectFailure $validationScript "mutated staged reward pack" @("Package validation failed: staged artifact 'SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/core\.json' differs from current release input")
    [IO.File]::WriteAllBytes($stagedCorePack, $corePackBytes)
    (Get-Item -LiteralPath $stagedCorePack).LastWriteTimeUtc = $packageTimestampUtc
    Assert-Condition ((Invoke-ProductionScript $validationScript) -eq 0) "restored staged reward packs did not pass validation."

    $seed = Join-Path $stage "aaa-seeded-delete"
    [void](New-Item -ItemType Directory -Path $seed -Force)
    foreach ($index in 1..1500) { [IO.File]::WriteAllBytes((Join-Path $seed ("seed-{0:D4}.bin" -f $index)), [byte[]](1)) }
    $clientDll = Join-Path $packageRoot "ContrabandCases\Client\bin\Release\netstandard2.1\ContrabandCases.Client.dll"
    $mutation = Start-Job -ScriptBlock {
        param($SeedPath, $DllPath)
        while (Test-Path -LiteralPath $SeedPath) { Start-Sleep -Milliseconds 10 }
        (Get-Item -LiteralPath $DllPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(10)
    } -ArgumentList $seed, $clientDll
    Invoke-ExpectFailure $packageScript "package stage-deletion race" @("Packaging failed: packaging input '.+ContrabandCases\.Client\.dll' changed during staging or validation\.")
    Wait-Job -Job $mutation | Out-Null
    Receive-Job -Job $mutation | Out-Null
    Remove-Job -Job $mutation -Force

    # Direct validation must detect an artifact change after initial source/stage comparison.
    $validationRoot = New-TemporaryProjectClone "validation-race"
    $temporaryRoots += $validationRoot
    $validationPackage = Join-Path $validationRoot "ContrabandCases\tools\Package.ps1"
    $validationScript = Join-Path $validationRoot "ContrabandCases\tools\Validate-Package.ps1"
    $clientDll = Join-Path $validationRoot "ContrabandCases\Client\bin\Release\netstandard2.1\ContrabandCases.Client.dll"
    $padding = New-Object byte[] (8MB)
    $paddingStream = [IO.File]::Open($clientDll, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $paddingStream.Write($padding, 0, $padding.Length) } finally { $paddingStream.Dispose() }
    Assert-Condition ((Invoke-ProductionScript $validationPackage) -eq 0) "clean validation setup failed."
    $stagedClientDll = Join-Path $validationRoot "dist\stage\BepInEx\plugins\ContrabandCases\ContrabandCases.Client.dll"
    $mutation = Start-Job -ScriptBlock {
        param($StageDllPath, $SourceDllPath)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $probe = [IO.File]::Open($StageDllPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $probe.Dispose()
                Start-Sleep -Milliseconds 10
            }
            catch {
                Start-Sleep -Milliseconds 200
                try {
                    $probe = [IO.File]::Open($StageDllPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                    $probe.Dispose()
                }
                catch {
                    (Get-Item -LiteralPath $SourceDllPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes(10)
                    return
                }
            }
        }
        throw "The validator never reached the post-comparison staged-assembly boundary."
    } -ArgumentList $stagedClientDll, $clientDll
    Invoke-ExpectFailure $validationScript "direct validation race" @("Package validation failed: validation source input '.+ContrabandCases\.Client\.dll' changed while validation was running\.")
    Wait-Job -Job $mutation | Out-Null
    Assert-Condition ($mutation.State -eq "Completed") "direct validation synchronization job did not complete."
    Receive-Job -Job $mutation -ErrorAction Stop | Out-Null
    Remove-Job -Job $mutation -Force

    # A root with source-release remnants cannot claim standalone mode.
    $partialRoot = New-TemporaryProjectClone "partial-layout"
    $temporaryRoots += $partialRoot
    $partialPackage = Join-Path $partialRoot "ContrabandCases\tools\Package.ps1"
    Assert-Condition ((Invoke-ProductionScript $partialPackage) -eq 0) "clean partial-layout setup failed."
    $partialProject = Join-Path $partialRoot "ContrabandCases"
    foreach ($path in @("Client", "Shared", "Server", "Directory.Build.props")) { Remove-Item -LiteralPath (Join-Path $partialProject $path) -Recurse -Force }
    Invoke-ExpectFailure (Join-Path $partialProject "tools\Validate-Package.ps1") "partial source/release layout" @("Package validation failed: validation root is neither a complete source project nor a standalone extracted package\.")

    # A validator-only tools root plus sibling package artifacts is the sole standalone layout.
    $standaloneRoot = Join-Path ([IO.Path]::GetTempPath()) ("contraband-package-gate-standalone-" + [Guid]::NewGuid().ToString("N"))
    $temporaryRoots += $standaloneRoot
    $standaloneTools = Join-Path $standaloneRoot "ContrabandCases\tools"
    [void](New-Item -ItemType Directory -Path $standaloneTools -Force)
    Copy-Item -LiteralPath $validationScript -Destination (Join-Path $standaloneTools "Validate-Package.ps1") -Force
    Copy-Item -LiteralPath (Join-Path $validationRoot "dist") -Destination $standaloneRoot -Recurse -Force
    Assert-Condition ((Invoke-ProductionScript (Join-Path $standaloneTools "Validate-Package.ps1")) -eq 0) "validator-only standalone extraction failed."

    # The distributable config uses case stock and find-only key weight defaults.
    $configShapeRoot = New-TemporaryProjectClone "config-shape"
    $temporaryRoots += $configShapeRoot
    $configShapeProject = Join-Path $configShapeRoot "ContrabandCases"
    $configShapePath = Join-Path $configShapeProject "config\config.jsonc"
    $configShapeText = [IO.File]::ReadAllText($configShapePath).Replace('"caseStock"', '"traderStock"')
    [IO.File]::WriteAllText($configShapePath, $configShapeText, (New-Object Text.UTF8Encoding($false)))
    Invoke-ExpectFailure (Join-Path $configShapeProject "tools\Package.ps1") "legacy config property" @("Package validation failed: config property set")

    foreach ($stockCase in @(
        [pscustomobject]@{ Label = "case-stock"; Property = "caseStock"; Before = 5; After = 4 },
        [pscustomobject]@{ Label = "key-loot"; Property = "keyLootWeightPercent"; Before = "2.0"; After = "2.5" },
        [pscustomobject]@{ Label = "case-loot"; Property = "caseLootWeightPercent"; Before = "1.0"; After = "1.5" }
    )) {
        $stockRoot = New-TemporaryProjectClone $stockCase.Label
        $temporaryRoots += $stockRoot
        $stockProject = Join-Path $stockRoot "ContrabandCases"
        $stockConfigPath = Join-Path $stockProject "config\config.jsonc"
        $originalStockConfigText = [IO.File]::ReadAllText($stockConfigPath)
        $stockConfigText = $originalStockConfigText.Replace(
            '"' + $stockCase.Property + '": ' + $stockCase.Before,
            '"' + $stockCase.Property + '": ' + $stockCase.After)
        Assert-Condition ($stockConfigText -ne $originalStockConfigText) "$($stockCase.Property) fixture did not change the config."
        [IO.File]::WriteAllText($stockConfigPath, $stockConfigText, (New-Object Text.UTF8Encoding($false)))
        Invoke-ExpectFailure (Join-Path $stockProject "tools\Package.ps1") "$($stockCase.Property) canonical default" @("Package validation failed: canonical config $($stockCase.Property) must be $($stockCase.Before)\.")
    }

    $grantConfigRoot = New-TemporaryProjectClone "grant-config-default"
    $temporaryRoots += $grantConfigRoot
    $grantConfigProject = Join-Path $grantConfigRoot "ContrabandCases"
    $grantConfigPath = Join-Path $grantConfigProject "config\config.jsonc"
    $grantConfigText = [IO.File]::ReadAllText($grantConfigPath)
    $mutatedGrantConfigText = $grantConfigText.Replace('"testingInventoryGrantsEnabled": false', '"testingInventoryGrantsEnabled": true')
    Assert-Condition (![string]::Equals($grantConfigText, $mutatedGrantConfigText, [StringComparison]::Ordinal)) "testingInventoryGrantsEnabled canonical default was not found in config.jsonc."
    [IO.File]::WriteAllText($grantConfigPath, $mutatedGrantConfigText, (New-Object Text.UTF8Encoding($false)))
    Invoke-ExpectFailure (Join-Path $grantConfigProject "tools\Package.ps1") "enabled inventory grants canonical default" @("Package validation failed: canonical config testingInventoryGrantsEnabled must be false\.")

    $missingPackRoot = New-TemporaryProjectClone "missing-core-pack"
    $temporaryRoots += $missingPackRoot
    $missingPackProject = Join-Path $missingPackRoot "ContrabandCases"
    Remove-Item -LiteralPath (Join-Path $missingPackProject "config\reward-packs\core.json") -Force
    Invoke-ExpectFailure (Join-Path $missingPackProject "tools\Package.ps1") "missing canonical core pack" @("Packaging failed: packaging input '.+core\.json' disappeared while its state was captured\.")

    foreach ($packCase in @(
        [pscustomobject]@{ Label = "core-pack-schema"; RelativePath = "config\reward-packs\core.json"; Before = '"schemaVersion": 1'; After = '"schemaVersion": 2'; Description = "core pack schema mutation"; Expected = "Package validation failed: reward pack 'core' schemaVersion must be integer 1\." },
        [pscustomobject]@{ Label = "core-pack-provider"; RelativePath = "config\reward-packs\core.json"; Before = '"providerId": "core"'; After = '"providerId": "other"'; Description = "core pack provider mutation"; Expected = "Package validation failed: reward pack 'core' must declare canonical providerId 'core'\." },
        [pscustomobject]@{ Label = "core-pack-version"; RelativePath = "config\reward-packs\core.json"; Before = '"packVersion": "0.3.3"'; After = '"packVersion": "0.3.4"'; Description = "core pack version mutation"; Expected = "Package validation failed: reward pack 'core' packVersion must be '0\.3\.3'\." },
        [pscustomobject]@{ Label = "anime-pack-version"; RelativePath = "config\reward-packs\krackasourus.anime-cards.json"; Before = '"packVersion": "1.5.2"'; After = '"packVersion": "1.5.3"'; Description = "Anime Cards pack version mutation"; Expected = "Package validation failed: reward pack 'krackasourus\.anime-cards' packVersion must be '1\.5\.2'\." },
        [pscustomobject]@{ Label = "pokemon-pack-version"; RelativePath = "config\reward-packs\krackasourus.pokemon-cards.json"; Before = '"packVersion": "1.1.2"'; After = '"packVersion": "1.1.3"'; Description = "Pokemon Cards pack version mutation"; Expected = "Package validation failed: reward pack 'krackasourus\.pokemon-cards' packVersion must be '1\.1\.2'\." },
        [pscustomobject]@{ Label = "yugioh-pack-version"; RelativePath = "config\reward-packs\krackasourus.yugioh-cards.json"; Before = '"packVersion": "0.1.2"'; After = '"packVersion": "0.1.3"'; Description = "Yu-Gi-Oh Cards pack version mutation"; Expected = "Package validation failed: reward pack 'krackasourus\.yugioh-cards' packVersion must be '0\.1\.2'\." },
        [pscustomobject]@{ Label = "sjx-pack-version"; RelativePath = "config\reward-packs\sjx.combat-chemistry.json"; Before = '"packVersion": "1.0.2"'; After = '"packVersion": "1.0.3"'; Description = "SJX pack version mutation"; Expected = "Package validation failed: reward pack 'sjx\.combat-chemistry' packVersion must be '1\.0\.2'\." },
        [pscustomobject]@{ Label = "cooler-stims-pack-version"; RelativePath = "config\reward-packs\vultify.cooler-stims.json"; Before = '"packVersion": "2.0.2"'; After = '"packVersion": "2.0.3"'; Description = "CoolerStims pack version mutation"; Expected = "Package validation failed: reward pack 'vultify\.cooler-stims' packVersion must be '2\.0\.2'\." }
    )) {
        $packRoot = New-TemporaryProjectClone $packCase.Label
        $temporaryRoots += $packRoot
        $packProject = Join-Path $packRoot "ContrabandCases"
        $packPath = Join-Path $packProject $packCase.RelativePath
        $packText = [IO.File]::ReadAllText($packPath)
        $mutatedPackText = $packText.Replace($packCase.Before, $packCase.After)
        Assert-Condition (![string]::Equals($packText, $mutatedPackText, [StringComparison]::Ordinal)) "$($packCase.Description) fixture did not change the pack."
        [IO.File]::WriteAllText($packPath, $mutatedPackText, (New-Object Text.UTF8Encoding($false)))
        Invoke-ExpectFailure (Join-Path $packProject "tools\Package.ps1") $packCase.Description @($packCase.Expected)
    }

    # Same-count recipe substitutions must fail even when every shallow schema/count check still passes.
    $recipeTamperRoot = New-TemporaryProjectClone "core-recipe-tamper"
    $temporaryRoots += $recipeTamperRoot
    $recipeTamperProject = Join-Path $recipeTamperRoot "ContrabandCases"
    $recipeTamperPath = Join-Path $recipeTamperProject "config\reward-packs\core.json"
    $recipeTamperText = [IO.File]::ReadAllText($recipeTamperPath)
    $mutatedRecipeTamperText = $recipeTamperText.Replace(
        '{ "kind": "preset", "presetId": "657120b36fe59548840cb542" }',
        '{ "kind": "preset", "presetId": "000000000000000000000000" }')
    Assert-Condition (![string]::Equals($recipeTamperText, $mutatedRecipeTamperText, [StringComparison]::Ordinal)) "core recipe-tamper fixture did not replace the night-patrol preset."
    [IO.File]::WriteAllText($recipeTamperPath, $mutatedRecipeTamperText, (New-Object Text.UTF8Encoding($false)))
    Invoke-ExpectFailure (Join-Path $recipeTamperProject "tools\Package.ps1") "same-count core recipe tamper" @("Package validation failed: reward pack 'core' SHA-256 '[0-9A-F]{64}' does not match accepted hash '335252043DF371B802BFD595D08E4C60803E126731A8CF782093C2BDBFD2D0F8'\.")

    # A canonical-revalidation failure must preserve every previously published canonical output.
    $publicationRoot = New-TemporaryProjectClone "publication-preservation"
    $temporaryRoots += $publicationRoot
    $publicationProject = Join-Path $publicationRoot "ContrabandCases"
    $publicationPackage = Join-Path $publicationProject "tools\Package.ps1"
    Assert-Condition ((Invoke-ProductionScript $publicationPackage) -eq 0) "clean publication-preservation setup failed."

    $publicationDist = Join-Path $publicationRoot "dist"
    $publicationStage = Join-Path $publicationDist "stage"
    $publicationArchive = Join-Path $publicationDist "ContrabandCases-0.4.8-SPT4.1.5.zip"
    $publicationHash = Join-Path $publicationDist "ContrabandCases-0.4.8-SPT4.1.5-SHA256.txt"
    $publishedStageSnapshot = Get-DirectoryByteSnapshot $publicationStage
    $publishedArchiveHash = (Get-FileHash -LiteralPath $publicationArchive -Algorithm SHA256).Hash
    $publishedManifestHash = (Get-FileHash -LiteralPath $publicationHash -Algorithm SHA256).Hash

    $publicationCorePath = Join-Path $publicationProject "config\reward-packs\core.json"
    $publicationCoreBytes = [IO.File]::ReadAllBytes($publicationCorePath)
    $publicationCoreTimestamp = (Get-Item -LiteralPath $publicationCorePath).LastWriteTimeUtc
    $publicationValidator = Join-Path $publicationProject "tools\Validate-Package.ps1"
    $publicationRealValidator = Join-Path $publicationProject "tools\Validate-Package.Real.ps1"
    Move-Item -LiteralPath $publicationValidator -Destination $publicationRealValidator
    $validatorWrapper = @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$StagePath,
    [Parameter(Mandatory = $true)][string]$ArchivePath,
    [Parameter(Mandatory = $true)][string]$HashPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$canonicalStagePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\dist\stage"))
$resolvedStagePath = [IO.Path]::GetFullPath($StagePath)
if ([string]::Equals($resolvedStagePath, $canonicalStagePath, [StringComparison]::OrdinalIgnoreCase)) {
    $corePackPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\config\reward-packs\core.json"))
    $corePackText = [IO.File]::ReadAllText($corePackPath)
    $mutatedCorePackText = $corePackText.Replace('"packVersion": "0.3.3"', '"packVersion": "0.3.4"')
    if ([string]::Equals($corePackText, $mutatedCorePackText, [StringComparison]::Ordinal)) {
        throw "Canonical-validation fixture could not mutate the core pack version."
    }
    [IO.File]::WriteAllText($corePackPath, $mutatedCorePackText, (New-Object Text.UTF8Encoding($false)))
}

& (Join-Path $PSScriptRoot "Validate-Package.Real.ps1") -StagePath $StagePath -ArchivePath $ArchivePath -HashPath $HashPath
'@
    [IO.File]::WriteAllText($publicationValidator, $validatorWrapper + "`n", (New-Object Text.UTF8Encoding($false)))

    Invoke-ExpectFailure $publicationPackage "canonical revalidation with existing canonical outputs" @("Package validation failed: staged artifact 'SPT_Runtime/user/mods/Wade-ContrabandCases/config/reward-packs/core\.json' differs from current release input")
    Assert-Condition ([string]::Equals((Get-DirectoryByteSnapshot $publicationStage), $publishedStageSnapshot, [StringComparison]::Ordinal)) "failed packaging changed the previously published stage tree."
    Assert-Condition ((Get-FileHash -LiteralPath $publicationArchive -Algorithm SHA256).Hash -eq $publishedArchiveHash) "failed packaging changed the previously published archive."
    Assert-Condition ((Get-FileHash -LiteralPath $publicationHash -Algorithm SHA256).Hash -eq $publishedManifestHash) "failed packaging changed the previously published checksum manifest."
    $publicationDebris = @(
        Get-ChildItem -LiteralPath $publicationDist -Force |
            Where-Object { $_.Name -like ".candidate-*" -or $_.Name -like ".previous-*" }
    )
    $publicationDebrisNames = @($publicationDebris | ForEach-Object { $_.Name }) -join ", "
    $publicationFailureOutput = (($script:LastProductionOutput -replace "\s+", " ").Trim())
    Assert-Condition ($publicationDebris.Count -eq 0) "failed packaging left candidate or rollback debris in the distribution directory: '$publicationDebrisNames'. Output: $publicationFailureOutput"

    [IO.File]::WriteAllBytes($publicationCorePath, $publicationCoreBytes)
    (Get-Item -LiteralPath $publicationCorePath).LastWriteTimeUtc = $publicationCoreTimestamp
    Assert-Condition ((Invoke-ProductionScript $publicationRealValidator) -eq 0) "preserved canonical outputs did not validate after restoring the source fixture."

    $optionalRequirementsRoot = New-TemporaryProjectClone "optional-pack-requirements"
    $temporaryRoots += $optionalRequirementsRoot
    $optionalRequirementsProject = Join-Path $optionalRequirementsRoot "ContrabandCases"
    $optionalRequirementsPath = Join-Path $optionalRequirementsProject "config\reward-packs\krackasourus.anime-cards.json"
    $optionalRequirementsPack = Get-Content -Raw $optionalRequirementsPath | ConvertFrom-Json -ErrorAction Stop
    $optionalRequirementsPack.requiredTemplateIds = @()
    $optionalRequirementsJson = $optionalRequirementsPack | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($optionalRequirementsPath, $optionalRequirementsJson + "`n", (New-Object Text.UTF8Encoding($false)))
    Invoke-ExpectFailure (Join-Path $optionalRequirementsProject "tools\Package.ps1") "optional pack without provider requirements" @("Package validation failed: reward pack 'krackasourus\.anime-cards' property 'requiredTemplateIds' contains 0 entries; expected 12\.")

    # Both accepted UnityFS artifacts are immutable release inputs, not merely format-compatible bundles.
    foreach ($bundleCase in @(
        [pscustomobject]@{ Label = "case-bundle-hash"; RelativePath = "bundles\contrabandcases\br12_case.bundle"; BundleKey = "contrabandcases/br12_case.bundle"; ExpectedHash = "086E3CED4C4FC379223F2C419CD8E64E4B0FA568A08373C33B471DF466834884"; Description = "case bundle hash mutation" },
        [pscustomobject]@{ Label = "key-bundle-hash"; RelativePath = "bundles\contrabandcases\br12_key.bundle"; BundleKey = "contrabandcases/br12_key.bundle"; ExpectedHash = "46D975BA9EE55D07722A4DFB1A1F1E322A1D3B1780AC925B41FD280C5DD77200"; Description = "key bundle hash mutation" }
    )) {
        $bundleRoot = New-TemporaryProjectClone $bundleCase.Label
        $temporaryRoots += $bundleRoot
        $bundleProject = Join-Path $bundleRoot "ContrabandCases"
        $bundlePath = Join-Path $bundleProject $bundleCase.RelativePath
        $bundleStream = [IO.File]::Open($bundlePath, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bundleStream.WriteByte(0x42)
        }
        finally {
            $bundleStream.Dispose()
        }
        Invoke-ExpectFailure (Join-Path $bundleProject "tools\Package.ps1") $bundleCase.Description @("Package validation failed: bundle '$([Text.RegularExpressions.Regex]::Escape($bundleCase.BundleKey))' SHA-256 '[0-9A-F]{64}' does not match accepted hash '$($bundleCase.ExpectedHash)'\.")
    }

    Write-Host "Package gate regressions passed."
}
finally {
    foreach ($root in $temporaryRoots) {
        $resolved = [IO.Path]::GetFullPath($root)
        $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if ($resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolved).StartsWith("contraband-package-gate-", [StringComparison]::Ordinal)) {
            if (Test-Path -LiteralPath $resolved -PathType Container) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        }
    }
}
