# Runs inside Blender (via --python) once the job's .blend has been opened by the render client.
#
#   blender <job.blend> --python blender_task_setup.py -- \
#       --frame-start 1 --frame-end 25 --artnet-ip 127.0.0.1 --artnet-port 6454
#
# 1. Forces the DMX Connector add-on's UDP (Art-Net) client to the given address/port and turns it on.
# 2. Sets the frame snapshot export range to the task's frames.
# 3. Starts the frame snapshot export (bthl.frame_snapshot_range_export) once Blender's window exists.
#    The add-on asks HNode to save each frame as <blend name>_frames/frame_NNNNNN.png.
# 4. When the export stops, prints BDMX_EXPORT_DONE and quits Blender (without a save prompt).
#
# Status goes to stdout, which the client reads: "BDMX: ..." for progress, "BDMX_ERROR: ..." for
# failures (the client fails the task with that message), "BDMX_EXPORT_DONE" when the export has
# stopped (the client then checks the frames are all there and uploads them).
import argparse
import sys

import bpy

EXPORT_OPERATOR = "BTHL_OT_frame_snapshot_range_export"
EXPORT_DONE = "BDMX_EXPORT_DONE"
SCENE_PROPERTIES = ("udp_target_ip", "udp_target_port", "udp_client_active",
                    "frame_snapshot_range_start", "frame_snapshot_range_end")


def status(message):
    print(f"BDMX: {message}", flush=True)


def error(message):
    print(f"BDMX_ERROR: {message}", flush=True)


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(prog="blender_task_setup.py")
    parser.add_argument("--frame-start", type=int, required=True)
    parser.add_argument("--frame-end", type=int, required=True)
    parser.add_argument("--artnet-ip", default="127.0.0.1")
    parser.add_argument("--artnet-port", type=int, default=6454)
    try:
        return parser.parse_args(argv)
    except SystemExit:  # argparse exits on bad arguments; report instead of killing the setup silently
        error(f"Invalid setup script arguments: {' '.join(argv)}")
        return None


def connector_available():
    return hasattr(bpy.types, EXPORT_OPERATOR) and all(hasattr(bpy.types.Scene, p) for p in SCENE_PROPERTIES)


def configure(args):
    scene = bpy.context.scene

    # Art-Net output to HNode.
    scene.udp_target_ip = args.artnet_ip
    scene.udp_target_port = args.artnet_port
    scene.udp_client_active = True

    # Frames to export.
    scene.frame_snapshot_range_start = args.frame_start
    scene.frame_snapshot_range_end = args.frame_end

    status(f"DMX connector UDP client -> {scene.udp_target_ip}:{scene.udp_target_port} "
           f"(active={scene.udp_client_active}); snapshot range {scene.frame_snapshot_range_start}-"
           f"{scene.frame_snapshot_range_end} in scene '{scene.name}'")


def start_export():
    """Timer callback: the export is a modal operator, so it needs a window to run in."""
    windows = bpy.context.window_manager.windows
    if not windows:
        return 0.5  # UI not up yet; try again shortly

    window = windows[0]
    try:
        with bpy.context.temp_override(window=window, screen=window.screen):
            result = bpy.ops.bthl.frame_snapshot_range_export('INVOKE_DEFAULT')
    except Exception as ex:  # noqa: BLE001 - report anything to the client
        error(f"Could not start the frame snapshot export: {ex}")
        return None

    if "RUNNING_MODAL" in result:
        status("Frame snapshot export started")
        bpy.app.timers.register(watch_export, first_interval=0.5)
    else:
        error(f"Frame snapshot export did not start (operator returned {sorted(result)})")
    return None


def watch_export():
    """Timer callback: once the add-on's export stops, tell the client and quit Blender."""
    if bpy.context.scene.frame_snapshot_running:
        return 0.5
    status("Frame snapshot export stopped")
    print(EXPORT_DONE, flush=True)
    bpy.app.timers.register(quit_blender, first_interval=0.5)
    return None


def quit_blender():
    windows = bpy.context.window_manager.windows
    # EXEC (the default for script calls) quits without the unsaved-changes prompt.
    if windows:
        with bpy.context.temp_override(window=windows[0], screen=windows[0].screen):
            bpy.ops.wm.quit_blender()
    else:
        bpy.ops.wm.quit_blender()
    return None


def main():
    args = parse_args()
    if args is None:
        return
    if not connector_available():
        error("The Blender DMX Connector add-on isn't installed or enabled in this Blender.")
        return
    configure(args)

    if bpy.app.background:
        status("Running in background mode; the frame snapshot export needs Blender's UI and was not started")
        return
    bpy.app.timers.register(start_export, first_interval=1.0)


main()
