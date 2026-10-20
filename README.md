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

## Credits

- Supply/demand peak pattern table: [WorkshopOptimizerPlugin](https://github.com/belzaru17/WorkshopOptimizerPlugin)
  by belzaru17, MIT licence (notice kept in `Calypso/Core/Workshop/PeakPatterns.cs`).
- Workshop value formula and supply model as published by the Overseas Casuals community (facts only; no code used).
- Game structures: [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs); game data via Lumina.

## Building

`dotnet build Calypso.sln` with the Dalamud dev hooks installed (`%AppData%\XIVLauncher\addon\Hooks\dev`), then add
`Calypso\bin\Debug\Calypso.dll` as a dev plugin.
