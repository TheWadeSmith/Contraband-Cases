# Contraband Cases

Contraband Cases **0.4.15** is a client-and-server mod for
**SPT 4.1.x**, targeting **SPT 4.1.5** with the compatible 4.1.3 server SDK.
Full gameplay acceptance is still pending. Back up your saves and delivery
records, and update matching client and server files together.

### 0.4.15: transition guards and opening recovery

- Relay UI discovery waits for a real lobby with no active GameWorld.
  Leaving that lobby invalidates earlier discovery work across transitions.
- A failed opening can retry from a fresh request after validating the current
  catalog, with guards against duplicate submission and stale results.
- Recovery screens support **Close — resume later** and Escape. Pending requests
  remain observed after closing, so an eventual result can still be recovered.
- Messenger's live notification work has a **two-second total budget**. Slow or
  failed notifications no longer hold the opening flow indefinitely; isolated
  notification snapshots and late-failure handling preserve delivery recovery
  and duplicate-payout protection.

Reward content, economy, odds, case/key settings, models and sounds are unchanged
from 0.4.14. These fixes have automated regression coverage; the reported older
transition failure has no confirmed live cause, and native focus/Escape and
gameplay acceptance remain pending. See the release notes accompanying the GitHub download.

### 0.4.14: Black Site expansion and Relay recovery

- Eight full loadouts: M4 Assault, MP7 Breacher, SR-25 Marksman, RSASS Overwatch,
  AUG Night Patrol, MCX Night Raider, SCAR-H Heavy Assault and MPX Combat Medic.
  Each includes an assembled weapon, matching ammunition, armored rig, helmet,
  headset, backpack and medicine, plus a spare magazine, night vision or surgery
  kit. Load magazines before use. These join Black Site and Mixed, not Operations.
- Ten optional storage prizes: More Cases' Weapon Modifications Case, THICC
  Weapon Modifications Case, Equipment Cabinet, Safe, THICC Ammo Case, THICC
  Grenade Case, THICC Medcase and Weapon Rack; CNN's Gear Box and Mod Case.
  They require their content mods. High-capacity chase rewards stay exceptionally
  rare and do not create a separate jackpot-only opening category.
- Fresh surprise Legendary openings award **the single prize the reel lands on**.
  There is no three-way choice or extra selection key. Epic still offers three
  choices; Legendary choices saved by older builds remain intact.
- A finished Relay sidegrade no longer advertises a contradictory next upgrade.
  This fixes the repeated **Opening needs attention** screen when reading that
  saved result. Claiming still uses the original reward and exactly-once delivery.

All rewards remain separate **Mechanic Messenger** attachments. A medical or
storage case is an **empty prize**, not a wrapper holding the other items. No
automatic stash insertion, bulk-medicine filler or profile migration is added.
The 496 published reward definitions and existing case/key drop settings remain
unchanged. New content can affect automatic case pricing and individual loot odds.

### 0.4.13: distinctive rewards and lighter previews

Future openings use new, immutable compact recipes: medical stations, weapon
reserves, specialist equipment and collector expeditions. Repeated endgame armor,
headsets and survival tools have been reduced; ordinary losses, near-even results
and wins remain possible. High-value draws are weighted more sparingly, and each
cargo case retains an exceptionally rare thematic chase. No native item price
is inflated. Cash Cache payouts and case/key loot weights are unchanged.

Cargo prices now reserve a **₽75,000 key opportunity cost** and use a slightly
lower reference-price factor. Keys remain universal and find-only; Therapist's
default buyback target is ₽75,000 before profile bonuses. Existing explicit
price overrides are respected. A higher key sale value is a tradeoff, not free
value: keeping or selling a found key competes with opening a case or using Relay.

Some prizes include an **empty storage case as loot**, never as a compulsory
wrapper. All prize items are still separate Messenger attachments. Secure
containers, map loot containers, currency and access keys remain excluded from
cargo rewards. Cash Cache alone has its currency-specific rules.

Rarity bands are recalibrated for these recipes only. Previously earned rewards,
their contents, grades and saved Relay choices are unchanged. Relay is an optional
wager, not a guaranteed three-step climb: available progression depends on the
actual package and catalog. A same-rarity replacement can be worth less and ends
the chain. Favor does not bypass chase exclusions.
Normal and premium choices show Relay eligibility before you choose. Legendary
prizes correctly show that there is no higher rarity. The wager screen shows the
inventory key count; the server still checks whether a key is usable.

The reel now requests only displayed artwork, prioritizing the winner and visible
tiles, with at most two native launches per frame and four in flight. Timeouts or
missing pictures do not block delivery. Messenger identifies each modern prize
by name and delivery reference. Inventory and Broker consistently call the
all-content case **BR-12 Mixed Case**.

