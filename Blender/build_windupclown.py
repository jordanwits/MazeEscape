# ---------------------------------------------------------------------------
# Wind-up wooden clown robot - procedural build.
# Blender 5.1, headless:  blender.exe -b --python build_windupclown.py -- [flags]
# flags:  nosave    don't write the .blend
#         norender  skip the preview renders
#
# Proportions come off the concept art (1400 px tall -> 1.95 m).  The whole
# figure is procedural: no sculpting, no external textures.  Every material is
# a shader-node setup keyed off object-space coordinates, so parts can be moved
# or rescaled without re-UVing anything.
#
# The wind-up key sits on the BACK (the concept art has it on the hip).
# ---------------------------------------------------------------------------
import bpy, bmesh, math, os, sys, random, tempfile
from mathutils import Vector, Euler, Matrix

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
DO_SAVE = "nosave" not in argv
DO_RENDER = "norender" not in argv

ROOT_DIR = r"H:/Unity/Maze Escape/Blender"
PREVIEW = os.path.join(tempfile.gettempdir(), "windupclown_preview")

C = bpy.context
D = bpy.data
TAU = math.pi * 2.0
PI = math.pi
COLL = "WindupClown"
UP = Vector((0.0, 0.0, 1.0))

# --------------------------------------------------------------- dimensions
P = dict(
    head_z=1.575, head_r=0.166,
    torso_top=1.322, torso_bot=0.992,
    waist_z=0.960,
    skirt_top=0.948, skirt_bot=0.742,
    leg_x=0.172,
    dyn_x=0.561, dyn_z=1.185, dyn_r=0.066, dyn_h=0.400,
)
HC = Vector((0.0, 0.0, P["head_z"]))
HR = P["head_r"]

WOOD_A = (0.118, 0.080, 0.045)     # deep grain
WOOD_B = (0.330, 0.250, 0.156)     # pale weathered face
WOOD_D = (0.075, 0.052, 0.031)
RED    = (0.180, 0.036, 0.023)
RED_HI = (0.250, 0.052, 0.030)
CREAM  = (0.300, 0.238, 0.148)
ORANGE = (1.000, 0.235, 0.010)


# =========================================================== scene utilities
def clear_scene():
    for o in list(D.objects):
        D.objects.remove(o, do_unlink=True)
    for blk in (D.meshes, D.materials, D.curves, D.images, D.node_groups):
        for b in list(blk):
            if b.users == 0:
                blk.remove(b)


def get_coll(name=COLL, parent=None):
    c = D.collections.get(name)
    if c is None:
        c = D.collections.new(name)
        (parent or C.scene.collection).children.link(c)
    return c


def link(obj, coll=COLL):
    for c in list(obj.users_collection):
        c.objects.unlink(obj)
    get_coll(coll).objects.link(obj)
    return obj


def set_mat(obj, m):
    if m is None:
        return obj
    if isinstance(m, str):
        m = D.materials.get(m)
    obj.data.materials.clear()
    obj.data.materials.append(m)
    return obj


def empty(name, loc=(0, 0, 0), coll=COLL):
    e = D.objects.new(name, None)
    e.empty_display_type = "PLAIN_AXES"
    e.empty_display_size = 0.08
    e.location = Vector(loc)
    return link(e, coll)


