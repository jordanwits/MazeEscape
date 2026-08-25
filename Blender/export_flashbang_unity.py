# ---------------------------------------------------------------------------
# Export Flashbang.blend to a Unity-ready FBX + pack the URP MetallicSmoothness.
# Run with the blend ALREADY open so no wm.open_mainfile is needed in-script:
#   blender.exe "Flashbang.blend" --background --python export_flashbang_unity.py
# ---------------------------------------------------------------------------
import bpy, os
import numpy as np

ROOT   = r"H:/Unity/Maze Escape/Blender"
TEXDIR = os.path.join(ROOT, "textures")
FBX    = os.path.join(ROOT, "Flashbang.fbx")

# ---------------------------------------------------------- 1. single mesh
# All 12 parts already share M_Flashbang, so joining a throwaway copy gives
# Unity one mesh / one material / one draw call. The .blend keeps the parts
# separate for future edits; this join is never saved back.
root = bpy.data.objects["Flashbang"]
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
merged.name = "Flashbang"
merged.data.name = "Flashbang"
merged.data.calc_loop_triangles()
print("merged tris:", len(merged.data.loop_triangles),
      "verts:", len(merged.data.vertices),
      "materials:", [m.name for m in merged.data.materials])

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
    bake_space_transform=True,             # Z-up -> Y-up baked into verts
    use_mesh_modifiers=True,
    mesh_smooth_type='FACE',
    add_leaf_bones=False,
    bake_anim=False,
    path_mode='STRIP',
)
print("wrote", FBX, os.path.getsize(FBX), "bytes")

# ------------------------------ 3. URP MetallicSmoothness (R=metal, A=gloss)
def load_linear(name):
    img = bpy.data.images.load(os.path.join(TEXDIR, name), check_existing=False)
    img.colorspace_settings.name = 'Non-Color'
    buf = np.empty(len(img.pixels), dtype=np.float32)
    img.pixels.foreach_get(buf)
    return img, buf.reshape(-1, 4)

met_img, met = load_linear("Flashbang_Metallic.png")
rgh_img, rgh = load_linear("Flashbang_Roughness.png")
w, h = met_img.size
assert rgh_img.size[:] == met_img.size[:], "metallic/roughness size mismatch"

out = np.zeros_like(met)
out[:, 0] = met[:, 0]              # R = metallic
out[:, 3] = 1.0 - rgh[:, 0]        # A = smoothness (URP reads gloss from alpha)

ms = bpy.data.images.new("Flashbang_MetallicSmoothness", w, h, alpha=True)
ms.colorspace_settings.name = 'Non-Color'
ms.pixels.foreach_set(out.flatten())
ms.filepath_raw = os.path.join(TEXDIR, "Flashbang_MetallicSmoothness.png")
ms.file_format = 'PNG'
ms.save()
print("wrote", ms.filepath_raw)
print("  metallic range %.3f..%.3f | smoothness range %.3f..%.3f"
      % (met[:, 0].min(), met[:, 0].max(), out[:, 3].min(), out[:, 3].max()))
print("EXPORT DONE")
