#!/usr/bin/env python3
"""
Checks the mod's part XML before it ever reaches the game.

A bad asset Id or a mistyped texture path is a *silent* failure in-game: the part renders
untextured, or invisible, or not at all, with nothing in any log to say why. This walks the
XML and verifies, among other things:

  1. every SubPart InstanceOf resolves, against our own file or against Core's library
  2. every Material Id resolves, likewise
  3. every Mesh Id exists in the mesh atlas the file declares
  4. every file path (<MeshAtlas>, <GltfFile>, <Shader>, a material's textures) is actually there
  5. every static object's SubObject names a StaticSubObject declared here
  6. no texture file is named by two materials, since KSA loads it for only one of them
  7. every asset Id and texture path the C# names is declared here

Check 3 is the one that earns its keep -- the mesh names come out of Blender, and nothing
else in the toolchain would notice a rename.

    ./tools/validate-parts.py
    KSA_DIR=/path/to/KSA ./tools/validate-parts.py

Exits non-zero if anything is unresolved or the XML is malformed.
"""

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from importlib import import_module
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent / "model"))
meshinfo = import_module("meshinfo")

REPO = Path(__file__).resolve().parent.parent
MOD = REPO / "src" / "KSAStructures"
KSA_DIR = Path(os.environ.get("KSA_DIR", "/mnt/c/Program Files/Kitten Space Agency"))
CORE = KSA_DIR / "Content" / "Core"

TEXTURE_TAGS = ("Diffuse", "Normal", "AoRoughMetal", "Emissive", "Alpha")

# What this mod's own Ids start with: one of these that is not declared here is declared nowhere.
OWN_PREFIXES = ("KSAStructures", "KSAOffice_", "KSArmory_Prefab_Office")


def exists_case_exact(path):
    """True only if the file exists with exactly this name.

    plain is_file() is not enough: Windows and macOS filesystems are case-insensitive, so a
    `Textures/airdefence_diffuse.png` typo loads there and fails on Linux, where KSA also runs.
    Comparing against the real directory listing catches it on any platform.
    """
    if not path.is_file():
        return False
    return path.name in {entry.name for entry in path.parent.iterdir()}


def collect_core_ids(core_dir):
    """Maps element tag -> set of declared Ids across every Core asset XML."""
    declared = {}
    for path in sorted(core_dir.glob("*.xml")):
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as exc:
            print(f"  skipping unparseable {path.name}: {exc}", file=sys.stderr)
            continue
        for el in root.iter():
            ident = el.get("Id")
            if ident:
                declared.setdefault(el.tag, set()).add(ident)
    return declared


def atlas_mesh_names(glb_path):
    """Mesh names inside a .glb, or None if it cannot be read."""
    try:
        gltf = meshinfo.read_glb_json(str(glb_path))
    except Exception as exc:
        print(f"  could not read {glb_path.name}: {exc}", file=sys.stderr)
        return None
    return {m.get("name") for m in gltf.get("meshes", [])}