# ================================================================ materials
def _nodes(m):
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    out.location = (600, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.location = (300, 0)
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return nt, bsdf


def sv(b, key, val):
    if key in b.inputs:
        b.inputs[key].default_value = val


def _axis_scale(grain, gscale, axis):
    sc = [grain[0] * gscale, grain[1] * gscale, grain[2] * gscale]
    if axis == "X":
        return [sc[2], sc[0], sc[1]]
    if axis == "Y":
        return [sc[0], sc[2], sc[1]]
    return sc


def plain_mat(name, color, rough=0.7, metal=0.0):
    m = D.materials.get(name) or D.materials.new(name)
    nt, b = _nodes(m)
    sv(b, "Base Color", (color[0], color[1], color[2], 1.0))
    sv(b, "Roughness", rough)
    sv(b, "Metallic", metal)
    m.diffuse_color = (color[0], color[1], color[2], 1.0)
    m.roughness, m.metallic = rough, metal
    return m


def wood_mat(name, c1, c2, rough=0.85, gscale=1.0, bumpf=0.55, metal=0.0,
             grain=(11.0, 11.0, 0.45), axis="Z"):
    """Weathered timber: 3D noise squashed along `axis` so the grain streaks
    run down the length of the part."""
    m = D.materials.get(name) or D.materials.new(name)
    nt, b = _nodes(m)
    n, L = nt.nodes, nt.links
    coord = n.new("ShaderNodeTexCoord"); coord.location = (-1100, 0)
    mapn = n.new("ShaderNodeMapping"); mapn.location = (-940, 0)
    mapn.inputs["Scale"].default_value = tuple(_axis_scale(grain, gscale, axis))
    g1 = n.new("ShaderNodeTexNoise"); g1.location = (-760, 120)
    g1.inputs["Scale"].default_value = 2.4
    g1.inputs["Detail"].default_value = 7.0
    g1.inputs["Roughness"].default_value = 0.62
    g1.inputs["Distortion"].default_value = 0.6
    g2 = n.new("ShaderNodeTexNoise"); g2.location = (-760, -160)
    g2.inputs["Scale"].default_value = 11.0
    g2.inputs["Detail"].default_value = 6.0
    g2.inputs["Roughness"].default_value = 0.72
    blend = n.new("ShaderNodeMix"); blend.location = (-560, 0)
    blend.data_type = "FLOAT"
    blend.inputs["Factor"].default_value = 0.35
    ramp = n.new("ShaderNodeValToRGB"); ramp.location = (-380, 0)
    ramp.color_ramp.elements[0].position = 0.34
    ramp.color_ramp.elements[0].color = (c1[0], c1[1], c1[2], 1)
    ramp.color_ramp.elements[1].position = 0.66
    ramp.color_ramp.elements[1].color = (c2[0], c2[1], c2[2], 1)
    dirt = n.new("ShaderNodeTexNoise"); dirt.location = (-760, -420)
    dirt.inputs["Scale"].default_value = 1.8
    dirt.inputs["Detail"].default_value = 4.0
    dmix = n.new("ShaderNodeMix"); dmix.location = (-170, 0)
    dmix.data_type = "RGBA"
    dmix.blend_type = "MULTIPLY"
    dmix.inputs["Factor"].default_value = 0.30
    bump = n.new("ShaderNodeBump"); bump.location = (30, -380)
    bump.inputs["Strength"].default_value = bumpf
    bump.inputs["Distance"].default_value = 0.006
    rgh = n.new("ShaderNodeMix"); rgh.location = (30, -160)
    rgh.data_type = "FLOAT"
    rgh.inputs[2].default_value = max(0.0, rough - 0.12)
    rgh.inputs[3].default_value = min(1.0, rough + 0.10)
    L.new(coord.outputs["Object"], mapn.inputs["Vector"])
    for t in (g1, g2, dirt):
        L.new(mapn.outputs["Vector"], t.inputs["Vector"])
    L.new(g1.outputs["Fac"], blend.inputs[2])
    L.new(g2.outputs["Fac"], blend.inputs[3])
    L.new(blend.outputs[0], ramp.inputs["Fac"])
    L.new(ramp.outputs["Color"], dmix.inputs[6])
    L.new(dirt.outputs["Color"], dmix.inputs[7])
    L.new(dmix.outputs[2], b.inputs["Base Color"])
    L.new(blend.outputs[0], bump.inputs["Height"])
    L.new(bump.outputs["Normal"], b.inputs["Normal"])
    L.new(blend.outputs[0], rgh.inputs["Factor"])
    L.new(rgh.outputs[0], b.inputs["Roughness"])
    sv(b, "Metallic", metal)
    m.diffuse_color = (c2[0], c2[1], c2[2], 1)
    m.roughness, m.metallic = rough, metal
    return m


def paint_mat(name, paint, wood, rough=0.72, chip=0.34, gscale=1.0, metal=0.0,
              grain=(11.0, 11.0, 0.45), axis="Z", chipscale=16.0):
    """Flaking paint worn back to grained bare timber."""
    m = D.materials.get(name) or D.materials.new(name)
    nt, b = _nodes(m)
    n, L = nt.nodes, nt.links
    coord = n.new("ShaderNodeTexCoord"); coord.location = (-1200, 0)
    mapn = n.new("ShaderNodeMapping"); mapn.location = (-1040, 0)
    mapn.inputs["Scale"].default_value = tuple(_axis_scale(grain, gscale, axis))
    grn = n.new("ShaderNodeTexNoise"); grn.location = (-860, 240)
    grn.inputs["Scale"].default_value = 3.0
    grn.inputs["Detail"].default_value = 7.0
    grn.inputs["Distortion"].default_value = 0.5
    wramp = n.new("ShaderNodeValToRGB"); wramp.location = (-680, 240)
    wramp.color_ramp.elements[0].position = 0.36
    wramp.color_ramp.elements[0].color = (wood[0] * 0.5, wood[1] * 0.5,
                                          wood[2] * 0.5, 1)
    wramp.color_ramp.elements[1].position = 0.64
    wramp.color_ramp.elements[1].color = (wood[0], wood[1], wood[2], 1)
    pramp = n.new("ShaderNodeValToRGB"); pramp.location = (-680, -60)
    pramp.color_ramp.elements[0].position = 0.30
    pramp.color_ramp.elements[0].color = (paint[0] * 0.62, paint[1] * 0.62,
                                          paint[2] * 0.62, 1)
    pramp.color_ramp.elements[1].position = 0.74
    pramp.color_ramp.elements[1].color = (min(1, paint[0] * 1.22),
                                          min(1, paint[1] * 1.22),
                                          min(1, paint[2] * 1.22), 1)
    chp = n.new("ShaderNodeTexNoise"); chp.location = (-860, -380)
    chp.inputs["Scale"].default_value = chipscale
    chp.inputs["Detail"].default_value = 9.0
    chp.inputs["Roughness"].default_value = 0.68
    chp.inputs["Distortion"].default_value = 0.8
    cramp = n.new("ShaderNodeValToRGB"); cramp.location = (-660, -380)
    cramp.color_ramp.elements[0].position = max(0.0, chip - 0.06)
    cramp.color_ramp.elements[1].position = min(1.0, chip + 0.05)
    mixn = n.new("ShaderNodeMix"); mixn.location = (-260, 0)
    mixn.data_type = "RGBA"
    rmix = n.new("ShaderNodeMix"); rmix.location = (-260, -280)
    rmix.data_type = "FLOAT"
    rmix.inputs[2].default_value = 0.94
    rmix.inputs[3].default_value = rough
    bump = n.new("ShaderNodeBump"); bump.location = (-40, -460)
    bump.inputs["Strength"].default_value = 0.35
    bump.inputs["Distance"].default_value = 0.006
    L.new(coord.outputs["Object"], mapn.inputs["Vector"])
    for t in (grn, chp):
        L.new(mapn.outputs["Vector"], t.inputs["Vector"])
    L.new(grn.outputs["Fac"], wramp.inputs["Fac"])
    L.new(grn.outputs["Fac"], pramp.inputs["Fac"])
    L.new(chp.outputs["Fac"], cramp.inputs["Fac"])
    L.new(cramp.outputs["Color"], mixn.inputs["Factor"])
    L.new(wramp.outputs["Color"], mixn.inputs[6])
    L.new(pramp.outputs["Color"], mixn.inputs[7])
    L.new(cramp.outputs["Color"], rmix.inputs["Factor"])
    L.new(mixn.outputs[2], b.inputs["Base Color"])
    L.new(rmix.outputs[0], b.inputs["Roughness"])
    L.new(cramp.outputs["Color"], bump.inputs["Height"])
    L.new(bump.outputs["Normal"], b.inputs["Normal"])
    sv(b, "Metallic", metal)
    m.diffuse_color = (paint[0], paint[1], paint[2], 1)
    m.roughness = rough
    return m


def spiral_mat(name, ca, cb, starts=2.0, pitch=7.5, duty=0.48, rough=0.70,
               wood=None, chip=0.30):
    """Helical barber-pole stripes around the object's local Z axis."""
    m = D.materials.get(name) or D.materials.new(name)
    nt, b = _nodes(m)
    n, L = nt.nodes, nt.links
    if wood is None:
        wood = (ca[0] * 0.45, ca[1] * 0.45, ca[2] * 0.45)
    coord = n.new("ShaderNodeTexCoord"); coord.location = (-1200, 0)
    grad = n.new("ShaderNodeTexGradient"); grad.location = (-1020, 120)
    grad.gradient_type = "RADIAL"
    sep = n.new("ShaderNodeSeparateXYZ"); sep.location = (-1020, -140)
    mz = n.new("ShaderNodeMath"); mz.location = (-840, -140)
    mz.operation = "MULTIPLY"
    mz.inputs[1].default_value = pitch
    ma = n.new("ShaderNodeMath"); ma.location = (-840, 120)
    ma.operation = "MULTIPLY"
    ma.inputs[1].default_value = starts
    add = n.new("ShaderNodeMath"); add.location = (-660, 0)
    add.operation = "ADD"
    frac = n.new("ShaderNodeMath"); frac.location = (-500, 0)
    frac.operation = "FRACT"
    ramp = n.new("ShaderNodeValToRGB"); ramp.location = (-330, 0)
    ramp.color_ramp.interpolation = "CONSTANT"
    ramp.color_ramp.elements[0].position = 0.0
    ramp.color_ramp.elements[0].color = (ca[0], ca[1], ca[2], 1)
    ramp.color_ramp.elements[1].position = duty
    ramp.color_ramp.elements[1].color = (cb[0], cb[1], cb[2], 1)
    chp = n.new("ShaderNodeTexNoise"); chp.location = (-660, -400)
    chp.inputs["Scale"].default_value = 30.0
    chp.inputs["Detail"].default_value = 9.0
    chp.inputs["Roughness"].default_value = 0.68
    cramp = n.new("ShaderNodeValToRGB"); cramp.location = (-470, -400)
    cramp.color_ramp.elements[0].position = max(0.0, chip - 0.07)
    cramp.color_ramp.elements[1].position = min(1.0, chip + 0.05)
    wear = n.new("ShaderNodeMix"); wear.location = (-120, 0)
    wear.data_type = "RGBA"
    wear.inputs[6].default_value = (wood[0], wood[1], wood[2], 1)
    bump = n.new("ShaderNodeBump"); bump.location = (60, -380)
    bump.inputs["Strength"].default_value = 0.30
    bump.inputs["Distance"].default_value = 0.006
    L.new(coord.outputs["Object"], grad.inputs["Vector"])
    L.new(coord.outputs["Object"], sep.inputs["Vector"])
    L.new(coord.outputs["Object"], chp.inputs["Vector"])
    L.new(grad.outputs["Fac"], ma.inputs[0])
    L.new(sep.outputs["Z"], mz.inputs[0])
    L.new(ma.outputs[0], add.inputs[0])
    L.new(mz.outputs[0], add.inputs[1])
    L.new(add.outputs[0], frac.inputs[0])
    L.new(frac.outputs[0], ramp.inputs["Fac"])
    L.new(chp.outputs["Fac"], cramp.inputs["Fac"])
    L.new(cramp.outputs["Color"], wear.inputs["Factor"])
    L.new(ramp.outputs["Color"], wear.inputs[7])
    L.new(wear.outputs[2], b.inputs["Base Color"])
    L.new(cramp.outputs["Color"], bump.inputs["Height"])
    L.new(bump.outputs["Normal"], b.inputs["Normal"])
    sv(b, "Roughness", rough)
    m.diffuse_color = (ca[0], ca[1], ca[2], 1)
    return m


def glow_mat(name, color, strength=1.6):
    m = D.materials.get(name) or D.materials.new(name)
    nt, b = _nodes(m)
    sv(b, "Base Color", (color[0], color[1], color[2], 1))
    sv(b, "Emission Color", (color[0], color[1], color[2], 1))
    sv(b, "Emission Strength", strength)
    sv(b, "Roughness", 0.35)
    m.diffuse_color = (color[0], color[1], color[2], 1)
    return m


def build_materials():
    return {
        "wood":     wood_mat("Wood_Body", WOOD_A, WOOD_B, rough=0.88, gscale=1.0),
        "wood_dk":  wood_mat("Wood_Dark", WOOD_D, (0.360, 0.268, 0.168),
                             rough=0.90, gscale=1.4),
        "wood_head": wood_mat("Wood_Head", WOOD_A, (0.545, 0.432, 0.298),
                              rough=0.86, gscale=0.8, bumpf=0.7),
        "red":      paint_mat("Paint_Red", RED, WOOD_B, rough=0.68, chip=0.33),
        "red_dk":   paint_mat("Paint_RedDk", (0.215, 0.045, 0.030), WOOD_A,
                              rough=0.72, chip=0.32, gscale=1.2),
        "boot":     paint_mat("Paint_Boot", (0.190, 0.053, 0.036), WOOD_A,
                              rough=0.62, chip=0.30, gscale=0.8, chipscale=13.0),
        "cream":    paint_mat("Paint_Cream", CREAM, WOOD_A, rough=0.76, chip=0.34),
        "metal":    plain_mat("Metal_Aged", (0.085, 0.081, 0.078), 0.50, 1.0),
        "metal_lt": plain_mat("Metal_Light", (0.150, 0.145, 0.140), 0.42, 1.0),
        "brass":    plain_mat("Metal_Brass", (0.330, 0.240, 0.100), 0.40, 1.0),
        "glow":     glow_mat("Eye_Glow", ORANGE, 1.6),
        "dark":     plain_mat("Dark_Void", (0.016, 0.014, 0.012), 0.95),
        "rope":     plain_mat("Rope_Fuse", (0.285, 0.212, 0.112), 0.95),
        "tooth":    plain_mat("Tooth_Wood", (0.395, 0.322, 0.215), 0.78),
        "hat":      spiral_mat("Hat_Spiral", RED_HI, CREAM, starts=2.0,
                               pitch=7.5, duty=0.48, wood=WOOD_A),
    }


# =============================================================== primitives
def _fin(o, name, mat, loc=None, rot=None, scale=None, coll=COLL):
    o.name = o.data.name = name
    if loc is not None:
        o.location = Vector(loc)
    if rot is not None:
        o.rotation_euler = Euler(rot)
    if scale is not None:
        o.scale = Vector(scale)
    set_mat(o, mat)
    return link(o, coll)


def mbox(name, size, loc=(0, 0, 0), rot=(0, 0, 0), mat=None, coll=COLL):
    """Cube with its dimensions baked into the mesh, so object scale stays 1
    (keeps bevel widths honest and survives place())."""
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, 0))
    o = C.object
    o.data.transform(Matrix.Diagonal(Vector(size)).to_4x4())
    return _fin(o, name, mat, loc, rot, None, coll)


