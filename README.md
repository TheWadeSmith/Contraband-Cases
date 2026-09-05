# Contraband Cases

Contraband Cases **0.4.6** is a client-and-server mod for **SPT 4.1.3**.

**This is a source-only test candidate, not an installable release.** See
[source build and publication scope](docs/SOURCE-BUILD.md) for prerequisites,
omitted Unity/art assets and verification evidence. Live preview images, sound,
model orientation and the original reported crash still need in-game acceptance.

0.4.6 refreshes the frontend: case-specific overviews, readable odds with their
probability context, three side-by-side premium cards, complete package contents,
consistent Save & Close, and a menu-only Broker reward browser/Dossier/status page.
It adds clear theme/tier testing selectors, mechanical sound cues and Test Sound,
keyboard focus/scrolling, explicit missing-art states, and interrupted-hold safety.
Rarity labels use plain names: Common, Uncommon, Rare, Epic and Legendary.
Premium cards show compact contents and purpose; Details retains full names,
quantities and source. Reference values sit beside their valuation caveat.
The browser offers direct case/category selection, item/mod search, and two rows
of cards (four at smaller resolutions, six on larger screens). Search with Enter
or Search; opening details preserves the selected filters, search and page.
The reel uses small edge pointers, varied starting cards and smooth seeded
acceleration/deceleration, plus the existing duration/landing variation. These
are cosmetic differences, not changed odds or manufactured near misses.
Off-screen cards no longer receive per-frame shimmer updates. Reduced motion
and Skip still lead to the same saved result. No frame-rate settings are changed.
Prices, key supply, jackpot chances, reward packs and settlement rules are unchanged.

0.4.5 adds exceptionally rare surprise Epic and Legendary openings, with three
saved premium packages to browse and choose from. It also corrects case/key
orientation and duplicate key lettering, publishes finalized flea prices,
enables Therapist case resale by default, repairs menu-audio routing, and
allows safe lobby relaunch after a disconnected game. The original reported
game shutdown has not been conclusively diagnosed; this is not a crash-free guarantee.
Open a BR-12 case, consider up to three offers from its loot pool, and either secure
your chosen package or risk it on Relay. The roulette presents an already
saved server result; it never chooses loot. Only in-game items and roubles
are involved. There are no real-money purchases.

The new **Cash Cache** is a separate one-payout contract: RUB, USD, EUR or
physical Bitcoin. It uses the same universal key and roulette, with no
discard decision, Relay attempt or Broker Favor change.

This is a **test candidate**, not a claim of complete in-game validation.
Automated tests, the offline catalog audit and Unity bundle checks are distinct
from running the installed mod in Tarkov.

## Play loop

1. Buy a **BR-12 case from Mechanic LL1**. Find a **BR-12 Relay Key** in
   raids. Keys are not sold by traders.
2. In your stash, right-click the case and choose **Unpack**. Read the server's
   current category and lot odds before confirming.
3. A normal opening spends **one case and one key**, saves three offers, and reveals
   the first. **Choose This Package** locks that package for the next decision.
   **Discard & Reveal Next** permanently gives it up.
4. You may discard twice. The third offer is automatically locked.
5. **Claim Items** grants the entire locked package without another key.
   **Risk on Relay — 1 Key** stakes that package plus an additional key on Relay.
   Hold the risk button to confirm.

Reward packages include complete weapon presets with ammunition, hearing and
field gear, surgery and trauma supplies, ordnance, ammunition reserves, and
curated optional-mod items such as cards, injectors, armor and attachments.
Items are not automatically pulled from every installed mod.

## Case types

One **universal, single-use BR-12 Relay Key** opens any case and also pays for
each Relay attempt. There are no case-specific keys.

| Case | Contents | Inventory color |
| --- | --- | --- |
| BR-12 Relay Case | Mixed catalog, including all available themed content | Existing case color |
| Operations | Practical ammunition, medical supplies, field equipment and loadouts | Green |
| Relics | Anime, Pokemon and Yu-Gi-Oh cards, historical relics and arcane curios | Violet |
| Black Site | Elite equipment, night operations, ordnance and experimental supplies | Red |
| Cash Cache | One exact stack payout of RUB, USD, EUR or physical Bitcoin | Yellow |

