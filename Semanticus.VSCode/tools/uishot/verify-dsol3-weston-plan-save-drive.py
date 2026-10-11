#!/usr/bin/env python3
"""Verifier check of m11/drafts-sol round 3 (item 4, no false refusal in steady state): the Plan page's Save row in a REAL VS Code, on a REAL engine, ONE model, nothing else opens.

The Plan shows "Save the changes you have made" while the open model has unsaved changes, and its button saves the way Ctrl+S does (plan.tsx savesOnRow). The page posts
{ type: 'saveModel' } and the Studio relay runs saveCommand(): not a command (no registerPersonCommand scope) and not an 'rpc' message (no withModelIdentity), so the `save`
request names no model, and the round-3 strict rule refuses it while a model is open (test/verify-drafts-sol-3.test.mjs is the same path in a unit test).

  1  Model A opens (the lane's own MCP door). The person renames the column AuditNote through the Model tree's Rename with Input Box (palette): A has an unsaved change.
  2  The person opens Studio, finds the Plan's row "Save the changes you have made" and presses its Save button.
  3  Expected: A is saved (the TMDL file on disk has the new name) and no refusal is shown.
  4  Control: Semanticus: Save Model from the palette (a command, so a person's action on A) saves the same change.

Uses the helpers of dsol3-weston-rename-drive.py (the lane's own drive: CDP boxes, XTest input, the MCP door), read from its top half.
usage: python3 verify-dsol3-weston-plan-save-drive.py <lane>     (the lane is launched first with the VSIX of the branch and the northstar fixture)
Exit 0 only when every check passes.
"""
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
src = (HERE / "dsol3-weston-rename-drive.py").read_text()
exec(compile(src[: src.index("# ---- the drive")], "dsol3-weston-rename-drive.py (helpers)", "exec"))   # noqa: S102  (defines L, ln, cdp, click_element, key, type_text, wait_for, shows, screenshot, Mcp, column_names, check, results)

ws = LANE / "workspace"
a_path = next((str(p) for p in sorted(ws.iterdir()) if p.name.endswith(".SemanticModel")), None)
if not a_path:
    sys.exit("no .SemanticModel folder in the lane workspace")
sales = Path(a_path) / "definition" / "tables" / "Sales.tmdl"
NEW = "AuditNoteSavedFromPlan"
mcp = Mcp()


def palette(text, escape=True):
    if escape:
        key("Escape")
    key("ctrl+shift+p")
    time.sleep(0.8)
    type_text(text)
    time.sleep(1.0)
    key("Return")


def person_rename(new_name):
    key("Escape")
    assert click_element("workbench", "treeitem", "Sales"), "the Sales table is not in the Model tree"
    key("Left"); time.sleep(0.5); key("Right"); time.sleep(1.0)
    assert click_element("workbench", "treeitem", "AuditNote"), "the column AuditNote is not in the Model tree"
    time.sleep(0.6)
    palette("Rename with Input Box", escape=False)   # Escape on the tree would drop the row the command reads
    up = wait_for(lambda: shows("quickpick", 'Rename this column "AuditNote"') or shows("workbench", 'Rename this column "AuditNote"'), 15)
    check("the person's Rename opens its input box", bool(up))
    key("ctrl+a"); type_text(new_name); key("Return")
    time.sleep(3)


try:
    if shows("studio", "Let Pro decide for this model?"):
        click_element("studio", "button", "Not now")
        time.sleep(1.0)
    ok, text = mcp.call("open_model", path=a_path, discardUnsaved=True)
    time.sleep(3)
    cols = column_names(mcp)
    check("A is open and has the column AuditNote", ok and bool(cols) and "AuditNote" in cols, (text[:200], cols))

    # 1  one unsaved change on A, made by the person
    person_rename(NEW)
    cols = column_names(mcp) or set()
    check("the person's rename is in A (unsaved)", NEW in cols, cols)
    check("A's file on disk does not have it yet", sales.exists() and NEW not in sales.read_text())

    # 2  the Plan's Save row
    palette("Semanticus: Open Studio")
    row = wait_for(lambda: shows("studio", "Save the changes you have made"), 30, 1.0)
    check("the Plan shows the row 'Save the changes you have made'", bool(row))
    shot = screenshot("plan-save-row")
    if shot:
        print("  screenshot:", shot)
    pressed = click_element("studio", "button", "Save")
    check("the row's Save button is pressed", pressed)
    time.sleep(4)
    shot = screenshot("plan-save-after")
    if shot:
        print("  screenshot:", shot)
    refused = shows("workbench", "another model") or shows("studio", "Nothing was saved")
    check("no refusal is shown for a Save of the one open model", not refused,
          "the person read the refusal: " + ("'Save failed: This page was showing another model...'" if shows("workbench", "another model") else "") + (" 'Nothing was saved...'" if shows("studio", "Nothing was saved") else ""))
    check("the Plan's Save wrote A's change to disk", sales.exists() and NEW in sales.read_text())

    # 4  control: the palette's Save Model, a person's action on A
    key("Escape")
    palette("Semanticus: Save Model")
    time.sleep(4)
    check("control: Semanticus: Save Model (a command) writes the same change to disk", sales.exists() and NEW in sales.read_text())
finally:
    mcp.close()

failed = results.count(False)
print(f"\n{len(results) - failed} PASS, {failed} FAIL")
sys.exit(1 if failed else 0)