def cyl(name, r, h, loc=(0, 0, 0), rot=(0, 0, 0), mat=None, verts=24,
        scale=(1, 1, 1), coll=COLL):
    bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=r, depth=h,
                                        location=(0, 0, 0))
    return _fin(C.object, name, mat, loc, rot, scale, coll)


def cone(name, r1, r2, h, loc=(0, 0, 0), rot=(0, 0, 0), mat=None, verts=28,
         scale=(1, 1, 1), coll=COLL):
    bpy.ops.mesh.primitive_cone_add(vertices=verts, radius1=r1, radius2=r2,
                                    depth=h, location=(0, 0, 0))
    return _fin(C.object, name, mat, loc, rot, scale, coll)


def ball(name, r, loc=(0, 0, 0), rot=(0, 0, 0), mat=None, seg=32, rings=16,
         scale=(1, 1, 1), coll=COLL):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=seg, ring_count=rings,
                                         radius=r, location=(0, 0, 0))
    return _fin(C.object, name, mat, loc, rot, scale, coll)


def ring(name, major, minor, loc=(0, 0, 0), rot=(0, 0, 0), mat=None,
         mseg=32, minseg=12, scale=(1, 1, 1), coll=COLL):
    bpy.ops.mesh.primitive_torus_add(major_radius=major, minor_radius=minor,
                                     major_segments=mseg, minor_segments=minseg,
                                     location=(0, 0, 0))
    return _fin(C.object, name, mat, loc, rot, scale, coll)


def poly_prism(name, pts, depth, loc=(0, 0, 0), rot=(0, 0, 0), mat=None,
               coll=COLL):
    """Extrude a 2D polygon (list of (x, y)) along +Z by `depth`."""
    me = D.meshes.new(name)
    bm = bmesh.new()
    vs = [bm.verts.new((p[0], p[1], 0.0)) for p in pts]
    f = bm.faces.new(vs)
    bmesh.ops.triangulate(bm, faces=[f])
    ret = bmesh.ops.extrude_face_region(bm, geom=list(bm.faces))
    ev = [g for g in ret["geom"] if isinstance(g, bmesh.types.BMVert)]
    bmesh.ops.translate(bm, verts=ev, vec=(0, 0, depth))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(me)
    bm.free()
    o = D.objects.new(name, me)
    o.location = Vector(loc)
    o.rotation_euler = Euler(rot)
    set_mat(o, mat)
    return link(o, coll)


def staved_tube(name, z0, z1, r0, r1, n=20, inset=0.007, depth=0.005, mat=None,
                loc=(0, 0, 0), rot=(0, 0, 0), coll=COLL, jitter=0.0, seed=None):
    """Tapered tube whose side faces are inset into individual vertical staves
    - this is what gives the barrel, limbs and thighs their plank look."""
    if seed is not None:
        random.seed(seed)
    me = D.meshes.new(name)
    bm = bmesh.new()
    a_ring, b_ring = [], []
    for i in range(n):
        a = TAU * i / n
        ca, sa = math.cos(a), math.sin(a)
        j = 1.0 + (random.uniform(-jitter, jitter) if jitter else 0.0)
        a_ring.append(bm.verts.new((ca * r0 * j, sa * r0 * j, z0)))
        b_ring.append(bm.verts.new((ca * r1 * j, sa * r1 * j, z1)))
    side = []
    for i in range(n):
        k = (i + 1) % n
        side.append(bm.faces.new((a_ring[i], a_ring[k], b_ring[k], b_ring[i])))
    bm.faces.new(list(reversed(a_ring)))
    bm.faces.new(list(b_ring))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bmesh.ops.inset_individual(bm, faces=side, thickness=inset, depth=depth,
                               use_even_offset=True)
    bm.to_mesh(me)
    bm.free()
    o = D.objects.new(name, me)
    o.location = Vector(loc)
    o.rotation_euler = Euler(rot)
    set_mat(o, mat)
    return link(o, coll)


def loft(name, secs, n=24, mat=None, coll=COLL):
    """Loft super-ellipse cross-sections along +Y.
    secs: (y, z_centre, half_x, half_z, exponent) - used for the boots."""
    me = D.meshes.new(name)
    bm = bmesh.new()
    rings = []
    for (y, z, w, h, e) in secs:
        p = 2.0 / e
        rings.append([bm.verts.new((
            w * math.copysign(abs(math.cos(TAU * k / n)) ** p,
                              math.cos(TAU * k / n)), y,
            z + h * math.copysign(abs(math.sin(TAU * k / n)) ** p,
                                  math.sin(TAU * k / n))))
            for k in range(n)])
    for i in range(len(rings) - 1):
        A, B = rings[i], rings[i + 1]
        for k in range(n):
            j = (k + 1) % n
            bm.faces.new((A[k], A[j], B[j], B[k]))
    bm.faces.new(list(rings[0]))
    bm.faces.new(list(reversed(rings[-1])))
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bmesh.ops.remove_doubles(bm, verts=list(bm.verts), dist=1e-5)
    bm.to_mesh(me)
    bm.free()
    o = D.objects.new(name, me)
    set_mat(o, mat)
    link(o, coll)
    return smooth(o, 42)


