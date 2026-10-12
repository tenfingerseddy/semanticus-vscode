# Semanticus

**Build, test and ship Power BI and Fabric semantic models in VS Code. Work alongside your own assistant on one live model.**

[Download 1.4.0](https://github.com/tenfingerseddy/semanticus-vscode/releases/latest) ·
[Website](https://semanticus.com.au) · [User guide](https://semanticus.com.au/docs) ·
[Release notes](CHANGELOG.md)

Semanticus brings model editing, DAX and M, model quality, lineage, tests and publishing into one workbench.
Open a local project or connect to a model, make a change, check what it affects and review the difference before
publishing. You can do the work yourself, ask your assistant to help, or move between the two.

Studio opens on the **Plan**, where you start, and has five more areas: **Explore**, **Edit**, **Review**,
**Publish** and **Workflows**.
You pick the area by the question you start with, then the page inside it.

![Semanticus Studio model diagram](https://semanticus.com.au/assets/shots/diagram.png)

## You and your assistant, working together

You use the Model tree, Properties and Studio. Your assistant uses MCP. Both act on the same model and share
one undo history, with changes attributed to whoever made them. An assistant's edit appears in the UI immediately;
your assistant sees your edits when it next reads the model.

Ask it to explain a measure, find unused columns, improve descriptions, investigate a failed test or prepare a
reviewable set of proposed changes. You can inspect the result, adjust it and undo it from the workbench.

Semanticus never holds your assistant's API key. On Free it runs no AI inference; on Pro, Jev, the Semanticus
helper, makes small modelling choices you can check (see Free and Pro). Bring your own MCP-compatible assistant
and account. The [MCP reference](https://semanticus.com.au/docs/mcp-tools) covers more than 340 operations.

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
[Download the skills pack](https://github.com/tenfingerseddy/semanticus-vscode/releases/latest)

Skills use your existing MCP connection. **Semanticus: Connect AI Assistant** writes the connection file for clients using
`.mcp.json`; the setup guide explains the equivalent configuration for other clients. Core orientation also
comes from MCP, so an assistant can work without installing skills.

## The six areas

| Area | What you do there |
|---|---|
| **Plan** | Where you start. Every piece of work on the model is a row that says who does it: Semanticus, you or your assistant. Type a task, give work to your assistant, run the ready fixes as one change you can undo, and open History to see every edit, who made it and undo back to any point. Workflow runs are rows here too. |
| **Explore** | Data, Ask your data (Pro: type a question about your numbers and get the answer as a table; when the question can be read two ways, it says which one it shows and names the other), Diagram, Lineage and Size. |
| **Edit** | Find and replace, Queries (write a DAX query, run it and compare answers across contexts), AI instructions with Model notes, Advanced modelling and Power Query. Tables, measures, columns, relationships, calculation groups, roles and perspectives are edited here, in DAX and M. |
| **Review** | Model quality (BPA), AI readiness, Validation with its Reports, and Docs. |
| **Publish** | Published: compare against a live destination, publish after you confirm, and roll back to a published version. |
| **Workflows** | Your workflows, and Author for the one you open. Start from a starter that comes with Semanticus, or describe what you want in words. Team rules, when a team sets them, say which workflows must be used. |

Connections and Settings sit in the same header. Connections names four roles: Editing, Tests and queries,
Publish to and Reference model.

Open BIM files, TMDL folders and PBIP semantic-model projects. Connect to remote XMLA models in Power BI,
Fabric or Azure Analysis Services. On Windows x64, you can also discover a running Power BI Desktop model.

![A workflow with questions and recorded checks](https://semanticus.com.au/assets/shots/workflows.png)

## Install 1.4.0

Install Semanticus from the [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=semanticus-vscode.semanticus-vscode): in VS Code, open **Extensions**,
search for Semanticus and choose **Install**. VS Code picks the right build for your computer, and every build
includes its own engine, so you do not need a separate .NET installation.

To install by hand, open the [latest GitHub release](https://github.com/tenfingerseddy/semanticus-vscode/releases/latest). It has five installers, one each for
Windows x64, Windows ARM64, Linux x64, macOS Intel and macOS Apple Silicon, and a checksum file. Download the one
for the computer where VS Code runs. In VS Code, open **Extensions**, choose **Views and More Actions**, select
**Install from VSIX**, then reload.

## Start with a model

1. Open the **Semanticus** view in VS Code.
2. Run **Semanticus: Open Model** and choose a local model or supported connection.
3. Open Studio. It opens on the Plan; the other areas are Explore, Edit, Review, Publish and Workflows.
4. Run **Semanticus: Connect AI Assistant** if you want your assistant to work in the same session.
5. Save local work. For a live destination, review what will be published before you confirm Publish.

The [getting started guide](https://semanticus.com.au/docs/getting-started) covers connections and your first edits.

## What changed in 1.4.0

Your assistant can now drive Studio while you watch. A bar at the top says who has the wheel, and you can see it press a button. A yes in chat is enough for it to go ahead; only the
Permissions page still needs you. On Pro, bulk edits change many objects in one step with one Undo, and they are much
faster. Run the plan finishes the plan in one press, and Refresh plan looks again for new work. The Plan speaks in plain
words, unsaved changes keep a recovery copy, and your assistant can list a workspace's SQL endpoints. Read
[the changelog](CHANGELOG.md) for the full list.

## Free and Pro

Free is the tool. Pro is the tool that works for you.

Everything you can do by hand, or with your own assistant, is free. That is Model Spec, Advanced
modelling, Power Query, Docs, Model notes, Validation, Reports, Model quality, AI readiness, Lineage,
Pending changes, Queries, Record verified edits, every fix, the assistant permission matrix, Promote,
Publish, and the advanced publishing tools including source control, Fabric Git, automated delivery and
the Data Agent.

Pro is the product deciding and doing it for you: the plan ranked, scored and run, the ask box on the Plan, Workflows, Ask your data, bulk edits that change many objects in one step, and Jev. Jev is the Semanticus helper that makes small modelling choices on Pro: it picks column formats, chooses base measures for number columns, chooses what Copilot sees and puts the plan in order. It also drafts from your words: a workflow you describe on the Workflows page, a task you type with Ctrl J, and a task you type on the Plan once you press Draft the steps. Each choice about the model arrives as a Plan row you can read, change or drop, and What Pro decides, on the Pro page, lists every choice and switches each one off. Nothing is sent to Jev until you agree for that model. Then what goes is table, column and measure names, their types, formats, folders and descriptions, the other names your model gives a measure (its synonyms) when your question uses one, counts and relationships, facts about a measure's formula such as the functions it uses (never the formula itself), and the words you type: questions in the ask box, in Ask your data and in the question box on Queries, and the words that describe a task or a workflow. Words you type go as you typed them. A row or a value from your tables never goes, and neither do your connection or anything from another model. Your agreement is kept outside the model files; a small marker, `Semanticus_ConsentId`, is written into the model so the agreement stays with it, and you will see it in a commit. On the free plan the plan still lists every gap in rule
order, with a jump on each one to the page where you can do it by hand.
[Compare the current plans](https://semanticus.com.au/pro).

## Platform and data boundaries

Power BI Desktop discovery, local XMLA and local M preview require Windows 11 x64. Remote XMLA and Fabric features
depend on your tenant, capacity, permissions and sign-in. Modern calendars require compatibility level 1701 or later.
Role security analysis does not impersonate a user to prove dynamic RLS behaviour.

Model work runs locally. Pro sends the model's shape (names, types, formats, counts, relationships and some
descriptions) and the words you type to its advice service, only after you allow it for that model, and never rows,
values or credentials. Connections and confirmed publishing use the sources and targets you choose. Semanticus sends
no product telemetry. Your assistant's data handling is governed by the assistant and account you use.
Read the [support details](https://semanticus.com.au/docs/supported-platforms) and
[privacy policy](https://semanticus.com.au/privacy).

## Build from source

You need the .NET 8 SDK, Node.js 22 or later (the packager, vsce 4, needs 22) and JDK 17. Initialise the pinned Tabular Editor submodule when cloning:

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

### Where the engine keeps your state

The engine keeps per-user state (saved connections, connection history, agent policy, consents, global
knowledge, the licence file) in `~/.semanticus`, and saved accounts and sign-ins in `Semanticus/auth` under the
local application data folder (`~/.local/share` on Linux). To keep a run out of both, for example an assistant or
a test working on a copy of a model, point the engine at another folder with `--state-dir <dir>`,
`--state-dir=<dir>` or the `SEMANTICUS_HOME` environment variable:

```bash
Semanticus.Engine mcp --workspace /tmp/copy --state-dir /tmp/copy-state
```

The folder is used by that engine and by any engine it starts. Its sign-ins go in `<dir>/auth`, so a run with
its own folder signs in on its own and never reads or changes yours. A tool that must use your own sign-in on
purpose can name that store with `SEMANTICUS_SIGNIN_DIR`. Any other spelling of the option (`--STATE-DIR`,
`--statedir`, a missing folder, or `--state-dir` pasted with a long dash from a document or chat) is refused, so
a typo never falls back to your real folder. If `HOME` names a
folder that does not exist and no state folder is given, the engine stops with a message rather than writing
into whatever folder it was started in. An engine given a
state folder never joins an engine already running for the same workspace that keeps its state somewhere else,
and your own engine (given no state folder) never joins one that another run started with a folder of its own:
either way it stops with a message that names the other folder. The locks that stop two engines on one model from
writing over each other's History, drafts and Plan claims do not move with the state folder: they live in
`Semanticus/locks` under the local application data folder (`~/.local/share/Semanticus/locks` on Linux, whatever
`XDG_DATA_HOME` says), so a run with its own folder still waits for your engine if it opens the same model. Those empty lock files are the only thing such a run writes outside its folder, unless it signs in: the sign-in library may then create an empty `.IdentityService` folder there too. One limit applies while you update: an engine from an earlier release keeps its locks only in `~/.semanticus/locks`. A run with its own folder waits on those as well, but only on lock files that engine has already created, and the older engine cannot see the run's own locks. So while an older engine is still open on the same model, the two can still write the same History, and the older one may treat the run's Plan claims as gone. Close older windows before you start such a run on your real model. Opening a model under the system temp folder is recorded in the history but is
not added to the saved connections, and those history rows only ever push out each other, never yours. The saved list keeps every cloud connection and every labelled one; only local
models you opened (files, folders and Power BI Desktop instances) roll off, oldest first. Each rewrite of
`connections.json` and `connection-history.json` keeps the previous version beside it as a `.bak` file, and a
file that cannot be read is set aside and the list carries on from that copy. Because the copy is one change
behind, every row it brings back comes back labelled `prod`, even one that had no label or a looser one; set the label again by hand if it should be looser.

A browser sign-in opens your default browser only in the engine a VS Code window starts. Every other engine
(the assistant's, a test's, a script's) refuses such a sign-in with a message and opens nothing. Test runs, smokes and the probe tools are also cut off from your desktop: no display, no desktop session bus (so no keyring either), and a stub in place of every browser opener and desktop launcher on the command path. A browser started by its full path meets no stub, but it has no display or session to open on. Test runs and smokes cannot sign in as you without a browser either: the Azure CLI is a stub whose settings folder is an empty one of the run's own, a sign-in store named from outside is ignored, and so are service principal settings (except in the CI smoke, whose live check needs one). The two live probes you run by hand against an endpoint you name still use your Azure CLI. If anything in a run calls a stub, the stub opens nothing and the run fails, naming the call. The one case the run cannot fail on is a call made after its last check, from a thread still running as the test process exits; no test may register code that runs at exit.

## Licence and acknowledgements

Semanticus is source-available under the [Elastic License 2.0](LICENSE). It builds on work from Tabular Editor,
.NET, ANTLR, React, CodeMirror and other projects. Their licences and attributions are recorded in
[Third-party notices](THIRD-PARTY-NOTICES.md).
