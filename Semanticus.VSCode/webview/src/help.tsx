import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { runHostCommand } from './bridge';
import { ASSISTANT_SYNC_COPY, EDIT_ACTION_COPY, SAFETY_CHECK_COPY, SESSION_STATE_COPY } from './copy';

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

const ACTION_HELP_TITLES = new Set(['Model Spec', 'Power Query', 'Proposed changes', 'Published changes', 'Workflows']);

function SharedStateHelp() {
  return <SectionBlock s={{
    h: 'When a page cannot continue',
    bullets: [
      `Loading: ${SESSION_STATE_COPY.loading.detail}`,
      `Empty: ${SESSION_STATE_COPY.empty.detail} ${SESSION_STATE_COPY.empty.action}.`,
      `No live endpoint: ${SESSION_STATE_COPY.noLiveConnection.detail}`,
      `Stale results: ${SESSION_STATE_COPY.staleQuery.detail}`,
      `No permission: ${SESSION_STATE_COPY.noPermission.detail}`,
      `Error: ${SESSION_STATE_COPY.error.detail}`,
      `Success: ${SESSION_STATE_COPY.success.detail}`,
    ],
  }} />;
}

function SharedActionHelp() {
  return <SectionBlock s={{
    h: 'What the action words mean',
    bullets: [EDIT_ACTION_COPY.apply, EDIT_ACTION_COPY.save, EDIT_ACTION_COPY.publish, EDIT_ACTION_COPY.restore],
  }} />;
}

