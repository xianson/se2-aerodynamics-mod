"""Export the per-blade break models (blade_break_rotors.py, BladeBreak_v2.blend) with the propulsor exporter's own FBX /
Havok-glTF settings (its functions read from propulsor_export.py). Per rotor N, into the propulsor export folder:
  Models/NonFractured/Subpart05/<N>_S5_BladeStub          (N|Stub_LODn)
  Models/NonFractured/Subpart07/<N>_S7_BladeTipLost       (N|BladeTipLost_LODn)
  Models/NonFractured/Subpart08/<N>_S8_BladeCCWStub       (main rotors: N|BladeCCWStub_LODn)
  Models/NonFractured/Subpart09/<N>_S9_BladeCCWTipLost    (main rotors: N|BladeCCWTipLost_LODn)
  Models/Chunks/<N>_BladeChunk{A,B}{,_LOD1,_LOD2}.fbx + <N>_BladeChunk{A,B}_collision.gltf   (the flying pieces' hulls)
Run (a VISIBLE Blender: the Havok glTF extension needs a GPU context):
  blender BladeBreak_v2.blend --python blade_break_rotors_export.py -- <export dir>
Log: <export dir>/export_log_BladeBreakRotors.txt
"""
import bpy, os, sys, traceback

OUT = sys.argv[sys.argv.index("--") + 1:][0]
PE = os.path.join(os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else
                  r"C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\tools\meshgen", "propulsor_export.py")
src = open(PE).read()
LOG = []
exec(src[src.index("def lc_of"):src.index("def blocks")])   # lc_of, base, BaseNames, export_fbx, export_collision
MAIN = ["HelicopterRotor_050", "HelicopterRotor_100", "HelicopterRotor_150", "HelicopterRotor_250"]
ALL = MAIN + ["HelicopterTailRotor_050"]
PARTS = [(5, "Stub", "BladeStub"), (7, "BladeTipLost", "BladeTipLost"), (8, "BladeCCWStub", "BladeCCWStub"), (9, "BladeCCWTipLost", "BladeCCWTipLost")]


def run():
    try:
        for o in bpy.data.objects:
            try: o.hide_set(False)
            except RuntimeError: pass
        sfx = lambda n: f"_LOD{n}" if n else ""
        name = lambda o: o.get("ExportName") or o.name
        for N in ALL:
            m = os.path.join(OUT, N, "Models"); ch = os.path.join(m, "Chunks"); os.makedirs(ch, exist_ok=True)
            for k, coll, stem in PARTS:
                if f"{N}|{coll}_LOD0" not in bpy.data.collections: continue
                sp = os.path.join(m, "NonFractured", f"Subpart0{k}"); os.makedirs(sp, exist_ok=True)
                for n in range(3):
                    export_fbx(f"{N}|{coll}_LOD{n}", os.path.join(sp, f"{N}_S{k}_{stem}{sfx(n)}.fbx"), OUT)
            for n in range(3):
                for k in ("ChunkA", "ChunkB"):
                    export_fbx(f"{N}|{k}_LOD{n}", os.path.join(ch, f"{N}_Blade{k}{sfx(n)}.fbx"), OUT)
            for k in ("ChunkA", "ChunkB"):
                export_collision(f"{N}|{k}_Collision", os.path.join(ch, f"{N}_Blade{k}_collision.gltf"), name, OUT)
            LOG.append(f"OK {N}")
        LOG.append("ALL OK")
    except Exception:
        LOG.append(traceback.format_exc())
    os.makedirs(OUT, exist_ok=True)
    open(os.path.join(OUT, "export_log_BladeBreakRotors.txt"), "w").write(chr(10).join(LOG))
    bpy.ops.wm.quit_blender()


bpy.app.timers.register(run, first_interval=2.0)