Cases are separate physical items sold by Mechanic, with distinct names,
descriptions, loot tables, published odds and prices. They reuse the same 3D
crate model; inventory colors are not new painted textures. Each opens into
the same offer-card/roulette interface. For cargo cases, Relay stays within the case pool and
the chosen lot's original progression track.

Normal Relics openings draw three distinct collection/relic tracks, rather than forcing cards
into Weapons/Equipment/Supplies. Black Site now also draws three specialist
tracks, so it does not guarantee a strong weapons category every opening.
A cargo case without three eligible groups is not
sold or opened; it never fills missing groups with unrelated loot. Availability
therefore depends on which supported packs resolve against the installed mods.

## Rare surprise openings

Mixed, Operations, Relics and Black Site cases look ordinary until opened.
The server saves one surprise-tier roll and all three packages **before spending
the case/key**. Cash Cache is excluded.

| Opening | Chance | What you receive |
| --- | ---: | --- |
| Normal | 98.3% | The existing discard/lock gamble, including ordinary jackpots |
| Epic | 1.5% | Three distinct Epic-or-Legendary packages; browse all, choose **one** |
| Legendary / Godly | 0.2% | Three distinct Legendary packages; browse all, choose **one** |

These are rare upgrades to the opening, not separate purchasable guaranteed-win
cases. No extra key is charged for browsing or choosing. After choosing, Claim
Items and eligible Relay actions work normally. **Save & Close** leaves the
same choices available through the Dossier. Reconnecting cannot reroll them.
Premium packages keep the case theme, but can share a category or progression
track. Choosing one never grants all three.

Premium eligibility also checks reference value: Epic requires at least the
greater of 200,000 RUB or 1.15 times `(case price + 25,000)`; Legendary requires
at least the greater of 300,000 RUB or 1.6 times that cost. The 25,000 is a key
scarcity allowance, not a new fee or key sale price. Reference value does not
guarantee a particular trader cash-out. At least three non-chase packages must
qualify. If a tier is unavailable, its probability becomes Normal **before the
roll**; the confirmation shows its zero rate. Missing saved content blocks a
choice rather than silently replacing a promised reward.

Extreme chase packages retain their combined 1-in-400 cap per premium draw.
Packages are drawn without replacement, recalculating weights after each draw.
Full odds publish both tier rates and complete first-draw premium probabilities.

With the captured installed-mod catalog, base prices remain Mixed **157,000**,
Operations **167,000**, Relics **106,000**, Black Site **180,000** RUB; Cash Cache's
previous baseline is **128,000**. Live prices depend on resolved content/config.
All case templates are accepted by Therapist by default. Native resale is
approximately 63% of the finalized handbook value before other modifiers;
the existing explicit `therapistSellPriceCase` override remains Mixed-only.

## Cash Cache

Buy a BR-12 Cash Cache from **Mechanic LL1**, then Unpack it in your stash.
Review the exact odds and confirm to spend **one cache and one universal key**.
The server commits one payout before the roulette begins. **Collect Payout**
grants that exact amount without another key. A full stash leaves the original
payout saved for retry. Closing, skipping or reconnecting cannot reroll it.

The versioned `cash-v1` table is fixed and does not use cargo chase reweighting:

| Payout | Chance per opening |
| --- | ---: |
| ₽25,000 | 15% |
| ₽60,000 | 16% |
| ₽100,000 | 10% |
| ₽150,000 | 17% |
| ₽175,000 | 10% |
| ₽220,000 | 10% |
| ₽300,000 | 3% |
| US $1,000 | 7% |
| US $1,500 | 6% |
| €1,000 | 4% |
| €2,000 | 1% |
| 1 physical Bitcoin | 0.90% |
| 2 physical Bitcoins | 0.10% |

Bitcoins are ordinary in-game items, not a real wallet, cryptocurrency exchange
or fractional crypto balance. Two Bitcoins are separate items when native stack
rules require it. No other crypto or arbitrary modded currency is admitted.

Price is the weighted mean of **non-Bitcoin payouts**, rounded up to ₽1,000.
USD/EUR use published direct rouble purchase offers; Bitcoin uses an estimated
standard LL1 Therapist sale value from the finalized handbook and buy rules.
Foreign-currency purchase value is **not** guaranteed rouble liquidation value;
player-specific sale modifiers are not simulated. Price and amounts remain
fixed for that catalog session. Cash ignores the Mixed case fixed-price override.

The base-database audit gives **₽130,000 + one key** and an expected reference
payout of about **₽138,599**, not expected cash-sale proceeds. Defining near-even
as within 10% of case plus assumed key cost:

