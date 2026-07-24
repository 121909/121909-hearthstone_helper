# Offline regression report

- Generated (UTC): `2026-07-24T07:53:11.6415081+00:00`
- Result: **FAIL**
- Replays: 37
- Snapshots: 150/355 evaluated
- Legal routes: 525/525 (100.00 %)
- Latency p50/p95/max: 23.42/171.05/200.19 ms
- Deadline expiration: 0/150 (0.00 %)
- Expert annotation gate: **DISABLED**
- Visible-suggestion prerequisites: **NOT MET**

## Shadow run

- Completed games with a published Shadow analysis: 6/5 (6 completed, 6 started)
- Completed games without a published analysis: 0
- Runs / version cohorts / games missing metadata: 1/1/0
- Target cohort: `0.4.11` / `0.3.3`
- Historical games ignored from other cohorts: 1
- Automated acceptance thresholds: **NOT MET**
- Requests/terminal analyses: 355/355
- Published/superseded/cancelled/failed: 234/0/0/121
- Superseded rate: 0.00 %
- Latency p50/p95/max: 2.18/50.33/478.88 ms
- Duplicate state requests: 0
- Missing request starts / unfinished requests: 0/0
- Visible suggestions: 0
- Unsupported analyses / occurrences: 43/51

| Plugin | Rules | Started | Completed | Requests | Analyses |
| --- | --- | ---: | ---: | ---: | ---: |
| `0.4.11` | `0.3.3` | 6 | 6 | 355 | 355 |

## Unsupported interactions

| Interaction | Snapshots |
| --- | ---: |
| `inactive_player:OPPONENT` | 108 |
| `inactive_step:BEGIN_MULLIGAN` | 6 |
| `inactive_step:FINAL_GAMEOVER` | 5 |
| `inactive_step:MAIN_END` | 18 |
| `inactive_step:MAIN_READY` | 3 |
| `inactive_step:MAIN_START` | 61 |
| `incomplete_known_deck:11/9` | 1 |
| `incomplete_known_deck:12/10` | 1 |
| `incomplete_known_deck:13/11` | 1 |
| `incomplete_known_deck:21/19` | 1 |
| `incomplete_known_deck:24/22` | 1 |
| `incomplete_known_deck:26/24` | 1 |
| `unknown_choice_source` | 9 |
| `unknown_hand_card:DAL_354t` | 8 |
| `unknown_hand_card:DMF_COIN2` | 58 |
| `unsupported_reborn:162:CORE_ULD_723` | 4 |

## Input errors

None.

Deadline expiration is an offline deadline check. Actual state supersession is reported separately when shadow-run telemetry is supplied.