### Compact prizes and Messenger delivery

New cargo rewards are curated raid kits, specialist equipment and collectible
packages. Value comes from useful equipment and rare items, not repeated weapons
or large quantities of basic medicine. Shipped current cargo packages have at most eight
separate items, 128 total item/attachment nodes and 64 storage cells. Attached
weapon/armor parts remain assembled. No native item prices are inflated.

**Send to Messenger** delivers your saved prize through **Mechanic**. Collect
attachments individually as stash space allows; delivery never inserts the prize
directly into the stash or sorting table. There is no mandatory backpack or
container wrapper. Larger historical prizes and currency payouts split into
messages of at most eight separate items, preserving the complete prize.

Uncollected attachments remain for ten years. Deleting a message discards its
remaining items. **Close — resume later** leaves an unsent prize pending; reopening it does
not reroll it. Already-paid inventory prizes are not moved or mailed again.

The economy targets roughly one million roubles per case, with different theme
pools, useful losing results, ordinary wins and exceptionally rare chase prizes.
Prices still follow the installed catalog and existing configuration; reference
value is not guaranteed trader resale. Cash Cache retains its published payouts
and probabilities, including GP Coins and physical Bitcoin.

### Previous 0.4.11 compatibility changes (historical rewards)

Four optional integrations now have current, validated recipes alongside their
unchanged, recovery-only historical definitions. ISB and WTT armor uses assembled
source presets; ISB's incompatible optional Korund side plates are omitted.
Natalya uses currently supported Level 6 equipment instead of removed Level 7
items. Amonya uses legal two-token stacks: 32 Arcane + 32 Mystic tokens, or
40 Eldritch tokens; these are barter tokens, not fireable rounds. Its Satchel
shipment remains eight items. These changes restore unavailable content, so
installed-catalog prices and package odds can change when those packs load.
Case/key drop settings, premium-tier rates and Bitcoin odds are not changed by
this compatibility fix.

Reward details distinguish **reference value** from **estimated trader resale**.
Resale is a handbook-based startup estimate using the best eligible default-unlocked
RUB buyer for each complete item, excluding Fence and profile bonuses. Missing
buyers/prices or unsupported condition/container data show "unavailable", not
zero or a partial total. This estimate does not change reward grading or pricing.
Cash Cache retains its currency-specific valuation labels.

Old paid forests are never resized or replaced. Valid retired forests remain
recoverable; missing or invalid historical content remains blocked, with reasons
listed under `UnavailableRetiredLots` in `catalog-report.json`. Corrected rewards
use a separate Relay track. Both server and client must be updated together.
The new `retiredLotIds` pack field keeps historical recipes out of new openings;
`omitRootSlots` on preset recipes can omit only named optional leaf slots, never
required armor parts.

The release checks pin the corrected packs, historical recipes and accepted
Unity bundles. Existing published archives are not replaced.

### Slightly more loot

The defaults now add **2.2% key loot weight** and **1.1% combined
case loot weight**: a small 10% relative increase from 2.0% and 1.0%. Cases remain
crate-only; keys keep their existing eligible container/bot rules. Reward odds,
prices, shipments, item weights and item actions are unchanged by this supply
adjustment. These are added loot-table weights, **not per-container drop
probabilities**. Explicit settings in existing installations are preserved;
set `keyLootWeightPercent` to `2.2` and `caseLootWeightPercent` to `1.1` in your
existing config to opt into the new defaults.

### Million-rouble stakes

The earlier 0.4.10 large shipments remain intact for saved-prize recovery.
Current cargo draws use compact curated successors instead; contents and exact
quantities are listed before delivery. Higher-stakes rarity bands, rare premium
openings and chase caps remain in place. Cash Cache retains larger
currency payouts, including **4 Bitcoins at 0.90%** and **10 at 0.10%**.
All original paid packages/payouts retain their exact contents and grades;
old and new cargo Relay generations cannot cross. Keys remain find-only and
universal; drop rates, 5 kg case weights, Unity models and sounds are unchanged.
Use the installable `ContrabandCases-0.4.15-SPT4.1.5.zip` and
`ContrabandCases-0.4.15-SPT4.1.5-SHA256.txt` from the GitHub release.
GitHub's automatic
"Source code" archives are not installable mod packages. Updating requires both
client and server files; see **Install and build** below.

### Earlier releases

0.4.9 sets **all five case types to 5 kg each**, replacing the 15 kg inherited
from the native airdrop container. Existing cases use the same templates, so no
inventory migration is needed; restart the server and game after updating.
Keys, reward contents, prices, drop rates, and Unity models/sounds are unchanged.

