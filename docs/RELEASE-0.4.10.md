# Contraband Cases 0.4.10 — Million-Rouble Rebalance

For **SPT 4.1.5**. Update the client and server together.

## All five cases rebalanced

New openings now award real larger shipments, with automatic purchase prices
around one million roubles. Native item prices are not inflated to create value.
Cases keep their distinct themes, collectible cards and installed-mod rewards.

| Case | Indicative purchase price |
| --- | ---: |
| Mixed / Relay | ₽1,043,000 |
| Operations | ₽1,098,000 |
| Relics | ₽1,023,000 |
| Black Site | ₽1,204,000 |
| Cash Cache | ₽1,018,000 |

Cargo prices are projections using captured finalized mod-item values; Cash is
from an offline SPT 4.1.5 database audit. Actual prices depend on loaded content,
currency quotes and configuration. Reference reward values are not guaranteed
trader resale proceeds. Losses, near-even outcomes and valuable wins remain.

## Rewards and rarity

- 117 new authored cargo shipments; 122 original definitions retained for recovery.
- Cargo shipments contain 2–6 complete original packages; relic/collection
  shipments contain eight. Contents can include duplicate items or cards; the
  package lists the actual quantities. Native inventory safety limits remain.
- New cargo rarity thresholds: Uncommon ₽240k, Rare ₽450k, Epic ₽900k and
  Legendary ₽1.8m. Historical paid rewards keep their original grades.
- Surprise Epic/Legendary chances remain unchanged. Named chase rewards remain
  rare, with the combined 1-in-400 cap per selected group when ordinary
  alternatives exist. Shipment value outliers enter the chase pool at ₽4.5m.

## Cash Cache

| Payout | Chance |
| --- | ---: |
| ₽200,000 | 15% |
| ₽480,000 | 13% |
| ₽800,000 | 10% |
| ₽1,050,000 | 17% |
| ₽1,400,000 | 9% |
| ₽1,760,000 | 10% |
| ₽2,400,000 | 3% |
| US $8,000 | 7% |
| US $12,000 | 6% |
| €8,000 | 4% |
| €16,000 | 1% |
| 80 GP Coins | 3% |
| 200 GP Coins | 1% |
| 4 physical Bitcoins | 0.90% |
| 10 physical Bitcoins | 0.10% |

One payout per opening; no Cash Relay or Favor. Ordinary payouts determine entry
price, so Bitcoin jackpots cannot inflate it. Native stack limits and the Bitcoin
outlier guard remain. Cash's offline reference-value split is 41% meaningful loss,
24% near-even and 35% win, including 25k/65k key-cost scenarios. Near-even means
within 10% of total reference cost; these are diagnostics, not forced quotas.

## Safe upgrades and unchanged features

- Already-paid cargo and cash payouts retain their exact contents. Saved cash
  payouts survive quote changes and remain retryable after a full stash.
- Historical and new-generation Relay candidates cannot cross.
- Keys remain universal, single-use and find-only. Key and case drop rates are
  unchanged; case drops remain restricted to eligible crates, not people.
- All five cases remain 5 kg. Existing Unity models, orientation, sound bundles,
  roulette, Broker shortcut and MCM controls are unchanged.
- Default Therapist case resale uses the existing native coefficient, not full
  purchase value. Custom fixed-price/sell-target settings remain explicit overrides.

## Install

Download `ContrabandCases-0.4.10-SPT4.1.5.zip` and the matching SHA256 manifest.
Do not use GitHub's automatic Source code ZIP as an installer.

1. Close Tarkov, the launcher and SPT server. Back up the profile and existing mod.
2. Extract the installable ZIP into the SPT game root, merging its `BepInEx` and
   `SPT_Runtime` folders. Keep the server mod named `Wade-ContrabandCases`.
3. Update both DLL sets **and the shipped reward-pack JSON files**. Preserve live
   configuration and unrelated/custom mods. Back up edits to shipped packs and
   reapply them carefully; leaving old pack files prevents the new shipments.
4. Restart server and game. Check Broker status, current prices and published odds.

Unopened cases use the new table. Openings paid before the update retain their
original rewards. No profile reset or Unity rebuild is required.

## Verification and limitations

**1,554 tests passed**, clean Release build, native cargo/cash audits, client
library/odds contract checks and staged/archive package validation. The native
audit resolved 106 cargo definitions (53 current shipments), with maximum current
shipment size 64 roots, 198 total item nodes and 192 stash cells. The captured
mod-value projection retained 104 current shipments and qualified Epic/Legendary
pools for all four cargo themes.

No GitHub CI is configured; these are local results. Full in-game acceptance and
modded template/placement validation remain pending. Existing optional-pack
compatibility warnings are not repaired by this economy update; invalid packs
still fail closed. No claim of crash-free gameplay is made.

Editable licensed artwork and game/SDK binaries are not distributed in the source
repository. Accepted models/textures remain only in compiled release bundles.
