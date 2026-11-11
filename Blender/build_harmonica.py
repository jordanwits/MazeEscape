# ---------------------------------------------------------------------------
# Old 10-hole diatonic harmonica - procedural build, UV, bake, render, export.
# Styled for Level01 (Decrepit Dungeon): tarnished, scratched steel covers built
# from the pack's Metal_B scratches, a worn Wood_C comb, oxidised brass plates.
#
# Game-ready budget: ~400 tris, ONE material, ONE 512px RGBA atlas
# (RGB = albedo with AO baked in, A = smoothness for URP "Albedo Alpha").
#
# Blender 5.1, headless:  blender.exe -b --python build_harmonica.py -- [flags]
# flags:  nobake   skip the texture bake (fast model iteration)
#         nosave   don't write the .blend / FBX
#         fast     low-res previews
# ---------------------------------------------------------------------------
import bpy, bmesh, math, os, sys
import numpy as np
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
DO_BAKE = "nobake" not in argv
DO_SAVE = "nosave" not in argv

ROOT     = r"H:/Unity/Maze Escape/Blender"
TEXDIR   = os.path.join(ROOT, "textures")
PACKTEX  = r"H:/Unity/Maze Escape/Assets/Decrepit Dungeon LITE/Textures"
PREVIEW  = os.environ.get("HARMONICA_PREVIEW_DIR", os.path.join(ROOT, "previews"))
os.makedirs(TEXDIR, exist_ok=True)
os.makedirs(PREVIEW, exist_ok=True)

NAME        = "Harmonica"
BAKE_RES    = 512
BAKE_MARGIN = 4           # px; must stay well under the packed island gap
PACK_MARGIN = 0.02        # UV units (~10px at 512)

# --------------------------------------------------------------- dimensions
# Blender axes: X = length, -Y = mouthpiece (holes), Z = up.  ~11 cm long, a touch
# over a real 10.2 cm Marine-Band-sized harp so it reads in the first-person hand.
COMB_X      = 0.0515
COMB_Y0     = -0.0135     # comb front sits proud of the plates/covers
COMB_Y1     = 0.0105
COMB_Z      = 0.0034
HOLES       = 10
HOLE_PITCH  = 0.0098
HOLE_W      = 0.0064
HOLE_DEPTH  = 0.0150      # chamber depth back from the comb front

PLATE_X     = 0.0525
PLATE_Y0    = -0.0128     # flush with the cover front so its top face can go
PLATE_Y1    = 0.0110
PLATE_T     = 0.0009      # brass reed plate thickness
PLATE_Z1    = COMB_Z + PLATE_T

COVER_X     = 0.0555      # covers wrap past the plates at the ends
COVER_X_IN  = 0.0525      # where the end chamfer starts
END_INSET_Y = 0.0012
END_DROP    = 0.86        # end section height factor (rounded-off ends)

# Top cover cross-section (y, z), front (mouthpiece) to back.  The back curve is
# deliberately fine enough that every turn is < 40 deg, so auto-smooth keeps it
# round while the front fold and the bottom edge stay crisp.
PROFILE = [(-0.0128, PLATE_Z1), (-0.0128, 0.0063), (-0.0108, 0.0069),
           (-0.0020, 0.0072), ( 0.0030, 0.0090), ( 0.0075, 0.0099),
           ( 0.0105, 0.0094), ( 0.0122, 0.0082), ( 0.0129, 0.0066),
           ( 0.0130, PLATE_Z1)]

# ------------------------------------------------------------------ helpers
def purge():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    for coll in (bpy.data.meshes, bpy.data.materials, bpy.data.images,
                 bpy.data.node_groups, bpy.data.objects):
        for item in list(coll):
            coll.remove(item)

def link(obj):
    bpy.context.collection.objects.link(obj)
    return obj

def set_active(obj):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    return obj

def apply_mods(obj):
    set_active(obj)
    for m in list(obj.modifiers):
        bpy.ops.object.modifier_apply(modifier=m.name)

def smooth(obj, angle=40.0):
    set_active(obj)
    bpy.ops.object.shade_auto_smooth(angle=math.radians(angle))

