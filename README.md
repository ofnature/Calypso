# Calypso

Island Sanctuary automation for FFXIV (Dalamud, API 15), part of the Daedalus plugin suite. The goal is to take a
character from first arrival on the island to max rank and then run the island every day, unattended, on any number
of toons.

**Status: scaffold.** The island state reader, the daily-loop runner, IPC and fleet status are in place. The
systems themselves (workshop solver, gathering, granary, cropland, pasture, exports, progression, layout) are built
one at a time on top of it.

## Commands

- `/calypso` (alias `/caly`): open the main window
- `/calypso start`: run the daily loop ("do my island")
- `/calypso stop`: stop

## IPC

| Gate                     | Shape                | Notes                                           |
|--------------------------|----------------------|-------------------------------------------------|
| `Calypso.GetVersion`     | `Func<int>`          | IPC contract version                            |
| `Calypso.IsBusy`         | `Func<bool>`         | loop or single task running (incl. waiting)     |
| `Calypso.GetStatusJson`  | `Func<string>`       | status JSON; extend-only, check `schema`        |
| `Calypso.StartDailyLoop` | `Func<bool>`         | false = busy / not logged in                    |
| `Calypso.RunTask`        | `Func<string, bool>` | run one task by id                              |
| `Calypso.Stop`           | `Action`             | stop whatever is running                        |

Fleet status and start/stop travel over the Daedalus LAN relay (`calypso.status`, `calypso.control`). Without
Daedalus, every box simply runs on its own.

## Workshop

The Workshop tab plans the whole remaining week and re-plans every day: a week-horizon search over efficiency chains
that carries your own production (supply), groove, materials, rest days and favors forward. In **Suggest** mode it only
shows the plan; in **Auto** the daily loop also sets tomorrow's agenda. **Rank progress** keeps the materials for
every construction your rank allows out of the plan. Paste a community schedule to compare it under the same model.

## Granary

The granary planner (logic only so far; the in-game part comes next) decides each day, for both granaries, whether to
collect and where to send the next expedition for how long. It feeds the workshop first: the granary rares this week's
and next week's plans would use if they had them, then construction costs set aside in rank mode, then gatherables
that are slow to gather, and finally a small stock of every rare. Expeditions run the full 7 days unless the cowries
run short; a collection that would overflow the 999 pouch cap is flagged with exactly what would be lost.

## Cropland and pasture

The cropland and pasture planners (logic only so far; the in-game part comes next) feed the workshop's produce and
leavings the same way the granary feeds its rares: what this week's and next week's plans would use if they had them,
then construction, then a small stock of everything. Each day the cropland planner collects what the gardener
harvested, plants empty plots with the crops that cover the most of what is short (spread across the needs, only
from seeds you hold, counting the seed every replant takes), and keeps, replants or dismisses growing plots only when
the need moved enough to be worth it, with the care fee for each plot. The pasture planner collects the caretaker's
leavings and advises which animals to keep, capture or release and which feed pays; capturing, feeding and releasing
stay with you. A collection that would overflow the 999 pouch cap is flagged with exactly what would be lost.

## Credits

- Supply/demand peak pattern table: [WorkshopOptimizerPlugin](https://github.com/belzaru17/WorkshopOptimizerPlugin)
  by belzaru17, MIT licence (notice kept in `Calypso/Core/Workshop/PeakPatterns.cs`).
- Workshop value formula and supply model as published by the Overseas Casuals community (facts only; no code used).
- In-game value measurements and a worked four-workshop day from
  [mji-craftwork](https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork) by Jim Jiang, Apache-2.0 licence
  (numbers only, used as test cases; no code used).
- Granary mechanics (50 cowries a day, 7 days at most, 2-6 rares a day by granary level, 0-24 of each normal
  resource, payout at the daily reset) as published by the Console Games Wiki and Icy Veins (facts only). Expedition
  area names from the game's MJIText sheet via [ffxiv-datamining](https://github.com/xivapi/ffxiv-datamining) (game
  data, facts only). Extension pricing and mid-expedition collection cross-checked against
  [visland](https://github.com/awgil/ffxiv_visland) by awgil (no licence: facts only, no code used).
- Cropland and pasture mechanics: growth time, the 50-produce and 20-leavings care caps, feeds and the moods they
  reach, care fees in seafarer's cowries, from the game's own help and item text; crops, seeds, animals and their
  leavings from the MJICropSeed, MJIItemPouch, MJIAnimals and MJIFarmPastureRank sheets, all via
  [ffxiv-datamining](https://github.com/xivapi/ffxiv-datamining) (game data, facts only). Yield per harvest, care fees
  per plot and per animal and the second-leaving chance by mood as published by the Console Games Wiki and Icy Veins
  (facts only). The 5-cowrie plot fee cross-checked against [visland](https://github.com/awgil/ffxiv_visland) by
  awgil (no licence: facts only, no code used).
- Game structures: [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs); game data via Lumina.

## Building

`dotnet build Calypso.sln` with the Dalamud dev hooks installed (`%AppData%\XIVLauncher\addon\Hooks\dev`), then add
`Calypso\bin\Debug\Calypso.dll` as a dev plugin.

## Licence

MIT — see [LICENSE](LICENSE).
