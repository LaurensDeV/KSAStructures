#!/usr/bin/env python3
"""An MCP server that lets an agent drive a running KSA through the mod's bridge.

The mod reads commands from <KSA user dir>/Logs/bridge/KSAStructures/in and answers in .../out (Ksa/Bridge.cs):
files, so nothing needs a port and it works across the WSL boundary. This turns that folder into
tools -- load a save, hold the view in the office, light it, step the world, photograph it -- and hands pictures back
inline, so a capture arrives in the conversation the way Blender's MCP returns a render.

Stdlib only: MCP is JSON-RPC over stdin and stdout, one message a line.

    python3 tools/ksa-mcp/server.py                          # serve MCP on stdio
    python3 tools/ksa-mcp/server.py cli status               # one tool from a shell
    python3 tools/ksa-mcp/server.py cli capture '{"label":"a","frames":4}'
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "vis"))

import vis  # noqa: E402

_user_dir: Path | None = None


def user_dir() -> Path:
    global _user_dir
    if _user_dir is None:
        out = subprocess.run([str(REPO / "tools" / "ksa-user-dir.sh")], capture_output=True, text=True)
        _user_dir = Path(out.stdout.strip())
    return _user_dir


def bridge() -> Path:
    # The mod's own subfolder, so another mod's bridge never answers a command meant for this one.
    return user_dir() / "Logs" / "bridge" / "KSAStructures"


def game_running() -> bool:
    try:
        out = subprocess.run(["tasklist.exe"], capture_output=True, text=True, timeout=15).stdout.lower()
    except (OSError, subprocess.TimeoutExpired):
        return False
    return "starmap.exe" in out or "kittenspaceagency" in out


class Refused(Exception):
    pass


def wsl(path: str) -> str:
    """A path the mod wrote, from Windows, as this side reads it."""
    if len(path) > 2 and path[1] == ":" and path[2] in "\\/":
        return f"/mnt/{path[0].lower()}/" + path[3:].replace("\\", "/")
    return path


def send(cmd: str, timeout: float = 30.0, **args) -> dict:
    """One command to the mod, and its reply. Raises Refused with the mod's own reason."""
    inbox, outbox = bridge() / "in", bridge() / "out"
    inbox.mkdir(parents=True, exist_ok=True)
    ident = f"{time.time_ns()}"
    body = {"id": ident, "cmd": cmd, **{k: v for k, v in args.items() if v is not None}}

    part = inbox / f"{ident}.json.part"
    part.write_text(json.dumps(body))
    part.rename(inbox / f"{ident}.json")

    reply_path = outbox / f"{ident}.json"
    deadline = time.time() + timeout
    while time.time() < deadline:
        if reply_path.exists():
            try:
                reply = json.loads(reply_path.read_text())
            except json.JSONDecodeError:
                time.sleep(0.05)
                continue
            reply_path.unlink(missing_ok=True)
            if not reply.get("ok"):
                raise Refused(reply.get("error") or "refused")
            return reply.get("data") or {}
        time.sleep(0.05)

    (inbox / f"{ident}.json").unlink(missing_ok=True)
    raise Refused(f"no reply to '{cmd}' in {timeout:.0f} s -- is the game running with this build?")


# ---- the game's lifetime -------------------------------------------------------------------

STATE = Path(__file__).resolve().parent / ".launched"


def launch(save: str | None = None) -> str:
    """Starts the game if none is running. Never a second one, and never over the player's."""
    if game_running():
        note = "a game is already running; using it"
    else:
        log = open(Path(__file__).resolve().parent / "launch.log", "w")
        proc = subprocess.Popen([str(REPO / "tools" / "run.sh")], cwd=REPO, stdout=log,
                                stderr=subprocess.STDOUT, start_new_session=True)
        STATE.write_text(str(proc.pid))
        note = "launched"

    deadline = time.time() + 300
    while True:
        try:
            send("status", timeout=5)
            break
        except Refused:
            if time.time() > deadline:
                raise Refused("the game never answered on the bridge")
            time.sleep(2)

    if save:
        data = send("load", timeout=90, save=save)
        note += f"; loaded '{save}', flying {data.get('craft')}"
    return note


