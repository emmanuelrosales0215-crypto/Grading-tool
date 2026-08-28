# Dynamo bridge — what we learned, and why it isn't the path

**Status: blocked.** Driving the grading engine from a Dynamo Python node does not work in
Civil 3D 2024 with the CPython3 engine. The live path is the compiled add-in,
[`src/GradingTool.Civil3D`](../src/GradingTool.Civil3D). This directory is kept because the
findings below cost real time to establish and one of the scripts is still useful.

## The idea

`GradingTool.Core` is `netstandard2.0` and references no Autodesk assembly, so Dynamo can load
it directly. `DelegateSurface` was added so a Python callback could stand in for `ISurface`,
which would have let a graph run the tested solver against a live Civil 3D surface with **no
Windows build step**. The add-in had never been compiled — no machine with Civil 3D was
available at the time — so this looked like the faster road to a real drawing.

## What actually happened, in order

Tested live in Civil 3D 2024, Dynamo 3.x, CPython3 engine:

| # | Problem | Resolution |
| --- | --- | --- |
| 1 | `clr.AddReferenceToFileAndPath` → `AttributeError` | IronPython-only API; PythonNet has no such attribute |
| 2 | `clr.AddReference("GradingTool.Core")` → `FileNotFoundException` | PythonNet 3 dropped `sys.path` assembly probing |
| 3 | `Assembly.LoadFrom(path)` → `FileLoadException`, HRESULT `0x80131515` | Windows mark-of-the-web on a downloaded DLL. **Fix:** `Assembly.Load(File.ReadAllBytes(path))`, which skips the zone check. Ticking *Unblock* in the file's Properties is the manual equivalent |
| 4 | `System.Func[Double, Double, Double](fn)` → `Constructor on type 'System.Reflection.Emit.TypeBuilder' not found` | **Fatal.** No fix |

Problems 1–3 are solved, and this is confirmed working against a real drawing: the
`netstandard2.0` engine loads into Civil 3D's .NET Framework host, its types import, a
`Select Object` node's wrapper unwraps via `InternalObjectId`, and a transaction reads the
surface.

Problem 4 is the wall. Dynamo's CPython3 host blocks the `Reflection.Emit` that PythonNet needs
to synthesise a .NET delegate from a Python function, so `System.Func[...](callable)` cannot be
constructed at all. It reproduces in seven lines with no Civil 3D and no project DLL:

```python
from System import Func, Double
def f(x, y): return x + y
d = Func[Double, Double, Double](f)     # TypeBuilder error
```

So it is the host, not our code. `DelegateSurface` is sound C# with 12 passing tests — it is
simply not reachable from Python here.

## Files

| File | State |
| --- | --- |
| `explore_tin_api.py` | **Works.** Uses no delegates and no project DLL — pure Civil 3D introspection. Reports a live `TinSurface`'s real triangle/vertex API, which is what blocks whole-surface 2D grading in `SurfaceGrader`. Still worth running. |
| `smoke_test.py` | Blocked at the delegate line. Everything above it is confirmed working. |
| `gradingtool_bridge.py` | Blocked at the same line. |

## If you want to revive this

Two routes, neither taken:

- **Pass data, not functions.** A `GridSurface : ISurface` in Core, built from a sampled
  elevation array that Python fills with `FindElevationAtXY` calls. Arrays marshal fine without
  `Reflection.Emit`. Cost: a resampled grid approximates Civil 3D's TIN and softens breaklines,
  where the callback would have been exact.
- **A newer host.** Civil 3D 2025.1+ moved Dynamo to .NET 8 / Dynamo Core 3.x / PythonNet3. If
  that host permits `Reflection.Emit`, the existing scripts may work as written once the
  byte-loading fix is in — which it now is.

## The one trap worth remembering

If you load any DLL from a Dynamo Python node, **byte-load it**:

```python
from System.IO import File
from System.Reflection import Assembly
Assembly.Load(File.ReadAllBytes(path))
```

`Assembly.LoadFrom` fails on anything Windows has marked as downloaded, which is every DLL that
arrives by email, browser, or chat. The error (`0x80131515`, "Operation is not supported") names
nothing about blocking, so it is a genuinely hard hour to lose.