def trapezoid(name, w_top, w_bot, length, thick, mat=None, coll=COLL):
    """Kilt slat: top edge at local Y=0, hanging to Y=-length."""
    pts = [(-w_top * 0.5, 0.0), (w_top * 0.5, 0.0),
           (w_bot * 0.5, -length), (-w_bot * 0.5, -length)]
    o = poly_prism(name, pts, thick, mat=mat, coll=coll)
    o.data.transform(Matrix.Translation((0.0, 0.0, -thick * 0.5)))
    return o


def petal(name, w_top, w, length, thick, mat=None, coll=COLL, arcseg=12,
          cup=0.0):
    """Ruff scallop: tapered sides, semicircular bottom, optional dish."""
    r = w * 0.5
    ybase = -(length - r)
    pts = [(-w_top * 0.5, 0.0), (w_top * 0.5, 0.0), (r, ybase)]
    for i in range(1, arcseg):
        a = PI * i / arcseg
        pts.append((r * math.cos(a), ybase - r * math.sin(a)))
    pts.append((-r, ybase))
    o = poly_prism(name, pts, thick, mat=mat, coll=coll)
    o.data.transform(Matrix.Translation((0.0, 0.0, -thick * 0.5)))
    if cup:
        half = w * 0.5
        for v in o.data.vertices:
            v.co.z += cup * (v.co.x / half) ** 2
    return o


# ================================================================ transforms
def place(o, origin, xdir, ydir, zdir, scale=(1, 1, 1)):
    """Set an object transform straight from an orthonormal basis."""
    x = Vector(xdir).normalized() * scale[0]
    y = Vector(ydir).normalized() * scale[1]
    z = Vector(zdir).normalized() * scale[2]
    o.matrix_world = Matrix(((x.x, y.x, z.x, origin[0]),
                             (x.y, y.y, z.y, origin[1]),
                             (x.z, y.z, z.z, origin[2]),
                             (0.0, 0.0, 0.0, 1.0)))
    o.rotation_mode = "QUATERNION"
    return o


def radial(a, tilt=0.0):
    """(outward, tangential, up) at azimuth `a`; +tilt flares the bottom out."""
    out = Vector((math.cos(a), math.sin(a), 0.0))
    tan = Vector((-math.sin(a), math.cos(a), 0.0))
    if tilt:
        return ((out * math.cos(tilt) + UP * math.sin(tilt)).normalized(), tan,
                (UP * math.cos(tilt) - out * math.sin(tilt)).normalized())
    return out, tan, UP


def frame_from(dirv, hint=(0.0, 1.0, 0.0)):
    z = Vector(dirv).normalized()
    h = Vector(hint)
    if abs(z.dot(h)) > 0.98:
        h = Vector((1.0, 0.0, 0.0))
    x = z.cross(h).normalized()
    return x, z.cross(x).normalized() * -1.0, z


def frame_euler(dirv):
    x, y, z = frame_from(dirv)
    return Matrix((x, y, z)).transposed().to_euler()


def limb(name, p0, p1, r0, r1, n=11, mat=None, seed=None):
    """Staved wooden limb segment running p0 -> p1."""
    p0, p1 = Vector(p0), Vector(p1)
    o = staved_tube(name, 0.0, (p1 - p0).length, r0, r1, n=n, inset=0.005,
                    depth=0.0035, mat=mat, jitter=0.008, seed=seed)
    flat(o); bev(o, 0.0015, 2, 60)
    x, y, z = frame_from(p1 - p0)
    return place(o, p0, x, y, z)


def tube_curve(name, pts, radius, mat=None, coll=COLL):
    cu = D.curves.new(name, "CURVE")
    cu.dimensions = "3D"
    cu.resolution_u = 8
    cu.bevel_depth = radius
    cu.bevel_resolution = 4
    sp = cu.splines.new("NURBS")
    sp.points.add(len(pts) - 1)
    for i, p in enumerate(pts):
        sp.points[i].co = (p[0], p[1], p[2], 1.0)
    sp.use_endpoint_u = True
    sp.order_u = min(4, len(pts))
    o = D.objects.new(name, cu)
    set_mat(o, mat)
    return link(o, coll)


# ================================================================ modifiers
def bev(o, width=0.006, seg=2, angle=50.0):
    m = o.modifiers.new("Bevel", "BEVEL")
    m.width, m.segments = width, seg
    m.limit_method = "ANGLE"
    m.angle_limit = math.radians(angle)
    m.use_clamp_overlap = True
    return o


def smooth(o, angle=45.0):
    """Smooth shading that still keeps hard edges (cylinder caps, torus seams).
    Pre-4.1 that is mesh auto-smooth; 4.1+ it is the Smooth by Angle modifier,
    and without it every cylinder renders as a featureless blob."""
    for p in o.data.polygons:
        p.use_smooth = True
    if hasattr(o.data, "use_auto_smooth"):
        o.data.use_auto_smooth = True
        o.data.auto_smooth_angle = math.radians(angle)
        return o
    bpy.ops.object.select_all(action="DESELECT")
    o.select_set(True)
    C.view_layer.objects.active = o
    try:
        bpy.ops.object.shade_auto_smooth(angle=math.radians(angle))
    except Exception as e:
        print("  shade_auto_smooth skipped on %s: %s" % (o.name, e))
    return o


def flat(o):
    for p in o.data.polygons:
        p.use_smooth = False
    return o


def star_pts(r_out, r_in, points=5):
    return [(math.cos(TAU * i / (points * 2) + PI * 0.5) * (r_out if i % 2 == 0 else r_in),
             math.sin(TAU * i / (points * 2) + PI * 0.5) * (r_out if i % 2 == 0 else r_in))
            for i in range(points * 2)]


def diamond_pts(w, h):
    return [(0.0, h * 0.5), (w * 0.5, 0.0), (0.0, -h * 0.5), (-w * 0.5, 0.0)]


def decal_poly(name, pts, target, loc, rot, thick=0.008, offset=0.002, cuts=6,
               mat=None, coll=COLL):
    """Flat polygon projected onto `target` along local -Z then thickened
    outward - painted markings that hug a curved surface without UVs."""
    me = D.meshes.new(name)
    bm = bmesh.new()
    f = bm.faces.new([bm.verts.new((p[0], p[1], 0.0)) for p in pts])
    bmesh.ops.triangulate(bm, faces=[f])
    for _ in range(cuts):
        bmesh.ops.subdivide_edges(bm, edges=list(bm.edges), cuts=1,
                                  use_grid_fill=True)
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(me)
    bm.free()
    o = D.objects.new(name, me)
    o.location = Vector(loc)
    o.rotation_euler = Euler(rot)
    set_mat(o, mat)
    link(o, coll)
    sw = o.modifiers.new("Wrap", "SHRINKWRAP")
    sw.wrap_method = "PROJECT"
    sw.use_project_z = True
    sw.use_negative_direction = True
    sw.use_positive_direction = False
    sw.target = target
    sw.offset = offset
    sd = o.modifiers.new("Thick", "SOLIDIFY")
    sd.thickness = thick
    sd.offset = 1.0
    return o


def bolts(name, count, radius, z, r_bolt=0.008, h=0.010, mat=None, phase=0.0):
    return [smooth(cyl("%s_%02d" % (name, i), r_bolt, h,
                       loc=(math.cos(TAU * i / count + phase) * radius,
                            math.sin(TAU * i / count + phase) * radius, z),
                       rot=(0, PI * 0.5, TAU * i / count + phase),
                       mat=mat, verts=10))
            for i in range(count)]


# ================================ head surface helpers (exact sphere maths)
def on_head(x, z):
    dy2 = HR * HR - x * x - (z - HC.z) ** 2
    return Vector((x, -math.sqrt(max(dy2, 1e-6)), z))


def on_head_az(az, z):
    rr = math.sqrt(max(HR * HR - (z - HC.z) ** 2, 1e-6))
    return Vector((math.cos(az) * rr, math.sin(az) * rr, z))


