#!/usr/bin/env python3
"""
token-lens — Semanticus MCP tool-surface token accountant.

docs/harness-engineering.md §2 ("Tool-surface economy"): every tool schema rides in
EVERY agent context at session init, and result payloads compound the tax. Before
pruning anything, MEASURE. This tool does the measurement — no pruning, no op changes.

WHAT IT MEASURES
  Schema mode (default): parses Semanticus.Engine/McpTools*.cs and, per
    [McpServerTool(Name=...)] method, extracts the tool name, its Description text,
    and each parameter's name + [Description] text. Computes per-op SCHEMA cost in
    chars and estimated tokens, ranks the ops, totals the whole surface (what every
    agent session pays at init), and clusters by name-prefix family.
  Results mode (--results): for the highest-traffic READ ops, extracts the result
    DTO type from the method return (Task<X>) and measures the DTO's FIELD COUNT +
    nesting DEPTH as a STATIC COMPLEXITY PROXY. This is NOT a real payload size (the
    engine must run for that); it is honestly labeled a proxy. See README for the
    three REAL payload sizes McpSmoke already logs.

TOKEN APPROXIMATION
  Estimated tokens = ceil(chars / 4). This is the standard rough English-text ratio
  (~4 chars/token). It is an APPROXIMATION, not a real tokenizer count — good enough
  to rank ops relative to each other and size the surface. Real BPE counts would run
  a few percent different per-op but would not change the ranking or the conclusions.

USAGE
  python tools/token-lens/lens.py            # ranked schema table + totals + families
  python tools/token-lens/lens.py --json     # machine-readable schema output
  python tools/token-lens/lens.py --results  # DTO complexity table for the read ops

Pure stdlib. Run from the repo root (or anywhere — paths are resolved relative to
this file's location: ../../Semanticus.Engine).
"""
import sys
import re
import os
import json
import math

# ---- paths -----------------------------------------------------------------
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
ENGINE = os.path.join(REPO, "Semanticus.Engine")
TOOL_FILES = [
    os.path.join(ENGINE, "McpTools.cs"),
    os.path.join(ENGINE, "McpTools.Harness.cs"),
]
# Source files that define result DTOs (searched, in order, for a type name).
DTO_SOURCES = [
    os.path.join(ENGINE, "Protocol.cs"),
    os.path.join(ENGINE, "McpTools.cs"),
    os.path.join(ENGINE, "HarnessReport.cs"),
    os.path.join(ENGINE, "Plan.cs"),
    os.path.join(ENGINE, "Spec.cs"),
    os.path.join(ENGINE, "Workflow.cs"),
    os.path.join(ENGINE, "Knowledge.cs"),
    os.path.join(REPO, "Semanticus.Analysis", "Readiness.cs"),
    os.path.join(REPO, "Semanticus.Analysis", "Bpa.cs"),
]

CHARS_PER_TOKEN = 4  # rough English ratio; est_tokens = ceil(chars / 4). See header.


def est_tokens(chars):
    return math.ceil(chars / CHARS_PER_TOKEN)


# ---- C# string-literal reader ----------------------------------------------
# Reads a regular C# string literal starting at text[i] == '"', returning
# (decoded_content, index_after_closing_quote). Handles \" and other escapes.
# Then folds any adjacent '+' string concatenation ("a" + "b") into one value.
def _read_string_literal(text, i):
    assert text[i] == '"'
    i += 1
    out = []
    n = len(text)
    while i < n:
        c = text[i]
        if c == "\\":
            # keep the escape's logical char; for length purposes the decoded
            # form is what an agent's context actually carries (\" -> ", \\ -> \).
            if i + 1 < n:
                nxt = text[i + 1]
                out.append(nxt if nxt in '"\\/' else nxt)
                i += 2
                continue
            out.append(c)
            i += 1
        elif c == '"':
            i += 1
            return "".join(out), i
        else:
            out.append(c)
            i += 1
    return "".join(out), i  # unterminated (shouldn't happen in valid source)


