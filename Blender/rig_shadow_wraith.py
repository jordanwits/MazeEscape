"""Build the ShadowWraith deform rig.

Run inside Blender with Blender/ShadowWraith.blend open:
    exec(compile(open(path).read(), path, 'exec'))

The wraith mesh is procedural and is authored as 38 disjoint parts (core, 2 arms,
2 legs, 2 palms, 8 fingers, 2 thumbs, 18 rib arcs, 2 clavicles, 1 spine ridge).
Every joint below is measured off that geometry rather than hardcoded, so the
script survives a mesh rebuild.

Bones use mixamorig: naming and a Unity-Humanoid-complete hierarchy so the FBX
can be assigned a Humanoid avatar and retarget Mixamo clips.  Front is -Y, up
is +Z, he stands on z=0.
"""

import bpy, bmesh, math
import numpy as np
from mathutils import Vector, Matrix

BODY = "ShadowWraith_Body"
EYES = "ShadowWraith_Eyes"
ROOT = "ShadowWraith"
ARM_NAME = "Armature"
P = "mixamorig:"

# ---------------------------------------------------------------- utilities

def verts_of(ob):
    me = ob.data
    a = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", a)
    return a.reshape(-1, 3)


def loose_parts(ob):
    """Connected components as lists of vertex indices."""
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    bm.verts.ensure_lookup_table()
    seen = set()
    parts = []
    for v in bm.verts:
        if v.index in seen:
            continue
        stack = [v]
        comp = []
        seen.add(v.index)
        while stack:
            c = stack.pop()
            comp.append(c.index)
            for e in c.link_edges:
                o = e.other_vert(c)
                if o.index not in seen:
                    seen.add(o.index)
                    stack.append(o)
        parts.append(comp)
    bm.free()
    return parts


def centerline(co, idxs, n):
    """Sample (x,y,z) means in n z-slices across a part, low z first."""
    a = co[idxs]
    z0, z1 = a[:, 2].min(), a[:, 2].max()
    out = []
    for k in range(n):
        lo = z0 + (z1 - z0) * k / n
        hi = z0 + (z1 - z0) * (k + 1) / n
        s = a[(a[:, 2] >= lo) & (a[:, 2] <= hi)] if k == n - 1 else a[(a[:, 2] >= lo) & (a[:, 2] < hi)]
        if len(s):
            out.append(s.mean(axis=0))
    return np.array(out)


def at_z(cl, z):
    """Linear interpolation of a centerline at a given height."""
    zs = cl[:, 2]
    return np.array([np.interp(z, zs, cl[:, i]) for i in range(3)])


# ------------------------------------------------------- part classification

def classify(ob):
    co = verts_of(ob)
    parts = loose_parts(ob)
    info = []
    for p in parts:
        a = co[p]
        info.append(dict(idx=p, n=len(p), lo=a.min(0), hi=a.max(0), c=a.mean(0)))

    g = {}
    rest = list(info)

    def take(pred, key=None, sort=None):
        hit = [d for d in rest if pred(d)]
        if sort:
            hit.sort(key=sort)
        for d in hit:
            rest.remove(d)
        if key:
            g[key] = hit
        return hit

    # core: by far the largest island
    core = max(rest, key=lambda d: d["n"])
    rest.remove(core)
    g["core"] = [core]

    # legs reach the floor
    take(lambda d: d["lo"][2] < 0.05, "legs", sort=lambda d: d["c"][0])
    # arms are the remaining long islands
    take(lambda d: (d["hi"][2] - d["lo"][2]) > 0.8, "arms", sort=lambda d: d["c"][0])
    # palms: the outboard islands that reach highest below the elbow
    digits_and_hands = [d for d in rest if abs(d["c"][0]) > 0.20 and d["hi"][2] < 1.2]
    hand_top = max(d["hi"][2] for d in digits_and_hands)
    take(lambda d: d in digits_and_hands and d["hi"][2] > hand_top - 0.02, "hands",
         sort=lambda d: d["c"][0])
    # thumbs sit higher than the fingers
    digits = [d for d in rest if abs(d["c"][0]) > 0.20 and d["hi"][2] < 1.2]
    finger_top = min(d["hi"][2] for d in digits)
    take(lambda d: d in digits and d["hi"][2] > finger_top + 0.01, "thumbs",
         sort=lambda d: d["c"][0])
    # fingers, ordered thumb-side outward per hand
    fingers = take(lambda d: abs(d["c"][0]) > 0.20 and d["hi"][2] < 1.2)
    g["fingersL"] = sorted([d for d in fingers if d["c"][0] < 0], key=lambda d: -d["c"][0])
    g["fingersR"] = sorted([d for d in fingers if d["c"][0] > 0], key=lambda d: d["c"][0])
    # spine ridge: narrow in x, tall, behind the body
    take(lambda d: (d["hi"][0] - d["lo"][0]) < 0.05, "ridge")
    # clavicles sit in front (-y) above the ribs
    clav_z = max(d["c"][2] for d in rest)
    take(lambda d: d["c"][1] < -0.04 and d["c"][2] > clav_z - 0.05, "clavicles",
         sort=lambda d: d["c"][0])
    # everything left is ribcage
    g["ribs"] = sorted(rest, key=lambda d: (-d["c"][2], d["c"][0]))
    return co, g


