"""Hair from Shapes, as a Blender add-on: the same library agents call (hair_shapes.py), as buttons in
the 3D view's sidebar (N), Hair tab, for a person to finish the hair by hand.

  Shapes     tubes on or off, stack the sections (the collection's children, the first nearest the scalp)
  Convert    the active section collection to live hair, with its settings in the redo panel
  Bake       guides (comb them, the children stay live), strands (everything real), unbake
  Use        follow the head (cut-scenes in Blender), export a groom (.abc) for a game engine
"""
import bpy
from bpy.props import BoolProperty, FloatProperty, IntProperty, PointerProperty, StringProperty
from bpy_extras.io_utils import ExportHelper

from . import hair_shapes as hs


def _shapes(context):
    return context.scene.hs_shapes or bpy.data.collections.get(hs.SHAPES)


def _surface(context, ob=None):
    if context.scene.hs_surface:
        return context.scene.hs_surface
    if ob is not None and ob.parent and ob.parent.type == "MESH":
        return ob.parent
    return None


def _viewport_share(scene, _context=None):
    """Sets how many of the children every live hair object shows in the viewport (renders get all)."""
    for ob in bpy.data.objects:
        for md in ob.modifiers:
            if md.type == "NODES" and md.node_group and md.node_group.name.startswith("Duplicate Hair Curves"):
                for i in md.node_group.interface.items_tree:
                    if getattr(i, "in_out", "") == "INPUT" and i.name == "Viewport Amount":
                        getattr(md.properties.inputs, i.identifier).value = scene.hs_viewport
        if ob.type == "CURVES" and ob.modifiers:
            ob.update_tag()


def _hair_selected(context):
    return [o for o in context.selected_objects if o.type == "CURVES"]


class HS_OT_tubes(bpy.types.Operator):
    """Show the locks as tubes of their radius, or as bare curves (always bare before converting)"""
    bl_idname = "hair_shapes.tubes"
    bl_label = "Tubes"
    bl_options = {"REGISTER", "UNDO"}
    on: BoolProperty(name="On", default=True)

    def execute(self, context):
        coll = _shapes(context)
        if coll is None:
            self.report({"ERROR"}, "No shapes collection")
            return {"CANCELLED"}
        for c in [coll, *coll.children_recursive]:
            hs.preview(c.name, on=self.on)
        return {"FINISHED"}


class HS_OT_stack(bpy.types.Operator):
    """Layer the sections like shingles: each child collection of the shapes collection is lifted over the
    ones listed before it (the first is nearest the scalp). Deep crossings are listed in the info log"""
    bl_idname = "hair_shapes.stack"
    bl_label = "Stack Sections"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        coll = _shapes(context)
        surf = _surface(context)
        if coll is None or surf is None:
            self.report({"ERROR"}, "Set the shapes collection and the head (surface) first")
            return {"CANCELLED"}
        order = [c.name for c in coll.children]
        res = hs.stack(order, surf)
        self.report({"INFO"}, "; ".join(f"{k}: {v[0]} points, up to {v[1] * 1000:.0f} mm" for k, v in res.items()))
        return {"FINISHED"}


