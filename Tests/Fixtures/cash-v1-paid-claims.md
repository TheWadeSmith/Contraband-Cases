# Pre-GP Cash Cache compatibility fixture

`cash-v1-paid-claims.json` contains 13 synthetic active paid claims, one for each
original cash-v1 payout. It was generated from the unmodified 0.4.7 payout code
before adding GP Coins on 2026-09-06. It contains no player profile or inventory.

Reference inputs were USD 130, EUR 150 and Bitcoin 500,000 RUB, with native-like
currency stacks of 500,000 and Bitcoin stacks of one. The fixture preserves the
original identities, forests, fingerprints, commitment evidence and journal shape.

Do not regenerate it from the new draw table. It exists to catch changes that
strand already-paid rewards when future-opening weights or currency support change.
