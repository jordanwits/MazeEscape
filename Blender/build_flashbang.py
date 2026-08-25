# ---------------------------------------------------------------------------
# M84-style flashbang / stun grenade - procedural build, UV, bake, render.
# Blender 5.1, headless:  blender.exe -b --python build_flashbang.py -- [flags]
# flags:  nobake   skip the texture bake (fast model iteration)
#         nosave   don't write the .blend
# ---------------------------------------------------------------------------
import bpy, bmesh, math, os, sys
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
DO_BAKE = "nobake" not in argv
DO_SAVE = "nosave" not in argv

HERE     = os.path.dirname(os.path.abspath(bpy.data.filepath or __file__))
ROOT     = r"H:/Unity/Maze Escape/Blender"
TEXDIR   = os.path.join(ROOT, "textures")
PREVIEW  = r"C:/Users/jorda/AppData/Local/Temp/claude/H--Unity-Maze-Escape/3f78cba3-ef6d-4b8f-8e5f-f96525b88270/scratchpad"
os.makedirs(TEXDIR, exist_ok=True)
os.makedirs(PREVIEW, exist_ok=True)
BAKE_RES    = 2048
BAKE_MARGIN = 6           # px; keep well under the packed island gap

# --------------------------------------------------------------- dimensions
# derived from the reference photo, scaled so total height = 134 mm (real M84)
R_BODY      = 0.0165      # perforated sleeve outer radius
WALL        = 0.0018      # sleeve wall thickness
Z_BODY_BOT  = 0.0210
Z_BODY_TOP  = 0.0890

R_BASE      = 0.0205      # hex base nut circumradius
Z_BASE_BOT  = 0.0000
Z_BASE_TOP  = 0.0210

R_COLLAR    = 0.0225      # octagonal collar circumradius
Z_COLLAR_BOT= 0.0890
Z_COLLAR_TOP= 0.1080

R_NECK      = 0.0060
Z_NECK_TOP  = 0.1125
R_FUZE      = 0.0090
Z_FUZE_TOP  = 0.1265
R_STRIKER   = 0.0065
Z_STRIKER_TOP = 0.1300

R_HOLE      = 0.0045
HOLE_COLS   = 6
HOLE_ROWS_Z = (0.0313, 0.0540, 0.0767)

Z_BAND_LO   = 0.0410      # glow-in-the-dark band
Z_BAND_HI   = 0.0675

R_CORE      = 0.0130      # inner pyrotechnic charge seen through the holes
Z_CORE_BOT  = 0.0215
Z_CORE_TOP  = 0.0885
Z_CORE_RED  = 0.0400      # red below this, copper above

ASSEMBLY_YAW = 26.0       # deg: swings lever/pin/ring off -X toward the camera
HOLE_PHASE   = 30.0       # deg: puts one hole column dead-centre to the camera

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
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except RuntimeError as e:
            print(f"  ! modifier {m.name} on {obj.name}: {e}")
            obj.modifiers.remove(m)

def bevel(obj, width, segments=2, angle=32.0, clamp=True):
    m = obj.modifiers.new("bevel", 'BEVEL')
    m.width, m.segments = width, segments
    m.limit_method = 'ANGLE'
    m.angle_limit = math.radians(angle)
    m.use_clamp_overlap = clamp
    m.miter_outer = 'MITER_ARC'
    return m

def smooth(obj, angle=32.0):
    set_active(obj)
    bpy.ops.object.shade_auto_smooth(angle=math.radians(angle))

def cyl(name, radius, z0, z1, verts=96, fill='NGON', rot=None, loc=None):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=radius,
                                        depth=(z1 - z0), end_fill_type=fill,
                                        location=(0, 0, (z0 + z1) * 0.5))
    o = bpy.context.object
    o.name = name
    if rot: o.rotation_euler = rot
    if loc: o.location = loc
    return o

def prism(name, radius, z0, z1, sides, twist_deg):
    """Regular prism with a flat facing -Y (toward the camera)."""
    o = cyl(name, radius, z0, z1, verts=sides)
    me = o.data
    m = Matrix.Rotation(math.radians(twist_deg), 4, 'Z')
    me.transform(m)
    return o