def quit_game() -> str:
    """Closes a game this server launched, and refuses any other: that one is somebody's session."""
    if not STATE.exists():
        raise Refused("this server did not launch the running game; it is the player's to close")
    pid = int(STATE.read_text())
    try:
        os.killpg(pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    STATE.unlink(missing_ok=True)
    return "closed"


# ---- captures ------------------------------------------------------------------------------

def capture(label: str = "shot", frames: int = 1, every_s: float | None = None,
            every_frames: int | None = None, width: int = 960) -> list[dict]:
    """Pictures and what they show. A series comes back as a sheet, an animation and a temporal map."""
    content: list[dict] = []

    data = send("capture", timeout=60 + frames * 10, label=label, frames=frames,
                every_s=every_s, every_frames=every_frames)
    shots = data["frames"]
    imgs = [vis.load(wsl(s["file"])) for s in shots]

    if frames == 1:
        content.append(_text(f"{shots[0]['file']}\n{_brief(shots[0])}"))
        content.append(_image(imgs[0], width))
        return content

    folder = Path(wsl(data["folder"]))
    labels = [f"#{s.get('index', '?')}" for s in shots]
    sheet_path = folder / "sheet.jpg"
    vis.sheet(imgs, labels, cols=min(4, len(imgs))).save(sheet_path, quality=88)
    gif_path = vis.gif(imgs, folder / "series.gif")
    t, stats = vis.temporal(imgs)
    t.save(folder / "temporal.png")

    content.append(_text(f"{frames} frames in {folder}\nsheet {sheet_path}\nanimation {gif_path}\n"
                         f"temporal {stats} -- with the world paused, bright is renderer noise\n"
                         + "\n".join(_brief(s) for s in shots)))
    content.append(_image(vis.load(sheet_path), 1280))
    content.append(_image(t, width))
    return content


def _brief(shot: dict) -> str:
    keys = ("label", "craft", "paused", "speed", "fov_deg")
    return ", ".join(f"{k}={shot[k]}" for k in keys if k in shot)


def _text(text: str) -> dict:
    return {"type": "text", "text": text}


def _image(img, width: int) -> dict:
    return {"type": "image", "data": vis.jpeg_b64(img, width), "mimeType": "image/jpeg"}


def reload_shaders() -> str:
    """Copies the shaders from the tree into the installed mod and recompiles them in the game.

    A write from WSL reaches the game late: flushed or not, the game can compile the file as it was
    before the copy and report "reloaded". So the reply's size and hash for each file are checked
    against the tree's, and the reload is repeated until the game compiled what the tree holds."""
    src = REPO / "src" / "KSAStructures" / "Shaders"
    dst = user_dir() / "mods" / "KSAStructures" / "Shaders"
    want = {}
    for f in src.glob("*.comp"):
        shutil.copy2(f, dst / f.name)
        with open(dst / f.name, "rb+") as out:
            os.fsync(out.fileno())
        want[f.name] = (f.stat().st_size, hashlib.sha1(f.read_bytes()).hexdigest()[:12])

    for attempt in range(8):
        data = send("reload_shaders", timeout=60)
        seen = " ".join(data.get("modules") or [])
        stale = [name for name, (size, sha1) in want.items()
                 if name in seen and (f"{name}, {size} bytes" not in seen or sha1 not in seen)]
        if not stale:
            return f"reloaded {data.get('reloaded')} as the tree has them" + (f" (attempt {attempt + 1})" if attempt else "")
        time.sleep(0.5)

    raise Refused(f"the game kept compiling an older {', '.join(stale)} than the tree's")


def log_tail(pattern: str = "", lines: int = 40) -> str:
    text = (user_dir() / "Logs" / "KSAStructures.log").read_text(errors="replace").splitlines()
    if pattern:
        text = [t for t in text if pattern.lower() in t.lower()]
    return "\n".join(text[-lines:])


# ---- the tools -----------------------------------------------------------------------------

def _num(desc):
    return {"type": "number", "description": desc}


TOOLS = {
    "ksa_status": ("What the game is doing: scene, craft, pause, speed, and whether a view is held.", {}, [],
                   lambda a: [_text(json.dumps(send("status"), indent=1))]),
    "ksa_launch": ("Start KSA with this tree's deployed build if it is not running, and optionally load a "
                   "save. Uses a running game rather than starting a second.",
                   {"save": {"type": "string"}}, [], lambda a: [_text(launch(a.get("save")))]),
    "ksa_quit": ("Close the game, only if this server launched it.", {}, [], lambda a: [_text(quit_game())]),
    "ksa_load": ("Load a save by name.", {"save": {"type": "string"}}, ["save"],
                 lambda a: [_text(json.dumps(send("load", timeout=90, save=a["save"])))]),
    "ksa_pause": ("Pause the world.", {}, [], lambda a: [_text(json.dumps(send("pause")))]),
    "ksa_resume": ("Resume the world.", {}, [], lambda a: [_text(json.dumps(send("resume")))]),
    "ksa_speed": ("Set the simulation speed.", {"x": _num("speed multiple")}, ["x"],
                  lambda a: [_text(json.dumps(send("speed", x=a["x"])))]),
    "ksa_step": ("Run the world for so many simulated seconds, then pause.", {"seconds": _num("sim seconds")},
                 ["seconds"], lambda a: [_text(json.dumps(send("step", timeout=a["seconds"] * 20 + 40,
                                                               seconds=a["seconds"])))]),
    "ksa_capture": ("Screenshot the game. frames>1 takes a series (every_s simulated seconds, or every_frames "
                    "rendered frames when paused) and returns a sheet, an animation path and a temporal-noise "
                    "map.",
                    {"label": {"type": "string"}, "frames": _num("count"), "every_s": _num("sim s"),
                     "every_frames": _num("frames"), "width": _num("px")}, [],
                    lambda a: capture(**a)),
    "ksa_set": ("Set a setting by name, as the settings window would: FirstPerson, OfficeLamps, VerboseLog.",
                {"name": {"type": "string"}, "value": {}}, ["name", "value"],
                lambda a: [_text(json.dumps(send("set", name=a["name"], value=a["value"])))]),
    "ksa_get": ("Read a setting by name.", {"name": {"type": "string"}}, ["name"],
                lambda a: [_text(json.dumps(send("get", name=a["name"])))]),
    "ksa_camera": ("Hold the main view east, north and up of the craft being flown, looking at it or at the "
                   "craft named at (at_up metres above it). release hands the view back.",
                   {"east": _num("m"), "north": _num("m"), "up": _num("m"), "at": {"type": "string"},
                    "at_up": _num("m"), "fov_deg": _num("deg"), "release": {"type": "boolean"}}, [],
                   lambda a: [_text(json.dumps(send("camera", **a)))]),
    "ksa_reload_shaders": ("Copy src/KSAStructures/Shaders into the installed mod and recompile them in the "
                           "running game. A compile error comes back as the error; the old shader stays.", {}, [],
                           lambda a: [_text(reload_shaders())]),
    "ksa_office_view": ("Hold the main view at a place in the office tower's own frame: x up from the ground "
                        "(the garage floor is -5), y east and z north of the building's origin, looking along "
                        "a compass heading and a pitch. release hands the view back.",
                        {"x": _num("m up"), "y": _num("m east"), "z": _num("m north"), "heading_deg": _num("deg from north, east positive"),
                         "pitch_deg": _num("deg up"), "fov_deg": _num("deg"), "release": {"type": "boolean"}}, [],
                        lambda a: [_text(json.dumps(send("office_view", **a)))]),
    "ksa_office_light": ("The office garage's lighting, changed while the game runs: the darkening, the lamps' "
                         "shadows, their brightness, and the share of it the rooms return onto every face.",
                         {"darken": {"type": "boolean"}, "shadows": {"type": "boolean"}, "scale": _num("x"),
                          "fill": _num("0..1")}, [],
                         lambda a: [_text(json.dumps(send("office_light", **a)))]),
    "ksa_office_put": ("Set the flown craft down at a place in the office tower's own frame, underground included.",
                       {"x": _num("m up"), "y": _num("m east"), "z": _num("m north")}, [],
                       lambda a: [_text(json.dumps(send("office_put", **a)))]),
    "ksa_log": ("The mod's log, filtered.", {"pattern": {"type": "string"}, "lines": _num("count")}, [],
                lambda a: [_text(log_tail(a.get("pattern", ""), int(a.get("lines", 40))))]),
}


def call(name: str, args: dict) -> tuple[list[dict], bool]:
    if name not in TOOLS:
        return [_text(f"no tool {name}")], True
    try:
        return TOOLS[name][3](args or {}), False
    except Refused as e:
        return [_text(str(e))], True
    except Exception as e:  # noqa: BLE001 -- a tool that throws must still answer
        return [_text(f"{type(e).__name__}: {e}")], True


def serve() -> None:
    def reply(ident, result=None, error=None):
        msg = {"jsonrpc": "2.0", "id": ident}
        msg["result" if error is None else "error"] = result if error is None else error
        sys.stdout.write(json.dumps(msg) + "\n")
        sys.stdout.flush()

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue

        method, ident = msg.get("method"), msg.get("id")
        if ident is None:
            continue  # a notification

        if method == "initialize":
            version = (msg.get("params") or {}).get("protocolVersion", "2025-06-18")
            reply(ident, {"protocolVersion": version, "capabilities": {"tools": {"listChanged": False}},
                          "serverInfo": {"name": "ksa", "version": "0.1"}})
        elif method == "ping":
            reply(ident, {})
        elif method == "tools/list":
            reply(ident, {"tools": [{"name": n, "description": t[0],
                                     "inputSchema": {"type": "object", "properties": t[1], "required": t[2]}}
                                    for n, t in TOOLS.items()]})
        elif method == "tools/call":
            params = msg.get("params") or {}
            content, is_error = call(params.get("name", ""), params.get("arguments") or {})
            reply(ident, {"content": content, "isError": is_error})
        else:
            reply(ident, error={"code": -32601, "message": f"no method {method}"})


def cli(argv: list[str]) -> int:
    name = argv[0] if argv[0].startswith("ksa_") else f"ksa_{argv[0]}"
    args = json.loads(argv[1]) if len(argv) > 1 else {}
    content, is_error = call(name, args)

    # What an MCP client would be shown inline, written out so a shell can look at it too.
    shown = Path(__file__).resolve().parent / "last"
    shutil.rmtree(shown, ignore_errors=True)
    shown.mkdir()
    for k, c in enumerate(content):
        if c["type"] == "text":
            print(c["text"])
        else:
            path = shown / f"{k:02d}.jpg"
            path.write_bytes(base64.b64decode(c["data"]))
            print(f"[image] {path}")
    return 1 if is_error else 0


if __name__ == "__main__":
    if len(sys.argv) > 2 and sys.argv[1] == "cli":
        sys.exit(cli(sys.argv[2:]))
    serve()