const HELP: Record<string, TabHelp> = {
  "modelhome": {
    "title": "Overview",
    "lead": "See the model at a glance, select an object, then continue in the complete tool for the job.",
    "start": [
      "Choose a table here or select an object in the native Model tree.",
      "Use the named actions for that object, or choose Diagram, Lineage, or Find and replace for the whole model.",
      "Use Back to return with the same object and work in context."
    ],
    "sections": [
      { "h": "Model facts", "bullets": ["Counts and table rows come from the open model. Loading, failures, and a model with no tables are shown separately."] },
      { "h": "Object actions", "bullets": ["Actions open the existing full tools. Editing a formula opens the native DAX editor in VS Code."] }
    ]
  },
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
    "title": "Find and replace",
    "lead": "Find names, descriptions and formulas across the model. Review replacements before changing anything.",
    "start": [
      "Type at least two characters in the search box.",
      "Select a result to find the item and its settings. Turn on Include DAX & M to search formulas and data-loading code.",
      "To change text, enter a replacement and preview one result, or use Replace all to collect the edits under Changes > Proposed."
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
          "Replace all collects the edits under Changes > Proposed for review. It does not apply them immediately.",
          "Check each proposed change. Some fields are read-only or need a dedicated editor; the result explains why they cannot be replaced here."
        ]
      }
    ]
  },
  "lineage": {
    "title": "Lineage",
    "lead": "See what a calculation uses and what relies on it. Check what a change would affect, and find what nothing uses any more.",
    "start": [
      "Find a measure or column using the search box, then pick it.",
      "Read the card: what uses it in the model, what was found in the reports that were checked, and which saved checks to run after changing it.",
      "Choose reports once. Impact and the cleanup list both count only the reports that were read."
    ],
    "sections": [
      {
        "h": "Choose a view",
        "bullets": [
          "Graph draws the connections. Click connected items to follow the chain; Clear pins resets the selection.",
          "Tree shows dependencies as a list. Upstream means the inputs an item uses. Downstream means the items that use it.",
          "Impact answers two questions from one page: what uses this field, and what can I clean up. Tick Show cleanup candidates for the second one."
        ]
      },
      {
        "h": "Choose the reports",
        "bullets": [
          "Choosing a report saves it with the model. It is not read until you check it, and it comes back as not checked when you reopen the model.",
          "Signing in to read a published report asks for permission that can also edit reports. Semanticus uses it only to read.",
          "A report that could not be fully read is said so, and its uncertainty stays in the answer. Report folders on this computer need no sign-in."
        ]
      },
      {
        "h": "Read a removal answer",
        "bullets": [
          "Nothing here is called safe. A group says what was established: no use found in these checks, still used, or needs checking.",
          "A report that was not checked could still use one of these. Check more reports to narrow that gap.",
          "Propose removal adds a proposal to Changes. The field stays in the model until you apply it there, and the apply step checks the same model and the same reports again first."
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
          "To keep a formula, edit the measure in the Model list or collect it under Changes > Proposed. A local edit does not update the live test model until you publish it."
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
          "Use Calculations > DAX Lab to ask a specific question or filter the data. Use Model > Power Query to change how data is loaded at the next refresh."
        ]
      }
    ]
  },
  "stats": {
    "title": "Size by table",
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
          "Review the build result, then use Model > Diagram and Checks > Tests to check the model. Publishing is a separate action under Changes > Published."
        ]
      }
    ],
    "pro": "Create in Model is a Semanticus Pro feature. Editing measures, columns and relationships stays free."
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
    ],
    "pro": "Create in Model is a Semanticus Pro feature. Editing measures, columns and relationships stays free."
  },
  "mcode": {
    "title": "Power Query",
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
    ],
    "pro": "Create in Model is a Semanticus Pro feature. Editing measures, columns and relationships stays free."
  },
  "optimize": {
    "title": "Proposed changes",
    "lead": "Review proposed edits together before applying them. See each change and choose which ones to keep.",
    "start": [
      "Choose Analyse model, or send findings here from Checks > AI understanding or Checks > Model quality.",
      "Review the before-and-after values. Fill in missing text and select the items you want.",
      "Apply the selected changes and read the result. Use Changes > History if you need to undo an applied batch."
    ],
    "sections": [
      {
        "h": "Prepare a plan",
        "bullets": [
          "A plan is a list of proposed changes. Creating or editing the plan does not change the model.",
          "Your assistant can work on the same plan. Its changes appear here. Your assistant sees your changes on its next call.",
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
          "An applied batch becomes one entry under Changes > History and can be undone together. Saving to a file and publishing to a live model are separate actions."
        ]
      }
    ]
  },
  "readiness": {
    "title": "AI understanding",
    "lead": "Find missing context and model settings that can make AI answers less useful. Review the findings and improve them.",
    "start": [
      "Read the grade, coverage notes and most important findings.",
      "Open a finding to see the affected item and the suggested change.",
      "Apply a supported fix, write the missing explanation, or collect changes under Changes > Proposed."
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
          "Accept finding means you accept it without fixing it, so the check counts as overridden. The reason is recorded and the finding stays visible. Reopen it when it should count again."
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
    "seeAlso": [
      {
        "label": "Check known business answers",
        "tab": "tests"
      }
    ]
  },
  "bpa": {
    "title": "Model quality",
    "lead": "Check the model for common design, naming and performance issues. Review each finding before deciding how to address it.",
    "start": [
      "Read the findings and select an affected item.",
      "Use its suggested fix or Ask AI for help with a change that needs judgement.",
      "Review several changes under Changes > Proposed, or accept an intentional exception with a recorded reason."
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
          "Accept finding records a reason and keeps the finding visible, so the check counts as overridden. Accept for the whole model accepts that rule everywhere. You can reopen it later."
        ]
      },
      {
        "h": "Custom rules",
        "bullets": [
          "Use a template to create your own naming or property checks. Preview what the rule would flag before saving it.",
          "Custom rules are saved with the model. Reusing a standard rule ID replaces that rule; the preview explains this before you save."
        ]
      }
    ]
  },
  "tests": {
    "title": "Tests",
    "lead": "Check that calculations return the answers you trust, and that tables connect and count correctly.",
    "start": [
      "Choose a live test model. Select New check to save an answer you already trust. Relationships and table row counts are checked on every run.",
      "Run all enabled checks, or open the arrow beside it to run one group or only the checks you ticked.",
      "Read the one status line, then open any row that differs. Record it keeps the run in History; Report reads or exports it."
    ],
    "sections": [
      {
        "h": "Choose a useful check",
        "bullets": [
          "A trusted answer compares a calculation with a number you know is right. Use an independent business report or source calculation for that number.",
          "Compare with source checks the same number against SQL you accept as the truth. It needs the model connection and a saved SQL source.",
          "A table row count compares the rows in a model table with the rows in its source table. Map the table to a SQL source once."
        ]
      },
      {
        "h": "Connect to source data",
        "bullets": [
          "Save a SQL source once in Connections, with its server, database and sign-in. Every check and table mapping can then pick it by name. Your model connection alone does not provide SQL access.",
          "Review generated SQL before accepting it. A query based on the same faulty logic as the model is not an independent check."
        ]
      },
      {
        "h": "Read the outcome",
        "bullets": [
          "Pass means the check ran and met the answer you trust. Differs means it found a difference. Could not check means there is no result to rely on, which is not the same as a failure.",
          "A partial run covers only what you asked for and gets no whole-model grade. Every row that did not run keeps its own earlier result and date.",
          "Relationship checks look for rows that do not join, lookup values that are not unique, and column types that do not work together.",
          "Counts differ on a table means matching snapshots were not confirmed, so on its own it does not prove missing rows."
        ]
      }
    ],
    "pro": "Tests is a Semanticus Pro feature. Model quality and AI understanding still check your whole model for free.",
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
    "title": "Saved reports",
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
        "label": "Run the model checks",
        "tab": "tests"
      },
      {
        "label": "Run a workflow",
        "tab": "workflows"
      }
    ],
    "pro": "Saved reports is part of Tests, a Semanticus Pro feature. Model quality and AI understanding still check your whole model for free."
  },
  "deploy": {
    "title": "Published changes",
    "lead": "Review changes for a live destination, publish them, restore an earlier version or move a model between release stages.",
    "start": [
      "Check the destination and account, then select Publish to review the proposed changes.",
      "Read the change list and any failed checks. Confirm only when the destination and changes are the ones you intend.",
      "Read the publishing result. Refresh data separately if your change needs new data to load."
    ],
    "sections": [
      {
        "h": "Save and publish are different",
        "bullets": [
          "Save keeps your local model or editor changes. Publish sends reviewed model design changes to the live destination used by reports.",
          "The editing model, test model and publishing destination can differ. The bottom bar identifies each one.",
          "On Changes > Published, the What to publish mode shows a comparison so you can review selected changes first."
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
    ],
    "pro": "Advanced publishing is a Semanticus Pro feature. Publish, Promote, compare and restore points stay free."
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
    "lead": "Choose what your assistant may do without asking and review requests waiting for your decision.",
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
          "The main switch turns these assistant permission checks on or off. Workflow checks are managed separately in Workflows > Governance.",
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
    ],
    "pro": "Create in Model is a Semanticus Pro feature. Editing measures, columns and relationships stays free."
  },
  "history": {
    "title": "History",
    "lead": "See changes made by you and your assistant. Undo recent model edits or review the saved audit record.",
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
          "A batch, such as an applied set of proposed changes, is one timeline entry and is undone together.",
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
        "h": "Edit a workflow",
        "bullets": [
          "Steps, Canvas and Source show one shared draft. Change the draft in the view that suits the job, then use Save workflow to review the complete proposed file before writing it.",
          "Preview shows the full-context difference and any warnings. If the saved file changed, reload it or review the current file before keeping your draft.",
          "Built-in workflows are read-only. Copy one into the project before editing it. Saved workflow files can also be edited through your assistant with the same preview and current-file checks."
        ]
      },
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
          "An action is a task the model tools can carry out. Instructions explain what to do. A check runs at a step and records whether it passed, failed or was not checked.",
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
          "In Governance, choose a project profile to load its workflow settings. Review the resulting controls, including which workflows are required and whether their checks have to pass.",
          "The switch labelled Required workflows in Governance turns required checks off for new runs, so those runs record nothing as passed. Runs already underway keep the setting they started with. This is separate from assistant permissions."
        ]
      }
    ],
    "pro": "Workflows is a Semanticus Pro feature. The app still shows any run already going and anything waiting for you.",
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
    "title": "Model notes",
    "lead": "Keep the business context that helps you and your assistant understand this model.",
    "start": [
      "Read the model overview and saved business context.",
      "Add or correct definitions, important calculations and known issues.",
      "Review supporting insights when the model or its business rules change."
    ],
    "sections": [
      {
        "h": "What model notes are",
        "bullets": [
          "Model notes are a short introduction to the model. Explain what it covers, what common terms mean and which calculations people should use.",
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
          "Insights are saved lessons. Edit their text and search terms, or adjust their importance. Review a suggested addition to your model notes before selecting Accept.",
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
    ],
    "pro": "Create in Model is a Semanticus Pro feature. Editing measures, columns and relationships stays free."
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
    "pro": "Advanced publishing is a Semanticus Pro feature, and the Data Agent lives inside it. Publish, Promote, compare and restore points stay free."
  }
};

