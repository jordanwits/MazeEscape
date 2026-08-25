# ---------------------------------------------------------------------------
# Decoy grenade - procedural build, UV, bake, render.  Its own scene/file.
# Blender 5.1 headless:  blender.exe -b --python build_decoygrenade.py -- [flags]
#   nobake  skip the texture bake (fast model iteration)
#   nosave  do not write the .blend
#   fast    low-sample preview renders
#
# Inspired by the reference, deliberately NOT a copy: the lattice is a full
# wrapping shoulder band (not a one-sided patch), the ribs are wider flat-topped
# corrugations rather than fine threads, a smooth collar separates the two
# zones, and the body is a taller egg rather than a squat sphere.
# No text anywhere on the model or in the texture.
# ---------------------------------------------------------------------------
import bpy, bmesh, math, os, sys
from mathutils import Vector, Matrix

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
DO_BAKE = "nobake" not in argv
DO_SAVE = "nosave" not in argv

ROOT    = r"H:/Unity/Maze Escape/Blender"
TEXDIR  = os.path.join(ROOT, "textures")
PREVIEW = r"C:/Users/jorda/AppData/Local/Temp/claude/H--Unity-Maze-Escape/3f78cba3-ef6d-4b8f-8e5f-f96525b88270/scratchpad"
os.makedirs(TEXDIR, exist_ok=True)
os.makedirs(PREVIEW, exist_ok=True)
BAKE_RES    = 2048
BAKE_MARGIN = 6

# --------------------------------------------------------------- dimensions
R_SPHERE = 0.0295                 # 59 mm ball
R_NECK   = 0.0115
Z_BOT    = 0.0000
Z_CEN    = R_SPHERE               # south pole sits on z=0
Z_TOP    = Z_CEN + math.sqrt(R_SPHERE ** 2 - R_NECK ** 2)   # cut at neck radius

RIB_LO, RIB_HI = 0.0060, 0.0265   # corrugated zone, lower hemisphere
RIB_PERIOD     = 0.0042
RIB_AMP        = 0.00095

STRIPE_LO, STRIPE_HI = 0.0300, 0.0520   # 4 wrapping stripes, upper hemisphere
STRIPE_COUNT = 4
STRIPE_GAP   = 0.26               # fraction of each band left as bare shell
TH_LO = math.acos((STRIPE_LO - Z_CEN) / R_SPHERE)   # polar angle at band bottom
TH_HI = math.acos((STRIPE_HI - Z_CEN) / R_SPHERE)   # ...and at band top

Z_NECK_TOP = Z_TOP + 0.0045
R_CAP      = 0.0136
Z_CAP_TOP  = Z_NECK_TOP + 0.0080
R_STRIKER  = 0.0082
Z_STR_TOP  = Z_CAP_TOP + 0.0060

Z_PIN      = Z_CAP_TOP - 0.0027
ASSEMBLY_YAW = 32.0

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
            print("  ! modifier %s on %s: %s" % (m.name, obj.name, e))
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

def cyl(name, radius, z0, z1, verts=64, fill='NGON', rot=None, loc=None):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=radius,
                                        depth=(z1 - z0), end_fill_type=fill,
                                        location=(0, 0, (z0 + z1) * 0.5))
    o = bpy.context.object
    o.name = name
    if rot: o.rotation_euler = rot
    if loc: o.location = loc
    return o

def wire(name, pts, radius, res=6):
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

def ribbon(name, pts, halfw, thickness, flange=0.0015, edge_frac=0.86):
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

def yaw_world(obj, deg):
    obj.matrix_world = Matrix.Rotation(math.radians(deg), 4, 'Z') @ obj.matrix_world
    set_active(obj)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

def smoothstep(a, b, x):
    if b <= a: return 0.0 if x < a else 1.0
    t = min(1.0, max(0.0, (x - a) / (b - a)))
    return t * t * (3.0 - 2.0 * t)

# ------------------------------------------------- body silhouette + normals
def r_base(z):
    """True sphere, truncated flat where it meets the neck."""
    z = min(max(z, Z_BOT), Z_TOP)
    d = z - Z_CEN
    v = R_SPHERE * R_SPHERE - d * d
    return math.sqrt(v) if v > 0.0 else 0.0

