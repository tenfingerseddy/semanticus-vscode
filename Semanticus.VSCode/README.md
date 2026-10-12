# Semanticus

**Build, test and ship Power BI and Fabric semantic models in VS Code. Work alongside your own assistant on one live model.**

[Website](https://semanticus.com.au) · [User guide](https://semanticus.com.au/docs) ·
[Download](https://github.com/tenfingerseddy/semanticus-vscode/releases/latest)

Semanticus brings model editing, DAX and M, model quality, lineage, tests and publishing into one workbench.
Open a local project or connect to a model. Make a change, check what it affects and review the difference before
publishing. Work yourself, ask your assistant to help, or move between the two.

Studio opens on the **Plan**, where you start, and has five more areas: **Explore**, **Edit**, **Review**,
**Publish** and **Workflows**.
Pick the area by the question you start with, then the page inside it.

![Semanticus Studio model diagram](https://semanticus.com.au/assets/shots/diagram.png)

## One model, two ways to work

You use the Model tree, Properties and Studio. Your assistant uses MCP. Both act on the same model and share
one undo history, with every change attributed to its author. Assistant edits appear in the UI immediately;
your assistant sees your edits when it next reads the model.

Ask your assistant to explain measures, find unused columns, improve descriptions, investigate a failed test or
prepare a set of proposed changes. Inspect and adjust the result in the workbench, and undo it from the same
timeline.

Semanticus never holds your assistant's API key. On Free it runs no AI inference; on Pro, Jev, the Semanticus
helper, makes small modelling choices you can check (see Free and Pro). Bring your own MCP-compatible assistant
and account. The [MCP reference](https://semanticus.com.au/docs/mcp-tools) documents more than 340 operations.

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
Publish to and SQL sources.

Work with BIM files, TMDL folders and PBIP semantic-model projects, or connect to remote XMLA models in Power BI,
Fabric and Azure Analysis Services. Windows x64 also supports discovering a running Power BI Desktop model.

![A workflow with questions and recorded checks](https://semanticus.com.au/assets/shots/workflows.png)

## Get started

1. Install the package for the operating system and architecture where VS Code runs.
2. Open the **Semanticus** view and run **Semanticus: Open Model**.
3. Choose a local model or supported connection, then open Studio. It opens on the Plan; the other areas are
   Explore, Edit, Review, Publish and Workflows.
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

Get the matching installer and checksum from the
[latest GitHub release](https://github.com/tenfingerseddy/semanticus-vscode/releases/latest).
Marketplace publication is separate, so the version offered there can differ.

## What's new in 1.4

Your assistant can now drive Studio while you watch.

- **Claude drives, or you do.** A switch at the top of Studio says who is at the wheel. On Claude drives, your
  assistant opens pages, picks things and shows you what it is doing. On I drive, Studio stays where you put it.
  Take the wheel back any time by using Studio yourself.
- **Notes.** Your assistant leaves a short note next to what it is talking about. Read it and carry on.
- **The Claude cursor.** A cursor shows where your assistant is working, so you can follow along.
- **Suggestions.** Your assistant offers the next step as a button you can press.
- **First run.** The first time you open Studio, a short card sets up how you want to work.
- **Learn.** A guided tour on a small sample shop model. Pick **Show me around** on the first run, or open Learn later.
- **A yes in chat is enough.** When your assistant asks to go ahead and you say yes in the chat, it goes ahead. Only
  the Permissions page still needs you.
- **Bulk edits in one step (Pro).** Waive findings, write words and synonyms, edit objects, create measures and ten more
  forms change many objects in one call, with one Undo and the real count. They are much faster than one at a time.
- **Run the plan finishes the plan.** One press does every ready row and hands your assistant its rows. Refresh plan looks
  again for new work and clears the finished rows.
- **The Plan speaks plain words.** Counts add up, and each change names what it changes.
- **Your work is kept.** Unsaved changes to a file model keep a recovery copy. Open the model again to Restore them or
  Discard.
- **Faster.** The check after each edit and synonym writes are much quicker, and your assistant can list the SQL
  endpoints of a workspace.

### Since 1.2

Studio is six areas: Plan, Explore, Edit, Review, Publish and Workflows. The Plan is where you start, and History
lives inside it. Workflows is one page: start from a starter, or on Pro describe a workflow in words and it becomes
steps. Connections are on one page, with a SQL endpoint for each model. On Pro, Ask your data (under Explore) answers a
question about your numbers. When a question can fairly be read two ways, the answer says which reading it shows and names the other, for example "Showing Net Sales. You may have meant Gross Sales." And Jev makes small modelling choices you can check: column formats, base measures for
number columns, what Copilot sees and the plan's order. What Pro decides, on the Pro page, lists
every choice and switches each one off. Nothing goes to Jev until you agree; Free and Pro below says exactly what goes.

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

## Know the boundaries

Power BI Desktop discovery, local XMLA and local M preview require Windows 11 x64. Remote features depend on your
tenant, capacity, permissions and sign-in. Modern calendars require compatibility level 1701 or later. Role security
analysis does not impersonate a user to prove dynamic RLS behaviour.

Model work runs locally. Pro sends the model's shape (names, types, formats, counts, relationships and some
descriptions) and the words you type to its advice service, only after you allow it for that model, and never rows,
values or credentials. Connections and publishing use the sources and targets you choose. Semanticus sends no product
telemetry. Your assistant uses its own account and data policies.

[Support details](https://semanticus.com.au/docs/supported-platforms) ·
[Privacy](https://semanticus.com.au/privacy) · [Source and licence](https://github.com/tenfingerseddy/semanticus-vscode)
