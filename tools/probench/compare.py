#!/usr/bin/env python3
"""ProBench independent comparator. Pure stdlib; shares ZERO code with the engine's
verify_dax_equivalence (the credibility requirement from docs/verified-edits-plan.md).

Reads gold/<task>.json + runs/<arm>/<task>.scored.json and emits results/summary.json + summary.md.

Cell semantics (pre-registered): BLANK, ERROR and numeric values are three DISTINCT worlds.
  gold BLANK  -> candidate must be BLANK
  gold ERROR  -> a reference should never error; such cells are excluded and flagged loudly
  gold VALUE  -> candidate must be VALUE with |a-b| <= max(abs_tol, rel_tol*|gold|)
pass@1.0 = every comparable cell matches. Perf/style are only comparable among pass@1.0 candidates
and live outside this script.
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).parent
ABS_TOL = 1e-6
REL_TOL = 1e-9

TIER_WEIGHT = {"basic": 1, "intermediate": 2, "advanced": 3}


def cell_matches(gold, cand):
    gk, ck = gold["kind"], cand["kind"]
    if gk == "BLANK":
        return ck == "BLANK"
    if gk == "VALUE":
        if ck != "VALUE":
            return False
        try:
            a, b = float(gold["value"]), float(cand["value"])
            return abs(a - b) <= max(ABS_TOL, REL_TOL * abs(a))
        except (TypeError, ValueError):
            return str(gold["value"]) == str(cand["value"])
    return False  # gold ERROR is excluded upstream


def main():
    tasks = {t["id"]: t for t in json.loads((HERE / "tasks.json").read_text(encoding="utf-8"))["tasks"]}
    arms = sorted(p.name for p in (HERE / "runs").iterdir() if p.is_dir()) if (HERE / "runs").exists() else []
    if not arms:
        sys.exit("no runs/<arm>/ directories found")

    results = {}
    flagged_gold_errors = []
    for arm in arms:
        per_task = {}
        for scored_file in sorted((HERE / "runs" / arm).glob("*.scored.json")):
            scored = json.loads(scored_file.read_text(encoding="utf-8"))
            tid = scored["task"]
            gold = json.loads((HERE / "gold" / f"{tid}.json").read_text(encoding="utf-8"))
            gold_cells = gold["cells"]
            cand_cells = scored["cells"]
            comparable, matched = 0, 0
            for g, c in zip(gold_cells, cand_cells):
                if g["gold"]["kind"] == "ERROR":
                    flagged_gold_errors.append((tid, g["filters"]))
                    continue
                comparable += 1
                if cell_matches(g["gold"], c["candidate"]):
                    matched += 1
            per_task[tid] = {
                "tier": tasks[tid]["tier"],
                "failureMode": tasks[tid]["failureMode"],
                "cells": comparable,
                "matched": matched,
                "cellAccuracy": round(matched / comparable, 4) if comparable else None,
                "pass": comparable > 0 and matched == comparable,
                "attempts": scored.get("attempts"),
            }
        results[arm] = per_task

    # rollups
    summary = {"arms": {}, "headline": None, "goldErrorCells": len(flagged_gold_errors)}
    for arm, per_task in results.items():
        n = len(per_task)
        passed = sum(1 for r in per_task.values() if r["pass"])
        wsum = sum(TIER_WEIGHT[r["tier"]] for r in per_task.values())
        wpass = sum(TIER_WEIGHT[r["tier"]] for r in per_task.values() if r["pass"])
        by_tier = {}
        for tier in ("basic", "intermediate", "advanced"):
            tr = [r for r in per_task.values() if r["tier"] == tier]
            if tr:
                by_tier[tier] = {"tasks": len(tr), "passed": sum(1 for r in tr if r["pass"])}
        by_mode = {}
        for r in per_task.values():
            m = by_mode.setdefault(r["failureMode"], {"tasks": 0, "passed": 0})
            m["tasks"] += 1
            m["passed"] += 1 if r["pass"] else 0
        summary["arms"][arm] = {
            "tasks": n, "passed": passed,
            "passRate": round(passed / n, 4) if n else None,
            "weightedPassRate": round(wpass / wsum, 4) if wsum else None,
            "meanCellAccuracy": round(sum(r["cellAccuracy"] or 0 for r in per_task.values()) / n, 4) if n else None,
            "byTier": by_tier, "byFailureMode": by_mode,
        }
    if "pro" in summary["arms"] and "freeplus" in summary["arms"]:
        summary["headline"] = {
            "metric": "PRO minus FREE+ (weighted pass rate)",
            "value": round(summary["arms"]["pro"]["weightedPassRate"] - summary["arms"]["freeplus"]["weightedPassRate"], 4),
        }

    out = HERE / "results"
    out.mkdir(exist_ok=True)
    (out / "summary.json").write_text(json.dumps({"summary": summary, "perTask": results}, indent=1), encoding="utf-8")

    lines = ["# ProBench results", ""]
    if summary["headline"]:
        lines += [f"**Headline: {summary['headline']['metric']} = {summary['headline']['value']:+.1%}**", ""]
    lines += ["| Arm | Tasks | pass@1.0 | Weighted | Mean cell accuracy |", "|---|---|---|---|---|"]
    for arm, s in summary["arms"].items():
        lines.append(f"| {arm} | {s['tasks']} | {s['passed']}/{s['tasks']} ({s['passRate']:.0%}) | {s['weightedPassRate']:.0%} | {s['meanCellAccuracy']:.1%} |")
    lines += ["", "## By tier"]
    for arm, s in summary["arms"].items():
        for tier, tr in s["byTier"].items():
            lines.append(f"- {arm} / {tier}: {tr['passed']}/{tr['tasks']}")
    lines += ["", "## By failure mode"]
    for arm, s in summary["arms"].items():
        for mode, mr in s["byFailureMode"].items():
            lines.append(f"- {arm} / {mode}: {mr['passed']}/{mr['tasks']}")
    if flagged_gold_errors:
        lines += ["", f"⚠ {len(flagged_gold_errors)} gold cells ERRORED and were excluded — the reference DAX needs review:"]
        for tid, filters in flagged_gold_errors[:10]:
            lines.append(f"- {tid}: {json.dumps(filters)}")
    (out / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