def box(name, x0, x1, y0, y1, z0, z1):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x = x0 if v.co.x < 0 else x1
        v.co.y = y0 if v.co.y < 0 else y1
        v.co.z = z0 if v.co.z < 0 else z1
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    return link(bpy.data.objects.new(name, me))

def delete_faces(obj, pred):
    """Remove faces hidden against another part (also kills coplanar z-fighting)."""
    bm = bmesh.new(); bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    dead = [f for f in bm.faces if pred(f)]
    bmesh.ops.delete(bm, geom=dead, context='FACES')
    bm.to_mesh(obj.data); bm.free()
    return len(dead)

def cover(name, flip):
    """Loft the cover profile along X: chamfered end section, full body, chamfered end."""
    s = -1.0 if flip else 1.0
    cy = 0.5 * (PROFILE[0][0] + PROFILE[-1][0])
    sections = []
    for x, inset in ((-COVER_X, True), (-COVER_X_IN, False),
                     ( COVER_X_IN, False), ( COVER_X, True)):
        ring = []
        for y, z in PROFILE:
            if inset:
                # pull the end section in toward the centre line and down toward the plate
                y = cy + (y - cy) * (1.0 - END_INSET_Y / abs(PROFILE[0][0] - cy))
                z = PLATE_Z1 + (z - PLATE_Z1) * END_DROP
            ring.append(Vector((x, y, z * s)))
        sections.append(ring)

    bm = bmesh.new()
    vrows = [[bm.verts.new(p) for p in ring] for ring in sections]
    n = len(PROFILE)
    for a, b in zip(vrows[:-1], vrows[1:]):
        for k in range(n):
            k2 = (k + 1) % n
            bm.faces.new((a[k], a[k2], b[k2], b[k]))
    bm.faces.new(list(reversed(vrows[0])))
    bm.faces.new(vrows[-1])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    o = link(bpy.data.objects.new(name, me))
    # the flat underside sits flush on the reed plate: never visible, and coplanar
    delete_faces(o, lambda f: abs(f.normal.z * s + 1.0) < 1e-3
                 and abs(abs(f.calc_center_median().z) - PLATE_Z1) < 1e-5)
    return o

purge()
print("=== building geometry ===")

# ------------------------------------------------------------ 1. wooden comb
comb = box("Comb", -COMB_X, COMB_X, COMB_Y0, COMB_Y1, -COMB_Z, COMB_Z)
cutters = []
for i in range(HOLES):
    cx = (i - (HOLES - 1) * 0.5) * HOLE_PITCH
    cutters.append(box("cut_%d" % i, cx - HOLE_W * 0.5, cx + HOLE_W * 0.5,
                       COMB_Y0 - 0.002, COMB_Y0 + HOLE_DEPTH,
                       -COMB_Z - 0.002, COMB_Z + 0.002))
bpy.ops.object.select_all(action='DESELECT')
for c in cutters: c.select_set(True)
bpy.context.view_layer.objects.active = cutters[0]
bpy.ops.object.join()
holes = bpy.context.object
b = comb.modifiers.new("holes", 'BOOLEAN')
b.object, b.operation, b.solver = holes, 'DIFFERENCE', 'EXACT'
apply_mods(comb)
bpy.data.objects.remove(holes, do_unlink=True)
# top and bottom of the comb are covered by the reed plates (and coplanar with them)
print("comb hidden faces removed:",
      delete_faces(comb, lambda f: abs(abs(f.normal.z) - 1.0) < 1e-3))
smooth(comb, 30.0)

# ------------------------------------------------------ 2. brass reed plates
plate_top = box("PlateTop", -PLATE_X, PLATE_X, PLATE_Y0, PLATE_Y1, COMB_Z, PLATE_Z1)
plate_bot = box("PlateBottom", -PLATE_X, PLATE_X, PLATE_Y0, PLATE_Y1, -PLATE_Z1, -COMB_Z)
# the outer faces sit flush under the covers (whose undersides are removed): never seen
delete_faces(plate_top, lambda f: f.normal.z > 0.999)
delete_faces(plate_bot, lambda f: f.normal.z < -0.999)
smooth(plate_top, 30.0); smooth(plate_bot, 30.0)

# ---------------------------------------------------------- 3. cover plates
cover_top = cover("CoverTop", False)
cover_bot = cover("CoverBottom", True)
smooth(cover_top, 40.0); smooth(cover_bot, 40.0)