def profile_normal(z, h=1e-5):
    """Outward normal of the silhouette in the (r,z) plane."""
    z0 = max(Z_BOT + 1e-6, z - h)
    z1 = min(Z_TOP - 1e-6, z + h)
    dr = r_base(z1) - r_base(z0)
    dz = z1 - z0
    n = Vector((dz, -dr))
    if n.length < 1e-12: return Vector((1.0, 0.0))
    n.normalize()
    if n.x < 0: n = -n
    return n

def rib_mask(z):
    return smoothstep(RIB_LO, RIB_LO + 0.005, z) * (1.0 - smoothstep(RIB_HI - 0.005, RIB_HI + 0.004, z))

purge()
print("=== building geometry ===")

# --------------------------------------------------------- 1. ribbed body
# Dense rings ONLY through the corrugated zone; the smooth cap and shoulder need
# far fewer. Uniform sampling fine enough for the ribs cost 100k+ tris on its own.
def _span(z0, z1, n, skip_first=False):
    out = []
    for i in range(n + 1):
        if skip_first and i == 0:
            continue
        out.append(z0 + (z1 - z0) * i / float(n))
    return out

zs = (_span(Z_BOT, RIB_LO, 22)
      + _span(RIB_LO, RIB_HI + 0.004, 96, True)
      + _span(RIB_HI + 0.004, Z_TOP, 40, True))

# arc length along the base silhouette, so ribs are evenly spaced ON the surface
arc = [0.0]
for i in range(1, len(zs)):
    dr = r_base(zs[i]) - r_base(zs[i - 1])
    dz = zs[i] - zs[i - 1]
    arc.append(arc[-1] + math.hypot(dr, dz))

prof = []
for i, z in enumerate(zs):
    rb = r_base(z)
    ripple = 0.5 * (1.0 - math.cos(2.0 * math.pi * arc[i] / RIB_PERIOD))
    ripple = ripple ** 0.7                     # flat-topped corrugation
    off = RIB_AMP * ripple * rib_mask(z)
    n = profile_normal(z)
    prof.append((max(0.0, rb + n.x * off), z + n.y * off))

def lathe(name, points, seg=128):
    bm = bmesh.new()
    rings = []
    for r, z in points:
        if r < 1e-7:
            rings.append([bm.verts.new((0.0, 0.0, z))])
        else:
            rings.append([bm.verts.new((math.cos(2 * math.pi * i / seg) * r,
                                        math.sin(2 * math.pi * i / seg) * r, z))
                          for i in range(seg)])
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        if len(a) == 1:
            for j in range(seg):
                bm.faces.new((a[0], b[j], b[(j + 1) % seg]))
        elif len(b) == 1:
            for j in range(seg):
                bm.faces.new((a[j], a[(j + 1) % seg], b[0]))
        else:
            for j in range(seg):
                bm.faces.new((a[j], a[(j + 1) % seg], b[(j + 1) % seg], b[j]))
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-6)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.normal_update()
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me); bm.free()
    return link(bpy.data.objects.new(name, me))

body = lathe("Body", prof, seg=88)
smooth(body, 40.0)

# The stripes are NOT geometry - they are painted into the body texture as a
# green decal by stripe_mask() below, driven from world Z. See STRIPE_*.

# ---------------------------------------------------------- 3. fuze stack
neck = cyl("Neck", R_NECK, Z_TOP - 0.004, Z_NECK_TOP, verts=64)
bevel(neck, 0.0007, 2); apply_mods(neck); smooth(neck)

cap = cyl("FuzeCap", R_CAP, Z_NECK_TOP - 0.0006, Z_CAP_TOP, verts=64)
bevel(cap, 0.0013, 3); apply_mods(cap); smooth(cap)

striker = cyl("Striker", R_STRIKER, Z_CAP_TOP - 0.0006, Z_STR_TOP, verts=48)
bevel(striker, 0.0010, 3); apply_mods(striker); smooth(striker)

hinge = cyl("HingePin", 0.0014, -0.0040, 0.0040, verts=24,
            rot=(math.radians(90), 0, 0))
