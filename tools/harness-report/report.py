#!/usr/bin/env python3
"""Offline harness-ergonomics report over the L0 experience log (.semanticus/experience.jsonl).

docs/harness-engineering.md §5 fallback for when no Semanticus engine is attached. Pure stdlib.
Prints the SAME tables as the engine `harness_report` op, with the SAME deterministic definitions
as Semanticus.Engine/HarnessReport.cs — keep the two in lock-step.

Usage:  python report.py <path-to-experience.jsonl> [--top N]

Envelope schema (verified against ExperienceTee, do not guess):
  common:   schemaVersion, when(ISO-8601), kind("change"|"activity"), sessionId, origin, modelFingerprint
  change:   revision, label, deltas[]            (an applied delta — always successful, no ok/error)
  activity: seq, op, label, target, ok(bool), error(str|null), rowCount, elapsedMs, result
Only activity records carry ok/error, so error-rate/retry/flail analysis is over activity records.
The envelope carries NO token counts -> tokens-per-outcome is NOT derivable from L0 v1.

Deterministic definitions (IDENTICAL to HarnessReport.cs):
  RETRY CLUSTER: a maximal run of >=2 activity records that share the same `op` and are ALL
    failures (ok is False), ADJACENT in the activity stream in file order (append-only JSONL is
    chronological). Change records do not break a cluster (they are filtered out first).
  FLAIL SITE: per op, failures-before-a-success. Walk the op's activity records in file order with a
    running failure counter; on a success add the counter to the total and reset; on a failure
    increment. Trailing failures that never reach a success are NOT counted (hard error, not flail).
  INTERVENTION: an agent-origin -> human-origin transition in the full record stream (file order).
  FLAIL RATE: failure activity records per 100 activity records.
  TASK SUCCESS RATE: ok activity records / activity records that carry `ok`.
  TOKENS PER OUTCOME: not derivable from L0 v1 (no token field in the envelope).
"""
import json
import sys
from collections import Counter, defaultdict

TOKENS_PER_OUTCOME = "not derivable from L0 v1 (envelope carries no token counts)"


def analyze(lines, top_n=10):
    records = []
    skipped = 0
    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            records.append(json.loads(line))
        except Exception:
            skipped += 1  # fail-soft: one corrupt line must not sink the report

    activity = [r for r in records if r.get("kind") == "activity" and r.get("op")]

    # Per-op counts + error rate (over records that carry `ok`).
    op_stats = {}
    for r in activity:
        s = op_stats.setdefault(r["op"], {"count": 0, "with_ok": 0, "failures": 0})
        s["count"] += 1
        if isinstance(r.get("ok"), bool):
            s["with_ok"] += 1
            if r["ok"] is False:
                s["failures"] += 1
    op_rows = []
    for op, s in op_stats.items():
        rate = 0.0 if s["with_ok"] == 0 else round(s["failures"] / s["with_ok"], 4)
        op_rows.append((op, s["count"], s["failures"], rate))
    op_rows.sort(key=lambda x: (-x[2], -x[1], x[0]))

    # Retry clusters: maximal runs of >=2 adjacent same-op failures in the activity stream.
    clusters = []
    i = 0
    n = len(activity)
    while i < n:
        a = activity[i]
        if a.get("ok") is False:
            j = i + 1
            while j < n and activity[j].get("ok") is False and activity[j].get("op") == a.get("op"):
                j += 1
            run = j - i
            if run >= 2:
                errs = []
                for x in activity[i:j]:
                    e = x.get("error")
                    if e and e not in errs:
                        errs.append(e)
                clusters.append((a.get("op"), run, a.get("when"), activity[j - 1].get("when"), errs))
                i = j
                continue
        i += 1
    clusters.sort(key=lambda c: (-c[1], c[0] or ""))

    # Error-message frequency (top N distinct error texts on failed activity records).
    err_counter = Counter(r["error"] for r in activity if r.get("ok") is False and r.get("error"))
    top_errors = sorted(err_counter.items(), key=lambda kv: (-kv[1], kv[0]))[:top_n]

    # Flail sites: failures-before-a-success, per op.
    by_op = defaultdict(list)
    for r in activity:
        by_op[r["op"]].append(r)
    flail = []
    for op, rs in by_op.items():
        running = 0
        total = 0
        for r in rs:
            if r.get("ok") is False:
                running += 1
            elif r.get("ok") is True:
                total += running
                running = 0
        if total > 0:
            flail.append((op, total))
    flail.sort(key=lambda f: (-f[1], f[0]))
    flail = flail[:top_n]

    # Interventions: agent -> human origin transitions in the full record stream (file order).
    interventions = 0
    for k in range(1, len(records)):
        if records[k].get("origin") == "human" and records[k - 1].get("origin") == "agent":
            interventions += 1

    # KPIs.
    with_ok = [r for r in activity if isinstance(r.get("ok"), bool)]
    ok_count = sum(1 for r in with_ok if r["ok"] is True)
    fail_count = sum(1 for r in activity if r.get("ok") is False)
    success_rate = None if not with_ok else round(ok_count / len(with_ok), 4)
    flail_rate = 0.0 if not activity else round(fail_count / len(activity) * 100.0, 2)

    return {
        "total_records": len(records),
        "activity_records": len(activity),
        "change_records": sum(1 for r in records if r.get("kind") == "change"),
        "skipped": skipped,
        "op_rows": op_rows,
        "clusters": clusters,
        "top_errors": top_errors,
        "flail": flail,
        "kpis": {
            "events": len(activity),
            "success_rate": success_rate,
            "tokens_per_outcome": TOKENS_PER_OUTCOME,
            "flail_rate_per_100": flail_rate,
            "interventions": interventions,
        },
    }


