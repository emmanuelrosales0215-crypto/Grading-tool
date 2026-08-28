"""
Probe a live Civil 3D TIN surface's triangle API - the answer that unblocks 2D grading.

src/GradingTool.Civil3D/README.md records the blocker: SurfaceGrader iterates a *managed*
TinSurface's triangles, so grading a whole Civil 3D surface in 2D needs an adapter that
enumerates Civil 3D's own triangles. That adapter was never written because the triangle
enumeration API "could not be compile-verified" on a machine without Civil 3D.

This script answers it by asking the object itself, which is a thing a Dynamo Python node can
do and a compiler on a Mac cannot. Run it once, read the output, then write the adapter
against what it actually reports rather than against a guess.

    IN[0]  surface   Civil 3D TIN surface   <- Select Object node
    OUT    a report: candidate members, and what one triangle actually looks like

Throwaway diagnostic - it reads nothing and writes nothing, and is not part of the tool.
"""

import clr

clr.AddReference("AcDbMgd")
clr.AddReference("AcMgd")
clr.AddReference("AecBaseMgd")
clr.AddReference("AeccDbMgd")

from Autodesk.AutoCAD.ApplicationServices import Application
from Autodesk.AutoCAD.DatabaseServices import OpenMode

INTERESTING = ("triangle", "vertex", "vertices", "edge", "face", "point", "slope", "elevation")

report = []

document = Application.DocumentManager.MdiActiveDocument
with document.TransactionManager.StartTransaction() as transaction:
    surface_id = IN[0].InternalObjectId if hasattr(IN[0], "InternalObjectId") else IN[0]
    surface = transaction.GetObject(surface_id, OpenMode.ForRead)

    report.append("type: %s" % surface.GetType().FullName)

    members = sorted(m for m in dir(surface) if not m.startswith("_"))
    report.append("--- members mentioning %s ---" % ", ".join(INTERESTING))
    report.extend(m for m in members if any(word in m.lower() for word in INTERESTING))

    # Whatever the enumeration turns out to be called, look at one element: the adapter needs
    # to know how a triangle exposes its three vertices and their coordinates.
    for accessor in ("GetTriangles", "Triangles"):
        if not hasattr(surface, accessor):
            continue
        try:
            member = getattr(surface, accessor)
            triangles = member(True) if callable(member) else member
            first = None
            for triangle in triangles:
                first = triangle
                break
            if first is None:
                report.append("%s: present but empty" % accessor)
                continue
            report.append("--- %s -> %s ---" % (accessor, first.GetType().FullName))
            report.extend("  " + m for m in sorted(
                m for m in dir(first) if not m.startswith("_")))
        except Exception as error:
            report.append("%s raised: %s" % (accessor, error))

    transaction.Commit()

OUT = report