hinge.location = (0.0132, 0.0, 0.0962)
bevel(hinge, 0.0004, 2); apply_mods(hinge); smooth(hinge)

# ----------------------------------------------------------- 4. safety lever
def envelope_r(z):
    """Widest thing at this height, so the lever can stand off from all of it."""
    e = 0.0
    if Z_BOT <= z <= Z_TOP:
        e = max(e, r_base(z))
    if Z_TOP - 0.004 <= z <= Z_NECK_TOP:  e = max(e, R_NECK)
    if Z_NECK_TOP - 0.001 <= z <= Z_CAP_TOP: e = max(e, R_CAP)
    if Z_CAP_TOP - 0.001 <= z <= Z_STR_TOP:  e = max(e, R_STRIKER)
    return e

# Standoff has to clear the lattice AND the inward-turned U-channel flange on
# the lever itself (2.0mm flange + 0.8mm half thickness), otherwise the arm
# saws straight through the tiles.
LEVER_STANDOFF = 0.0062
LEVER_PTS = [( 0.0150, Z_STR_TOP - 0.0077), ( 0.0157, Z_STR_TOP - 0.0041),
             ( 0.0150, Z_STR_TOP - 0.0013), ( 0.0112, Z_STR_TOP + 0.0005),
             ( 0.0034, Z_STR_TOP + 0.0012), (-0.0044, Z_STR_TOP + 0.0010),
             (-0.0106, Z_STR_TOP - 0.0005), (-0.0144, Z_STR_TOP - 0.0033)]
LEVER_W   = [0.0086, 0.0091, 0.0096, 0.0098, 0.0098, 0.0096, 0.0090, 0.0082]
ARM_ZS = [Z_CAP_TOP - 0.0020, 0.0640, 0.0608, 0.0576, 0.0544, 0.0512,
          0.0480, 0.0445, 0.0410, 0.0375, 0.0340, 0.0295, 0.0250]
for _i, _z in enumerate(ARM_ZS):
    _t = _i / float(len(ARM_ZS) - 1)
    LEVER_PTS.append((-(envelope_r(_z) + LEVER_STANDOFF), _z))
    LEVER_W.append(0.0076 * (1.0 - _t) + 0.0039 * _t)
assert len(LEVER_PTS) == len(LEVER_W), (len(LEVER_PTS), len(LEVER_W))
lever = ribbon("Lever", LEVER_PTS, LEVER_W, 0.0016, flange=0.0020)
apply_mods(lever)
bevel(lever, 0.00035, 2, angle=35.0)
apply_mods(lever); smooth(lever, 40.0)

# ------------------------------------------------------ 5. cotter pin + ring
Y_RING = -0.0118
BEND_R = 0.0022
X_BEND = -0.0205
_bc = Vector((X_BEND, -BEND_R, Z_PIN))
pin_pts = [Vector((0.0058, 0.0, Z_PIN)), Vector((-0.0150, 0.0, Z_PIN))]
for _i in range(1, 9):
    _a = math.radians(90 + 90 * _i / 8.0)
    pin_pts.append(_bc + Vector((math.cos(_a), math.sin(_a), 0.0)) * BEND_R)
pin_pts.append(Vector((X_BEND - BEND_R, -0.0092, Z_PIN)))
pin = wire("SafetyPin", pin_pts, 0.0010)
smooth(pin, 40.0)

R_RING = 0.0112
E = Vector((X_BEND - BEND_R, Y_RING, Z_PIN))
n = Vector((-0.06, -0.985, 0.16)).normalized()
d = Vector((-0.760, 0.0, -0.650))
d = (d - d.project(n)).normalized()
u, v = d, n.cross(d)
bpy.ops.mesh.primitive_torus_add(major_radius=R_RING, minor_radius=0.0011,
                                 major_segments=64, minor_segments=12)
ring = bpy.context.object; ring.name = "PullRing"
smooth(ring)
M = Matrix((u, v, n)).transposed().to_4x4()
M.translation = E + d * R_RING
ring.matrix_world = M
set_active(ring); bpy.ops.object.transform_apply(rotation=True, scale=True)

bpy.ops.mesh.primitive_torus_add(major_radius=0.0026, minor_radius=0.00075,
                                 major_segments=24, minor_segments=10)