def ribbon(name, pts, halfw, thickness, flange=0.0015, edge_frac=0.86):
    """Sweep a stamped U-channel strip through the XZ path points in pts.

    A real grenade safety lever is not a flat ribbon: both long edges are folded
    toward the body for stiffness, which is what gives the head its visible
    depth.  Per path point we build a local frame (tangent T, width axis U = +Y,
    in-plane normal N) and lay 4 profile points across it, the outer two pushed
    along +N (which points at the body on the arm and downward over the head).
    """
    P = [Vector((x, 0.0, z)) for x, z in pts]
    n = len(P)
    bm = bmesh.new()
    rows = []
    for i in range(n):
        if   i == 0:     T = P[1] - P[0]
        elif i == n - 1: T = P[-1] - P[-2]
        else:            T = P[i + 1] - P[i - 1]
        T.normalize()
        U = Vector((0.0, 1.0, 0.0))
        N = Vector((-T.z, 0.0, T.x))
        w = halfw[i]
        prof = ((-w, flange), (-w * edge_frac, 0.0),
                ( w * edge_frac, 0.0), ( w, flange))
        rows.append([bm.verts.new(P[i] + U * u + N * nn) for u, nn in prof])
    for i in range(n - 1):
        a, b = rows[i], rows[i + 1]
        for k in range(3):
            bm.faces.new((a[k], b[k], b[k + 1], a[k + 1]))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.normal_update()
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    o = link(bpy.data.objects.new(name, me))
    s = o.modifiers.new("solid", 'SOLIDIFY')
    s.thickness = thickness
    s.offset = 0.0
    s.use_even_offset = True
    s.use_rim = True
    return o

def wire(name, pts, radius, res=6):
    """Round-section wire swept down a polyline (the cotter pin)."""
    cu = bpy.data.curves.new(name, 'CURVE')
    cu.dimensions = '3D'
    cu.bevel_depth = radius
    cu.bevel_resolution = res
    cu.use_fill_caps = True
    sp = cu.splines.new('POLY')
    sp.points.add(len(pts) - 1)
    for i, p in enumerate(pts):
        sp.points[i].co = (p[0], p[1], p[2], 1.0)
    o = link(bpy.data.objects.new(name, cu))
    set_active(o)
    bpy.ops.object.convert(target='MESH')
    return bpy.context.object


def yaw_world(obj, deg):
    obj.matrix_world = Matrix.Rotation(math.radians(deg), 4, 'Z') @ obj.matrix_world
    set_active(obj)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

purge()
print("=== building geometry ===")

# ------------------------------------------------------- 1. perforated body
body = cyl("Body", R_BODY, Z_BODY_BOT, Z_BODY_TOP, verts=96, fill='NOTHING')
sol = body.modifiers.new("solid", 'SOLIDIFY')
sol.thickness, sol.offset, sol.use_even_offset = WALL, -1.0, True
apply_mods(body)

cutters = []
for zi in HOLE_ROWS_Z:
    for c in range(HOLE_COLS):
        a = 2 * math.pi * c / HOLE_COLS + math.radians(HOLE_PHASE)
        cut = cyl(f"cut_{c}", R_HOLE, -0.006, 0.006, verts=32,
                  rot=(0, math.radians(90), a))
        cut.location = (math.cos(a) * 0.0148, math.sin(a) * 0.0148, zi)
        cutters.append(cut)
bpy.ops.object.select_all(action='DESELECT')
for c in cutters: c.select_set(True)
bpy.context.view_layer.objects.active = cutters[0]
bpy.ops.object.join()
holes = bpy.context.object
holes.name = "holes"

b = body.modifiers.new("holes", 'BOOLEAN')
b.object, b.operation, b.solver = holes, 'DIFFERENCE', 'EXACT'
apply_mods(body)
bpy.data.objects.remove(holes, do_unlink=True)
bevel(body, 0.00030, 2, angle=30.0)
apply_mods(body)
smooth(body, 30.0)

# ------------------------------------------- 2. inner pyrotechnic charge tube
core = cyl("Core", R_CORE, Z_CORE_BOT, Z_CORE_TOP, verts=64)
bevel(core, 0.0009, 2, angle=30.0)
apply_mods(core)
smooth(core, 30.0)

# ----------------------------------------------------- 3. hex base / octagon
base = prism("BaseNut", R_BASE, Z_BASE_BOT, Z_BASE_TOP, 6, 0.0)
bevel(base, 0.0013, 3, angle=25.0)
apply_mods(base); smooth(base, 25.0)

collar = prism("Collar", R_COLLAR, Z_COLLAR_BOT, Z_COLLAR_TOP, 8, 22.5)
bevel(collar, 0.0011, 3, angle=25.0)
apply_mods(collar); smooth(collar, 25.0)

# ------------------------------------------------------------- 4. fuze stack
neck = cyl("Neck", R_NECK, Z_COLLAR_TOP - 0.001, Z_NECK_TOP, verts=48)
bevel(neck, 0.0005, 2); apply_mods(neck); smooth(neck)

