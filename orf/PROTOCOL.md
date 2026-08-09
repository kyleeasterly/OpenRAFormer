# OpenRAFormer2 engine ↔ orchestrator protocol (v0)

Single source of truth for the file-based protocol between the OpenRA engine traits
(`OpenRA.Mods.LLM`) and the `orf` orchestrator. Everything is a file under the run
directory so that the exact bytes every LLM saw and emitted are persisted.

## Conventions

- **Run dir** `R`: `runs/<runId>/` at repo root (gitignored). The engine process receives
  it via the `ORF_RUN_DIR` environment variable. All engine traits no-op when unset.
- **Atomic writes**: every JSON file is written as `<name>.tmp` in the same directory,
  then renamed to `<name>`. Readers never see partial files. Writers overwrite via
  rename; readers that consume-and-delete only delete files they fully parsed.
- **Player identity**: `match.json` lists players in an array. The engine sorts the map's
  playable slots by name ascending (e.g. `Multi0`..`Multi7`) and assigns `players[i]` to
  slot `i`. Each player is addressed everywhere else by their `slug`.
- All cell coordinates are `[x, y]` map cell integers. Actor ids are engine `ActorID`
  uints. Ticks are world ticks (40 ms at default game speed; 25 ticks = 1 s).

## Files

| Path | Writer | Reader | When |
|---|---|---|---|
| `R/match.json` | orf | engine (load screen + traits) | once, before game launch |
| `R/state/<slug>.json` | engine | orf agent loop | every `stateIntervalTicks`, atomic overwrite |
| `R/state/game.json` | engine | orf dashboard | every `stateIntervalTicks`, atomic overwrite |
| `R/orders/<slug>/inbox/<seq>.json` | orf | engine (consumed + deleted) | agent turn |
| `R/orders/<slug>/results/<seq>.json` | engine | orf agent loop | after consuming inbox file |
| `R/result.json` | engine | orf | once, game over |
| `R/live.jpg` | ffmpeg | orf dashboard | continuously (`-update 1`) |
| `R/engine.log` / `R/engine.err` | orf (redirect) | humans | continuous |
| `R/agents/<slug>/…` | orf | humans/analysis | per turn (see orf docs) |

## match.json (orf → engine)

```json
{
  "runId": "20260808-2593-first-ffa",
  "map": "<map uid OR .oramap filename>",
  "stateIntervalTicks": 25,
  "options": { "gamespeed": "default" },
  "players": [
    { "slug": "p1", "display": "Llama-3.3-70B", "bot": "llm",
      "faction": "Random", "spawn": 1, "team": 0 }
  ]
}
```

- `options` entries become lobby `option <key> <value>` commands (host-issued).
- `spawn`: 1-based map spawn point, `0` = random. `faction`: internal faction name or
  `Random`. `team`: `0` = FFA.
- `bot` is the engine bot type (always `llm` for LLM players; other types, e.g. `cabal`,
  allow mixed LLM-vs-scripted-AI matches later).
- The load screen starts the match only when `ORF_RUN_DIR` is set and this file exists;
  otherwise normal game behavior.

## state/<slug>.json (engine → agent) — fog-of-war scoped

Everything in this file respects what `slug`'s player can legitimately know:
live actors gated by `CanBeViewedByPlayer`, remembered structures from the player's
`FrozenActorLayer`, resources only in explored cells.

```json
{
  "tick": 1500, "second": 60,
  "you": { "slug": "p1", "faction": "nod", "cash": 2300,
           "powerProvided": 200, "powerDrained": 140 },
  "map": { "width": 126, "height": 126, "yourSpawnCell": [12, 100] },
  "production": [
    { "queue": "Building",
      "current": { "item": "proc", "name": "Tiberium Refinery",
                   "progressPercent": 40, "paused": false, "ready": false },
      "queued": ["proc"],
      "buildable": [ { "item": "nuke", "name": "Power Plant", "cost": 300 } ] }
  ],
  "buildings": [ { "id": 133, "type": "fact", "name": "Construction Yard",
                   "cell": [12, 100], "hpPercent": 100 } ],
  "units": [ { "id": 140, "type": "e1", "name": "Minigunner",
               "cell": [14, 98], "hpPercent": 100, "idle": true } ],
  "visibleEnemies": [ { "id": 201, "type": "ltnk", "name": "Light Tank",
                        "owner": "p3", "cell": [40, 90], "hpPercent": 80,
                        "isBuilding": false } ],
  "lastKnownEnemyBuildings": [ { "type": "fact", "name": "Construction Yard",
                                 "owner": "p3", "cell": [60, 80] } ],
  "exploredResources": [ { "cell": [20, 90], "cells": 42 } ],
  "recentResults": null
}
```