0.4.8 adds **10 GP Coins at 3%** and **25 GP Coins at 1%** to Cash Cache.
Bitcoin rewards stay at 1 coin (0.90%) and 2 coins (0.10%). The two GP entries
take probability from existing rouble outcomes, not from Bitcoin. GP values are
labelled handbook barter references, not guaranteed rouble cash-outs. Existing
paid payouts remain collectible. Case/key drop rates, cargo rewards, Relay,
Favor and Unity models/sounds are unchanged.

0.4.7 hides the Broker menu button by default: press **F5** in the main menu
or stash to open it. Rebind this shortcut in MCM; the optional button is under
Advanced. The shortcut does not open the Broker during raids, text entry or MCM.
This update also cleans up MCM testing controls, resumes saved offers without
replaying a misleading spin, fixes stale Therapist case quotes, and adds rare
case finds to verified crates only. Keys remain separate finds and universal.
That release used 1% combined case loot weight; it is not a per-crate drop chance.

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

The new **Cash Cache** is a separate one-payout contract: RUB, USD, EUR, GP Coins or
physical Bitcoin. It uses the same universal key and roulette, with no
discard decision, Relay attempt or Broker Favor change.

Automated tests, the offline catalog audit and Unity bundle checks are distinct
from complete in-game validation. Sound and model orientation were confirmed
in-game on the preceding build; the accepted Unity bundles are unchanged.

Finalized case/key handbook values now synchronize into SPT's trader-price
caches at the startup barrier.
This fixes the provisional ₽1,000 value producing ₽630 Therapist quotes even
when the case's finalized reference price was correct. Native trader rates and
configured sell targets are unchanged; other items' cached prices are preserved.
This version also reorganizes MCM without renaming persisted settings,
uses one-shot action buttons, and resumes saved offers directly without replaying
the spinner.

## Play loop

1. Buy a **BR-12 case from Mechanic LL1**. Find a **BR-12 Relay Key** in
   raids. Keys are not sold by traders.
2. In your stash, right-click the case and choose **Unpack**. Read the server's
   current category and lot odds before confirming.
3. A normal opening spends **one case and one key**, saves three offers, and reveals
   the first. **Choose This Package** locks that package for the next decision.
   **Discard & Reveal Next** permanently gives it up.
4. You may discard twice. The third offer is automatically locked.
5. **Send to Messenger** delivers the entire locked package through Mechanic
   without another key. Take individual attachments when you have room.
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
| BR-12 Mixed Case | Mixed catalog, including all available themed content | Existing case color |
| Operations | Practical ammunition, medical supplies, field equipment and loadouts | Green |
| Relics | Anime, Pokemon and Yu-Gi-Oh cards, historical relics and arcane curios | Violet |
| Black Site | Elite equipment, complete loadouts, night operations, rare storage and experimental supplies | Red |
| Cash Cache | One exact payout of RUB, USD, EUR, GP Coins or physical Bitcoin | Yellow |

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
The server commits the surprise tier and reward draw **before spending the
case/key**. Cash Cache is excluded.

| Opening | Chance | What you receive |
| --- | ---: | --- |
| Normal | 98.3% | The existing discard/lock gamble, including ordinary jackpots |
| Epic | 1.5% | Three distinct Epic-or-Legendary packages; browse all, choose **one** |
| Legendary / Godly | 0.2% | One saved Legendary package; the reel lands on the prize you receive |

These are rare upgrades to the opening, not separate purchasable guaranteed-win
cases. No extra key is charged for browsing or choosing. Epic keeps its three-way
choice; fresh Legendary openings lock one prize automatically and have no higher
Relay tier. Messenger delivery works normally. **Close — resume later** leaves the
same choices or locked prize available through the Dossier. Reconnecting cannot
reroll them. Legendary three-way choices saved by older builds are preserved.
Premium packages keep the case theme, but can share a category or progression
track. Choosing one never grants all three.

Premium eligibility also checks reference value: Epic requires at least the
greater of 200,000 RUB or 1.15 times `(case price + 75,000)`; Legendary requires
at least the greater of 300,000 RUB or 1.6 times that cost. The 75,000 is the
standard foregone key-sale allowance, not an additional charge. Custom trader
settings can differ from that benchmark. Reference value does not
guarantee a particular trader cash-out. At least three non-chase packages must
qualify. If a tier is unavailable, its probability becomes Normal **before the
roll**; the confirmation shows its zero rate. Missing saved content blocks a
choice rather than silently replacing a promised reward.