def _read_concatenated_string(text, i):
    """Read a string literal at i and fold trailing '+ "..."' concatenations."""
    value, i = _read_string_literal(text, i)
    n = len(text)
    while True:
        j = i
        while j < n and text[j] in " \t\r\n":
            j += 1
        if j < n and text[j] == "+":
            j += 1
            while j < n and text[j] in " \t\r\n":
                j += 1
            if j < n and text[j] == '"':
                more, i = _read_string_literal(text, j)
                value += more
                continue
        break
    return value, i


# ---- op parser -------------------------------------------------------------
TOOL_RE = re.compile(r"McpServerTool\s*\(\s*Name\s*=\s*\"((?:[^\"\\]|\\.)*)\"\s*\)")
DESC_RE = re.compile(r"Description\s*\(")
# a param declaration tail after a [Description(...)]: ...)] <type...> <name> [,)=]
PARAM_NAME_RE = re.compile(
    r"\]\s*(?:params\s+)?[A-Za-z_][\w\.\<\>\[\]\?,\s]*?\b([A-Za-z_]\w*)\s*(?=[,)=])"
)
RETURN_RE = re.compile(r"\bTask\s*<")


def _match_angle(text, start):
    """text[start] == '<'; return index just after the matching '>'."""
    depth = 0
    i = start
    n = len(text)
    while i < n:
        if text[i] == "<":
            depth += 1
        elif text[i] == ">":
            depth -= 1
            if depth == 0:
                return i + 1
        i += 1
    return n


def parse_ops(text):
    """Yield one dict per [McpServerTool] method found in `text`."""
    ops = []
    starts = [m.start() for m in TOOL_RE.finditer(text)]
    for idx, start in enumerate(starts):
        end = starts[idx + 1] if idx + 1 < len(starts) else len(text)
        seg = text[start:end]
        m = TOOL_RE.search(seg)
        name = m.group(1)

        # Tool description = the FIRST Description(...) after the McpServerTool attr.
        tool_desc = ""
        dm = DESC_RE.search(seg, m.end())
        first_desc_end = None
        if dm:
            q = seg.index('"', dm.end() - 1)
            tool_desc, first_desc_end = _read_concatenated_string(seg, q)

        # Return DTO type: the Task<X> in the method signature.
        return_type = None
        rm = RETURN_RE.search(seg, first_desc_end or m.end())
        if rm:
            lt = seg.index("<", rm.start())
            inner = seg[lt + 1 : _match_angle(seg, lt) - 1].strip()
            return_type = inner

        # Parameters: every Description(...) AFTER the first (tool) one is a param.
        params = []
        pos = first_desc_end if first_desc_end is not None else m.end()
        while True:
            pm = DESC_RE.search(seg, pos)
            if not pm:
                break
            q = seg.index('"', pm.end() - 1)
            pdesc, after = _read_concatenated_string(seg, q)
            # find the "])" then the param type+name that follows
            close = seg.find("]", after)
            pname = None
            if close != -1:
                nm = PARAM_NAME_RE.match(seg, close)
                if nm:
                    pname = nm.group(1)
            params.append({"name": pname or "?", "desc": pdesc})
            pos = after

        ops.append(
            {
                "name": name,
                "description": tool_desc,
                "return_type": return_type,
                "params": params,
            }
        )
    return ops


def compute_schema_cost(op):
    """chars = tool description + each param's (name + description)."""
    chars = len(op["description"])
    for p in op["params"]:
        chars += len(p["name"]) + len(p["desc"])
    return chars


def load_ops():
    ops = []
    for path in TOOL_FILES:
        with open(path, "r", encoding="utf-8") as f:
            ops.extend(parse_ops(f.read()))
    return ops