PARTS = [comb, plate_top, plate_bot, cover_top, cover_bot]
for p in PARTS: p.data.calc_loop_triangles()
print("tris:", sum(len(p.data.loop_triangles) for p in PARTS))

# ============================================================== 4. materials
print("=== materials ===")
S = bpy.types.NodeSocket

def new_mat(name):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes): nt.nodes.remove(n)
    out  = nt.nodes.new('ShaderNodeOutputMaterial'); out.location = (900, 0)
    bsdf = nt.nodes.new('ShaderNodeBsdfPrincipled'); bsdf.location = (600, 0)
    nt.links.new(bsdf.outputs['BSDF'], out.inputs['Surface'])
    return m, nt, bsdf

def plug(nt, sock, inp):
    if isinstance(sock, S): nt.links.new(sock, inp)
    elif isinstance(sock, (tuple, list)):
        inp.default_value = (*sock, 1.0) if len(sock) == 3 else tuple(sock)
    else: inp.default_value = sock

def noise(nt, scale, detail=6, rough=0.55, vec=None):
    n = nt.nodes.new('ShaderNodeTexNoise')
    n.inputs['Scale'].default_value     = scale
    n.inputs['Detail'].default_value    = detail
    n.inputs['Roughness'].default_value = rough
    if vec is not None: nt.links.new(vec, n.inputs['Vector'])
    return n.outputs['Fac']

def ramp(nt, src, stops):
    n = nt.nodes.new('ShaderNodeValToRGB')
    els = n.color_ramp.elements
    els[0].position, els[0].color = stops[0]
    els[1].position, els[1].color = stops[1]
    for p, c in stops[2:]:
        e = els.new(p); e.color = c
    plug(nt, src, n.inputs['Fac'])
    return n.outputs['Color']

def cmix(nt, fac, a, b, blend='MIX'):
    n = nt.nodes.new('ShaderNodeMix')
    n.data_type, n.blend_type, n.clamp_factor = 'RGBA', blend, True
    plug(nt, fac, n.inputs[0]); plug(nt, a, n.inputs[6]); plug(nt, b, n.inputs[7])
    return n.outputs[2]

def fmix(nt, fac, a, b):
    n = nt.nodes.new('ShaderNodeMix'); n.data_type = 'FLOAT'; n.clamp_factor = True
    plug(nt, fac, n.inputs[0]); plug(nt, a, n.inputs[2]); plug(nt, b, n.inputs[3])
    return n.outputs[0]

def mth(nt, op, a, b=None, clamp=False):
    n = nt.nodes.new('ShaderNodeMath'); n.operation = op; n.use_clamp = clamp
    plug(nt, a, n.inputs[0])
    if b is not None: plug(nt, b, n.inputs[1])
    return n.outputs[0]

def maprange(nt, val, f0, f1, t0=0.0, t1=1.0):
    n = nt.nodes.new('ShaderNodeMapRange'); n.clamp = True
    plug(nt, val, n.inputs[0])
    for i, v in ((1, f0), (2, f1), (3, t0), (4, t1)): n.inputs[i].default_value = v
    return n.outputs[0]

def xyz(nt):
    g = nt.nodes.new('ShaderNodeNewGeometry')
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    nt.links.new(g.outputs['Position'], sep.inputs['Vector'])
    return g, sep.outputs['X'], sep.outputs['Y'], sep.outputs['Z']

def pack_image(nt, fname, vec, non_color=False):
    img = bpy.data.images.load(os.path.join(PACKTEX, fname), check_existing=True)
    if non_color: img.colorspace_settings.name = 'Non-Color'
    t = nt.nodes.new('ShaderNodeTexImage')
    t.image = img
    t.interpolation = 'Cubic'
    nt.links.new(vec, t.inputs['Vector'])
    return t.outputs['Color']

def box_vec(nt, scale):
    """Object coords scaled for a BOX-projected image."""
    tc = nt.nodes.new('ShaderNodeTexCoord')
    mp = nt.nodes.new('ShaderNodeMapping')
    mp.inputs['Scale'].default_value = (scale, scale, scale)
    nt.links.new(tc.outputs['Object'], mp.inputs['Vector'])
    return mp.outputs['Vector']

