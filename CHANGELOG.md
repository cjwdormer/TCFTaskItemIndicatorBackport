# Changelog

All notable changes to Task Item Indicator are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/); versions follow [SemVer](https://semver.org/).

## [released]

## [1.0.0] - 2026-08-06
### Added
- On-screen ring indicator when you are near a task item you still need, backporting the task item locator from live 1.0.5.0
- Directional arcs - the part of the ring facing the item brightens, so an item behind you lights the bottom
- The whole ring goes bright when you are close and looking at it, and drops back to directional if you turn away
- Ring is generated at runtime from measurements taken off BSG's reveal footage, no ripped assets
- Ring hides while the take/read prompt is up, the same way the client suppresses its own pointer
- Four settings in F12: enable, ring scale, converge distance, scan interval. Everything else was tuned in raid and baked in
- Mark and place-beacon objectives now indicate too, pointing at the zone trigger in the map instead of a loot pickup
- Three new settings in F12: Ring Thickness, Ring Opacity, and Ring Color - previously fixed values

### Fixed
- Mark/place-beacon zones that spawn into the scene a few seconds after raid start (e.g. from content mods) could be silently missed - the trigger scan now keeps rescanning until the scene settles, instead of only checking once right at raid start

### Changed
- Plugin GUID changed from `com.vultify.taskitemindicator` to `com.thecrimsonfuckr.taskitemindicator`, matching the naming convention used on my other TCF mods. Existing config will reset under the new GUID.