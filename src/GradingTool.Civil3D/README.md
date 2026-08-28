# GradingTool.Civil3D

The Civil 3D **2024** add-in: command entry points and the bridge from live drawing
objects to the tested engine in `GradingTool.Core`. **Windows + Civil 3D 2024 only** —
it cannot build or run on the macOS dev box, and is intentionally **not** in
`GradingTool.sln` so the Mac build stays green.

Civil 3D 2024 hosts **.NET Framework 4.8**, so this project targets `net48`.
(Civil 3D 2025+ moved to .NET 8 — if you upgrade, retarget to `net8.0-windows` and
update the assembly paths.)

## Building (on the Windows machine)

Only the **.NET SDK 8** is required — no Visual Studio. The project pulls the `net48` targeting
pack from NuGet (`Microsoft.NETFramework.ReferenceAssemblies`), so `dotnet build` works on a
machine with nothing but the SDK and Civil 3D installed.

1. Confirm the Civil 3D 2024 managed assemblies are at the default path
   `C:\Program Files\Autodesk\AutoCAD 2024\` (acmgd.dll, acdbmgd.dll, accoremgd.dll,
   AecBaseMgd.dll, AeccDbMgd.dll). If your install is elsewhere, pass it:
   ```
   dotnet build src/GradingTool.Civil3D -p:AcadDir="D:\Your\AutoCAD 2024\"
   ```
2. Or add it to the solution and open in Visual Studio 2022:
   ```
   dotnet sln add src/GradingTool.Civil3D/GradingTool.Civil3D.csproj
   ```

The Autodesk references use `Private=false` (CopyLocal off) — Civil 3D already has these
loaded in-process, and shipping copies causes assembly-load conflicts.

## Loading and running

1. In Civil 3D 2024: `NETLOAD`, pick `GradingTool.Civil3D.dll`.
2. Commands:
   - **GRADEPROBE** — pick a TIN surface, then pick points; reports elevation + slope at
     each. Proves the `Civil3DSurface` → `ISurface` bridge on a live surface.
   - **GRADELINE** — pick a TIN surface and a 3D polyline (proposed feature line), choose
     the surface type, and run the grading solver; reports findings and offers to write the
     solved elevations back to the polyline. Endpoints are treated as fixed tie-ins.

## What is wired, and what is next

- `Civil3DSurface` implements `ISurface` on `TinSurface.FindElevationAtXY` + `GeometricExtents`.
  This is the production half of the hybrid: the solver/graders were unit-tested against the
  managed `TinSurface` on the dev box and run unchanged here against the real Civil 3D surface.
- The **1D grading solver** runs fully through `GRADELINE`.
- **TODO (next integration step):** the **2D `SurfaceGrader`** iterates a *managed* TinSurface's
  triangles, so to grade a whole Civil 3D surface in 2D it needs a small adapter that
  enumerates Civil 3D's own triangles (`TinSurface` triangle/vertex API) and feeds their
  slope + centroid into the same rule check. Left as a follow-up because that triangle-
  enumeration API could not be compile-verified on the dev box. **Run
  `dynamo/explore_tin_api.py` to settle it** - a Python node can ask a live surface what its
  API is, which is the thing a compiler on the dev box cannot do.
- The slope stencil moved to `SlopeStencil` in Core, so `Civil3DSurface` no longer carries its
  own copy and the math is finally covered by the test suite.

## The Dynamo bridge was tried, and does not work

`dynamo/` holds an attempt to skip this project entirely by driving the engine from a Dynamo
Python node. It failed: Dynamo's CPython3 host blocks the `Reflection.Emit` that PythonNet needs
to build a .NET delegate from a Python function, so `DelegateSurface` cannot be constructed
there. See `dynamo/README.md` for the four interop problems found and which three were solved.

This project is therefore the only working path to a live drawing, not merely the shipping form.
One script survives and is still worth running: `dynamo/explore_tin_api.py` needs no delegates
and answers the triangle-enumeration question in the TODO below.

## Not compiled on the dev box

Everything here is written against the Civil 3D 2024 API but has **not been compiled** — no
Civil 3D or .NET Framework toolchain exists on the macOS dev machine. `FindElevationAtXY`,
`GeometricExtents`, `Polyline3d` vertex iteration, and the editor prompt APIs are long-stable,
but expect to resolve minor issues on the first Windows build. The engine it calls
(`GradingTool.Core`) is fully unit-tested (77 tests green).
