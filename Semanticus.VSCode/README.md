# Semanticus: the semantic-model workbench for Fabric and Power BI

Semanticus brings semantic-model authoring, analysis and proof into one VS Code workbench. Use the Model tree,
Properties and Studio yourself, then let your own AI Assistant work through MCP against the same live session. Every
change is attributed, broadcast and placed on one undo timeline.

Semanticus 1.0.1 corrects the Marketplace listing and adds matching-host packages for five operating-system and
architecture targets. Every package bundles its engine and is published only after that engine is extracted and
executed on the matching CI runner, plus the documented human acceptance checks.

![AI-readiness scorecard](https://semanticus.com.au/assets/readiness.png)

## What is included

- **Model authoring.** Edit supported BIM, PBIP and TMDL models, model properties, DAX, M, relationships,
  calculation groups, perspectives, roles and modern calendars.
- **Best Practice Analyzer.** Run compatible rules, apply individual fixes, load custom rulesets and record honest
  waivers without hiding the finding.
- **AI Readiness.** Grade model metadata from A to F, explain each finding, apply deterministic safe fixes and prepare
  descriptions, synonyms, Q&A and Prep for AI metadata.
- **Lineage and impact.** Trace model dependencies and supported local report references with explicit safe, unsafe
  or unknown verdicts.
- **DAX Lab.** Query, benchmark, profile and prove a candidate rewrite against known-good values before applying it.
- **Tests and evidence.** Store model checks, run deterministic proofs and export tamper-evident HTML and JSON
  evidence.
- **Workflows and Change Plans.** Enforce reviewable steps and checks, then apply an approved batch as one undoable
  transaction.
- **Compare and deploy preparation.** Compare two models, copy supported objects and preview deployment changes
  before any confirmed live write.

![Model diagram](https://semanticus.com.au/assets/shots/diagram.png)

![Lineage and impact](https://semanticus.com.au/assets/shots/lineage.png)

## Supported platforms

| Platform | 1.0.1 status | What is supported |
|---|---|---|
| Windows 11 x64 | Supported release package | VS Code extension, bundled engine, offline files, Power BI Desktop discovery, local M preview and supported XMLA journeys |
| Windows 11 ARM64 | Supported release package | VS Code extension, bundled engine, offline files and supported remote XMLA and Fabric journeys |
| Ubuntu 24.04 x64 | Supported release package | VS Code extension, bundled engine, offline files and supported remote XMLA and Fabric journeys |
| macOS Intel | Supported release package | VS Code extension, bundled engine, offline files and supported remote XMLA and Fabric journeys |
| macOS Apple Silicon | Supported release package | VS Code extension, bundled engine, offline files and supported remote XMLA and Fabric journeys |

The production package is platform-specific and bundles its engine. Users of an accepted package do not need a
separate .NET runtime. Remote XMLA and Fabric features still require a compatible tenant, capacity, permissions and
credentials.
Power BI Desktop discovery, local XMLA and local M preview remain Windows 11 x64 only.

## AI-native by design

Run **Semanticus: Connect AI Assistant** to configure the current workspace for a supported MCP client. The assistant
uses the same engine session shown in the UI, so its changes appear immediately and can be undone by the human.

The engine holds no model-provider API keys, performs no inference and sends no product telemetry. AI work happens
through the user's own assistant and account.

![Workflows](https://semanticus.com.au/assets/workflows.png)

## Free and Pro

The Free tier provides the workbench and individual edits. Pro adds reviewed bulk changes, enforced workflows,
advanced evidence and continuous health features. Current plans, terms and account options are published on the
Semanticus website; this README does not make a separate price promise.

Licenses verify offline. The extension opens the Semanticus account website for upgrade or management and does not
implement checkout, billing, cancellation or license minting.

## Quick start

1. Install the platform-specific extension package accepted for your operating system.
2. Open the **Semanticus** activity-bar view.
3. Run **Semanticus: Open Model** and select a supported local model, Power BI Desktop instance or XMLA endpoint.
4. Open Studio for readiness, diagrams, lineage, Tests, workflows and deployment preparation.
5. Optionally run **Semanticus: Connect AI Assistant** for dual-drive work through MCP.

## Known limitations

- Power BI Desktop discovery and local M preview are Windows-only.
- Modern calendar metadata requires compatibility level 1701 or later. Older models keep the classic Date-table
  path until the user explicitly upgrades and authors a calendar.
- Role security is static-first; there is no view-as-role impersonation proof.
- Cloud writes are deliberate: report discovery, Fabric git commit and update, and Data Agent publishing always
  preview as a dry run first and write only on explicit confirmation.
- When a model or tenant does not support a capability, Semanticus refuses with a clear reason instead of
  reporting success.

## Privacy

Model work runs locally. Network access occurs only for data or package sources the user explicitly opens, the
account website the user chooses to open, and supported optional downloads. Semanticus sends no product telemetry.

**Links:** [Website](https://semanticus.com.au) | [Product details](https://semanticus.com.au/pro) |
[GitHub](https://github.com/tenfingerseddy/semanticus-vscode) | [Support](mailto:hello@semanticus.com.au)