# ---- parser self-validation (must equal grep -c "McpServerTool(Name") -------
def grep_count():
    total = 0
    pat = re.compile(r"McpServerTool\(Name")
    for path in TOOL_FILES:
        with open(path, "r", encoding="utf-8") as f:
            total += len(pat.findall(f.read()))
    return total


def validate(ops):
    expected = grep_count()
    found = len(ops)
    if found != expected:
        sys.stderr.write(
            "PARSER MISMATCH: parsed {} ops but grep counts {} "
            'McpServerTool(Name occurrences. The parser is wrong — fix it.\n'.format(
                found, expected
            )
        )
        sys.exit(2)
    return expected


FAMILY_PREFIXES = [
    "set_", "get_", "list_", "create_", "delete_", "update_", "run_",
    "daxlib_", "fabric_git_", "git_", "ai_readiness_", "bpa_", "deploy_",
    "connect_", "save_", "load_", "apply_", "open_", "model_", "capture_",
    "compare_", "benchmark_", "profile_", "analyze_", "preview_",
]


def family_of(name):
    # longest matching prefix wins (fabric_git_ before git_)
    best = None
    for p in sorted(FAMILY_PREFIXES, key=len, reverse=True):
        if name.startswith(p):
            best = p
            break
    if best:
        return best.rstrip("_")
    # fall back to the leading token before the first '_'
    return name.split("_", 1)[0] if "_" in name else name


# ---- schema report ---------------------------------------------------------
def report_schema(ops):
    rows = []
    for op in ops:
        c = compute_schema_cost(op)
        rows.append((op["name"], c, est_tokens(c), len(op["params"])))
    rows.sort(key=lambda r: r[1], reverse=True)

    total_chars = sum(r[1] for r in rows)
    total_tokens = est_tokens(total_chars)

    print("=" * 78)
    print("SEMANTICUS MCP TOOL-SURFACE SCHEMA COST  (docs/harness-engineering.md §2)")
    print("est_tokens = ceil(chars / {}) — approximation, see header".format(CHARS_PER_TOKEN))
    print("=" * 78)
    print()
    print("{:<34} {:>8} {:>8} {:>7}".format("op", "chars", "~tokens", "params"))
    print("-" * 60)
    for name, c, t, np in rows:
        print("{:<34} {:>8} {:>8} {:>7}".format(name, c, t, np))
    print("-" * 60)
    print("{:<34} {:>8} {:>8} {:>7}".format("TOTAL (" + str(len(rows)) + " ops)", total_chars, total_tokens, ""))
    print()
    print(">> WHOLE-SURFACE SCHEMA TAX paid at EVERY session init:")
    print("   {:,} chars  ~  {:,} est. tokens".format(total_chars, total_tokens))
    print("   (raw description + param-name + param-description text only;")
    print("    excludes JSON-schema envelope/type keywords the SDK adds per op.)")
    print()

    # families
    fam = {}
    for op in ops:
        f = family_of(op["name"])
        c = compute_schema_cost(op)
        d = fam.setdefault(f, {"ops": 0, "chars": 0})
        d["ops"] += 1
        d["chars"] += c
    fam_rows = sorted(fam.items(), key=lambda kv: kv[1]["chars"], reverse=True)
    print("=" * 78)
    print("FAMILY CLUSTERING (by name prefix) — per-family schema subtotal")
    print("=" * 78)
    print("{:<18} {:>5} {:>9} {:>9}".format("family", "ops", "chars", "~tokens"))
    print("-" * 44)
    for f, d in fam_rows:
        print("{:<18} {:>5} {:>9} {:>9}".format(f, d["ops"], d["chars"], est_tokens(d["chars"])))
    print()


