# Compliance review

Review date: `2026-07-21`  
Decision: **offline-only development**  
Machine policy: `profiles/compliance-policy.json`

This is an engineering release decision, not legal advice. The available public
terms do not establish permission to provide turn-by-turn recommendations in a
live ranked Hearthstone match. Live advice therefore remains disabled unless
Blizzard provides written authorization that expressly covers this product.

## Source findings

### Blizzard

The current [Blizzard End User License Agreement](https://www.blizzard.com/en-us/legal/08b946df-660a-40e4-a072-1fbde65173b1/blizzard-end-user-license-agreement)
was reviewed on `2026-07-21`. Section 1.C prohibits creating, using, offering,
or distributing methods not expressly authorized by Blizzard that influence or
facilitate gameplay and give a user an advantage. It separately prohibits bots,
unauthorized software that changes or facilitates gameplay, unauthorized data
mining, unauthorized connections, and unauthorized esports use.

The planned advisor does not automate input, but a live overlay that computes
the best legal route is specifically intended to influence play. The EULA's
wording is broader than automation, so the absence of mouse or keyboard control
does not make live advice compliant by itself. Blizzard's reserved discretion
to allow some third-party interfaces is not the same as authorization for this
advisor.

### Hearthstone Deck Tracker

The pinned HDT [README](https://github.com/HearthSim/Hearthstone-Deck-Tracker/blob/ddc0ec01afe61a03d459c96b7d7b3f3b5d344573/README.md)
documents deck tracking, an overlay, and plugin extensibility. The
[plugin guide](https://github.com/HearthSim/Hearthstone-Deck-Tracker/wiki/Creating-Plugins)
documents the .NET Framework `4.7.2` plugin ABI, references to the installed HDT
executable, lifecycle handling, and dependency packaging.

Those documents show that HDT supports third-party plugins technically. They do
not grant Blizzard authorization for all plugin behavior. The HDT repository
also states that its contents are all rights reserved. The project must compile
against the user's pinned HDT installation and must not redistribute HDT,
HearthDb, or dependencies already supplied by HDT.

### HSReplay and HearthSim

The [HearthSim Terms of Service](https://github.com/HearthSim/legal/blob/ba1c4deefe877eb8638dba89e5cdd9443c1ead69/TERMS.md)
cover HSReplay.net and HDT. Section 2 limits access to personal, non-commercial
use and prohibits non-API service access at greater-than-human request rates.
Section 8 prohibits scraping, copying, distributing, or exploiting the Service
without express written permission.

The target deck URL may remain a human-readable provenance link. Runtime code
must not scrape HSReplay pages, download premium statistics, reproduce HSReplay
branding, or treat the page as an API. The frozen card definitions come from
HearthstoneJSON instead.

## Release decision

| Capability | Decision | Reason |
| --- | --- | --- |
| Synthetic Snapshot and rule tests | Allowed | No game connection or third-party service access |
| Offline analysis of personal or explicitly authorized `.hdtreplay` files | Allowed | User-controlled data; still apply privacy filtering |
| Offline expert annotation | Allowed | No live match advantage |
| Read-only live Snapshot capture | Blocked for release | HDT exposes the API, but Blizzard authorization for this advisor is unproven |
| Live local turn recommendations | Blocked | Intended to influence and facilitate live gameplay |
| Live LLM reranking or upload | Blocked | Same live-advice issue plus a remote data flow |
| Mouse, keyboard, or client automation | Prohibited | Directly conflicts with the EULA and project non-goals |
| Hidden CardIds, memory inspection, or protocol interception | Prohibited | Violates visibility rules and creates data-mining/connection risk |
| Automated HSReplay page access | Prohibited | Non-API scraping is outside the granted service use |
| Tournament or esports use | Prohibited | Requires separate event and Blizzard authorization |

## Required product controls

1. `OFFLINE_ONLY` is the default and only releasable mode under policy version 1.
2. Live HDT event subscription, Snapshot export, search, Overlay advice, and LLM
   calls must remain behind one fail-closed compliance gate.
3. A blocked live request returns `COMPLIANCE_BLOCKED`; it must not calculate a
   hidden route in the background.
4. No input-simulation or game-process memory code may enter the repository.
5. Opponent hidden hand and deck identities are never serialized, even if a log
   or upstream object exposes them.
6. Network upload is off by default. A future authorized build still requires a
   separate explicit opt-in and the privacy whitelist.
7. HDT and HearthDb binaries are development prerequisites supplied by the
   user's HDT installation, not distributable project artifacts.
8. Terms must be reviewed again for every release and before changing the
   compatibility matrix to a live-capable status.

## Authorization evidence

Live enablement requires written Blizzard authorization that identifies this
advisor and explicitly permits live state reading, local and/or remote route
computation, an on-screen recommendation overlay, and use in Ranked Wild. A
general statement that deck trackers are tolerated, the existence of HDT's
plugin API, or publication in a community plugin list is insufficient.

Until that evidence is recorded in this repository and the policy is reviewed,
implementation can continue against fixtures and authorized replays, but the
full real-time MVP cannot be released or represented as compliant.