| Assumed key cost | Meaningful loss | Near-even | Win |
| --- | ---: | ---: | ---: |
| Found key, ₽0 financial cost | 41% | 7% | 52% |
| ₽25,000 opportunity cost | 48% | 21% | 31% |
| ₽65,000 opportunity cost | 79% | 6% | 15% |

These are candidate estimates, not installed-mod or raid-cadence validation.
A found key still has time and opportunity cost. Key scarcity can make this a
poor-value opening even when its sticker price looks fair. There is no loss
quota, personalized probability, forced win or disguised reward substitution.

Bitcoin cannot inflate entry price. If its weighted payout contribution exceeds
10% of the ordinary mean, or required quotes become invalid, **new Cash openings
and sales stop**. Structurally valid already-paid payouts remain collectible,
even without a conversion estimate. Saved stack layouts remain unchanged when
native stack caps increase; an unsafe decrease blocks the invalid payout until
the required content is restored. Currency support does not relax cargo exclusions.

Cash uses authored payout tiers, not the cargo value-to-rarity ladder below.
Tier color is a presentation label, not a guarantee of profit. Current startup
prices, exact odds and key-cost scenarios are written to **cash-catalog-report.json**
beside the server DLL. Cash gallery previews always show the collection layout.

## What Relay means

Relay exchanges an unclaimed package for a chance at a higher rarity within
the same progression track. It is optional; securing the current reward is
always the safe choice when the server permits Claim.

| Stage | Upgrade | Same-rarity replacement | Lose the lot |
| ---: | ---: | ---: | ---: |
| 1 | 55% | 30% | 15% |
| 2 | 45% | 25% | 30% |
| 3 | 35% | 20% | 45% |

An upgrade advances one rarity and may allow another Relay. A replacement
ends the chain with a different claimable package of the same rarity; it need
not have the same value. A loss consumes the stake and key and grants no items.
Stage 3 and Legendary rewards end the chain. Incomplete target pools disable
Relay without preventing a valid Claim.

**Broker Favor:** each modern Manifest loss adds **one**, regardless of stage.
At **3/3**, the next eligible Relay is a guaranteed upgrade and then resets
Favor to zero. It still costs a key. Favor persists between cases.
Claim and Forfeit do not change it. Older physical-weapon Relay saves retain
their historical stage-depth recovery rule.

Cargo rarity is derived from the complete package's reference value, not authored
as a reward-pack override:

| Mark | Rarity | Reference-value band |
| --- | --- | ---: |
| I | Common | below ₽40,000 |
| II | Uncommon | ₽40,000–74,999 |
| III | Rare | ₽75,000–149,999 |
| IV | Epic | ₽150,000–299,999 |
| V | Legendary | ₽300,000 and above |

These are item reference values, **not cash payouts or trader resale quotes**.

## Odds, clues and presentation

Mixed and Operations use three broad categories:
**Weapons, Equipment and Supplies**. Relics and Black Site use their respective
collection/relic and specialist tracks.
Categories are drawn uniformly without replacement, then a provider is weighted
within its category, then a lot within its provider. Adding a narrowly themed
pack does not give it an extra category-sized share of all openings.

The confirmation screen publishes current server odds and offers a full audit
view. Next-offer category signals help the keep/discard decision, but exact
future lots remain secret. The client checks the catalog again before opening;
a change requires fresh confirmation.

Normal opening reels use complete lots from the published catalog. Card
frequency and neighboring cards are decorative and **are not the published
odds**. The center-line landing always matches the saved server reward.
Recovery and Relay can use a disclosed-contents/sealed-card fallback reel.

Decision screens show a hero image, rarity name and rank, contents thumbnails,
and an optional **Contents & Details** view with the full item list and Relay
disclosure. Artwork falls back from the main item to a real contents image,
then to a built-in rarity seal. Missing artwork does not alter rewards.

## Prices and keys

Shipped defaults:

- All five case prices: automatic, stock **5** per case type.
- No fixed Mixed price or Therapist case-buyback override by default.
- Keys: **find-only**; no default key buyback override.
- Key injection weight: **2% of existing pool weight** in eligible static
  containers and bot pools that already contain vanilla key items. This is
  **not a flat 2% probability per container or raid** (about 1.96% per
  independent weighted selection before downstream loot rules).

