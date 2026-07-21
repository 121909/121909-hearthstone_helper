# Card data snapshot

The target deck profile is stored in
`profiles/wild-discard-warlock.json`. It freezes the deck quantities and the
card definitions used by the first rule set.

## Source

- Provider: [HearthstoneJSON](https://hearthstonejson.com/)
- Hearthstone build: `246003`
- Snapshot date: `2026-07-21`
- English definitions:
  `https://api.hearthstonejson.com/v1/246003/enUS/cards.collectible.json`
- Simplified Chinese definitions:
  `https://api.hearthstonejson.com/v1/246003/zhCN/cards.collectible.json`

The localized `name` and `text` values are preserved as published, including
Hearthstone formatting markup and line-break hints. The `tags` object contains
the structured collectible card fields needed by the deck gate and rule
engine. Flavor text, artist credits, and acquisition text are intentionally not
part of the rule-set snapshot because they cannot affect gameplay.

Future card updates must create a new compatibility entry instead of silently
changing this snapshot. A changed gameplay text or tag makes the matching rule
set unsupported until it has been reviewed and tested.
