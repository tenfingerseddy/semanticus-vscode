# Which license governs `Semanticus.Dax`

**Today, in this repository: the Elastic License 2.0.** This project has no separate license of its own.
It is part of the Semanticus repository and is covered by the root [`LICENSE`](../LICENSE), the Elastic
License 2.0, ratified in PR #149. No other license is offered for this code by this repository.

**Planned, at standalone publication: MIT.** Kane ratified on **2026-07-23** that the DAX *syntax* layer
is republished as a standalone repository and NuGet package under the MIT license at the end of Phase A
(the decision is `docs/tomwrapper-replacement-feasibility.md` §12, "The open-source play"; the release
lane is `[K-A8]` in the task register). The scope line is: syntax is a gift, semantics is the product.

**Why this file exists.** The planning documents call this project "the MIT package", and they are correct
about the intent. They are describing the future publication target, not the license you receive today. A
reader who lands on this directory could reasonably read "the MIT package" as a present grant, and it is
not one. The MIT grant takes effect only when the standalone package is actually published; until then
this code is Elastic License 2.0 like the rest of the repository.

`Semanticus.VSCode/test/attribution-and-license.test.mjs` fails if this note goes missing or stops naming
both licenses.