Existing live settings are not automatically reset by source changes. If
`fixedCasePrice` is null, cargo cases use 95% of the lesser of mean and median
reward **reference use value** under optimal sequential keep/discard play,
minus a **₽25,000 opening-key allowance**, rounded up to ₽1,000 with a ₽1,000
minimum for inexpensive custom catalogs. This reserves value for a raid-earned key and
prevents a rare jackpot from inflating the case above normal rewards. It is
not a cash-return target, a full valuation of your play time, or a profit promise.
Opening price does not charge for optional Relay/Favor. Those are reported
separately, including every key consumed. Prices are
calculated at startup, published before opening, and bound to the case's
catalog identity. They are reference-value prices, not promised trader return
or Relay-profit targets. Existing fixed-price settings remain explicit overrides.

The gameplay target is one retained key per **1.5–2 normal looting raids** on
average, including zero-key raids, leaving room for occasional Relay spending.
The initial 2% weight remains unchanged pending actual supply measurements;
it has **not** been shown to meet that target. PMC and scav supply must be
evaluated separately; the current logger covers PMC only.

For illustration, 25 eligible
independent item selections per raid with 75% of generated keys retained would
average 0.368 keys/raid (one per 2.72 raids) at 2%, versus 0.075 keys/raid
(one per 13.39 raids) at 0.4%. Real looting and other mods can differ greatly.
The server now logs `Key raid audit: PMC completed` with newly retained key
instance counts after normal PMC raid settlement. Carried keys and duplicate
end events are excluded; scav/transit runs and mail/BTR deliveries are not
counted. Testing-grant enablement is labeled. This logging never adjusts loot.

### Gameplay balance candidate

The captured-mod projection (not live sale quotes) currently gives automatic
prices of **Mixed ₽157,000; Operations ₽167,000; Relics ₽106,000; Black Site
₽180,000**. These move with the resolved catalog; the proposed starting prices
are not forced overrides. The report compares optimal reference-value play,
keeping the first offer, and keeping the first offer worth case price + ₽25,000.
Taking the first offer has a substantially lower average return; three offers
are a keep/discard decision, not three rewards or a guaranteed best-of-three.

Five component-only Eco kits are no longer new-opening or fresh-Relay rewards.
Their exact definitions remain available for recovery. New Precision Armorer
and Field Armorer kits bundle the existing parts without adding dependencies.
Six append-only core caches add multi-raid medical, ammunition, breaching and
specialist supplies, including a Black Site Expedition Jackpot. No existing
item prices or historical reward recipes were rewritten.

An expensive label is not proof of utility or cash value. For example, the
historical WTT Field Medical Kit contains an analyzer and iodide tablets, not
ordinary first-aid supplies. Its high reference value is treated as a chase;
actual usability and trader sale quotes remain part of in-game acceptance.

### Rare chase rewards

Opening-only chases include Black Site Marksman, Erica Ultimate, Dragonite
Holo, Tri-Horned Dragon, Vault Twin Rifles and Precision Assault. Packages
worth at least ₽750,000 reference value also join that pool, protecting against
modded price outliers such as Condor Loadout and Field Medical Kit.

Together they receive **at most 0.25% of each selected group's package odds**
when ordinary alternatives exist. Already-rarer weights are not increased.
This is not a 0.25% chance to claim a jackpot from the whole opening. The odds
table publishes each actual conditional chance; reveal/claim odds also depend
on group inclusion and your discard decisions. A chase-only custom group
publishes its real distribution rather than fabricating ordinary filler.
Ordinary Epic/Legendary packages remain available as regular wins.

Fresh Relay/Favor pools exclude opening-only chases. Existing exact frozen
rewards remain valid; an older frozen upgrade can retain a necessary legacy
continuation after a catalog change. No individual win/loss quotas or personalized
odds are applied. Missing preview catalogs use neutral seals, and Relay result
headers no longer announce an ordinary outcome before the roulette lands.

At startup the mod writes **catalog-report.json** alongside the server DLL.
It reports resolved/skipped packs, lots, rarity distribution, stage-specific
Relay eligibility, and finite-horizon keep/discard/Relay/Favor comparisons
under several key-cost assumptions. Break-even figures are reference-value
ceilings, not sale-price recommendations or guaranteed profit.
Its `ThemedCases` section also lists each resolved case's price, availability,
groups, lot count, first-Relay coverage and complete opening odds.