Extreme chase packages retain their combined 1-in-400 cap per premium draw.
Epic packages are drawn without replacement, recalculating weights after each draw.
Full odds publish both tier rates and complete first-draw premium probabilities;
for a fresh Legendary opening, that first draw is the only awarded prize. Its
reel uses the Legendary pool, not a three-card choice preview.

The roughly one-million-rouble target is not a fixed price override. Read each
case's finalized price and reward odds in the Broker: available integrations and
configuration affect them. Offline projections are not live mod-hook validation.
All case templates are accepted by Therapist by default. Native resale is
approximately 63% of the finalized handbook value before other modifiers;
the existing explicit `therapistSellPriceCase` override remains Mixed-only.

## Cash Cache

Buy a BR-12 Cash Cache from **Mechanic LL1**, then Unpack it in your stash.
Review the exact odds and confirm to spend **one cache and one universal key**.
The server commits one payout before the roulette begins. **Send to Messenger**
delivers that exact amount without another key. Native currency stack limits are
respected; more than eight stacks use additional messages. A full stash does not
block delivery: collect attachments later. Closing, skipping or reconnecting
cannot reroll it.

The `cash-opening-v3-million` draw table is fixed and does not use cargo chase
reweighting. Existing `cash-v1` payout identities are retained for paid claims;
new `.shipment-v1` identities use these amounts with the same published chances:

| Payout | Chance per opening |
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

Bitcoins are ordinary in-game items, not a real wallet, cryptocurrency exchange
or fractional crypto balance. Bitcoins are separate items when native stack
rules require it. GP Coins are Tarkov barter currency, not cryptocurrency.
Their payouts follow native stack limits; no arbitrary modded currency is admitted.

Price is the weighted mean of **non-Bitcoin payouts**, rounded up to ₽1,000.
USD/EUR use published direct rouble purchase offers; GP Coins use their finalized
handbook barter reference value, not a promised trader cash-out. Bitcoin uses an estimated
standard LL1 Therapist sale value from the finalized handbook and buy rules.
Foreign-currency purchase value is **not** guaranteed rouble liquidation value;
player-specific sale modifiers are not simulated. Price and amounts remain
fixed for that catalog session. Cash ignores the Mixed case fixed-price override.

The SPT 4.1.5 base-database audit gives **₽1,018,000 + one key** and an expected reference
payout of about **₽1,049,676**, not expected cash-sale proceeds. Defining near-even
as within 10% of case plus assumed key cost:

| Assumed key cost | Meaningful loss | Near-even | Win |
| --- | ---: | ---: | ---: |
| Found key, ₽0 financial cost | 41% | 24% | 35% |
| ₽25,000 opportunity cost | 41% | 24% | 35% |
| ₽65,000 opportunity cost | 41% | 24% | 35% |

These are offline estimates, not installed-mod or raid-cadence validation.
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
not have the same value and can be worth less. A loss consumes the stake and key and grants no items.
Three stages is a ceiling, not a promised climb. Some packages allow fewer or no
wagers; the UI offers only currently available Relay actions.
Stage 3 and Legendary rewards end the chain. Incomplete target pools disable
Relay without preventing a valid Claim.

**Broker Favor:** each modern Manifest loss adds **one**, regardless of stage.
At **3/3**, the next eligible Relay is a guaranteed upgrade and then resets
Favor to zero. It still costs a key. Favor persists between cases.
Claim and Forfeit do not change it. Older physical-weapon Relay saves retain
their historical stage-depth recovery rule.

Current compact-v2 cargo rarity is derived from the complete package's reference value,
not authored as a reward-pack override:

| Rarity | Reference-value band |
| --- | ---: |
| Common | below ₽400,000 |
| Uncommon | ₽400,000–799,999 |
| Rare | ₽800,000–1,499,999 |
| Epic | ₽1,500,000–2,399,999 |
| Legendary | ₽2,400,000 and above |

These are item reference values, **not cash payouts or trader resale quotes**.
Historical non-shipment lots retain their original 40k/75k/150k/300k boundaries
and shipment-v1/compact-v1 retain 240k/450k/900k/1.8m boundaries, so existing
claims and frozen Relay candidates remain valid.

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
New Relay results can use a disclosed-contents/sealed-card fallback reel.
Reopening an already saved offer goes straight to its decision screen, not a
second spin. Recovery of an unfinished transaction may reveal a newly committed result.

Decision screens show a hero image, plain rarity name, contents thumbnails,
and an optional **Contents & Details** view with the full item list and Relay
disclosure. Artwork falls back from the main item to a real contents image,
then to a built-in rarity seal. Missing artwork does not alter rewards.

## Prices and keys

