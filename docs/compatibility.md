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
Its rule set `0.2.0` is `EXPERIMENTAL`: the plugin gate, specialized card rules,
unknown-deck sampling, local search, LLM validation, Overlay, replay regression,
and shadow telemetry are implemented. Promotion beyond experimental still
requires the phase 5 expert annotations, 50 completed shadow games, metric
review, and a small visible-advice trial.

## Deck hash

The deck hash does not use localized names or card text. To reproduce it:

1. Sort entries by ordinal `CardId`.
2. Format each entry as `CardId:count` followed by one LF byte (`0A`).
3. Encode the complete payload as UTF-8 without a BOM.
4. Compute SHA-256 and format it as lowercase hexadecimal.

The target deck canonical payload hashes to
`c980187b4a4ff17509f32aa68db749fc6bcb64cc6f8bd32dd61f0d49b8fd2eb0`.

This LF rule is explicit so the result is identical on Windows and Unix-like
development hosts.

## Patch changes

A new Hearthstone build starts as unsupported. Add a new matrix row only after
the 17 target definitions have been compared, changed rules have tests, and all
binary and CardDefs fingerprints have been refreshed. Do not modify an older
row in place to make a new patch appear compatible.