fuze = cyl("FuzeBody", R_FUZE, Z_NECK_TOP - 0.0005, Z_FUZE_TOP, verts=48)
bevel(fuze, 0.0012, 3); apply_mods(fuze); smooth(fuze)

striker = cyl("Striker", R_STRIKER, Z_FUZE_TOP - 0.0005, Z_STRIKER_TOP, verts=40)
bevel(striker, 0.0009, 3); apply_mods(striker); smooth(striker)

hinge = cyl("HingePin", 0.0013, -0.0038, 0.0038, verts=24,
            rot=(math.radians(90), 0, 0))
hinge.location = (0.0128, 0.0, 0.1300)
bevel(hinge, 0.0004, 2); apply_mods(hinge); smooth(hinge)

# --------------------------------------------------------- 5. safety lever
LEVER_PTS = [( 0.0148, 0.1250), ( 0.0157, 0.1288), ( 0.0152, 0.1316),
             ( 0.0120, 0.1331), ( 0.0046, 0.1336), (-0.0032, 0.1335),
             (-0.0102, 0.1326), (-0.0148, 0.1306), (-0.0172, 0.1268),
             (-0.0188, 0.1218), (-0.0215, 0.1155), (-0.0250, 0.1095),
             (-0.0265, 0.1010), (-0.0270, 0.0910), (-0.0274, 0.0760),
             (-0.0278, 0.0580), (-0.0282, 0.0440)]
LEVER_W   = [0.0088, 0.0094, 0.0099, 0.0100, 0.0100, 0.0098, 0.0092, 0.0084,
             0.0075, 0.0067, 0.0062, 0.0058, 0.0055, 0.0051, 0.0047, 0.0042,
             0.0038]
lever = ribbon("Lever", LEVER_PTS, LEVER_W, 0.0016, flange=0.0020)
apply_mods(lever)
bevel(lever, 0.00035, 2, angle=35.0)
apply_mods(lever); smooth(lever, 40.0)

# ------------------------------------------------------ 6. safety pin + ring
# A cotter pin: the shank runs through the fuze and the lever at y=0, then bends
# 90 deg toward the viewer so its looped head clears the OUTSIDE face of the
# lever.  Without that bend the ring plane sits at the lever mid-width and the
# ring visually saws through the handle instead of hanging beside it.
Z_PIN  = 0.1215
Y_RING = -0.0115          # lever half-width here is 6.7mm, so this is clear of it
BEND_R = 0.0022
X_BEND = -0.0202                      # just outboard of the lever outer face
_bc    = Vector((X_BEND, -BEND_R, Z_PIN))
pin_pts = [Vector((0.0060, 0.0, Z_PIN)), Vector((-0.0150, 0.0, Z_PIN))]
for _i in range(1, 9):
    _a = math.radians(90 + 90 * _i / 8.0)
    pin_pts.append(_bc + Vector((math.cos(_a), math.sin(_a), 0.0)) * BEND_R)
pin_pts.append(Vector((X_BEND - BEND_R, -0.0090, Z_PIN)))
pin = wire("SafetyPin", pin_pts, 0.0010)
smooth(pin, 40.0)

# ring plane faces the camera so it reads as a circle, and the whole plane is
# pushed out to Y_RING so it hangs in FRONT of the lever rather than through it
R_RING = 0.0122
E = Vector((X_BEND - BEND_R, Y_RING, Z_PIN))
n = Vector((-0.06, -0.985, 0.16)).normalized()
d = Vector((-0.745, 0.0, -0.667))
d = (d - d.project(n)).normalized()
u, v = d, n.cross(d)

bpy.ops.mesh.primitive_torus_add(major_radius=R_RING, minor_radius=0.0010,
                                 major_segments=72, minor_segments=14)
ring = bpy.context.object; ring.name = "PullRing"
smooth(ring)
M = Matrix((u, v, n)).transposed().to_4x4()
M.translation = E + d * R_RING          # puts the ring circle exactly through E
ring.matrix_world = M
set_active(ring); bpy.ops.object.transform_apply(rotation=True, scale=True)

# looped pin head at E, axis along the ring tangent so the ring threads it
bpy.ops.mesh.primitive_torus_add(major_radius=0.0026, minor_radius=0.00075,
                                 major_segments=28, minor_segments=10)
eye = bpy.context.object; eye.name = "PinEye"
smooth(eye)
ME = Matrix((n, u, v)).transposed().to_4x4()
ME.translation = E
eye.matrix_world = ME
set_active(eye); bpy.ops.object.transform_apply(rotation=True, scale=True)