Default configuration:

- All five case prices: automatic, stock **5** per case type.
- No fixed Mixed price or Therapist case-buyback override by default.
- Keys: **find-only**; no default key buyback override.
- Cases and keys are excluded from generated fake-player flea listings and
  generated barter requirements, even when a compatibility mod disables the
  global BSG blacklist. Finalization removes stale generated offers containing
  them and refreshes Mechanic's cached case prices. Real player and unrelated
  offers are left alone. Mechanic's flea visibility still follows your trader
  settings; this does not make keys purchasable. A mod that deliberately removes
  these custom exclusions later can still override them.
- Cases: also found in wooden, weapon, ammo, grenade and supply crates (including
  the corresponding Labyrinth crates). No case spawn loot on bots/people,
  static corpses, jackets, bags, drawers, safes or ground/barrel caches.
  Only opening-enabled case types enter the crate pool, with equal shares.
  These are ordinary cases: any rare Epic/Legendary surprise still happens at opening.
- Case injection weight: **1.1% combined added pool weight**, shared by all available
  case types, not 1.1% for each type. This is about **1.09% per independent item
  selection**, not per crate or raid; empty crates, space limits and SPT/mod loot
  settings affect actual finds. Cases and keys are separate finds, not guaranteed pairs.
  `caseLootWeightPercent` in the server config adjusts this weight; **0 disables
  case drops** without disabling keys. Omitted settings default to 1.1; explicit
  existing settings are not overwritten.
- Key injection weight: **2.2% of existing pool weight** in eligible static
  containers and bot pools that already contain vanilla key items. This is
  **not a flat 2.2% probability per container or raid** (about 2.15% per
  independent weighted selection before downstream loot rules).

The case restriction governs generated spawn loot, not inventory confiscation:
a looting AI can still pick up a case from a crate, and players can carry cases.
Other mods that replace loot generation or modify pools after startup can override
these rules. These loot rules do not change trader stock or reward odds.

Existing live settings are not automatically reset by source changes. If
`fixedCasePrice` is null, cargo cases use 90% of the lesser of mean and median
reward **reference use value** under optimal sequential keep/discard play,
minus a **₽75,000 opening-key allowance**, rounded up to ₽1,000 with a ₽1,000
minimum for inexpensive custom catalogs. This reserves value for a raid-earned key and
prevents a rare jackpot from inflating the case above normal rewards. It is
not a cash-return target, a full valuation of your play time, or a profit promise.
Opening price does not charge for optional Relay/Favor. Those are reported
separately, including every key consumed. Prices are
calculated at startup, published before opening, and bound to the case's
catalog identity. They are reference-value prices, not promised trader return
or Relay-profit targets. Existing fixed-price settings remain explicit overrides.

`therapistSellPriceKey` defaults to **75000**, including when omitted from an
older config. An explicit positive override is preserved; explicit `null` keeps
the legacy ₽65,000 handbook registration (not a ₽65,000 trader payout). The
target uses the standard Therapist coefficient; other mods and profile bonuses
can change the actual quote. No trader sells keys, and flea listings stay disabled.

The gameplay target is one retained key per **1.5–2 normal looting raids** on
average, including zero-key raids, leaving room for occasional Relay spending.
The initial 2% weight received a modest increase to 2.2%; this still has **not**
been shown to meet that target. PMC and scav supply must be
evaluated separately; the current logger covers PMC only.

For illustration, 25 eligible
independent item selections per raid with 75% of generated keys retained would
average 0.404 keys/raid (one per 2.48 raids) at 2.2%, versus 0.368 keys/raid
(one per 2.72 raids) at the previous 2.0%. Real looting and other mods can differ greatly.
The server logs `Supply raid audit: PMC completed` with newly retained key and
per-case-type instance counts after normal PMC raid settlement, including zeros.
Carried items and duplicate
end events are excluded; scav/transit runs and mail/BTR deliveries are not
counted. Testing-grant enablement is labeled; exclude those samples from balance
measurements. Counts describe retained items, not every spawned or encountered
item. This logging never adjusts loot and does not include profile identifiers.

### Gameplay balance

Prices follow the current resolved compact catalog, not the earlier bulk-shipment
projection. The economy report compares optimal reference-value play, keeping
the first offer, and keeping the first offer worth case price + ₽75,000. Three
offers are a keep/discard decision, not three rewards or a guaranteed best-of-three.
Native item prices are unchanged; optional mods can change the value and supply
of equipment substantially.

Current rewards favor complete compatible equipment, small support kits,
collectible sets and rare individual chase items. An empty storage case can be
a prize in its own right; it is never required as a reward wrapper. Historical
large shipments remain recovery-only. New openings and Relay from compact rewards
cannot select them; saved older Relay chains keep their own generation.

