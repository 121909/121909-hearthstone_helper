# Wild Discard Warlock rule specification

Rule-set version: `0.3.4`
Card definitions: Hearthstone build `247416`
Target mode: `RANKED_WILD`

This document is the implementation contract for the 17 collectible cards in
`profiles/wild-discard-warlock.json`. Card text is not interpreted at runtime.
Each CardId is mapped to an explicit handler, and any interaction outside this
contract produces an `UNSUPPORTED` branch.

## Shared terms

- **Play** means validate the action, pay the dynamic cost, remove the source
  from hand, and resolve its play text. Playing a minion first reserves its
  selected board position.
- **Discard** means remove the selected hand entity, increment the friendly
  discard count, update discard-count-dependent continuous effects, emit the
  discard event, and then resolve that entity's discard triggers.
- **Summon** moves or creates an entity on board without paying its cost and
  without resolving its Battlecry. Each summon is attempted separately; an
  attempt with seven occupied board slots fails.
- **Dynamic cost** is the current entity cost in the Snapshot, not the printed
  cost in the profile.
- **Actual candidates** are the entity IDs presented by the live client Choice.
  The engine may validate and rank them but may not invent or replace them.
- **Terminal lethal** stops the state transition after the event that destroyed
  a hero. Later queued effects are not used to score a post-game state.

## Global event order

| Order | Event | Required transition |
| ---: | --- | --- |
| 1 | Validate action | Check turn owner, source zone, mana, targets, board space, attack rules, location cooldown, and current Choice. Invalid actions create no state. |
| 2 | Commit action | Pay resources and move the played source out of hand; a minion or location occupies its selected slot before Battlecry or use text. |
| 3 | Resolve written text | Resolve clauses from left to right. A clause finishes its nested draw, discard, summon, damage, death, and trigger chain before the next clause. |
| 4 | Discard entity | Move the exact entity from hand, increment `discardCount`, refresh Duke of Below, emit `DISCARD`, then run the discarded card's triggers. |
| 5 | Draw one | Remove the next deck entity. Resolve Casts When Drawn before hand-size checks; after it casts, perform its replacement draw. Otherwise add it to hand or emit `BURN` when hand is full. Empty deck emits the next fatigue event. |
| 6 | Deal damage | Apply damage, detect lethal, process deaths and their queued Deathrattles in engine event order, then continue only if the game is not terminal. |
| 7 | Summon many | Attempt entities one at a time in declared order. Re-evaluate board space and continuous effects after each attempt. |
| 8 | End turn | Discard Temporary hand entities in ascending hand position, fully resolving each discard chain; then finish ordinary end-turn cleanup and pass priority. |

For one random event, branches are ordered by stable CardId/entity ID but retain
their real probabilities. Production sampling records its seed. Equal-cost
random discard candidates are equiprobable unless CardDefs explicitly supplies
different weights. Fixed seed, Snapshot, and rule-set version must reproduce
the same branch set and event order.

The live Snapshot exposes the friendly remaining deck as a complete CardId
multiset, never as a known order. Each draw from a deck with more than one
remaining entity is therefore a uniform random branch. Small branch sets are
enumerated exactly; larger multi-draw chains use the configured deterministic
Monte Carlo seed. Ordered decks are permitted only in explicit rule fixtures.

Visible Divine Shield, Poisonous, and Lifesteal tags are resolved for combat
and spell damage. Divine Shield is removed before health changes, Poisonous
destroys a minion only when damage is applied, and Lifesteal heals after the
simultaneous combat damage is known. A visible Reborn minion currently marks
the Snapshot `unsupported_reborn`; reconstructing it without the original
unbuffed stats and enchantment split would invent state.

The live client represents the second-player compensation Coin as
`DMF_COIN2`; it is equivalent to `GAME_005`: both spend zero mana and add one
temporary mana without exceeding 10 available mana. `DAL_354t` is not part of
the locked deck or the direct effect of Disposable Acolytes. It can enter hand
when Disposable Acolytes randomly summons `DAL_354` Acornbearer and that
minion's Deathrattle adds two Squirrels. Each `DAL_354t` is modeled in hand as
a vanilla 1-mana 1/1 minion; the random pool still marks Acornbearer's
Deathrattle itself as unmodeled instead of predicting that future transition.

`HeroState.Attack` is the total visible hero `ATK`, including the equipped
weapon contribution, matching HDT's `BoardHero` semantics. Equipping or
destroying a weapon replaces or removes that contribution; combat never adds
`Weapon.Attack` a second time.

