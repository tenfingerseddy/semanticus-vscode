# Held-out set for the format fill rule, verification V1i (m11/feedback-rules at 36c61aa5)

Written blind by Sol (Codex CLI 0.155.1, model gpt-6-sol, reasoning effort xhigh, the `~/.codex` account) in an empty
scratch folder, before the verifier read FormatFill.cs or the builder's tests. Sol was given only the harm and what the
product does (`writer-input-1.md`, unedited), never the rule or any code. Its transcript shows every command it ran was
inside its own folder (kept in the verifier's evidence folder, `v-rules-v1i-evidence/writer/`).

The engine's own TMDL loader (a parse check only: open the folder, list the measures and columns; no rule, fix or plan
ran) rejected Sol's first output on TMDL syntax alone: relationship columns written `Table[Column]`, a `description:`
property, and a calculation group with an M partition. Sol fixed only that syntax (`writer-input-2.md`, unedited).
`labels.json`, `changes.json` and `judge.py` are byte-identical between the two outputs. After the fix the loader opens
all five models: 144 measures and columns, exactly one per label, shipped formats as labelled, and `Time Calc` a
calculation group with two items. The first output is kept unedited in the evidence folder (`writer/output-1-before-syntax-fix`).

`set/` is Sol's folder as it stands after the fix, unedited: `models/` (five TMDL models), `labels.json` (144 labels),
`changes.json` (10 scripted model changes between suggestion and write), `judge.py` (Sol's judge; `--self-test` passes
1500 examples), `README.md` and `build.py` (Sol's generator).

Nothing has been run on these models. The run protocol is committed on its own before the one run.