# ---------------------------------------- swing the lever assembly off -X
for o in (lever, pin, eye, ring, hinge):
    yaw_world(o, ASSEMBLY_YAW)

PARTS = [body, core, base, collar, neck, fuze, striker, hinge,
         lever, pin, eye, ring]
print("parts:", ", ".join(p.name for p in PARTS))

# The lever has to run OUTSIDE the collar, which is the widest part of the
# grenade (octagon corner 22.5mm vs body 16.5mm). Verify rather than eyeball it.
from mathutils.bvhtree import BVHTree
_dg = bpy.context.evaluated_depsgraph_get()
def _bvh(o): return BVHTree.FromObject(o, _dg)
_HARD = (collar, body, base, neck, fuze, striker, core)
_clash = 0
# SafetyPin is deliberately excluded: it is meant to pass through the lever and
# the fuze body. Everything else must be genuinely clear.
for _src, _targets, _label in ((lever, _HARD,              "lever"),
                               (ring,  _HARD + (lever,),   "ring"),
                               (eye,   (lever,) + _HARD,   "eye")):
    _sb = _bvh(_src)
    for _o in _targets:
        _n = len(_sb.overlap(_bvh(_o)))
        _clash += _n
        if _n:
            print("  CLASH %s vs %s: %d overlapping faces" % (_label, _o.name, _n))
print("clearance check:", "OK" if _clash == 0 else "FAILED (%d)" % _clash)
for p in PARTS: p.data.calc_loop_triangles()
print("tris:", sum(len(p.data.loop_triangles) for p in PARTS))

# ============================================================== 7. materials
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

def geo(nt):
    return nt.nodes.new('ShaderNodeNewGeometry')

def noise(nt, scale, detail=8, rough=0.55, dist=0.0):
    n = nt.nodes.new('ShaderNodeTexNoise')
    n.inputs['Scale'].default_value      = scale
    n.inputs['Detail'].default_value     = detail
    n.inputs['Roughness'].default_value  = rough
    n.inputs['Distortion'].default_value = dist
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

def bumps(nt, coarse=(90.0, 0.16, 0.0007), fine=(650.0, 0.11, 0.00018)):
    b1 = nt.nodes.new('ShaderNodeBump')
    b1.inputs['Strength'].default_value = coarse[1]
    b1.inputs['Distance'].default_value = coarse[2]
    nt.links.new(noise(nt, coarse[0], 6, 0.6), b1.inputs['Height'])
    b2 = nt.nodes.new('ShaderNodeBump')
    b2.inputs['Strength'].default_value = fine[1]
    b2.inputs['Distance'].default_value = fine[2]
    nt.links.new(noise(nt, fine[0], 4, 0.5), b2.inputs['Height'])
    nt.links.new(b1.outputs['Normal'], b2.inputs['Normal'])
    return b2.outputs['Normal']

# ---- palette (linear) -----------------------------------------------------
OLIVE      = (0.052, 0.063, 0.036)
OLIVE_HW   = (0.043, 0.053, 0.031)
OLIVE_FUZE = (0.030, 0.037, 0.023)
CREAM      = (0.440, 0.480, 0.330)
COPPER     = (0.360, 0.150, 0.045)
REDCHG     = (0.300, 0.014, 0.018)
STEEL      = (0.560, 0.570, 0.580)
BAREMETAL  = (0.170, 0.165, 0.155)
RINGBLACK  = (0.013, 0.013, 0.014)

def edge_mask(nt, radius=0.0007, samples=8):
    # Convex/concave edge detector: the Bevel node normal diverges from the true
    # shading normal only at real edges. Unlike Pointiness this is scale-correct
    # and does NOT fire across a smoothly curved surface such as the body tube.
    bev = nt.nodes.new('ShaderNodeBevel')
    bev.samples = samples
    bev.inputs['Radius'].default_value = radius
    g2 = nt.nodes.new('ShaderNodeNewGeometry')
    dp = nt.nodes.new('ShaderNodeVectorMath'); dp.operation = 'DOT_PRODUCT'
    nt.links.new(bev.outputs['Normal'], dp.inputs[0])
    nt.links.new(g2.outputs['Normal'],  dp.inputs[1])
    return mth(nt, 'SUBTRACT', 1.0, dp.outputs['Value'], clamp=True)


