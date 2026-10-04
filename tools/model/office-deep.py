#!/usr/bin/env python3
"""The office's deep ramp and hall, generated: a square spiral road down a shaft into a hall.

Writes the mesh atlas, its flat-colour textures, the shadow caster, the static sub-objects with their
box colliders into KSAStructuresOffice.xml between its markers, and the layout the code and the garage's
shader pass share. Everything is axis-aligned, because a static object's colliders are.

    ./tools/model/office-deep.py            # from the repository root

The frame is the building's: x up from the levelled ground, y east, z north.
"""
import json
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[2] / "src" / "KSAStructures"

# ---- the layout -------------------------------------------------------------------------------
SHAFT = (25.0, 55.0, -60.0, -30.0)          # inner faces: y0, y1, z0, z1
CORE = (32.0, 48.0, -53.0, -37.0)
TURN = 8.0                                   # what one turn descends
LEG = 2.0                                    # what one leg descends
DEPTH = 54.0
TURNS = 7
SLAB = 0.3
ROOM = (12.0, 82.0, -118.0, -62.0)           # the hall's inner faces
ROOM_TOP = -40.0
DOOR = (48.0, 55.0)                          # east extent of the way from the shaft into the hall
DOOR_TOP = -49.0
COVER = 0.08

y0, y1, z0, z1 = SHAFT
cy0, cy1, cz0, cz1 = CORE
NE = (cy1, y1, cz1, z1)
NW = (y0, cy0, cz1, z1)
SW = (y0, cy0, z0, cz0)
SE = (cy1, y1, z0, cz0)
N_LEG = (cy0, cy1, cz1, z1)
W_LEG = (y0, cy0, cz0, cz1)
S_LEG = (cy0, cy1, z0, cz0)
E_LEG = (cy1, y1, cz0, cz1)

# One pit under the shaft and the hall together: a circle, because an absolute level decal is one, and
# one decal, because a planet has sixteen terrain textures for every mod on it. East, north, radius.
PITS = [(45.0, -78.0, 62.0)]

# ---- geometry ---------------------------------------------------------------------------------
SWATCH = {"wall": 0, "road": 1, "floor": 2, "ceiling": 3, "column": 4, "fixture": 5, "paint": 6, "core": 7}
COLOUR = {"wall": (0.36, 0.36, 0.35), "road": (0.05, 0.05, 0.055), "floor": (0.14, 0.14, 0.15),
          "ceiling": (0.33, 0.33, 0.32), "column": (0.42, 0.42, 0.40), "fixture": (0.92, 0.92, 0.86),
          "paint": (0.75, 0.60, 0.10), "core": (0.28, 0.28, 0.28)}