# ------------------------------------------------------------ joint solving

def solve_joints(co, g):
    """Measure every joint centre off the mesh."""
    j = {}
    core = g["core"][0]
    ccl = centerline(co, core["idx"], 30)
    z0, z1 = core["lo"][2], core["hi"][2]
    span = z1 - z0

    def spine_pt(f, crown=False):
        z = (z1 - 0.005) if crown else (z0 + f * span)
        p = at_z(ccl, z)
        return Vector((0.0, float(p[1]), float(z)))

    j["hips"]   = spine_pt(0.040)
    j["spine"]  = spine_pt(0.170)
    j["spine1"] = spine_pt(0.315)
    j["spine2"] = spine_pt(0.470)
    j["neck"]   = spine_pt(0.620)   # base of neck / top of chest
    j["head"]   = spine_pt(0.745)   # base of skull
    j["crown"]  = spine_pt(0.0, crown=True)

    for side, s in (("L", -1), ("R", 1)):
        arm  = [d for d in g["arms"]  if d["c"][0] * s > 0][0]
        hand = [d for d in g["hands"] if d["c"][0] * s > 0][0]
        leg  = [d for d in g["legs"]  if d["c"][0] * s > 0][0]
        digits = g["fingersL"] if side == "L" else g["fingersR"]
        thumb = [d for d in g["thumbs"] if d["c"][0] * s > 0][0]

        acl = centerline(co, arm["idx"], 16)
        hcl = centerline(co, hand["idx"], 6)

        wrist = Vector((float(hcl[-1][0]), float(hcl[-1][1]), float(hand["hi"][2]) - 0.005))
        top = acl[-1]
        shoulder = Vector((float(top[0]) * 0.88, float(top[1]), float(arm["hi"][2]) - 0.045))
        e = at_z(acl, (shoulder.z + wrist.z) * 0.5)
        # elbows point backwards (+y): the mesh already bows that way, nudge it
        # further so Unity's avatar cannot mistake the flexion axis.
        elbow = Vector((float(e[0]), float(e[1]) + 0.008, float(e[2])))

        j[f"clav.{side}"]     = Vector((s * 0.030, j["neck"].y + 0.020, j["neck"].z - 0.028))
        j[f"shoulder.{side}"] = shoulder
        j[f"elbow.{side}"]    = elbow
        j[f"wrist.{side}"]    = wrist
        palm = np.mean([d["c"] for d in digits], axis=0)
        j[f"palm.{side}"] = Vector((float(palm[0]), float(palm[1]),
                                    float(max(d["hi"][2] for d in digits))))

        for name, d in zip(("Index", "Middle", "Ring", "Pinky"), digits):
            fcl = centerline(co, d["idx"], 8)
            j[f"{name}.{side}.root"] = Vector((float(fcl[-1][0]), float(fcl[-1][1]), float(d["hi"][2])))
            j[f"{name}.{side}.tip"]  = Vector((float(fcl[0][0]),  float(fcl[0][1]),  float(d["lo"][2])))
        tcl = centerline(co, thumb["idx"], 6)
        j[f"Thumb.{side}.root"] = Vector((float(tcl[-1][0]), float(tcl[-1][1]), float(thumb["hi"][2])))
        j[f"Thumb.{side}.tip"]  = Vector((float(tcl[0][0]),  float(tcl[0][1]),  float(thumb["lo"][2])))

        lcl = centerline(co, leg["idx"], 16)
        hip = at_z(lcl, leg["hi"][2] - 0.030)
        ankle = at_z(lcl, 0.105)
        k = at_z(lcl, (hip[2] + ankle[2]) * 0.5)
        j[f"hip.{side}"]   = Vector((float(hip[0]), float(hip[1]), float(hip[2])))
        # knees point forwards (-y): the raw centreline bows the wrong way by
        # ~7 mm, which would read to Unity as a backward-bending leg.
        j[f"knee.{side}"]  = Vector((float(k[0]), float(k[1]) - 0.022, float(k[2])))
        j[f"ankle.{side}"] = Vector((float(ankle[0]), float(ankle[1]), float(ankle[2])))
        j[f"toe.{side}"]   = Vector((float(ankle[0]), float(ankle[1]) - 0.085, 0.035))
    return j