def edge_mask(nt, radius=0.0004, samples=8):
    # Bevel-node curvature: ~0 on flat and smoothly curved faces, rises at real edges.
    bev = nt.nodes.new('ShaderNodeBevel')
    bev.samples = samples
    bev.inputs['Radius'].default_value = radius
    g2 = nt.nodes.new('ShaderNodeNewGeometry')
    dp = nt.nodes.new('ShaderNodeVectorMath'); dp.operation = 'DOT_PRODUCT'
    nt.links.new(bev.outputs['Normal'], dp.inputs[0])
    nt.links.new(g2.outputs['Normal'],  dp.inputs[1])
    return mth(nt, 'SUBTRACT', 1.0, dp.outputs['Value'], clamp=True)

def bw(nt, col):
    n = nt.nodes.new('ShaderNodeRGBToBW')
    plug(nt, col, n.inputs['Color'])
    return n.outputs['Val']

def lines_box(nt, ax, y, cx_half, cy, cy_half, width):
    """1 on the outline of an axis-aligned rectangle in (|x|, y), 0 elsewhere."""
    dx = mth(nt, 'SUBTRACT', ax, cx_half)
    dy = mth(nt, 'SUBTRACT', mth(nt, 'ABSOLUTE', mth(nt, 'SUBTRACT', y, cy)), cy_half)
    d  = mth(nt, 'ABSOLUTE', mth(nt, 'MAXIMUM', dx, dy))
    return maprange(nt, d, width * 0.5, width, 1.0, 0.0)

# ---- palette (linear) -----------------------------------------------------
STEEL_DARK   = (0.050, 0.048, 0.043)
STEEL_TARN   = (0.150, 0.140, 0.118)
STEEL_SCR    = (0.400, 0.392, 0.370)
STEEL_POLISH = (0.330, 0.325, 0.310)
GRIME        = (0.045, 0.035, 0.022)
RUST         = (0.130, 0.048, 0.016)
VENT         = (0.008, 0.007, 0.006)
BRASS        = (0.300, 0.190, 0.060)
BRASS_DARK   = (0.090, 0.060, 0.022)
PATINA       = (0.050, 0.090, 0.060)
WOOD_TINT    = (0.72, 0.62, 0.50)