def head_frame(p):
    n = (p - HC).normalized()
    fx = UP.cross(n).normalized()
    return fx, n.cross(fx).normalized(), n


def on_face(o, p, off=0.0):
    fx, fy, fz = head_frame(p)
    return place(o, tuple(p + fz * off), fx, fy, fz)


# ===========================================================================
#                                   BUILD
# ===========================================================================
clear_scene()
M = build_materials()

# ------------------------------------------------------------- torso barrel
torso = staved_tube("Torso_Staves", P["torso_bot"], P["torso_top"],
                    0.1525, 0.1405, n=18, inset=0.0075, depth=0.0060,
                    mat=M["wood"], jitter=0.008, seed=7)
flat(torso); bev(torso, 0.0018, 2, 60)
flat(cyl("Torso_RimTop", 0.1455, 0.030, (0, 0, P["torso_top"] - 0.012),
         mat=M["wood_dk"], verts=32))
flat(cyl("Torso_RimBot", 0.1560, 0.026, (0, 0, P["torso_bot"] + 0.010),
         mat=M["wood_dk"], verts=32))
flat(cone("Torso_Deck", 0.1405, 0.1180, 0.048, (0, 0, P["torso_top"] + 0.021),
          mat=M["wood"], verts=32))
smooth(cyl("Neck_Post", 0.062, 0.070, (0, 0, P["torso_top"] + 0.062),
           mat=M["wood_dk"], verts=20))

belt = flat(cyl("Waist_Band", 0.1585, 0.052, (0, 0, P["waist_z"]),
                mat=M["metal"], verts=32))
bev(belt, 0.004, 2, 40)
smooth(cyl("Waist_Lip", 0.1645, 0.014, (0, 0, P["waist_z"] + 0.028),
           mat=M["metal_lt"], verts=32))
smooth(cyl("Waist_Lip2", 0.1645, 0.014, (0, 0, P["waist_z"] - 0.028),
           mat=M["metal_lt"], verts=32))
bolts("Waist_Bolt", 12, 0.1585, P["waist_z"], 0.0085, 0.014, M["metal_lt"], 0.13)

decal_poly("Chest_Star", star_pts(0.092, 0.041), torso, (0, -0.34, 1.152),
           (PI * 0.5, 0, 0), thick=0.006, offset=0.0015, cuts=6, mat=M["red"])
_ka = math.atan2(-0.092, 0.108)
flat(cyl("Chest_Knot", 0.030, 0.026, (0.108, -0.092, 1.118), (0, PI * 0.5, _ka),
         mat=M["wood_dk"], verts=20))
flat(cyl("Chest_KnotIn", 0.020, 0.030, (0.107, -0.091, 1.118),
         (0, PI * 0.5, _ka), mat=M["dark"], verts=18))

# ---------------------------------------------------------------- kilt
flat(cyl("Pelvis_Core", 0.108, 0.235, (0, 0, 0.845), mat=M["wood_dk"], verts=20))
random.seed(11)
_rt, _rb = 0.150, 0.238
_zt, _zb = P["skirt_top"], P["skirt_bot"]
_tilt = math.atan2(_rb - _rt, _zt - _zb)
_len = math.hypot(_rb - _rt, _zt - _zb)
for i in range(24):
    a = TAU * i / 24 + TAU / 48.0
    out, tan, up = radial(a, _tilt)
    sl = trapezoid("Skirt_Slat_%02d" % i, 0.0435, 0.0625,
                   _len * random.uniform(0.96, 1.04), 0.023,
                   mat=(M["red_dk"] if i % 2 else M["cream"]))
    flat(sl); bev(sl, 0.0035, 2, 55)
    place(sl, (math.cos(a) * _rt, math.sin(a) * _rt, _zt), tan, up, out)
flat(cyl("Skirt_Cuff", 0.1545, 0.022, (0, 0, _zt + 0.002), mat=M["wood_dk"],
         verts=32))

# ---------------------------------------------------------------- legs
BOOT_SECS = [
    (0.070, 0.082, 0.045, 0.078, 3.0), (0.045, 0.078, 0.062, 0.078, 2.8),
    (0.005, 0.072, 0.072, 0.072, 2.6), (-0.045, 0.066, 0.076, 0.066, 2.5),
    (-0.100, 0.060, 0.077, 0.060, 2.5), (-0.150, 0.057, 0.073, 0.057, 2.6),
    (-0.190, 0.058, 0.064, 0.055, 2.8), (-0.218, 0.062, 0.048, 0.050, 3.0),
    (-0.236, 0.070, 0.028, 0.040, 3.2), (-0.243, 0.080, 0.012, 0.026, 3.4),
]
for s in (1, -1):
    tag = "R" if s > 0 else "L"
    x = s * P["leg_x"]
    yaw = s * math.radians(23.0)

    smooth(ball("Hip_Ball_" + tag, 0.058, (x, 0, 0.762), mat=M["metal"],
                seg=24, rings=12))
    th = staved_tube("Thigh_" + tag, 0.560, 0.775, 0.0555, 0.0585, n=11,
                     inset=0.005, depth=0.0035, mat=M["wood"], loc=(x, 0, 0),
                     jitter=0.01, seed=40 + s)
    flat(th); bev(th, 0.0015, 2, 60)
    flat(cyl("Thigh_Band_" + tag, 0.0555, 0.020, (x, 0, 0.572),
             mat=M["metal_lt"], verts=20, scale=(1.10, 1.10, 1.0)))

    smooth(cyl("Knee_DiscA_" + tag, 0.0585, 0.086, (x, 0, 0.540),
               (0, PI * 0.5, 0), M["metal"], verts=28))
    smooth(cyl("Knee_DiscB_" + tag, 0.0475, 0.078, (x, 0, 0.482),
               (0, PI * 0.5, 0), M["metal"], verts=24))
    for sg in (-1, 1):
        smooth(cyl("Knee_Cap_%s%d" % (tag, sg), 0.0335, 0.016,
                   (x + sg * 0.049, 0, 0.540), (0, PI * 0.5, 0), M["metal_lt"], verts=18))
        smooth(cyl("Knee_Bolt_%s%d" % (tag, sg), 0.0105, 0.014,
                   (x + sg * 0.055, 0, 0.540), (0, PI * 0.5, 0), M["metal_lt"], verts=12))
        smooth(cyl("Knee_BoltB_%s%d" % (tag, sg), 0.0085, 0.012,
                   (x + sg * 0.043, 0, 0.482), (0, PI * 0.5, 0), M["metal_lt"], verts=12))

    sh0 = staved_tube("Shin_Top_" + tag, 0.420, 0.478, 0.0485, 0.0495, n=10,
                      inset=0.004, depth=0.003, mat=M["wood"], loc=(x, 0, 0),
                      jitter=0.008, seed=60 + s)
    flat(sh0); bev(sh0, 0.0015, 2, 60)
    flat(cyl("Shin_Band_" + tag, 0.0525, 0.022, (x, 0, 0.424), mat=M["metal"], verts=22))

    shin = flat(cone("Shin_" + tag, 0.0505, 0.0475, 0.196, (x, 0, 0.322),
                     mat=M["wood"], verts=28))
    for row, zc in enumerate((0.272, 0.370)):
        for k in range(4):
            a = TAU * k / 4.0 + (TAU / 8.0 if row else 0.0) + 0.20
            o2, tan, up = radial(a)
            org = (x + math.cos(a) * 0.14, math.sin(a) * 0.14, zc)
            d = decal_poly("Shin_Dia_%s%d%d" % (tag, row, k),
                           diamond_pts(0.068, 0.086), shin, org, (0, 0, 0),
                           thick=0.0035, offset=0.0012, cuts=5, mat=M["red"])
            place(d, org, tan, up, o2)
    smooth(cyl("Ankle_Band_" + tag, 0.0555, 0.030, (x, 0, 0.230), mat=M["metal"], verts=22))

    bt = loft("Boot_" + tag, BOOT_SECS, n=28, mat=M["boot"])
    bt.location = (x, 0.010, 0.0)
    bt.rotation_euler = (0, 0, yaw)
    bt.scale = (1.08, 1.08, 1.08)
    bev(bt, 0.004, 2, 42)
    shaft = smooth(cone("Boot_Cuff_" + tag, 0.062, 0.055, 0.085, (x, 0, 0.185),
                        mat=M["boot"], verts=26))
    bev(shaft, 0.004, 2, 45)
    sole = loft("Sole_" + tag, [(y, z - h * 0.86, w * 1.045, h * 0.20, e)
                                for (y, z, w, h, e) in BOOT_SECS[1:-2]],
                n=28, mat=M["dark"])
    sole.location = (x, 0.010, 0.0)
    sole.rotation_euler = (0, 0, yaw)
    sole.scale = (1.08, 1.08, 1.08)
    cap = smooth(ball("Boot_ToeCap_" + tag, 0.052, mat=M["boot"], seg=24, rings=14,
                      scale=(1.3392, 1.5336, 1.0152)))
    cap.location = (x + math.sin(yaw) * 0.175, 0.010 - math.cos(yaw) * 0.175, 0.052)
    cap.rotation_euler = (0, 0, yaw)

