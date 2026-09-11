# Semanticus

**Build, test and ship Power BI and Fabric semantic models in VS Code. Work alongside your own AI Assistant on one live model.**

[Website](https://semanticus.com.au) · [User guide](https://semanticus.com.au/docs) ·
[Download 1.1.2](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.1.2)

Semanticus brings model editing, DAX and M, best practices, lineage, tests and deployment into one workbench.
Open a local project or connect to a model. Make a change, check what it affects and review the difference before
publishing. Work yourself, ask your AI Assistant to help, or move between the two.

![Semanticus Studio model diagram](https://semanticus.com.au/assets/shots/diagram.png)

## One model, two ways to work

You use the Model tree, Properties and Studio. Your AI Assistant uses MCP. Both act on the same model and share
one undo history, with every change attributed to its author. Assistant edits appear in the UI immediately;
your edits reach the assistant in its next tool result.

Ask your assistant to explain measures, find unused columns, improve descriptions, investigate a failed test or
prepare a Change Plan. Inspect and adjust the result in the workbench, and undo it from the same timeline.

Semanticus runs no AI inference and holds no model-provider API keys. Bring your own MCP-compatible assistant
and account. The [MCP reference](https://semanticus.com.au/docs/mcp-tools) documents 313 operations.

## Tools for the whole model journey

| Your task | What Semanticus provides |
|---|---|
| Build and edit | Tables, measures, columns, relationships, calculation groups, roles, perspectives, DAX and M |
| Understand a model | Diagrams, search, lineage, impact, data previews and storage analysis |
| Improve quality | Best Practice Analyzer, AI Readiness and model metadata checks |
| Prove a change | DAX Lab, model tests, SQL-to-DAX reconciliation and exported evidence |
| Follow a repeatable process | Workflows, reviewed Change Plans and shared undo |
| Review and ship | Model comparison, object copying, deployment previews and confirmed live writes |

Work with BIM files, TMDL folders and PBIP semantic-model projects, or connect to remote XMLA models in Power BI,
Fabric and Azure Analysis Services. Windows x64 also supports discovering a running Power BI Desktop model.

![A workflow with questions and recorded checks](https://semanticus.com.au/assets/shots/workflows.png)

## Get started

1. Install the package for the operating system and architecture where VS Code runs.
2. Open the **Semanticus** view and run **Semanticus: Open Model**.
3. Choose a local model or supported connection, then open Studio.
4. Run **Semanticus: Connect AI Assistant** to work with an MCP-compatible assistant.
5. Edit and run checks. Save locally, or review the deployment preview before confirming Publish.

For a downloaded VSIX, open **Extensions**, choose **Views and More Actions**, select **Install from VSIX** and
reload. Every installer bundles its engine, so you do not need a separate .NET runtime.

## Supported platforms

| Platform | Package support | Scope |
|---|---|---|
| Windows 11 x64 | Supported release package | Offline models, Power BI Desktop discovery, local XMLA, local M preview and remote connections |
| Windows 11 ARM64 | Supported release package | Offline models and remote XMLA and Fabric connections |
| Ubuntu 24.04 x64 | Supported release package | Offline models and remote XMLA and Fabric connections |
| macOS Intel | Supported release package | Offline models and remote XMLA and Fabric connections |
| macOS Apple Silicon | Supported release package | Offline models and remote XMLA and Fabric connections |

Get the matching 1.1.2 installer and checksum from the
[GitHub release](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.1.2).
Marketplace publication is separate, so the version offered there can differ.

## What's new in 1.1.2

The post-UAT fixes cover Save, Publish, workflow Submit, licence handling, object copying, DAX and M validation,
stale Change Plans and MCP startup. Workflow authoring adds saved-source editing, shared Canvas layouts, nested
calls with inputs and answers, and reviewed workflow format upgrades.

Installed Linux checks and a live Publish-and-restore round trip passed. Linux and Windows automated checks passed.
Each release installer is built on a matching runner and its bundled engine is extracted and executed.
Read the [release notes](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.1.2) for details.

## Free and Pro

Free includes the workbench, individual edits, scans, tests and previews. Pro adds reviewed bulk changes, enforced
workflows, Verified Mode, advanced evidence and Pro-gated cloud writes.
[Compare the current plans](https://semanticus.com.au/pro).

## Know the boundaries

Power BI Desktop discovery, local XMLA and local M preview require Windows 11 x64. Remote features depend on your
tenant, capacity, permissions and sign-in. Modern calendars require compatibility level 1701 or later. Role security
analysis does not impersonate a user to prove dynamic RLS behaviour.

Model work runs locally. Connections and publishing use the sources and targets you choose. Semanticus sends no
product telemetry. Your AI Assistant uses its own account and data policies.

[Support details](https://semanticus.com.au/docs/supported-platforms) ·
[Privacy](https://semanticus.com.au/privacy) · [Source and licence](https://github.com/tenfingerseddy/semanticus-vscode)
