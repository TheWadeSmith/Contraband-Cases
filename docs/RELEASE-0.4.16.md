# Contraband Cases 0.4.16 — Bigger jackpots, bounded delivery

For **SPT 4.1.5**, using the compatible 4.1.3 server SDK. Update the client and
server together. This release strengthens exceptional jackpots without flooding
the stash or changing previously earned rewards.

## Jackpots

- **Cash Cache:** 50 physical Bitcoins at **0.10%** (1 in 1,000 openings), up
  from 10. The smaller 4-Bitcoin payout remains at 0.90%. With native single-coin
  stacks, the big jackpot arrives in seven Messenger messages, at most eight
  coins per message. Saved older Bitcoin prizes retain their original amounts.
- **Operations — Quartermaster:** THICC item case, THICC weapon case, LEDX and
  injector case, plus **3,000,000 RUB**.
- **Black Site — Juggernaut:** Slick with class-6 plates, Rys-T helmet and a
  separately supplied compatible face shield, assembled advanced M4, 60 M995
  rounds and a THICC item case, plus **5,000,000 RUB**. Armor uses native stats;
  this is not invulnerability.
- **Black Site — More Cases Equipment Cabinet:** the modded storage cabinet
  plus **5,000,000 RUB**, when that integration is installed and valid.
- **Relics:** Erica Ultimate, Dragonite Holo and Tri-Horned Dragon jackpots
  retain four distinct collector cards, add their matching collection container,
  retain a THICC item case and include **3,000,000 RUB**. Each requires its
  matching card mod. Repetitive Red Rebel filler is removed from these new prizes.
- **Mixed:** inherits the selected themed prize and its full bonus.

These are six specific equipment jackpots, not a bonus on every Legendary.
Normal losses, near-even results and wins remain. Weights, chase caps, premium
surprise rates, universal keys and case/key loot settings are unchanged. The
offline all-mod comparison preserved equipment-case prices and exact normal and
premium conditional odds; installed hooks can affect your actual prices.

## Safe delivery and compatibility

Cash and gear are one committed prize, delivered through **Mechanic's Messenger**.
Claim attachments as space permits. Containers arrive **empty**, not as wrappers;
weapon and armor preset parts remain assembled. No direct stash insertion,
permanent stash expansion or profile migration is added.

Equipment jackpot limits: eight equipment roots, 128 equipment/attachment nodes,
64 total footprint cells and at most 32 legal RUB stacks. Delivery is split into
at most eight root items per message. Invalid modded stack limits fail validation
instead of generating item spam. Cash Cache retains its separate bounded rules.

All 514 previously published equipment definitions are retained, with six new
immutable successors (520 definitions total). The all-mod offline projection still
has 135 eligible fresh equipment prizes. Saved rewards and recovery records must
be preserved. New jackpot definitions are opening-only, not fresh Relay/Favor loot.

## Install

Download **ContrabandCases-0.4.16-SPT4.1.5.zip** and the accompanying
**ContrabandCases-0.4.16-SPT4.1.5-SHA256.txt**. GitHub's automatic Source code
archives are not installers.

1. Close the game, launcher and SPT server. Back up mod files, settings,
   `SPT_Runtime/user/profiles` and `SPT_Runtime/user/profileData` together.
2. Extract the install ZIP into the SPT game root, merging `BepInEx` and
   `SPT_Runtime`. **Skip the ZIP's `config/config.jsonc` if that file already
   exists.** Update matching client/server DLLs and the shipped reward-pack files.
   Keep the server folder named `Wade-ContrabandCases`.
3. Preserve custom reward packs and profile/delivery journals. Review personal
   edits to shipped packs before replacing them. Never delete journals to bypass
   a recovery block. Start the matched server/game yourself when ready.

Roll back only with a coherent backup of mod files, profiles and delivery state,
while game/server are closed. Older DLLs alone cannot safely undo recorded new prizes.

## Verification and caveats

The Release regression suite contains **1,823 passing tests**, including exact
odds, old-reward compatibility, client preview data, native stack limits, failed
journal/profile saves, partial Messenger collection and duplicate-claim protection.
Offline native and installed-mod source-data audits passed, with two independent
code reviews. Native-only audits correctly exclude unavailable optional packs.

No GitHub CI workflow is configured; these are local checks using compatible game
references and private test fixtures. Game assemblies, generated optional-mod
captures and editable artwork are excluded from the public source repository.
Unity models and sounds are unchanged.

**Live gameplay acceptance remains pending.** Managed/headless tests do not prove
native rendering, interaction or scene-transition behavior. On a backed-up profile,
check jackpot previews, Messenger delivery with a full stash, partial collection,
reconnect/retry, and older saved rewards before relying on the update in a main save.
This publication does not automatically install the update or start the server/game.