class Mesh:
    def __init__(self):
        self.p, self.n, self.uv, self.i = [], [], [], []

    def quad(self, a, b, c, d, swatch):
        """Counter-clockwise from outside. Each face has its own corner of a swatch, so every
        triangle has UV area and a UV gradient the engine's tangent frame can use."""
        a, b, c, d = (np.asarray(v, dtype=np.float64) for v in (a, b, c, d))
        normal = np.cross(b - a, c - a)
        length = np.linalg.norm(normal)
        if length < 1e-9:
            return
        normal /= length
        u0 = (swatch % 4) * 0.25 + 0.0625
        v0 = (swatch // 4) * 0.25 + 0.0625
        base = len(self.p)
        self.p += [a, b, c, d]
        self.n += [normal] * 4
        self.uv += [(u0, v0), (u0 + 0.125, v0), (u0 + 0.125, v0 + 0.125), (u0, v0 + 0.125)]
        self.i += [base, base + 1, base + 2, base, base + 2, base + 3]

    def prism(self, ya, yb, za, zb, top, swatch, thick=SLAB, top_swatch=None):
        """A slab over a rectangle whose top is given at its four corners: (ya,za) (yb,za) (yb,zb) (ya,zb)."""
        t = [np.array([top[0], ya, za]), np.array([top[1], yb, za]), np.array([top[2], yb, zb]), np.array([top[3], ya, zb])]
        b = [v - np.array([thick, 0.0, 0.0]) for v in t]
        self.quad(t[0], t[1], t[2], t[3], swatch if top_swatch is None else top_swatch)
        self.quad(b[3], b[2], b[1], b[0], swatch)
        for k in range(4):
            m = (k + 1) % 4
            self.quad(t[m], t[k], b[k], b[m], swatch)

    def box(self, x0, x1, ya, yb, za, zb, swatch, top_swatch=None):
        self.prism(ya, yb, za, zb, [x1] * 4, swatch, thick=x1 - x0, top_swatch=top_swatch)


def leg_top(name, k):
    """A leg's top at its rectangle's four corners, in prism order, and its two ends' levels."""
    if name == "N":      # from the NE landing at -8k down to the west
        hi, lo = -TURN * k, -TURN * k - LEG
        return N_LEG, [lo, hi, hi, lo]
    if name == "W":      # from the NW landing down to the south
        hi, lo = -TURN * k - LEG, -TURN * k - 2 * LEG
        return W_LEG, [lo, lo, hi, hi]
    if name == "S":      # from the SW landing down to the east
        hi, lo = -TURN * k - 2 * LEG, -TURN * k - 3 * LEG
        return S_LEG, [hi, lo, lo, hi]
    hi, lo = -TURN * k - 3 * LEG, -TURN * k - 4 * LEG      # E: from the SE landing down to the north
    return E_LEG, [hi, hi, lo, lo]


structure = Mesh()
caster = Mesh()
boxes = []        # colliders: (name, x0, x1, ya, yb, za, zb)
lamps = []        # (x, y, z, range, intensity)
INSET = 0.08


drawn = 0


def skin():
    """A few millimetres, different for every body drawn: two bodies meeting on one plane z-fight."""
    global drawn
    drawn += 1
    return 0.003 + (drawn * 7 % 23) * 0.0009


def solid(name, x0, x1, ya, yb, za, zb, swatch, top_swatch=None, collide=True):
    e = skin()
    structure.box(x0 - e, x1 + e, ya - e, yb + e, za - e, zb + e, SWATCH[swatch], None if top_swatch is None else SWATCH[top_swatch])
    if collide:
        boxes.append((name, x0, x1, ya, yb, za, zb))
    if min(x1 - x0, yb - ya, zb - za) > 3 * INSET:
        caster.box(x0 + INSET, x1 - INSET, ya + INSET, yb - INSET, za + INSET, zb - INSET, 0)


# The shaft's walls, flush with the ground at the top, and the core the road winds round.
W = 0.6
solid("ShaftW", -DEPTH - 0.5, 0.0, y0 - W, y0, z0 - W, z1 + W, "wall")
solid("ShaftE", -DEPTH - 0.5, 0.0, y1, y1 + W, z0 - W, z1 + W, "wall")
solid("ShaftN", -DEPTH - 0.5, 0.0, y0, y1, z1, z1 + W, "wall")
solid("ShaftS0", -DEPTH - 0.5, 0.0, y0, DOOR[0], z0 - W, z0, "wall")
solid("ShaftS1", DOOR_TOP, 0.0, DOOR[0], DOOR[1], z0 - W, z0, "wall")
solid("Core", -DEPTH - 0.5, 0.0, cy0, cy1, cz0, cz1, "core")

# The road: four landings and four legs a turn, ending on the SE landing at the hall's level.
n = 0
for k in range(TURNS):
    for name, rect, level in (("NE", NE, -TURN * k), ("NW", NW, -TURN * k - LEG), ("SW", SW, -TURN * k - 2 * LEG),
                              ("SE", SE, -TURN * k - 3 * LEG)):
        solid(f"Landing{name}{k}", level - SLAB, level, rect[0], rect[1], rect[2], rect[3], "road")
        # A lamp under whatever is over a landing, where anything is.
        over = min(level + TURN, 0.0)
        if not (k == 0 and name in ("NE", "NW")):
            h = over - SLAB - 0.4 - level
            lamps.append((over - SLAB - 0.4, (rect[0] + rect[1]) / 2, (rect[2] + rect[3]) / 2, round(1.7 * h, 1), round(2.0 * h * h, 0)))

    for name in ("N", "W", "S", "E"):
        if name == "E" and k == TURNS - 1:
            continue
        rect, top = leg_top(name, k)
        e = skin()
        structure.prism(rect[0] - e, rect[1] + e, rect[2] - e, rect[3] + e, [t + e for t in top], SWATCH["road"], thick=SLAB + 2 * e)
        caster.prism(rect[0] + INSET, rect[1] - INSET, rect[2] + INSET, rect[3] - INSET, [t - INSET for t in top], 0,
                     thick=SLAB - 2 * INSET)

        # Colliders in steps, since a collider is a box: 8 cm a step.
        steps = 25
        along_y = name in ("N", "S")
        a, b = (rect[0], rect[1]) if along_y else (rect[2], rect[3])
        first, last = (top[0], top[1]) if along_y else (top[0], top[3])
        for s in range(steps):
            f0, f1 = s / steps, (s + 1) / steps
            level = first + (last - first) * (f0 + f1) / 2
            ya, yb = (a + (b - a) * f0, a + (b - a) * f1) if along_y else (rect[0], rect[1])
            za, zb = (rect[2], rect[3]) if along_y else (a + (b - a) * f0, a + (b - a) * f1)
            boxes.append((f"Leg{name}{k}_{s}", level - SLAB, level, ya, yb, za, zb))
            n += 1

        if not (k == 0 and name in ("N", "W")):
            mid = sum(top) / 4
            over = min(mid + TURN, 0.0)
            h = over - SLAB - 0.4 - mid
            lamps.append((over - SLAB - 0.4, (rect[0] + rect[1]) / 2, (rect[2] + rect[3]) / 2, round(1.7 * h, 1), round(2.0 * h * h, 0)))

# The bottom of the shaft under the last turn, and the roof over the covered half of the first.
solid("ShaftFloor", -DEPTH - SLAB, -DEPTH, cy1, y1, cz0, z1, "road")
solid("DoorFloor", -DEPTH - SLAB, -DEPTH, DOOR[0], DOOR[1], z0 - W, z0, "road")
solid("ShaftRoofS", -SLAB, 0.0, y0, y1, z0, cz0, "ceiling", collide=False)
solid("ShaftRoofE", -SLAB, 0.0, cy1, y1, cz0, cz1, "ceiling", collide=False)

# The hall.
ry0, ry1, rz0, rz1 = ROOM
solid("HallFloor", -DEPTH - SLAB, -DEPTH, ry0 - 1, ry1 + 1, rz0 - 1, z0 - W, "floor")
solid("HallCeiling", ROOM_TOP, ROOM_TOP + 0.5, ry0 - 1, ry1 + 1, rz0 - 1, z0 - W, "ceiling", collide=False)
solid("HallW", -DEPTH - 0.5, ROOM_TOP, ry0 - 1, ry0, rz0 - 1, z0 - W, "wall")
solid("HallE", -DEPTH - 0.5, ROOM_TOP, ry1, ry1 + 1, rz0 - 1, z0 - W, "wall")
solid("HallS", -DEPTH - 0.5, ROOM_TOP, ry0, ry1, rz0 - 1, rz0, "wall")
solid("HallN0", -DEPTH - 0.5, ROOM_TOP, ry0, DOOR[0], rz1, z0 - W, "wall")
solid("HallN1", -DEPTH - 0.5, ROOM_TOP, DOOR[1], ry1, rz1, z0 - W, "wall")
solid("HallN2", DOOR_TOP, ROOM_TOP, DOOR[0], DOOR[1], rz1, z0 - W, "wall")
COLUMNS = [(y, z) for y in (26.0, 40.0, 54.0, 68.0) for z in (-76.0, -90.0, -104.0)]
for c, (y, z) in enumerate(COLUMNS):
    solid(f"Column{c}", -DEPTH, ROOM_TOP, y - 0.5, y + 0.5, z - 0.5, z + 0.5, "column")
for y in (19.0, 33.0, 47.0, 61.0, 75.0):
    for z in (-69.0, -83.0, -97.0, -111.0):
        e = skin()
        structure.box(ROOM_TOP - 0.12 - e, ROOM_TOP - 0.02, y - 1.2 - e, y + 1.2 + e, z - 0.2 - e, z + 0.2 + e, SWATCH["fixture"])
        h = ROOM_TOP - 0.5 + DEPTH
        lamps.append((ROOM_TOP - 0.5, y, z, 22.0, round(1.3 * h * h, 0)))
# Bays painted on the hall's floor.
for y in np.arange(16.0, 80.0, 3.0):
    for z in (-114.0, -66.5):
        structure.box(-DEPTH + 0.004, -DEPTH + 0.02 + skin(), y - 0.07, y + 0.07, z - 2.5, z + 2.5, SWATCH["paint"])

# The cover: ground over everything dug, open over the first two legs of the road.
cover = Mesh()
cut = [(y0, y1, cz1, z1), (y0, cy0, cz0, cz1)]
ys = sorted({p[0] + d for p in PITS for d in (-p[2] - 3, p[2] + 3)} | {y0, cy0, cy1, y1})
zs = sorted({p[1] + d for p in PITS for d in (-p[2] - 3, p[2] + 3)} | {z0, cz0, cz1, z1})
cover_cells = []
for a, b in zip(ys, ys[1:]):
    for c, d in zip(zs, zs[1:]):
        my, mz = (a + b) / 2, (c + d) / 2
        near = any(abs(my - p[0]) < p[2] + 3 and abs(mz - p[1]) < p[2] + 3 for p in PITS)
        if not near or any(r[0] <= my <= r[1] and r[2] <= mz <= r[3] for r in cut):
            continue
        cover.quad((COVER, a, c), (COVER, b, c), (COVER, b, d), (COVER, a, d), SWATCH["wall"])
        caster.box(-0.26, -0.10, a, b, c, d, 0)
        cover_cells.append((a, b, c, d))

# ---- files ------------------------------------------------------------------------------------
def glb(path, meshes, material=False):
    blob = bytearray()
    views, accessors, gl_meshes, nodes = [], [], [], []

    def view(data, target=None):
        while len(blob) % 4:
            blob.append(0)
        v = {"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)}
        if target:
            v["target"] = target
        blob.extend(data)
        views.append(v)
        return len(views) - 1

    for name, m in meshes:
        p = np.asarray(m.p, dtype=np.float32)
        nm = np.asarray(m.n, dtype=np.float32)
        uv = np.asarray(m.uv, dtype=np.float32)
        idx = np.asarray(m.i, dtype=np.uint32)
        base = len(accessors)
        accessors += [
            {"bufferView": view(p.tobytes(), 34962), "componentType": 5126, "count": len(p), "type": "VEC3",
             "min": p.min(0).tolist(), "max": p.max(0).tolist()},
            {"bufferView": view(nm.tobytes(), 34962), "componentType": 5126, "count": len(nm), "type": "VEC3"},
            {"bufferView": view(uv.tobytes(), 34962), "componentType": 5126, "count": len(uv), "type": "VEC2"},
            {"bufferView": view(idx.tobytes(), 34963), "componentType": 5125, "count": len(idx), "type": "SCALAR"},
        ]
        primitive = {"attributes": {"POSITION": base, "NORMAL": base + 1, "TEXCOORD_0": base + 2}, "indices": base + 3}
        if material:
            primitive["material"] = 0
        gl_meshes.append({"name": name, "primitives": [primitive]})
        nodes.append({"name": name, "mesh": len(gl_meshes) - 1})

    doc = {"asset": {"version": "2.0", "generator": "office-deep.py"}, "scene": 0,
           "scenes": [{"nodes": list(range(len(nodes)))}], "nodes": nodes, "meshes": gl_meshes,
           "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(blob)}]}
    if material:
        doc["materials"] = [{"name": "caster", "pbrMetallicRoughness": {"baseColorFactor": [0.5, 0.5, 0.5, 1.0]}}]

    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    while len(blob) % 4:
        blob.append(0)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 28 + len(text) + len(blob)))
        f.write(struct.pack("<II", len(text), 0x4E4F534A) + text)
        f.write(struct.pack("<II", len(blob), 0x004E4942) + bytes(blob))


glb(ROOT / "Meshes" / "KSAOfficeDeep.glb", [("KSAOffice_Deep", structure), ("KSAOffice_DeepTop", cover)])
glb(ROOT / "Meshes" / "KSAOffice_DeepCaster.glb", [("KSAOffice_DeepCaster", caster)], material=True)

# Flat colours, sixteen swatches; a material's textures are its own files, because KSA loads a
# texture path for the first material that names it and hands the rest white.
srgb = lambda c: int(round(255 * (12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055)))
img = np.zeros((64, 64, 3), dtype=np.uint8)
img[:] = srgb(0.36)
for name, s in SWATCH.items():
    r, c = s // 4, s % 4
    img[r * 16:(r + 1) * 16, c * 16:(c + 1) * 16] = [srgb(v) for v in COLOUR[name]]
Image.fromarray(img).save(ROOT / "Textures" / "KSAOffice_Deep_Diffuse.png")
Image.fromarray(np.full((8, 8, 3), (255, 217, 0), dtype=np.uint8)).save(ROOT / "Textures" / "KSAOffice_Deep_AoRoughMetal.png")
Image.fromarray(np.full((8, 8, 3), (128, 128, 255), dtype=np.uint8)).save(ROOT / "Textures" / "KSAOffice_Deep_Normal.png")

# The static sub-objects, between the markers in the office's asset file.
lines = ['  <PbrMaterial Id="KSAOffice_DeepMaterial">',
         '    <Diffuse Path="Textures/KSAOffice_Deep_Diffuse.png" Category="Vessel" />',
         '    <Normal Path="Textures/KSAOffice_Deep_Normal.png" Category="Vessel" />',
         '    <AoRoughMetal Path="Textures/KSAOffice_Deep_AoRoughMetal.png" Category="Vessel" />',
         '  </PbrMaterial>',
         '  <StaticSubObject Id="KSAOffice_DeepBody">',
         '    <PartModel Id="KSAOffice_DeepBody_Model">',
         '      <Mesh Id="KSAOffice_Deep" />',
         '      <Material Id="KSAOffice_DeepMaterial" />',
         '    </PartModel>',
         '    <Collider Id="KSAOffice_Deep_Collider">']
for name, x0, x1, ya, yb, za, zb in boxes:
    lines += [f'      <Box Id="Deep{name}">',
              f'        <LocationAsmb X="{(x0 + x1) / 2:.4f}" Y="{(ya + yb) / 2:.4f}" Z="{(za + zb) / 2:.4f}" />',
              f'        <LengthX M="{x1 - x0:.4f}" />', f'        <LengthY M="{yb - ya:.4f}" />',
              f'        <LengthZ M="{zb - za:.4f}" />', '      </Box>']
lines += ['    </Collider>', '  </StaticSubObject>',
          '  <StaticSubObject Id="KSAOffice_DeepGround">',
          '    <PartModel Id="KSAOffice_DeepGround_Model">',
          '      <Mesh Id="KSAOffice_DeepTop" />',
          '      <Material Id="KSAOffice_DeepMaterial" />',
          '      <Terrain>true</Terrain>',
          '    </PartModel>',
          '  </StaticSubObject>']
xml_path = ROOT / "KSAStructuresOffice.xml"
xml = xml_path.read_text()
begin, end = "  <!-- office-deep.py: begin -->\n", "  <!-- office-deep.py: end -->\n"
if begin not in xml:
    sys.exit("KSAStructuresOffice.xml has no office-deep.py markers")
xml = xml[:xml.index(begin) + len(begin)] + "\n".join(lines) + "\n" + xml[xml.index(end):]
xml_path.write_text(xml)

layout = {"shaft": SHAFT, "core": CORE, "room": ROOM, "room_top": ROOM_TOP, "depth": DEPTH, "turn": TURN, "leg": LEG,
          "turns": TURNS, "door": DOOR, "pits": PITS, "lamps": lamps, "columns": COLUMNS,
          "cover": [min(c[0] for c in cover_cells), max(c[1] for c in cover_cells),
                    min(c[2] for c in cover_cells), max(c[3] for c in cover_cells)]}
(Path(__file__).resolve().parent / "office-deep.json").write_text(json.dumps(layout, indent=1))
rows = "\n".join(f"        ({x:.2f}, {y:.2f}, {z:.2f}, {r:.1f}f, {i:.0f}f)," for x, y, z, r, i in lamps)
(ROOT / "Sim" / "OfficeDeepLayout.cs").write_text(f"""namespace KSAStructures;

// Generated by tools/model/office-deep.py, with the mesh and the colliders these belong to.
public static partial class OfficeDeep
{{
    /// <summary>Each lamp: up, east and north in the building's frame, its range and its intensity.</summary>
    public static readonly (double X, double Y, double Z, float Range, float Intensity)[] Lamps =
    [
{rows}
    ];
}}
""")
print(f"structure {len(structure.i) // 3} triangles, cover {len(cover.i) // 3}, caster {len(caster.i) // 3}, "
      f"{len(boxes)} colliders ({n} of them steps), {len(lamps)} lamps")