eye = bpy.context.object; eye.name = "PinEye"
smooth(eye)
ME = Matrix((n, u, v)).transposed().to_4x4()
ME.translation = E
eye.matrix_world = ME
set_active(eye); bpy.ops.object.transform_apply(rotation=True, scale=True)

for o in (lever, pin, eye, ring, hinge):
    yaw_world(o, ASSEMBLY_YAW)

PARTS = [body, neck, cap, striker, hinge, lever, pin, eye, ring]
print("parts:", ", ".join(p.name for p in PARTS))

from mathutils.bvhtree import BVHTree
_dg = bpy.context.evaluated_depsgraph_get()
def _bvh(o): return BVHTree.FromObject(o, _dg)
_HARD = (body, neck, cap, striker)
_clash = 0
for _src, _targets, _label in ((lever, _HARD, "lever"),
                               (ring,  _HARD + (lever,), "ring"),
                               (eye,   _HARD + (lever,), "eye")):
    _sb = _bvh(_src)
    for _o in _targets:
        _n = len(_sb.overlap(_bvh(_o)))
        _clash += _n
        if _n:
            print("  CLASH %s vs %s: %d faces" % (_label, _o.name, _n))
print("clearance check:", "OK" if _clash == 0 else "FAILED (%d)" % _clash)

# ============================================================== 6. materials
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
    for i, x in ((1, f0), (2, f1), (3, t0), (4, t1)):
        n.inputs[i].default_value = x
    return n.outputs[0]

def bumps(nt, coarse=(110.0, 0.15, 0.0006), fine=(700.0, 0.10, 0.00016)):
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

def edge_mask(nt, radius=0.0006, samples=8):
    bev = nt.nodes.new('ShaderNodeBevel')
    bev.samples = samples
    bev.inputs['Radius'].default_value = radius
    g2 = nt.nodes.new('ShaderNodeNewGeometry')
    dp = nt.nodes.new('ShaderNodeVectorMath'); dp.operation = 'DOT_PRODUCT'
    nt.links.new(bev.outputs['Normal'], dp.inputs[0])
    nt.links.new(g2.outputs['Normal'],  dp.inputs[1])
    return mth(nt, 'SUBTRACT', 1.0, dp.outputs['Value'], clamp=True)

def cavity(nt, dist=0.0035, samples=8):
    """AO Fac: ~1 on exposed rib crests, lower down in the grooves."""
    ao = nt.nodes.new('ShaderNodeAmbientOcclusion')
    ao.samples = samples
    ao.only_local = True
    ao.inputs['Distance'].default_value = dist
    return ao.outputs['AO']

def stripe_mask(nt, soft=0.010):
    """Four continuous stripes wrapping the upper body, from world Z alone.

    There is deliberately no angular term, so each band is an unbroken ring
    around the shell rather than a row of separate cells.
    """
    g = geo(nt)
    sep = nt.nodes.new('ShaderNodeSeparateXYZ')
    nt.links.new(g.outputs['Position'], sep.inputs['Vector'])

    # Spaced by POLAR ANGLE, not by Z: equal Z slices on a sphere give unequal
    # surface widths (the top band came out ~34% wider than the bottom), and
    # acos((z-Z_CEN)/R) is proportional to arc length. Clamped because the rib
    # offset can push a vertex a hair outside the sphere near the poles.
    d  = mth(nt, 'DIVIDE', mth(nt, 'SUBTRACT', sep.outputs['Z'], Z_CEN), R_SPHERE)
    d  = mth(nt, 'MAXIMUM', mth(nt, 'MINIMUM', d, 1.0), -1.0)
    th = mth(nt, 'ARCCOSINE', d)
    v  = mth(nt, 'DIVIDE', mth(nt, 'SUBTRACT', TH_LO, th), (TH_LO - TH_HI))
    cv = mth(nt, 'FRACT', mth(nt, 'MULTIPLY', v, float(STRIPE_COUNT)))
    lo = maprange(nt, cv, STRIPE_GAP * 0.5 - soft, STRIPE_GAP * 0.5 + soft, 0.0, 1.0)
    hi = maprange(nt, cv, 1.0 - STRIPE_GAP * 0.5 - soft,
                  1.0 - STRIPE_GAP * 0.5 + soft, 1.0, 0.0)
    m  = mth(nt, 'MULTIPLY', lo, hi)
    b0 = maprange(nt, v, -0.004, 0.004, 0.0, 1.0)
    b1 = maprange(nt, v,  0.996, 1.004, 1.0, 0.0)
    return mth(nt, 'MULTIPLY', m, mth(nt, 'MULTIPLY', b0, b1))

