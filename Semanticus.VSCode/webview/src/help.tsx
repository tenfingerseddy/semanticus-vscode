import { useEffect, useMemo, useState } from 'react';

// ===================================================================================================
// Per-tab in-context help — the USER GUIDE (Studio v2). A '?' in the header opens a slide-over with
// two views: "This tab" (a detailed guide for the ACTIVE tab: concepts, tasks with the real button
// names, gotchas, Pro notes) and "Where do I…?" (a searchable task → location index, because plenty
// of authoring lives OUTSIDE Studio — the Model tree, the Properties view, the command palette — and
// users look here first). Content is written against the actual components; keep it that way: when a
// tab's controls change, its entry here changes in the same PR.
// ===================================================================================================

interface HelpSection { h: string; body?: string; bullets?: string[] }
interface SeeAlso { label: string; tab?: string; hint?: string }
interface TabHelp {
  title: string;
  lead: string;
  start: string[];
  sections: HelpSection[];
  pro?: string;           // what's Pro-gated on this tab (omit when nothing is)
  tip?: string;
  seeAlso?: SeeAlso[];    // entries with a tab id render as clickable jumps
}

const HELP: Record<string, TabHelp> = {
  "diagram": {
    "title": "Diagram",
    "lead": "See how your tables connect. Arrange the map and create or edit the links between tables.",
    "start": [
      "Open a model, then expand a table to see its columns.",
      "Follow a line to see which columns connect two tables. Double-click it to review the relationship.",
      "To add a relationship, drag a column onto a matching column in another table."
    ],
    "sections": [
      {
        "h": "Understand the map",
        "bullets": [
          "A relationship lets a filter in one table affect another. For example, choosing a customer can narrow the sales shown in a report.",
          "One-to-many means one customer can have many sales. Cross-filter direction controls which way the filter travels. An inactive relationship is saved but is not used by default."
        ]
      },
      {
        "h": "Arrange and edit",
        "bullets": [
          "Use the layout buttons to arrange tables, or drag them yourself. Switch to Relationships for a list you can sort.",
          "All tables shows the whole model. New creates a custom diagram; Add tables lets you choose what appears. Removing a table from a custom diagram does not delete it from the model.",
          "Select a relationship line and press Delete to remove the relationship. Double-click the line to change its settings."
        ]
      },
      {
        "h": "Add tables and calculations",
        "bullets": [
          "Use the Model list in the VS Code sidebar to create tables, columns and measures. A measure is a calculation, such as total sales. Right-click a table to see the available actions.",
          "Select an item in the Model list to edit its name, description and other settings in Properties."
        ]
      }
    ]
  },
  "search": {
    "title": "Search",
    "lead": "Find names, descriptions and formulas across the model. Review replacements before changing anything.",
    "start": [
      "Type at least two characters in the search box.",
      "Select a result to find the item and its settings. Turn on Include DAX & M to search formulas and data-loading code.",
      "To change text, enter a replacement and preview one result, or use Replace all to prepare a Change Plan."
    ],
    "sections": [
      {
        "h": "Narrow your search",
        "bullets": [
          "Match case distinguishes upper- and lowercase letters. Whole word finds complete words. Regular expression is an optional pattern search for advanced users.",
          "The result-type filters change which results you see. They do not narrow the changes prepared by Replace all."
        ]
      },
      {
        "h": "Review replacements",
        "bullets": [
          "Replace all prepares changes for review in Change Plan. It does not apply them immediately.",
          "Check each proposed change. Some fields are read-only or need a dedicated editor; the result explains why they cannot be replaced here."
        ]
      }
    ]
  },
  "lineage": {
    "title": "Lineage & Impact",
    "lead": "See what a calculation uses and what relies on it. Check the likely effect of changing or removing a field.",
    "start": [
      "Find a measure or column using the search box.",
      "Use Tree to follow its inputs, or Impact to see what depends on it.",
      "Check Published reports before treating an unused field as a removal candidate."
    ],
    "sections": [
      {
        "h": "Choose a view",
        "bullets": [
          "Graph draws the connections. Click connected items to follow the chain; Clear pins resets the selection.",
          "Tree shows dependencies as a list. Upstream means the inputs an item uses. Downstream means the items that use it.",
          "Impact lists the calculations and relationships a change could affect."
        ]
      },
      {
        "h": "Interpret removal advice",
        "bullets": [
          "A field with no model dependencies may still be used by a report. Published reports adds the report usage that Semanticus can inspect.",
          "Read the coverage and uncertainty notes with each result. Missing report access or an expression that could not be understood leaves an information gap.",
          "Review the listed dependents before deleting. The result covers the sources that were checked, not every possible external consumer."
        ]
      }
    ]
  },
  "daxlab": {
    "title": "DAX Lab",
    "lead": "Try calculations, compare answers and investigate slow queries. Experiments here do not change the saved model.",
    "start": [
      "Choose a test model. In Visual mode, add a calculation to Values and a field such as Customer or Product to Rows.",
      "Run the query to see the result. Use Performance to measure its speed.",
      "For a rewritten formula, open Verify and compare the original with the candidate across relevant fields."
    ],
    "sections": [
      {
        "h": "Build a query",
        "bullets": [
          "DAX is the formula language used by Power BI models. A query asks the model for a result.",
          "Visual mode builds the query from your selected fields. Query mode lets you edit the DAX directly. Hover a result to see its filter context: the values, such as year or region, that were used to calculate it.",
          "The model used for queries can differ from the model you are editing. Check the Testing details at the bottom of Studio."
        ]
      },
      {
        "h": "Understand performance",
        "bullets": [
          "Quick measures elapsed time without clearing cached results. Cold / Warm compares a run after clearing the cache with repeat runs that can reuse earlier work.",
          "Profile separates calculation work (Formula Engine) from reading stored data (Storage Engine) when the server provides those details. Missing trace events are missing evidence, not proof that no data was read.",
          "Trace setup time is separate from query time. Plan shows the server's execution steps when available. Clearing a shared server's cache can slow other users' next queries; the existing confirmation explains that effect."
        ]
      },
      {
        "h": "Compare answers",
        "bullets": [
          "In Verify, enter Original and Candidate formulas. Choose fields from tables that matter to the calculation, such as Customer and Product, and include totals and edge cases.",
          "A match supports the contexts actually checked. Empty, partial or unavailable checks do not establish that the rewrite is correct everywhere.",
          "An intended change to the business rule needs a known-answer test in Tests. It should not be expected to match the old formula."
        ]
      },
      {
        "h": "Inspect intermediate values and save a change",
        "bullets": [
          "Debug records values from EVALUATEANDLOG(expression, \"label\") in a query. Log each measure adds logging for selected measures. No captured output means there is no debug result to inspect.",
          "To keep a formula, edit the measure in the Model list or use a Change Plan. A local edit does not update the live test model until you publish it."
        ]
      }
    ],
    "seeAlso": [
      {
        "label": "Test a known business answer",
        "tab": "tests"
      },
      {
        "label": "Review and apply changes",
        "tab": "optimize"
      }
    ]
  },
  "data": {
    "title": "Data",
    "lead": "Browse a sample of the rows stored in a live model. Use this to understand the values behind your reports.",
    "start": [
      "Choose a test model with a live connection.",
      "Select a table from the list.",
      "Choose how many rows to display and review the returned sample."
    ],
    "sections": [
      {
        "h": "What the sample tells you",
        "bullets": [
          "This is a limited sample, not every row in the table. A value missing from the sample may still exist elsewhere.",
          "The preview reads loaded model data. It does not edit rows or run the data-loading steps."
        ]
      },
      {
        "h": "If no rows appear",
        "bullets": [
          "Check the Testing connection at the bottom of Studio and any message above the preview. A model opened from a file can be edited without having a live server to query.",
          "Use DAX Lab to ask a specific question or filter the data. Use M Code to change how data is loaded at the next refresh."
        ]
      }
    ]
  },
  "stats": {
    "title": "Storage",
    "lead": "Find the tables and columns using the most model memory. Review opportunities to reduce their size.",
    "start": [
      "Connect the model you want to measure, then choose Scan storage.",
      "Select a large table or column to see its breakdown.",
      "Review the suggested action, then refresh and scan again after a change to measure its effect."
    ],
    "sections": [
      {
        "h": "Read the breakdown",
        "bullets": [
          "Data is the space used to store values. Dictionary is the list of unique values. Hash indexes help the model look values up.",
          "A column with many different values can use more memory. A size scan alone does not tell you the exact number of different values.",
          "Before-and-after numbers compare measured scans. A smaller number is not automatically a saving caused by your edit."
        ]
      },
      {
        "h": "Know what was measured",
        "bullets": [
          "Without a live connection, the page can show model structure but cannot measure live memory use.",
          "For Direct Lake, the scan reports data currently held in memory. That is not the complete size of the model.",
          "Read the dependency check before removing a column. If its usage is unknown, the removal action is unavailable."
        ]
      }
    ]
  },
  "spec": {
    "title": "Model Spec",
    "lead": "Plan the tables, relationships and calculations before building them. The draft stays separate from the model until you build it.",
    "start": [
      "Choose a starting point: the open model, an empty draft, a SQL source or a saved Model Spec.",
      "Review and edit the tables, columns, relationships and measures in the draft.",
      "Save the draft, then choose Build into model when you want to add the reviewed items."
    ],
    "sections": [
      {
        "h": "Choose or change a starting point",
        "bullets": [
          "A Model Spec is a saved model plan. Drafting from an existing model reads its structure. Drafting from SQL reads table and column information; it does not copy source data.",
          "Use Back to start to return to the choices. Save any draft you want to keep before replacing it.",
          "Open spec loads a saved draft. Save spec exports a JSON file, a text format that you can keep with the project."
        ]
      },
      {
        "h": "Understand the building blocks",
        "bullets": [
          "A fact table usually holds events or transactions, such as sales. A dimension table describes things such as products or customers. A date table supports reporting over time.",
          "A key identifies a row. A relationship connects columns in different tables. A measure calculates a result, such as sales after discounts."
        ]
      },
      {
        "h": "Build and continue",
        "bullets": [
          "Build into model adds the reviewed objects as one undoable model change. It does not publish or load source data.",
          "Review the build result, then use Diagram and Tests to check the model. Publish is a separate action in Deploy."
        ]
      }
    ]
  },
  "advmodels": {
    "title": "Advanced Modelling",
    "lead": "Build reusable calculations, calendars and report choices, or control which data people can see.",
    "start": [
      "Choose the feature you need from the tabs on this page.",
      "Use its form to pick the affected tables or fields and enter the required settings.",
      "Review the result in the Model list. Test calculations and access rules before publishing."
    ],
    "sections": [
      {
        "h": "Simplify reports",
        "bullets": [
          "Perspectives are named subsets of a model. They make field lists easier to browse; they are not a security boundary.",
          "Field parameters let a report reader switch the field shown in a visual. Select the fields in the order you want them to appear."
        ]
      },
      {
        "h": "Reuse calculations and dates",
        "bullets": [
          "Calculation groups apply a shared calculation to different measures, such as year-to-date sales and year-to-date costs. Add a group, then add its calculation items.",
          "Calendars define date periods such as fiscal years and weeks. Choose a template and its settings. Some features need a newer model compatibility level; read the upgrade message before making that one-way change.",
          "DaxLib offers reusable DAX functions. Preview a package before installing it and its required dependencies."
        ]
      },
      {
        "h": "Control data access",
        "bullets": [
          "Row-level security (RLS) limits which rows a role can see. Object-level security (OLS) hides entire tables or columns.",
          "Create a role, choose its members and set its filters. An empty row filter does not restrict rows. Test roles against a live model to check what they actually expose."
        ]
      }
    ]
  },
  "mcode": {
    "title": "M Code",
    "lead": "Edit Power Query steps that load and transform data. Changes take effect when the model is refreshed.",
    "start": [
      "Choose a table and the query or shared expression you want to edit.",
      "Edit the Power Query formula, or use the column actions to add a transformation.",
      "Save the query. Publish and refresh through your normal process to load the changed data."
    ],
    "sections": [
      {
        "h": "Edit a query",
        "bullets": [
          "M is Power Query's formula language. A partition is a section of a table with its own loading query. A shared expression is a reusable query or parameter.",
          "The editor offers suggestions, formatting and error markers. Applied steps lets you jump to, rename or remove a step.",
          "Column actions such as rename, filter and change type write a new step into the editor."
        ]
      },
      {
        "h": "Understand the preview",
        "bullets": [
          "The sample shows data already loaded in the live model. It does not run each edited step, so it will not immediately reflect an unsaved or unrefreshed change.",
          "Save updates the query definition. It does not run a refresh. You can edit the definition without a live connection."
        ]
      },
      {
        "h": "Set up incremental refresh",
        "bullets": [
          "Incremental refresh reloads a recent time window instead of every row. Set how much history to keep and how much recent data to refresh.",
          "The checklist explains required date parameters and filters. Save policy checks those settings and can create the required parameters and filter.",
          "Hybrid mode combines stored data with live queries for the newest rows. The page shows whether the model supports it."
        ]
      }
    ]
  },
  "optimize": {
    "title": "Change Plan",
    "lead": "Review proposed edits together before applying them. See each change and choose which ones to keep.",
    "start": [
      "Choose Analyse model, or send findings here from AI Readiness or Best practices.",
      "Review the before-and-after values. Fill in missing text and select the items you want.",
      "Apply the selected changes and read the result. Use Edits if you need to undo an applied batch."
    ],
    "sections": [
      {
        "h": "Prepare a plan",
        "bullets": [
          "A plan is a list of proposed changes. Creating or editing the plan does not change the model.",
          "Your AI Assistant can work on the same plan. Its changes appear here. The AI Assistant sees your changes on its next call.",
          "Needs content means a value still needs to be written, such as a useful business description. Ask AI prepares a prompt for your own assistant."
        ]
      },
      {
        "h": "Read the evidence",
        "bullets": [
          "Review the actual change, not just its label. A verified formula has been compared in the recorded test contexts; that is not a guarantee for every possible input.",
          "Items you reject are left out. Read applied, skipped and failed counts after applying; selecting an item is not evidence that it was changed."
        ]
      },
      {
        "h": "Save the result",
        "bullets": [
          "An applied batch becomes one entry in Edits and can be undone together. Saving to a file and publishing to a live model are separate actions."
        ]
      }
    ],
    "pro": "Reviewing plans is free. Applying a batch in one step is Pro; individual edits remain available on Free."
  },
  "readiness": {
    "title": "AI Readiness",
    "lead": "Find missing context and model settings that can make AI answers less useful. Review the findings and improve them.",
    "start": [
      "Read the grade, coverage notes and most important findings.",
      "Open a finding to see the affected item and the suggested change.",
      "Apply a supported fix, write the missing explanation, or collect changes in a Change Plan."
    ],
    "sections": [
      {
        "h": "Understand the score",
        "bullets": [
          "The grade summarises the checks that apply to this model. A check with nothing relevant to examine does not increase the score.",
          "A higher grade means fewer issues were found by these checks. It does not prove that every AI answer will be correct.",
          "Read any incomplete-data or grade-limit message alongside the number."
        ]
      },
      {
        "h": "Choose an action",
        "bullets": [
          "Apply makes the offered model change. Review it first. Ask AI copies a prompt containing the relevant model context for your own assistant.",
          "Descriptions should explain business meaning, not just repeat a field name. Tests can check whether important business questions get the expected answers.",
          "Waive means accept a finding without fixing it. The reason is recorded and the finding remains visible. Remove the waiver when it should count again."
        ]
      },
      {
        "h": "Add your own checks",
        "bullets": [
          "Custom rules let you check your organisation's naming or modelling requirements. Preview the findings before saving a rule.",
          "Saved rules travel with the model. They do not replace the built-in limits on the readiness grade."
        ]
      }
    ],
    "pro": "Single fixes and individual waivers are free. Bulk fixes and waiving a whole rule are Pro.",
    "seeAlso": [
      {
        "label": "Check known business answers",
        "tab": "tests"
      }
    ]
  },
  "bpa": {
    "title": "BPA: Best Practice Analyzer",
    "lead": "Check the model for common design, naming and performance issues. Review each finding before deciding how to address it.",
    "start": [
      "Read the findings and select an affected item.",
      "Use its suggested fix or Ask AI for help with a change that needs judgement.",
      "Review several changes in a Change Plan, or accept an intentional exception with a recorded reason."
    ],
    "sections": [
      {
        "h": "What these checks mean",
        "bullets": [
          "BPA stands for Best Practice Analyzer. Its rules flag patterns worth reviewing; a finding is not always a broken model.",
          "Checks run when the model opens and when it changes. They examine model definitions, not every result returned by live data."
        ]
      },
      {
        "h": "Fix or accept a finding",
        "bullets": [
          "Fix applies the offered change. Ask AI prepares a prompt for your own assistant. Review as a plan collects proposed fixes before you apply them.",
          "Waive accepts a finding with a reason and keeps it visible. Waive rule accepts that rule throughout the model. You can reverse a waiver later."
        ]
      },
      {
        "h": "Custom rules",
        "bullets": [
          "Use a template to create your own naming or property checks. Preview what the rule would flag before saving it.",
          "Custom rules are saved with the model. Reusing a standard rule ID replaces that rule; the preview explains this before you save."
        ]
      }
    ],
    "pro": "Single fixes and individual waivers are free. Bulk fixes and waiving a whole rule are Pro."
  },
  "tests": {
    "title": "Tests",
    "lead": "Check that calculations return expected answers, tables connect correctly and access rules behave as intended.",
    "start": [
      "Choose a live test model. Select New test to save an answer you already trust, or run the built-in relationship and access checks.",
      "Choose whether to run everything, the current section or selected tests.",
      "Read passed, failed and untested results. Use Run + record to keep a complete run, or Report to review and export it."
    ],
    "sections": [
      {
        "h": "Choose a useful test",
        "bullets": [
          "A known-answer test compares a calculation with a value you trust. Use an independent business report or source calculation for the expected value.",
          "A source comparison, also called reconciliation, checks model results against a SQL query. It needs both the model connection and access to the source SQL server.",
          "Model Interview saves business questions and expected answers. Include meaningful product, customer, region and time examples, not only different months."
        ]
      },
      {
        "h": "Connect to source data",
        "bullets": [
          "For a SQL comparison, review the suggested source, then enter the server, database and sign-in method. Your model connection alone does not provide SQL access.",
          "Review generated SQL before accepting it. A query based on the same faulty logic as the model is not an independent check."
        ]
      },
      {
        "h": "Read the outcome",
        "bullets": [
          "Passed means the check ran and met its expected result. Failed means it found a difference or problem. Untested or skipped means there is no result to rely on.",
          "A partial run covers only your chosen tests and does not receive a whole-model grade. If the model changes, run tests again before relying on the old results.",
          "Relationship checks look for data that does not join as expected. Security checks review saved filters and visibility settings. They do not sign in as each user or prove live row access."
        ]
      }
    ],
    "pro": "Running checks is free. Recording a complete run and exporting reports are Pro.",
    "seeAlso": [
      {
        "label": "Review saved reports",
        "tab": "evidence"
      },
      {
        "label": "Compare two DAX formulas",
        "tab": "daxlab"
      }
    ]
  },
  "evidence": {
    "title": "Evidence",
    "lead": "Review saved test and workflow reports. See what was checked, when it ran and which model it describes.",
    "start": [
      "Select a saved report from the list.",
      "Read its outcome, model details and the checks that actually ran.",
      "For an up-to-date result after a model change, return to Tests or Workflows and run the relevant checks again."
    ],
    "sections": [
      {
        "h": "Use a saved report",
        "bullets": [
          "A report is a snapshot of a completed check. Opening it does not run the tests again or change the model.",
          "Read failed, skipped and incomplete sections as well as the summary. A report can be complete as a record while documenting an unsuccessful check."
        ]
      },
      {
        "h": "If the list is empty",
        "bullets": [
          "Run checks in Tests or Workflows and use their report-saving controls. Reports saved with the current model appear here.",
          "A report from another time or model is useful history, but it does not prove the current model is correct."
        ]
      }
    ],
    "seeAlso": [
      {
        "label": "Run model tests",
        "tab": "tests"
      },
      {
        "label": "Run a workflow",
        "tab": "workflows"
      }
    ]
  },
  "deploy": {
    "title": "Deploy",
    "lead": "Publish reviewed changes to a live model, restore an earlier version or move a model between release stages.",
    "start": [
      "Check the destination and account, then select Publish to review the proposed changes.",
      "Read the change list and any failed checks. Confirm only when the destination and changes are the ones you intend.",
      "Read the publishing result. Refresh data separately if your change needs new data to load."
    ],
    "sections": [
      {
        "h": "Save and publish are different",
        "bullets": [
          "Save keeps your local model or editor changes. Publish sends model design changes to the live destination used by reports.",
          "The editing model, test model and publishing destination can differ. The bottom bar identifies each one.",
          "Choose what to publish opens a comparison so you can review selected changes."
        ]
      },
      {
        "h": "Restore or move a release",
        "bullets": [
          "Roll back lets you choose a saved restore point and review what will be restored, removed or left in place before confirming.",
          "Promote moves items between release stages, such as development, testing and production. Review the source, destination and preview before deploying.",
          "A failed check may offer an override with a recorded reason. Read the specific failure to decide whether that is appropriate."
        ]
      },
      {
        "h": "Advanced delivery",
        "bullets": [
          "Advanced contains source-control tools, Fabric Git, automation setup and Data Agent publishing. Use these when your project needs that delivery route.",
          "Publishing changes model definitions. It does not refresh the data. Local Undo does not reverse a completed write to the live server."
        ]
      }
    ]
  },
  "compare": {
    "title": "Review changes",
    "lead": "Compare two versions of a model and choose which differences to apply.",
    "start": [
      "Choose a source and target, then select Review.",
      "Open a difference to compare the old and new values.",
      "Select the changes you want, validate the selection and review the result before applying."
    ],
    "sections": [
      {
        "h": "Read differences",
        "bullets": [
          "Create adds an item, Update changes it and Delete removes it. Most items are matched by name, so a rename can appear as a deletion and an addition.",
          "Validation checks whether the selected changes can be applied together. A missing table or other required item may need to be included."
        ]
      },
      {
        "h": "Choose where changes go",
        "bullets": [
          "Merging into the open model creates an undoable batch. Applying changes to a file writes that file; use your saved copies or source control to recover earlier versions.",
          "Comparing models does not publish them. Use Publish when you are ready to update a live destination.",
          "When reviewing a live publishing destination, read the final confirmation card. Applying that selection writes to the server; it is not reversed by local Undo."
        ]
      }
    ]
  },
  "permissions": {
    "title": "Permissions",
    "lead": "Choose what your AI Assistant may do without asking and review requests waiting for your decision.",
    "start": [
      "Read the current setting and the actions listed in the permission table.",
      "Choose a preset or adjust the allowed actions for each environment.",
      "For a waiting request, check the action, destination and approval scope before allowing or declining it."
    ],
    "sections": [
      {
        "h": "Read the table",
        "bullets": [
          "Allow lets the assistant perform the action. Ask requires an approval. Deny refuses the action.",
          "Local is work on this machine. Dev is a development environment. UAT is a test environment used before release. Prod is production, where people use the published model.",
          "Label your saved destinations correctly so the intended column of the table applies."
        ]
      },
      {
        "h": "Understand an approval",
        "bullets": [
          "A request names the proposed action and destination. Read its description rather than approving from the action name alone.",
          "Approval to read rows can cover several read operations for the stated time. Other approvals apply to the described action; the request explains the scope."
        ]
      },
      {
        "h": "Choose the amount of control",
        "bullets": [
          "The main switch turns these assistant permission checks on or off. Workflow checks are managed separately in Workflows → Governance.",
          "These settings help prevent accidental actions through Semanticus. They do not replace the access rights of the account connected to your data."
        ]
      }
    ]
  },
  "docs": {
    "title": "Docs",
    "lead": "Create a readable guide to the model, with its structure, calculations and your business explanations.",
    "start": [
      "Choose the sections to include in the document.",
      "Add a title, branding and explanations of the model, tables or measures.",
      "Preview the document, then export it or use Print / PDF to share it."
    ],
    "sections": [
      {
        "h": "Choose useful detail",
        "bullets": [
          "Include the tables, calculations, relationships and checks your reader needs. Hidden fields and formulas can be included when the audience needs that detail.",
          "Generated content describes the model structure. Add business meaning, definitions and examples so a reader understands why it exists."
        ]
      },
      {
        "h": "Write explanations",
        "bullets": [
          "Choose the model, a table or a measure and write the relevant explanation. Markdown is a simple text format for headings, lists and links.",
          "These explanations are saved with the model and are separate from the Description field in Properties. Ask AI prepares a prompt for your own assistant."
        ]
      },
      {
        "h": "Share the result",
        "bullets": [
          "HTML creates a formatted page; Markdown creates an editable text document. Print / PDF opens a print view.",
          "Check the preview before sharing. The document reflects the model state used to generate it."
        ]
      }
    ]
  },
  "history": {
    "title": "Edits",
    "lead": "See changes made by you and your AI Assistant. Undo recent model edits or review the saved audit record.",
    "start": [
      "Find the change you want to inspect in the timeline.",
      "Use Undo last to reverse the latest change, or Redo to restore an undone change.",
      "Read the audit trail for saved checks and publishing decisions that continue across sessions."
    ],
    "sections": [
      {
        "h": "Undo model edits",
        "bullets": [
          "The timeline is shared by you and your assistant. Undo to here reverses the chosen change and every later change, not just one item in the middle.",
          "A batch, such as an applied Change Plan, is one timeline entry and is undone together.",
          "This timeline covers the current session. Local Undo does not reverse every remote action, such as publishing to a server."
        ]
      },
      {
        "h": "Read the audit trail",
        "bullets": [
          "The saved audit trail records checked edits and other recorded actions. Its integrity check looks for a changed or missing link in the record.",
          "A check badge appears only when evidence is attached. An entry without a badge was not necessarily tested.",
          "Details contains the evidence behind the summary. Integrity of the record does not itself prove the underlying model is correct."
        ]
      }
    ],
    "pro": "The timeline, undo and redo are free. Exporting the audit trail is Pro."
  },
  "workflows": {
    "title": "Workflows",
    "lead": "Follow a repeatable set of steps for a modelling task. Use a built-in workflow or create one for your project.",
    "start": [
      "Choose a task on Home, or browse Library to find a workflow.",
      "Read its steps, then start a run. Runs lets you continue work already started by you or your assistant.",
      "Follow the instructions, submit each step and review its result before continuing."
    ],
    "sections": [
      {
        "h": "Find your way around",
        "bullets": [
          "Home offers starting tasks. Library lists saved workflows. Runs shows work in progress and past results.",
          "Governance contains project profiles and workflow controls. A profile is a saved set of choices about which workflows and checks apply.",
          "Author lets you create or edit a workflow. Customise makes an editable copy of a built-in workflow."
        ]
      },
      {
        "h": "Understand a step",
        "bullets": [
          "An action is a task the model tools can carry out. Instructions explain what to do. A gate is a check attached to a step.",
          "A required check can stop the run when it fails. A warning records the problem and allows progress. Off skips that check. Read the result rather than treating a submitted answer as a passed test.",
          "Skipping or stopping a run keeps the recorded outcome; it does not turn an incomplete check into a pass."
        ]
      },
      {
        "h": "Build a workflow",
        "bullets": [
          "Use Author to edit steps, instructions and checks. The saved source is the workflow definition and can be kept with your project files.",
          "Canvas helps you see and arrange the sequence. Edit the steps or saved source to change what the workflow does; moving a box alone does not create a new action."
        ]
      },
      {
        "h": "Use project controls",
        "bullets": [
          "In Governance, choose a project profile to load its workflow settings. Review the resulting controls, including required workflows and whether checks are enforced.",
          "Turning enforcement off skips checks for new runs. Runs already underway keep the setting they started with. This is separate from assistant permissions."
        ]
      }
    ],
    "pro": "Reading, designing and following workflows manually is free. Running workflows with enforced checks is Pro.",
    "seeAlso": [
      {
        "label": "Review saved reports",
        "tab": "evidence"
      },
      {
        "label": "Set assistant permissions",
        "tab": "permissions"
      }
    ]
  },
  "knowledge": {
    "title": "Primer",
    "lead": "Keep the business context that helps you and your AI Assistant understand this model.",
    "start": [
      "Read the model overview and saved business context.",
      "Add or correct definitions, important calculations and known issues.",
      "Review supporting insights when the model or its business rules change."
    ],
    "sections": [
      {
        "h": "What a Primer is",
        "bullets": [
          "A Primer is a short introduction to the model. Explain what it covers, what common terms mean and which calculations people should use.",
          "Write for someone new to the project. For example, define whether revenue includes tax or whether customer counts include inactive accounts."
        ]
      },
      {
        "h": "Keep knowledge accurate",
        "bullets": [
          "Saved insights are reusable notes from earlier work. Their importance and usage help find relevant notes; those numbers do not prove that a note is correct.",
          "Correct a note when the business rule changes. Keep facts separate from guesses and unresolved questions.",
          "Your own assistant helps write and review this context. Semanticus stores and retrieves it; it does not generate answers by itself."
        ]
      },
      {
        "h": "Use saved lessons and workflows",
        "bullets": [
          "Insights are saved lessons. Edit their text and search terms, or adjust their importance. Review a suggested Primer addition before selecting Accept.",
          "Learned workflows are reusable processes saved from earlier work. Check confirms that the definition can be read; Replay check tries the saved examples without changing the model.",
          "Recall searches for lessons relevant to your next task. Delete saved knowledge removes lessons from future use for the chosen scope; it keeps the stored history."
        ]
      }
    ],
    "seeAlso": [
      {
        "label": "Save business questions as tests",
        "tab": "tests"
      },
      {
        "label": "Write a full model document",
        "tab": "docs"
      }
    ]
  },
  "dataagent": {
    "title": "Data Agent",
    "lead": "Set up a Fabric Data Agent so people can ask questions about selected model data through their own assistant.",
    "start": [
      "Choose the Fabric account and workspace, then select or create an agent.",
      "In Scope, choose its data sources. In Teach, add the business instructions it needs.",
      "Review changes before applying them. Publish when ready, then copy the connection details for your assistant."
    ],
    "sections": [
      {
        "h": "Choose and explain the data",
        "bullets": [
          "Scope controls which model data is offered to the agent. Review included and excluded fields before saving the draft.",
          "Teach contains instructions such as preferred measures, business definitions and how to handle ambiguous questions.",
          "Read the support notes for example questions. Saving an example in the definition does not guarantee that Fabric uses it when answering."
        ]
      },
      {
        "h": "Publish and connect",
        "bullets": [
          "Publishing makes the reviewed agent available to its consumers while you can continue editing its draft.",
          "The connection card provides an MCP endpoint, the address your assistant uses to reach the published agent. Semanticus itself does not ask the agent questions.",
          "Deleting an agent removes the Fabric item. It is a remote action and cannot be reversed with model Undo."
        ]
      }
    ],
    "pro": "Automatically adding the open model as a source is Pro. Manually configuring a source remains available on Free."
  }
};

