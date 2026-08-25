# ---------------------------------------------------------------------------
# Generic prop -> Unity export: joined FBX + packed URP MetallicSmoothness.
# Expects <Name>.blend containing a root empty called <Name> whose mesh children
# all share one baked material, plus textures/<Name>_{Metallic,Roughness}.png.
#
#   blender.exe "<Name>.blend" --background --python export_prop_unity.py -- <Name>
#
# Run with the blend ALREADY open so no wm.open_mainfile is needed in-script.
# ---------------------------------------------------------------------------
import bpy, os, sys
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not argv:
    raise SystemExit("usage: ... --python export_prop_unity.py -- <AssetName>")
NAME = argv[0]

ROOT   = r"H:/Unity/Maze Escape/Blender"
TEXDIR = os.path.join(ROOT, "textures")
FBX    = os.path.join(ROOT, NAME + ".fbx")

# ---------------------------------------------------------- 1. single mesh
root = bpy.data.objects[NAME]
parts = [o for o in root.children if o.type == 'MESH']
print("parts to join:", len(parts))

bpy.ops.object.select_all(action='DESELECT')
copies = []
for o in parts:
    c = o.copy()
    c.data = o.data.copy()
    c.parent = None
    c.matrix_world = o.matrix_world
    bpy.context.collection.objects.link(c)
    c.select_set(True)
    copies.append(c)
bpy.context.view_layer.objects.active = copies[0]
bpy.ops.object.join()
merged = bpy.context.object
merged.name = NAME
merged.data.name = NAME
merged.data.calc_loop_triangles()
mats = [m.name for m in merged.data.materials]
print("merged tris:", len(merged.data.loop_triangles),
      "verts:", len(merged.data.vertices), "materials:", mats)
if len(mats) != 1:
    print("  ! expected exactly one material, got", mats)

# ------------------------------------------------------------- 2. FBX out
bpy.ops.object.select_all(action='DESELECT')
merged.select_set(True)
bpy.context.view_layer.objects.active = merged
bpy.ops.export_scene.fbx(
    filepath=FBX,
    use_selection=True,
    object_types={'MESH'},
    apply_scale_options='FBX_SCALE_ALL',   # keeps localScale 1 in Unity
    axis_up='Y', axis_forward='-Z',
    bake_space_transform=True,             # Z-up -> Y-up baked into the verts
    use_mesh_modifiers=True,
    mesh_smooth_type='FACE',
    add_leaf_bones=False,
    bake_anim=False,
    path_mode='STRIP',
)
print("wrote", FBX, os.path.getsize(FBX), "bytes")

# ------------------------------ 3. URP MetallicSmoothness (R=metal, A=gloss)
def load_linear(fname):
    img = bpy.data.images.load(os.path.join(TEXDIR, fname), check_existing=False)
    img.colorspace_settings.name = 'Non-Color'
    buf = np.empty(len(img.pixels), dtype=np.float32)
    img.pixels.foreach_get(buf)
    return img, buf.reshape(-1, 4)

met_img, met = load_linear(NAME + "_Metallic.png")
rgh_img, rgh = load_linear(NAME + "_Roughness.png")
w, h = met_img.size
assert rgh_img.size[:] == met_img.size[:], "metallic/roughness size mismatch"

out = np.zeros_like(met)
out[:, 0] = met[:, 0]              # R = metallic
out[:, 3] = 1.0 - rgh[:, 0]        # A = smoothness (URP reads gloss from alpha)

ms = bpy.data.images.new(NAME + "_MetallicSmoothness", w, h, alpha=True)
ms.colorspace_settings.name = 'Non-Color'
ms.pixels.foreach_set(out.flatten())
ms.filepath_raw = os.path.join(TEXDIR, NAME + "_MetallicSmoothness.png")
ms.file_format = 'PNG'
ms.save()
print("wrote", ms.filepath_raw)
print("  metallic %.3f..%.3f | smoothness %.3f..%.3f"
      % (met[:, 0].min(), met[:, 0].max(), out[:, 3].min(), out[:, 3].max()))
print("FBX_MATERIAL:", mats[0] if mats else "NONE")
print("EXPORT DONE")