# ---- palette (linear) -----------------------------------------------------
CHARCOAL = (0.0150, 0.0158, 0.0172)
GREEN    = (0.0450, 0.1050, 0.0300)   # lattice decal
FUZEBLK  = (0.0105, 0.0105, 0.0118)
LEVERBLK = (0.0135, 0.0138, 0.0152)
STEEL    = (0.5500, 0.5600, 0.5700)
RINGBLK  = (0.0190, 0.0190, 0.0210)
BAREMTL  = (0.0980, 0.0950, 0.0900)

def shell_material(name, col, rough=0.50, wear=0.55, grime=0.0, metal_edge=0.40,
                   sticker=None, sticker_rough=0.32):
    """Painted steel. grime>0 darkens the rib valleys.

    Do NOT try to lighten the rib crests with an AO node: AO reads ~1.0 on every
    exposed surface of a convex body, so it wears the entire shell to bare metal
    rather than just the high points. Darkening the cavities is the workable
    half of the effect; crest wear comes from the Bevel-node edge mask, which
    genuinely fires on the tight rib curvature.
    """
    m, nt, bsdf = new_mat(name)
    blotch = ramp(nt, noise(nt, 42, 7, 0.58), [(0.34, (0.72, 0.72, 0.72, 1)),
                                               (0.68, (1.22, 1.22, 1.22, 1))])
    speck  = ramp(nt, noise(nt, 360, 5, 0.5),  [(0.40, (0.88, 0.88, 0.88, 1)),
                                                (0.62, (1.10, 1.10, 1.10, 1))])
    tone   = cmix(nt, 1.0, blotch, speck, 'MULTIPLY')
    base   = cmix(nt, 1.0, col, tone, 'MULTIPLY')
    rgh    = fmix(nt, ramp(nt, noise(nt, 65, 6, 0.55), [(0.38, (0, 0, 0, 1)),
                                                        (0.64, (1, 1, 1, 1))]),
                  rough - 0.09, rough + 0.11)

    if grime > 0.0:
        gm = ramp(nt, cavity(nt, 0.0030),
                  [(0.70, (0.45, 0.44, 0.40, 1)), (0.97, (1, 1, 1, 1))])
        base = cmix(nt, grime, base, cmix(nt, 1.0, base, gm, 'MULTIPLY'))
    smask = None
    if sticker is not None:
        smask = stripe_mask(nt)
        base = cmix(nt, smask, base, cmix(nt, 1.0, sticker, tone, 'MULTIPLY'))
        rgh  = fmix(nt, smask, rgh, sticker_rough)
    wmask = ramp(nt, edge_mask(nt), [(0.020, (0, 0, 0, 1)), (0.280, (1, 1, 1, 1))])
    wmask = mth(nt, 'MULTIPLY', wmask, wear)
    wmask = mth(nt, 'MULTIPLY', wmask,
                ramp(nt, noise(nt, 140, 6, 0.6),
                     [(0.33, (0.10, 0.10, 0.10, 1)), (0.60, (1, 1, 1, 1))]))
    base = cmix(nt, wmask, base, BAREMTL)
    rgh  = fmix(nt, wmask, rgh, 0.34)
    met  = mth(nt, 'MULTIPLY', wmask, metal_edge)

    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, rgh,  bsdf.inputs['Roughness'])
    plug(nt, met,  bsdf.inputs['Metallic'])
    nrm = bumps(nt)
    if smask is not None:
        # printed vinyl, not a raised cube: only enough lip to catch a rim light
        lip = nt.nodes.new('ShaderNodeBump')
        lip.inputs['Strength'].default_value = 0.35
        lip.inputs['Distance'].default_value = 0.00007
        nt.links.new(smask, lip.inputs['Height'])
        nt.links.new(nrm, lip.inputs['Normal'])
        nrm = lip.outputs['Normal']
    nt.links.new(nrm, bsdf.inputs['Normal'])
    return m