def steel_material(name):
    m, nt, bsdf = new_mat(name)
    g, x, y, z = xyz(nt)
    ax, az = mth(nt, 'ABSOLUTE', x), mth(nt, 'ABSOLUTE', z)

    # scratches straight off the dungeon pack's Metal_B, box-projected
    scr = bw(nt, pack_image(nt, "Metal_B.png", box_vec(nt, 9.0)))
    scr = maprange(nt, scr, 0.045, 0.30)
    tarn = ramp(nt, noise(nt, 55, 7, 0.6),
                [(0.30, (*STEEL_DARK, 1)), (0.70, (*STEEL_TARN, 1))])
    base = cmix(nt, mth(nt, 'MULTIPLY', scr, 0.85), tarn, STEEL_SCR)

    # grime creeping in from the ends and the back fold
    endish  = maprange(nt, ax, 0.040, 0.0555)
    backish = maprange(nt, y, 0.006, 0.0130)
    gm = mth(nt, 'MAXIMUM', endish, backish)
    gm = mth(nt, 'MULTIPLY', gm, ramp(nt, noise(nt, 140, 6, 0.6),
                                      [(0.35, (0.25, 0.25, 0.25, 1)), (0.65, (1, 1, 1, 1))]))
    base = cmix(nt, mth(nt, 'MULTIPLY', gm, 0.75), base, GRIME)

    # rust pitting, heavier where the grime sits
    rmask = maprange(nt, noise(nt, 420, 4, 0.5), 0.64, 0.72)
    rmask = mth(nt, 'MULTIPLY', rmask, mth(nt, 'ADD', mth(nt, 'MULTIPLY', gm, 0.8), 0.25), clamp=True)
    rblot = maprange(nt, noise(nt, 70, 5, 0.6), 0.62, 0.75)
    rmask = mth(nt, 'MAXIMUM', rmask, mth(nt, 'MULTIPLY', rblot, mth(nt, 'MULTIPLY', gm, 0.7)))
    base = cmix(nt, rmask, base, RUST)

    # worn-bright fold edges
    wear = maprange(nt, edge_mask(nt), 0.03, 0.30)
    wear = mth(nt, 'MULTIPLY', wear, 0.8)
    base = cmix(nt, wear, base, STEEL_POLISH)

    # stamped double border on the top panel + nail heads near the ends
    top = maprange(nt, az, 0.0068, 0.0074)
    l1 = lines_box(nt, ax, y, 0.0455, 0.0010, 0.0080, 0.00045)
    l2 = lines_box(nt, ax, y, 0.0440, 0.0010, 0.0065, 0.00030)
    stamp = mth(nt, 'MULTIPLY', mth(nt, 'MAXIMUM', l1, l2), top)
    base = cmix(nt, mth(nt, 'MULTIPLY', stamp, 0.85), base, STEEL_DARK)

    nail_d = nt.nodes.new('ShaderNodeVectorMath'); nail_d.operation = 'LENGTH'
    nv = nt.nodes.new('ShaderNodeCombineXYZ')
    plug(nt, mth(nt, 'SUBTRACT', ax, 0.0505), nv.inputs['X'])
    plug(nt, mth(nt, 'SUBTRACT', y, 0.0040), nv.inputs['Y'])
    nt.links.new(nv.outputs['Vector'], nail_d.inputs[0])
    nd = nail_d.outputs['Value']
    head = mth(nt, 'MULTIPLY', maprange(nt, nd, 0.0009, 0.0007), top)
    ring = mth(nt, 'MULTIPLY', mth(nt, 'MULTIPLY', maprange(nt, nd, 0.0008, 0.0010),
                                   maprange(nt, nd, 0.0014, 0.0012)), top)
    base = cmix(nt, ring, base, VENT)
    base = cmix(nt, head, base, (0.22, 0.20, 0.17))

    # open back vent between the cover fold and the reed plate
    vent = mth(nt, 'MULTIPLY', maprange(nt, y, 0.01275, 0.01290),
               maprange(nt, az, 0.0062, 0.0058))
    base = cmix(nt, vent, base, VENT)

    rgh = fmix(nt, mth(nt, 'MULTIPLY', scr, 0.6), 0.52, 0.38)
    rgh = fmix(nt, gm, rgh, 0.72)
    rgh = fmix(nt, rmask, rgh, 0.88)
    rgh = fmix(nt, wear, rgh, 0.30)
    rgh = fmix(nt, stamp, rgh, 0.70)
    rgh = fmix(nt, vent, rgh, 0.95)
    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, rgh,  bsdf.inputs['Roughness'])
    plug(nt, 0.0,  bsdf.inputs['Metallic'])
    return m

def wood_material(name):
    m, nt, bsdf = new_mat(name)
    g, x, y, z = xyz(nt)
    # grain runs along the harmonica's length on every face: u = y+z, v = x
    cv = nt.nodes.new('ShaderNodeCombineXYZ')
    plug(nt, mth(nt, 'MULTIPLY', mth(nt, 'ADD', y, z), 14.0), cv.inputs['X'])
    plug(nt, mth(nt, 'MULTIPLY', x, 6.0), cv.inputs['Y'])
    wood = pack_image(nt, "Wood_C.png", cv.outputs['Vector'])
    wood = cmix(nt, 1.0, wood, WOOD_TINT, 'MULTIPLY')
    dirt = ramp(nt, noise(nt, 160, 6, 0.6), [(0.35, (0.55, 0.50, 0.45, 1)),
                                             (0.70, (1.0, 1.0, 1.0, 1))])
    base = cmix(nt, 1.0, wood, dirt, 'MULTIPLY')
    wear = mth(nt, 'MULTIPLY', maprange(nt, edge_mask(nt, 0.0003), 0.03, 0.30), 0.55)
    base = cmix(nt, wear, base, (0.30, 0.22, 0.14))
    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, fmix(nt, wear, 0.82, 0.62), bsdf.inputs['Roughness'])
    plug(nt, 0.0, bsdf.inputs['Metallic'])
    return m

