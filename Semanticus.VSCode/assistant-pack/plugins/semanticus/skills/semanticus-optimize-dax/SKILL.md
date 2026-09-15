---
name: semanticus-optimize-dax
description: Investigate and optimise DAX measures with Semanticus benchmarks, profiles and equivalence checks. Use when asked to make a measure faster or explain its query cost.
---

# Optimise a DAX measure

Read the expression with `get_dax` and relevant business context with `get_model_primer`.
Check `connection_status`: timing and equivalence need a live query connection to the intended
model. Reuse its account and endpoint. Without an endpoint, complete useful analysis and label any
candidate as unmeasured. Do not claim a speedup or a passed equivalence check.

Use `benchmark_dax` on a representative query for first-run and warm timings. It does not clear
the cache. Use `benchmark_dax_coldwarm` when cold runs are needed; clearing a shared server's
cache requires the tool's explicit confirmation. Keep baseline and candidate conditions comparable.

Use `profile_dax` to locate a bottleneck when tracing is available. Separate trace setup from query
time. Missing storage-engine events do not establish formula-engine-only work; report that limit.
For expression debugging, wrap selected sub-expressions in `EVALUATEANDLOG(expression, "label")`
and call `evaluate_and_log` with the instrumented query.

Write a candidate aimed at the measured bottleneck. Run `verify_dax_equivalence` with `exprA`,
`exprB` and `groupBy` fields from relevant dimension tables. Cover meaningful customer, product,
region, date or other contexts, totals and edge cases. Honour the user's chosen contexts.
A match covers the executed contexts only. Inspect mismatches before applying an optimisation.
For example, when those columns exist, `groupBy: ["'Customer'[Region]", "'Product'[Category]"]`
checks relevant cross-table contexts. Read the returned evidence state: a truncated, empty,
degraded or grand-total-only comparison is not a verified rewrite even when values match.

For a behaviour-preserving rewrite, apply after the requested checks pass or the user explicitly
accepts an unverified change. An intentional business-rule change needs expected-answer tests,
not equivalence to the old rule. Stage a reviewed rewrite with `add_plan_item`, `kind: set_dax`
and `verifyGroupBy`, or use the appropriate typed edit tool. Read the actual apply result.

An in-session edit does not change the deployed model used by live queries. Benchmark an inline
candidate in the same query, or re-measure after a requested publish. Do not benchmark an unchanged
server measure and credit the local rewrite. Use `save_model` for local work; publish within the
requested scope through `deploy_live`'s preview and commit flow. The VS Code view updates an edit at
once; your assistant sees it on its next call. Report timings and tested contexts.