Use the Broker case overview for your actual finalized price, packages and odds.
Prices vary with installed content; no one installation's private catalog report
is a universal price list. The report models loss/near-even/win with a ±10%
reference-value band, both bought and found cases, and key opportunity costs of
₽0, ₽25,000, ₽65,000, ₽75,000 and ₽150,000. These are diagnostics, not enforced
quotas, cash returns or promises about the live modded economy.

Normal openings retain losses, near-even outcomes and wins. A typical high-value
result must not make every selection Legendary; exceptionally valuable chases
remain rare. Repetition checks cover storage cases, surgical equipment and repair
kits as well as endgame armor, headsets and survival tools. Not every package
supports Relay, and no top-rarity package does. The published **1.5% Epic** and
**0.2% Legendary** surprise rates apply only when enough qualifying packages are
available. Relics requires its optional content mods. Cash Cache amounts, price
formula and draw odds are unchanged; its diagnostics now include the same
₽75,000 key benchmark used for cargo and Relay.

For example, Extraction Specialist has useful protection/storage equipment and
exactly one SJ6 and one M.U.L.E., not a bulk injector shipment. Medical Command
and Medical Acquisition offer different specialist kits. Collectible card sets
remain, alongside historical relics, night-vision kits and rare thermal chases.
The WW2 weapon packages supply a compatible magazine/scope separately;
their descriptions explicitly tell you what to fit. Standard preset weapons and
armor remain assembled. The WTT night-vision kit includes its compatible mount chain.

An expensive label is not proof of utility or cash value. For example, the
historical WTT Field Medical Kit contains an analyzer and iodide tablets, not
ordinary first-aid supplies. Its high reference value is treated as a chase;
actual usability and trader sale quotes remain part of in-game acceptance.

### Rare chase rewards

Opening-only chases include Black Site Marksman, Black Site Expedition Jackpot, Erica Ultimate, Dragonite
Holo, Tri-Horned Dragon, Vault Twin Rifles and Precision Assault. Packages
worth at least ₽4,500,000 reference value also join that pool (₽750,000 for
historical non-shipment lots), protecting against
modded price outliers such as Condor Loadout and Field Medical Kit. The More Cases
Equipment Cabinet is also explicitly opening-only, even below that value cutoff.

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

Installed mod hooks can change values, item compatibility and available Relay
steps. Offline projections are checks of the captured definitions, not proof
that every installed hook has executed. The finalized live report remains the
authority for the catalog used by an opening.

## Included integrations

All **20 packs** ship together: **135 eligible compact rewards**, plus five
non-opening current definitions and **374 unchanged published definitions**
retained for recovery (514 definitions total). The all-pack projection resolves
135 fresh lots: 42 Operations, 29 Relics and 64 Black Site. Its current packages
remain below the enforced
8 / 128 / 64 limits. Cash Cache's 15 current and 15
historical payouts are separate. Unavailable
optional packs are skipped with a reason instead of generating invalid rewards;
your active catalog may therefore be smaller.

The all-pack projection has no skipped providers and validates every current
compact prize. Historical recovery still requires its exact old content. Some
current rewards have no comparable trader-resale estimate; the UI reports
that uncertainty instead of inventing a price. The projection uses declared
source templates/presets, not an execution of every installed mod hook.

- Required Core: practical raid kits, specialist equipment and rare chase prizes.
- Vanilla Vault: rare complete equipment packages.
- Black Site Loadouts: eight complete raid kits using native items and presets.
- More Cases and CNN Containers: ten standalone storage prizes, when installed.
- Krackasourus Anime, Pokemon and Yu-Gi-Oh Cards: collection packages.
- SJX Combat Chemistry and Vultify CoolerStims: small specialist medical kits.
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

All published cargo definitions through 0.4.13, including the original 50 core
lots and `core.json` pack version **0.3.3**, remain unchanged. New generations
use new lot identities; published recipes are not edited in place.
Old saved rarity ladders retain their original meanings, and old Relay rewards
cannot upgrade into the new higher-stakes generation.

## MCM / configuration menu

In the BepInEx configuration UI / compatible MCM, controls are grouped as follows:

