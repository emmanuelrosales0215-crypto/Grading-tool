# Dynamo bridge

Runs the tested `GradingTool.Core` engine against a live Civil 3D surface from a Dynamo
Python Script node — **with no Windows build step**.

## Why this exists

`src/GradingTool.Civil3D/` is the shipping form of the tool, but it can only be built on a
Windows machine with Civil 3D installed, and every change costs a build + `NETLOAD` + Civil 3D
restart. This path skips all of it:

- `GradingTool.Core` is `netstandard2.0` and references **no** Autodesk assembly, so it builds
  anywhere `dotnet` runs and loads into Civil 3D 2024 (.NET Framework 4.8) and 2025+ (.NET 8)
  alike.
- `DelegateSurface` (`src/GradingTool.Core/Surface/DelegateSurface.cs`) lets a plain Python
  callback stand in for `ISurface`, so the Civil 3D interop can live in a script instead of a
  compiled adapter.
- A Python node edits and re-runs in seconds, and can *ask* the API what it supports rather
  than guessing at it — which is what `explore_tin_api.py` is for.

The engine is unchanged and untouchable from here: the solver, the ADA and municipality rules,
and the TIN math all stay in tested C#. Dynamo only does selection, orchestration and display.

## Files

| File | What it is |
| --- | --- |
| `gradingtool_bridge.py` | `GRADELINE` as a Python node: surface + polyline in, findings and solved elevations out, optional write-back. |
| `explore_tin_api.py` | One-off probe that reports a live `TinSurface`'s real triangle/vertex API. Answers the blocker recorded in the add-in README for 2D grading. |

## Setup

1. **Build Core** on any machine with the .NET SDK:

   ```
   dotnet build src/GradingTool.Core -c Release
   ```

   Copy `src/GradingTool.Core/bin/Release/netstandard2.0/GradingTool.Core.dll` to the Civil 3D
   machine. Nothing else is needed for the default configuration.

2. **Set Geometry Scaling to Medium** — Dynamo → Settings → Geometry Scaling. Civil 3D
   coordinates are large enough that the Large/Extra Large settings make geometry-based nodes
   return nulls. This bites everyone once.

3. **Build the graph**:

   ```
   Select Object  ──> IN[0]   (the existing TIN surface)
   Select Object  ──> IN[1]   (the proposed 3D polyline)
   String         ──> IN[2]   ("StandardParking", "AccessibleRoute", ...)
   Boolean        ──> IN[3]   (write solved elevations back?)
   File Path      ──> IN[4]   (full path to GradingTool.Core.dll)
                      │
                Python Script  ──> Watch
   ```

   Paste `gradingtool_bridge.py` into the Python Script node and set its input count to 5.
   `IN[2]` accepts any member name of `AdaComplianceStandards.SurfaceUse`.

4. Run it. `OUT` is `[summary, [findings], [solved elevations]]`.

Once the graph works, save it and run it from **Dynamo Player** — an engineer then picks a
surface and a polyline and clicks play, without ever opening the node editor. `U` in Civil 3D
undoes a run.

## Known trap: municipality configs on Civil 3D 2024

The bridge passes `ConservativeGradingRules()` with no municipality, matching what the
`GRADELINE` command does today. That is deliberate: loading a config from `Municipalities/`
goes through `System.Text.Json`, which on Civil 3D **2024** (.NET Framework 4.8) drags in
`System.Memory`, `System.Buffers` and friends, and assembly binding conflicts inside AutoCAD
are a well-known nuisance.

If you need jurisdiction rules in the graph:

- On **2025+** (.NET 8) it just works — reference `MunicipalityConfig.Load` and go.
- On **2024**, `dotnet publish src/GradingTool.Core -c Release` and point
  `clr.AddReferenceToFileAndPath` at the DLL inside the full publish output, so its
  dependencies sit beside it.

## Version split

Civil 3D 2025.1 moved Dynamo to .NET 8, Dynamo Core 3.x and PythonNet3; 2024 and earlier are
.NET Framework 4.8 with Dynamo Core 2.x. `GradingTool.Core.dll` spans both — it is
`netstandard2.0` precisely so it does not have to be rebuilt per host. What does *not* span
both is third-party Dynamo packages and, occasionally, Python engine selection; these scripts
use only built-in nodes and the standard library to stay clear of that.

## Verifying against the add-in

Once `GradingTool.Civil3D` is first built on the Windows machine, run `GRADELINE` and this
graph on the same surface and polyline. The findings and solved elevations must match — same
engine, two front ends — so any divergence is an interop bug in the bridge, not an engineering
difference.