The 2026-09-05 offline base-game audit resolves **53 lots** (50 core plus
3 Vault), with **100% of non-Legendary lots eligible for a first Relay step**.
Installed mod hooks may change values and availability; the finalized live
report is required to evaluate that catalog.

## Included integrations

All **17 packs / 122 authored lots** ship together. Unavailable optional packs
are skipped with a reason instead of generating invalid rewards.

- Required Core: 50 lots, including 32 progression-support packages and six
  gameplay-balance caches.
- Vanilla Vault: 3 high-value lots.
- Krackasourus Anime, Pokemon and Yu-Gi-Oh Cards: collection packages.
- SJX Combat Chemistry and Vultify CoolerStims: injector packages.
- ISB/Aishi: field and elite armory packs.
- Natalya: field gear and elite armor packs.
- WTT ContentBackport: field resupply and elite optics packs.
- Eco Attachment Emporium: field cache and elite optics packs.
- Amonya: arcane cache.
- Eco WW2: relic cache.

Each pack uses verified, explicit template IDs and must pass complete
dependency, forest, resource-state and placement validation. Optional packs
are not promises that every item is Relay-eligible at every installed price.
The gallery and live report show what actually resolved.

The original 12 core lot definitions and `core.json` pack version **0.3.3**
remain unchanged. New lots are append-only. Old saved rarity ladders retain
their original meanings.

## MCM / configuration menu

In the BepInEx configuration UI / compatible MCM:

- **Presentation → Effects Volume:** controls reveal and outcome effects.
- **Presentation → Reduced Motion:** skips motion-heavy reveal effects.
- **Broker Dossier → Open Dossier:** view current Favor and up to 50 recent
  settled Manifest receipts through the Broker home. Choose **Resume Saved Reward**
  to reopen an unfinished Manifest, including after using your last case/key.
  Testing Mode is not required. The button checks authenticated saved state;
  it cannot create a fresh opening or reroll a reward. If a transaction was
  already prepared, its existing recovery runs with the original IDs.
- **Broker Dossier → Show Broker Button:** shows a menu-only entry near the top
  right. The same Broker home includes case/category reward filters, full package
  inspection, source labels, compatible/skipped packs and **Status & Sound → Test Sound**.
  Browse previews are public catalog examples, not upcoming saved rewards.
- **Keyboard:** Tab/Shift+Tab or left/right cycles Broker controls and text areas.
  Select or hover a text area, then use Page Up/Down or Home/End to scroll it.
  Hold confirmation requires a fresh primary click or Submit press. Focus loss,
  deselection and disabling cancel an interrupted hold.
- **Testing (Catalog Gallery) → Open Gallery:** requires Testing Mode.
  Browse actual resolved lots by ID and rarity; choose offer, claim-ready,
  upgrade, replacement, confiscated, guaranteed-upgrade or secured layouts.
- **Force Missing Artwork** and **Long Name Stress Test:** gallery-only
  checks. They never modify items or influence real selection.

Gallery buttons only navigate/close. It cannot send economic actions.
The Dossier remains read-only until you explicitly choose to leave it and resume
your pending Manifest. An empty ledger reports that nothing is pending.
Opening toggles reset after one request.
Select **CashCache** under the inventory testing grant's **Case Theme** to request
real test caches; this does not force a particular payout. Cash gallery previews
do not simulate Relay, confiscation or discard layouts that Cash cannot enter.

Inventory testing grants remain separate: enable both the client's Testing
Mode and the server's `testingInventoryGrantsEnabled` on a test profile.
The shipped server setting is **false**. Forced testing pools restrict matching
providers within a category; categories absent from that pool still use normal
selection. They are not guaranteed exclusive themed contracts.

In **Selection Mode = CaseAndTier**, select a **Case Theme** and an **Opening Tier**
(Natural, Epic or Legendary), then set case/key quantities and grant once. Cash
accepts Natural only. **LegacyProviderPool** preserves the older Forced Crate Pool
selector, including **EpicMixed / LegendaryMixed** and Epic/Legendary variants
for Operations, Relics and Black Site. These force the
surprise tier for newly granted test cases; they do not affect purchased cases.
Open them before restarting the server: unopened testing tags are process-local
and bounded to 256 entries. After an opening is saved, its tier and choices
persist normally. An unavailable forced tier gives a clear error and consumes
neither case nor key. The older Vault/Mega provider filters are **not** God cases.