def check_file(path, core_subparts, core_materials, core_substances):
    """Returns (problems, references checked) for one asset XML."""
    problems = checked = 0

    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as exc:
        print(f"  MALFORMED: {exc}", file=sys.stderr)
        return 1, 0

    local_subparts = {el.get("Id") for el in root.iter("SubPart") if el.get("Id")}
    local_materials = {el.get("Id") for el in root.iter("PbrMaterial") if el.get("Id")}

    # Paths are resolved against the XML's own directory, which is also the mod root: the asset XML
    # sits there so the two readings of a relative path cannot disagree.
    meshes_in_atlas = set()
    for el in root.iter("MeshAtlas"):
        checked += 1
        atlas = path.parent / el.get("Path", "")
        if not exists_case_exact(atlas):
            print(f"  MISSING MeshAtlas Path=\"{el.get('Path')}\"", file=sys.stderr)
            problems += 1
            continue
        names = atlas_mesh_names(atlas)
        if names is None:
            problems += 1
        else:
            meshes_in_atlas |= names
            print(f"  atlas: {el.get('Path')}  ({len(names)} meshes)")

    for tag in TEXTURE_TAGS:
        for el in root.iter(tag):
            rel = el.get("Path")
            if not rel:
                continue          # referenced by Id instead, which the Core check covers
            checked += 1
            if not exists_case_exact(path.parent / rel):
                print(f"  MISSING {tag} Path=\"{rel}\"", file=sys.stderr)
                problems += 1

    for tag, child in (("GltfFile", "Source"), ("Shader", None)):
        for el in root.iter(tag):
            source = el if child is None else el.find(child)
            rel = None if source is None else source.get("Path")
            checked += 1
            if not rel or not exists_case_exact(path.parent / rel):
                print(f"  MISSING {tag} Id=\"{el.get('Id')}\" Path=\"{rel}\"", file=sys.stderr)
                problems += 1

    static_parts = {el.get("Id") for el in root.findall("StaticSubObject")}
    for el in root.iter("SubObject"):
        checked += 1
        if el.get("InstanceOf") not in static_parts:
            print(f"  UNRESOLVED SubObject InstanceOf=\"{el.get('InstanceOf')}\"", file=sys.stderr)
            problems += 1

    for el in root.iter("Mesh"):
        ident = el.get("Id")
        if not ident:
            continue
        checked += 1
        # Only meaningful once this file declares an atlas of its own; a mesh Id could
        # otherwise legitimately live in Core's library.
        if meshes_in_atlas and ident not in meshes_in_atlas:
            print(f"  UNRESOLVED Mesh Id=\"{ident}\" (not in the declared atlas)", file=sys.stderr)
            problems += 1

    for el in root.iter("SubPart"):
        source = el.get("InstanceOf")
        if not source:
            continue
        checked += 1

        # The mod's own Ids must resolve in its own file, whether or not Core's library could be
        # read.
        #
        # The Core check below is skipped when that library is unavailable - offline, or a
        # different install layout - and this case must not be skipped with it. A SubPart
        # instancing one of the mod's own templates that does not exist kills the game on load with
        # "PartTemplate is null", and detecting that needs no Core: a name used here is declared
        # here.
        if source.startswith(OWN_PREFIXES) and source not in local_subparts:
            print(f"  UNDECLARED SubPart InstanceOf=\"{source}\" - no such template in this file",
                  file=sys.stderr)
            problems += 1
            continue

        if core_subparts is None:
            continue
        if source not in core_subparts and source not in local_subparts:
            print(f"  UNRESOLVED SubPart InstanceOf=\"{source}\"", file=sys.stderr)
            problems += 1

    # <Material> names two different things depending on where it sits: a PbrMaterial, which is
    # how something is drawn, and a Substance, which is what it is made of -- and a substance is
    # referenced by phase, "Aluminum.2014(s)" for the solid Core declares as "Aluminum.2014".
    for el in root.iter("Material"):
        ident = el.get("Id")
        if not ident:
            continue
        checked += 1
        if core_materials is None:
            continue
        substance = re.sub(r"\((s|l|g)\)$", "", ident)
        if (ident in core_materials or ident in local_materials
                or substance in core_substances):
            continue
        print(f"  UNRESOLVED Material Id=\"{ident}\"", file=sys.stderr)
        problems += 1

    for el in root.iter("PartGameData"):
        print(f"  part: {el.get('Id')}  \"{el.get('DisplayName', '')}\"")
    for el in root.iter("Part"):
        print(f"  part: {el.get('Id')}")

    return problems, checked


