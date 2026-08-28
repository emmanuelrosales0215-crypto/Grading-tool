"""
Smallest possible first run: prove the interop before trusting the engineering.

Wraps a live Civil 3D surface in a DelegateSurface and reads elevation and slope at its
centre. No solver, no feature line, no write-back - so if this fails, the problem is loading
the DLL or talking to the surface, and if it passes, anything that fails afterwards is the
grading logic or the polyline handling. Run this before gradingtool_bridge.py.

    IN[0]  surface    Civil 3D TIN surface   <- Select Object node
    IN[1]  core_dll   path to GradingTool.Core.dll  <- File Path node
    OUT    a few lines you can eyeball against Civil 3D's own surface properties
"""

import clr
import System

clr.AddReference("AcDbMgd")
clr.AddReference("AcMgd")
clr.AddReference("AecBaseMgd")
clr.AddReference("AeccDbMgd")

from Autodesk.AutoCAD.ApplicationServices import Application
from Autodesk.AutoCAD.DatabaseServices import OpenMode

clr.AddReferenceToFileAndPath(IN[1])
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

    # Must read as outside - a point far beyond the extents proves the NaN convention is
    # surviving the Python -> .NET hop, which is the one thing most likely to be wrong.
    far = wrapped.ElevationAt(extents.MaxPoint.X + 10000.0, extents.MaxPoint.Y + 10000.0)
    report.append("far-away point reads as outside: %s" % (far is None))

    transaction.Commit()

OUT = report
