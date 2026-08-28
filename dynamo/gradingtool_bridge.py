"""
BLOCKED - kept as the record of a dead end. Read dynamo/README.md before using this.

GRADELINE as a Dynamo Python node. The design was: hand DelegateSurface a Python callback so a
graph could drive the tested engine with no Windows build. Live testing in Civil 3D 2024 killed
it - Dynamo's CPython3 host blocks the Reflection.Emit that PythonNet needs to build a .NET
delegate from a Python function, so `System.Func[...](callable)` raises:

    Constructor on type 'System.Reflection.Emit.TypeBuilder' not found.

Reproduced in seven lines with no Civil 3D and no project DLL, so it is the host, not this code.
The live path is the compiled add-in: src/GradingTool.Civil3D, command GRADELINE.

What this script proved DOES work, and is worth keeping:
  - loading the netstandard2.0 engine into Civil 3D's .NET Framework host (see load_engine)
  - unwrapping a Select Object node's wrapper via InternalObjectId
  - reading a Polyline3d's vertices inside a transaction

    IN[0]  surface       Civil 3D TIN surface      <- Select Object node
    IN[1]  polyline      3D polyline to grade      <- Select Object node
    IN[2]  surface_use   e.g. "StandardParking"    <- String / Choose node
    IN[3]  write_back    True to update the polyline elevations  <- Boolean
    IN[4]  core_dll      full path to GradingTool.Core.dll       <- File Path node

    OUT    [summary, [findings], [solved elevations]]
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
from System.IO import File
from System.Reflection import Assembly


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


load_engine(IN[4])

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

    THIS IS THE LINE THAT CANNOT RUN on Dynamo's CPython3: constructing System.Func from a
    Python callable needs Reflection.Emit, which the host blocks. The C# equivalent in the
    add-in (Civil3DSurface) has no such problem - it implements ISurface directly, with no
    delegate to synthesise.
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
