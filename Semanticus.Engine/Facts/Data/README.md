# Model facts data

`gazetteer.json` is the small place list the model facts snapshot uses to work out how much of a text column
looks like countries or states. It is embedded in the engine and never leaves the computer.

## What is in it

- Countries: short names in common English use, plus a few everyday alternatives (for example "UK",
  "United States of America", "Holland").
- Regions: the names and postal abbreviations of the Australian states and territories, the United States
  states and the District of Columbia, and the Canadian provinces and territories.
- Postcode patterns for Australia, the United States, the United Kingdom, Canada and New Zealand are written as
  code in `Facts/ValueFacts.cs`, not as data.

## Cities

There is no city list. No small city list with a clean licence was available to bundle, so the snapshot always
reports the city share as null (unknown) rather than guessing. A later slice can add one if a suitable list is
found; GeoNames (CC BY 4.0) is the obvious candidate but its smallest useful extract is large and would need
an attribution entry in `THIRD-PARTY-NOTICES.md`.

## Licence

The list was compiled by the Semanticus project from public facts. Names of countries, states and provinces
and their postal abbreviations are facts, not creative works, and no third-party dataset or file was copied to
make it. It is licensed with the rest of Semanticus (see `LICENSE` at the repository root), so it needs no entry
in `third-party-manifest.json` or `THIRD-PARTY-NOTICES.md`.
