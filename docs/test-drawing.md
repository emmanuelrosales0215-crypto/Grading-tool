# The test drawing

A five-minute synthetic drawing for exercising the add-in against known answers. Deliberately
trivial: every number below can be checked by hand, so a wrong result is unambiguous.

Use an **imperial** template (`_AutoCAD Civil 3D (Imperial) NCS.dwt`) — the engine works in feet
throughout (`RetainingWallTriggerFt`, elevations, stencil half-steps).

## 1. Five points

`POINT`, entering each coordinate in turn:

```
0,0,100
200,0,104
0,200,100
200,200,104
100,100,102
```

A flat plane tilted **2% to the east**. Z climbs with X, so downhill is due west — bearing 270°.

## 2. The surface

1. Prospector → right-click **Surfaces** → **Create Surface**
2. Type **TIN surface**, name **`EG`** → OK
3. Expand `Surfaces` → `EG` → `Definition`
4. Right-click **Drawing Objects** → **Add** → object type **Points** → select all five → Enter

## 3. The polyline to grade

`3DPOLY`, four vertices:

```
20,100,100
60,100,106
100,100,101
140,100,103
```

Deliberately bad but **solvable**: the first segment climbs 6 ft over 40 ft = **15%**, far past
the 5% parking limit, while the two endpoints are only 3 ft apart over 120 ft (2.5% overall).
Since the solver pins the endpoints and moves only interior stations, a legal answer exists.

## 4. An infeasible polyline (optional but worth it)

```
20,120,100
80,120,106
140,120,112
```

Endpoints 12 ft apart over 120 ft = **10% overall**, both pinned. No legal answer exists. The
solver must report this **infeasible** rather than inventing a result — that is the safety
behaviour worth confirming, and it is easy to get wrong.

## Expected results

**`GRADEPROBE`** at the surface centre (100, 100):

| | |
| --- | --- |
| Elevation | **102** |
| Slope | **2%** toward **270°** |

**`GRADELINE`** on the polyline from §3, surface type `StandardParking`:

- Findings citing running slope on the 15% first segment
- Solved elevations where every segment is ≤5% — check by hand:
  `(z2 - z1) / 40 x 100` for each consecutive pair
- First and last stations still exactly **100** and **103**; they are fixed tie-ins

**`GRADELINE`** on the polyline from §4: reported infeasible.