def check_cross_body_planes():
    """Looks for faces shared between two different subparts, placed as the XML places them.

    checkmesh.py analyses one mesh at a time, so a plane shared by two *bodies* — a turntable
    resting exactly on the cap of its mast — is invisible to it and z-fights in game like any
    other coincident pair. Worse when the two ride a common axis, because the fight then rotates.

    This lives here rather than in checkmesh.py because the atlas carries no node transforms: the
    meshes are pivot-local and only this file knows where the XML puts them.
    """
    checkmesh = import_module("checkmesh")

    problems = checked = 0
    for path in sorted(MOD.glob("KSAStructures*.xml")):
        root = ET.parse(path).getroot()

        atlas = root.find(".//MeshAtlas")
        if atlas is None or atlas.get("Path") is None:
            continue
        glb = MOD / atlas.get("Path")
        if not glb.is_file():
            continue

        # Definition Id -> the mesh it draws with. The MeshView copy is the same geometry under
        # another name, so it would report every body as fighting itself.
        mesh_of = {}
        for sub in root.findall("SubPart"):
            model = sub.find("PartModel/Mesh")
            if sub.get("Id") and model is not None and model.get("Id"):
                mesh_of[sub.get("Id")] = model.get("Id")

        gltf, binary = checkmesh.read_glb(str(glb))

        # One part at a time. Two bodies can only z-fight if they are drawn together, and bodies
        # belonging to different parts never are -- they are separate things a player attaches
        # separately. Pooling them reports every pair of parts whose mounting faces both sit at
        # X = 0, which is all of them by construction.
        for part in root.findall("Part"):
            placements = {}
            for sub in part.findall("SubPart"):
                mesh = mesh_of.get(sub.get("InstanceOf"))
                if mesh is None:
                    continue
                position = sub.find("Transform/Position")
                origin = ([float(position.get(axis, "0")) for axis in "XYZ"]
                          if position is not None else [0.0, 0.0, 0.0])
                rotation = sub.find("Transform/Rotation")
                euler = ([float(rotation.get(axis, "0")) for axis in "XYZ"]
                         if rotation is not None else [0.0, 0.0, 0.0])
                # A body instanced more than once would collide with its own copies at rest.
                placements.setdefault(mesh, (origin, euler))

            checked += len(placements)
            for area, (a, b) in checkmesh.cross_body_overlaps(gltf, binary, placements):
                print(f"  COPLANAR {area * 1e4:.1f} cm² shared by {a} and {b} "
                      f"in {part.get('Id')}", file=sys.stderr)
                problems += 1

    return problems, checked


def check_assets_declared():
    """Verifies every asset XML at the mod root is listed in mod.toml.

    mod.toml names its assets one by one. A file left out is simply never loaded and nothing
    reports it -- no warning at load, no missing-asset error. Whatever it declared then resolves
    to null at the point something first asks for it, which for a character is a crash inside
    KSA's own constructor rather than anything pointing back here.
    """
    toml = MOD / "mod.toml"
    declared = set(re.findall(r'"([^"]+\.xml)"', toml.read_text()))

    problems = checked = 0
    for path in sorted(MOD.glob("KSAStructures*.xml")):
        checked += 1
        if path.name not in declared:
            print(f"  UNDECLARED {path.name} -- present but not in mod.toml's assets, so KSA "
                  f"never loads it", file=sys.stderr)
            problems += 1

    for name in sorted(declared):
        if not (MOD / name).is_file():
            print(f"  MISSING {name} -- listed in mod.toml, not on disk", file=sys.stderr)
            problems += 1

    return problems, checked


def check_textures_unshared():
    """No texture file may be named by two materials.

    KSA loads a texture path once and hands it to one material; the second material naming the same
    file is drawn without it, and nothing is logged. Each material therefore carries files of its
    own, however alike two of them are.
    """
    problems = checked = 0
    owners = {}
    for path in sorted(MOD.glob("KSAStructures*.xml")):
        for material in ET.parse(path).getroot().iter("PbrMaterial"):
            for el in material:
                rel = el.get("Path")
                if el.tag not in TEXTURE_TAGS or not rel:
                    continue
                checked += 1
                owner = owners.setdefault(rel.lower(), material.get("Id"))
                if owner != material.get("Id"):
                    print(f"  SHARED {rel} -- named by {owner} and {material.get('Id')}; KSA loads "
                          f"it for only one of them", file=sys.stderr)
                    problems += 1

    return problems, checked


def check_code_names_what_is_declared():
    """Every asset Id and texture path a C# file names is declared in the asset XML or on disk.

    The code finds its static objects, parts, materials and shader by Id, and an Id that matches
    nothing is a tower that is not placed or a pass that does not draw, with one warning in the log.
    """
    declared = set()
    for path in sorted(MOD.glob("KSAStructures*.xml")):
        root = ET.parse(path).getroot()
        declared |= {el.get("Id") for el in root if el.get("Id")}

    problems = checked = 0
    for source in sorted(MOD.glob("Ksa/*.cs")) + sorted(MOD.glob("Sim/*.cs")):
        text = source.read_text()

        for rel in sorted(set(re.findall(r'Path="((?:Textures|Meshes|Shaders)/[^"]+)"', text))):
            checked += 1
            if not exists_case_exact(MOD / rel):
                print(f"  MISSING {rel} -- named in {source.name}", file=sys.stderr)
                problems += 1

        for line in text.splitlines():
            # A line of embedded XML names decals, which the code itself declares.
            if "<" in line and "/>" in line or line.lstrip().startswith(("<", "//")):
                continue
            for ident in re.findall(r'"((?:KSAOffice_|KSArmory_Prefab_Office|KSAStructures)\w*)"', line):
                if ident in ("KSAStructures",):
                    continue
                checked += 1
                if ident not in declared:
                    print(f"  UNDECLARED {ident} -- named in {source.name}, declared in no asset XML",
                          file=sys.stderr)
                    problems += 1

    return problems, checked


