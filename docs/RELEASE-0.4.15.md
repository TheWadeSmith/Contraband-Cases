# Contraband Cases 0.4.15 — Transition guards and opening recovery

For **SPT 4.1.5**, using the compatible 4.1.3 server SDK. Update the client and
server together. This patch improves recovery around lobby transitions, failed
openings and slow Messenger notifications.

## Changes

- Defers Relay UI discovery until a real lobby is available and no active
  GameWorld exists. Leaving that lobby invalidates earlier discovery work.
- Retries failed openings with a fresh request and current-catalog validation,
  while guarding against duplicate submission and stale responses.
- Makes **Close — resume later** and Escape available on recovery screens.
  Closing retains observation of pending requests, so their eventual saved
  results remain recoverable.
- Limits Messenger live notification work to **two seconds total**. Slow or
  failed notifications no longer hold the opening flow indefinitely. Notification
  snapshots are isolated, late failures are observed, and delivery keeps its
  saved records and duplicate-payout protection.

Reward definitions, economy, prices/odds rules, case/key settings, item content,
models and sounds are unchanged from 0.4.14. Existing saved rewards retain their
contents. This release does not add a profile migration.

## Install

Download `ContrabandCases-0.4.15-SPT4.1.5.zip` and
`ContrabandCases-0.4.15-SPT4.1.5-SHA256.txt` from this release.
GitHub's automatic **Source code** archives are not installers.

1. Close the game, launcher and SPT server. Back up the mod files, settings,
   `SPT_Runtime/user/profiles` and `SPT_Runtime/user/profileData` together.
2. Extract the install ZIP into the SPT game root, merging `BepInEx` and
   `SPT_Runtime`, but **skip the ZIP's `config/config.jsonc` if that file already
   exists**. Do not replace custom reward packs. Update both client/server DLL
   sets and the shipped reward-pack files. Keep the server folder named
   `Wade-ContrabandCases`.
3. Preserve your existing `config/config.jsonc`, custom reward packs and
   profile/delivery journals. Review any personal edits to shipped packs before
   replacing those files. Never delete journals to bypass a recovery block.
4. When ready, start the matched server/game yourself and run the checks below
   on a backed-up profile before relying on the update in your main save.

Roll back only with a coherent matching backup of mod files, profiles and
delivery state while the game/server are closed. Replacing DLLs alone does not
undo recorded progress; do not mix old journals with newer profiles.

## Verification and remaining acceptance

The matched Release build completed with **zero warnings and zero errors**.
The full suite passed **1,786 automated tests** in both the provisioned development
workspace and the public-source checkout supplied with compatible local references
and private test fixtures, with no failures or skipped tests. This includes **nine
transition-guard tests** and **thirteen opening-stall regressions**. C# and final
code reviews approved the candidate.

The install archive passed candidate, canonical and standalone package validation.
The full package-gate regression suite also passed, including corruption rejection,
concurrent-run protection and preservation of existing outputs after a failed package.
The archive and checksum file are the verified release outputs; generated private
fixtures are not included. These are local checks; no GitHub CI workflow is
configured.

Headless tests use test-only substitutions for native Unity behavior. They
exercise managed recovery logic but do not establish native keyboard focus,
Escape handling, scene transitions or gameplay behavior. The earlier reported
transition failure has no confirmed live cause. This patch is not a claim that
the original game shutdown or restart failure has been conclusively resolved.

**Live gameplay acceptance remains pending.** No server/game startup or live
installation was performed for these changes. On a backed-up profile, check:

- Lobby/raid transitions and game close/relaunch with fresh logs.
- A failed opening followed by retry, a changed catalog, duplicate clicks, and
  Close — resume later / Escape while recovery is pending; verify the same
  committed result resumes without another spend or reward.
- Delayed/failed Messenger live notifications, full-stash delivery, partial
  collection and reconnect; verify the saved reward is delivered only once.
- Existing Relay sidegrades, normal/Epic/Legendary/Cash openings, native focus,
  sounds and layouts with the matching client/server build.

The accepted Unity bundles are unchanged. Game assemblies, optional-mod
captures and editable licensed artwork are excluded from the source repository.
