# Environment Setup & Build Guide

## 1. Install the .NET 9 SDK

Download and run the installer from https://dotnet.microsoft.com/en-us/download/dotnet/9.0
(get the **SDK**, not just the runtime). Verify it worked:

```
dotnet --version
```

should print something starting with `9.`. (Only `TaskItemIndicator.Shared` and its tests target
.NET - the plugin itself targets `netstandard2.1` to match BepInEx, but still builds with the .NET 9
SDK.)

## 2. Install an IDE

Either works fine:

- **Visual Studio 2022 Community** (free) - https://visualstudio.microsoft.com/vs/community/. During
  install, check the **".NET desktop development"** workload.
- **VS Code** + the **C# Dev Kit** extension - lighter weight, works fine for everything here.

## 3. Open the solution

Open `TCFTaskItemIndicatorBackport.sln`. Your IDE should restore NuGet packages automatically on first
load. If it doesn't, run `dotnet restore` from the repo root.

## 4. Point the plugin project at your game's managed DLLs

`TaskItemIndicator` needs several DLLs from your own SPT install that can't be redistributed or
fetched via NuGet - they ship with the game itself (BepInEx core, Assembly-CSharp, UnityEngine
modules). The project finds them via an MSBuild property, `SptInstallDir`, rather than a hardcoded
path, so your personal install location never ends up in a file you'd commit.

Set it one of two ways:

- **Local props file (recommended):** copy `build/Directory.Build.local.props.example` to
  `build/Directory.Build.local.props` and edit the path inside. That file is gitignored, so it stays
  on your machine.
- **Environment variable:** set `SPT_INSTALL_DIR` to your SPT folder, e.g. `E:\Single Player Tarkov`.

If `SptInstallDir` isn't set either way, the build fails fast with a message telling you to do one of
the above, instead of a confusing "file not found" from the compiler.

`TaskItemIndicator.Shared` (and its test project) don't touch Unity/BepInEx at all, so they build and
run with no SPT install present - useful if you only want to work on the ring math.

## 5. Build

Set the configuration to **Release** and build the whole solution (Build > Rebuild Solution, or
`Ctrl+Shift+B` in VS / `Ctrl+Shift+F9` in Rider). From the command line:

```
dotnet build -c Release
```

## 6. Run the tests

```
dotnet test Tests\TaskItemIndicator.Shared.Tests\TaskItemIndicator.Shared.Tests.csproj
```

These only exercise `TaskItemIndicator.Shared` - the pure geometry/opacity math behind the ring - so
they run without an SPT install or BepInEx/UnityEngine references. `dotnet test` on the whole solution
will also try to build the plugin project, which does need `SptInstallDir` set (step 4).

## 7. Deploy layout

After a successful Release build:

```
<SPT client>/
  BepInEx/plugins/TCF-TaskItemIndicator/
    TaskItemIndicator.dll          (from src/TaskItemIndicator/bin/Release/TaskItemIndicator/)
    TaskItemIndicator.Shared.dll
```

`build/deploy.ps1` builds and copies this for you - see that script for usage.

## 8. Testing in-raid

Launch the game with the plugin installed and check BepInEx's log for `Task Item Indicator loaded.`.
Pick up a quest with a `FindItem` condition, get within 5m of the item, and the ring should fade in.
F12 opens the mod's config (enable toggle, ring scale, converge distance, scan interval).