def json_schema(ops):
    total_chars = sum(compute_schema_cost(op) for op in ops)
    fam = {}
    for op in ops:
        f = family_of(op["name"])
        d = fam.setdefault(f, {"ops": 0, "chars": 0})
        d["ops"] += 1
        d["chars"] += compute_schema_cost(op)
    out = {
        "approximation": "est_tokens = ceil(chars/{})".format(CHARS_PER_TOKEN),
        "op_count": len(ops),
        "total_schema_chars": total_chars,
        "total_schema_est_tokens": est_tokens(total_chars),
        "ops": [
            {
                "name": op["name"],
                "family": family_of(op["name"]),
                "return_type": op["return_type"],
                "param_count": len(op["params"]),
                "schema_chars": compute_schema_cost(op),
                "schema_est_tokens": est_tokens(compute_schema_cost(op)),
                "description_chars": len(op["description"]),
                "params": [
                    {"name": p["name"], "desc_chars": len(p["desc"])}
                    for p in op["params"]
                ],
            }
            for op in sorted(ops, key=lambda o: compute_schema_cost(o), reverse=True)
        ],
        "families": [
            {
                "family": f,
                "ops": d["ops"],
                "schema_chars": d["chars"],
                "schema_est_tokens": est_tokens(d["chars"]),
            }
            for f, d in sorted(fam.items(), key=lambda kv: kv[1]["chars"], reverse=True)
        ],
    }
    print(json.dumps(out, indent=2))


# ---- results mode: DTO complexity proxy ------------------------------------
# The highest-traffic READ ops (hardcoded per the task brief).
READ_OPS = [
    "get_model_summary", "ai_readiness_scan", "ai_readiness_summary",
    "bpa_scan", "bpa_summary", "get_model_graph", "model_graph_summary",
    "list_measures", "list_columns", "list_objects", "get_grounding",
    "list_workflows", "get_workflow", "list_calendars", "harness_report",
]

# Map an op's declared return type (Task<X>) to the bare DTO type name, stripping
# arrays and one level of collection generics (List<T>/IReadOnlyList<T>/...).
def bare_type(t):
    if not t:
        return None
    t = t.strip()
    t = re.sub(r"\[\s*\]$", "", t)  # X[]
    m = re.match(r"^[\w\.]*(?:List|IList|IReadOnlyList|IEnumerable|ICollection|Collection)\s*<(.+)>$", t)
    if m:
        return bare_type(m.group(1))
    # strip namespace qualifiers (Semanticus.Engine.Foo -> Foo)
    t = t.split(".")[-1]
    t = t.rstrip("?")
    return t


PRIMITIVES = {
    "string", "int", "long", "bool", "double", "float", "decimal", "object",
    "DateTime", "DateTimeOffset", "Guid", "byte", "short", "uint", "ulong",
    "TimeSpan", "int?", "long?", "bool?", "double?", "char", "String", "Int32",
    "Int64", "Boolean", "Double",
}


def load_dto_text():
    """Concatenate all DTO source files into one blob for type lookups."""
    blobs = []
    for p in DTO_SOURCES:
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8") as f:
                blobs.append(f.read())
    return "\n".join(blobs)


PROP_RE = re.compile(
    r"public\s+(?:virtual\s+|sealed\s+|override\s+)?([\w\.\<\>\[\]\?,\s]+?)\s+([A-Za-z_]\w*)\s*(?:\{\s*get\b|=>)"
)


def find_type_body(blob, type_name):
    """Return the {...} body text of a class/record/struct named type_name, or None."""
    m = re.search(
        r"\b(?:class|record|struct)\s+" + re.escape(type_name) + r"\b[^{;]*", blob
    )
    if not m:
        return None
    # positional record?  record Foo(...);  -> body is the parameter list
    tail = blob[m.end():]
    # skip base list up to '{' or '(' or ';'
    i = 0
    while i < len(tail) and tail[i] in " \t\r\n:":
        i += 1
    if i < len(tail) and tail[i] == "(":
        # positional record — treat the ctor params as properties
        depth = 0
        j = i
        while j < len(tail):
            if tail[j] == "(":
                depth += 1
            elif tail[j] == ")":
                depth -= 1
                if depth == 0:
                    return ("POSITIONAL", tail[i + 1 : j])
            j += 1
        return None
    # brace body
    br = tail.find("{")
    if br == -1:
        return None
    depth = 0
    j = br
    while j < len(tail):
        if tail[j] == "{":
            depth += 1
        elif tail[j] == "}":
            depth -= 1
            if depth == 0:
                return ("BODY", tail[br + 1 : j])
        j += 1
    return None


