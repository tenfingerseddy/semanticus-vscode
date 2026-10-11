#!/usr/bin/env python3
"""drafts-sol round 3: the Model tree's Rename (F2) in a REAL VS Code, on a REAL engine, while another model opens (the check's T1).

Everything between the key press and the model is the real thing: the installed extension, its one registerPersonCommand wrapper, the real showInputBox, the extension's
own engine connection (FencedMessageWriter) and the engine's PersonFence. Input is real XTest on the lane's private Xwayland display (the UAT harness, ~/uat-sol/harness).
The assistant is a second connection: the lane's own MCP door (lane_engine_mcp.sh), the way Claude Code reaches the engine.

  1  The Model tree's Rename command (the one F2 is bound to: "Rename with Input Box...") on the column "AuditNote" of model A, by each of the two ways it starts:
       menu     right-click the row, then the menu item: the command gets the tree node, which carries the model it was fetched for;
       palette  the Command Palette: the command gets no node, so it takes the model the host showed at the moment it was started.
     The input box opens (a real showInputBox: this is the proof that AsyncLocalStorage survives it).
  2  While the input box is open the assistant opens model B (a copy: same objects, same names) over MCP.
  3  The person types the new name and presses Enter.
  4  Expected: nothing is written into B (its column AuditNote is still AuditNote), and the person is told why.
  5  Control: model A opens again, the same command, a new name, Enter: A's column IS renamed.

F2 itself is not driven: in this VS Code the key does nothing on a Model tree row (the Explorer's F2 works on the same display), because the binding's when-clause reads the menu-only
context key `viewItem`. That is not this lane's: it is reported as an item for the join.

usage: python3 dsol3-weston-rename-drive.py <lane>      (the lane is launched first, with the VSIX of the branch and the northstar fixture, and the Semanticus view is open)
Exit 0 only when every check passes.
"""
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

HARNESS = Path.home() / "uat-sol" / "harness"
sys.path.insert(0, str(HARNESS))
import lanelib as L  # noqa: E402
import xlane  # noqa: E402

if len(sys.argv) < 2:
    sys.exit(__doc__)
LANE = L.resolve_lane(sys.argv[1])
NODE = shutil.which("node")
NODE_FLAGS = ["--experimental-websocket"] if subprocess.run([NODE, "-e", "process.exit(typeof WebSocket==='function'?0:1)"], capture_output=True).returncode else []
results = []


def check(name, ok, detail=""):
    results.append(ok)
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else "  " + str(detail)), flush=True)


# ---- the lane's VS Code: element boxes over CDP, input over XTest ------------------------------------------------------------------------------------------------

ln = xlane.Lane(LANE)


def cdp(*args, timeout=45):
    r = subprocess.run([NODE, *NODE_FLAGS, str(HARNESS / "cdp_tree.mjs"), str(ln.rec["cdp_port"]), *map(str, args)], capture_output=True, text=True, timeout=timeout,
                       env={"PATH": os.environ.get("PATH", "/usr/bin"), "HOME": str(LANE / "home")})
    try:
        return json.loads(r.stdout)
    except ValueError:
        return {"ok": False, "error": (r.stderr or r.stdout)[-300:]}


def origin(meta):
    wins = ln.code_windows()
    pick = next((w for w in wins if w["name"] == meta.get("title")), None) or (wins[0] if wins else None)
    if not pick:
        raise SystemExit("the lane's VS Code has no window on its display")
    with ln as s:
        x, y, _, _ = s.geometry(pick["wid"])
    return x, y


def click_element(scope, role, name, nth=None, count=1):
    res = cdp("locate", scope, role, name, *([nth] if nth is not None else []))
    if not res.get("ok"):
        return False
    time.sleep(0.15)
    ox, oy = origin(res["meta"])
    dpr = res["meta"].get("dpr") or 1
    n = res["node"]
    with ln as s:
        s.guard()
        s.click(int(ox + (n["x"] + n["w"] / 2) * dpr), int(oy + (n["y"] + n["h"] / 2) * dpr), 1, count)
    return True


def key(combo):
    with ln as s:
        s.guard()
        s.key(combo)


def type_text(t):
    with ln as s:
        s.guard()
        s.type(t)


def wait_for(pred, timeout, step=0.5):
    end = time.time() + timeout
    while time.time() < end:
        v = pred()
        if v:
            return v
        time.sleep(step)
    return None


def shows(scope, text):
    return bool(cdp("text", scope, text).get("found"))


def screenshot(name):
    shots = LANE / "shots"
    shots.mkdir(exist_ok=True)
    before = set(shots.glob("wayland-screenshot-*.png"))
    try:
        subprocess.run(["/usr/bin/weston-screenshooter"], env=ln.env(), cwd=shots, check=True, timeout=20, capture_output=True)
    except Exception:
        return None
    new = sorted(set(shots.glob("wayland-screenshot-*.png")) - before)
    if not new:
        return None
    out = shots / f"dsol3-{name}.png"
    new[-1].rename(out)
    return out


# ---- the assistant: the lane's own MCP door -----------------------------------------------------------------------------------------------------------------------

class Mcp:
    def __init__(self):
        self.p = subprocess.Popen(["/bin/bash", str(HARNESS / "lane_engine_mcp.sh"), str(LANE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.id = 0
        self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "dsol3-drive", "version": "1"}})
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.p.stdin.flush()

    def rpc(self, method, params):
        self.id += 1
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.id, "method": method, "params": params}) + "\n")
        self.p.stdin.flush()
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError("the MCP door closed: " + (self.p.stderr.read() or "")[-300:])
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            if msg.get("id") == self.id:
                return msg

    def call(self, tool, **args):
        msg = self.rpc("tools/call", {"name": tool, "arguments": args})
        if "error" in msg:
            return False, json.dumps(msg["error"])
        res = msg["result"]
        body = "".join(c.get("text", "") for c in res.get("content", []))
        return not res.get("isError"), body

    def close(self):
        try:
            self.p.stdin.close()
            self.p.terminate()
        except Exception:
            pass


