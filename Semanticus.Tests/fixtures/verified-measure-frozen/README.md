# Frozen verified-measure v7

`verified-measure-v7.md` is a byte copy of `Semanticus.Engine/workflows/verified-measure.md` as it
stood before UX16 shortened the shipped file to four person-facing steps. It is a TEST FIXTURE and
must never be installed as a workflow.

It exists because the shipped four-step file no longer reaches three engine behaviours that were
measured, argued and paid for:

- a second hard equivalence gate after a performance rewrite (the old Step 7);
- witness-purity revalidation against model drift at a later step;
- the run alias and lineage receipt when the candidate measure is renamed after the first proof.

`VerifiedMeasureV7CounterfactualTests` replays the six measured v6 failures through this file, so
those behaviours keep a definition behind them. What that suite no longer proves is anything about
the SHIPPED file; the shipped file's own shape is asserted in `HardMeasureWorkflowTests`.

Do not edit this fixture to track the shipped file. If the shipped file should regain a behaviour,
change the shipped file and the assertions that read it.
