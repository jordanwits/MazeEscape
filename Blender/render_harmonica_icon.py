# Hotbar icon for the harmonica: 512x512 transparent PNG, item laid diagonally like the other ItemIcons.
#   blender.exe Harmonica.blend -b --python render_harmonica_icon.py -- <out.png>
import bpy, math, sys
from mathutils import Vector, Euler

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUT = argv[0] if argv else r"H:/Unity/Maze Escape/Assets/Icons/ItemIcons/HarmonicaIcon.png"

sc = bpy.context.scene
root = bpy.data.objects["Harmonica"]
# Diagonal across the frame, tipped toward the camera so the holes and the stamped cover both read.
root.rotation_euler = Euler((math.radians(38), math.radians(-35), 0.0), "XYZ")
bpy.context.view_layer.update()

parts = [o for o in root.children if o.type == 'MESH']
pts = [o.matrix_world @ Vector(c) for o in parts for c in o.bound_box]
lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
centre = (lo + hi) * 0.5

cam = sc.camera
cam.data.type = 'ORTHO'
cam.location = centre + Vector((0.0, -0.5, 0.0))
cam.rotation_euler = (math.radians(90), 0.0, 0.0)          # looking down +Y at the item
cam.data.ortho_scale = max(hi.x - lo.x, hi.z - lo.z) * 1.12
cam.data.clip_end = 2.0

# brighter studio than the preview so the dark, grimy steel still reads on the dark hotbar
for ob in [o for o in bpy.data.objects if o.type == 'LIGHT']:
    bpy.data.objects.remove(ob, do_unlink=True)
def area(name, loc, size, power):
    d = bpy.data.lights.new(name, 'AREA'); d.size, d.energy = size, power
    o = bpy.data.objects.new(name, d); sc.collection.objects.link(o)
    o.location = centre + Vector(loc)
    o.rotation_euler = (centre - o.location).to_track_quat('-Z', 'Y').to_euler()
area("Key",  (-0.18, -0.25, 0.22), 0.30, 5.0)
area("Fill", ( 0.25, -0.20, -0.05), 0.40, 2.0)
area("Rim",  ( 0.05,  0.25, 0.20), 0.30, 3.0)
w = sc.world
bg = next(n for n in w.node_tree.nodes if n.type == 'BACKGROUND')
bg.inputs['Strength'].default_value = 0.6

sc.render.engine = 'CYCLES'
sc.cycles.device = 'CPU'
sc.cycles.samples = 96
sc.cycles.use_denoising = True
sc.render.film_transparent = True
sc.render.resolution_x = sc.render.resolution_y = 512
sc.render.resolution_percentage = 100
sc.render.image_settings.file_format = 'PNG'
sc.render.image_settings.color_mode = 'RGBA'
sc.view_settings.view_transform = 'Standard'
sc.render.filepath = OUT
bpy.ops.render.render(write_still=True)
print("ICON WRITTEN", OUT)
