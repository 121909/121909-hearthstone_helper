# Card data snapshot

The target deck profile is stored in
`profiles/wild-discard-warlock.json`. It freezes the deck quantities and the
card definitions used by the active rule set.

## Source

- Provider: [HearthstoneJSON](https://hearthstonejson.com/)
- Hearthstone build: `247416`
- Snapshot date: `2026-07-23`
- English definitions:
  `https://api.hearthstonejson.com/v1/247416/enUS/cards.collectible.json`
  (`74b69fa5b65d091212f152e2adfa665ac5c6e1b2f820cbeee3c92fb8a6057400`)
- Simplified Chinese definitions:
  `https://api.hearthstonejson.com/v1/247416/zhCN/cards.collectible.json`
  (`f81f2c35858b2feea761dc76b5b36af65ac36c1fe4f0daab7b224099689f653c`)

The localized `name` and `text` values are preserved as published, including
Hearthstone formatting markup and line-break hints. The `tags` object contains
the structured collectible card fields needed by the deck gate and rule
engine. Flavor text, artist credits, and acquisition text are intentionally not
part of the rule-set snapshot because they cannot affect gameplay.

Card updates create a new compatibility entry instead of rewriting older matrix
rows. Build `247416` changed `BOT_568` from 1 to 2 mana; rule set `0.3.2`
contains the reviewed cost update. A future gameplay text or tag change makes
the active rule set unsupported until it has been reviewed and tested.