def metal_material(name, col, rough, metallic=1.0, wear=0.35):
    m, nt, bsdf = new_mat(name)
    tone = ramp(nt, noise(nt, 280, 6, 0.55), [(0.35, (0.72, 0.72, 0.72, 1)),
                                              (0.65, (1.22, 1.22, 1.22, 1))])
    base = cmix(nt, 1.0, col, tone, 'MULTIPLY')
    rgh  = fmix(nt, ramp(nt, noise(nt, 160, 6, 0.5), [(0.35, (0, 0, 0, 1)),
                                                      (0.65, (1, 1, 1, 1))]),
                rough - 0.08, rough + 0.12)
    pol  = mth(nt, 'MULTIPLY',
               ramp(nt, edge_mask(nt, 0.0005), [(0.020, (0, 0, 0, 1)), (0.280, (1, 1, 1, 1))]),
               wear)
    rgh  = fmix(nt, pol, rgh, max(0.06, rough - 0.18))
    plug(nt, base, bsdf.inputs['Base Color'])
    plug(nt, rgh,  bsdf.inputs['Roughness'])
    plug(nt, metallic, bsdf.inputs['Metallic'])
    nt.links.new(bumps(nt, (220.0, 0.09, 0.00035), (950.0, 0.07, 0.00010)),
                 bsdf.inputs['Normal'])
    return m

MATS = {
    'body'  : shell_material("SRC_Body",  CHARCOAL, 0.50, 0.26, grime=0.65,
                             sticker=GREEN),
    'fuze'  : shell_material("SRC_Fuze",  FUZEBLK,  0.40, 0.26),
    'lever' : shell_material("SRC_Lever", LEVERBLK, 0.42, 0.42),
    'steel' : metal_material("SRC_Steel", STEEL,    0.26, 1.0, 0.55),
    'ring'  : metal_material("SRC_Ring",  RINGBLK,  0.32, 0.85, 0.45),
}
ASSIGN = [(body, 'body'), (neck, 'fuze'), (cap, 'fuze'),
          (striker, 'fuze'), (hinge, 'steel'), (lever, 'lever'),
          (pin, 'steel'), (eye, 'steel'), (ring, 'ring')]
for obj, key in ASSIGN:
    obj.data.materials.clear()
    obj.data.materials.append(MATS[key])

# ================================================================== 7. UVs
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

bpy.ops.object.select_all(action='DESELECT')
for o in PARTS: o.select_set(True)
bpy.context.view_layer.objects.active = PARTS[0]
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.select_all(action='SELECT')
bpy.ops.uv.average_islands_scale()
# gap must stay well above BAKE_MARGIN or islands bleed into each other
bpy.ops.uv.pack_islands(rotate=True, scale=True, margin=0.008)
bpy.ops.object.mode_set(mode='OBJECT')

umin = umax = None
for o in PARTS:
    for uv in o.data.uv_layers[0].uv:
        a, b = uv.vector
        umin = (min(umin[0], a), min(umin[1], b)) if umin else (a, b)
        umax = (max(umax[0], a), max(umax[1], b)) if umax else (a, b)
print("uv bounds:", umin, umax)