## Recovery and save safety

All economic operations are server-authoritative and use durable profile
journals, exact frozen item definitions and transaction witnesses.

- Closing or skipping an animation does not reroll or undo the result.
- Reopening a case resumes an unfinished Manifest before opening another.
- A full stash/sorting table leaves the whole entitlement owed; make space and
  retry Claim. No partial substitute package is granted.
- Missing content blocks unsafe actions. Restore the exact reward pack to
  resume. **Forfeit permanently abandons the entitlement without reward.**
- Ordinary missing-key rejection is friendly and happens before input
  consumption. Unexpected failures still remain visible in logs.
- Never delete journals or alter profile inventory to bypass a recovery block.

Back up profiles before testing an upgrade. Downgrading after using new content
may require restoring the matching backup, not merely replacing DLLs.

## Install and build

Close Tarkov and the SPT server before updating. Back up the profile, existing
client/server mod files and live configuration. Install matching client and
server assemblies together. Preserve the existing server folder name:

```text
BepInEx/plugins/ContrabandCases/
  ContrabandCases.Client.dll
  ContrabandCases.Shared.dll
SPT_Runtime/user/mods/Wade-ContrabandCases/
  ContrabandCases.Server.dll
  ContrabandCases.Shared.dll
  config/reward-packs/
  bundles/
  bundles.json
```

Do not overwrite a customized live `config/config.jsonc` with defaults.
Preserve custom reward packs too. Remove no other mods.

Source build/test prerequisites and installation-path overrides are documented
in [SOURCE-BUILD.md](docs/SOURCE-BUILD.md). In the fully provisioned development
workspace, after restoring dependencies:

```powershell
dotnet test Tests/ContrabandCases.Tests.csproj -c Release --no-restore -v minimal
dotnet build ContrabandCases.sln -c Release --no-restore -v minimal
pwsh -NoProfile -File tools/Package.ps1
pwsh -NoProfile -File tools/Validate-Package.ps1
pwsh -NoProfile -File tools/Package-Gate-Regression.ps1
```

The Unity asset-bundle project requires **Unity 2022.3.43f1**, Windows x64.
Its existing `ContrabandCasesBundleBuilder.BuildBundles` method performs the
required EFT SDK path-ID replacement passes and validates outputs before
publishing. Do not substitute a generic Unity player build. The client plugin
itself is compiled against the installed game assemblies.

## Outstanding acceptance

Before calling this candidate game-verified:

1. Start the matched client/server build and inspect fresh logs and the live
   resolved catalog report.
2. Test unpack, discard, lock, full-stash Claim/retry, Relay outcomes, Favor
   guarantee, reconnect/recovery and dossier history on a backed-up profile.
   In particular, spend your last case/key, leave the payout unclaimed, restart,
   and resume through the Dossier. Also test an empty ledger and a double-click.
3. Use the gallery for every rarity/layout, missing artwork and long names;
   check mouse/keyboard, reduced motion, sound, common resolutions and cleanup.
4. Capture full-resolution inventory-grid and inspect screenshots of the key.
   Case/key rotations and the key's duplicate label were corrected and visually
   checked in the SDK preview rig; verify both again in Tarkov after its icon
   cache resets. Editor screenshots are not an in-game acceptance test.
5. Operations, Relics and Black Site are implemented separate purchasable cases.
   Verify their finalized live prices, available pools and pre-spend odds, then
   check actual trader sale quotes for rewards (including legal disassembly),
   actual retained-key cadence and the effect of spending keys on Relay.
6. Cash Cache: verify live price/odds, RUB/USD/EUR/Bitcoin previews and amounts,
   one-key consumption, collection/full-stash retry, unchanged Favor, and
   reconnect recovery. Confirm spin ticks and landing sounds audibly, including
   after reopening the UI and at different interface/master/effects volumes.
7. Force every Epic/Legendary MCM variant; browse all three, choose each ordinal,
   Save & Close, reconnect and resume, then claim or Relay where eligible. Verify
   only one selected package is granted and no selection key is consumed.
8. Reproduce game close/relaunch and inspect fresh logs. The former Lobby-state
   restart rejection is regression-tested; the original game shutdown cause
   remains unresolved. Do not blame another mod from shutdown cleanup alone.

See [source build and verification status](docs/SOURCE-BUILD.md). Private logs,
development handoffs and local diagnostic reports are not included in this repo.
