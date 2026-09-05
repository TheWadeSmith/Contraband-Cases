# Source build and publication scope

This repository contains the **0.4.6 test-candidate source** for SPT 4.1.3.
It is not an installable mod archive or a standalone Unity game project.

## Included

- Client, server and shared C# projects.
- Automated tests and authored compatibility fixtures.
- Default settings and reward-pack definitions, including optional-mod item references.
- Catalog audit, packaging and package-validation tools.
- Contraband Cases' Unity editor scripts, prefab definitions and materials.

Optional-mod references do not redistribute those mods or their artwork.
The MIT license covers the mod's own source; it grants no rights to external
game assemblies, SDK components, artwork or trademarks.

## Build and test the code

Use Windows, the .NET 10 SDK and your own compatible SPT 4.1.3 installation
with BepInEx. Dependencies declared in the project files restore from NuGet.
The client and tests reference installed game assemblies, which are not
included in this repository.

From the repository root, set these paths to your own installation:

```powershell
$gameManaged = 'D:/Your-SPT/EscapeFromTarkov_Data/Managed'
$bepInExCore = 'D:/Your-SPT/BepInEx/core'
dotnet build ContrabandCases.sln -c Release "-p:GameManagedPath=$gameManaged" "-p:BepInExCorePath=$bepInExCore"
dotnet test Tests/ContrabandCases.Tests.csproj -c Release "-p:GameManagedPath=$gameManaged" "-p:BepInExCorePath=$bepInExCore"
```

These commands restore dependencies. Use `--no-restore` only after a successful
restore. Build outputs stay under the ignored `bin` and `obj` directories;
the commands do not install files into SPT.

## Unity and packaging limitations

The tested local bundle pipeline uses **Unity 2022.3.43f1**, the compatible
EFT SDK, and **StandaloneWindows64**. The included `Unity/Assets/ContrabandCases`
directory is a source overlay for that environment, not a complete Unity project.
The SDK, its third-party tools and game-derived assets are deliberately omitted.

Models, textures, Blender files and compiled case/key bundles are also omitted
pending a separate redistribution/provenance check. Existing prefab/material
GUID references need the matching original assets and SDK to resolve. The
overlay alone cannot regenerate or display the models.

In the fully provisioned authoring environment,
`ContrabandCasesBundleBuilder.BuildBundles` performs the required EFT path-ID
replacement and validates both bundles. Generic Unity player builds are not
a substitute.

`tools/Package.ps1`, `tools/Validate-Package.ps1` and the package regression
require those matching bundles at `bundles/contrabandcases/`. They deliberately
reject missing or unrecognized bundles. A source-only checkout cannot pass
these packaging gates until the exact required assets are supplied.

Saves, profile journals, logs, diagnostic captures, local handoffs, credentials,
live settings and third-party binaries are excluded. The source `config`
directory contains defaults, not a copy of a live profile's configuration.

## Verification status

The complete development workspace passed **1,383 automated tests**, a clean
Release build and candidate/canonical package validation for 0.4.6. Its Unity
2022.3.43f1 case/key bundles are unchanged from 0.4.5. Earlier 0.4.6 adversarial
package checks passed before the final UI-only polish; packaging scripts did
not change afterward. Native Unity fixtures exercised the compiled UI, layouts
and cosmetic spins, not live Tarkov item previews or audible game sound.
Those local-workspace results are not evidence that the reduced public checkout
contains the omitted packaging assets.

The clean 0.4.6 public-source snapshot was independently restored, built and
tested against compatible installed SPT 4.1.3 assemblies: **1,383 passed, zero
failures or skips**. No game assemblies or build outputs are included in the
upload.

The confirmed lobby-relaunch rejection is fixed and regression-tested. The
original reported game shutdown has **not** been conclusively diagnosed.
In-game verification of previews, sound, model orientation, trader quotes,
spinner frame times and complete opening/recovery flows remains outstanding.
Do not treat this upload as a crash-free
release or as evidence that the user's installed mod was upgraded.