class HS_OT_convert(bpy.types.Operator):
    """Turn the active section collection (pick it in the Outliner) into live strand hair. Edit the locks
    afterwards and the hair follows; tune it in the redo panel or on its modifiers"""
    bl_idname = "hair_shapes.convert"
    bl_label = "Convert Section"
    bl_options = {"REGISTER", "UNDO"}
    guides: IntProperty(name="Guides per lock", default=10, min=1, max=64)
    children: IntProperty(name="Children per guide", default=60, min=0, max=400)
    spread: FloatProperty(name="Spread", default=0.0025, min=0.0, unit="LENGTH", precision=4)
    clump: FloatProperty(name="Clump", default=0.55, min=0.0, max=1.0)
    trim: FloatProperty(name="Trim", default=0.02, min=0.0, unit="LENGTH", precision=4)
    frizz: FloatProperty(name="Frizz", default=0.0006, min=0.0, unit="LENGTH", precision=4)
    noise: FloatProperty(name="Noise", default=0.0012, min=0.0, unit="LENGTH", precision=4)
    curl: FloatProperty(name="Curl", default=0.0, min=0.0, unit="LENGTH", precision=4)
    strand_radius: FloatProperty(name="Strand radius", default=0.00006, min=0.0, unit="LENGTH", precision=5)
    strays: BoolProperty(name="Add strays", default=True)

    def execute(self, context):
        coll = context.view_layer.active_layer_collection.collection
        if coll is None or not coll.all_objects:
            self.report({"ERROR"}, "Make the section's collection active in the Outliner first")
            return {"CANCELLED"}
        surf = _surface(context)
        hs.preview(coll.name, on=False)
        mat = context.scene.hs_material
        ob = hs.hair(f"Hair {coll.name}", coll.name, guides=self.guides, children=self.children, spread=self.spread,
                     clump=self.clump, trim=self.trim, frizz=self.frizz, noise=self.noise, curl=self.curl,
                     strand_radius=self.strand_radius, material=mat, parent=surf, viewport=context.scene.hs_viewport)
        if self.strays:
            hs.strays(f"Strays {coll.name}", coll.name, material=mat, parent=surf)
        coll.hide_render = True
        context.view_layer.objects.active = ob
        return {"FINISHED"}


class _Bake:
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return bool(_hair_selected(context))


class HS_OT_bake_guides(_Bake, bpy.types.Operator):
    """Make the selected live hair's guides real curves to comb in Curves Sculpt mode; the children, clumps
    and frizz stay live on top. The live object is kept, hidden"""
    bl_idname = "hair_shapes.bake_guides"
    bl_label = "Bake Guides"

    def execute(self, context):
        done = []
        for ob in _hair_selected(context):
            if ob.modifiers and ob.modifiers[0].name == "Shapes to Hair":
                done.append(hs.bake_guides(ob, _surface(context, ob)))
        if not done:
            self.report({"ERROR"}, "Select live hair (made by Convert Section)")
            return {"CANCELLED"}
        for o in context.selected_objects:
            o.select_set(False)
        for o in done:
            o.select_set(True)
        context.view_layer.objects.active = done[0]
        return {"FINISHED"}


class HS_OT_bake(_Bake, bpy.types.Operator):
    """Bake the selected hair (live, or brushed guides) into plain strands to brush, render or export. The
    source is kept, hidden: Unbake brings it back"""
    bl_idname = "hair_shapes.bake"
    bl_label = "Bake Strands"

    def execute(self, context):
        done = [hs.bake(ob, _surface(context, ob)) for ob in _hair_selected(context) if ob.modifiers]
        for o in context.selected_objects:
            o.select_set(False)
        for o in done:
            o.select_set(True)
        if done:
            context.view_layer.objects.active = done[0]
        return {"FINISHED"}


class HS_OT_unbake(_Bake, bpy.types.Operator):
    """Remove the selected bakes and show what they were baked from"""
    bl_idname = "hair_shapes.unbake"
    bl_label = "Unbake"

    def execute(self, context):
        for ob in [o for o in _hair_selected(context) if "hs_source" in o]:
            src = hs.unbake(ob)
            src.select_set(True)
            context.view_layer.objects.active = src
        return {"FINISHED"}


class HS_OT_follow(_Bake, bpy.types.Operator):
    """Make the selected baked hair follow the head as it moves and deforms (for cut-scenes in Blender)"""
    bl_idname = "hair_shapes.follow"
    bl_label = "Follow Head"

    def execute(self, context):
        for ob in _hair_selected(context):
            if "surface_uv_coordinate" not in ob.data.attributes:
                surf = _surface(context, ob)
                if surf is None:
                    self.report({"ERROR"}, f"{ob.name}: set the head (surface) first")
                    return {"CANCELLED"}
                hs.attach(ob, surf)
            hs.follow(ob)
        return {"FINISHED"}