def brass_material(name):
    m, nt, bsdf = new_mat(name)
    tone = ramp(nt, noise(nt, 200, 6, 0.6), [(0.30, (*BRASS_DARK, 1)),
                                             (0.70, (*BRASS, 1))])
    pat  = maprange(nt, noise(nt, 380, 4, 0.5), 0.62, 0.72)
    base = cmix(nt, pat, tone, PATINA)
    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, fmix(nt, pat, 0.45, 0.80), bsdf.inputs['Roughness'])
    plug(nt, 0.0, bsdf.inputs['Metallic'])
    return m

MATS = {
    'steel': steel_material("SRC_Steel"),
    'wood' : wood_material("SRC_Wood"),
    'brass': brass_material("SRC_Brass"),
}
for obj, key in ((comb, 'wood'), (plate_top, 'brass'), (plate_bot, 'brass'),
                 (cover_top, 'steel'), (cover_bot, 'steel')):
    obj.data.materials.clear()
    obj.data.materials.append(MATS[key])

# ================================================================== 5. UVs
print("=== uv unwrap ===")
for o in PARTS:
    if not o.data.uv_layers:
        o.data.uv_layers.new(name="UVMap")
    set_active(o)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(50), island_margin=0.01,
                             correct_aspect=True, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode='OBJECT')

bpy.ops.object.select_all(action='DESELECT')
for o in PARTS: o.select_set(True)
bpy.context.view_layer.objects.active = PARTS[0]
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.select_all(action='SELECT')
bpy.ops.uv.average_islands_scale()
bpy.ops.object.mode_set(mode='OBJECT')

# The plates' inner faces are only glimpsed through the hole slots (and AO-dark there):
# give them a fraction of the texel density so the covers get the atlas.
def shrink_uv(obj, pred, factor):
    bm = bmesh.new(); bm.from_mesh(obj.data)
    uv = bm.loops.layers.uv.active
    for f in bm.faces:
        if not pred(f): continue
        c = sum((l[uv].uv for l in f.loops), Vector((0.0, 0.0))) / len(f.loops)
        for l in f.loops:
            l[uv].uv = c + (l[uv].uv - c) * factor
    bm.to_mesh(obj.data); bm.free()
shrink_uv(plate_top, lambda f: abs(f.normal.z) > 0.999, 0.35)
shrink_uv(plate_bot, lambda f: abs(f.normal.z) > 0.999, 0.35)

bpy.ops.object.select_all(action='DESELECT')
for o in PARTS: o.select_set(True)
bpy.context.view_layer.objects.active = PARTS[0]
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.select_all(action='SELECT')
bpy.ops.uv.pack_islands(rotate=True, scale=True, margin=PACK_MARGIN)
bpy.ops.object.mode_set(mode='OBJECT')