// ---------------------------------------------------------------------------------------------------
// "Where do I…?" is the task-to-location index. Much of authoring is in native VS Code (the Model tree,
// the Properties view and the palette), so users look in Studio first. Keep entries
// verified against package.json/extension.ts.
// ---------------------------------------------------------------------------------------------------

interface WhereEntry { q: string; a: string; tab?: string }
const WHERE: { group: string; items: WhereEntry[] }[] = [
  {
    group: 'Start here',
    items: [
      { q: 'I am new to Semanticus. Where should I begin?', a: 'Open a model from the Semanticus sidebar. Read Model > Model notes for its business context, explore Model > Diagram, then select a calculation in the Model list. Each Studio page has Getting started steps and a detailed Help guide.', tab: 'knowledge' },
      { q: 'What is the difference between editing, testing and publishing?', a: 'Editing changes your working model. Testing asks a live model for answers. Publishing sends reviewed changes to a live destination. These can be different models; check the bottom bar and use Connections to change them.' },
      { q: 'Can I work without a live connection?', a: 'Yes. Open model files to inspect and edit their structure, formulas and descriptions. Running queries, measuring performance and testing real answers needs a live model with data.' },
      { q: 'Give my assistant the Semanticus guides', a: 'Run Semanticus: Install or Update Assistant Skills from the VS Code command palette. Choose your assistant, then refresh or restart it if needed. These guides use your existing Semanticus connection.' },
      { q: 'Test a business answer against a number I trust', a: 'Checks > Tests > New test. Choose a known answer and enter the calculation, expected value and the filters the answer applies to.', tab: 'tests' },
      { q: 'Compare model results with source data', a: 'Checks > Tests > New test > source comparison. Review the SQL that supplies the expected answer and connect to its server and database. This needs a SQL connection as well as the live model.', tab: 'tests' },
      { q: 'Review saved test and workflow reports', a: 'Checks > Results lists the reports saved with this model. Open one to read its results, date and coverage.', tab: 'evidence' },
      { q: 'Change when the assistant asks for permission', a: 'Permissions shows the current settings and waiting requests. Workflow requirements are a separate choice under Workflows > Governance.', tab: 'permissions' },
    ],
  },
  {
    group: 'Common terms explained',
    items: [
      { q: 'Semantic model, table, column and measure', a: 'A semantic model organises data and business calculations for reports. Tables contain rows; columns hold values such as date or customer. A measure calculates an answer, such as total sales.' },
      { q: 'DAX and filter context', a: 'DAX is the formula language for model calculations. Filter context is the set of values used for an answer, such as sales for a particular product, region and year.', tab: 'daxlab' },
      { q: 'Power Query, M and partition', a: 'Power Query prepares data for loading. M is its formula language. A partition is a section of a model table with its own loading query.', tab: 'mcode' },
      { q: 'XMLA endpoint, SQL server and tenant', a: 'An endpoint is a connection address. XMLA connects to a semantic model; SQL connects to source tables. A tenant identifies the Microsoft organisation your account signs into.' },
      { q: 'Workflow, check and project profile', a: 'A workflow is a saved sequence of steps. A check runs at a step and records whether it passed, failed or was not checked. A project profile saves which workflows are available or required and how their checks apply.', tab: 'workflows' },
      { q: 'Evidence, coverage and reconciliation', a: 'Evidence is the recorded result of a check, kept under Checks > Results. Coverage says what was checked and what was left out. Reconciliation compares model results with an independent source answer.', tab: 'tests' },
      { q: 'Model quality, finding and override', a: 'Model quality runs the Best Practice Analyzer (BPA) rules over the model. A finding is an issue raised by a check. An override records your decision to accept that issue without fixing it.', tab: 'bpa' },
      { q: 'MCP and assistant skills', a: 'MCP is the connection that lets your assistant use Semanticus tools. A skill is a guide that helps it choose and use those tools for a task.' },
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
    group: 'Model and Calculations (Studio)',
    items: [
      { q: 'Field parameters, calc groups, calendars, perspectives, RLS/OLS, DaxLib', a: 'Model > Advanced Modelling holds six guided builders. They are also on the Model view "…" menu.', tab: 'advmodels' },
      { q: 'Try a calculation and see which filters affect its answer', a: 'Calculations > DAX Lab, in Visual or Query mode against a live model.', tab: 'daxlab' },
      { q: 'Check whether two DAX formulas return the same answers', a: 'Calculations > DAX Lab > Verify. Choose relevant fields, compare both formulas and read which contexts were checked.', tab: 'daxlab' },
      { q: 'Compare a first run with a repeat run', a: 'Calculations > DAX Lab > Performance, then Cold / Warm.', tab: 'daxlab' },
      { q: 'Edit the load steps or incremental refresh', a: 'Model > Power Query, or right-click a table and choose Edit M Code.', tab: 'mcode' },
      { q: 'Preview table rows', a: 'Model > Data, or right-click a table and choose Preview Data.', tab: 'data' },
      { q: 'Find what depends on a field, and what nothing uses any more', a: 'Model > Lineage > Impact, or right-click an object and choose Show Lineage & Impact.', tab: 'lineage' },
      { q: 'See which tables use the most memory', a: 'Model > Size by table. Choose Scan storage against a live model to rank the largest tables and columns.', tab: 'stats' },
      { q: 'Refresh a partition', a: 'Model tree → expand the table → right-click the partition → "Refresh Partition…" (pick a refresh type, dry-run, confirm).' },
    ],
  },
  {
    group: 'Checks, Changes and Workflows (Studio)',
    items: [
      { q: 'Score and fix how well AI understands the model', a: 'Checks > AI understanding. Apply a safe fix in one click, or copy a ready prompt for the rest.', tab: 'readiness' },
      { q: 'Find design, naming and performance problems', a: 'Checks > Model quality. Fix one finding, apply a batch with Pro, or send the changes to Changes > Proposed for review.', tab: 'bpa' },
      { q: 'Review a batch of changes before applying', a: 'Changes > Proposed. Keep or reject each item, then apply the rest as one undoable step.', tab: 'optimize' },
      { q: 'Publish the open model to a live workspace', a: 'Press Publish in the header. It is the one publish button in the app. The review names the destination and changes, then you confirm. Ctrl+S never publishes.', tab: 'deploy' },
      { q: SAFETY_CHECK_COPY.helpQuestion, a: `${SAFETY_CHECK_COPY.explainer} ${SAFETY_CHECK_COPY.helpTail}`, tab: 'history' },
      { q: 'Compare model versions and combine selected changes', a: 'Changes > Published > What to publish can review any two supported model sources.', tab: 'deploy' },
      { q: 'Copy objects from another model', a: 'The Reference Model view (side bar): "Set Reference Model…", then right-click → "Copy into Open Model" (or Ctrl+C there, Ctrl+V in the Model tree).' },
      { q: 'Generate documentation', a: 'Model > Docs. Compose the guide, brand it, add your own explanations, then print to PDF.', tab: 'docs' },
      { q: 'Publish a Fabric Data Agent', a: 'Changes > Published > Advanced > Data Agent: choose the model data, add its guidance, review the changes and publish.', tab: 'dataagent' },
      { q: 'Edit a workflow', a: 'Workflows > Author. Steps, Canvas and Source edit one shared draft. Save workflow opens a full preview before the saved file changes.', tab: 'workflows' },
      { q: 'Recover a workflow after the file changed', a: 'Workflows > Author. Choose Reload saved file to start from the newer file, or Review current file to compare it with your draft before keeping your work.', tab: 'workflows' },
      { q: 'Check a workflow before running it', a: 'Workflows > Author. Read the workflow checks and warnings, then start a run only after the steps and answers match the task.', tab: 'workflows' },
    ],
  },
  {
    group: 'Your assistant and safety',
    items: [
      { q: 'Connect your assistant to this model', a: 'Command palette > "Semanticus: Connect AI Assistant" writes the connection file. Nothing is connected until you refresh or restart your assistant so it reads that file. Keep Semanticus open in VS Code.' },
      { q: 'See what your assistant changed', a: 'Changes > History names every change by who made it on one timeline. Undo to here reverses it.', tab: 'history' },
      { q: 'Undo or redo a change', a: 'Use the buttons on Changes > History, or the palette commands "Semanticus: Undo" and "Semanticus: Redo". A batch undoes as one step. Publishing to a live server is separate and is not reversed by local Undo.', tab: 'history' },
      { q: 'Run a verified playbook', a: 'Workflows runs steps whose checks record evidence. Workflows is a Pro feature.', tab: 'workflows' },
      { q: 'Turn workflow checks off for a quick task', a: 'Workflows > Governance > Required workflows. Off means new runs skip their required checks and record nothing as passed. Runs already underway keep their original setting.', tab: 'workflows' },
      { q: 'Save business context and lessons from earlier work', a: 'Model > Model notes holds the model guide, saved insights, reusable workflows and a search over your notes.', tab: 'knowledge' },
      { q: 'Accept a finding without fixing it', a: 'Use Accept finding on Checks > AI understanding or Checks > Model quality. A reason is required, and the finding stays visible as overridden.', tab: 'readiness' },
    ],
  },
  {
    group: 'Setup and housekeeping',
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
      <SharedStateHelp />
      {ACTION_HELP_TITLES.has(h.title) && <SharedActionHelp />}
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

/**
 * The page notes, behind the ⓘ at the end of the area row. This used to be an always-open block (PageGuide) under
 * the breadcrumb, which spent 59px above every page on a sentence most people read once. Same words, same steps,
 * on request. Keyboard contract matches the header menus: Enter or Space opens, Escape closes and puts focus back
 * on the ⓘ, a click outside closes.
 */
export function PageNotesButton({ tab }: { tab: string }) {
  const guide = HELP[tab];
  const [open, setOpen] = useState(false);
  const wrap = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', outside);
    return () => document.removeEventListener('mousedown', outside);
  }, [open]);
  useEffect(() => { if (open) popRef.current?.focus(); }, [open]);
  // The notes close when the page changes under them: the sentence would be about the page you just left.
  useEffect(() => { setOpen(false); }, [tab]);
  // Escape is a CANCEL: focus goes back to the button you opened, ring and all, so the keyboard keeps its place.
  const closeAndReturnFocus = () => { setOpen(false); buttonRef.current?.focus(); };
  // TAB STAYS IN THE DIALOG. Astra, 2026-09-14: Space to open, one Tab, then Escape left focus in the tool
  // BEHIND the notes, so Escape belonged to that tool and the notes were stranded over the canvas with no
  // keyboard way out. Reproduced on Overview, Diagram and Lineage. This is a small dialog, so the ring cycles
  // dialog -> its own stops -> dialog and never reaches the page behind it.
  const trapTab = (e: React.KeyboardEvent) => {
    if (e.key !== 'Tab') return;
    e.preventDefault();
    const stops = [...(popRef.current?.querySelectorAll<HTMLElement>('a[href], button:not([disabled])') ?? [])];
    if (stops.length === 0) { popRef.current?.focus(); return; }
    const at = stops.indexOf(document.activeElement as HTMLElement);
    if (at === -1) { (e.shiftKey ? stops[stops.length - 1] : stops[0]).focus(); return; }
    const next = at + (e.shiftKey ? -1 : 1);
    if (next < 0 || next >= stops.length) popRef.current?.focus(); else stops[next].focus();
  };
  // And if focus leaves anyway (a script, a click, anything the trap cannot see), the notes go with it rather
  // than outliving the keyboard. A window losing focus is not a person leaving the notes, so that case stays.
  const closeOnFocusLeaving = (e: React.FocusEvent) => {
    if (!open) return;
    const next = e.relatedTarget as Node | null;
    if (wrap.current?.contains(next)) return;
    if (!next && !document.hasFocus()) return;
    setOpen(false);
  };
  return <div ref={wrap} className="studio-page-notes-wrap relative shrink-0" onBlur={closeOnFocusLeaving}>
    <button ref={buttonRef} type="button" className="studio-page-notes" aria-haspopup="dialog" aria-expanded={open}
      aria-label="Page notes" title={guide ? `What this page is for, and how to start: ${guide.title}` : 'What this page is for'}
      onClick={() => setOpen((v) => !v)}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setOpen(true); }
        else if (e.key === 'Escape' && open) { e.preventDefault(); setOpen(false); }
      }}>i</button>
    {open && <div ref={popRef} tabIndex={-1} role="dialog" aria-label="Page notes" className="studio-page-notes-pop absolute right-0 top-full z-50 mt-1"
      onKeyDown={(e) => { if (e.key === 'Escape') { e.preventDefault(); closeAndReturnFocus(); } else trapTab(e); }}>
      {/* Kane, 2026-09-14: the guide's words MOVE, they are not rewritten. The lead sentence, the numbered steps
          and the closing line are the same strings the open guide block showed, and the heading still reads
          "Getting started" rather than being uppercased by CSS into something a screen reads differently. */}
      {guide ? <>
        <p className="m-0 text-[12px] leading-relaxed" style={{ color: 'var(--sem-muted)' }}>{guide.lead}</p>
        <div className="mt-2 text-[12px] font-medium" style={{ color: 'var(--sem-accent)' }}>Getting started</div>
        <div className="mt-2 max-w-4xl"><GettingStarted steps={guide.start} /></div>
        <p className="m-0 mt-2 text-[11px]" style={{ color: 'var(--sem-muted)' }}>Open Help above for more detail, related tasks and explanations of common terms.</p>
      </> : <p className="m-0 text-[12px]" style={{ color: 'var(--sem-muted)' }}>No notes for this page yet. Open Help above for the rest of the guide.</p>}
      {/* A way out that is not a key. It is also what the Tab trap cycles to, so the ring always has somewhere
          to go inside the dialog instead of falling onto the page behind it. */}
      <div className="mt-2 flex justify-end">
        <button type="button" className="studio-page-notes-close" onClick={closeAndReturnFocus}>Close</button>
      </div>
    </div>}
  </div>;
}

