# Contraband Cases 0.4.11 — Restored rewards and clearer values

For **SPT 4.1.5**. Update the client, server and shipped reward packs together.

## Changes

- Restores four optional reward integrations: Amonya Arcane Cache, ISB Aishi
  Elite Armory, Natalya Elite Armor and WTT Content Backport Elite Optics.
  Adds 13 corrected rewards while retaining all 26 historical definitions.
- Armor rewards use assembled presets with required parts. Natalya uses
  currently supported Level 6 equipment; Amonya tokens use legal stack sizes.
- Reward cards and details separate reference value from estimated trader
  resale. Estimates exclude Fence/profile bonuses and show unavailable when
  they cannot be calculated safely. They are not guaranteed sale quotes.
- Slightly increases default key/case loot weights from 2.0/1.0% to 2.2/1.1%.
  This is a 10% relative increase in added table weights, not a per-container
  probability. Cases remain crate-only; keys remain universal and find-only.
- Repairs the release validator and regression fixtures for the corrected
  packs, whole-number weights and deterministic package timestamps.

Million-rouble shipment balancing, 5 kg case weights, rare premium openings,
Bitcoin jackpot odds, MCM controls and accepted Unity bundles are retained.
Restored packs can change automatic case prices and package probabilities.
No backpack/container bundling is included in this release.

## Saved rewards

Already-paid rewards are never resized or silently replaced. Valid historical
rewards remain recoverable. Missing or invalid historical content stays blocked,
with separate diagnostics under `UnavailableRetiredLots` in the catalog report.
Corrected rewards use separate Relay tracks and cannot substitute for old stakes.

## Install

Download `ContrabandCases-0.4.11-SPT4.1.5.zip` and its matching SHA256 manifest.
GitHub's automatic source archive is not the installer.

1. Close the game, launcher and server; back up your mod and saves.
2. Extract the install ZIP into the SPT game root. Keep the server folder named
   `SPT_Runtime/user/mods/Wade-ContrabandCases`.
3. Replace both client/server DLL sets and the shipped reward-pack files.
   Preserve custom packs and settings; back up edits to shipped packs first.
4. To adopt the slightly higher loot defaults in an existing config, set
   `keyLootWeightPercent` to `2.2` and `caseLootWeightPercent` to `1.1`.
   Unrelated settings, including testing-grant preferences, need not change.
5. Restart and check the Broker's current prices, content and published odds.

## Verification scope

1,575 automated tests pass with a clean Release build. Native cargo/cash audits
and real client-parser checks pass. Offline installed-definition fixtures cover
all 13 corrected rewards. The package validator checks both DLL sets, exact pack
hashes, Unity bundles, archive contents and checksums.

The full adversarial packaging regression also passes, including input races,
malformed/tampered packs, bundle changes and rollback preservation. The curated
public source passes the same 1,575 tests with locally generated prerequisites.

Full in-game opening/claim, layout and trader-quote acceptance is still required.
Automated tests do not guarantee crash-free gameplay. There is no GitHub CI
workflow; verification is performed in the provisioned local development setup.
Private captures, profiles, game binaries and editable licensed art are excluded.
