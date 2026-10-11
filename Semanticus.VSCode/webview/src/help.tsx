import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
import { runHostCommand } from './bridge';
import { EDIT_ACTION_COPY, SAFETY_CHECK_COPY } from './copy';
import { requestPlanGuide, WHAT_CHANGED, WHAT_CHANGED_CARD_ID } from './plan-guide';

// ===================================================================================================
// Per-tab in-context help, the USER GUIDE (Studio v2). A '?' in the header opens a slide-over with
// two views: "This tab" (a detailed guide for the ACTIVE tab: concepts, tasks with the real button
// names, gotchas, Pro notes) and "Where do I…?" (a searchable task → location index, because plenty
// of authoring lives OUTSIDE Studio (the Model tree, the Properties view, the command palette) and
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

const ACTION_HELP_TITLES = new Set(['Model Spec', 'Power Query', 'Pending changes', 'Published', 'Workflows']);

function SharedActionHelp() {
  return <SectionBlock s={{
    h: 'What the action words mean',
    bullets: [EDIT_ACTION_COPY.apply, EDIT_ACTION_COPY.save, EDIT_ACTION_COPY.publish, EDIT_ACTION_COPY.restore],
  }} />;
}

const HELP: Record<string, TabHelp> = {
  "plan": {
    "title": "Plan",
    "lead": "One living plan for this model: what it should become, the gaps, and the next thing worth doing about it.",
    "start": [
      "Start with the strip. Click a track to see its rows.",
      "Next lists the rows Semanticus and your assistant can do. Rows only you can do sit last, in a fold.",
      "Use a row's link to go where the work is done.",
      "History has every change and who made it, with Undo to here and Restore."
    ],
    "sections": [
      {
        "h": "The strip",
        "bullets": [
          "Three tracks: Semanticus, you, and your assistant. Each block is one row of the plan.",
          "Solid is done today. Striped is being done now. An outline is ready. A yellow outline needs you. Dashed is paused, or waits for another row.",
          "Choose a track to see only its rows. Choose it again to see everything.",
          "Latest names the newest change. Choose it to open History."
        ]
      },
      {
        "h": "Every row says three things",
        "bullets": [
          "Where it came from: found by a rule, proposed by Jev (Pro), your words, sent from a page, a workflow, or a repeat.",
          "Who does it: Semanticus, you, or your assistant. Press it to change who. The choices are Semanticus, Your assistant and Me.",
          "Where it stands: Needs you, Ready, Waiting, On it, Applied and checking, or Done. While a run works on a row it says Working out, Applying or Checking.",
          "Waits for names the row that has to finish first. When that row is done, this one turns Ready by itself."
        ]
      },
      {
        "h": "Describe a task or an aim",
        "bullets": [
          "Type what you want and press Enter. It becomes a row for your assistant. Press its doer and choose Me to keep it yourself.",
          "On Pro, the field also says which page does that work. It never takes you there unless you choose it."
        ]
      },
      {
        "h": "Run the plan",
        "bullets": [
          "Run the plan does every row Semanticus can do, including rows that only wait for another row of the same run. The number on the button is how many.",
          "Each row shows its step: Working out, Applying, Checking, then Done. Finished rows move to Done just now.",
          "When it ends, a line says how many rows ran and how many changes. Undo takes back the whole run. Save saves a model opened from files.",
          "If the plan changed after you read it, nothing is applied and the page asks you to look again.",
          "When only your assistant has rows left, the button becomes Tell your assistant. It copies a short ask to paste into your assistant."
        ]
      },
      {
        "h": "Your assistant's tasks",
        "bullets": [
          "Your assistant sees its Ready tasks the next time it checks in. Nothing wakes it.",
          "Paused stops new tasks being offered. A task it already took can still finish. Talking to it directly works either way.",
          "The dots on a row hold the rest: Move up for one of your assistant's tasks, Repeat, Draft steps, Set aside and Remove.",
          "Open a row to leave one note and read its reply. To take a task yourself, press its doer and choose Me, then press Mark done when it is finished."
        ]
      },
      {
        "h": "History",
        "bullets": [
          "History lists every change and who made it, newest first.",
          "Undo to here takes a change back, with any made after it. Restore goes back to a saved version."
        ]
      },
      {
        "h": "What Jev decides (Pro)",
        "bullets": [
          "Jev is the Semanticus helper that makes small choices for you on Pro. A choice about the model arrives as a Plan row you can read, change or drop.",
          "It picks a column's format, chooses a base measure for a number column, chooses what Copilot sees, and puts the plan in order.",
          "It drafts from your words too: a workflow you describe on the Workflows page becomes steps, a task you type with Ctrl J becomes a draft at once, and a task you type on the Plan becomes one when you press Draft the steps.",
          "What Pro decides, on the Pro page, lists every choice, says which are on, and switches each one off for this model.",
          "Nothing goes to Jev until you agree on the card. Then it gets names, types, formats, folders, descriptions, the other names your model gives a measure (its synonyms) when your question uses one, counts and relationships, facts about a measure's formula such as the functions it uses (never the formula), and the words you type: questions, and the words that describe a task or a workflow.",
          "Words you type go as you typed them. A row or a value from your tables never goes.",
          "Your agreement is kept outside the model files. A small marker is written into the model so the agreement stays with it, and you will see it in a commit."
        ]
      }
    ],
    "pro": "Running the plan, with Jev's proposals, is a Semanticus Pro feature. Every row, the strip, your tasks, Move up, the switch and History are free.",
    "seeAlso": [
      { "label": "Model quality", "tab": "bpa" },
      { "label": "AI readiness", "tab": "readiness" },
      { "label": "Workflows", "tab": "workflows" }
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
      "To change text, enter a replacement and preview one result, or use Replace all to collect the edits under Plan > Pending changes."
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
          "Replace all collects the edits under Plan > Pending changes for review. It does not apply them immediately.",
          "Check each pending change. Some fields are read-only or need a dedicated editor; the result explains why they cannot be replaced here."
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
          "Tree shows dependencies as a list. Built from means the inputs an item uses. Used by means the items that use it.",
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
          "Propose removal adds it to Plan > Pending changes. The field stays in the model until you apply it there, and the apply step checks the same model and the same reports again first."
        ]
      }
    ]
  },
  "daxlab": {
    "title": "Queries",
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
          "Profile separates calculation work (Formula Engine) from reading stored data (Storage Engine) when the server provides those details. Missing trace events mean something was not recorded, not proof that no data was read.",
          "Trace setup time is separate from query time. Plan shows the server's execution steps when available. Clearing a shared server's cache can slow other users' next queries; the existing confirmation explains that effect."
        ]
      },
      {
        "h": "Compare answers",
        "bullets": [
          "In Verify, enter Original and Candidate formulas. Choose fields from tables that matter to the calculation, such as Customer and Product, and include totals and edge cases.",
          "A match supports the contexts actually checked. Empty, partial or unavailable checks do not establish that the rewrite is correct everywhere.",
          "An intended change to the business rule needs a known answer saved as a check on Review > Validation. It should not be expected to match the old formula."
        ]
      },
      {
        "h": "Inspect intermediate values and save a change",
        "bullets": [
          "Debug records values from EVALUATEANDLOG(expression, \"label\") in a query. Log each measure adds logging for selected measures. No captured output means there is no debug result to inspect.",
          "To keep a formula, edit the measure in the Model list or collect it under Plan > Pending changes. A local edit does not update the live test model until you publish it."
        ]
      }
    ],
    "seeAlso": [
      {
        "label": "Check a known business answer",
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
          "Use Edit > Queries to ask a specific question or filter the data. Use Edit > Advanced modelling > Power Query to change how data is loaded at the next refresh."
        ]
      }
    ]
  },
  "askdata": {
    "title": "Ask your data",
    "pro": "Ask your data is a Semanticus Pro feature. Queries still runs any query you write, and your own assistant can still write and run one for you.",
    "lead": "Type a question about your numbers, or choose a measure and the columns to split it by. Semanticus builds, checks and runs the query, and keeps the question.",
    "start": [
      "Choose a test model with a live connection.",
      "Type your question and press Ask. Or choose a number, then the columns to split it by.",
      "Change any part and the question runs again. Save a good answer as a check."
    ],
    "sections": [
      {
        "h": "What it does",
        "bullets": [
          "Every question becomes one checked query that only reads. Nothing in the model changes.",
          "Each question is kept with the model, so you and your assistant can run it again.",
          "On Pro you can also type the question. Jev reads it into the parts, shown as chips you can change, and Semanticus builds and checks the query. Your words are sent to be read. The model's values and rows never are.",
          "Reading typed questions can be switched off for a model under What Pro decides, on the Pro page. The chips still work without it."
        ]
      }
    ]
  },
  "stats": {
    "title": "Size",
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
    "lead": "Plan the tables, relationships and calculations before building them. The draft stays separate from the model until you build it. It opens as a panel on the Plan page, from Help > All tools, from a saved link, or when your assistant sends you there. On a model with no tables, the Plan's From a SQL source start opens it too.",
    "start": [
      "Choose a starting point: the open model, an empty draft, a SQL source or a saved Model Spec.",
      "Review and edit the tables, columns, relationships and measures in the draft.",
      "Save the draft, then choose Build into model when you want to add the reviewed items."
    ],
    "sections": [
      {
        "h": "Choose or change a starting point",
        "bullets": [
          "A Model Spec is a saved model plan. Drafting from an existing model reads its structure. Drafting from a SQL source reads the tables, columns and keys from a Fabric SQL endpoint with a Microsoft sign-in. No data rows are copied and no password is kept.",
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
          "Review the build result, then use Explore > Diagram and Review > Validation to check the model. Publishing is a separate action under Publish > Published."
        ]
      }
    ]
  },
  "advmodels": {
    "title": "Advanced modelling",
    "lead": "Build reusable calculations, calendars and report choices, or control which data people can see.",
    "start": [
      "Choose what you need from the tabs on this page: Calendars, Field parameters, Calculation groups, Perspectives, RLS / OLS and DaxLib.",
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
    "tip": "The row at the top of the page opens any of these directly, and Power Query sits in the same row."
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
    ]
  },
  "optimize": {
    "title": "Pending changes",
    "lead": "Review pending edits together before applying them. See each change and choose which ones to keep. It opens as a panel on the Plan page, from a Review these together button, from Help > All tools, from a saved link, or when your assistant sends you there. Each change in a set also sits on the plan, under the item it belongs to, where you can keep or drop it. A change no item claims gets an item of its own.",
    "start": [
      "Choose Analyse model, or send findings here from Review > AI readiness or Review > Model quality.",
      "Review the before-and-after values. Fill in missing text and select the items you want.",
      "Apply the selected changes and read the result. Use Plan > History if you need to undo a set of changes you already applied."
    ],
    "sections": [
      {
        "h": "Prepare the changes",
        "bullets": [
          "Pending changes are edits waiting for your decision. Building the list does not change the model.",
          "Your assistant can work on the same list. Its changes appear here. Your assistant sees your changes on its next call.",
          "Needs content means a value still needs to be written, such as a useful business description. Give to your assistant puts it on your Plan as a task, and your assistant sees it on its next call. Copy gives you the same words to paste."
        ]
      },
      {
        "h": "Read what actually changed",
        "bullets": [
          "Review the actual change, not just its label. A verified formula has been compared in the recorded test contexts; that is not a guarantee for every possible input.",
          "Items you reject are left out. Read applied, skipped and failed counts after applying; selecting an item does not prove it was changed."
        ]
      },
      {
        "h": "Save the result",
        "bullets": [
          "A set of changes you apply becomes one entry in the Plan's History and can be undone together. Saving to a file and publishing to a live model are separate actions."
        ]
      }
    ]
  },
  "readiness": {
    "title": "AI readiness",
    "lead": "Find missing context and model settings that can make AI answers less useful. Review the findings and improve them.",
    "start": [
      "Read the grade, coverage notes and most important findings.",
      "Open a finding to see the affected item and the suggested change.",
      "Apply a supported fix, write the missing explanation, or collect changes under Plan > Pending changes."
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
          "Apply makes the offered model change. Review it first. Give to your assistant puts a task on your Plan with the relevant model context, and Copy copies the same words.",
          "Descriptions should explain business meaning, not just repeat a field name. Validation can check that key numbers match answers you trust.",
          "Accept means you accept it without fixing it, so the check counts as overridden. The reason is recorded and the finding stays visible. Reopen it when it should count again."
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
    "title": "Model quality (BPA)",
    "lead": "Check the model for common design, naming and performance issues. Review each finding before deciding how to address it.",
    "start": [
      "Read the findings and select an affected item.",
      "Use its suggested fix, or give it to your assistant when the change needs judgement.",
      "Review several changes under Plan > Pending changes, or accept an intentional exception with a recorded reason."
    ],
    "sections": [
      {
        "h": "What the Best practice letter means",
        "bullets": [
          "Best practice is A when the model has no open errors and no open warnings. Info notes never count against the letter. A finding you accepted counts as passed. An error or warning rule that could not be checked also stops an A.",
          "Below A, each open error counts 5 points and each open warning 2 points. The points are divided by the number of objects checked, so a big model is not marked down for its size. Tables, columns, measures, relationships, roles and the like are objects; partitions are not, because the table already stands for them.",
          "B is up to 0.10 points per object, C is over 0.10 up to 0.25, D is over 0.25 up to 0.50, and F is over 0.50.",
          "More clean objects lower the points per object, but they never make an A: A needs no open errors or warnings, whatever the size.",
          "The same letter shows on the Plan beside AI readiness, and your assistant sees it in the scan results."
        ]
      },
      {
        "h": "What these checks mean",
        "bullets": [
          "These rules flag design, naming and performance patterns worth reviewing. A finding is not always a broken model.",
          "Checks run when the model opens and when it changes. They examine model definitions, not every result returned by live data."
        ]
      },
      {
        "h": "Fix or accept a finding",
        "bullets": [
          "Fix applies the offered change. Give to your assistant puts it on your Plan as a task. Review all fixes sends the fixes to Plan > Pending changes, where you apply them together.",
          "Accept records a reason and keeps the finding visible, so the check counts as overridden. Accept for the whole model accepts that rule everywhere. You can reopen it later."
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
    "title": "Validation",
    "lead": "Check that calculations return the answers you trust, and that tables connect and count correctly.",
    "start": [
      "Choose a live test model. Select New check to save an answer you already trust. Relationships and table row counts are checked on every run.",
      "Select Run checks, or open the arrow beside it to run one group. Tick checks first and the button runs only those.",
      "Read the one status line, then open any row that differs. Record it keeps the run with the model. Report opens the report for this run, and the Reports tab lists the ones you kept."
    ],
    "sections": [
      {
        "h": "Choose a useful check",
        "bullets": [
          "A trusted answer compares a calculation with a number you know is right. Use an independent business report or source calculation for that number.",
          "Check against SQL compares the same number with SQL you accept as the truth. It needs the model connection and a saved SQL source.",
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
    "seeAlso": [
      {
        "label": "Open the Reports tab",
        "tab": "evidence"
      },
      {
        "label": "Compare two DAX formulas",
        "tab": "daxlab"
      }
    ]
  },
  "evidence": {
    "title": "Reports",
    "lead": "The reports kept with this model, on the second tab of Validation. See what was checked, when it ran and which model it describes.",
    "start": [
      "Open Review > Validation and press Reports.",
      "Select a report and read its outcome, model details and the checks that actually ran.",
      "For an up-to-date result after a model change, go back to the first tab or to Workflows and run the checks again."
    ],
    "sections": [
      {
        "h": "Use a report",
        "bullets": [
          "A report records a check that finished. Opening it does not run the checks again or change the model.",
          "Read failed, skipped and incomplete sections as well as the summary. A report can be complete as a record while documenting an unsuccessful check."
        ]
      },
      {
        "h": "If the list is empty",
        "bullets": [
          "Run checks on Validation or in Workflows and use their report-saving controls. Reports kept with the current model appear here.",
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
    ]
  },
  "deploy": {
    "title": "Published",
    "lead": "Review changes for a live destination, publish them, restore an earlier version or move a model between release stages.",
    "start": [
      "Check the destination and account at the top of the page.",
      "Untick any change you want to keep back. The button names how many changes go and where.",
      "Press the button. The result line says what was published and offers Undo publish."
    ],
    "sections": [
      {
        "h": "Save and publish are different",
        "bullets": [
          "Save keeps your local model or editor changes. Publish sends the ticked model design changes to the live destination used by reports.",
          "The editing model, test model and publishing destination can differ. The bottom bar identifies each one."
        ]
      },
      {
        "h": "Changes, Earlier versions and Promote",
        "bullets": [
          "Changes compares your copy with the destination. A change that needs a table you did not tick says so and offers to tick it.",
          "Something only on the destination starts unticked. Tick it to remove it there.",
          "Every publish saves the destination first. Undo publish puts it back, and Earlier versions lists every saved version.",
          "Promote copies a model from one release stage to the next, such as test to production."
        ]
      },
      {
        "h": "What publishing does not do",
        "bullets": [
          "Publishing changes model definitions. It does not refresh the data. Local Undo does not reverse a completed write to the live server; use Undo publish."
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
          "A request names the action it asks for and the destination. Read its description rather than approving from the action name alone.",
          "Approval to read rows can cover several read operations for the stated time. Other approvals apply to the described action; the request explains the scope."
        ]
      },
      {
        "h": "Choose the amount of control",
        "bullets": [
          "The main switch turns these assistant permission checks on or off. Workflow checks are separate, under Team rules on Workflows.",
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
          "These explanations are saved with the model and are separate from the Description field in Properties. Give to your assistant puts a task on your Plan to write one."
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
    "title": "Plan > History",
    "lead": "See changes made by you and your assistant. Undo recent model edits or review the saved audit record.",
    "start": [
      "Open History on the Plan and find the change you want to inspect.",
      "Use Undo to here to reverse it and every later change, or Redo to here to bring undone changes back.",
      "Open a change to read who made it, what it changed and the check behind it."
    ],
    "sections": [
      {
        "h": "Undo model edits",
        "bullets": [
          "The timeline is shared by you and your assistant. Undo to here reverses the chosen change and every later change, not just one item in the middle.",
          "A set of changes applied together is one timeline entry and is undone together.",
          "This timeline covers the current session. Local Undo does not reverse every remote action, such as publishing to a server."
        ]
      },
      {
        "h": "Read the audit trail",
        "bullets": [
          "The saved audit trail records checked edits and other recorded actions. Its integrity check looks for a changed or missing link in the record.",
          "A check badge appears only when a recorded result is attached. An entry without a badge was not necessarily checked.",
          "Details contains the recorded result behind the summary. Integrity of the record does not itself prove the underlying model is correct."
        ]
      }
    ],
  },
  "workflows": {
    "title": "Workflows",
    "lead": "Follow a repeatable set of steps for a modelling task. Use a built-in workflow or create one for your project.",
    "start": [
      "Workflows is the list of your workflows. Press New workflow to describe what you want in words, or to use a starter, a copy you can change.",
      "Open a workflow to read and change its steps. Run puts it on the Plan as a row, where you answer its questions and follow each step.",
      "When a run ends, its report is in the Plan's History. Make it reusable there saves the run as a workflow of your own."
    ],
    "sections": [
      {
        "h": "Edit a workflow",
        "bullets": [
          "Steps shows each step in order: what it does, what you choose, what your assistant writes with its brief, and the checks. Canvas and Source are in the menu under View.",
          "Save writes at once. When the saved file changed somewhere else, or something needs a warning, Semanticus shows you the difference first and writes nothing until you choose.",
          "A starter stays as it is. Use makes a copy that is yours to change. Your assistant can edit your workflows too, with the same checks."
        ]
      },
      {
        "h": "Find your way around",
        "bullets": [
          "The list shows your workflows, with a mark for each step: a square for Semanticus, a circle where you choose, a diamond where your assistant writes.",
          "Search finds a workflow by name. Required, Off and Hidden show only the workflows in that state, starters included.",
          "Each workflow's Settings hold its notes for your assistant, the inputs it asks each run, where it is offered, what it is required for, and Repeat on the Plan.",
          "Your assistant can save a starter of your own, and make a whole model's checks warn instead of stop. These two stay with your assistant on purpose: ask it."
        ]
      },
      {
        "h": "Understand a step",
        "bullets": [
          "An action is a task the model tools can carry out. A check runs at a step and records whether it passed, failed or was not checked.",
          "A check that must pass can stop the run when it fails. A warning records the problem and lets the run go on. Read the result rather than treating a submitted answer as a passed check.",
          "Skipping or stopping a run keeps the recorded outcome; it does not turn an incomplete check into a pass."
        ]
      },
      {
        "h": "Team rules",
        "bullets": [
          "Team rules make a workflow the required way to do something, such as every new measure going through Test a complex measure. Someone working alone can ignore them. The one sign of a rule on the list is a Required mark.",
          "Checks in runs turns the checks inside runs on or off. Off skips those checks for new runs; actions a team rule requires still go through their workflow, and runs already underway keep the setting they started with. This is separate from assistant permissions.",
          "Team standard and Consulting delivery each apply a set of team rules at once, and show exactly what they change before you apply them. No team rules takes them all away."
        ]
      }
    ],
    "pro": "Workflows is a Semanticus Pro feature. The app still shows any run already going and anything waiting for you.",
    "seeAlso": [
      {
        "label": "Read the reports",
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
    "lead": "The notes your assistant keeps about this model, on the Notes tab of AI instructions.",
    "start": [
      "Open Edit, then AI instructions, then the Notes tab.",
      "Read the model overview and saved business context.",
      "Add or correct definitions, important calculations and known issues."
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
          "Saved workflows are reusable processes kept from earlier work. They live in Workflows, marked From a run, where Check and Check against past runs are in each one's menu.",
          "Recall searches for lessons relevant to your next task. Delete saved lessons removes lessons from future use for the chosen scope; it keeps the stored history."
        ]
      }
    ],
    "seeAlso": [
      {
        "label": "The other tab: AI instructions",
        "tab": "instructions"
      },
      {
        "label": "Check numbers you trust",
        "tab": "tests"
      },
      {
        "label": "Write a full model document",
        "tab": "docs"
      }
    ]
  },
  "instructions": {
    "title": "AI instructions",
    "lead": "Two tabs on one page. Instructions holds the standing words your assistant starts from. Notes holds the business context written for people.",
    "start": [
      "On the Instructions tab, read what is saved now. The bar shows how many characters are used of the number this model allows.",
      "Press Edit, write or correct the instructions, then press Save.",
      "Select the Notes tab for the model notes, the saved lessons and the saved workflows."
    ],
    "sections": [
      {
        "h": "What the two tabs hold",
        "bullets": [
          "Instructions is for your assistant. Every answer it gives starts from these words, so keep them short and true.",
          "Notes is for people. It says what the model covers, what the business terms mean and what earlier work taught.",
          "Both are written background for the same model, which is why they share one page. Instructions travels inside the model; the notes are saved beside it."
        ]
      },
      {
        "h": "What belongs here",
        "bullets": [
          "Write the background an assistant needs before it answers: which measure to prefer, what a business term means here, how to read a date, and what to do when a question is unclear.",
          "Notes for people are a different thing and live on the Notes tab. There is a link to it at the bottom of this tab.",
          "Keep it short and true. This model sets the character limit, and the page shows how much of it is used."
        ]
      },
      {
        "h": "One saved text, wherever it is edited",
        "bullets": [
          "The words are stored in the model itself, not on this computer, so they travel with it.",
          "This page reads and writes the same saved text your assistant reads and writes. There is no second copy of it.",
          "Save here and the VS Code view updates at once. Your assistant sees the change on its next call."
        ]
      },
      {
        "h": "Suggestions wait for a person",
        "bullets": [
          "Your assistant can suggest an addition. It waits on this page and nothing is added until someone presses Accept.",
          "Accepted words go straight into the saved instructions, where you can still edit them."
        ]
      }
    ],
    "seeAlso": [
      { "label": "The other tab: Model notes", "tab": "knowledge" },
      { "label": "AI readiness", "tab": "readiness" }
    ]
  }
};
// The legacy Publish routes land on the Publish page, so they show its notes.
HELP.compare = HELP.deploy;
HELP.dataagent = HELP.deploy;

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
      { q: 'I am new to Semanticus. Where should I begin?', a: 'Open a model from the Semanticus sidebar. Read Edit > AI instructions, Notes tab, for its business context, open Explore > Diagram to see how the tables connect, then select a calculation in the Model list. Each Studio page has Getting started steps and a detailed Help guide.', tab: 'knowledge' },
      { q: 'What is the difference between editing, testing and publishing?', a: 'Editing changes your working model. Testing asks a live model for answers. Publishing sends reviewed changes to a live destination. These can be different models; check the bottom bar and use Connections to change them.' },
      { q: 'Can I work without a live connection?', a: 'Yes. Open model files to inspect and edit their structure, formulas and descriptions. Running queries, measuring performance and testing real answers needs a live model with data.' },
      { q: 'Give my assistant the Semanticus guides', a: 'Run Semanticus: Install or Update Assistant Skills from the VS Code command palette. Choose your assistant, then refresh or restart it if needed. These guides use your existing Semanticus connection.' },
      { q: 'Check a business answer against a number I trust', a: 'Review > Validation > New check. Choose a known answer and enter the calculation, expected value and the filters the answer applies to.', tab: 'tests' },
      { q: 'Check model results against source data', a: 'Review > Validation > New check > Check against SQL. Review the SQL that supplies the expected answer and connect to its server and database. This needs a SQL connection as well as the live model.', tab: 'tests' },
      { q: 'Read the saved check and workflow reports', a: 'Review > Validation > Reports lists the reports kept with this model. Open one to read its results, date and coverage.', tab: 'evidence' },
      { q: 'Change when the assistant asks for permission', a: 'Permissions shows the current settings and waiting requests. Workflow requirements are a separate choice, under Team rules on Workflows.', tab: 'permissions' },
    ],
  },
  {
    group: 'Common terms explained',
    items: [
      { q: 'Semantic model, table, column and measure', a: 'A semantic model organises data and business calculations for reports. Tables contain rows; columns hold values such as date or customer. A measure calculates an answer, such as total sales.' },
      { q: 'DAX and filter context', a: 'DAX is the formula language for model calculations. Filter context is the set of values used for an answer, such as sales for a particular product, region and year.', tab: 'daxlab' },
      { q: 'Power Query, M and partition', a: 'Power Query prepares data for loading. M is its formula language. A partition is a section of a model table with its own loading query.', tab: 'mcode' },
      { q: 'XMLA endpoint, SQL endpoint and tenant', a: 'An endpoint is a connection address. XMLA connects to a semantic model; a SQL endpoint connects to source tables, such as a Fabric warehouse or lakehouse. A tenant identifies the Microsoft organisation your account signs into.' },
      { q: 'Workflow, starter and team rules', a: 'A workflow is a saved sequence of steps, and it is yours to change. A starter is a workflow that comes with Semanticus: Use makes a copy of it for you. Team rules say which workflows must be used for a task and how their checks apply. A check runs at a step and records whether it passed, failed or was not checked.', tab: 'workflows' },
      { q: 'Report, coverage and checking against SQL', a: 'A report is the recorded result of a check, kept under Review > Validation > Reports. Coverage says what was checked and what was left out. A check against SQL compares model results with an independent source answer.', tab: 'tests' },
      { q: 'Model quality, finding and override', a: 'Model quality checks the model against a library of design, naming and performance rules. A finding is an issue raised by a check. An override records your decision to accept that issue without fixing it.', tab: 'bpa' },
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
      { q: 'Rename or delete', a: 'Select it, then F2 to rename in the Properties Name row (DAX references and synonyms follow automatically), or right-click → "Delete" (references are NOT rewritten, so check dependents first).' },
      { q: 'Create a relationship', a: 'Diagram tab: drag column → column. Or Model tree: right-click the many-side column → "New Relationship from Column…" and pick the lookup column from the list.', tab: 'diagram' },
      { q: 'Bulk-edit DAX or TMDL as a script', a: 'Right-click object(s) → "Script ▸ DAX/TMDL (editable)": edit the script, then the ▶ "Apply" button in the editor title writes it back as one undoable change.' },
      { q: 'Edit several objects at once', a: 'Multi-select in the Model tree; the Properties view switches to multi-edit (shared properties; one change applies to all).' },
    ],
  },
  {
    group: 'Explore and Edit (Studio)',
    items: [
      { q: 'Field parameters, calc groups, calendars, perspectives, RLS/OLS, DaxLib', a: 'Edit > Advanced modelling holds Power Query and six guided builders: Calendars, Calculation groups, Field parameters, RLS / OLS, Perspectives and DaxLib. The builders are also on the Model view "…" menu.', tab: 'advmodels' },
      { q: 'Try a calculation and see which filters affect its answer', a: 'Edit > Queries, in Visual or Query mode against a live model.', tab: 'daxlab' },
      { q: 'Check whether two DAX formulas return the same answers', a: 'Edit > Queries > Verify. Choose relevant fields, compare both formulas and read which contexts were checked.', tab: 'daxlab' },
      { q: 'Compare a first run with a repeat run', a: 'Edit > Queries > Performance, then Cold / Warm.', tab: 'daxlab' },
      { q: 'Edit the load steps or incremental refresh', a: 'Edit > Advanced modelling > Power Query, or right-click a table and choose Edit M Code.', tab: 'mcode' },
      { q: 'Preview table rows', a: 'Explore > Data, or right-click a table and choose Preview Data.', tab: 'data' },
      { q: 'Find what depends on a field, and what nothing uses any more', a: 'Explore > Lineage > Impact, or right-click an object and choose Show Lineage & Impact.', tab: 'lineage' },
      { q: 'See which tables use the most memory', a: 'Explore > Size. Choose Scan storage against a live model to rank the largest tables and columns.', tab: 'stats' },
      { q: 'Refresh a partition', a: 'Model tree → expand the table → right-click the partition → "Refresh Partition…" (pick a refresh type, dry-run, confirm).' },
    ],
  },
  {
    group: 'Review, Publish and Workflows (Studio)',
    items: [
      { q: 'Score and fix how well AI understands the model', a: 'Review > AI readiness. Apply a safe fix, or copy a ready prompt for the rest.', tab: 'readiness' },
      { q: 'Find design, naming and performance problems', a: 'Review > Model quality. Fix one finding, apply several fixes at once, or send them to Plan > Pending changes for review.', tab: 'bpa' },
      { q: 'Review a set of changes before applying', a: 'Plan > Pending changes. Keep or reject each item, then apply the rest as one undoable step.', tab: 'optimize' },
      { q: 'Publish the open model to a live workspace', a: 'Open Publish. Tick the changes to send, then press the button at the top right. It names the changes and the destination. Ctrl+S saves and never publishes.', tab: 'deploy' },
      { q: SAFETY_CHECK_COPY.helpQuestion, a: `${SAFETY_CHECK_COPY.explainer} ${SAFETY_CHECK_COPY.helpTail}`, tab: 'plan' },
      { q: 'Compare the open model with the published one', a: 'Publish > Published > Changes compares your copy with the publish destination.', tab: 'deploy' },
      { q: 'Copy objects from another model', a: 'The Reference Model view (side bar): "Set Reference Model…", then right-click → "Copy into Open Model" (or Ctrl+C there, Ctrl+V in the Model tree).' },
      { q: 'Generate documentation', a: 'Review > Docs. Compose the guide, brand it, add your own explanations, then print to PDF.', tab: 'docs' },
      { q: 'Publish a Fabric Data Agent', a: 'Ask your assistant. The Data Agent screen is hidden for now, and your assistant can still choose the model data, add its guidance and publish.' },
      { q: 'Edit a workflow', a: 'Workflows > Author. Steps, Canvas and Source edit one shared draft. Save workflow opens a full preview before the saved file changes.', tab: 'workflows' },
      { q: 'Recover a workflow after the file changed', a: 'Workflows > Author. Choose Reload saved file to start from the newer file, or Review current file to compare it with your draft before keeping your work.', tab: 'workflows' },
      { q: 'Check a workflow before running it', a: 'Workflows > Author. Read the workflow checks and warnings, then start a run only after the steps and answers match the task.', tab: 'workflows' },
    ],
  },
  {
    group: 'Your assistant and safety',
    items: [
      { q: 'Connect your assistant to this model', a: 'Command palette > "Semanticus: Connect AI Assistant" writes the connection file. Nothing is connected until you refresh or restart your assistant so it reads that file. Keep Semanticus open in VS Code.' },
      { q: 'See what your assistant changed', a: 'Plan > History lists every change and who made it, newest first. Choose Your assistant to see only its changes. Undo to here takes a change back, with any made after it.', tab: 'plan' },
      { q: 'Undo or redo a change', a: 'Open Plan > History and use Undo to here or Redo to here on a row, or the palette commands "Semanticus: Undo" and "Semanticus: Redo". A set of changes undoes as one step. Publishing to a live server is separate and is not reversed by local Undo.', tab: 'plan' },
      { q: 'Run a workflow that records what it checked', a: 'Workflows runs steps whose checks are recorded as you go. Workflows is a Pro feature.', tab: 'workflows' },
      { q: 'Turn workflow checks off for a quick task', a: 'Workflows > Team rules > Checks in runs. Off means new runs skip the checks inside them; actions a team rule requires still go through their workflow. Runs already underway keep their original setting.', tab: 'workflows' },
      { q: 'Write the standing instructions your assistant starts from', a: 'Edit > AI instructions, Instructions tab. Write them once, save them in the model, and accept or reject anything your assistant suggests adding.', tab: 'instructions' },
      { q: 'Save business context and lessons from earlier work', a: 'Edit > AI instructions, Notes tab, holds the model guide, saved insights, reusable workflows and a search over your notes.', tab: 'knowledge' },
      { q: 'Accept a finding without fixing it', a: 'Use Accept on Review > AI readiness or Review > Model quality. A reason is required, and the finding stays visible as overridden.', tab: 'readiness' },
    ],
  },
  {
    group: 'Setup and housekeeping',
    items: [
      { q: 'Open or connect a model', a: '"Semanticus: Open Model…" (tree toolbar or palette): a file (.bim/TMDL/PBIP), a running Power BI Desktop, or an XMLA endpoint (recent connections remembered).' },
      { q: 'Search the whole model', a: '"Semanticus: Find in Model" (the search icon on the Model view): names, descriptions and DAX.' },
      { q: 'Save the model to disk', a: 'The Save icon on the Model view (TMDL beside the model).' },
      { q: 'Format DAX', a: 'In any DAX editor: Format Document (offline), or right-click → "Semanticus: Format DAX with DAX Formatter (online)".' },
    ],
  },
];

// ---------------------------------------------------------------------------------------------------

function SectionBlock({ s }: { s: HelpSection }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="text-xs font-semibold" style={{ color: 'var(--sem-fg)' }}>{s.h}</div>
      {s.body && <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>{s.body}</div>}
      {s.bullets && (
        <ul className="flex flex-col gap-1">
          {s.bullets.map((p, i) => (
            <li key={i} className="text-xs flex gap-2" style={{ color: 'var(--sem-muted)' }}>
              <span style={{ color: 'var(--sem-muted)' }}>•</span><span>{p}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function TabGuide({ h, onGo }: { h: TabHelp; onGo?: (tab: string) => void }) {
  return (
    <div className="flex flex-col gap-4">
      <div className="text-(length:--sem-text-body)" style={{ color: 'var(--sem-fg)' }}>{h.lead}</div>
      <GettingStarted steps={h.start} />
      {h.sections.map((s, i) => <SectionBlock key={i} s={s} />)}
      {ACTION_HELP_TITLES.has(h.title) && <SharedActionHelp />}
      {(h.pro || h.tip) && (
        // The Pro note and the tip are two insets of one block: in the page's 16 px column they would stand 16 px
        // apart, and two insets side by side are 12 px apart everywhere else.
        <div className="flex flex-col gap-3">
          {h.pro && (
            <div className="sem-inset text-xs" style={{ color: 'var(--sem-muted)' }}>
              <span className="font-semibold" style={{ color: 'var(--sem-fg)' }}>Pro · </span>{h.pro}
            </div>
          )}
          {h.tip && <div className="sem-inset text-xs" style={{ color: 'var(--sem-muted)' }}>{h.tip}</div>}
        </div>
      )}
      {h.seeAlso && h.seeAlso.length > 0 && (
        <div className="flex flex-col gap-1">
          <div className="text-xs font-semibold" style={{ color: 'var(--sem-fg)' }}>Looking for…</div>
          {h.seeAlso.map((s, i) => (
            <div key={i} className="text-xs flex gap-2" style={{ color: 'var(--sem-muted)' }}>
              <span style={{ color: 'var(--sem-muted)' }}>→</span>
              <span>
                {s.tab && onGo
                  ? <button className="underline" style={{ color: 'var(--sem-fg)' }} onClick={() => onGo(s.tab!)}>{s.label}</button>
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
        value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter tasks… e.g. measure, publish, undo"
        className="sem-field w-full"
      />
      {groups.length === 0 && <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No tasks match “{q}”.</div>}
      {groups.map((g) => (
        <div key={g.group} className="flex flex-col gap-2">
          <div className="text-xs font-semibold" style={{ color: 'var(--sem-fg)' }}>{g.group}</div>
          {g.items.map((it, i) => (
            <div key={i} className="flex flex-col gap-1 border-b pb-2" style={{ borderColor: 'var(--sem-rule)' }}>
              <div className="text-xs font-medium" style={{ color: 'var(--sem-fg)' }}>
                {it.tab && onGo
                  ? <button className="underline text-left" style={{ color: 'var(--sem-fg)' }} onClick={() => onGo(it.tab!)}>{it.q}</button>
                  : it.q}
              </div>
              <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>{it.a}</div>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}

function GettingStarted({ steps }: { steps: string[] }) {
  return <ol className="list-decimal pl-4 flex flex-col gap-2 text-xs leading-5" style={{ color: 'var(--sem-fg)' }}>
    {steps.map((step, index) => <li key={index}>{step}</li>)}
  </ol>;
}

/** One row of the Help menu. `section` starts a new named group directly above the row. */
interface HelpItem { id: string; label: string; note?: string; section?: string; title?: string; keepOpen?: boolean; run: () => void; }

// The Plan's tour and What changed card, back on request (Plan-heart contract 10). They head the Help menu on
// every page; from another page they go to the Plan first, because the tour points at the Plan's own parts.
// "What changed in this version" is only offered when this build ships a card.
export function guideHelpItems(tab: string, onGo?: (tab: string) => void): HelpItem[] {
  const open = (mode: 'tour' | 'card') => { if (tab !== 'plan') onGo?.('plan'); requestPlanGuide(mode); };
  const items: HelpItem[] = [{ id: 'tour', label: 'Show the tour', title: 'Six short steps on the Plan', run: () => open('tour') }];
  if (WHAT_CHANGED[WHAT_CHANGED_CARD_ID]) {
    items.push({ id: 'what-changed', label: 'What changed in this version', title: 'The few things that work differently now', run: () => open('card') });
  }
  return items;
}

/** The top of Help's slide-over on the Plan: the tour and the card, for a refresher. */
export function PlanGuideReopen({ onGo, onDone }: { onGo?: (tab: string) => void; onDone?: () => void }) {
  const [tour, card] = guideHelpItems('plan', onGo);
  const pick = (item: HelpItem) => { onDone?.(); item.run(); };
  return <div className="plan-guide-reopen">
    <span className="plan-guide-reopen-lead">New here, or want a refresher?</span>
    <span className="plan-guide-reopen-acts">
      <button type="button" className="sem-btn sem-btn-sm sem-btn-primary" onClick={() => pick(tour)}>{tour.label}</button>
      {card && <button type="button" className="sem-btn sem-btn-sm sem-btn-quiet" onClick={() => pick(card)}>{card.label}</button>}
    </span>
  </div>;
}

// The "?" on the Plan header opens Help's slide-over at the Plan. HelpButton owns the slide-over, so this asks it
// through one window event rather than growing a second copy of the panel.
const OPEN_HELP_EVENT = 'semanticus:open-help';
export function openHelpGuide(): void {
  try { window.dispatchEvent(new Event(OPEN_HELP_EVENT)); } catch { /* no window */ }
}
export function PlanHelpMark() {
  return <button type="button" className="plan-help-mark" aria-label="Help for the Plan" title="Help and the tour"
    onClick={openHelpGuide}>?</button>;
}

// Help is a small menu, not one button with one destination. The page guide keeps first place, the shortcuts
// sheet stops being a footnote inside the guide, and "Something wrong?" gives a stuck engine a visible way out
// of Studio (Restart engine was Command-Palette-only, which is not a place a person looks when nothing responds).
export function HelpButton({ tab, onGo, onShortcuts, onTextSize, onAllTools, zoom }: {
  tab: string; onGo?: (tab: string) => void; onShortcuts?: () => void;
  onTextSize?: (direction: 'smaller' | 'larger' | 'reset') => void;
  onAllTools?: () => void; zoom?: number;
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
  // Esc closes the slide-over (part of the keyboard suite: every Studio overlay closes on Escape).
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
  useEffect(() => {
    const show = () => { setOpen(false); setView('tab'); setGuideOpen(true); };
    window.addEventListener(OPEN_HELP_EVENT, show);
    return () => window.removeEventListener(OPEN_HELP_EVENT, show);
  }, []);

  // Six rows and no group headings (they named groups of one or two). Text size is one row of three small controls,
  // and it stays open so a person can step more than once.
  const items: HelpItem[] = [...guideHelpItems(tab, onGo)];
  items.push({ id: 'page', label: 'Help for this page', note: h?.title,
    title: h ? `Guide: the ${h.title} tab (and “Where do I…?”)` : 'Help for this page',
    run: () => { setView('tab'); setGuideOpen(true); } });
  // The header's Find a tool moved here: it opens the same menu of every tool.
  if (onAllTools) items.push({ id: 'all-tools', label: 'All tools', title: 'Every tool in Studio, by area', run: onAllTools });
  if (onShortcuts) items.push({ id: 'shortcuts', label: 'Keyboard shortcuts', note: '?',
    title: 'Every key you can press in Studio', run: onShortcuts });
  // The same control as Ctrl and the wheel, for anyone who would rather not scroll for it. Between 80% and 130%.
  if (onTextSize) {
    items.push({ id: 'textsize-smaller', label: 'Smaller', keepOpen: true,
      title: 'Make everything in Studio a step smaller, down to 80%. Ctrl+scroll does the same.', run: () => onTextSize('smaller') });
    items.push({ id: 'textsize-reset', label: 'Reset', keepOpen: true,
      title: 'Put Studio back to its normal size. Ctrl+0 does the same.', run: () => onTextSize('reset') });
    items.push({ id: 'textsize-larger', label: 'Larger', keepOpen: true,
      title: 'Make everything in Studio a step bigger, up to 130%. Ctrl+scroll does the same.', run: () => onTextSize('larger') });
  }
  items.push({ id: 'restart', label: 'Restart engine',
    title: 'Start the Semanticus engine again. It asks first if you have unsaved edits.',
    run: () => runHostCommand('semanticus.restartEngine') });

  const pick = (item: HelpItem, fromKeyboard: boolean) => {
    if (item.keepOpen) { item.run(); return; }
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
        className="studio-header-action shrink-0">Help</button>
      {open && (
        <div ref={menuRef} tabIndex={-1} role="menu" aria-label="Help" className="studio-chip-menu studio-help-menu studio-header-pop absolute right-0 top-full mt-1"
          onKeyDown={(e) => {
            if (e.key === 'Escape') { e.preventDefault(); closeAndReturnFocus(); }
            else if (e.key === 'ArrowDown') { e.preventDefault(); setHighlight((h2) => (h2 + 1 + items.length) % items.length); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight((h2) => (h2 <= 0 ? items.length : h2) - 1); }
            else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); const it = items[highlight]; if (it) pick(it, true); }
            else if (e.key === 'Tab') { setOpen(false); }
          }}>
          {items.map((item, i) => item.id === 'textsize-reset' || item.id === 'textsize-larger' ? null
            : item.id === 'textsize-smaller' ? <div key="textsize" className="studio-help-textsize" role="group" aria-label="Text size">
              <span>Text size</span>
              {[items[i], items[i + 1], items[i + 2]].map((control, k) => <button key={control.id} ref={(el) => { itemRefs.current[i + k] = el; }} role="menuitem" type="button"
                title={control.title} aria-label={control.label} className={i + k === highlight ? 'is-highlighted' : undefined}
                onMouseEnter={() => setHighlight(i + k)} onClick={() => pick(control, false)}>
                {k === 0 ? '−' : k === 2 ? '+' : `${zoom ?? 100}%`}
              </button>)}
            </div>
            : <Fragment key={item.id}>
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
            style={{ width: 460, maxWidth: '92vw', background: 'var(--sem-bg)', borderLeft: '1px solid var(--sem-border)', boxShadow: '-8px 0 24px rgba(0,0,0,0.28)' }}>
            {/* Instrument: the panel keeps the page margin on both sides, a rule under its head, and a segmented
                switch between the two views of the guide. */}
            <div className="flex items-center gap-2 px-6 border-b" style={{ borderColor: 'var(--sem-rule)', minHeight: 'var(--sem-bar-h)' }}>
              <span className="sem-card-title flex-1">{view === 'tab' ? (h?.title ?? 'Help') : 'Where do I…?'}</span>
              <div className="sem-seg" role="group" aria-label="Guide view">
                <button type="button" onClick={() => setView('tab')} className="sem-seg-item" aria-pressed={view === 'tab'}>This tab</button>
                <button type="button" onClick={() => setView('where')} className="sem-seg-item" aria-pressed={view === 'where'}>Where do I…?</button>
              </div>
              <button type="button" onClick={() => setGuideOpen(false)} aria-label="Close help" className="sem-btn sem-btn-sm w-6 px-0" style={{ borderColor: 'transparent', color: 'var(--sem-muted)' }}>✕</button>
            </div>
            <div className="flex-1 overflow-auto px-6 py-4">
              {view === 'tab' && tab === 'plan' && <PlanGuideReopen onGo={onGo} onDone={() => setGuideOpen(false)} />}
              {view === 'tab'
                ? (h ? <TabGuide h={h} onGo={go} /> : <div className="text-xs" style={{ color: 'var(--sem-muted)' }}>No guide for this view yet. Try “Where do I…?”.</div>)
                : <WhereIndex onGo={go} />}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
