# Compatibility baseline

The machine-readable compatibility matrix is stored in
`profiles/patch-compatibility.json`. A patch is eligible for advice only when
all of the following values match a reviewed row:

1. Hearthstone build.
2. Target deck hash.
3. Rule-set version.
4. CardDefs SHA-256.
5. HDT and HearthDb compatibility fingerprints.

The initial row targets Windows, HDT `1.53.11` at commit
`ddc0ec01afe61a03d459c96b7d7b3f3b5d344573`, and Hearthstone build `246003`.
Its rule set `0.3.1` is retained as historical compatibility evidence. The
active row targets Hearthstone build `247416` with rule set `0.3.3` after
`BOT_568` changed from 1 to 2 mana and Silverware Golem moved from `KAR_205`
to its current `WON_098` printing. It is `EXPERIMENTAL`: the plugin gate,
unknown-deck sampling, local search, LLM validation, Overlay, replay regression,
and shadow telemetry are implemented. Promotion beyond experimental still
requires five completed shadow games, metric review, and a small visible-advice
trial. Expert annotations are optional in the active validation profile.

## Deck hash

The deck hash does not use localized names or card text. To reproduce it:

1. Sort entries by ordinal `CardId`.
2. Format each entry as `CardId:count` followed by one LF byte (`0A`).
3. Encode the complete payload as UTF-8 without a BOM.
4. Compute SHA-256 and format it as lowercase hexadecimal.

The target deck canonical payload hashes to
`3204a0f302fa866376ca21b1b793e4f48cf4e8736525512b44ee951f4408d3b4`.

This LF rule is explicit so the result is identical on Windows and Unix-like
development hosts.

## Patch changes

A new Hearthstone build starts as unsupported. Add a new matrix row only after
the 17 target definitions have been compared, changed rules have tests, and all
binary and CardDefs fingerprints have been refreshed. Do not modify an older
row in place to make a new patch appear compatible.

HDT may temporarily omit the live Hearthstone build from game metadata. In
that case the plugin infers the active build only when the local
`CardDefs.base.xml` SHA-256 exactly matches a reviewed HDT data-file hash. The
HDT file is normalized differently from the source `CardDefs.xml`; build
`247416` therefore accepts source hash
`a3b0e3dcd112626aa47ba16ede1b26506eed175b1fda288c1b6952065c06aac4` and
HDT data-file hash
`1d9cf031fb1fe37a39fdf4f515702bcc2425eb71ba8be0a236948372b15a38bb`.
It does not treat the older build embedded in the pinned HearthDb assembly as
the live client build.