# --------------------------------------------------------------- build rig

# (name, parent, head-key, tail-key, connected)
def bone_plan(j):
    plan = [
        ("Hips",   None,     j["hips"],   j["spine"],  False),
        ("Spine",  "Hips",   j["spine"],  j["spine1"], True),
        ("Spine1", "Spine",  j["spine1"], j["spine2"], True),
        ("Spine2", "Spine1", j["spine2"], j["neck"],   True),
        ("Neck",   "Spine2", j["neck"],   j["head"],   True),
        ("Head",   "Neck",   j["head"],   j["crown"],  True),
    ]
    for side, word in (("L", "Left"), ("R", "Right")):
        plan += [
            (f"{word}Shoulder", "Spine2",            j[f"clav.{side}"],     j[f"shoulder.{side}"], False),
            (f"{word}Arm",      f"{word}Shoulder",   j[f"shoulder.{side}"], j[f"elbow.{side}"],    True),
            (f"{word}ForeArm",  f"{word}Arm",        j[f"elbow.{side}"],    j[f"wrist.{side}"],    True),
            (f"{word}Hand",     f"{word}ForeArm",    j[f"wrist.{side}"],    j[f"palm.{side}"],     True),
            (f"{word}UpLeg",    "Hips",              j[f"hip.{side}"],      j[f"knee.{side}"],     False),
            (f"{word}Leg",      f"{word}UpLeg",      j[f"knee.{side}"],     j[f"ankle.{side}"],    True),
            (f"{word}Foot",     f"{word}Leg",        j[f"ankle.{side}"],    j[f"toe.{side}"],      True),
        ]
        for dig in ("Thumb", "Index", "Middle", "Ring", "Pinky"):
            root, tip = j[f"{dig}.{side}.root"], j[f"{dig}.{side}.tip"]
            cuts = [0.0, 0.42, 0.75, 1.0]
            for k in range(3):
                a = root.lerp(tip, cuts[k])
                b = root.lerp(tip, cuts[k + 1])
                parent = f"{word}Hand" if k == 0 else f"{word}Hand{dig}{k}"
                plan.append((f"{word}Hand{dig}{k+1}", parent, a, b, k > 0))
    return plan


def build_armature(j, ctx_root):
    for o in list(bpy.data.objects):
        if o.type == 'ARMATURE':
            bpy.data.objects.remove(o, do_unlink=True)
    arm_data = bpy.data.armatures.new(ARM_NAME)
    arm_ob = bpy.data.objects.new(ARM_NAME, arm_data)
    bpy.context.scene.collection.objects.link(arm_ob)
    arm_ob.parent = ctx_root
    arm_ob.show_in_front = True

    bpy.context.view_layer.objects.active = arm_ob
    bpy.ops.object.mode_set(mode='EDIT')
    eb = arm_data.edit_bones
    for name, parent, head, tail, conn in bone_plan(j):
        b = eb.new(P + name)
        b.head, b.tail = head, tail
        if parent:
            b.parent = eb[P + parent]
            b.use_connect = conn
        d = (Vector(tail) - Vector(head)).normalized()
        ref = Vector((0, 0, 1)) if abs(d.y) > 0.85 else Vector((0, -1, 0))
        b.align_roll(ref)
    bpy.ops.object.mode_set(mode='OBJECT')
    return arm_ob


# ----------------------------------------------------------------- skinning

POW, EPS, SMOOTH_ITERS = 4.0, 0.012, 4


