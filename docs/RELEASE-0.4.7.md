# Contraband Cases 0.4.7 — SPT 4.1.3

## Changes

- Broker stays hidden by default. Press **F5** in the main menu or stash to open it; rebind or clear the shortcut in MCM. The optional menu button is an advanced opt-in.
- Cleaner MCM sections and one-shot testing buttons, with clearer case/key selections and no replay of saved testing actions.
- Reopening a saved offer returns directly to the decision screen. It does not play a misleading repeat spin. Fresh openings and Relay outcomes still animate.
- Finalized case/key prices now refresh SPT's trader caches, fixing stale Therapist quotes without changing native trader rates.
- Cases can be found in verified crates, not generated on people. The combined added case loot weight defaults to 1%, split across available case types. It is not a flat drop chance. Keys remain separate finds and universal.
- Coordinated 0.4.7 client/server/shared versions and updated package validation for the crate setting and native MCM rendering dependency.

## Installation

For **SPT 4.1.3**. Close the game and server, then back up your existing mod and settings. Extract the install ZIP into the game folder containing `BepInEx` and `SPT_Runtime`, preserving the folder paths. Keep the server folder name `Wade-ContrabandCases`.

When upgrading, preserve your customized `SPT_Runtime/user/mods/Wade-ContrabandCases/config/config.jsonc` and BepInEx configuration. An older config without `caseLootWeightPercent` uses the new 1.0 default. Public defaults keep inventory testing grants disabled. Do not overwrite custom reward packs without comparing them first.

Restart the server and game. The Broker button is intentionally hidden, including when the old default-visible setting remains in an upgraded config. Use F5 or MCM's Open Dossier action.

## Verification and limits

1,509 automated tests passed in both the development workspace and curated source checkout. Clean Release build; package contents, assembly versions, Unity bundle hashes and archive integrity verified. Installed server reached ready state and registered crate-only case loot. Sound/model orientation were confirmed on the preceding build; the same bundles are retained.

Optional reward packs with incompatible or missing finalized templates are skipped safely. Automated/server checks do not replace live UI acceptance, and no crash-free guarantee is made.