def dto_props(blob, type_name):
    """Return list of (prop_type, prop_name) for a DTO, or None if not found."""
    found = find_type_body(blob, type_name)
    if not found:
        return None
    kind, body = found
    if kind == "POSITIONAL":
        props = []
        for part in re.split(r",(?![^<>]*>)", body):
            part = part.strip()
            if not part:
                continue
            toks = part.replace("=", " ").split()
            if len(toks) >= 2:
                props.append((toks[-2], toks[-1]))
        return props
    props = []
    for m in PROP_RE.finditer(body):
        props.append((m.group(1).strip(), m.group(2)))
    return props


def analyze_dto(blob, type_name, seen=None, depth=1):
    """Return (field_count, max_depth) — a static complexity proxy for the DTO tree."""
    if seen is None:
        seen = set()
    props = dto_props(blob, type_name)
    if props is None:
        return (None, depth)  # not a DTO we can resolve (primitive/opaque)
    if type_name in seen:
        return (len(props), depth)
    seen = seen | {type_name}
    field_count = len(props)
    max_depth = depth
    for ptype, _ in props:
        child = bare_type(ptype)
        if child and child not in PRIMITIVES and re.match(r"^[A-Za-z_]\w*$", child):
            sub = dto_props(blob, child)
            if sub is not None:
                cf, cd = analyze_dto(blob, child, seen, depth + 1)
                if cf:
                    field_count += cf
                if cd > max_depth:
                    max_depth = cd
    return (field_count, max_depth)


def report_results(ops):
    by_name = {op["name"]: op for op in ops}
    blob = load_dto_text()
    print("=" * 78)
    print("RESULT DTO COMPLEXITY PROXY  (docs/harness-engineering.md §2)")
    print("=" * 78)
    print("STATIC PROXY ONLY — field count + nesting depth of the return DTO tree.")
    print("This is NOT a real payload size: actual bytes scale with the model (row")
    print("counts, finding counts, DAX bodies) and need the engine running. McpSmoke")
    print("already logs three REAL summary/full payload sizes: see")
    print("Semanticus.McpSmoke/Program.cs '[lens] payload bytes (summary/full)'.")
    print()
    print("{:<24} {:<22} {:>6} {:>6}".format("read op", "result DTO", "fields", "depth"))
    print("-" * 62)
    for name in READ_OPS:
        op = by_name.get(name)
        if not op:
            print("{:<24} {:<22} {:>6} {:>6}".format(name, "(op not found)", "-", "-"))
            continue
        dto = bare_type(op["return_type"])
        arr = "[]" if op["return_type"] and op["return_type"].strip().endswith("[]") else ""
        fc, dp = analyze_dto(blob, dto) if dto else (None, 0)
        label = (dto or "?") + arr
        fcs = str(fc) if fc is not None else "n/a"
        print("{:<24} {:<22} {:>6} {:>6}".format(name, label, fcs, dp))
    print("-" * 62)
    print()
    print("Notes: 'fields' sums the DTO's own + nested-DTO properties (a structural")
    print("proxy for how much shape an agent must parse); 'depth' is max nesting.")
    print("A '[]' DTO returns a LIST — real cost multiplies fields by row count, so")
    print("list ops are the true payload risk regardless of a modest field count.")


def main():
    args = set(sys.argv[1:])
    ops = load_ops()
    validate(ops)  # exits non-zero on parser/grep mismatch

    if "--json" in args:
        json_schema(ops)
    elif "--results" in args:
        report_results(ops)
    else:
        report_schema(ops)


if __name__ == "__main__":
    main()
