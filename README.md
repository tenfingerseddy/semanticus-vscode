# Semanticus

**Build, test and ship Power BI and Fabric semantic models in VS Code. Work alongside your own AI Assistant on one live model.**

[Download 1.1.2](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.1.2) ·
[Website](https://semanticus.com.au) · [User guide](https://semanticus.com.au/docs) ·
[Release notes](CHANGELOG.md)

Semanticus brings model editing, DAX and M, best practices, lineage, tests and deployment into one workbench.
Open a local project or connect to a model, make a change, check what it affects and review the difference before
publishing. You can do the work yourself, ask your AI Assistant to help, or move between the two.

![Semanticus Studio model diagram](https://semanticus.com.au/assets/shots/diagram.png)

## You and your assistant, working together

You use the Model tree, Properties and Studio. Your AI Assistant uses MCP. Both act on the same model and share
one undo history, with changes attributed to whoever made them. An assistant's edit appears in the UI immediately;
your edits reach the assistant in its next tool result.

Ask it to explain a measure, find unused columns, improve descriptions, investigate a failed test or prepare a
reviewable Change Plan. You can inspect the result, adjust it and undo it from the workbench.

Semanticus runs no AI inference and holds no model-provider API keys. Bring your own MCP-compatible assistant
and account. The [MCP reference](https://semanticus.com.au/docs/mcp-tools) covers 313 operations.

## From first edit to publish

| What you need to do | Tools in the workbench |
|---|---|
| Build and edit a model | Tables, measures, columns, relationships, calculation groups, roles, perspectives, model properties, DAX and M |
| Understand what is there | Model diagrams, search, lineage and impact, data previews and storage analysis |
| Improve model quality | Best Practice Analyzer, AI Readiness, descriptions, synonyms and metadata checks |
| Check that a change is right | DAX Lab, reusable model tests, SQL-to-DAX reconciliation, rewrite proofs and exported evidence |
| Work through a complex change | Workflows, reviewed Change Plans and shared undo |
| Prepare and publish | Model comparison, object copying, deployment previews and confirmed live writes |

Open BIM files, TMDL folders and PBIP semantic-model projects. Connect to remote XMLA models in Power BI,
Fabric or Azure Analysis Services. On Windows x64, you can also discover a running Power BI Desktop model.

![A workflow with questions and recorded checks](https://semanticus.com.au/assets/shots/workflows.png)

## Install 1.1.2

Choose the installer that matches the operating system and architecture where VS Code runs. Every VSIX includes
its own engine; you do not need a separate .NET installation.

| Platform | Installer |
|---|---|
| Windows 11 x64 | [Windows x64](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.1.2/semanticus-win32-x64-1.1.2.vsix) |
| Windows 11 ARM64 | [Windows ARM64](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.1.2/semanticus-win32-arm64-1.1.2.vsix) |
| Ubuntu 24.04 x64 | [Linux x64](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.1.2/semanticus-linux-x64-1.1.2.vsix) |
| macOS Intel | [macOS Intel](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.1.2/semanticus-darwin-x64-1.1.2.vsix) |
| macOS Apple Silicon | [macOS Apple Silicon](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.1.2/semanticus-darwin-arm64-1.1.2.vsix) |

In VS Code, open **Extensions**, choose **Views and More Actions**, select **Install from VSIX**, then reload.
The [release page](https://github.com/tenfingerseddy/semanticus-vscode/releases/tag/v1.1.2) also includes checksums.
Semanticus is available through the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=semanticus-vscode.semanticus-vscode);
its published version can differ from the direct GitHub release.

## Start with a model

1. Open the **Semanticus** view in VS Code.
2. Run **Semanticus: Open Model** and choose a local model or supported connection.
3. Open Studio to edit, explore, run checks and review changes.
4. Run **Semanticus: Connect AI Assistant** if you want your assistant to work in the same session.
5. Save local work. For a live target, review the deployment preview before confirming Publish.

The [getting started guide](https://semanticus.com.au/docs/getting-started) covers connections and your first edits.

## What changed in 1.1.2

This release includes the post-UAT repairs to Save, Publish, workflow Submit, licence handling and object copying.
It fixes valid quoted DAX and M expressions being rejected, prevents stale Change Plans from overwriting newer
column settings, and fixes MCP startup when workflow tools have optional inputs.

Workflow authoring also gains saved-source editing, shared Canvas layouts, nested calls with inputs and answers,
and an explicit preview for upgrading saved workflow formats. See the [changelog](CHANGELOG.md) for the details.

The final post-fix check passed on the installed Linux workbench, including a live Publish-and-restore round trip.
Linux and Windows automated checks passed. Release installers are built on matching runners, and each bundled
engine is extracted and executed before publication.

## Free and Pro

Free includes the workbench, individual edits, scans, model tests and previews. Pro adds reviewed bulk changes,
enforced workflows, Verified Mode, advanced evidence and Pro-gated cloud writes. See the
[current plans](https://semanticus.com.au/pro) for the full comparison.

## Platform and data boundaries

Power BI Desktop discovery, local XMLA and local M preview require Windows 11 x64. Remote XMLA and Fabric features
depend on your tenant, capacity, permissions and sign-in. Modern calendars require compatibility level 1701 or later.
Role security analysis does not impersonate a user to prove dynamic RLS behaviour.

Model work runs locally. Connections and confirmed publishing use the sources and targets you choose. Semanticus
sends no product telemetry. Your AI Assistant's data handling is governed by the assistant and account you use.
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