# ---------------------------------------------------------------- arms
for s in (1, -1):
    tag = "R" if s > 0 else "L"
    Ps = Vector((s * 0.196, 0.004, 1.282))
    Pe = Vector((s * 0.362, 0.006, 1.046))
    Pw = Vector((s * 0.484, 0.004, 1.132))
    ua = (Pe - Ps).normalized()
    fa = (Pw - Pe).normalized()

    smooth(ball("Sho_Cap_" + tag, 0.0575, tuple(Ps - ua * 0.030), mat=M["wood"],
                seg=26, rings=14, scale=(1.05, 0.90, 0.86)))
    smooth(ball("Sho_Ball_" + tag, 0.0615, tuple(Ps), mat=M["metal"], seg=26, rings=14))
    smooth(cyl("Sho_Collar_" + tag, 0.0495, 0.056, tuple(Ps + ua * 0.054),
               frame_euler(ua), M["metal_lt"], verts=20))

    limb("Upper_Arm_" + tag, Ps + ua * 0.072, Pe - ua * 0.052, 0.0465, 0.0435,
         n=10, mat=M["wood"], seed=90 + s)
    smooth(cyl("Upper_Band_" + tag, 0.0470, 0.021, tuple(Pe - ua * 0.060),
               frame_euler(ua), M["metal"], verts=20))

    smooth(cyl("Elb_DiscA_" + tag, 0.0425, 0.078, tuple(Pe), (PI * 0.5, 0, 0),
               M["metal"], verts=26))
    for sg in (-1, 1):
        smooth(cyl("Elb_Cap_%s%d" % (tag, sg), 0.0215, 0.013,
                   (Pe.x, Pe.y + sg * 0.044, Pe.z), (PI * 0.5, 0, 0), M["metal_lt"], verts=16))
        smooth(cyl("Elb_Bolt_%s%d" % (tag, sg), 0.0080, 0.011,
                   (Pe.x, Pe.y + sg * 0.050, Pe.z), (PI * 0.5, 0, 0), M["metal_lt"], verts=10))
    smooth(cyl("Elb_Link_" + tag, 0.0360, 0.054, tuple(Pe + fa * 0.030),
               frame_euler(fa), M["metal"], verts=20))

    limb("Fore_Arm_" + tag, Pe + fa * 0.046, Pw - fa * 0.014, 0.0410, 0.0385,
         n=9, mat=M["wood"], seed=120 + s)
    smooth(cyl("Wrist_Band_" + tag, 0.0425, 0.025, tuple(Pw), frame_euler(fa),
               M["metal"], verts=20))

# --------------------------------------------- dynamite sticks + gripping hands
R = P["dyn_r"]
ZC = P["dyn_z"]
HH = P["dyn_h"] * 0.5
RF = R + 0.017
for s in (1, -1):
    tag = "R" if s > 0 else "L"
    X0 = s * P["dyn_x"]
    # Heel of the hand faces INBOARD, towards the wrist the forearm arrives
    # from - putting A0 the other way up strands the hand on the far side of
    # the dynamite with nothing reaching back to the wrist.
    A0 = PI if s > 0 else 0.0
    # Wrap direction for the FINGERS: they curl from the heel round the BACK of
    # the stick, and the thumb opposes them across the front (-CURL).  The stick
    # is too thick for a hand to encircle, so thumb-near / fingers-far is the
    # grip that actually closes on it.
    CURL = -s

    stick = flat(cyl("Dyn_Stick_" + tag, R, P["dyn_h"], (X0, 0, ZC),
                     mat=M["red"], verts=32))
    bev(stick, 0.005, 2, 50)
    for zz in (ZC + 0.146, ZC - 0.146):
        smooth(cyl("Dyn_Band_%s_%d" % (tag, int(zz * 1000)), R + 0.004, 0.024,
                   (X0, 0, zz), mat=M["metal"], verts=32))
    flat(cyl("Dyn_Top_" + tag, R * 0.99, 0.014, (X0, 0, ZC + HH - 0.004),
             mat=M["wood_dk"], verts=32))
    decal_poly("Dyn_Star_" + tag, star_pts(0.030, 0.0135), stick,
               (X0, -0.30, ZC + 0.052), (PI * 0.5, 0, 0), thick=0.003,
               offset=0.001, cuts=4, mat=M["dark"])

    fz = ZC + HH
    tube_curve("Fuse_" + tag,
               [(X0, 0.0, fz - 0.012), (X0 + s * 0.006, -0.006, fz + 0.030),
                (X0 + s * 0.026, -0.016, fz + 0.056),
                (X0 + s * 0.056, -0.012, fz + 0.050),
                (X0 + s * 0.066, 0.004, fz + 0.028),
                (X0 + s * 0.052, 0.016, fz + 0.014)], 0.0085, mat=M["rope"])

    # heel of the hand: bridges the wrist band across to the stick
    # sits back behind the thumb; if it reaches too far forward it reads as a
    # bare plate stuck on the side of the stick
    kn = mbox("Knuck_Back_" + tag, (0.056, 0.082, 0.124),
              (X0 + math.cos(A0) * (R + 0.013), 0.019, 1.108), mat=M["wood"])
    flat(kn); bev(kn, 0.021, 4, 40)
    # wrist stub, so the forearm meets the hand instead of stopping short
    _pw = Vector((s * 0.484, 0.004, 1.132))
    _hand = Vector((X0 + math.cos(A0) * (R + 0.022), 0.010, 1.124))
    smooth(cyl("Hand_Wrist_" + tag, 0.0390, (_hand - _pw).length + 0.026,
               tuple((_pw + _hand) * 0.5), frame_euler(_hand - _pw),
               M["wood"], verts=24))

    # Segments are sized to very nearly touch, both along each finger and
    # between fingers, and the joints between them are WOOD knuckle beads a
    # touch fatter than the gap.  The fist has to read as one carved mass with
    # creases; dark metal beads between every segment turn it into a waffle.
    # index reaches furthest round the stick, little finger least, so the tips
    # make a scalloped silhouette instead of one flat wall
    REACH = (0.030, 0.055, 0.010, -0.070)
    for row, z in enumerate((1.161, 1.128, 1.095, 1.061)):
        shrink = 1.0 - 0.04 * abs(row - 1.2)
        # sweep measured from the palm; centres the grip on the front face
        for k, (dl, ln, th) in enumerate(((0.80, 0.050, 0.036),
                                          (1.34, 0.047, 0.034),
                                          (1.88, 0.043, 0.031))):
            a = A0 + CURL * (dl + (REACH[row] if k else 0.0))
            o2, tan, up = radial(a)
            f = mbox("Fing_%s%d%d" % (tag, row, k),
                     (th, ln * shrink, 0.0295 * shrink), mat=M["wood"])
            flat(f)
            bev(f, 0.013 if k == 2 else 0.006, 4 if k == 2 else 3, 40)
            place(f, (X0 + math.cos(a) * RF, math.sin(a) * RF, z), o2, tan, up)
            if k < 2:
                a2 = A0 + CURL * (dl + 0.27 + (REACH[row] if k else 0.0))
                smooth(cyl("Knuck_%s%d%d" % (tag, row, k), 0.0163, 0.0288 * shrink,
                           (X0 + math.cos(a2) * (RF - 0.004),
                            math.sin(a2) * (RF - 0.004), z), mat=M["wood"], verts=14))
    # Thumb lies UP the front-inboard face, just inside the first finger column
    # and slightly proud of it.  A fist closed round something this thick shows
    # the thumb on the SAME side as the fingers - putting it round the back
    # leaves the grip splayed open and reads as a broken wrist.
    # chunkier than a finger, so it reads as a thumb and not a fifth digit
    for k, (dl, wid, hgt, z) in enumerate(((0.36, 0.049, 0.064, 1.078),
                                           (0.57, 0.046, 0.055, 1.140))):
        a = A0 - CURL * dl
        o2, tan, up = radial(a)
        t = mbox("Thumb_%s%d" % (tag, k), (0.040, wid, hgt), mat=M["wood"])
        flat(t); bev(t, 0.017, 4, 40)
        place(t, (X0 + math.cos(a) * (RF + 0.010),
                  math.sin(a) * (RF + 0.010), z), o2, tan, up)
    a2 = A0 - CURL * 0.47
    o2, tan, up = radial(a2)
    kt = smooth(cyl("Knuck_T" + tag, 0.0175, 0.047, mat=M["wood"], verts=14))
    place(kt, (X0 + math.cos(a2) * (RF + 0.008),
               math.sin(a2) * (RF + 0.008), 1.110), o2, up, tan)

