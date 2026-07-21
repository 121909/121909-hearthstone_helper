# Protocol schema contracts

Version 1 protocol contracts use JSON Schema 2020-12 and live in
`schemas/v1`. All schemas use camel-case JSON properties and reject unknown
properties at data boundaries.

| Contract | Purpose |
| --- | --- |
| `snapshot.schema.json` | Immutable, visibility-filtered game state |
| `action.schema.json` | One of the six legal action shapes |
| `branch.schema.json` | One reproducible state transition outcome |
| `route.schema.json` | A scored sequence of up to 12 actions |
| `advisor-result.schema.json` | State-bound local or LLM-reranked result |

`common.schema.json` contains shared entity, card, score, and version types.
Schema `$id` values are stable protocol identifiers and are not network
dependencies.

## Snapshot privacy boundary

`gameId` is a plugin-generated session UUID. It must not be a GameHandle,
account identifier, or server identifier. The opponent model contains only a
hand count, publicly revealed cards, and secret candidates. Unknown opponent
hand CardIds have no field in the schema. BattleTag, AccountId, ServerInfo,
chat, and local paths are not accepted properties.

The builder must copy all values from HDT objects before producing a snapshot.
After publication, a snapshot is immutable. Collections are normalized by zone
position and then entity ID before `stateId` is computed. Volatile fields such
as `remainingTurnTimeMs` are excluded from the state hash.

## Runtime invariants

JSON Schema cannot express every relationship. Producers and consumers must
also enforce these invariants:

1. Friendly hand and board entity IDs are unique and their positions are
   contiguous.
2. Original deck counts total 30 and its hash matches the active profile.
3. Branch probabilities for one action total 1 within `1e-9`.
4. Event sequence values are contiguous and events are resolved in order.
5. Route step indexes start at zero and are contiguous.
6. Every route action passes the legality validator against the state produced
   by the preceding step.
7. `selectedRouteId` and `alternativeRouteId` identify routes in the same
   result; the alternative is absent or different from the selected route.
8. A result is accepted only when its `stateId`, protocol version, rule-set
   version, and candidate-set hash still match the current request.
9. Unknown cards, tags, or generated effects create an `UNSUPPORTED` branch;
   they never create guessed actions or state transitions.

## Compatibility

Protocol version changes only when a wire contract becomes incompatible.
Rule-set versions change when gameplay behavior or scoring changes. A consumer
must reject unknown major protocol versions and rule-set versions not approved
by `profiles/patch-compatibility.json`.