def check_asset_id_collisions():
    """No Id may name two different assets, counting the meshes the atlas registers.

    A <MeshAtlas> registers every mesh under its glTF node name, so declaring a SubPart -- or
    anything else -- under that same name puts two assets on one Id and the loader keeps whichever
    it saw first. Nothing fails: the mod loads, the part renders, the game runs. Only an importer
    that enforces uniqueness, such as SpaceDock's, rejects it.
    """
    problems = checked = 0

    # Across the whole mod, not per file: the loader registers one namespace for all of them, and
    # the importer's rule is "declared more than once within this mod". Two files each internally
    # consistent can still collide with each other.
    from_atlas = set()
    declared = {}

    for path in sorted(MOD.glob("KSAStructures*.xml")):
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError:
            continue

        for el in root.iter("MeshAtlas"):
            atlas = path.parent / el.get("Path", "")
            if atlas.is_file():
                from_atlas |= set(atlas_mesh_names(atlas))

        # Top-level only. An Id deeper in the tree is a *reference* -- <Mesh Id>, <StartSound Id>,
        # a nested <SubPart Id> naming an instance -- and only the direct children of <Assets>
        # register a name. Walking the whole tree flags every reference as a redeclaration.
        for el in root:
            ident = el.get("Id")
            if ident is None:
                continue

            # A *GameData entry is a companion keyed on another asset's Id -- that is how KSA
            # pairs a part's art with its physics, and Core does the same. It is a different
            # registry, so it is not a redeclaration.
            if el.tag.endswith("GameData"):
                continue

            checked += 1

            if ident in declared:
                where, first = declared[ident]
                print(f"  DUPLICATE Id \"{ident}\" on <{first}> in {where} and <{el.tag}> "
                      f"in {path.name}", file=sys.stderr)
                problems += 1
            elif ident in from_atlas:
                print(f"  DUPLICATE Id \"{ident}\" on <{el.tag}> in {path.name} — the atlas "
                      f"already registers a mesh under that name", file=sys.stderr)
                problems += 1

            declared[ident] = (path.name, el.tag)

    if problems == 0:
        print(f"  {checked} asset id(s), none declared twice")

    return problems, checked


def check_editor_tags(core_dir):
    """Verifies every <EditorTag> a part names is defined, here or by Core.

    A tag with no <EditorTagDef> is a *warning* in KSA's own log and nothing else: the part still
    loads, still appears under All, and simply has no category of its own. So a typo -- or Core
    renaming a tag under us -- costs a part its place in the picker with nothing failing.

    The flags are the other half and matter more. A tag carries RootPartWhitelist,
    FaceSnapTargetWhitelist and DiameterFilterlist, so moving a part between tags silently changes
    whether it can be a craft's root part and what can attach to it.

    Skips the Core half offline: those definitions live in the game install.
    """
    problems = 0
    checked = 0

    declared = set()
    used = {}

    for path in sorted(MOD.glob("KSAStructures*.xml")):
        text = path.read_text()
        declared.update(re.findall(r'<EditorTagDef\s+Id="([^"]+)"', text))
        for tag in re.findall(r'<EditorTag\s+Value="([^"]+)"', text):
            used.setdefault(tag, path.name)

    if core_dir is not None:
        for path in Path(core_dir).rglob("*.xml"):
            try:
                declared.update(re.findall(r'<EditorTagDef\s+Id="([^"]+)"', path.read_text()))
            except (OSError, UnicodeDecodeError):
                continue

    for tag, where in sorted(used.items()):
        checked += 1
        if core_dir is None and tag not in declared:
            continue                       # offline: Core's own tags are not readable
        if tag in declared:
            continue

        print(f"  UNDECLARED EditorTag \"{tag}\" in {where} -- no <EditorTagDef> defines it",
              file=sys.stderr)
        problems += 1

    if problems == 0 and checked:
        scope = "ours" if core_dir is None else "ours and Core's"
        print(f"  editor tags: {checked} tag(s) used, all declared ({scope})")

    return problems, checked