# ---------------------------------------------------------------- head
head = smooth(ball("Head", HR, tuple(HC), mat=M["wood_head"], seg=56, rings=32))

for s in (1, -1):
    tag = "R" if s > 0 else "L"
    p = on_head(s * 0.081, 1.557)
    fx, fy, fz = head_frame(p)
    on_face(smooth(cyl("Eye_Socket_" + tag, 0.0355, 0.030, mat=M["dark"], verts=32)), p, -0.015)
    on_face(smooth(ring("Eye_Bezel_" + tag, 0.0378, 0.0068, mat=M["metal_lt"],
                        mseg=40, minseg=12)), p, -0.0015)
    on_face(smooth(ring("Eye_Rim_" + tag, 0.0475, 0.0072, mat=M["wood_head"],
                        mseg=40, minseg=10)), p, -0.011)
    for k, ang in enumerate((0.72, -0.72)):
        bar = mbox("Eye_X_%s%d" % (tag, k), (0.050, 0.0105, 0.009), mat=M["glow"])
        bev(bar, 0.0020, 2, 40)
        ax = (fx * math.cos(ang) + fy * math.sin(ang)).normalized()
        place(bar, tuple(p + fz * -0.0035), ax, fz.cross(ax).normalized(), fz)

nose = poly_prism("Nose", [(-0.0205, 0.024), (0.0205, 0.024), (0.0, -0.031)],
                  0.026, mat=M["wood_head"])
nose.data.transform(Matrix.Translation((0.0, 0.0, -0.019)))
flat(nose); bev(nose, 0.0035, 2, 45)
on_face(nose, on_head(-0.003, 1.520), -0.0075)

MW, MZ = 0.119, 1.481
top_y = lambda u: 0.0215 + 0.048 * u * u
bot_y = lambda u: -0.0255 + 0.079 * u * u
us = [-1.0 + 2.0 * i / 30 for i in range(31)]
decal_poly("Mouth_Band",
           [(u * MW, top_y(u)) for u in us] + [(u * MW, bot_y(u)) for u in reversed(us)],
           head, (0.0, -0.40, MZ), (PI * 0.5, 0, 0), thick=0.0022, offset=0.0004,
           cuts=4, mat=M["dark"])
for i in range(15):
    u = (i / 14.0) * 2.0 - 1.0
    ty, by = top_y(u), bot_y(u)
    th = mbox("Tooth_%02d" % i, (0.0152, (ty - by) * 0.56, 0.010), mat=M["tooth"])
    bev(th, 0.0018, 2, 45)
    on_face(th, on_head(u * MW * 0.94, MZ + (ty + by) * 0.5), -0.0018)

for s in (1, -1):
    tag = "R" if s > 0 else "L"
    p = on_head_az(0.0 if s > 0 else PI, 1.598)
    on_face(smooth(cyl("Ear_Post_" + tag, 0.0150, 0.022, mat=M["wood_dk"], verts=20)), p, 0.004)
    on_face(smooth(cyl("Ear_Disc_" + tag, 0.0295, 0.014, mat=M["wood_head"], verts=28)), p, 0.014)
    on_face(smooth(ring("Ear_Ring_" + tag, 0.0280, 0.0080, mat=M["wood_head"],
                        mseg=30, minseg=10)), p, 0.015)

# hairline cracks: chains of short tangent slivers so they hug the sphere
for ci, ((x0, z0), (x1, z1), wd, segs) in enumerate([
        ((0.020, 1.688), (0.032, 1.598), 0.0024, 7),
        ((-0.090, 1.648), (-0.060, 1.582), 0.0020, 6),
        ((0.096, 1.540), (0.072, 1.468), 0.0019, 5),
        ((-0.052, 1.452), (-0.028, 1.416), 0.0017, 4)]):
    for k in range(segs):
        t0, t1 = k / float(segs), (k + 1) / float(segs)
        ax = x0 + (x1 - x0) * t0 + 0.007 * math.sin(ci * 2.1 + k * 2.7)
        bx = x0 + (x1 - x0) * t1 + 0.007 * math.sin(ci * 2.1 + (k + 1) * 2.7)
        az, bz = z0 + (z1 - z0) * t0, z0 + (z1 - z0) * t1
        p = on_head((ax + bx) * 0.5, (az + bz) * 0.5)
        fx, fy, fz = head_frame(p)
        ang = math.atan2(bx - ax, bz - az)
        sl = mbox("Crack_%d_%d" % (ci, k),
                  (wd * (1.0 - 0.10 * k), math.hypot(bx - ax, bz - az) * 1.18, 0.005),
                  mat=M["dark"])
        rx = (fx * math.cos(-ang) + fy * math.sin(-ang)).normalized()
        place(sl, tuple(p + fz * 0.0004), rx, fz.cross(rx).normalized(), fz)

# ---------------------------------------------------------------- party hat
_hb = Vector((-0.014, -0.004, 1.664))
_ha = Vector((math.sin(0.55) * math.cos(0.20), math.sin(0.55) * math.sin(0.20),
              math.cos(0.55))).normalized()
_hh = 0.244
_hx, _hy, _hz = frame_from(_ha)
hat = flat(cone("Hat_Cone", 0.0815, 0.006, _hh, verts=44, mat=M["hat"]))
place(hat, tuple(_hb + _ha * (_hh * 0.5)), _hx, _hy, _hz)
smooth(hat, 30)
place(smooth(ring("Hat_Rim", 0.0815, 0.0095, mat=M["hat"], mseg=44, minseg=10)),
      tuple(_hb), _hx, _hy, _hz)
place(smooth(cyl("Hat_FinialStem", 0.0090, 0.026, mat=M["metal"], verts=14)),
      tuple(_hb + _ha * (_hh + 0.002)), _hx, _hy, _hz)
smooth(ball("Hat_Finial", 0.0275, tuple(_hb + _ha * (_hh + 0.018)),
            mat=M["metal"], seg=26, rings=14))

# ---------------------------------------------------------------- clown ruff
def build_ruff(layer, n, r_in, z_in, r_out, z_out, w, phase, parity, wob_deg, cup):
    tilt = math.atan2(r_out - r_in, z_in - z_out)
    length = math.hypot(r_out - r_in, z_in - z_out)
    for i in range(n):
        a = TAU * i / n + phase
        wob = math.radians(wob_deg) * (1.0 if i % 2 else -1.0)
        out, tan, up = radial(a, tilt + wob)
        pl = petal("Ruff_%d_%02d" % (layer, i), w * 0.48, w,
                   length * (1.0 + 0.03 * ((i % 3) - 1)), 0.012,
                   mat=(M["red"] if (i + parity) % 2 else M["cream"]), cup=cup)
        flat(pl); bev(pl, 0.0045, 3, 55)
        place(pl, (math.cos(a) * r_in, math.sin(a) * r_in, z_in), tan, up, out)