def olive_material(name, col, rough=0.52, wear=0.55, band=False, metal_edge=0.55):
    # painted olive-drab steel; band=True adds the glow-in-the-dark stripe
    m, nt, bsdf = new_mat(name)
    g = geo(nt)
    blotch = ramp(nt, noise(nt, 38, 7, 0.58), [(0.36, (0.80, 0.80, 0.80, 1)),
                                               (0.66, (1.16, 1.16, 1.16, 1))])
    speck  = ramp(nt, noise(nt, 340, 5, 0.5),  [(0.40, (0.90, 0.90, 0.90, 1)),
                                                (0.62, (1.08, 1.08, 1.08, 1))])
    tone   = cmix(nt, 1.0, blotch, speck, 'MULTIPLY')
    base   = cmix(nt, 1.0, col, tone, 'MULTIPLY')
    rgh    = fmix(nt, ramp(nt, noise(nt, 60, 6, 0.55), [(0.38, (0, 0, 0, 1)),
                                                        (0.64, (1, 1, 1, 1))]),
                  rough - 0.09, rough + 0.10)

    if band:
        sep = nt.nodes.new('ShaderNodeSeparateXYZ')
        nt.links.new(g.outputs['Position'], sep.inputs['Vector'])
        # jitter the painted edge so the stripe is not machine-perfect
        jit = mth(nt, 'MULTIPLY', noise(nt, 55, 4, 0.5), 0.0011)
        jit = mth(nt, 'ADD', jit, -0.00055)
        z   = mth(nt, 'ADD', sep.outputs['Z'], jit)
        lo  = maprange(nt, z, Z_BAND_LO - 0.0003, Z_BAND_LO + 0.0003, 0.0, 1.0)
        hi  = maprange(nt, z, Z_BAND_HI - 0.0003, Z_BAND_HI + 0.0003, 1.0, 0.0)
        bmask = mth(nt, 'MULTIPLY', lo, hi)
        cream = cmix(nt, 1.0, CREAM, tone, 'MULTIPLY')
        base  = cmix(nt, bmask, base, cream)
        rgh   = fmix(nt, bmask, rgh, 0.36)

    # paint worn off the sharp edges down to bare metal
    wmask = ramp(nt, edge_mask(nt),
                 [(0.020, (0, 0, 0, 1)), (0.280, (1, 1, 1, 1))])
    wmask = mth(nt, 'MULTIPLY', wmask, wear)
    wmask = mth(nt, 'MULTIPLY', wmask,
                ramp(nt, noise(nt, 130, 6, 0.6),
                     [(0.35, (0.15, 0.15, 0.15, 1)), (0.60, (1, 1, 1, 1))]))
    base  = cmix(nt, wmask, base, BAREMETAL)
    rgh   = fmix(nt, wmask, rgh, 0.33)
    met   = mth(nt, 'MULTIPLY', wmask, metal_edge)

    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, rgh,  bsdf.inputs['Roughness'])
    plug(nt, met,  bsdf.inputs['Metallic'])
    nt.links.new(bumps(nt), bsdf.inputs['Normal'])
    return m

def metal_material(name, col, rough, metallic=1.0, wear=0.35):
    m, nt, bsdf = new_mat(name)
    g = geo(nt)
    tone = ramp(nt, noise(nt, 260, 6, 0.55), [(0.35, (0.72, 0.72, 0.72, 1)),
                                              (0.65, (1.20, 1.20, 1.20, 1))])
    base = cmix(nt, 1.0, col, tone, 'MULTIPLY')
    rgh  = fmix(nt, ramp(nt, noise(nt, 150, 6, 0.5), [(0.35, (0, 0, 0, 1)),
                                                      (0.65, (1, 1, 1, 1))]),
                rough - 0.08, rough + 0.12)
    pol  = ramp(nt, edge_mask(nt, 0.0005),
                [(0.020, (0, 0, 0, 1)), (0.280, (1, 1, 1, 1))])
    pol  = mth(nt, 'MULTIPLY', pol, wear)
    rgh  = fmix(nt, pol, rgh, max(0.06, rough - 0.18))
    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, rgh,  bsdf.inputs['Roughness'])
    plug(nt, metallic, bsdf.inputs['Metallic'])
    nt.links.new(bumps(nt, (200.0, 0.09, 0.00035), (900.0, 0.07, 0.00010)),
                 bsdf.inputs['Normal'])
    return m