// ---------------------------------------------------------------------------------------------------
// "Where do I…?" — the task → location index. Much of authoring is NATIVE VS Code (the Model tree,
// the Properties view, the palette), and users look in Studio first — this is the map. Keep entries
// verified against package.json/extension.ts.
// ---------------------------------------------------------------------------------------------------

interface WhereEntry { q: string; a: string; tab?: string }
const WHERE: { group: string; items: WhereEntry[] }[] = [
  {
    group: 'Start here',
    items: [
      { q: 'I am new to Semanticus. Where should I begin?', a: 'Open a model from the Semanticus sidebar. Read Primer for its business context, explore Diagram, then select a calculation in the Model list. Each Studio page has Getting started steps and a detailed Help guide.', tab: 'knowledge' },
      { q: 'What is the difference between editing, testing and publishing?', a: 'Editing changes your working model. Testing asks a live model for answers. Publishing sends reviewed changes to a live destination. These can be different models; check the bottom bar and use Connections to change them.' },
      { q: 'Can I work without a live connection?', a: 'Yes. Open model files to inspect and edit their structure, formulas and descriptions. Running queries, measuring performance and testing real answers needs a live model with data.' },
      { q: 'Give my AI Assistant the Semanticus guides', a: 'Run Semanticus: Install or Update Assistant Skills from the VS Code command palette. Choose your assistant, then refresh or restart it if needed. These guides use your existing Semanticus connection.' },
      { q: 'Test a business answer against a number I trust', a: 'Tests → New test. Choose a known answer and enter the calculation, expected value and the filters the answer applies to.', tab: 'tests' },
      { q: 'Compare model results with source data', a: 'Tests → New test → source comparison. Review the SQL that supplies the expected answer and connect to its server and database. This needs a SQL connection as well as the live model.', tab: 'tests' },
      { q: 'Review saved test and workflow reports', a: 'Evidence lists the reports saved with this model. Open one to read its results, date and coverage.', tab: 'evidence' },
      { q: 'Change when the assistant asks for permission', a: 'Permissions shows the current settings and waiting requests. Workflow requirements are a separate choice under Workflows → Governance.', tab: 'permissions' },
    ],
  },
  {
    group: 'Common terms explained',
    items: [
      { q: 'Semantic model, table, column and measure', a: 'A semantic model organises data and business calculations for reports. Tables contain rows; columns hold values such as date or customer. A measure calculates an answer, such as total sales.' },
      { q: 'DAX and filter context', a: 'DAX is the formula language for model calculations. Filter context is the set of values used for an answer, such as sales for a particular product, region and year.', tab: 'daxlab' },
      { q: 'Power Query, M and partition', a: 'Power Query prepares data for loading. M is its formula language. A partition is a section of a model table with its own loading query.', tab: 'mcode' },
      { q: 'XMLA endpoint, SQL server and tenant', a: 'An endpoint is a connection address. XMLA connects to a semantic model; SQL connects to source tables. A tenant identifies the Microsoft organisation your account signs into.' },
      { q: 'Workflow, gate and project profile', a: 'A workflow is a saved sequence of steps. A gate is a check attached to a step. A project profile saves which workflows are available or required and how their checks apply.', tab: 'workflows' },
      { q: 'Evidence, coverage and reconciliation', a: 'Evidence is the recorded result of a check. Coverage says what was checked and what was left out. Reconciliation compares model results with an independent source answer.', tab: 'tests' },
      { q: 'BPA, finding and waiver', a: 'BPA means Best Practice Analyzer. A finding is an issue raised by a check. A waiver records your decision to accept that issue without fixing it.', tab: 'bpa' },
      { q: 'MCP and assistant skills', a: 'MCP is the connection that lets your AI Assistant use Semanticus tools. A skill is a guide that helps it choose and use those tools for a task.' },
    ],
  },
  {
    group: 'Author (the Model tree: the side bar, not Studio)',
    items: [
      { q: 'Create a measure, calculated column, calculated table, calculation item, or function', a: 'Model tree → right-click a table (or a calculation group) → "New Measure/Calculated Column/…". The object is created at once and its editor opens with the name as the first line. Rename it there or edit the DAX, then Ctrl+S saves. A rename and the DAX are two tracked changes, applied in order (the rename first). New tables via the Model view "…" menu. Data columns come from the source/M, not the tree.' },
      { q: 'Create a hierarchy', a: 'Model tree → right-click a table → "New Hierarchy…" (name, then the level columns), or multi-select the level columns → "New Hierarchy from Selected Columns…".' },
      { q: 'Edit a measure\'s DAX', a: 'Click the measure in the Model tree; a real DAX editor opens (autocomplete, hover, F12, format). Ctrl+S saves back to the model.' },
      { q: 'Set a description or display folder', a: 'Select the object; the Properties view follows the selection. Edit Description (multiline) or DisplayFolder there. For folders you can also right-click a measure/column → "Move to Folder…".' },
      { q: 'Set a format string', a: 'Right-click the measure → "Set Format String…" (presets + custom), or the FormatString property in the Properties view.' },
      { q: 'Rename or delete', a: 'Select it, then F2 to rename in the Properties Name row (DAX references and Q&A synonyms follow automatically), or right-click → "Delete" (references are NOT rewritten, so check dependents first).' },
      { q: 'Create a relationship', a: 'Diagram tab: drag column → column. Or Model tree: right-click the many-side column → "New Relationship from Column…" and pick the lookup column from the list.', tab: 'diagram' },
      { q: 'Bulk-edit DAX or TMDL as a script', a: 'Right-click object(s) → "Script ▸ DAX/TMDL (editable)": edit the script, then the ▶ "Apply" button in the editor title writes it back as one undoable batch.' },
      { q: 'Edit several objects at once', a: 'Multi-select in the Model tree; the Properties view switches to multi-edit (shared properties; one change applies to all).' },
    ],
  },
  {
    group: 'Build & analyze (Studio)',
    items: [
      { q: 'Field parameters, calc groups, calendars, perspectives, RLS/OLS, DaxLib', a: 'Advanced Modelling: six guided builders (also reachable from the Model view "…" → Advanced Modelling submenu).', tab: 'advmodels' },
      { q: 'Try a calculation and see which filters affect its answer', a: 'DAX Lab: Visual or Query mode against a live engine.', tab: 'daxlab' },
      { q: 'Check whether two DAX formulas return the same answers', a: 'DAX Lab → Verify. Choose relevant fields, compare both formulas and read which contexts were checked.', tab: 'daxlab' },
      { q: 'Benchmark cold vs warm', a: 'DAX Lab → Performance → "Cold / Warm".', tab: 'daxlab' },
      { q: 'Edit M / applied steps / incremental refresh', a: 'M Code (or right-click a table → "Edit M Code").', tab: 'mcode' },
      { q: 'Preview table rows', a: 'Data tab (or right-click a table → "Preview Data").', tab: 'data' },
      { q: 'Find what depends on a field / what\'s safe to remove', a: 'Lineage & Impact (or right-click → "Show Lineage & Impact").', tab: 'lineage' },
      { q: 'See where storage goes', a: 'Storage: "Scan storage" against a live engine, for ranked consumers + opportunities.', tab: 'stats' },
      { q: 'Refresh a partition', a: 'Model tree → expand the table → right-click the partition → "Refresh Partition…" (pick a refresh type, dry-run, confirm).' },
    ],
  },
  {
    group: 'Improve & ship',
    items: [
      { q: 'Score & fix AI readiness', a: 'AI Readiness: one-click safe fixes, ready AI prompts for the rest.', tab: 'readiness' },
      { q: 'Best-practice violations', a: 'Best practices (BPA): fix one finding, apply a batch with Pro or review changes in a Change Plan.', tab: 'bpa' },
      { q: 'Review a batch of changes before applying', a: 'Change Plan: approve per item, apply as one undoable step.', tab: 'optimize' },
      { q: 'Publish the open model to a live workspace', a: 'Press the Publish chip in the status bar, or Ship > Deploy > Publish. One card names the changes, then you confirm. Ctrl+S never publishes.', tab: 'deploy' },
      { q: 'Compare model versions and combine selected changes', a: 'Deploy → Choose what to publish can review any two supported model sources.', tab: 'deploy' },
      { q: 'Copy objects from another model', a: 'The Reference Model view (side bar): "Set Reference Model…", then right-click → "Copy into Open Model" (or Ctrl+C there, Ctrl+V in the Model tree).' },
      { q: 'Generate documentation', a: 'Docs: compose, brand, print to PDF, plus the authored narrative layer.', tab: 'docs' },
      { q: 'Ship a Fabric Data Agent', a: 'Deploy → Advanced → Data Agent: scope from this model, teach it, publish.', tab: 'dataagent' },
    ],
  },
  {
    group: 'AI Assistant & safety',
    items: [
      { q: 'Connect the AI Assistant to this model', a: 'Command palette → "Semanticus: Connect AI Assistant" writes the connection settings. Refresh or restart your assistant so it reads them. Keep Semanticus open in VS Code.' },
      { q: 'See what the AI Assistant changed', a: 'Edit History: every change attributed on one timeline; "Undo to here" rolls back.', tab: 'history' },
      { q: 'Undo / redo', a: 'Edit History buttons, or the palette: "Semanticus: Undo" / "Semanticus: Redo". A batch undoes as one step. Publishing to a live server is a separate action and is not reversed by local Undo. Publishing to a live server is a separate action and is not reversed by local Undo. Publishing to a live server is a separate action and is not reversed by local Undo.', tab: 'history' },
      { q: 'Run a verified playbook', a: 'Workflows: gated steps verified with evidence; free to read, Pro to run enforced.', tab: 'workflows' },
      { q: 'Turn workflow enforcement off for a quick task', a: 'Workflows → Governance → Enforcement. Off skips checks for new runs. Existing runs keep their original setting.', tab: 'workflows' },
      { q: 'Save business context and lessons from earlier work', a: 'Primer: the model guide, saved insights, reusable workflows and a search for relevant notes.', tab: 'knowledge' },
      { q: 'Accept a finding without hiding it', a: 'Waive it (reason required) on AI Readiness or BPA; it stays surfaced forever under "⊘ Waived (accepted)".', tab: 'readiness' },
    ],
  },
  {
    group: 'Setup & housekeeping',
    items: [
      { q: 'Open or connect a model', a: '"Semanticus: Open Model…" (tree toolbar or palette): a file (.bim/TMDL/PBIP), a running Power BI Desktop, or an XMLA endpoint (recent connections remembered).' },
      { q: 'Search the whole model', a: '"Semanticus: Find in Model" (the search icon on the Model view): names, descriptions and DAX.' },
      { q: 'Save the model to disk', a: 'The Save icon on the Model view (TMDL beside the model).' },
      { q: 'Restart the engine', a: '"Restart Engine (rebuild)" on the Model view toolbar. The MCP door re-attaches automatically on its next call.' },
      { q: 'Activate / inspect a Pro license', a: 'Palette: "Semanticus: Activate License" / "Semanticus: Show License".' },
      { q: 'Format DAX', a: 'In any DAX editor: Format Document (offline), or right-click → "Semanticus: Format DAX with DAX Formatter (online)".' },
    ],
  },
];