def seg_dist(pts, a, b):
    ab = b - a
    denom = float(ab @ ab)
    t = np.clip((pts - a) @ ab / denom, 0.0, 1.0)[:, None]
    return np.linalg.norm(pts - (a + t * ab), axis=1)


def part_bones(g):
    """(vertex indices, candidate bone names, rigid) for every island."""
    out = []
    spine = ["Hips", "Spine", "Spine1", "Spine2", "Neck", "Head"]
    out.append((g["core"][0]["idx"], spine + ["LeftShoulder", "RightShoulder"], False))
    out.append((g["ridge"][0]["idx"], spine[:-1], False))
    for d in g["ribs"]:
        out.append((d["idx"], ["Spine", "Spine1", "Spine2"], True))
    # Islands that are disjoint from their parent island (arms, legs) must NOT
    # blend across the attaching joint.  There is no shared surface to smooth,
    # so a partial weight only shears the island -- and because linear blend
    # skinning is not invertible, a 20% shoulder blend put a 51 mm dent in the
    # deltoid as soon as the arm swung out to the T-pose.  Rigid to the limb
    # chain; the parent bone still carries the island through the hierarchy.
    for d in g["clavicles"]:
        w = "Left" if d["c"][0] < 0 else "Right"
        out.append((d["idx"], ["Spine2", w + "Shoulder"], False))
    for d in g["arms"]:
        w = "Left" if d["c"][0] < 0 else "Right"
        out.append((d["idx"], [w + "Arm", w + "ForeArm", w + "Hand"], False))
    for d in g["hands"]:
        w = "Left" if d["c"][0] < 0 else "Right"
        out.append((d["idx"], [w + "ForeArm", w + "Hand"], False))
    for d in g["legs"]:
        w = "Left" if d["c"][0] < 0 else "Right"
        out.append((d["idx"], [w + "UpLeg", w + "Leg", w + "Foot"], False))
    for key, digs in (("L", g["fingersL"]), ("R", g["fingersR"])):
        w = "Left" if key == "L" else "Right"
        for name, d in zip(("Index", "Middle", "Ring", "Pinky"), digs):
            out.append((d["idx"], [w + "Hand"] + [w + "Hand" + name + str(k) for k in (1, 2, 3)], False))
    for d in g["thumbs"]:
        w = "Left" if d["c"][0] < 0 else "Right"
        out.append((d["idx"], [w + "Hand"] + [w + "HandThumb" + str(k) for k in (1, 2, 3)], False))
    return out


def skin(ob, arm_ob, g, co):
    seg = {b.name: (np.array(b.head_local), np.array(b.tail_local)) for b in arm_ob.data.bones}

    # edge adjacency, used to relax the raw inverse-distance falloff
    nbr = [[] for _ in range(len(ob.data.vertices))]
    for e in ob.data.edges:
        a, b = e.vertices
        nbr[a].append(b)
        nbr[b].append(a)

    ob.vertex_groups.clear()
    vg = {}
    total = np.zeros(len(ob.data.vertices))

    for idxs, names, rigid in part_bones(g):
        pts = co[idxs]
        W = np.zeros((len(idxs), len(names)))
        for c, n in enumerate(names):
            a, b = seg[P + n]
            W[:, c] = 1.0 / (seg_dist(pts, a, b) + EPS) ** POW
        W /= W.sum(1, keepdims=True)

        if rigid:
            W[:] = W.mean(0, keepdims=True)
        else:
            pos = {v: i for i, v in enumerate(idxs)}
            for _ in range(SMOOTH_ITERS):
                acc = W.copy()
                for i, v in enumerate(idxs):
                    ns = [pos[k] for k in nbr[v] if k in pos]
                    if ns:
                        acc[i] = 0.5 * W[i] + 0.5 * W[ns].mean(0)
                W = acc
            W /= W.sum(1, keepdims=True)

        # Unity imports at most 4 influences per vertex
        if len(names) > 4:
            drop = np.argsort(-W, axis=1)[:, 4:]
            np.put_along_axis(W, drop, 0.0, axis=1)
            W /= W.sum(1, keepdims=True)

        for c, n in enumerate(names):
            if n not in vg:
                vg[n] = ob.vertex_groups.new(name=P + n)
            grp = vg[n]
            for i, v in enumerate(idxs):
                if W[i, c] > 1e-4:
                    grp.add([v], float(W[i, c]), 'REPLACE')
        total[idxs] += W.sum(1)

    bad = int((np.abs(total - 1.0) > 1e-3).sum())
    print("  skin: %d groups, unnormalised verts = %d" % (len(vg), bad))
    bind(ob, arm_ob)