def core_material(name):
    # copper pressed-charge body with the red band at the bottom
    m, nt, bsdf = new_mat(name)
    g   = geo(nt)
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    nt.links.new(g.outputs['Position'], sep.inputs['Vector'])
    tone = ramp(nt, noise(nt, 120, 7, 0.6), [(0.34, (0.68, 0.68, 0.68, 1)),
                                             (0.68, (1.22, 1.22, 1.22, 1))])
    red  = maprange(nt, sep.outputs['Z'], Z_CORE_RED - 0.0006,
                    Z_CORE_RED + 0.0006, 1.0, 0.0)
    col  = cmix(nt, red, COPPER, REDCHG)
    base = cmix(nt, 1.0, col, tone, 'MULTIPLY')
    rgh  = fmix(nt, red, 0.42, 0.58)
    met  = fmix(nt, red, 0.70, 0.05)
    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, rgh,  bsdf.inputs['Roughness'])
    plug(nt, met,  bsdf.inputs['Metallic'])
    nt.links.new(bumps(nt, (160.0, 0.14, 0.0005), (800.0, 0.10, 0.00012)),
                 bsdf.inputs['Normal'])
    return m

MATS = {
    'body'  : olive_material("SRC_Body",     OLIVE,      0.52, 0.60, band=True),
    'hw'    : olive_material("SRC_Hardware", OLIVE_HW,   0.50, 0.70),
    'fuze'  : olive_material("SRC_Fuze",     OLIVE_FUZE, 0.44, 0.45),
    'lever' : olive_material("SRC_Lever",    OLIVE_HW,   0.46, 0.85),
    'core'  : core_material("SRC_Core"),
    'steel' : metal_material("SRC_Steel",    STEEL,     0.26, 1.0, 0.55),
    'ring'  : metal_material("SRC_Ring",     RINGBLACK, 0.34, 0.85, 0.45),
}
ASSIGN = [(body, 'body'), (core, 'core'), (base, 'hw'), (collar, 'hw'),
          (neck, 'fuze'), (fuze, 'fuze'), (striker, 'fuze'), (hinge, 'steel'),
          (lever, 'lever'), (pin, 'steel'), (eye, 'steel'), (ring, 'ring')]
for obj, key in ASSIGN:
    obj.data.materials.clear()
    obj.data.materials.append(MATS[key])

# ================================================================== 8. UVs
print("=== uv unwrap ===")
for o in PARTS:
    if not o.data.uv_layers:
        o.data.uv_layers.new(name="UVMap")
    set_active(o)
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.select_all(action='SELECT')
    bpy.ops.uv.smart_project(angle_limit=math.radians(66), island_margin=0.003,
                             correct_aspect=True, scale_to_bounds=False)
    bpy.ops.object.mode_set(mode='OBJECT')

# pack every part into one shared 0-1 atlas at even texel density
bpy.ops.object.select_all(action='DESELECT')
for o in PARTS: o.select_set(True)
bpy.context.view_layer.objects.active = PARTS[0]
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.select_all(action='SELECT')
bpy.ops.uv.average_islands_scale()
# Island gap must comfortably exceed BAKE_MARGIN or each island bleeds its
# dilation into its neighbour (the body picked up the copper/red core strips).
bpy.ops.uv.pack_islands(rotate=True, scale=True, margin=0.008)
bpy.ops.object.mode_set(mode='OBJECT')

umin = umax = None
for o in PARTS:
    for uv in o.data.uv_layers[0].uv:
        u, v = uv.vector
        umin = (min(umin[0], u), min(umin[1], v)) if umin else (u, v)
        umax = (max(umax[0], u), max(umax[1], v)) if umax else (u, v)
print("uv bounds:", umin, umax)

