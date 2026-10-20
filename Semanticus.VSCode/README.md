# Semanticus

**Build, test and ship Power BI and Fabric semantic models in VS Code. Work alongside your own assistant on one live model.**

[Website](https://semanticus.com.au) · [User guide](https://semanticus.com.au/docs) ·
[Download](https://github.com/tenfingerseddy/semanticus-vscode/releases/latest)

Semanticus brings model editing, DAX and M, model quality, lineage, tests and publishing into one workbench.
Open a local project or connect to a model. Make a change, check what it affects and review the difference before
publishing. Work yourself, ask your assistant to help, or move between the two.

Studio opens on the **Plan**, where you start, and has four more areas: **Explore**, **Edit**, **Review** and
**Publish**.
Pick the area by the question you start with, then the page inside it.

![The Plan in Semanticus Studio, with work from Semanticus, Jev and your assistant](https://semanticus.com.au/assets/shots/plan.png)

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

![The model diagram in the bus matrix layout](https://semanticus.com.au/assets/shots/diagram-matrix.png)

## Help as you work

Every Studio page explains its purpose and offers three **Getting started** steps. **Help** opens a detailed
guide, a searchable list of tasks and explanations of common terms. You can read more when you need it
without leaving the workbench.

## Skills for your assistant

Seven optional guides help your assistant use Semanticus: getting started, DAX optimisation, AI readiness,
model interviews, Change Plans and model knowledge.

Run **Semanticus: Install or Update Assistant Skills** to install or update guides for Claude Code or Codex.
Copilot can load the bundled skills directly on VS Code versions supporting extension-provided skills.
The same content is available as a versioned plugin pack and portable skill folders.

[Install and use the skills](https://semanticus.com.au/docs/assistant-skills) ·
[Download the skills pack](https://github.com/tenfingerseddy/semanticus-vscode/releases/latest)

Skills use your existing MCP connection. **Semanticus: Connect AI Assistant** writes the connection file for clients using
`.mcp.json`; the setup guide explains the equivalent configuration for other clients. In Studio, the Your assistant
menu has **Connect another assistant** for Claude Desktop, Codex, Cursor and Claude Code. Core orientation also
comes from MCP, so an assistant can work without installing skills.

## The five areas

| Area | What you do there |
|---|---|
| **Plan** | Where you start. Every piece of work on the model is a row that says who does it: Semanticus, you or your assistant. One header line shows the grade figures, what is happening now and Run the plan, and the list is grouped by who does the work. Type a task, give work to your assistant, run one task or the ready fixes as one change you can undo, and open History to see every edit, who made it and undo back to any point. |
| **Explore** | Data, Query (a notebook: ask in plain words, which is Pro, or build it from fields, or write DAX; each question is one row with its number first and how it was read, and DAX, Table, Speed, Steps, Compare, Save and Explain open when you want them; Explain this number splits it by the column that explains it), Diagram (a bus matrix, with a dot grid under Arrange), Lineage and Size. |
| **Edit** | AI instructions with Model notes, Advanced modelling and Power Query. Tables, measures, columns, relationships, calculation groups, roles and perspectives are edited here, in DAX and M. |
| **Review** | Model quality (BPA), AI readiness, the Copilot test (see how Copilot answers your model and what to fix), Validation (checks against SQL or a trusted number, with Waive and a note that stays visible) with its Reports, and Docs. |
| **Publish** | Published: compare against a live destination, publish after you confirm (a rename goes with the measures, relationships and roles that use it, in one press), and roll back to a published version. |

Connections and Settings sit in the same header. Connections names four roles: Editing, Tests and queries,
Publish to and SQL sources.

Work with BIM files, TMDL folders and PBIP semantic-model projects, or connect to remote XMLA models in Power BI,
Fabric and Azure Analysis Services. Windows x64 also supports discovering a running Power BI Desktop model.

![The Query notebook: a question in plain words, its number and one picture](https://semanticus.com.au/assets/shots/query.png)

![Validation: a number checked against SQL, with an accepted difference](https://semanticus.com.au/assets/shots/validation.png)

## Get started

1. Install the package for the operating system and architecture where VS Code runs.
2. Open the **Semanticus** view and run **Semanticus: Open Model**.
3. Choose a local model or supported connection, then open Studio. It opens on the Plan; the other areas are
   Explore, Edit, Review and Publish.
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

## What's new in 1.6

Ask your model in plain words, check your numbers, and see how Copilot answers.

- **The Query notebook.** One box asks in plain words. Each question is one row with its number first and how it was
  read. Open its DAX, Table, Speed, Compare, Save and Explain when you want them. Pin, close or remove a row.
- **Validation.** Check your numbers against SQL or a number you trust, with the true number on both sides. Waive a
  difference with a note that stays visible. A waived check counts as accepted, never as a pass.
- **The Copilot test.** See how Copilot answers your model, and what to fix. Your assistant writes the questions, a
  small model answers from only what Copilot sees, and each miss names its cause.
- **Explain this number.** It splits a number by the column that explains it, and one press tries another column.
- **Publish.** One press sends a rename with the measures, relationships and roles that use it. The page tells the
  truth before the press and after it.
- **Saving and closing.** Your assistant saves to the model's own file. Closing VS Code no longer waits for work you
  already saved.

### In 1.5

- **A calmer Plan.** One header line holds the figures AI ready, Best practice and Plan done, the live slot and Run the
  plan. The list is grouped by who does the work, and History is in one place. Run one task with its own Undo. A long
  plan folds by group and has a Filter.
- **Lineage Graph on a big model.** It opens in about half a second, where it took 20 to 40 seconds. It lays out the
  same way each time.
- **Try Pro free for 30 days.** Start with your email. Your Pro month shows a tally of what Pro did for you, and a line
  in the last 3 days offers Keep Pro.
- **Connect another assistant.** One menu connects Claude Desktop, Codex, Cursor and Claude Code, and saves your Pro
  licence for them. Pro follows you to an assistant you connected on Free.
- **One colour for each worker.** Semanticus is green, you are blue and your assistant is purple. Charts share one
  palette of green, blue and purple. When your assistant drives, a clear purple outline runs round Studio.
- **The Link and the Next line.** The Link shows when Studio is working, thinking, done or stopped. A Next line names the
  next step on the other pages. Voice and small movements follow Personality in Help, which can be Quiet.
- **Publish.** Roles are created, updated and deleted on the live model, and Undo of a push brings a deleted role back.
  Your yes in chat lets your assistant publish through a red check, and upkeep-only warnings never block.
- **A bus matrix.** The Diagram keeps the bus matrix and adds a dot grid under Arrange.
- **Faster.** The first grade on open takes about 0.3 seconds, where it took about 3. A full Validation run on a large
  suite took 51 seconds and now takes 33.
- **One allow is enough for a build.** One allow gives Jev its permission for the model and the tables in the plan.

### In 1.4

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

The Plan is where you start, and History lives inside it. Connections are on one page, with a SQL endpoint for each model. On Pro, the plain-words box in Explore > Query answers a
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

Pro is the product deciding and doing it for you: the plan ranked, scored and run, the ask box on the Plan, the plain-words box on Query, bulk edits that change many objects in one step, and Jev. Jev is the Semanticus helper that makes small modelling choices on Pro: it picks column formats, chooses base measures for number columns, chooses what Copilot sees and puts the plan in order. It also drafts from your words: a task you type with Ctrl J, and a task you type on the Plan once you press Draft the steps. Each choice about the model arrives as a Plan row you can read, change or drop, and What Pro decides, on the Pro page, lists every choice and switches each one off. Nothing is sent to Jev until you agree for that model. Then what goes is table, column and measure names, their types, formats, folders and descriptions, the other names your model gives a measure (its synonyms) when your question uses one, counts and relationships, facts about a measure's formula such as the functions it uses (never the formula itself), and the words you type: questions in the ask box and in the plain-words box on Query, and the words that describe a task. Words you type go as you typed them. A row or a value from your tables never goes, and neither do your connection or anything from another model. Your agreement is kept outside the model files; a small marker, `Semanticus_ConsentId`, is written into the model so the agreement stays with it, and you will see it in a commit. On the free plan the plan still lists every gap in rule
order, with a jump on each one to the page where you can do it by hand.
You can try Pro free for 30 days.
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