- **General:** Open Broker, Open Broker shortcut, Reduced motion and Sound volume.
  Broker contains reward browsing, history, Favor, Resume Saved Reward and
  **Status & Sound → Test Sound**. It requires no testing toggle. Resume uses
  saved server state; it cannot create a fresh opening or reroll a package.
  Press **F5** on the main menu or in your stash to open Broker; rebind or clear
  **Open Broker shortcut** in MCM. It does not activate in raids, during case
  operations, while typing into a selected text field, or while MCM is open.
  Use Broker's Close button or Escape to leave it. Open Broker in MCM remains
  available without a shortcut. F5 had no assignments in the inspected local
  SPT/mod settings; other installations or hardcoded mod shortcuts may differ.
  The menu button is **hidden by default**, including upgrades from the old
  default-on setting. With advanced settings shown, **Show Broker button** opts
  into a button beside Trading on the normal main-menu layer, only if there is
  safe space. Its new saved key is `Show Menu Button`; the old `Show Broker Button`
  key is preserved but no longer controls visibility. Other preferences are unchanged.
- **Spawn test items:** enable **Enable item spawning**, leave **Spawn selection**
  on **Case and tier**, choose **Case type** and **Opening quality**, set the
  case/key quantities, then click **Spawn selected items**. Cases are limited
  to 10, keys to 40, and their combined quantity to 40. Zero cases means keys
  only, so case/quality selections are ignored. Cash Cache requires Natural
  quality and does not force a payout. Invalid selections disable the button
  with an explanation; they are not silently converted into another outcome.
- **Animation preview — no items spent:** enable **Enable previews**, choose
  **Preview result** and duration, then **Play animation preview**. These are
  local cosmetic tests, not real openings or item grants.