class HS_OT_game_budget(_Bake, bpy.types.Operator):
    """Fit the selected live hair to a game's strand budget: fewer, slightly wider strands, still editable"""
    bl_idname = "hair_shapes.game_budget"
    bl_label = "Game Budget"
    strands: IntProperty(name="Strands", default=hs.GAME_STRANDS, min=1000, max=1000000,
                         description="All the selected hair together. Epic's MetaHuman head hair: 50,000 at LOD0, 25,000 at LOD1")
    widen: BoolProperty(name="Widen strands", default=True, description="Keep the hair's cover with fewer strands")

    def execute(self, context):
        before, after = hs.game_budget(_hair_selected(context), self.strands, self.widen)
        self.report({"INFO"}, f"{before} strands -> {after}")
        return {"FINISHED"}


class HS_OT_export_groom(_Bake, bpy.types.Operator, ExportHelper):
    """Export the selected hair as an Alembic groom for a game engine: one group per object, in centimetres"""
    bl_idname = "hair_shapes.export_groom"
    bl_label = "Export Groom (.abc)"
    filename_ext = ".abc"
    filter_glob: StringProperty(default="*.abc", options={"HIDDEN"})

    def execute(self, context):
        r = hs.export_groom(_hair_selected(context), self.filepath)
        self.report({"INFO"}, f"{r['groups']} groups, {r['strands']} strands, {r['points']} points")
        return {"FINISHED"}


class HS_PT_panel(bpy.types.Panel):
    bl_label = "Hair from Shapes"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Hair"

    def draw(self, context):
        sc, lay = context.scene, self.layout
        col = lay.column(align=True)
        col.prop(sc, "hs_shapes", text="Shapes")
        col.prop(sc, "hs_surface", text="Head")
        col.prop(sc, "hs_material", text="Material")
        col.prop(sc, "hs_viewport", text="Viewport", slider=True)
        box = lay.box()
        box.label(text="Shapes")
        row = box.row(align=True)
        row.operator("hair_shapes.tubes", text="Tubes On").on = True
        row.operator("hair_shapes.tubes", text="Tubes Off").on = False
        box.operator("hair_shapes.stack")
        box.label(text="Sections stack in collection order, first nearest the scalp", icon="INFO")
        box = lay.box()
        box.label(text="Convert")
        box.operator("hair_shapes.convert")
        box = lay.box()
        box.label(text="Bake")
        box.operator("hair_shapes.bake_guides")
        box.operator("hair_shapes.bake")
        box.operator("hair_shapes.unbake")
        box = lay.box()
        box.label(text="Use")
        box.operator("hair_shapes.follow")
        box.operator("hair_shapes.game_budget")
        box.operator("hair_shapes.export_groom")


CLASSES = (HS_OT_tubes, HS_OT_stack, HS_OT_convert, HS_OT_bake_guides, HS_OT_bake, HS_OT_unbake, HS_OT_follow,
           HS_OT_game_budget, HS_OT_export_groom, HS_PT_panel)


def register():
    bpy.types.Scene.hs_shapes = PointerProperty(type=bpy.types.Collection, name="Shapes",
                                                description="The collection holding the locks; its children are the sections")
    bpy.types.Scene.hs_surface = PointerProperty(type=bpy.types.Object, name="Head",
                                                 poll=lambda _s, o: o.type == "MESH",
                                                 description="The head the hair grows from (needs a UV map for baking)")
    bpy.types.Scene.hs_material = PointerProperty(type=bpy.types.Material, name="Material",
                                                  description="The material new hair gets")
    bpy.types.Scene.hs_viewport = FloatProperty(name="Viewport", default=0.1, min=0.0, max=1.0, update=_viewport_share,
                                                description="Share of the hair shown in the viewport (renders get all). "
                                                            "A full head at 1 can crash Material Preview")
    for c in CLASSES:
        bpy.utils.register_class(c)


def unregister():
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)
    del bpy.types.Scene.hs_shapes, bpy.types.Scene.hs_surface, bpy.types.Scene.hs_material, bpy.types.Scene.hs_viewport