# ================================================================= 6. bake
FINAL_PATH = os.path.join(TEXDIR, NAME + "_Albedo.png")
final_img = None
if DO_BAKE:
    print("=== baking %dpx atlas ===" % BAKE_RES)
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.device = 'CPU'
    sc.render.bake.margin = BAKE_MARGIN
    sc.render.bake.margin_type = 'EXTEND'
    sc.render.bake.use_selected_to_active = False
    sc.render.bake.use_pass_direct = False
    sc.render.bake.use_pass_indirect = False
    # AO only looks for occluders within a few mm: crevices, chambers, folds
    w = bpy.data.worlds.new("BakeWorld"); sc.world = w
    w.light_settings.distance = 0.006

    def make_img(name, non_color):
        img = bpy.data.images.new(name, BAKE_RES, BAKE_RES, alpha=False)
        img.colorspace_settings.name = 'Non-Color' if non_color else 'sRGB'
        return img

    def set_targets(img):
        made = []
        for m in MATS.values():
            nt = m.node_tree
            tn = nt.nodes.new('ShaderNodeTexImage')
            tn.image = img
            for n in nt.nodes: n.select = False
            tn.select = True
            nt.nodes.active = tn
            made.append((nt, tn))
        return made

    def select_parts():
        bpy.ops.object.select_all(action='DESELECT')
        for o in PARTS: o.select_set(True)
        bpy.context.view_layer.objects.active = PARTS[0]

    def emit_swap(socket_name):
        saved = []
        for m in MATS.values():
            nt = m.node_tree
            out  = next(n for n in nt.nodes if n.type == 'OUTPUT_MATERIAL')
            bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
            orig = out.inputs['Surface'].links[0].from_socket
            em   = nt.nodes.new('ShaderNodeEmission')
            inp  = bsdf.inputs[socket_name]
            if inp.links:
                nt.links.new(inp.links[0].from_socket, em.inputs['Color'])
            else:
                v = inp.default_value
                try:    c = (v[0], v[1], v[2], 1.0)
                except (TypeError, IndexError): c = (v, v, v, 1.0)
                em.inputs['Color'].default_value = c
            nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
            saved.append((nt, out, orig, em))
        return saved

    def emit_restore(saved):
        for nt, out, orig, em in saved:
            nt.nodes.remove(em)
            nt.links.new(orig, out.inputs['Surface'])

    def bake(kind, img, samples=1):
        sc.cycles.samples = samples
        made = set_targets(img)
        select_parts()
        bpy.ops.object.bake(type=kind, margin=BAKE_MARGIN, use_clear=True)
        for nt, tn in made: nt.nodes.remove(tn)

    img_col = make_img(NAME + "_bake_col", False)
    img_rgh = make_img(NAME + "_bake_rgh", True)
    img_ao  = make_img(NAME + "_bake_ao",  True)
    sv = emit_swap('Base Color'); bake('EMIT', img_col, samples=4); emit_restore(sv)
    print("  baked base colour")
    sv = emit_swap('Roughness');  bake('EMIT', img_rgh, samples=4); emit_restore(sv)
    print("  baked roughness")
    bake('AO', img_ao, samples=256)
    print("  baked AO")

    # ---- composite: RGB = albedo * AO (in linear), A = smoothness
    def px(img):
        a = np.empty(len(img.pixels), dtype=np.float32)
        img.pixels.foreach_get(a)
        return a.reshape(-1, 4)
    def s2l(c): return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)
    def l2s(c): return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1 / 2.4) - 0.055)

    col = s2l(px(img_col)[:, :3])
    ao  = px(img_ao)[:, :1]
    rg  = px(img_rgh)[:, :1]
    ao_soft = 0.30 + 0.70 * ao            # keep crevices dark but never pure black
    out = np.ones((col.shape[0], 4), dtype=np.float32)
    out[:, :3] = np.clip(l2s(col * ao_soft), 0.0, 1.0)
    out[:, 3]  = np.clip((1.0 - rg[:, 0]) * (0.55 + 0.45 * ao[:, 0]), 0.0, 1.0)

    final_img = bpy.data.images.new(NAME + "_Albedo", BAKE_RES, BAKE_RES, alpha=True)
    final_img.colorspace_settings.name = 'sRGB'
    final_img.alpha_mode = 'STRAIGHT'
    final_img.pixels.foreach_set(out.flatten())
    final_img.filepath_raw = FINAL_PATH
    final_img.file_format = 'PNG'
    final_img.save()
    print("  wrote", FINAL_PATH)
    print("  albedo mean (sRGB) %.3f  smoothness %.2f..%.2f"
          % (out[:, :3].mean(), out[:, 3].min(), out[:, 3].max()))
    for tmp in (img_col, img_rgh, img_ao):
        bpy.data.images.remove(tmp)

# ---- final game material: one texture, smoothness from albedo alpha
fm, fnt, fbsdf = new_mat("M_Harmonica")
if final_img is None and os.path.exists(FINAL_PATH):
    final_img = bpy.data.images.load(FINAL_PATH)
if final_img is not None:
    t = fnt.nodes.new('ShaderNodeTexImage'); t.image = final_img; t.location = (-500, 0)
    fnt.links.new(t.outputs['Color'], fbsdf.inputs['Base Color'])
    inv = mth(fnt, 'SUBTRACT', 1.0, t.outputs['Alpha'], clamp=True)
    fnt.links.new(inv, fbsdf.inputs['Roughness'])
fbsdf.inputs['Metallic'].default_value = 0.0
for o in PARTS:
    o.data.materials.clear()
    o.data.materials.append(fm)
for m in MATS.values():
    m.use_fake_user = True