def check_stacking_connectors_sized():
    """Verifies every stacking connector declares a Scale equal to its part's diameter.

    Scale is a node's size, and KSA lets a part mate a tank's Internal node -- the nested one just
    inside each end, there for smaller parts -- only when its own node is no larger
    (Part.Connector.CanConnectIgnoringAligned). An unsized node is size one, so it passes, and a
    3 m part sinks onto that nested node: 8 cm into a 2 m tank and 24.5 cm into a 3 m one, with
    nothing reporting it.
    """
    roots = [ET.parse(path).getroot() for path in sorted(MOD.glob("KSAStructures*.xml"))]

    diameters, surface = {}, set()
    for data in (d for root in roots for d in root.iter("PartGameData")):
        part = data.get("Id")
        diameter = data.find("Diameter")
        if diameter is not None:
            diameters[part] = float(diameter.get("M"))
        for connector in data.findall("Connector"):
            flags = connector.findtext("Flags") or ""
            if any(flag in flags for flag in ("ToSurface", "FromSurface", "Internal")):
                surface.add((part, connector.get("Id")))

    problems = checked = 0
    for part in (p for root in roots for p in root.iter("Part")):
        part_id = part.get("Id")
        for connector in part.findall("Connector"):
            if (part_id, connector.get("Id")) in surface:
                continue
            checked += 1
            scale = connector.find("Transform/Scale")
            want = diameters.get(part_id)
            # No Scale is a scale of one, which is what a part a metre across wants.
            sizes = {1.0} if scale is None else {float(scale.get(axis, "1")) for axis in "XYZ"}
            if want is None or sizes != {want}:
                print(f"  UNSIZED {part_id} {connector.get('Id')} -- a stacking node needs "
                      f"<Scale> of its diameter ({want} m) on every axis, or it nests into a "
                      f"tank's Internal node; found {sorted(sizes)}",
                      file=sys.stderr)
                problems += 1

    return problems, checked


def main():
    # Without the game installed, everything that depends only on the mod's own files can still
    # be checked -- and on Linux that includes case, which is the difference between a mod that
    # loads on both platforms and one that only loads on Windows.
    parser = argparse.ArgumentParser(description="Check the part XML against itself, the meshes and Core.")
    parser.add_argument("--offline", action="store_true",
                        help="skip the checks that need KSA's Core assets")
    offline = parser.parse_args().offline

    if offline:
        print("offline: skipping the checks that need KSA's Core assets\n")
        core_subparts = core_materials = core_substances = None
    elif not CORE.is_dir():
        print(f"error: Core content not found at {CORE}", file=sys.stderr)
        print("       set KSA_DIR to your install, or pass --offline", file=sys.stderr)
        return 1
    else:
        print(f"reading Core assets from {CORE}")
        declared = collect_core_ids(CORE)
        core_subparts = declared.get("SubPart", set())
        core_materials = declared.get("PbrMaterial", set())
        core_substances = declared.get("Substance", set())
        print(f"  {len(core_subparts)} subparts, {len(core_materials)} materials, "
              f"{len(core_substances)} substances declared\n")

    problems = checked = 0
    for path in sorted(MOD.glob("KSAStructures*.xml")):
        print(f"checking {path.relative_to(REPO)}")
        p, c = check_file(path, core_subparts, core_materials, core_substances)
        problems += p
        checked += c

    for label, check in [
        ("every asset XML is declared in mod.toml", check_assets_declared),
        ("every stacking connector is sized to its part", check_stacking_connectors_sized),
        ("every editor tag a part names is defined", lambda: check_editor_tags(None if offline else CORE)),
        ("no asset id is declared twice", check_asset_id_collisions),
        ("no texture is shared between materials", check_textures_unshared),
        ("every asset the code names is declared", check_code_names_what_is_declared),
        ("for planes shared between subparts", check_cross_body_planes),
    ]:
        print(f"checking {label}")
        p, c = check()
        problems += p
        checked += c

    print()
    if problems:
        print(f"FAILED: {problems} problem(s) across {checked} reference(s)", file=sys.stderr)
        return 1

    print(f"OK: {checked} asset reference(s) resolve")
    return 0


if __name__ == "__main__":
    sys.exit(main())
