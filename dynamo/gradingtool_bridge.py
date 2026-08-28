"""
GRADELINE, as a Dynamo for Civil 3D Python Script node.

Paste this into a Python Script node with five inputs. It does what the GRADELINE command
does in src/GradingTool.Civil3D/GradingCommands.cs - read a TIN surface and a 3D polyline,
run the tested GradingTool.Core solver, report findings, optionally write the solved
elevations back - but it needs no Windows build: GradingTool.Core is netstandard2.0 and
references no Autodesk assembly, so Dynamo can load it straight from a `dotnet build` output
produced on any machine.

    IN[0]  surface       Civil 3D TIN surface      <- Select Object node
    IN[1]  polyline      3D polyline to grade      <- Select Object node
    IN[2]  surface_use   e.g. "StandardParking"    <- String / Choose node
    IN[3]  write_back    True to update the polyline elevations  <- Boolean
    IN[4]  core_dll      full path to GradingTool.Core.dll       <- File Path node

    OUT    [summary, [findings], [solved elevations]]

Wiring, traps and the 2024-vs-2025 runtime split are covered in dynamo/README.md. Read that
before the first run - in particular, set Dynamo's Geometry Scaling to Medium.
"""

import clr
import System

# --- Civil 3D / AutoCAD -------------------------------------------------------------------
# Case-insensitive on Windows; these names are stable across the releases this targets.
clr.AddReference("AcDbMgd")
clr.AddReference("AcMgd")
clr.AddReference("AecBaseMgd")
clr.AddReference("AeccDbMgd")

from Autodesk.AutoCAD.ApplicationServices import Application
from Autodesk.AutoCAD.DatabaseServices import OpenMode, Polyline3d, PolylineVertex3d
from Autodesk.AutoCAD.Geometry import Point3d as AcadPoint3d
from Autodesk.Civil.DatabaseServices import TinSurface as C3dTinSurface

# --- The engine ---------------------------------------------------------------------------
core_dll = IN[4]
clr.AddReferenceToFileAndPath(core_dll)

from GradingTool import AdaComplianceStandards, ConservativeGradingRules
from GradingTool.Geometry import Point3d as GtPoint3d
from GradingTool.Grading import FeatureLine, GradingSolver, Station
from GradingTool.Surface import DelegateSurface


def object_id(dynamo_object):
    """Unwrap the ObjectId from whatever a Select Object node handed us.

    Dynamo wraps drawing objects; the wrapper exposes InternalObjectId. Accept a raw
    ObjectId too, so the node still works if it is fed one directly.
    """
    for attribute in ("InternalObjectId", "InternalObjectID"):
        if hasattr(dynamo_object, attribute):
            return getattr(dynamo_object, attribute)
    if hasattr(dynamo_object, "ObjectId"):
        return dynamo_object.ObjectId
    return dynamo_object


def wrap_surface(c3d_surface, stencil_ft=1.0):
    """Present a live Civil 3D surface to the engine as an ISurface.

    DelegateSurface exists for exactly this: it takes a plain callback rather than
    requiring a compiled adapter class, so the interop can live in Python. The NaN
    convention matters - FindElevationAtXY *throws* outside the surface, and the engine
    needs that turned into "no data here", not an exception.
    """
    def elevation_at(x, y):
        try:
            return c3d_surface.FindElevationAtXY(x, y)
        except Exception:
            return DelegateSurface.Outside

    extents = c3d_surface.GeometricExtents
    return DelegateSurface(
        c3d_surface.Name,
        System.Func[System.Double, System.Double, System.Double](elevation_at),
        extents.MinPoint.X, extents.MinPoint.Y,
        extents.MaxPoint.X, extents.MaxPoint.Y,
        extents.MinPoint.Z, extents.MaxPoint.Z,
        stencil_ft,
    )


def read_feature_line(polyline, transaction, use):
    """Polyline vertices -> engine stations, endpoints pinned as tie-ins.

    Same convention as GradingCommands.GradeLine: the first and last vertices are fixed
    controls, everything between them is the solver's to move.
    """
    vertex_ids = [vertex_id for vertex_id in polyline]
    stations = []
    for index, vertex_id in enumerate(vertex_ids):
        vertex = transaction.GetObject(vertex_id, OpenMode.ForRead)
        position = vertex.Position
        is_endpoint = index == 0 or index == len(vertex_ids) - 1
        stations.append(
            Station(GtPoint3d(position.X, position.Y, position.Z), is_endpoint))
    return vertex_ids, FeatureLine(polyline.Handle.ToString(), use, stations)


surface_input, polyline_input, use_name, write_back = IN[0], IN[1], IN[2], bool(IN[3])
# getattr rather than Enum.Parse: fetching the CLR Type object for a *nested* enum
# differs between the IronPython and PythonNet engines, but member lookup does not.
use = getattr(AdaComplianceStandards.SurfaceUse, use_name)

document = Application.DocumentManager.MdiActiveDocument
database = document.Database

findings = []
elevations = []

with document.LockDocument():
    with database.TransactionManager.StartTransaction() as transaction:
        surface = transaction.GetObject(object_id(surface_input), OpenMode.ForRead)
        if not isinstance(surface, C3dTinSurface):
            raise TypeError("IN[0] must be a TIN surface, got %s" % type(surface).__name__)

        polyline = transaction.GetObject(object_id(polyline_input), OpenMode.ForRead)
        if not isinstance(polyline, Polyline3d):
            raise TypeError("IN[1] must be a 3D polyline, got %s" % type(polyline).__name__)

        vertex_ids, line = read_feature_line(polyline, transaction, use)

        # Municipality rules are left at None deliberately: loading a config pulls in
        # System.Text.Json, which is a nuisance to satisfy inside Civil 3D 2024's .NET
        # Framework host. See "Known trap" in dynamo/README.md before changing this.
        result = GradingSolver(wrap_surface(surface), ConservativeGradingRules()).Solve([line])

        summary = result.Summary()
        findings = [finding.ToString() for finding in result.Findings]
        elevations = [station.Point.Z for station in line.Stations]

        if write_back:
            for index, vertex_id in enumerate(vertex_ids):
                vertex = transaction.GetObject(vertex_id, OpenMode.ForWrite)
                point = line.Stations[index].Point
                vertex.Position = AcadPoint3d(point.X, point.Y, point.Z)
            summary += "  [elevations written back]"

        transaction.Commit()

OUT = [summary, findings, elevations]
