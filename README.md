# Semanticus

**Build, test and ship Power BI and Fabric semantic models in VS Code. Work alongside your own assistant on one live model.**

[Download 1.2.0](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.2.0) ·
[Website](https://semanticus.com.au) · [User guide](https://semanticus.com.au/docs) ·
[Release notes](CHANGELOG.md)

Semanticus brings model editing, DAX and M, model quality, lineage, tests and publishing into one workbench.
Open a local project or connect to a model, make a change, check what it affects and review the difference before
publishing. You can do the work yourself, ask your assistant to help, or move between the two.

Studio is grouped into five areas: **Model**, **Calculations**, **Checks**, **Changes** and **Workflows**.
You pick the area by the question you start with, then the page inside it.

![Semanticus Studio model diagram](https://semanticus.com.au/assets/shots/diagram.png)

## You and your assistant, working together

You use the Model tree, Properties and Studio. Your assistant uses MCP. Both act on the same model and share
one undo history, with changes attributed to whoever made them. An assistant's edit appears in the UI immediately;
your edits reach the assistant in its next tool result.

Ask it to explain a measure, find unused columns, improve descriptions, investigate a failed test or prepare a
reviewable set of proposed changes. You can inspect the result, adjust it and undo it from the workbench.

Semanticus runs no AI inference and holds no model-provider API keys. Bring your own MCP-compatible assistant
and account. The [MCP reference](https://semanticus.com.au/docs/mcp-tools) covers 314 operations.

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
| **Model** | Overview, Diagram, Lineage, Find and replace, Data, Size by table, Model Spec, Advanced Modelling, Power Query, Docs and Model notes. Tables, measures, columns, relationships, calculation groups, roles, perspectives and model properties are edited here, in DAX and M. |
| **Calculations** | DAX Lab. Write a formula, run it, compare answers across contexts and see where they differ. |
| **Checks** | Tests, Model quality, AI understanding and the saved Results. Reusable model tests, SQL-to-DAX reconciliation, DAX rewrite comparisons and exported results. |
| **Changes** | Proposed, History and Published. Review a set of changes before you keep it, see who changed what, compare against a live destination and publish after you confirm. |
| **Workflows** | A library grouped by the everyday job, a Runs view, and Steps, Canvas and Source as three views of one definition. |

Connections and Settings sit in the same header. Connections names four roles: Editing, Tests and queries,
Publish to and Reference model.

Open BIM files, TMDL folders and PBIP semantic-model projects. Connect to remote XMLA models in Power BI,
Fabric or Azure Analysis Services. On Windows x64, you can also discover a running Power BI Desktop model.

![A workflow with questions and recorded checks](https://semanticus.com.au/assets/shots/workflows.png)

## Install 1.2.0

Choose the installer that matches the operating system and architecture where VS Code runs. Every VSIX includes
its own engine; you do not need a separate .NET installation.

| Platform | Installer |
|---|---|
| Windows 11 x64 | [Windows x64](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.2.0/semanticus-win32-x64-1.2.0.vsix) |
| Windows 11 ARM64 | [Windows ARM64](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.2.0/semanticus-win32-arm64-1.2.0.vsix) |
| Ubuntu 24.04 x64 | [Linux x64](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.2.0/semanticus-linux-x64-1.2.0.vsix) |
| macOS Intel | [macOS Intel](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.2.0/semanticus-darwin-x64-1.2.0.vsix) |
| macOS Apple Silicon | [macOS Apple Silicon](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.2.0/semanticus-darwin-arm64-1.2.0.vsix) |

In VS Code, open **Extensions**, choose **Views and More Actions**, select **Install from VSIX**, then reload.
The [release page](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.2.0) also includes checksums.
Semanticus is available through the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=semanticus-vscode.semanticus-vscode);
its published version can differ from the direct GitHub release.

## Start with a model

1. Open the **Semanticus** view in VS Code.
2. Run **Semanticus: Open Model** and choose a local model or supported connection.
3. Open Studio and pick an area: Model, Calculations, Checks, Changes or Workflows.
4. Run **Semanticus: Connect AI Assistant** if you want your assistant to work in the same session.
5. Save local work. For a live destination, review what will be published before you confirm Publish.

The [getting started guide](https://semanticus.com.au/docs/getting-started) covers connections and your first edits.

## What changed in 1.2.0

Studio is now five areas: Model, Calculations, Checks, Changes and Workflows. Every page still exists and old
links still open the right page; several pages have plainer names, and a tool opened from a selected object
shows a Back trail. Workflows are a top-level area with a library grouped by everyday job and one editor for
Steps, Canvas and Source. Publishing says what is local and what is published, and never offers to publish
without a real destination. Required workflows keep the definition a project was bound against when the shipped
files change. The assistant is called your assistant everywhere. Read [the changelog](CHANGELOG.md) for the full list.

## Free and Pro

Semanticus Pro unlocks four whole features: Create in Model (Model Spec, Advanced Modelling, Power Query,
Docs and Model notes), Tests and Saved reports, the Advanced view under Changes and Published (source control,
Fabric Git, automated delivery and the Data Agent), and Workflows. Everything else is free, including every
one-click bulk fix, Model quality, AI understanding, Lineage with its published report checks, Verified Mode,
the assistant permission matrix, Promote, Publish, compare and restore points.
[Compare the current plans](https://semanticus.com.au/pro).

## Platform and data boundaries

Power BI Desktop discovery, local XMLA and local M preview require Windows 11 x64. Remote XMLA and Fabric features
depend on your tenant, capacity, permissions and sign-in. Modern calendars require compatibility level 1701 or later.
Role security analysis does not impersonate a user to prove dynamic RLS behaviour.

Model work runs locally. Connections and confirmed publishing use the sources and targets you choose. Semanticus
sends no product telemetry. Your assistant's data handling is governed by the assistant and account you use.
Read the [support details](https://semanticus.com.au/docs/supported-platforms) and
[privacy policy](https://semanticus.com.au/privacy).

## Build from source

You need the .NET 8 SDK, Node.js 20 or later and JDK 17. Initialise the pinned Tabular Editor submodule when cloning:

```bash
git clone --recurse-submodules https://github.com/tenfingerseddy/semanticus-vscode.git
cd semanticus-vscode
dotnet build Semanticus.sln
dotnet test Semanticus.Tests/Semanticus.Tests.csproj
cd Semanticus.VSCode
npm ci --no-fund --no-audit
npm test
npm run build:webview
```

Open the repository in VS Code and select **Run Semanticus Extension** to launch the development host with F5.
The extension and the MCP client connect to the same .NET engine session through JSON-RPC and MCP respectively.

## Licence and acknowledgements

Semanticus is source-available under the [Elastic License 2.0](LICENSE). It builds on work from Tabular Editor,
.NET, ANTLR, React, CodeMirror and other projects. Their licences and attributions are recorded in
[Third-party notices](THIRD-PARTY-NOTICES.md).
