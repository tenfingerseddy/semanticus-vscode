# DAX corpus bar (A2 §12.4)

The local, reproducible version of the A2 §12.4 corpus bar. It parses every harvested model expression and
reports round-trip, clean-parse rate, and the whitelist.

```bash
dotnet run -c Release --project tools/dax-corpus-bar/Semanticus.DaxCorpusBar
```

Exit code 0 means the bar passed on a complete corpus. A partial snapshot exits 1.

## This is not a CI gate, on purpose

The corpus is gitignored working data, so CI would measure a different corpus and quietly report a
different rate. The project is deliberately outside `Semanticus.sln`. Run it locally and paste the output
into the PR.

## Reproducing the full corpus

Both halves have to be present, and neither is committed:

```bash
git submodule update --init --recursive external/TabularEditor
# plus a local link or clone at the gitignored tools/readiness-corpus/work/
```

A run reporting fewer than **3,555 / 1,016 / 2,539** expressions is a partial snapshot. The tool prints a
PARTIAL warning, treats that as a failure, and exits 1. Its rates must not be quoted as corpus-wide.

## What it checks

| Item | Rule |
|---|---|
| §12.4 item 1 | 100% exact round-trip, ordinally. No whitelist, no exceptions. |
| §12.4 item 2 | Clean parse >= 99.9%, with the whitelisted artifacts excluded from the rate. |
| §12.4 item 3 | The whitelist is exactly the 4 known harvester artifacts, matched **by identity** (file, kind, text prefix) rather than by count, so a *different* non-clean expression fails the bar even while the total stays at four. |
| §12.4 item 4 | Partition counts are printed on every run, because the corpus is a live junction and drifts. |

"Clean" means no missing token, no skipped-token trivia, and no `DAXP` diagnostic.

The harvester is **linked** from `tools/dax-spike/Semanticus.DaxSpike/Corpus.cs`, not copied, so the bar and
the A2 §6.1 measurement read exactly the same expressions.

## Measured 2026-07-28

```
expressions total    3555
from .bim            1016
from .tmdl           2539
vendored TabularEditor 288
readiness corpus     3267
tokens               98501
round-trip failures  0
clean parse (raw)    3551/3555 = 99.887%
clean parse (scored) 3551/3551 = 100.000%
non-clean            4   (4 whitelisted, 0 unexplained)
```