## Draw, discard, and board invariants

1. A hand contains at most 10 entities and a board contains at most 7 minions
   and locations combined.
2. Discard targets must still be in the friendly hand when their event resolves.
3. A card that summons itself after discard is still counted as discarded.
4. Failed summons do not roll a replacement minion and do not resolve
   Battlecries.
5. A draw that burns a normal card does not run its play/discard text.
6. Casts When Drawn cards cast even when the hand is full and then request the
   replacement draw.
7. Damage and draw chains stop at terminal lethal; no hypothetical later state
   is emitted.

## Card rules

### `TLC_451` Cursed Catacombs / 咒怨之墓

**Play requirements:** 0 available mana and no unresolved Choice. The card is
legal with a full hand because playing it opens one hand slot.

**Sequence:**

1. Commit the spell and request a Discover Choice from the deck.
2. Wait for the actual candidate entity IDs. Do not infer candidates from
   hidden deck order.
3. `SELECT_CHOICE` moves the selected existing deck entity to hand, decreases
   deck count, and marks that entity Temporary through the current turn.
4. Preserve its entity ID, dynamic cost, and creation source. At end of turn it
   follows the global Temporary discard sequence.

No candidate or a candidate not present in the Choice is an unsupported client
state. Temporary expiration is a real discard and therefore runs discard
benefits.

### `TIME_026` Entropic Continuity / 续连熵能

Associated entities: `TIME_026e` (the +1/+1 enchantment) and `TIME_025t`
(Shred of Time / 时空撕裂).

**Sequence:**

1. Snapshot the friendly minions currently on board and give each +1/+1.
2. Do not include minions summoned after this clause.
3. Create two `TIME_025t` entities and shuffle them into the friendly deck using
   the branch seed. Increment `derived.shredsOfTimeInDeck` by two.

When `TIME_025t` is drawn, it casts immediately, deals 3 damage to the friendly
hero, decrements the derived counter, and requests a replacement draw. Lethal
self-damage ends the branch before that replacement draw.

### `VAC_940` Party Fiend / 派对邪犬

Associated token: `VAC_940t`, a 1/1 Felbeast.

**Sequence:**

1. Play Party Fiend at the selected position; this uses one board slot.
2. Attempt to summon the first Felbeast immediately to its right.
3. Attempt to summon the second Felbeast immediately to the right of the first
   successful token (or the source when the first attempt failed).
4. Deal 3 damage to the friendly hero even if either summon failed.

The action only requires one free board slot for Party Fiend. With fewer than
three free slots, token attempts fail as the board fills; self-damage is never
skipped. Terminal self-damage stops the branch.

### `TLC_603` Platysaur / 栉龙

Associated bindings: `TLC_603e`, `TLC_603e2`, and `TLC_603e3` in CardDefs.

**Battlecry sequence:**

1. Occupy the selected board slot.
2. Draw one card using the global draw sequence.
3. If a normal card entered hand, store
   `(platysaurEntityId, drawnEntityId)` in `derived.platysaurBindings`.
4. A burned card, fatigue event, or Casts When Drawn chain with no final hand
   entity creates no binding.

**Deathrattle sequence:** look up the bound entity ID. If that exact entity is
still in the friendly hand, discard it. If it was played, already discarded,
burned, or moved elsewhere, do nothing. Transformation does not break a binding
when the entity ID remains the same; copied cards are not bound.

### `EX1_308` Soulfire / 灵魂之火

**Play requirements:** 1 mana and a legal character target.

**Sequence:**

1. Deal 4 damage to the selected target.
2. Resolve lethal and deaths caused by that damage.
3. If the game continues and the friendly hand is non-empty, branch uniformly
   over every current hand entity and discard the chosen entity.

The spell itself has already left hand and is never a discard candidate. The
discard pool is evaluated after damage and death triggers, not at validation
time.

### `BOT_568` The Soularium / 莫瑞甘的灵界

**Play requirements:** 2 mana.

**Sequence:** perform three draw requests one at a time. After each normal card
enters hand, mark that entity Temporary through the current turn. A burned card
is not Temporary because it never entered hand. Casts When Drawn effects fully
resolve, including replacement draws, before the next of the three requested
draws.

At end of turn, remaining Temporary entities are discarded in hand order. Each
discard fully resolves Hand of Gul'dan, Soul Barrage, Disposable Acolytes,
Boneweb Egg, Silverware Golem, or Walking Dead before the next Temporary card.