# ============================================== 7. studio, camera, previews
print("=== previews ===")
sc = bpy.context.scene
sc.view_settings.view_transform = 'Standard'
sc.view_settings.look = 'None'
sc.render.image_settings.file_format = 'PNG'
sc.render.resolution_percentage = 60 if 'fast' in argv else 100
world = bpy.data.worlds.new("Studio"); sc.world = world
world.use_nodes = True
wbg = next(n for n in world.node_tree.nodes if n.type == 'BACKGROUND')
wbg.inputs['Color'].default_value = (0.32, 0.32, 0.34, 1)
wbg.inputs['Strength'].default_value = 0.25

def area_light(name, loc, size, power, target=(0, 0, 0)):
    d = bpy.data.lights.new(name, 'AREA')
    d.size, d.energy = size, power
    o = link(bpy.data.objects.new(name, d))
    o.location = loc
    o.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    return o

area_light("Key",  (-0.20, -0.25, 0.25), 0.25, 1.6)
area_light("Fill", ( 0.25, -0.15, 0.08), 0.35, 0.5)
area_light("Rim",  ( 0.05,  0.30, 0.15), 0.25, 1.2)

cd = bpy.data.cameras.new("Cam")
cam = link(bpy.data.objects.new("Cam", cd))
sc.camera = cam
def place_cam(az_deg, elev_deg, dist, target=(0, 0, 0), lens=85.0):
    az, el = math.radians(az_deg), math.radians(elev_deg)
    d = Vector((math.sin(az) * math.cos(el), -math.cos(az) * math.cos(el), math.sin(el)))
    cam.data.lens = lens
    cam.location = Vector(target) + d * dist
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat('-Z', 'Y').to_euler()

sc.render.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.samples = 24 if 'fast' in argv else 64
sc.cycles.use_denoising = True
for name, az, el, dist in (("quarter", -30.0, 32.0, 0.36), ("front", 0.0, 6.0, 0.34),
                           ("back", 160.0, 28.0, 0.36), ("end", -80.0, 15.0, 0.20)):
    sc.render.resolution_x, sc.render.resolution_y = 1000, 640
    place_cam(az, el, dist)
    sc.render.filepath = os.path.join(PREVIEW, "harmonica_%s.png" % name)
    bpy.ops.render.render(write_still=True)
    print("  rendered", sc.render.filepath)

# ============================================================== 8. save/export
root = link(bpy.data.objects.new(NAME, None))
root.empty_display_type = 'PLAIN_AXES'
root.empty_display_size = 0.03
for p in PARTS:
    p.parent = root
    p.matrix_parent_inverse = root.matrix_world.inverted()

if DO_SAVE:
    blend = os.path.join(ROOT, NAME + ".blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    try:
        bpy.ops.file.make_paths_relative()
        bpy.ops.wm.save_mainfile()
    except Exception as e:
        print("  relpath skipped:", e)
    print("saved", blend)

    # single joined mesh, single material, Y-up baked into the verts
    bpy.ops.object.select_all(action='DESELECT')
    copies = []
    for o in PARTS:
        c = o.copy(); c.data = o.data.copy(); c.parent = None
        c.matrix_world = o.matrix_world
        bpy.context.collection.objects.link(c)
        c.select_set(True); copies.append(c)
    bpy.context.view_layer.objects.active = copies[0]
    bpy.ops.object.join()
    merged = bpy.context.object
    merged.name = NAME; merged.data.name = NAME
    merged.data.calc_loop_triangles()
    print("EXPORT tris:", len(merged.data.loop_triangles), "verts:", len(merged.data.vertices),
          "materials:", [m.name for m in merged.data.materials])
    fbx = os.path.join(ROOT, NAME + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=fbx, use_selection=True, object_types={'MESH'},
        apply_scale_options='FBX_SCALE_ALL', axis_up='Y', axis_forward='-Z',
        bake_space_transform=True, use_mesh_modifiers=True, mesh_smooth_type='FACE',
        add_leaf_bones=False, bake_anim=False, path_mode='STRIP')
    print("wrote", fbx, os.path.getsize(fbx), "bytes")

tot = 0
for p in PARTS:
    p.data.calc_loop_triangles(); tot += len(p.data.loop_triangles)
print("TOTAL TRIS:", tot)
print("BUILD DONE")