def bind(ob, arm_ob):
    ob.parent = arm_ob
    ob.matrix_parent_inverse = Matrix.Identity(4)
    for m in list(ob.modifiers):
        ob.modifiers.remove(m)
    m = ob.modifiers.new("Armature", 'ARMATURE')
    m.object = arm_ob


def skin_rigid(ob, arm_ob, bone):
    ob.vertex_groups.clear()
    grp = ob.vertex_groups.new(name=P + bone)
    grp.add(list(range(len(ob.data.vertices))), 1.0, 'REPLACE')
    bind(ob, arm_ob)


# ------------------------------------------------------------------ posing

def arm_swing(arm_ob):
    """Quaternion per side that takes the authored hanging arm out to a T."""
    out = {}
    for word in ("Left", "Right"):
        b = arm_ob.data.bones
        sh = Vector(b[P + word + "Arm"].head_local)
        wr = Vector(b[P + word + "Hand"].head_local)
        d = (wr - sh).normalized()
        target = Vector((-1.0, 0.0, 0.0)) if word == "Left" else Vector((1.0, 0.0, 0.0))
        out[word] = d.rotation_difference(target)
    return out


def apply_swing(arm_ob, quats):
    """Rotate each upper arm rigidly about its shoulder; children follow."""
    for word, q in quats.items():
        pb = arm_ob.pose.bones[P + word + "Arm"]
        rest = arm_ob.data.bones[P + word + "Arm"].matrix_local
        piv = rest.translation
        pb.matrix = (Matrix.Translation(piv) @ q.to_matrix().to_4x4()
                     @ Matrix.Translation(-piv) @ rest)
        bpy.context.view_layer.update()


def bake_rest(arm_ob, meshes):
    """Freeze the current pose as the new rest pose, mesh included."""
    dg = bpy.context.evaluated_depsgraph_get()
    for ob in meshes:
        # bake the deformed shape without touching the armature modifier, so we
        # never depend on modifier_apply having a usable operator context
        baked = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
        old = ob.data
        baked.name = old.name
        ob.data = baked
        bpy.data.meshes.remove(old)
    bpy.context.view_layer.objects.active = arm_ob
    bpy.ops.object.mode_set(mode='POSE')
    bpy.ops.pose.armature_apply()
    bpy.ops.object.mode_set(mode='OBJECT')


def key_action(arm_ob, name, frames=2):
    """Key the current pose across `frames` as a standalone action."""
    if arm_ob.animation_data is None:
        arm_ob.animation_data_create()
    act = bpy.data.actions.new(name)
    arm_ob.animation_data.action = act
    if act.slots:
        arm_ob.animation_data.action_slot = act.slots[0]
    for pb in arm_ob.pose.bones:
        pb.rotation_mode = 'QUATERNION'
    for f in range(1, frames + 1):
        for pb in arm_ob.pose.bones:
            pb.keyframe_insert("rotation_quaternion", frame=f)
            pb.keyframe_insert("location", frame=f)
    # Blender 5.x actions are slotted; the FBX exporter only sees a bound slot
    if act.slots and arm_ob.animation_data.action_slot is None:
        arm_ob.animation_data.action_slot = act.slots[0]
    bpy.context.scene.frame_start = 1
    bpy.context.scene.frame_end = frames
    return act


# ------------------------------------------------------------------ export

def clear_pose(arm_ob):
    """Drop back to the rest (T) pose so the FBX node transforms are the T-pose."""
    for pb in arm_ob.pose.bones:
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.rotation_euler = (0, 0, 0)
        pb.location = (0, 0, 0)
        pb.scale = (1, 1, 1)
    bpy.context.view_layer.update()


