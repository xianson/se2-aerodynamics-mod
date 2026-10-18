import bpy, addon_utils
addon_utils.enable("mcp_for_blender", default_set=True)
import mcp_for_blender as m
def go():
    srv = getattr(bpy.types, "blendermcp_server", None)
    if srv is None:
        srv = m.BlenderMCPServer(port=9876); bpy.types.blendermcp_server = srv
    if not srv.running: srv.start()
    try: bpy.context.scene.blendermcp_server_running = srv.running
    except Exception: pass
    return None
bpy.app.timers.register(go, first_interval=1.0)