build_ruff(0, 20, 0.070, 1.426, 0.188, 1.312, 0.084, 0.0, 0, 6.0, -0.006)
build_ruff(1, 17, 0.068, 1.404, 0.144, 1.336, 0.068, TAU / 34.0, 1, 7.0, -0.005)
smooth(cyl("Collar_Ring", 0.0685, 0.036, (0, 0, 1.414), mat=M["wood_dk"],
           verts=26, scale=(1.02, 1.02, 1.25)))

# ------------------------------------------------- wind-up key (ON THE BACK)
KZ, YS = 1.128, 0.1425
flat(cyl("Key_Plate", 0.040, 0.022, (0, YS - 0.002, KZ), (PI * 0.5, 0, 0),
         M["wood_dk"], verts=26))
smooth(cyl("Key_Collar", 0.0275, 0.026, (0, YS + 0.012, KZ), (PI * 0.5, 0, 0),
           M["metal"], verts=24))
smooth(ring("Key_CollarRim", 0.0265, 0.0055, (0, YS + 0.024, KZ), (PI * 0.5, 0, 0),
            M["brass"], mseg=28, minseg=10))
smooth(cyl("Key_Shaft", 0.0135, 0.100, (0, YS + 0.064, KZ), (PI * 0.5, 0, 0),
           M["brass"], verts=20))
smooth(cyl("Key_ShaftStep", 0.0180, 0.015, (0, YS + 0.038, KZ), (PI * 0.5, 0, 0),
           M["brass"], verts=20))
_hy2 = YS + 0.113
for sg in (1, -1):
    smooth(ring("Key_Loop_%d" % sg, 0.0270, 0.0092, (0, _hy2, KZ + sg * 0.0255),
                (PI * 0.5, 0, 0), M["brass"], mseg=32, minseg=12))
smooth(cyl("Key_Waist", 0.0150, 0.020, (0, _hy2, KZ), mat=M["brass"], verts=16))
smooth(cyl("Key_Hub", 0.0175, 0.024, (0, _hy2 - 0.006, KZ), (PI * 0.5, 0, 0),
           M["brass"], verts=18))

# ===========================================================================
#                          organise / light / render
# ===========================================================================
GROUPS = [
    ("CL_Head",  ("Head", "Eye", "Nose", "Mouth", "Tooth", "Ear", "Crack",
                  "Hat_", "Ruff_", "Collar_")),
    ("CL_Torso", ("Torso", "Waist", "Neck", "Chest", "Pelvis", "Skirt")),
    ("CL_Arms",  ("Sho_", "Upper_", "Elb_", "Fore_", "Wrist_", "Fing", "Thumb",
                  "Knuck", "Hand_")),
    ("CL_Legs",  ("Hip_", "Thigh", "Knee", "Shin", "Ankle", "Boot", "Sole")),
    ("CL_Key",   ("Key_",)),
    ("CL_Props", ("Dyn_", "Fuse")),
]
root_coll = get_coll(COLL)
for gname, _ in GROUPS:
    get_coll(gname, root_coll)

rig = empty("ClownRoot", (0, 0, 0.005))
rig.empty_display_type = "ARROWS"
rig.empty_display_size = 0.30
for o in list(root_coll.objects):
    if o is rig:
        continue
    for gname, prefixes in GROUPS:
        if o.name.startswith(prefixes):
            for c in list(o.users_collection):
                c.objects.unlink(o)
            D.collections[gname].objects.link(o)
            break
    else:
        print("  UNSORTED:", o.name)
    o.parent = rig
    o.matrix_parent_inverse = rig.matrix_world.inverted()

sc = C.scene
for eng in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
    try:
        sc.render.engine = eng
        break
    except Exception:
        continue
sc.render.resolution_x, sc.render.resolution_y = 900, 1200
sc.render.image_settings.file_format = "PNG"
try:
    sc.eevee.taa_render_samples = 96
    sc.eevee.use_raytracing = True
except Exception:
    pass
try:
    sc.view_settings.view_transform = "AgX"
except Exception:
    pass

w = D.worlds.get("World") or D.worlds.new("World")
sc.world = w
w.use_nodes = True
bgn = w.node_tree.nodes.get("Background")
if bgn:
    bgn.inputs[0].default_value = (0.30, 0.30, 0.31, 1.0)
    bgn.inputs[1].default_value = 0.22

cam_d = D.cameras.new("Cam")
cam_d.lens = 105.0
cam = D.objects.new("Cam", cam_d)
cam.location = (0.0, -6.4, 1.02)
cam.rotation_euler = (PI * 0.5, 0.0, 0.0)
link(cam, "Rig")
sc.camera = cam

lrig = empty("LightRig", (0, 0, 0), coll="Rig")


def area(name, loc, rot, size, energy):
    ld = D.lights.new(name, "AREA")
    ld.energy, ld.size = energy, size
    o = D.objects.new(name, ld)
    o.location, o.rotation_euler = loc, rot
    link(o, "Rig")
    o.parent = lrig
    return o


area("Key",    (-2.3, -3.1, 3.5), (math.radians(48), 0, math.radians(-36)), 3.0, 190)
area("Fill",   (3.0, -2.6, 1.5), (math.radians(78), 0, math.radians(48)), 3.5, 55)
area("Rim",    (0.4, 3.6, 2.8), (math.radians(126), 0, math.radians(184)), 3.0, 120)
area("Bounce", (0.0, -1.6, -1.1), (math.radians(-20), 0, 0), 4.0, 22)

bd_mat = D.materials.new("Backdrop_Grey")
bd_mat.use_nodes = True
_nt = bd_mat.node_tree
_nt.nodes.clear()
_out = _nt.nodes.new("ShaderNodeOutputMaterial")
_em = _nt.nodes.new("ShaderNodeEmission")
_em.inputs[0].default_value = (0.60, 0.598, 0.590, 1.0)
_em.inputs[1].default_value = 1.15
_nt.links.new(_em.outputs[0], _out.inputs["Surface"])
bd = mbox("Backdrop", (14.0, 0.06, 10.0), (0, 3.4, 1.0), mat=bd_mat, coll="Rig")
bd.parent = lrig

if DO_RENDER:
    os.makedirs(PREVIEW, exist_ok=True)

    def shot(name, yaw, dist=6.4, height=1.02, lens=105.0):
        a = math.radians(yaw)
        lrig.rotation_euler = (0, 0, a)
        cam.data.lens = lens
        cam.location = (math.sin(a) * dist, -math.cos(a) * dist, height)
        cam.rotation_euler = (PI * 0.5, 0.0, a)
        C.view_layer.update()
        sc.render.filepath = os.path.join(PREVIEW, name)
        bpy.ops.render.render(write_still=True)
        print("  render:", name)

    shot("clown_front", 0.0)
    shot("clown_threequarter", 38.0)
    shot("clown_back", 180.0)
    shot("clown_headshot", 18.0, dist=2.5, height=1.60, lens=135.0)

if DO_SAVE:
    blend = os.path.join(ROOT_DIR, "WindupClown.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend, compress=True)
    print("saved", blend)

meshes = [o for o in D.objects if o.type == "MESH" and o.name != "Backdrop"]
tris = 0
dg = C.evaluated_depsgraph_get()
for o in meshes:
    ev = o.evaluated_get(dg)
    me = ev.to_mesh()
    tris += sum(len(p.vertices) - 2 for p in me.polygons)
    ev.to_mesh_clear()
zs = [(o.matrix_world @ Vector(c)).z for o in meshes for c in o.bound_box]
print("PARTS:", len(meshes), " TOTAL TRIS:", tris)
print("HEIGHT: %.3f m  (%.4f .. %.3f)" % (max(zs) - min(zs), min(zs), max(zs)))
print("BUILD DONE")