- `exploredResources`: resource cells the player has explored, clustered into 8×8 cell
  blocks; `cell` is the block centroid, `cells` the count in the block.
- `owner` on enemy entries is the enemy's slug (players know who owns what they can see).
- The engine keeps writing this file after a player dies (mostly-empty state +
  `you.defeated: true`) so the agent loop can wind down cleanly.

## orders/<slug>/inbox/<seq>.json (agent → engine)

`<seq>` is a zero-padded monotonically increasing integer per player (`000001.json`).
One file per agent turn. The engine processes files in name order, issues valid orders,
writes the result file, and deletes the inbox file.

```json
{ "orders": [
  { "type": "start_production", "item": "e1", "count": 3 },
  { "type": "cancel_production", "item": "e1", "count": 1 },
  { "type": "deploy",       "actorIds": [123] },
  { "type": "move",         "actorIds": [140, 141], "cell": [30, 90], "queued": false },
  { "type": "attack_move",  "actorIds": [140, 141], "cell": [40, 90], "queued": false },
  { "type": "attack",       "actorIds": [140], "targetActorId": 201 },
  { "type": "guard",        "actorIds": [140], "targetActorId": 133 },
  { "type": "stop",         "actorIds": [140] },
  { "type": "sell",         "actorId": 133 },
  { "type": "repair",       "actorId": 133 },
  { "type": "set_rally",    "actorId": 134, "cell": [15, 99] }
] }
```

Validation rules (engine side): actors must exist, be alive, and be owned by the
player (`targetActorId` must be visible to the player); production items must be
buildable in one of the player's queues. Invalid orders are rejected individually —
valid orders in the same file still execute.

Buildings are **auto-placed**: when a Building/Defense queue item completes, the engine
places it automatically near the base. The LLM only ever starts production. The first
MCV is auto-deployed shortly after game start if the agent hasn't deployed it.

## orders/<slug>/results/<seq>.json (engine → agent)

```json
{ "seq": "000001", "tick": 1520, "results": [
  { "index": 0, "status": "ok" },
  { "index": 1, "status": "rejected", "reason": "item 'x123' is not buildable" }
] }
```

orf feeds the most recent results into the next turn's prompt (`recentResults`).

## state/game.json (engine → dashboard) — observer scoped, no fog

```json
{
  "tick": 1500, "second": 60, "paused": false, "gameOver": false,
  "map": { "title": "Scorched Earth", "width": 126, "height": 126 },
  "players": [
    { "slug": "p1", "name": "LLM", "faction": "nod", "colorHex": "FF2020",
      "spawnCell": [12, 100], "cash": 2300, "earned": 5300,
      "powerProvided": 200, "powerDrained": 140,
      "unitCount": 12, "buildingCount": 6, "armyValue": 3400,
      "unitsKilled": 3, "unitsLost": 5, "buildingsKilled": 0, "buildingsLost": 1,
      "winState": "Undefined" }
  ]
}
```

`winState`: `Undefined` | `Won` | `Lost`. orf joins this with the spec (model/display
names) for the dashboard; the engine does not know model names.

## result.json (engine → orf)

```json
{ "finishedAtTick": 90000, "second": 3600,
  "players": [ { "slug": "p1", "winState": "Won" } ] }
```

Written once when the game ends (or every player but one is defeated). orf then stops
agent loops, stops ffmpeg, and archives the replay from the engine support dir into `R/`.
