# token-lens — MCP tool-surface token accountant

Implements the **§2 TOKEN LENS** from [`docs/harness-engineering.md`](../../docs/harness-engineering.md):

> "Token lens first (P-Efficiency 5): measure per-op schema cost + median result
> cost **before pruning anything. Data over taste.**"

This tool is **measurement only** — it prunes nothing, consolidates nothing, and
changes no ops. It parses the C# tool surface and reports where the schema tokens go,
so a later consolidation pass is driven by data rather than taste.

## Run it

```bash
python tools/token-lens/lens.py            # ranked schema table + surface total + families
python tools/token-lens/lens.py --json     # machine-readable schema output (for tooling/trends)
python tools/token-lens/lens.py --results  # result-DTO complexity proxy for the read ops
```

Pure stdlib Python 3 — no dependencies. Paths resolve relative to the script, so it
runs from anywhere in the repo.

### Self-validation (the verification)

There is no C# test for this Python tool. Instead the script **self-validates**: it
counts the ops it parsed and compares against `grep -c "McpServerTool(Name"` over the
same files. On any mismatch it prints `PARSER MISMATCH` to stderr and **exits 2**. A
clean `exit 0` from `python tools/token-lens/lens.py` *is* the passing test.

- Current run: **231 ops** parsed == `grep -c` (230 in `McpTools.cs` + 1 in
  `McpTools.Harness.cs`). Match exact.

## What it measures & the approximations (read this)

**Schema cost** per op = characters of the tool `Description` text + each parameter's
`name` + each parameter's `[Description]` text. This is the raw human-readable text an
agent's context carries per tool.

- **`est_tokens = ceil(chars / 4)`** — the standard rough English ratio (~4 chars per
  token). It is an **approximation**, not a real BPE tokenizer count. It is accurate
  enough to *rank* ops against each other and to *size* the surface; a real tokenizer
  would differ a few percent per op but would not move the ranking or the conclusions.
- The surface total **excludes** the JSON-Schema envelope the MCP SDK emits per op
  (type keywords, `required`, enum arrays, property wrappers). So the real init tax is
  **higher** than the number here — this is a floor, not a ceiling.

**Result cost** (`--results`) is a **static complexity proxy only**: for the
highest-traffic read ops it resolves the return type (`Task<X>`), then counts the
result DTO's field count + nesting depth (recursing into nested DTOs). It is **not** a
real payload size — actual bytes scale with the model (row counts, finding counts, DAX
bodies) and require the engine running. Honest label, honestly limited.

- **Real payload sizes already exist**: `Semanticus.McpSmoke/Program.cs` logs three
  real summary-vs-full byte counts against a live AdventureWorks session —
  `[lens] payload bytes (summary/full): readiness … bpa … graph …` (search that
  string). Run `dotnet run --project Semanticus.McpSmoke` for the real numbers; the
  DTO proxy here is the offline stand-in that needs no engine.

---

## Top-10 findings from the actual run (2026-07-04)

1. **The whole surface costs ~27,120 est. tokens (108,480 chars) at *every* session
   init** — before a single model is read. Across 231 ops that averages ~470 chars /
   ~117 tokens per op of pure description text (SDK schema envelope is on top).
2. **`set_` is the heaviest family: 29 ops, ~3,266 tokens (12% of the whole surface).**
3. **`get_` is second: 29 ops, ~2,514 tokens.** `set_` + `get_` together = ~21% of the
   entire schema tax.
4. **`create_` third: 19 ops, ~1,817 tokens**; `list_` fourth: 19 ops, ~1,533 tokens.
5. **Heaviest single op: `deploy_live` at 1,951 chars / ~488 tokens** (6 params, each
   heavily documented) — one op is ~1.8% of the surface.
6. Rest of the top-5 heaviest ops: `dry_run` (1,641/411), `optimize_measure`
   (1,495/374), `set_incremental_refresh_policy` (1,453/364, 9 params),
   `define_calendar_from_template` (1,349/338).