def _pct(x):
    return "n/a" if x is None else f"{x * 100:.1f}%"


def render(rep):
    out = []
    k = rep["kpis"]
    out.append("=== Harness report (docs/harness-engineering.md §5) ===")
    out.append(
        f"KPIs: success {_pct(k['success_rate'])} · tokens/outcome {k['tokens_per_outcome']} · "
        f"flail {k['flail_rate_per_100']}/100 · interventions {k['interventions']} "
        f"(over {k['events']} activity events)"
    )
    out.append(
        f"Records: {rep['total_records']} total ({rep['activity_records']} activity, "
        f"{rep['change_records']} change), {rep['skipped']} corrupt line(s) skipped."
    )
    if rep["total_records"] == 0:
        out.append("(Log is empty or unparseable — nothing to report.)")
        return "\n".join(out)

    out.append("\n-- Per-op counts + error rate --")
    out.append(f"{'op':<32}{'count':>7}{'fails':>7}{'err%':>8}")
    for op, count, fails, rate in rep["op_rows"]:
        out.append(f"{op:<32}{count:>7}{fails:>7}{rate * 100:>7.1f}%")

    out.append("\n-- Retry clusters (>=2 adjacent same-op failures) --")
    if not rep["clusters"]:
        out.append("(none)")
    for op, run, first, last, errs in rep["clusters"]:
        out.append(f"{op}: {run} consecutive failures  [{first} .. {last}]")
        for e in errs:
            out.append(f"    - {e}")

    out.append("\n-- Top error messages --")
    if not rep["top_errors"]:
        out.append("(none)")
    for err, cnt in rep["top_errors"]:
        out.append(f"{cnt:>5}x  {err}")

    out.append("\n-- Top flail sites (failures before a success) --")
    if not rep["flail"]:
        out.append("(none)")
    for op, total in rep["flail"]:
        out.append(f"{total:>5}  {op}")

    return "\n".join(out)


def main(argv):
    args = [a for a in argv[1:] if a != "--top"]
    top_n = 10
    if "--top" in argv[1:]:
        idx = argv.index("--top")
        try:
            top_n = int(argv[idx + 1])
            args = [a for a in args if a != argv[idx + 1]]
        except (IndexError, ValueError):
            print("--top requires an integer", file=sys.stderr)
            return 2
    if not args:
        print(__doc__)
        return 2
    path = args[0]
    try:
        with open(path, "r", encoding="utf-8") as f:
            lines = f.readlines()
    except FileNotFoundError:
        print(f"No experience log at {path} — nothing captured. Empty report.")
        return 0
    except OSError as e:
        print(f"Experience log present but unreadable ({e}) — skipped.")
        return 0
    print(render(analyze(lines, top_n)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