# ================================================================= 9. bake
MAPS = {}
if DO_BAKE:
    print("=== baking %dpx atlas ===" % BAKE_RES)
    sc = bpy.context.scene
    sc.render.engine = 'CYCLES'
    sc.cycles.device = 'CPU'
    sc.render.bake.margin = BAKE_MARGIN
    sc.render.bake.margin_type = 'ADJACENT_FACES'
    sc.render.bake.use_selected_to_active = False
    sc.render.bake.use_pass_direct = False
    sc.render.bake.use_pass_indirect = False

    def make_img(name, non_color):
        img = bpy.data.images.new(name, BAKE_RES, BAKE_RES, alpha=False)
        img.colorspace_settings.name = 'Non-Color' if non_color else 'sRGB'
        return img

    def set_targets(img):
        made = []
        for m in MATS.values():
            nt = m.node_tree
            tn = nt.nodes.new('ShaderNodeTexImage')
            tn.image = img; tn.location = (300, -700)
            for n in nt.nodes: n.select = False
            tn.select = True
            nt.nodes.active = tn
            made.append((nt, tn))
        return made

    def drop_targets(made):
        for nt, tn in made: nt.nodes.remove(tn)

    def select_parts():
        bpy.ops.object.select_all(action='DESELECT')
        for o in PARTS: o.select_set(True)
        bpy.context.view_layer.objects.active = PARTS[0]

    def emit_swap(socket_name):
        # route a Principled input straight into an Emission so EMIT bakes it
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
            em.inputs['Strength'].default_value = 1.0
            nt.links.new(em.outputs['Emission'], out.inputs['Surface'])
            saved.append((nt, out, orig, em))
        return saved

    def emit_restore(saved):
        for nt, out, orig, em in saved:
            nt.nodes.remove(em)
            nt.links.new(orig, out.inputs['Surface'])

    def bake(kind, img, samples=1, **kw):
        sc.cycles.samples = samples
        made = set_targets(img)
        select_parts()
        bpy.ops.object.bake(type=kind, margin=BAKE_MARGIN, use_clear=True, **kw)
        drop_targets(made)

    MAPS['BaseColor'] = make_img("Flashbang_BaseColor", False)
    MAPS['Roughness'] = make_img("Flashbang_Roughness", True)
    MAPS['Metallic']  = make_img("Flashbang_Metallic",  True)
    MAPS['Normal']    = make_img("Flashbang_Normal",    True)
    MAPS['AO']        = make_img("Flashbang_AO",        True)

    sv = emit_swap('Base Color'); bake('EMIT', MAPS['BaseColor']); emit_restore(sv)
    print("  baked BaseColor")
    sv = emit_swap('Metallic');   bake('EMIT', MAPS['Metallic']);  emit_restore(sv)
    print("  baked Metallic")
    bake('ROUGHNESS', MAPS['Roughness']);  print("  baked Roughness")
    bake('NORMAL',    MAPS['Normal']);     print("  baked Normal")
    bake('AO',        MAPS['AO'], samples=192); print("  baked AO")

    for key, img in MAPS.items():
        p = os.path.join(TEXDIR, "Flashbang_%s.png" % key)
        img.filepath_raw = p
        img.file_format = 'PNG'
        img.save()
        print("  wrote", p)

    # ---- final material driven by the baked maps
    fm, fnt, fbsdf = new_mat("M_Flashbang")
    def tex(img, x, y):
        t = fnt.nodes.new('ShaderNodeTexImage')
        t.image = img; t.location = (x, y)
        return t
    tb = tex(MAPS['BaseColor'], -700,  420)
    tr = tex(MAPS['Roughness'], -700,  120)
    tm = tex(MAPS['Metallic'],  -700, -180)
    tnr= tex(MAPS['Normal'],    -700, -480)
    nmap = fnt.nodes.new('ShaderNodeNormalMap'); nmap.location = (-380, -480)
    fnt.links.new(tnr.outputs['Color'], nmap.inputs['Color'])
    fnt.links.new(tb.outputs['Color'],  fbsdf.inputs['Base Color'])
    fnt.links.new(tr.outputs['Color'],  fbsdf.inputs['Roughness'])
    fnt.links.new(tm.outputs['Color'],  fbsdf.inputs['Metallic'])
    fnt.links.new(nmap.outputs['Normal'], fbsdf.inputs['Normal'])
    for o in PARTS:
        o.data.materials.clear()
        o.data.materials.append(fm)
    for m in MATS.values():
        m.use_fake_user = True
    print("  assigned M_Flashbang")

# ============================================== 10. studio, camera, previews
print("=== studio ===")
sc = bpy.context.scene
sc.view_settings.view_transform = 'Standard'
sc.view_settings.look = 'None'
sc.render.film_transparent = False
sc.render.resolution_x, sc.render.resolution_y = 900, 1200
sc.render.image_settings.file_format = 'PNG'
sc.render.resolution_percentage = 55 if 'fast' in argv else 100

world = bpy.data.worlds.new("Studio")
sc.world = world
world.use_nodes = True
wnt = world.node_tree
wbg = next(n for n in wnt.nodes if n.type == 'BACKGROUND')
wbg.inputs['Color'].default_value = (1, 1, 1, 1)
# Camera rays see a pure white sweep from every azimuth; every other ray sees a
# dim environment, so the background stays clean without flooding the subject.
wlp = wnt.nodes.new('ShaderNodeLightPath')
wnt.links.new(fmix(wnt, wlp.outputs['Is Camera Ray'], 0.12, 1.0),
              wbg.inputs['Strength'])

# white sweep behind the subject: reads as a clean background and rim-lights it
bpy.ops.mesh.primitive_plane_add(size=3.0, location=(0, 0.42, 0.07),
                                 rotation=(math.radians(90), 0, 0))