### `DMF_119` Wicked Whispers / 邪恶低语

**Play requirements:** 1 mana. Discard is an effect, not an additional play
cost; the spell remains legal when no other friendly hand entity exists.

**Sequence:**

1. Read current dynamic costs from the remaining hand.
2. If the hand is non-empty, find the minimum cost. If several entities tie,
   create equal-probability random branches; the searcher cannot select a
   winner.
3. Discard the branch entity, when one exists, and finish all nested discard
   triggers. With an empty hand this clause has no effect.
4. Give +1/+1 to every friendly minion now on board in either case.

Therefore spiders, random one-cost minions, Silverware Golem, and Walking Dead
summoned by the discard receive the buff. Failed summons do not create buff
targets.

### `SCH_147` Boneweb Egg / 骨网之卵

Associated token: `SCH_147t`, a 2/1 Boneweb Spider.

Playing the card summons a 0/2 minion with no immediate effect. Its normal
Deathrattle attempts to summon two spiders sequentially. Discarding the hand
entity moves it to the discarded/graveyard zone, increments discard count, and
then triggers the same two summon attempts without putting the Egg on board.
Available board space is checked before each spider.

### `CATA_499` Disposable Acolytes / 助祭耗材

Playing or discarding this spell starts the same trigger after the source has
left hand. Attempt two summons sequentially. For each available slot, sample
independently from the build `247416`, Wild-eligible random 1-Cost minion pool;
duplicates are allowed when the game's random-generation rules allow them.

Only the summoned minion's base entity and continuous/static effects apply.
Battlecries do not trigger. If a sampled minion has an effect not implemented by
the current rule set, preserve the summon and mark the branch
`unsupported_interaction`; do not approximate its text. A full board skips the
remaining attempt without consuming another random sample.

### `WON_103` Chamber of Viscidus / 维希度斯的窟穴

Playing the location requires a board slot and creates it with two durability.
It cannot be used on its play turn or while on cooldown.

**Use sequence:**

1. Commit the location activation: decrease durability, set the normal
   cooldown, and remove the location immediately if durability reaches zero.
2. When the client supplies a Choice, use its actual set of up to three hand
   entities.
3. Validate `selectedEntityId` against that Choice, discard it, and fully
   resolve its triggers. If the client supplies no Choice because the hand is
   empty, skip the discard clause.
4. Request two draws sequentially.

The engine never chooses from the entire hand when the client presented a
three-card subset. On the final activation, removing the location can open a
board slot before discard triggers try to summon minions. Board space opened or
consumed by the discard is visible to both subsequent draws and their Casts
When Drawn effects.

### `CATA_490` Ocular Occultist / 魔眼秘术师

**Play requirements:** 3 mana and one board slot. The Battlecry is not an
additional play cost, so the minion remains legal with no other card in hand.

**Sequence:**

1. Put the 3/6 Taunt in the selected board position.
2. If the remaining hand is non-empty, request a hand-discard Choice using the
   actual remaining hand entities.
3. Validate `SELECT_CHOICE`, then discard and fully resolve the selected entity.
   With an empty hand, the Battlecry has no effect and no Choice is fabricated.

The live client resolves this interaction by clicking the selected card in the
normal hand fan. It is not laid out like the location or Discover popup, so
automation advice exports the selected entity as `FRIENDLY_HAND` rather than
`CHOICE`.

Because Ocular Occultist occupies its slot first, a board that had six minions
has no room for a discarded Silverware Golem, Walking Dead, Boneweb spiders, or
Disposable Acolytes summons.

### `WON_098` Silverware Golem / 镀银魔像

Playing the card normally summons a 3/4 minion. When discarded, first count and
record the discard, then try to move that same entity from the discard zone to
the board as a 3/4 minion. It is summoned, not played, and receives no
Battlecry. With a full board it stays discarded and no placeholder is created.

### `RLK_532` Walking Dead / 行尸

Playing the card normally summons a 2/5 Taunt. Its discard handling is identical
to Silverware Golem except that the successfully summoned entity has Taunt.
Board capacity is checked after earlier triggers in the same discard chain.

### `END_016` Chronoclaws / 时空之爪

Playing the card equips a 4-Attack weapon whose live durability comes from the
HDT entity tags (three in the frozen CardDefs). Equipping follows the normal
weapon replacement sequence.

**Hero attack sequence:**

1. Validate the hero attack and target, then deal combat damage.
2. Consume weapon durability, process deaths, and queue the weapon's
   after-attack trigger even if the weapon breaks.