def column_names(mcp):
    """The names of the columns of the open model, as a set (so AuditNote and AuditNoteRenamedOnA are two names, not one substring of the other)."""
    ok, body = mcp.call("list_columns")
    if not ok:
        return None
    try:
        data, _ = json.JSONDecoder().raw_decode(body.lstrip())   # the result may end with the engine's "changed since your last call" line
        return {c.get("name") for c in (data if isinstance(data, list) else data.get("columns", []))}
    except (ValueError, AttributeError):
        return None


# ---- the drive ---------------------------------------------------------------------------------------------------------------------------------------------------

ws = LANE / "workspace"
a_path = next((str(p) for p in sorted(ws.iterdir()) if p.name.endswith(".SemanticModel")), None)
if not a_path:
    sys.exit("no .SemanticModel folder in the lane workspace")
b_dir = ws / "other-model"
shutil.rmtree(b_dir, ignore_errors=True)
b_dir.mkdir()
b_path = str(b_dir / Path(a_path).name)
shutil.copytree(a_path, b_path, ignore=shutil.ignore_patterns(".semanticus"))
PROMPT = 'Rename this column "AuditNote"'

mcp = Mcp()


def open_model(path):
    ok, text = mcp.call("open_model", path=path, discardUnsaved=True)
    time.sleep(3)   # the host learns of the swap, refreshes its tree and its idea of the open model
    return ok, text


def start_rename(how):
    """Select the column AuditNote of the open model in the Model tree and start the Rename command the given way."""
    key("Escape")
    assert click_element("workbench", "treeitem", "Sales"), "the Sales table is not in the Model tree"
    key("Left")    # collapse it if it is open (a closed root has no parent: nothing happens)
    time.sleep(0.5)
    key("Right")   # open it
    time.sleep(1.0)
    # A column, not a measure: a measure opens its DAX editor and the keyboard goes with it. A column row has no tooltip, so its accessible name is its label.
    assert click_element("workbench", "treeitem", "AuditNote"), "the column AuditNote is not in the Model tree"
    time.sleep(0.6)
    if how == "menu":
        res = cdp("locate", "workbench", "treeitem", "AuditNote")
        ox, oy = origin(res["meta"]); dpr = res["meta"].get("dpr") or 1; n = res["node"]
        with ln as s:
            s.guard(); s.click(int(ox + (n["x"] + n["w"] / 2) * dpr), int(oy + (n["y"] + n["h"] / 2) * dpr), 3, 1)
        time.sleep(0.8)
        assert click_element("workbench", "menuitem", "Rename with Input Box"), "the context menu has no Rename with Input Box item"
    else:
        key("ctrl+shift+p")
        time.sleep(0.8)
        type_text("Rename with Input Box")
        time.sleep(1.0)
        key("Return")


def rename(how, new_name, during_prompt):
    start_rename(how)
    up = wait_for(lambda: shows("quickpick", PROMPT) or shows("workbench", PROMPT), 15)
    check(f"[{how}] the Rename command opens the input box (a real showInputBox)", bool(up))
    key("ctrl+a")
    type_text(new_name)
    if during_prompt:
        opened, text = mcp.call("open_model", path=b_path, discardUnsaved=True)
        check(f"[{how}] while the input box is open the assistant opens model B over MCP", opened, text[:200])
        time.sleep(2.0)   # the host learns of the swap: it now shows B
        shot = screenshot(f"{how}-prompt-open-b")
        if shot:
            print("  screenshot:", shot)
    key("Return")
    time.sleep(3)


try:
    if shows("studio", "Let Pro decide for this model?"):
        click_element("studio", "button", "Not now")   # the first-open permission card: not part of this drive
        time.sleep(1.0)
    ok, text = open_model(a_path)
    cols = column_names(mcp)
    check("A is open on the engine and has the column AuditNote", bool(cols) and "AuditNote" in cols, cols)

    for how in ("menu", "palette"):
        # the race: B opens while the input box is open
        rename(how, "AuditNoteRenamedOnA", during_prompt=True)
        told = shows("workbench", "another model")
        check(f"[{how}] the person is told the rename did not happen, in the engine's own sentence", told)
        shot = screenshot(f"{how}-refused")
        if shot:
            print("  screenshot:", shot)
        names = column_names(mcp) or set()
        check(f"[{how}] B (open now) still has its own column AuditNote, and no column the person typed for A", "AuditNote" in names and "AuditNoteRenamedOnA" not in names, names)
        on_disk = b_dir / Path(a_path).name / "definition" / "tables" / "Sales.tmdl"
        check(f"[{how}] B's file was not touched either", on_disk.exists() and "AuditNoteRenamedOnA" not in on_disk.read_text())
        # the control: A is the open model again, nothing opens during the prompt
        opened, text = open_model(a_path)
        check(f"[{how}] control: model A is opened again", opened, text[:200])
        rename(how, "AuditNoteRenamedOnA", during_prompt=False)
        names = column_names(mcp) or set()
        check(f"[{how}] control: with no other model opening, the same rename writes A", "AuditNoteRenamedOnA" in names and "AuditNote" not in names, names)
        # put A back as it was for the next way (the rename is undone by reopening the file: nothing was saved)
        opened, text = open_model(a_path)
finally:
    mcp.close()

failed = results.count(False)
print(f"\n{len(results) - failed} PASS, {failed} FAIL")
sys.exit(1 if failed else 0)