# ================================================================= 8. bake
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

    MAPS['BaseColor'] = make_img("Decoy_BaseColor", False)
    MAPS['Roughness'] = make_img("Decoy_Roughness", True)
    MAPS['Metallic']  = make_img("Decoy_Metallic",  True)
    MAPS['Normal']    = make_img("Decoy_Normal",    True)
    MAPS['AO']        = make_img("Decoy_AO",        True)

    sv = emit_swap('Base Color'); bake('EMIT', MAPS['BaseColor'], 8); emit_restore(sv)
    print("  baked BaseColor")
    sv = emit_swap('Metallic');   bake('EMIT', MAPS['Metallic'], 8);  emit_restore(sv)
    print("  baked Metallic")
    bake('ROUGHNESS', MAPS['Roughness'], 8); print("  baked Roughness")
    bake('NORMAL',    MAPS['Normal']);       print("  baked Normal")
    bake('AO',        MAPS['AO'], 192);      print("  baked AO")

    for key, img in MAPS.items():
        p = os.path.join(TEXDIR, "DecoyGrenade_%s.png" % key)
        img.filepath_raw = p
        img.file_format = 'PNG'
        img.save()
        print("  wrote", p)

    fm, fnt, fbsdf = new_mat("M_DecoyGrenade")
    def tex(img, x, y):
        t = fnt.nodes.new('ShaderNodeTexImage')
        t.image = img; t.location = (x, y)
        return t
    tb  = tex(MAPS['BaseColor'], -700,  420)
    tr  = tex(MAPS['Roughness'], -700,  120)
    tm  = tex(MAPS['Metallic'],  -700, -180)
    tnr = tex(MAPS['Normal'],    -700, -480)
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
    print("  assigned M_DecoyGrenade")

# ============================================== 9. studio, camera, previews
print("=== studio ===")
sc = bpy.context.scene
sc.view_settings.view_transform = 'Standard'
sc.view_settings.look = 'None'
sc.render.film_transparent = False
sc.render.image_settings.file_format = 'PNG'
sc.render.resolution_percentage = 55 if 'fast' in argv else 100

world = bpy.data.worlds.new("Studio")
sc.world = world
world.use_nodes = True
wnt = world.node_tree
wbg = next(n for n in wnt.nodes if n.type == 'BACKGROUND')
wbg.inputs['Color'].default_value = (1, 1, 1, 1)
wlp = wnt.nodes.new('ShaderNodeLightPath')
wnt.links.new(fmix(wnt, wlp.outputs['Is Camera Ray'], 0.13, 1.0),
              wbg.inputs['Strength'])

bpy.ops.mesh.primitive_plane_add(size=3.0, location=(0, 0.32, 0.035),
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
sweep.visible_camera = False
sweep.visible_shadow = False
sweep.visible_diffuse = False
sweep.visible_transmission = False
sweep.visible_volume_scatter = False

def area_light(name, loc, size, power, target=(0, 0, 0.034)):
    d = bpy.data.lights.new(name, 'AREA')
    d.size, d.energy, d.shape = size, power, 'SQUARE'
    o = link(bpy.data.objects.new(name, d))
    o.location = loc
    o.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    return o

# An area light of power P gives E = P/(pi*d^2) at distance d, NOT P/(4*pi*d^2).
# Sizing for the latter over-lights by 4x, which washes a correct dark-paint
# albedo (~0.022 linear) out to mid grey.
area_light("Key",  (-0.28, -0.32, 0.30), 0.30, 4.5)
area_light("Fill", ( 0.34, -0.24, 0.10), 0.45, 1.8)
area_light("Top",  ( 0.05, -0.06, 0.44), 0.28, 2.4)
area_light("Rim",  (-0.14,  0.28, 0.22), 0.30, 3.2)

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

sc.render.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.samples = 24 if 'fast' in argv else 96
sc.cycles.use_denoising = True
sc.cycles.max_bounces = 8

VIEWS = [
    ("front",   0.0,   4.0, 0.255, (0, 0, 0.034), 85.0, (900, 1050)),
    ("quarter", 42.0, 18.0, 0.255, (0, 0, 0.034), 85.0, (900, 1050)),
    ("hero",    34.0, 34.0, 0.235, (0, 0, 0.032), 85.0, (1100, 1000)),
    ("fuze",    12.0, 10.0, 0.125, (-0.004, 0, 0.062), 85.0, (1100, 800)),
]
for name, az, el, dist, tgt, lens, res in VIEWS:
    sc.render.resolution_x, sc.render.resolution_y = res
    place_cam(az, el, dist, tgt, lens)
    sc.render.filepath = os.path.join(PREVIEW, "dg_%s.png" % name)
    bpy.ops.render.render(write_still=True)
    print("  rendered", sc.render.filepath)

# ============================================================== 10. save
root = link(bpy.data.objects.new("DecoyGrenade", None))
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
    blend = os.path.join(ROOT, "DecoyGrenade.blend")
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