- **Advanced tests and diagnostics** (MCM's advanced-settings filter): catalog
  ID/rarity/screen filters, missing-picture and long-name simulations, extra UI
  logging, and the legacy provider-pool selector. Catalog previews require
  Enable previews and read the server catalog without economic actions. To use
  **Legacy spawn pool**, first select **Legacy provider pool** under Spawn
  selection; the normal case/quality controls are then ignored.

Spawning requires the server's `testingInventoryGrantsEnabled=true` as well as
the client's Enable item spawning. The shipped server default is **false**;
use a test profile. **Enable previews is not required for spawning.** Grants
remain blocked in raids, while another case window is active, or by the server.

Natural preserves real opening odds. Epic/Legendary force that surprise tier
for newly granted test cases only. Epic provides three saved choices; Legendary
spins to one automatically locked prize with no choice screen.
Open forced cases before restarting the server: unopened testing tags are
process-local and bounded to 256 entries. Once opened, the tier and saved result
persist normally. An unavailable forced tier consumes neither case nor key.
Legacy provider filters such as Vault/Mega are **not** guaranteed God cases;
categories without a matching provider still use normal selection.

Actions appear as buttons rather than on/off preferences. Old configuration
section/key names and preference values remain compatible. Pending action
flags are cleared when the plugin binds settings, preventing replay on launch.
Without a compatible configuration UI, the original one-shot boolean entries
remain available in the config file.

In Broker, Tab/Shift+Tab or left/right cycles controls. Select or hover text,
then use Page Up/Down or Home/End to scroll. Hold confirmation requires a fresh
primary click or Submit press; focus loss cancels the hold. Catalog examples
are not upcoming rewards. The Dossier is read-only until Resume Saved Reward
explicitly leaves it for an existing settlement; an empty ledger opens no case.

## Recovery and save safety

All economic operations are server-authoritative and use durable profile
journals, exact frozen item definitions and transaction witnesses.

- Closing or skipping an animation does not reroll or undo the result.
- Reopening a case resumes an unfinished Manifest before opening another.
- Saved offers reopen directly at their decision screen without a second spin.
- Failed opening requests can retry after fresh catalog validation. Closing a
  recovery screen with **Close — resume later** or Escape retains observation
  of the pending request; it does not cancel or repeat a committed reward.
- Delivery does not require free stash/sorting space. Mechanic sends native
  Messenger attachments, which can be collected individually. A failed delivery
  remains recoverable; it does not create a replacement prize.
- Collected or manually deleted attachments are never reconstructed by retrying
  delivery. Mail expires after ten years; deleting it forfeits uncollected items.
- A native save or delivery-verification failure blocks further Contraband Cases actions
  for that profile in the current server session. Restart only after resolving
  the save problem, then resume the recorded reward; do not delete its journal.
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
Preserve custom reward packs and profile/delivery journals too. Remove no other mods.

When upgrading, replace the shipped reward-pack JSON files as well as
the DLLs: the new contents are authored in those files. Back up any edits to
shipped packs and reapply them carefully; custom fixed-price settings still
override automatic Mixed pricing. Already-paid openings retain their original
rewards. Unopened cases use the new table when opened after the update.

Source verification:

See [Source build and publication scope](https://github.com/TheWadeSmith/Contraband-Cases/blob/main/docs/SOURCE-BUILD.md) for required
local game references, optional fixtures and configurable installation paths.

```powershell
dotnet test Tests/ContrabandCases.Tests.csproj -c Release --no-restore -v minimal
dotnet build ContrabandCases.sln -c Release --no-restore -v minimal
pwsh -NoProfile -File tools/Package.ps1
pwsh -NoProfile -File tools/Validate-Package.ps1
pwsh -NoProfile -File tools/Package-Gate-Regression.ps1
```

The package regression runs in disposable copies. Release assemblies and scripts
must carry matching versions. New releases receive new archive names; published
older archives, including 0.4.14, must not be overwritten with different content.

The Unity asset-bundle project requires **Unity 2022.3.43f1**, Windows x64.
Its existing `ContrabandCasesBundleBuilder.BuildBundles` method performs the
required EFT SDK path-ID replacement passes and validates outputs before
publishing. Do not substitute a generic Unity player build. The client plugin
itself is compiled against the installed game assemblies.

## Outstanding acceptance

Automated tests, native-library checks and
offline economy audits are not a crash-free guarantee or a substitute for
gameplay testing. No GitHub CI workflow is configured.

The automated checks cover bounded artwork loading, immutable historical recipes,
reachable Relay paths, decision-weighted rarity/repetition, exact delivery recovery,
missing dependencies and the real client contract. Offline audits distinguish
reference value from known/unknown resale and compare different choice strategies,
bought cases, found-case sale opportunity cost and key values up to ₽150,000.
These checks do not start the server/game or modify live profiles.

Before calling 0.4.15 fully game-verified:

1. Start the matched client/server build and inspect fresh logs and the live
   resolved catalog report.
2. Test unpack, discard, lock, full-stash Messenger delivery/retry, Relay outcomes, Favor
   guarantee, reconnect/recovery and dossier history on a backed-up profile.
   In particular, spend your last case/key, leave the payout unclaimed, restart,
   and resume through the Dossier. Collect only part of a message, reconnect,
   and verify only the remaining attachments exist. Also test an empty ledger
   and a double-click.
3. Use the gallery for every rarity/layout, missing artwork and long names;
   check mouse/keyboard, reduced motion, sound, common resolutions and cleanup.
4. Sound and case/key orientation were confirmed in-game on the preceding
   build; the same accepted bundles are retained. Check for regressions if
   changing interface volume, graphics settings or the installed mod set.
5. Operations, Relics and Black Site are implemented separate purchasable cases.
   Verify their finalized live prices, available pools and pre-spend odds, then
   check actual trader sale quotes for rewards (including legal disassembly),
   actual retained-key cadence and the effect of spending keys on Relay.
6. Cash Cache: verify live price/odds, RUB/USD/EUR/GP Coin/Bitcoin previews and amounts,
   one-key consumption, partial attachment collection/full-stash retry, unchanged Favor, and
   reconnect recovery. Confirm spin ticks and landing sounds audibly, including
   after reopening the UI and at different interface/master/effects volumes.
7. Force every Epic/Legendary MCM variant. For Epic, browse three and test each
   selection. For fresh Legendary, verify the gold-only reel lands on one locked
   prize with no selection screen. Close — resume later, reconnect and claim;
   ensure the same prize resumes without a second spin or an extra key charge.
   Preserve and resume any three-way Legendary choice saved by an older build.
8. Reproduce game close/relaunch and inspect fresh logs. The former Lobby-state
   restart rejection is regression-tested; the original game shutdown cause
   remains unresolved. Do not blame another mod from shutdown cleanup alone.
9. Resume a saved Relay sidegrade and deliver it through Messenger. It should
   offer delivery, not another Relay or the repeated Opening needs attention
   screen. Verify each new loadout's usable equipment and collect individual
   attachments. Confirm storage prizes are empty and missing optional storage
   mods disable only their own reward packs.
10. Check lobby/raid transitions and relaunch with fresh logs. Exercise an opening
    failure followed by retry, current-catalog changes, duplicate clicks, and
    **Close — resume later** / Escape while recovery is pending. Check native
    keyboard focus and confirm the eventual saved result resumes exactly once.
    Delay or fail Messenger live notifications and verify that opening recovery
    completes without duplicating the recorded reward. The transition guard
    does not establish the cause of the earlier reported failure.

Compatibility tests also exercise all 13 corrected rewards against an offline
projection of installed template/preset definitions and the real client parser.
This is not a substitute for executing the installed mod hooks or claiming
rewards in-game. Compare displayed resale estimates with actual trader offers;
profile bonuses and trader eligibility can produce differences.