/** One row of the Help menu. `section` starts a new named group directly above the row. */
interface HelpItem { id: string; label: string; note?: string; section?: string; title?: string; run: () => void; }

// Help is a small menu, not one button with one destination. The page guide keeps first place, the shortcuts
// sheet stops being a footnote inside the guide, and "Something wrong?" gives a stuck engine a visible way out
// of Studio (Restart engine was Command-Palette-only, which is not a place a person looks when nothing responds).
export function HelpButton({ tab, onGo, onShortcuts, onTextSize }: {
  tab: string; onGo?: (tab: string) => void; onShortcuts?: () => void;
  onTextSize?: (direction: 'smaller' | 'larger' | 'reset') => void;
}) {
  const [guideOpen, setGuideOpen] = useState(false);
  const [open, setOpen] = useState(false);
  const [highlight, setHighlight] = useState(0);
  const [view, setView] = useState<'tab' | 'where'>('tab');
  const h = HELP[tab];
  const wrap = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const itemRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const go = onGo ? (t: string) => { setGuideOpen(false); onGo(t); } : undefined;
  // Esc closes the slide-over (part of the keyboard suite — every Studio overlay closes on Escape).
  useEffect(() => {
    if (!guideOpen) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setGuideOpen(false); } };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [guideOpen]);
  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!wrap.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener('mousedown', outside);
    return () => document.removeEventListener('mousedown', outside);
  }, [open]);
  useEffect(() => { if (open) setHighlight(0); }, [open]);
  useEffect(() => {
    if (!open) return;
    if (highlight >= 0) itemRefs.current[highlight]?.focus(); else menuRef.current?.focus();
  }, [open, highlight]);
  const closeAndReturnFocus = () => { setOpen(false); buttonRef.current?.focus(); };

  const items: HelpItem[] = [];
  items.push({ id: 'page', label: 'Help for this page', note: h?.title,
    title: h ? `Guide: the ${h.title} tab (and “Where do I…?”)` : 'Help for this page',
    run: () => { setView('tab'); setGuideOpen(true); } });
  if (onShortcuts) items.push({ id: 'shortcuts', label: 'Keyboard shortcuts', note: '?',
    title: 'Every key you can press in Studio', run: onShortcuts });
  // The same control as Ctrl and the wheel, for anyone who would rather not scroll for it. Between 80% and 130%.
  if (onTextSize) {
    items.push({ id: 'textsize-smaller', label: 'Smaller', note: 'Ctrl+scroll', section: 'Text size',
      title: 'Make everything in Studio a step smaller, down to 80%', run: () => onTextSize('smaller') });
    items.push({ id: 'textsize-larger', label: 'Larger', note: 'Ctrl+scroll',
      title: 'Make everything in Studio a step bigger, up to 130%', run: () => onTextSize('larger') });
    items.push({ id: 'textsize-reset', label: 'Reset', note: 'Ctrl+0',
      title: 'Put Studio back to its normal size', run: () => onTextSize('reset') });
  }
  items.push({ id: 'restart', label: 'Restart engine', section: 'Something wrong?',
    title: 'Start the Semanticus engine again. It asks first if you have unsaved edits.',
    run: () => runHostCommand('semanticus.restartEngine') });

  const pick = (item: HelpItem, fromKeyboard: boolean) => {
    setOpen(false);
    item.run();
    if (fromKeyboard) buttonRef.current?.focus(); else buttonRef.current?.blur();
  };

  return (
    <div ref={wrap} className="relative shrink-0">
      <button ref={buttonRef} type="button" aria-haspopup="menu" aria-expanded={open} aria-label="Help"
        onClick={() => setOpen((v) => !v)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); setOpen(true); }
          else if (e.key === 'Escape' && open) { e.preventDefault(); setOpen(false); }
        }}
        title={h ? `Guide: the ${h.title} tab, shortcuts and repairs` : 'Help, shortcuts and repairs'}
        className="h-6 px-2 rounded-md flex items-center justify-center text-[12px] font-semibold shrink-0"
        style={{ background: 'var(--sem-surface-2)', color: 'var(--sem-fg)', border: '1px solid var(--sem-border)' }}>Help</button>
      {open && (
        <div ref={menuRef} tabIndex={-1} role="menu" aria-label="Help" className="studio-chip-menu absolute right-0 top-full z-50 mt-1"
          onKeyDown={(e) => {
            if (e.key === 'Escape') { e.preventDefault(); closeAndReturnFocus(); }
            else if (e.key === 'ArrowDown') { e.preventDefault(); setHighlight((h2) => (h2 + 1 + items.length) % items.length); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight((h2) => (h2 <= 0 ? items.length : h2) - 1); }
            else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); const it = items[highlight]; if (it) pick(it, true); }
            else if (e.key === 'Tab') { setOpen(false); }
          }}>
          {items.map((item, i) => <Fragment key={item.id}>
            {item.section && <div className="studio-chip-menu-heading">{item.section}</div>}
            <button ref={(el) => { itemRefs.current[i] = el; }} role="menuitem" type="button" title={item.title}
              className={i === highlight ? 'is-highlighted' : undefined}
              onMouseEnter={() => setHighlight(i)} onClick={() => pick(item, false)}>
              <span>{item.label}</span>
              {item.note && <span className="studio-chip-menu-note">{item.note}</span>}
            </button>
          </Fragment>)}
        </div>
      )}
      {guideOpen && (
        <div className="fixed inset-0 z-50" onClick={() => setGuideOpen(false)} style={{ background: 'rgba(0,0,0,0.35)' }}>
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
              <button onClick={() => setGuideOpen(false)} aria-label="Close help" className="text-[14px]" style={{ color: 'var(--sem-muted)' }}>✕</button>
            </div>
            <div className="flex-1 overflow-auto px-4 py-3">
              {view === 'tab'
                ? (h ? <TabGuide h={h} onGo={go} /> : <div className="text-[12px]" style={{ color: 'var(--sem-muted)' }}>No guide for this view yet. Try “Where do I…?”.</div>)
                : <WhereIndex onGo={go} />}
              <div className="text-[10px] mt-3 pt-2 border-t flex items-center gap-2" style={{ color: 'var(--sem-muted)', borderColor: 'var(--sem-border)' }}>
                <span>Your assistant can use this same Semanticus session through MCP. {ASSISTANT_SYNC_COPY}</span>
                {onShortcuts && (
                  <button className="ml-auto underline whitespace-nowrap" style={{ color: 'var(--sem-accent)' }}
                    onClick={() => { setGuideOpen(false); onShortcuts(); }}>Keyboard shortcuts (?)</button>
                )}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
