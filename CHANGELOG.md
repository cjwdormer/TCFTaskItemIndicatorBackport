# Changelog

All notable changes to Task Item Indicator are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [Unreleased]
### Added
- Mark and place-beacon objectives now indicate too, pointing at the zone trigger in the map instead of a loot pickup. Logs the PlaceItemTrigger zone IDs it finds on first use so the zoneId match can be confirmed in raid - remove that log once verified.

### Changed
- Plugin GUID changed from `com.vultify.taskitemindicator` to `com.thecrimsonfuckr.taskitemindicator`, matching the naming convention used on my other TCF mods mods. 

## [1.0.0] - 2026-08-06
### Added
- On-screen ring indicator when you are near a task item you still need, backporting the task item locator from live 1.0.5.0
- Directional arcs - the part of the ring facing the item brightens, so an item behind you lights the bottom
- The whole ring goes bright when you are close and looking at it, and drops back to directional if you turn away
- Ring is generated at runtime from measurements taken off BSG's reveal footage, no ripped assets
- Ring hides while the take/read prompt is up, the same way the client suppresses its own pointer
- Four settings in F12: enable, ring scale, converge distance, scan interval. Everything else was tuned in raid and baked in
