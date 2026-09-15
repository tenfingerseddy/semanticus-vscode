# Semanticus

**Build, test and ship Power BI and Fabric semantic models in VS Code. Work alongside your own assistant on one live model.**

[Website](https://semanticus.com.au) · [User guide](https://semanticus.com.au/docs) ·
[Download 1.2.0](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.2.0)

Semanticus brings model editing, DAX and M, model quality, lineage, tests and publishing into one workbench.
Open a local project or connect to a model. Make a change, check what it affects and review the difference before
publishing. Work yourself, ask your assistant to help, or move between the two.

Studio is grouped into five areas: **Model**, **Calculations**, **Checks**, **Changes** and **Workflows**.
Pick the area by the question you start with, then the page inside it.

![Semanticus Studio model diagram](https://semanticus.com.au/assets/shots/diagram.png)

## One model, two ways to work

You use the Model tree, Properties and Studio. Your assistant uses MCP. Both act on the same model and share
one undo history, with every change attributed to its author. Assistant edits appear in the UI immediately;
your edits reach the assistant in its next tool result.

Ask your assistant to explain measures, find unused columns, improve descriptions, investigate a failed test or
prepare a set of proposed changes. Inspect and adjust the result in the workbench, and undo it from the same
timeline.

Semanticus runs no AI inference and holds no model-provider API keys. Bring your own MCP-compatible assistant
and account. The [MCP reference](https://semanticus.com.au/docs/mcp-tools) documents 314 operations.

## Help as you work

Every Studio page explains its purpose and offers three **Getting started** steps. **Help** opens a detailed
guide, a searchable list of tasks and explanations of common terms. You can read more when you need it
without leaving the workbench.

## Skills for your assistant

Seven optional guides help your assistant use Semanticus: getting started, DAX optimisation, AI readiness,
model interviews, Change Plans, reusable workflows and model knowledge.

Run **Semanticus: Install or Update Assistant Skills** to install or update guides for Claude Code or Codex.
Copilot can load the bundled skills directly on VS Code versions supporting extension-provided skills.
The same content is available as a versioned plugin pack and portable skill folders.

[Install and use the skills](https://semanticus.com.au/docs/assistant-skills) ·
[Download the skills pack](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.2.0/semanticus-assistant-skills-1.2.0.zip)

Skills use your existing MCP connection. **Semanticus: Connect AI Assistant** writes the connection file for clients using
`.mcp.json`; the setup guide explains the equivalent configuration for other clients. Core orientation also
comes from MCP, so an assistant can work without installing skills.

## The five areas

| Area | What you do there |
|---|---|
| **Model** | Overview, Diagram, Lineage, Find and replace, Data, Size by table, Model Spec, Advanced Modelling, Power Query, Docs and Model notes. Tables, measures, columns, relationships, calculation groups, roles and perspectives are edited here, in DAX and M. |
| **Calculations** | DAX Lab. Write a formula, run it, compare answers across contexts and see where they differ. |
| **Checks** | Tests, Model quality, AI understanding and Saved reports. Model tests, SQL-to-DAX reconciliation and exported results. |
| **Changes** | Proposed, History and Published. Review a set of changes before you keep it, see who changed what, compare against a live destination and publish after you confirm. |
| **Workflows** | A library grouped by the everyday job, a Runs view, and Steps, Canvas and Source as three views of one definition. |

Connections and Settings sit in the same header. Connections names four roles: Editing, Tests and queries,
Publish to and SQL sources.

Work with BIM files, TMDL folders and PBIP semantic-model projects, or connect to remote XMLA models in Power BI,
Fabric and Azure Analysis Services. Windows x64 also supports discovering a running Power BI Desktop model.

![A workflow with questions and recorded checks](https://semanticus.com.au/assets/shots/workflows.png)

## Get started

1. Install the package for the operating system and architecture where VS Code runs.
2. Open the **Semanticus** view and run **Semanticus: Open Model**.
3. Choose a local model or supported connection, then open Studio and pick an area: Model, Calculations,
   Checks, Changes or Workflows.
4. Run **Semanticus: Connect AI Assistant** to work with an MCP-compatible assistant.
5. Edit and run checks. Save locally, or review what will be published before you confirm Publish.

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

Get the matching 1.2.0 installer and checksum from the
[GitHub release](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.2.0).
Marketplace publication is separate, so the version offered there can differ.

## What's new in 1.2.0

Studio is now five areas: Model, Calculations, Checks, Changes and Workflows. Every page still exists and old
links still open the right page, and several pages have plainer names. Tests is rebuilt around one list of
saved checks, a New check drawer with three kinds, and SQL sources saved once in Connections. Lineage is one
page with Graph, Tree and Impact. Workflows are a top-level area with a library grouped by everyday job and one
editor for Steps, Canvas and Source. Publishing says what is local and what is published, and never offers to
publish without a real destination. Required workflows keep the definition a project was bound against when the
shipped files change. The assistant is called your assistant everywhere. Read [the release notes](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.2.0) for the full list.

## Free and Pro

Semanticus Pro unlocks four whole features: Create in Model (Model Spec, Advanced Modelling, Power Query,
Docs and Model notes), Tests and Saved reports, the Advanced view under Changes and Published (source control,
Fabric Git, automated delivery and the Data Agent), and Workflows. Everything else is free, including every
one-click bulk fix, Model quality, AI understanding, Lineage with its published report checks, Verified Mode,
the assistant permission matrix, Promote, Publish, compare and restore points.
[Compare the current plans](https://semanticus.com.au/pro).

## Know the boundaries

Power BI Desktop discovery, local XMLA and local M preview require Windows 11 x64. Remote features depend on your
tenant, capacity, permissions and sign-in. Modern calendars require compatibility level 1701 or later. Role security
analysis does not impersonate a user to prove dynamic RLS behaviour.

Model work runs locally. Connections and publishing use the sources and targets you choose. Semanticus sends no
product telemetry. Your assistant uses its own account and data policies.

[Support details](https://semanticus.com.au/docs/supported-platforms) ·
[Privacy](https://semanticus.com.au/privacy) · [Source and licence](https://github.com/tenfingerseddy/semanticus-vscode)