3. If the game continues, read the current friendly hand's dynamic costs.
4. Find the maximum. If several entities tie, create equal-probability random
   branches and discard one. With an empty hand, do nothing.

The discard pool is evaluated after combat deaths. Space opened by combat can
therefore receive discard-summoned minions. The searcher cannot choose a
highest-cost tie result.

### `CATA_493` Duke of Below / 地狱公爵

Base stats are 2/2 with Rush. Its current stats are base stats plus +2/+2 for
each successful friendly discard recorded this game, with other enchantments
applied by normal continuous-effect ordering.

Increment `discardCount` before resolving the discarded card's benefits, then
refresh every Duke entity in hand or on board. A Duke played after three
discards enters as an 8/8 with Rush. Multiple discards in one nested chain each
apply separately. Discarding Duke itself increments the count but provides no
summon trigger.

### `RLK_534` Soul Barrage / 灵魂弹幕

Playing or discarding the card starts the same five-missile effect. Spell Damage
does not modify it (`ImmuneToSpellpower`).

For missiles 1 through 5:

1. Build the pool of all currently living opposing characters, including the
   opposing hero and targetable or untargetable minions; this random split does
   not use normal spell targeting restrictions.
2. Branch uniformly over that current pool and deal 1 damage.
3. Resolve lethal, deaths, Deathrattles, and newly created characters.
4. Rebuild the pool before the next missile.

Stop on terminal hero lethal. Exact branch enumeration is used while the branch
count is below the search threshold; otherwise deterministic seeded sampling
must retain expected value, p10, and variance.

### `BT_300` Hand of Gul'dan / 古尔丹之手

Playing or discarding this spell performs three draw requests sequentially.
Each request fully resolves burn, fatigue, Casts When Drawn, replacement draws,
discard triggers caused by other effects, and lethal self-damage before the next
request. The source has already left hand, so the first normal draw can use the
slot it vacated. Stop the sequence on terminal lethal.

### `CS2_056` Life Tap / 生命分流

Using the Warlock hero power spends its live dynamic cost and consumes its use
for the turn. Resolve one draw request first, including burn, fatigue, Shred of
Time replacement draws, and terminal self-damage. If the game continues, deal 2
damage to the friendly hero. A lethal Shred or fatigue draw ends the sequence
before the additional 2 damage.

## Cross-card order examples

| Interaction | Required order |
| --- | --- |
| Wicked Whispers discards Boneweb Egg | Discard and count Egg; summon up to two spiders; then buff all surviving friendly minions, including those spiders. |
| Wicked Whispers discards Disposable Acolytes | Resolve both random summons and unsupported markers; then buff the surviving summoned minions. |
| Ocular Occultist discards Walking Dead on a six-minion board | Occultist takes the seventh slot; Walking Dead is discarded and counted; its summon fails. |
| Chronoclaws highest-cost tie | Finish combat and deaths; read current dynamic costs; create random branches for tied entity IDs; resolve the chosen discard. |
| Soulfire discards Hand of Gul'dan | Deal 4; if non-terminal choose the random discard; count it; resolve all three draws in order. |
| Hand of Gul'dan draws Shred of Time | Shred casts for 3 self-damage and requests a replacement draw; only then continue the remaining Hand draws. |
| Soul Barrage kills a minion | Deal one missile; remove the dead minion and resolve its Deathrattle; rebuild the target pool for the next missile. |
| Chamber discards lethal Soul Barrage | Resolve missiles first; terminal lethal prevents the Chamber's following two draws. |
| Temporary Soul Barrage expires | End-turn discard and count Barrage; resolve all five missiles before expiring the next Temporary entity. |
| Playing Catacombs or Ocular starts a Choice | End the current route at the client interaction; resume only from a new Snapshot containing actual candidates. |
| Multiple discards in one chain | Increment count and refresh Duke before each discarded entity's own benefit resolves. |

## Unsupported boundaries

The following conditions produce `unsupported_interaction` until their exact
CardDefs behavior has a rule and regression fixture:

- a generated random 1-Cost minion with an unimplemented aura, Deathrattle,
  trigger, or replacement effect;
- a target card transformed into an entity type the zone model cannot express;
- an actual Choice candidate missing from the copied Snapshot;
- a card or enchantment tag used by these handlers but absent from the whitelist;
- external cards that reorder, repeat, or suppress this document's triggers.

An unsupported branch remains identifiable for fixture export and confidence
reporting, but it is never presented as a fully legal, exact route.
