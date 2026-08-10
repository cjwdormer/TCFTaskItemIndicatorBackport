# Task Item Indicator

Live Tarkov got a thing in 1.0.5.0 where a small segmented ring shows up in the middle of your screen when you're near a task item you still need, and the part of the ring facing the item lights up. SPT's client is older than that patch so it never shows up. This puts it back.


## What it actually does

Get within range of a task item you still need and a faint ring fades in at the centre of your screen. The arcs facing the item light up brighter, so an item behind you lights the bottom of the ring and you know to turn around. Walk toward it while looking at it and the whole ring goes bright as you arrive.

It only fires on **task items** - the ones that go in your task items tab. A quest that wants three Salewas won't light up every Salewa on the map. Mark and place-beacon objectives light up the same way, pointing at the zone you need to reach instead of a loot pickup.

Turn away while you're standing near it and the direction comes straight back, because that's exactly when you still need it.

## Where the ring came from

Nothing here is ripped. Live is IL2CPP with encrypted metadata, so its code can't be read, and its art wasn't needed anyway - the ring is generated procedurally at startup from measurements taken off BSG's own reveal footage:

- four arcs, roughly 70 degrees each, centred on the diagonals
- gaps of about 20 degrees on the cardinals, which is why "behind you" lights two arcs rather than one
- outer radius 7px, inner 4px at 720p, scaled to your screen height
- each arc brightest in its middle, tapering to about 72% at the ends

Three frames across two different scenes agreed on all of it to within a few percent.

The client does carry a dormant version of this UI - `UIPointer` has a `SenseSprite` next to the usual hand and prohibited cursors, and `ActionPanel.ShowPointer` fades it in. It's a dead end: the sprite was never authored into this client build so it draws nothing, and `ShowPointer` takes a bool, so it could never have carried a direction anyway.

Which task items count is the game's own test, lifted from `GameWorld.ManageQuestLoot` - started quests, unfinished `FindItem` conditions, re-checked every couple of seconds so a quest you finish mid raid stops pinging. Mark and place-beacon objectives (`LeaveItemAtLocation`, `PlaceBeacon`) are tracked the same way, matched to their zone trigger in the map by zoneId.

## Settings

F12 in game. Everything takes effect immediately, no restart.

| Setting | Default | What it does |
|---|---|---|
| Enable Mod | on | master toggle |
| Ring Scale | 1.0 | size multiplier. 1.0 matches live, which is small |
| Ring Thickness | 0.43 | band width as a fraction of the ring's radius. higher is thicker, 1.0 is a solid disc |
| Ring Opacity | 1.0 | maximum opacity the ring reaches when fully lit |
| Ring Color | white | tint applied to the ring |
| Converge Distance | 1.5 m | how close before the whole ring lights up instead of pointing. you have to be looking at it too |
| Scan Interval | 0.25 s | how often it looks for items. direction still updates every frame |

Everything else - detection range, opacity, how dim the unlit arcs go, how tight the lit arc is, fade timing - got tuned in raid and then baked in. No point making you tune what already looks right.

Distances are measured from your eyes, not your feet, so an item on a shelf and one on the floor read the same.

## Structure

```
src/
  TaskItemIndicator/            BepInEx plugin - game state, config, Canvas/Texture2D
    TaskItemIndicator.cs
    TaskItemIndicator.csproj
  TaskItemIndicator.Shared/     pure ring math, no Unity/BepInEx dependency
    RingGeometry.cs
    TaskItemIndicator.Shared.csproj
Tests/
  TaskItemIndicator.Shared.Tests/   xUnit tests against RingGeometry
build/                          build/deploy tooling - see BUILD.md
```

Client-only BepInEx plugin, no server side. Built for SPT 4.0.13. See `BUILD.md` for the full
setup/build/test guide.

## Install

Extract the archive and drop the `BepInEx` folder into your SPT root. Needs SPT 4.0.13.

## Credits

Original mod by Vultify - the ring measurements, the Shared/plugin split, and the whole v1.0.0 feature set are their work. Maintained by TheCrimsonFuckr since.