// ---------------------------------------------------------------------------------------------------

function SectionBlock({ s }: { s: HelpSection }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-accent)' }}>{s.h}</div>
      {s.body && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>{s.body}</div>}
      {s.bullets && (
        <ul className="flex flex-col gap-1">
          {s.bullets.map((p, i) => (
            <li key={i} className="text-[12px] flex gap-2" style={{ color: 'var(--sem-muted)' }}>
              <span style={{ color: 'var(--sem-accent)' }}>•</span><span>{p}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function TabGuide({ h, onGo }: { h: TabHelp; onGo?: (tab: string) => void }) {
  return (
    <div className="flex flex-col gap-3.5">
      <div className="text-[12.5px]" style={{ color: 'var(--sem-fg)' }}>{h.lead}</div>
      <GettingStarted steps={h.start} />
      {h.sections.map((s, i) => <SectionBlock key={i} s={s} />)}
      {h.pro && (
        <div className="text-[11px] rounded-md px-2.5 py-2" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>
          <span className="font-semibold" style={{ color: 'var(--sem-accent)' }}>Pro · </span>{h.pro}
        </div>
      )}
      {h.tip && <div className="text-[11px] rounded-md px-2.5 py-2" style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-muted)' }}>{h.tip}</div>}
      {h.seeAlso && h.seeAlso.length > 0 && (
        <div className="flex flex-col gap-1">
          <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>Looking for…</div>
          {h.seeAlso.map((s, i) => (
            <div key={i} className="text-[12px] flex gap-2" style={{ color: 'var(--sem-muted)' }}>
              <span style={{ color: 'var(--sem-accent)' }}>→</span>
              <span>
                {s.tab && onGo
                  ? <button className="underline" style={{ color: 'var(--sem-accent)' }} onClick={() => onGo(s.tab!)}>{s.label}</button>
                  : <span style={{ color: 'var(--sem-fg)' }}>{s.label}</span>}
                {s.hint ? <span>: {s.hint}</span> : null}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function WhereIndex({ onGo }: { onGo?: (tab: string) => void }) {
  const [q, setQ] = useState('');
  const groups = useMemo(() => {
    const needle = q.trim().toLowerCase();
    if (!needle) return WHERE;
    return WHERE
      .map((g) => ({ ...g, items: g.items.filter((it) => (it.q + ' ' + it.a).toLowerCase().includes(needle)) }))
      .filter((g) => g.items.length > 0);
  }, [q]);
  return (
    <div className="flex flex-col gap-3">
      <input
        value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter tasks… e.g. measure, deploy, undo"
        className="w-full rounded-md px-2.5 py-1.5 text-[12px] outline-none"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}
      />
      {groups.length === 0 && <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No tasks match “{q}”.</div>}
      {groups.map((g) => (
        <div key={g.group} className="flex flex-col gap-1.5">
          <div className="text-[11px] font-semibold uppercase tracking-wide" style={{ color: 'var(--sem-accent)' }}>{g.group}</div>
          {g.items.map((it, i) => (
            <div key={i} className="flex flex-col gap-0.5 rounded-md px-2 py-1.5" style={{ background: 'var(--sem-surface-2)' }}>
              <div className="text-[12px] font-medium" style={{ color: 'var(--sem-fg)' }}>
                {it.tab && onGo
                  ? <button className="underline text-left" style={{ color: 'var(--sem-fg)' }} onClick={() => onGo(it.tab!)}>{it.q}</button>
                  : it.q}
              </div>
              <div className="text-[11.5px]" style={{ color: 'var(--sem-muted)' }}>{it.a}</div>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}

function GettingStarted({ steps }: { steps: string[] }) {
  return <ol className="list-decimal pl-5 flex flex-col gap-1.5 text-[12px] leading-relaxed" style={{ color: 'var(--sem-fg)' }}>
    {steps.map((step, index) => <li key={index}>{step}</li>)}
  </ol>;
}

/** Keep the purpose visible; longer instructions expand only when the user needs them. */
export function PageGuide({ tab }: { tab: string }) {
  const guide = HELP[tab];
  if (!guide) return null;
  return <aside aria-label="Page guidance" className="shrink-0 px-4 py-2 border-b max-h-[30vh] overflow-auto" style={{ borderColor: 'var(--sem-border)', background: 'var(--sem-surface)' }}>
    <p className="m-0 text-[12px] leading-relaxed" style={{ color: 'var(--sem-muted)' }}>{guide.lead}</p>
    <details key={tab} className="mt-1 text-[12px]">
      <summary className="cursor-pointer w-fit font-medium" style={{ color: 'var(--sem-accent)' }}>Getting started</summary>
      <div className="mt-2 max-w-4xl"><GettingStarted steps={guide.start} /></div>
      <p className="m-0 mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Open Help above for more detail, related tasks and explanations of common terms.</p>
    </details>
  </aside>;
}

export function HelpButton({ tab, onGo, onShortcuts }: { tab: string; onGo?: (tab: string) => void; onShortcuts?: () => void }) {
  const [open, setOpen] = useState(false);
  const [view, setView] = useState<'tab' | 'where'>('tab');
  const h = HELP[tab];
  const go = onGo ? (t: string) => { setOpen(false); onGo(t); } : undefined;
  // Esc closes the slide-over (part of the keyboard suite — every Studio overlay closes on Escape).
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [open]);
  return (
    <>
      <button onClick={() => { setView('tab'); setOpen(true); }} title={h ? `Guide: the ${h.title} tab (and “Where do I…?”)` : 'Help'} aria-label="Help"
        className="h-6 px-2 rounded-md flex items-center justify-center text-[12px] font-semibold shrink-0"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>Help</button>
      {open && (
        <div className="fixed inset-0 z-50" onClick={() => setOpen(false)} style={{ background: 'rgba(0,0,0,0.35)' }}>
          <div onClick={(e) => e.stopPropagation()} className="absolute top-0 right-0 h-full flex flex-col"
            style={{ width: 460, maxWidth: '92vw', background: 'var(--sem-surface)', borderLeft: '1px solid var(--sem-border)', boxShadow: '-8px 0 24px rgba(0,0,0,0.4)' }}>
            <div className="flex items-center gap-2 px-4 py-3 border-b" style={{ borderColor: 'var(--sem-border)' }}>
              <span className="text-[10px] uppercase tracking-wide" style={{ color: 'var(--sem-muted)' }}>guide</span>
              <span className="text-[14px] font-semibold flex-1">{view === 'tab' ? (h?.title ?? 'Help') : 'Where do I…?'}</span>
              <div className="flex rounded-md overflow-hidden" style={{ border: '1px solid var(--sem-border)' }}>
                <button onClick={() => setView('tab')} className="px-2 py-0.5 text-[11px]"
                  style={{ background: view === 'tab' ? 'var(--sem-surface-2)' : 'transparent', color: view === 'tab' ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>This tab</button>
                <button onClick={() => setView('where')} className="px-2 py-0.5 text-[11px]"
                  style={{ background: view === 'where' ? 'var(--sem-surface-2)' : 'transparent', color: view === 'where' ? 'var(--sem-fg)' : 'var(--sem-muted)' }}>Where do I…?</button>
              </div>
              <button onClick={() => setOpen(false)} aria-label="Close help" className="text-[14px]" style={{ color: 'var(--sem-muted)' }}>✕</button>
            </div>
            <div className="flex-1 overflow-auto px-4 py-3">
              {view === 'tab'
                ? (h ? <TabGuide h={h} onGo={go} /> : <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No guide for this view yet. Try “Where do I…?”.</div>)
                : <WhereIndex onGo={go} />}
              <div className="text-[10px] mt-3 pt-2 border-t flex items-center gap-2" style={{ color: 'var(--sem-muted)', borderColor: 'var(--sem-border)' }}>
                <span>Your AI Assistant can do everything here too, on the same live model.</span>
                {onShortcuts && (
                  <button className="ml-auto underline whitespace-nowrap" style={{ color: 'var(--sem-accent)' }}
                    onClick={() => { setOpen(false); onShortcuts(); }}>Keyboard shortcuts (?)</button>
                )}
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