def export(path, bake_space=True, anim=False, armature_only=False):
    """Model FBX (anim=False) or a Unity `model@clip.fbx` companion (anim=True).

    Unity builds a Humanoid avatar from the FBX's *default node transforms*, not
    from the bind poses, and Blender writes those from the pose at the current
    frame.  So the model file has to be exported with the pose cleared, and any
    clip has to ride in its own armature-only file.
    """
    types = {'ARMATURE'} if armature_only else {'ARMATURE', 'MESH', 'EMPTY'}
    bpy.ops.object.select_all(action='DESELECT')
    for o in bpy.data.objects:
        if o.type in types or (not armature_only and o.type in {'MESH', 'EMPTY'}):
            o.select_set(True)
    bpy.context.view_layer.objects.active = bpy.data.objects[ARM_NAME]
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True,
        object_types=types,
        apply_scale_options='FBX_SCALE_ALL',
        axis_forward='-Z', axis_up='Y',
        bake_space_transform=bake_space,
        add_leaf_bones=False,
        primary_bone_axis='Y', secondary_bone_axis='X',
        use_mesh_modifiers=True, mesh_smooth_type='FACE',
        bake_anim=anim, bake_anim_use_all_bones=True,
        bake_anim_use_nla_strips=False, bake_anim_use_all_actions=False,
        bake_anim_force_startend_keying=True, bake_anim_step=1.0,
        bake_anim_simplify_factor=0.0,
        path_mode='COPY', embed_textures=False,
    )
    print("  exported ->", path)


# -------------------------------------------------------------------- main

def run(export_path=None, bake_space=False):
    body = bpy.data.objects[BODY]
    eyes = bpy.data.objects[EYES]
    root = bpy.data.objects[ROOT]
    act = getattr(bpy.context, "object", None) or bpy.context.view_layer.objects.active
    if act is not None and act.mode != 'OBJECT':
        bpy.context.view_layer.objects.active = act
        bpy.ops.object.mode_set(mode='OBJECT')

    # bake_rest() replaces the mesh with its T-posed shape, so a second run
    # would measure joints off the T-pose.  Start from ShadowWraith_Unrigged.blend.
    assert not body.data.get("wraith_rig_baked"), (
        "this mesh has already been baked to a T-pose rest -- re-run against "
        "Blender/ShadowWraith_Unrigged.blend instead")

    co, g = classify(body)
    print("parts: core=%d legs=%d arms=%d hands=%d thumbs=%d fingersL=%d fingersR=%d "
          "ribs=%d clav=%d ridge=%d" % (len(g["core"]), len(g["legs"]), len(g["arms"]),
          len(g["hands"]), len(g["thumbs"]), len(g["fingersL"]), len(g["fingersR"]),
          len(g["ribs"]), len(g["clavicles"]), len(g["ridge"])))

    j = solve_joints(co, g)
    arm_ob = build_armature(j, root)
    print("bones: %d" % len(arm_ob.data.bones))

    skin(body, arm_ob, g, co)
    skin_rigid(eyes, arm_ob, "Head")

    authored = verts_of(body).copy()

    quats = arm_swing(arm_ob)
    apply_swing(arm_ob, quats)
    bake_rest(arm_ob, [body, eyes])

    # the authored hanging pose is now a pose on top of the T-pose rest
    back = {w: q.inverted() for w, q in quats.items()}
    apply_swing(arm_ob, back)
    key_action(arm_ob, "A_Pose", frames=2)

    dg = bpy.context.evaluated_depsgraph_get()
    ev = body.evaluated_get(dg).to_mesh()
    now = np.empty(len(ev.vertices) * 3)
    ev.vertices.foreach_get("co", now)
    now = now.reshape(-1, 3)
    err = np.linalg.norm(now - authored, axis=1)
    print("A_Pose round-trip: max err %.5f m, mean %.6f m" % (err.max(), err.mean()))
    body.evaluated_get(dg).to_mesh_clear()

    body.data["wraith_rig_baked"] = True
    eyes.data["wraith_rig_baked"] = True

    if export_path:
        held = arm_ob.animation_data.action
        arm_ob.animation_data.action = None
        clear_pose(arm_ob)
        export(export_path, bake_space)                       # T-posed model
        arm_ob.animation_data.action = held
        if held.slots:
            arm_ob.animation_data.action_slot = held.slots[0]
        bpy.context.scene.frame_set(1)
        bpy.context.view_layer.update()
        # NB: A_Pose is kept in the .blend as a reference pose only.  Exporting it
        # as a `ShadowWraith@A_Pose.fbx` companion is pointless -- the take is two
        # identical frames, and Unity discards a take with no motion.
    return arm_ob, g, j
