"""
BLOCKED - kept as the record of a dead end. Read dynamo/README.md before using this.

This script gets as far as loading the engine and then dies, in Civil 3D 2024 with Dynamo's
CPython3 engine, on the DelegateSurface construction below:

    PythonEvaluator.Evaluate operation failed.
    Constructor on type 'System.Reflection.Emit.TypeBuilder' not found.

Dynamo's CPython3 host blocks the Reflection.Emit that PythonNet needs to synthesise a .NET
delegate from a Python function, so `System.Func[...](callable)` cannot be constructed at all.
Reproduced in seven lines with no Civil 3D and no project DLL involved, so it is the host, not
this code. The live path is the compiled add-in - see src/GradingTool.Civil3D.

Everything ABOVE the delegate line is confirmed working against a real drawing, and is the
useful part to keep: the assembly load, the surface unwrap, the transaction.

    IN[0]  surface    Civil 3D TIN surface          <- Select Object node
    IN[1]  core_dll   path to GradingTool.Core.dll  <- File Path node
"""

import clr
import System
from System.IO import File
from System.Reflection import Assembly

clr.AddReference("AcDbMgd")
clr.AddReference("AcMgd")
clr.AddReference("AecBaseMgd")
clr.AddReference("AeccDbMgd")

from Autodesk.AutoCAD.ApplicationServices import Application
from Autodesk.AutoCAD.DatabaseServices import OpenMode


def load_engine(path):
    """Load GradingTool.Core, whatever the engine and whatever Windows thinks of the file.

    Three ways to get this wrong, all found the hard way:
      - clr.AddReferenceToFileAndPath is IronPython-only; PythonNet has no such attribute.
      - PythonNet 3 dropped sys.path probing, so clr.AddReference("GradingTool.Core") fails.
      - Assembly.LoadFrom refuses a file carrying Windows' mark-of-the-web with
        HRESULT 0x80131515, which is every DLL anyone downloads.
    Reading the bytes and loading those sidesteps the zone check entirely.
    """
    if hasattr(clr, "AddReferenceToFileAndPath"):   # IronPython
        clr.AddReferenceToFileAndPath(path)
    else:                                            # PythonNet
        Assembly.Load(File.ReadAllBytes(path))


load_engine(IN[1])
from GradingTool.Surface import DelegateSurface

report = []

document = Application.DocumentManager.MdiActiveDocument
with document.TransactionManager.StartTransaction() as transaction:
    surface_id = IN[0].InternalObjectId if hasattr(IN[0], "InternalObjectId") else IN[0]
    surface = transaction.GetObject(surface_id, OpenMode.ForRead)

    def elevation_at(x, y):
        try:
            return surface.FindElevationAtXY(x, y)
        except Exception:
            return DelegateSurface.Outside

    extents = surface.GeometricExtents

    # ---- everything below this line is unreachable on CPython3 --------------------------
    wrapped = DelegateSurface(
        surface.Name,
        System.Func[System.Double, System.Double, System.Double](elevation_at),
        extents.MinPoint.X, extents.MinPoint.Y,
        extents.MaxPoint.X, extents.MaxPoint.Y,
        extents.MinPoint.Z, extents.MaxPoint.Z,
        1.0,
    )

    mid_x = (extents.MinPoint.X + extents.MaxPoint.X) / 2.0
    mid_y = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0

    report.append("surface: %s" % wrapped.Name)
    report.append("extents: %s" % (wrapped.Extents,))
    report.append("elevation range: %s" % (wrapped.ElevationRange,))
    report.append("centre (%.3f, %.3f)" % (mid_x, mid_y))

    elevation = wrapped.ElevationAt(mid_x, mid_y)
    report.append("  elevation: %s" % ("outside the surface" if elevation is None else elevation))

    slope = wrapped.SlopeAt(mid_x, mid_y)
    if slope is None:
        report.append("  slope: none (stencil ran off the surface)")
    else:
        report.append("  slope: %.3f%% toward %.1f deg" % (slope.SlopePct, slope.AspectDegrees))

    far = wrapped.ElevationAt(extents.MaxPoint.X + 10000.0, extents.MaxPoint.Y + 10000.0)
    report.append("far-away point reads as outside: %s" % (far is None))

    transaction.Commit()

OUT = report