sweep = bpy.context.object; sweep.name = "Backdrop"
bm_, bnt, bbsdf = new_mat("M_Backdrop")
bnt.nodes.remove(bbsdf)
bem = bnt.nodes.new('ShaderNodeEmission')
bem.inputs['Color'].default_value = (1, 1, 1, 1)
bem.inputs['Strength'].default_value = 1.0
bout = next(n for n in bnt.nodes if n.type == 'OUTPUT_MATERIAL')
bnt.links.new(bem.outputs['Emission'], bout.inputs['Surface'])
sweep.data.materials.append(bm_)
# camera rays + reflections only, so the sweep reads pure white and gives the
# metal something to mirror without flooding the subject with diffuse bounce
sweep.visible_camera = False
sweep.visible_shadow = False
sweep.visible_diffuse = False
sweep.visible_transmission = False
sweep.visible_volume_scatter = False

def area_light(name, loc, size, power, target=(0, 0, 0.072)):
    d = bpy.data.lights.new(name, 'AREA')
    d.size, d.energy, d.shape = size, power, 'SQUARE'
    o = link(bpy.data.objects.new(name, d))
    o.location = loc
    o.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    return o

area_light("Key",  (-0.30, -0.34, 0.30), 0.30, 10.0)
area_light("Fill", ( 0.36, -0.26, 0.11), 0.45,  4.0)
area_light("Top",  ( 0.05, -0.06, 0.46), 0.28,  5.0)
area_light("Rim",  (-0.16,  0.30, 0.22), 0.30,  7.0)

cd = bpy.data.cameras.new("Cam")
cd.lens, cd.sensor_width = 85.0, 36.0
cam = link(bpy.data.objects.new("Cam", cd))
sc.camera = cam

def place_cam(az_deg, elev_deg, dist, target, lens=85.0):
    az, el = math.radians(az_deg), math.radians(elev_deg)
    d = Vector((math.sin(az) * math.cos(el), -math.cos(az) * math.cos(el), math.sin(el)))
    cam.data.lens = lens
    cam.location = Vector(target) + d * dist
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat('-Z', 'Y').to_euler()

ENGINE = 'eevee' if 'eevee' in argv else 'cycles'
ids = [i.identifier for i in bpy.types.RenderSettings.bl_rna.properties['engine'].enum_items]
if ENGINE == 'cycles':
    sc.render.engine = 'CYCLES'
    sc.cycles.device = 'CPU'
    sc.cycles.samples = 24 if 'fast' in argv else 96
    sc.cycles.use_denoising = True
    sc.cycles.max_bounces = 8
else:
    for cand in ('BLENDER_EEVEE_NEXT', 'BLENDER_EEVEE'):
        if cand in ids:
            sc.render.engine = cand
            break
print("render engine:", sc.render.engine)

VIEWS = [
    ("front",   0.0,   2.0, 0.44, (0, 0, 0.070), 85.0, (900, 1200)),
    ("quarter", 38.0,  9.0, 0.44, (0, 0, 0.070), 85.0, (900, 1200)),
    ("side",   -78.0,  3.0, 0.44, (0, 0, 0.070), 85.0, (900, 1200)),
    ("fuze",     4.0,  6.0, 0.150, (-0.006, 0, 0.116), 85.0, (1200, 780)),
]
for name, az, el, dist, tgt, lens, res in VIEWS:
    sc.render.resolution_x, sc.render.resolution_y = res
    place_cam(az, el, dist, tgt, lens)
    sc.render.filepath = os.path.join(PREVIEW, "fb_%s.png" % name)
    bpy.ops.render.render(write_still=True)
    print("  rendered", sc.render.filepath)

# ============================================================== 11. save
# group the parts so the asset moves as one unit, origin at the base centre
root = link(bpy.data.objects.new("Flashbang", None))
root.empty_display_type = 'PLAIN_AXES'
root.empty_display_size = 0.03
for o in PARTS:
    o.parent = root
    o.matrix_parent_inverse = root.matrix_world.inverted()

if DO_SAVE:
    for img in MAPS.values():
        img.source = 'FILE'
        img.filepath = os.path.join(TEXDIR, os.path.basename(img.filepath_raw))
        img.reload()
    blend = os.path.join(ROOT, "Flashbang.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    try:
        bpy.ops.file.make_paths_relative()
        bpy.ops.wm.save_mainfile()
    except Exception as e:
        print("  relpath skipped:", e)
    print("saved", blend)

tot = 0
for p in PARTS:
    p.data.calc_loop_triangles()
    tot += len(p.data.loop_triangles)
print("TOTAL TRIS:", tot)
print("BUILD DONE")
