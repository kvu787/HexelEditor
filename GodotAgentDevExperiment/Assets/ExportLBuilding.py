import bpy, math
bpy.ops.wm.read_factory_settings(use_empty=True)
# An L-shaped building: two boxes, one material carrying SimplePaint parameters as custom properties.
mat = bpy.data.materials.new("BuildingPaint")
mat["SimplePaint.BaseColorSrgb"] = [0.107, 0.223, 0.578]
mat["SimplePaint.Brightness"] = 0.5
mat["SimplePaint.Shift"] = 0.6
mat["SimplePaint.RotationDegrees"] = 45.0
mat["SimplePaint.DarkPoint"] = 0.05
mat["SimplePaint.LightPoint"] = 0.95
def box(name, size, location):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location)
    o = bpy.context.active_object
    o.name = name
    o.scale = (size[0], size[1], size[2])
    o.data.materials.append(mat)
    return o
a = box("Wing", (3.0, 1.0, 1.2), (0.0, 0.0, 0.6))
b = box("Tower", (1.0, 2.5, 2.0), (-1.0, 0.75, 1.0))
bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.transform_apply(scale=True)
bpy.ops.export_scene.gltf(filepath=r"C:\Users\k\AppData\Local\Temp\claude\C--Users-k-Repository-HexelEditor\6f573e45-4c2b-4f80-8e99-033afec691ef\scratchpad\BlenderExport\LBuilding.glb", export_format='GLB', export_extras=True, export_apply=True, export_yup=True, use_selection=False)
print("EXPORTED")