7. **The calendar cluster is a stealth cost**: 7 ops (`define_calendar`,
   `define_calendar_from_template`, `tag_calendar_column`, `generate_date_table`,
   `generate_time_intelligence`, `mark_date_table`, `list_calendars`) total ~1,272
   tokens — as much as the entire `create_` family's top third.
8. **15 single-property setters cost ~1,204 tokens** yet a generic `set_property`
   (which *already exists*, at just 327 chars / ~82 tokens) can express most of them.
9. **9 `git_*` passthroughs cost ~572 tokens** for what is essentially one CLI with
   subcommands.
10. **The summary/full read pairs are already the *right* pattern** — `ai_readiness_summary`
    vs `ai_readiness_scan`, `bpa_summary` vs `bpa_scan`, `model_graph_summary` vs
    `get_model_graph`. The `--results` proxy confirms the "summary" DTOs are materially
    smaller (e.g. `ModelGraphSummary` 10 fields/depth-1 vs `ModelGraph` 20/depth-2).
    This is the composite-read discipline to *extend*, not to cut.

### Result-DTO complexity (proxy) highlights

- `get_model_summary` returns the widest DTO (**71 fields, depth 3**) — expected: it is
  the blessed orientation primer that folds many sections into one round-trip. It is
  budgeted to ≤~2k tokens by design; the field count is the cost of doing the map in one
  call (a good trade).
- List ops (`list_measures`, `list_columns`, `list_objects`, `list_workflows`) have
  modest field counts (4–15) but return **arrays** — real payload = fields × row count,
  so these are the true payload risk on a large model regardless of the small per-row
  shape. Prefer the summary/count-first pattern before the full list.

---

## Consolidation candidates (PROPOSED — not done)

These are **recommendations for Kane**, per §2's named criteria
("single-property setter families → `set_property`; list/get families → composite
reads"). **Nothing here has been changed.** Savings are est. tokens off the
whole-surface init tax; all assume **deprecate-don't-break** (keep old names as thin
aliases until a major version, so the *savings* land only when the aliases are finally
dropped).

| # | Candidate | Today | Proposal | Est. saving |
|---|---|---|---|---|
| 1 | **Single-property setters → existing `set_property`** | 15 ops, ~1,204 tokens | Route through the already-present `set_property(ref, property, value)` (~82 tokens); keep typed setters as aliases | **~1,120 tokens** when aliases retire |
| 2 | **`git_*` passthroughs → one `git` op** | 9 ops, ~572 tokens | `git(subcommand, args)` — one documented op (~120 tokens) | **~450 tokens** |
| 3 | **Calendar cluster tightening** | 7 ops, ~1,272 tokens | Merge `define_calendar_from_template` into `define_calendar` (template = optional arg); it is the 2nd-heaviest calendar op at 338 tokens alone | **~300–400 tokens** |
| 4 | ~~**`get_dependencies` + `get_dependents` → composite**~~ **DONE 2026-07-04** | 2 ops → 1 | Shipped as `get_dependencies(ref, direction)` (direction: dependsOn \| dependents; unknown direction refuses with a teaching error) | realized |
| 5 | ~~**Deploy family review**~~ **DONE 2026-07-04** | prose diet | deploy_live/deploy_stage/cicd_publish/cicd_generate descriptions dieted — every semantic kept (dry-run defaults, human-only production gates, no-delete/no-refresh contracts), repetition cut | realized |

**Caveats for whoever executes this** (do NOT skip):
- Candidate 1's typed setters carry *validation and enum typing* (e.g. cardinality,
  crossfilter, summarize-by) that a stringly-typed `set_property` loses; verify per-op
  that the generic path validates as strictly before aliasing. Some may deserve to stay.
- Every proposal is **deprecate-don't-break**: the token saving is realized only at the
  major-version alias removal, not at the merge. The interim cost is *slightly higher*
  (op + alias both documented) — plan the sunset.
- The summary/full read pairs (finding #10) are the pattern to **keep and extend**, not
  a consolidation target.

Total addressable by candidates 1–4 (at alias-sunset): **~1,700–1,900 est. tokens
(~6–7% of the surface)**, plus prose diets on the heavy deploy/refresh ops. Modest per
op, but it is a tax paid on *every* session — it compounds across every agent turn.
