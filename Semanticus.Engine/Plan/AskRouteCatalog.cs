// The catalogue the box routes over: every place a person can be sent, and the one line that describes it.
//
// WHERE THESE WORDS COME FROM. Each line below is copied verbatim from the `lead` of the matching entry in
// Semanticus.VSCode/webview/src/help.tsx (the `HELP` table, one entry per tool). The id and the area are from
// Semanticus.VSCode/webview/src/route.ts (`ToolId` and `DESTINATION_OF_TOOL`), and the label is the one the
// area strip shows, from `TAB_GROUPS` in Semanticus.VSCode/webview/src/App.tsx. They are copied rather than
// read at run time because the engine ships without the webview and must route the same way headless; the
// test AskRouteCatalogTests keeps the copy honest against help.tsx itself, so a wording change in one place
// fails rather than drifts.
//
// THE SEVEN AREAS. Kane picked Option A from the hierarchy mockup on 22 September 2026
// (docs/redesign/ui-hierarchy.md, "Kane's pick"): Overview, Plan, Explore, Edit, Review, Publish, Workflows.
// They replace the five this catalogue used to carry (model, calc, checks, changes, workflows). Analyse was
// renamed Review in the same decision, so nothing here is called Analyse.
//
// TWENTY-TWO PLACES, EIGHTEEN OF THEM ON THE STRIP. help.tsx carries the same entries plus three that are
// in no area strip at all (compare, dataagent, permissions): a router that answered with a place the strip
// cannot show would be sending a person nowhere. The catalogue used to be exactly the strip, and now it is
// the strip PLUS the places that live as a TAB INSIDE another page. Today that is four: Reports on
// Validation, Pending changes and Model Spec on Plan, and Model notes on AI instructions. A tab is a real
// destination, so it is routable; it is just not a button on the strip. AskRouteTests reads the strip out of
// App.tsx and holds it to this catalogue minus the entries flagged IsTab, so the next tool added fails a
// test instead of quietly never being routed to.
//
// WHAT A CRITERION IS. The wording rule from Decisions/MeasureDefaultsPoint.cs applies here too: a criterion
// describes THE WORLD, never the product's state and never the answer we hope for. A tool's criterion is its
// own label and its own guide line and nothing else. No criterion mentions how often a tool is chosen, what
// the person opened last, or what the plan thinks, because feeding our own state back would make the service
// agree with us and call it evidence.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>The plain words a person types when they mean one place.
    ///
    /// NOUNS are the things that place is about; VERBS are what a person does to them. The split is not
    /// decoration: a noun and a verb matching together is much stronger evidence than two nouns, because
    /// "rename Total Sales" and "what breaks if I rename Total Sales" are different questions and the only
    /// thing telling them apart is which word is carrying the weight. Scoring is in AskRoutePoint.MatchWords.
    ///
    /// RELATIONS are a third class and they are the strongest, because a relation word is the SUBJECT of
    /// the question rather than a modifier of it. "What uses this measure" is not a question about
    /// measures, it is a question about what uses things, and the measure is only the object. A relation
    /// therefore outranks a noun and outranks a two-word noun phrase as well (Codex P2 on PR 386: the box
    /// answered that ask with the Queries page, which knows nothing about what uses what). The class lives
    /// on the word list and not on the tool, so another place could claim one; today only Lineage does,
    /// because dependency is the one thing this product has a page for.
    ///
    /// An entry may be a PHRASE ("date table", "who can see"), matched as a run of adjacent words. A phrase
    /// is worth more than a single word for the obvious reason: nobody types "who can see" by accident.
    ///
    /// Every word here is lowercase and is matched on its stem, so the singular and the plural both hit.
    /// Words a person would type about ANY place ("model", "page", "this") are deliberately absent: a word
    /// every tool claims tells the router nothing and only makes ties.</summary>
    public sealed class AskRouteWords
    {
        public AskRouteWords(string nouns, string verbs, string relations = null)
        {
            Nouns = Split(nouns);
            Verbs = Split(verbs);
            Relations = Split(relations);
        }

        public IReadOnlyList<string> Nouns { get; }
        public IReadOnlyList<string> Verbs { get; }

        /// <summary>The words that ask how things are connected. Empty for nearly every place.</summary>
        public IReadOnlyList<string> Relations { get; }

        private static string[] Split(string list) =>
            string.IsNullOrWhiteSpace(list)
                ? Array.Empty<string>()
                : list.Split(',').Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
    }

    /// <summary>One place a person can be sent.</summary>
    public sealed class AskRouteTool
    {
        public AskRouteTool(string id, string area, string label, string guide, AskRouteWords words, bool isTab = false)
        {
            Id = id;
            Area = area;
            Label = label;
            Guide = guide;
            Words = words ?? new AskRouteWords(null, null);
            IsTab = isTab;
        }

        /// <summary>The stable tool id the extension routes on, for example "lineage".</summary>
        public string Id { get; }

        /// <summary>One of plan, explore, edit, review, publish, workflows.</summary>
        public string Area { get; }

        /// <summary>What the area strip calls it, or what the tab is called when <see cref="IsTab"/> is true.</summary>
        public string Label { get; }

        /// <summary>The one line from the help page, verbatim.</summary>
        public string Guide { get; }

        /// <summary>The plain words that mean this place. Read by the first pass, which needs no service and
        /// no permission; the criterion below is only for the second pass.</summary>
        public AskRouteWords Words { get; }

        /// <summary>True when this place is a TAB INSIDE another page rather than a button on the area strip.
        /// It is still a destination and still routable; the strip just cannot show it directly, so the test
        /// that holds this catalogue to App.tsx takes the strip to be the catalogue minus these.</summary>
        public bool IsTab { get; }

        /// <summary>The criterion the router sees: the label and the guide line, nothing else.</summary>
        public string Criterion => Label + ". " + Guide;
    }

    public static class AskRouteCatalog
    {
        /// <summary>The option id that means "none of these fits". It is a real answer, not a failure: a person
        /// who asks something no tool covers is owed the menu, not a wrong door.</summary>
        public const string NoTool = "none";

        /// <summary>Where a question about the numbers is answered (Ask your data), and where a query is written by
        /// hand on every tier (Queries). Named once because the question reading of a line always names one of them
        /// (AskRoutePoint.Readings): Ask your data where the tier has it, Queries where it does not.</summary>
        public const string DataTool = "askdata";
        /// <summary>The Data page: sample rows to browse, which answers no question.</summary>
        public const string RowsTool = "data";
        public const string QueriesTool = "daxlab";

        /// <summary>The six areas, in strip order. Read by the tests rather than retyped there, so an area
        /// added or renamed is one edit. There were seven until the Overview retired (Plan-heart slice 11, lane
        /// R1): the Plan is the first area and the start page, and the Overview's words route there.</summary>
        public static readonly IReadOnlyList<string> Areas =
            new[] { "plan", "explore", "edit", "review", "publish", "workflows" };

        private static readonly AskRouteTool[] AllTools =
        {
            // The Overview retired in slice 11 (Plan-heart lane R1). Its words route to the Plan, which is the
            // start page and carries the model's facts and the selected object's actions now.
            new AskRouteTool("plan", "plan", "Plan",
                "One living plan for this model: what it should become, the gaps, and the next thing worth doing about it.",
                new AskRouteWords(
                    nouns: "plan, next, next thing, next step, priority, roadmap, backlog, to do, todo, what should i do, what is left, "
                         + "overview, glance, whole model, everything at once",
                    verbs: "")),
            // Pending changes and Model Spec are VIEWS INSIDE Plan since Kane's call on 23 September (the T266
            // amendment), not buttons of their own. Both are still real destinations and still routable; the
            // strip just reaches them through Plan. The labels do not change: somebody who knows where "Model
            // Spec" is should still find it under that name.
            new AskRouteTool("optimize", "plan", "Pending changes",
                "Review pending edits together before applying them. See each change and choose which ones to keep. It opens as a panel on the Plan page, from a Review these together button, from Help > All tools, from a saved link, or when your assistant sends you there. Each change in a set also sits on the plan, under the item it belongs to, where you can keep or drop it. A change no item claims gets an item of its own.",
                new AskRouteWords(
                    nouns: "pending change, proposed change, proposal, batch, staged edit",
                    verbs: "approve, apply, review"),
                isTab: true),
            new AskRouteTool("spec", "plan", "Model Spec",
                "Plan the tables, relationships and calculations before building them. The draft stays separate from the model until you build it. It opens as a panel on the Plan page, from Help > All tools, from a saved link, or when your assistant sends you there. On a model with no tables, the Plan's From a SQL source start opens it too.",
                new AskRouteWords(
                    // The SQL words are the wizard's other start (plan statement check, 28 Sep: "build the model from
                    // my SQL database" found no place). They name the MODEL being made from SQL on purpose: "sql
                    // source", "sql server" and "sql endpoint" on their own are how people ask for a SQL check
                    // (Validation) or where the endpoint is saved (Connections) (verifier M2), and "from the sql" on its
                    // own is in "validate the numbers from the sql source" and "refresh the data from the sql endpoint"
                    // too (verifier R2-M2). Only a request to make a model from a source is this page's (AskMakeIntent).
                    nouns: "spec, blueprint, draft, design, from scratch, greenfield, new model wizard",
                    // Whether an ask is a request to MAKE a model from a source is AskMakeIntent's call, made before the
                    // words are scored (AskRoutePoint.MatchWords): "refresh the model from the sql endpoint" is not a new model.
                    verbs: "plan out, sketch, build from"),
                isTab: true),
            new AskRouteTool("data", "explore", "Data",
                "Browse a sample of the rows stored in a live model. Use this to understand the values behind your reports.",
                new AskRouteWords(
                    nouns: "row, rows, value, values, sample, preview, actual data",
                    verbs: "browse, peek")),
            new AskRouteTool("diagram", "explore", "Diagram",
                "See how your tables connect. Arrange the map and create or edit the links between tables.",
                new AskRouteWords(
                    nouns: "diagram, relationship, relationships, join, joins, link, links, star schema, foreign key, cardinality",
                    verbs: "connect, arrange, relate, create, add, new, make")),
            new AskRouteTool("lineage", "explore", "Lineage",
                "See what a calculation uses and what relies on it. Check what a change would affect, and find what nothing uses any more.",
                new AskRouteWords(
                    nouns: "lineage, dependency, dependencies, impact, downstream, upstream, break, blast radius, knock on",
                    // "rename" stays an ordinary verb: it is the THING being asked for on Find and replace
                    // and only the verb in a question about impact, and those two numbers are what tell
                    // "rename Total Sales" from "what breaks if I rename Total Sales" apart.
                    verbs: "rename",
                    relations: "use, used, depend, affect, affected, breaks if, reference, referenced, feed, relies on, rely on")),
            new AskRouteTool("stats", "explore", "Size",
                "Find the tables and columns using the most model memory. Review opportunities to reduce their size.",
                new AskRouteWords(
                    // "most memory" is what tells a size question from a dependency question when both
                    // carry the word "use": using the most memory is consumption, not dependency, and
                    // Size says so in its own words rather than by a rule about who wins ties.
                    nouns: "size, memory, most memory, mb, gb, kb, big, large, footprint, heaviest",
                    verbs: "shrink, reduce")),
            // Ask your data (Kane, 26 September): a question about the model's numbers. The box only NAVIGATES here;
            // the page asks. It claims the words of a question about figures ("total sales by region"), which Queries
            // (where DAX is written) and Data (where rows are browsed) do not.
            new AskRouteTool("askdata", "explore", "Ask your data",
                "Type a question about your numbers, or choose a measure and the columns to split it by. Semanticus builds, checks and runs the query, and keeps the question.",
                new AskRouteWords(
                    // Phrases, not the bare nouns: "Total Sales" is also a measure a person renames or traces, so
                    // "sales" alone would pull those asks here. "sales by" is a question about the figures.
                    nouns: "ask your data, data question, top, bottom, breakdown, how much, "
                         + "sales by, revenue by, units by, margin by, profit by, cost by, orders by, total by",
                    verbs: "by, per, split by, broken down by")),
            new AskRouteTool("search", "edit", "Find and replace",
                "Find names, descriptions and formulas across the model. Review replacements before changing anything.",
                new AskRouteWords(
                    nouns: "rename, search, replacement, find and replace, replace",
                    verbs: "find, look for, sweep")),
            new AskRouteTool("daxlab", "edit", "Queries",
                "Try calculations, compare answers and investigate slow queries. Experiments here do not change the saved model.",
                new AskRouteWords(
                    nouns: "measure, kpi, dax, formula, calculation, expression, query, calculated column, calc column, calculated table, calc table, function, udf, slow query",
                    verbs: "create, add, new, make, write, edit, try, optimise, optimize, speed up, tune")),
            // Model notes is a TAB on AI instructions since Kane folded the two together (23 September). Same
            // label, same words, reached through AI instructions rather than from the strip.
            new AskRouteTool("knowledge", "edit", "Model notes",
                "The notes your assistant keeps about this model, on the Notes tab of AI instructions.",
                new AskRouteWords(
                    nouns: "note, notes, business context, glossary, model notes",
                    verbs: "remember, jot"),
                isTab: true),
            new AskRouteTool("instructions", "edit", "AI instructions",
                "Two tabs on one page. Instructions holds the standing words your assistant starts from. Notes holds the business context written for people.",
                new AskRouteWords(
                    // "ai" on its own belongs to AI readiness, so this place claims the PHRASE and the plain
                    // word "instruction" instead. A phrase is worth one more than a single word, which is
                    // what puts "ai instructions" here and leaves a bare "ai" question where it was.
                    nouns: "instruction, ai instruction, assistant instruction, house rule, standing order",
                    verbs: "instruct")),
            new AskRouteTool("advmodels", "edit", "Advanced modelling",
                "Build reusable calculations, calendars and report choices, or control which data people can see.",
                new AskRouteWords(
                    nouns: "calendar, date table, time intelligence, ytd, mtd, qtd, year to date, role, rls, row level security, security, who can see, perspective, calculation group, calc group, calculation item, field parameter, daxlib",
                    verbs: "mark, restrict, create, add, new, make")),
            new AskRouteTool("mcode", "edit", "Power Query",
                "Edit Power Query steps that load and transform data. Changes take effect when the model is refreshed.",
                new AskRouteWords(
                    nouns: "power query, m code, m query, partition, source query, etl, step, steps, mashup",
                    // "edit" and "change" are here as well as on Queries, because "where do I edit Power Query" was
                    // answered with Queries: "query" alone scored for Queries inside the phrase "power query", and a
                    // noun plus a verb beat a phrase with no verb (verify-askdata-r5, catalogue accuracy).
                    verbs: "load, transform, refresh, edit, change")),
            new AskRouteTool("bpa", "review", "Model quality (BPA)",
                "Check the model for common design, naming and performance issues. Review each finding before deciding how to address it.",
                new AskRouteWords(
                    // "review" is the area's own word and only this place claims it, on purpose. A bare
                    // "review the model" is the general "is this built well" question, and that is what this
                    // page answers; AI readiness answers the narrower "will an assistant answer well". A
                    // word both of them claimed would only make a tie and send nobody anywhere.
                    nouns: "quality, best practice, issue, problem, naming, bpa, rule, smell, review",
                    verbs: "tidy up, clean up")),
            new AskRouteTool("readiness", "review", "AI readiness",
                "Find missing context and model settings that can make AI answers less useful. Review the findings and improve them.",
                new AskRouteWords(
                    nouns: "readiness, ai readiness, description, synonym, copilot, ai, q and a, grounding",
                    verbs: "describe, prep for ai")),
            new AskRouteTool("tests", "review", "Validation",
                "Check that calculations return the answers you trust, and that tables connect and count correctly.",
                new AskRouteWords(
                    nouns: "check, test, validation, assertion, expected value, regression",
                    verbs: "verify, validate, prove")),
            // Reports is a TAB on Validation and not a button on the strip. Still a destination, still routable.
            new AskRouteTool("evidence", "review", "Reports",
                "The reports kept with this model, on the second tab of Validation. See what was checked, when it ran and which model it describes.",
                new AskRouteWords(
                    nouns: "report, evidence, saved report, run history, what was checked",
                    verbs: "keep a record"),
                isTab: true),
            new AskRouteTool("docs", "review", "Docs",
                "Create a readable guide to the model, with its structure, calculations and your business explanations.",
                new AskRouteWords(
                    nouns: "documentation, docs, guide, handbook, data dictionary",
                    verbs: "document, write up")),
            new AskRouteTool("deploy", "publish", "Published",
                "Review changes for a live destination, publish them, restore an earlier version or move a model between release stages.",
                new AskRouteWords(
                    nouns: "publish, deployment, workspace, release, stage, production, destination",
                    verbs: "deploy, promote, restore, ship")),
            // Edit History retired as a tab in slice 11 (Plan-heart lane R2): it is the History view inside the
            // Plan. The id stays, so a link or an older extension that names it still lands on that view.
            new AskRouteTool("history", "plan", "History",
                "See changes made by you and your assistant. Undo recent model edits or review the saved audit record.",
                new AskRouteWords(
                    nouns: "history, edit history, undo, redo, audit, timeline, change log, who changed",
                    verbs: "revert, roll back"),
                isTab: true),
            new AskRouteTool("workflows", "workflows", "Workflows",
                "Follow a repeatable set of steps for a modelling task. Use a built-in workflow or create one for your project.",
                new AskRouteWords(
                    nouns: "workflow, template, run book, repeatable steps, recipe",
                    verbs: "repeat, automate")),
        };

        /// <summary>Every place, in the order the area strips list them. The order is fixed so a recorded
        /// replay and a live call present the options identically.</summary>
        public static IReadOnlyList<AskRouteTool> Tools => AllTools;

        /// <summary>The places the area strip can show directly: every tool that is not a tab inside another
        /// page. This is what TAB_GROUPS in App.tsx must carry, and AskRouteTests compares the two.</summary>
        public static IReadOnlyList<AskRouteTool> StripTools => AllTools.Where(t => !t.IsTab).ToArray();

        public static AskRouteTool Find(string id) =>
            id == null ? null : AllTools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

        /// <summary>The area a tool belongs to, or null when the id is not one of them. A router answer
        /// naming anything else is dropped rather than passed on, which is why this returns null instead of
        /// guessing.</summary>
        public static string AreaOf(string id) => Find(id)?.Area;

        /// <summary>Phrases that LOOK like one of the places' own words and name something OUTSIDE Semanticus. A
        /// tool's word sitting inside one of these does not count towards that tool.
        ///
        /// There is one family of them today and it is the one that cost a person a wrong door: "report". Reports
        /// here is the tab of saved reports kept with the model; "report view", "report builder" and "report page"
        /// are Power BI's canvas and the people who work on it, which this product has no page for at all. So "Hide
        /// all the ID columns so report builders don't see them" was answered with Reports on the strength of one
        /// word (verify-askdata-r5, catalogue accuracy). Blanking beats deleting the bare noun "report", which would
        /// have left "where are the reports" with no answer.</summary>
        public static readonly IReadOnlyList<string> WordsThatAreNotOurs = new[]
        {
            "report view", "report builder", "report builders", "report page", "report canvas", "report designer",
        };

        /// <summary>The verbs that make a model. A request to make a model from a source is the Model Spec's when one of
        /// these (or a request word, "turn ... into", or a bare "new model") is the ask's MAIN verb and the model or dataset is
        /// its object (AskMakeIntent). An ask that does anything else to a model, or asks a question about one, is not.
        /// The ask bar's composer-model.ts carries the same lists, and test/sql-start.test.mjs fails when they differ.</summary>
        public static readonly IReadOnlyList<string> MakingWords =
            new[] { "build", "create", "make", "generate", "draft", "start", "begin", "set up", "spin up", "put together", "scaffold", "produce", "design" };

        /// <summary>The verbs that mean "I want to make one of these", as opposed to "where is one of these". Kept in one
        /// list because the difference between "create a measure" and "what uses this measure" is carried entirely by
        /// these five words.</summary>
        public static readonly IReadOnlyList<string> CreateVerbs =
            new[] { "create", "add", "new", "make", "set up" };

        /// <summary>One thing a person can ask to create, and the page it belongs to.
        ///
        /// THE ENGINE RECORDS WHAT IS BEING MADE AND NOTHING ELSE. Where the thing is actually made is a
        /// webview and extension fact, and this engine ships without either: four of these have their own
        /// panel inside Advanced modelling, five are made by a command the Model tree runs (a measure, a
        /// calculated column, a calculated table, a calculation item and a function), and two have no form
        /// and no command at all (a relationship is drawn on the Diagram canvas and a table is a row in a
        /// Model Spec draft). askbox.tsx holds that map and test/ask-box.test.mjs reads this list, so a
        /// thing added here with nowhere to make it fails there rather than going quiet.</summary>
        public sealed class AskCreateThing
        {
            public AskCreateThing(string name, string tool, string nouns)
            {
                Name = name;
                Tool = tool;
                Nouns = nouns.Split(',').Select(w => w.Trim()).Where(w => w.Length > 0).ToArray();
            }

            /// <summary>The thing, in the words a person would say it: "measure", "calendar", "role".</summary>
            public string Name { get; }

            /// <summary>The tool whose page holds it. Only a winner that IS this tool may carry this thing, so
            /// a create word cannot smuggle a second destination into an answer.</summary>
            public string Tool { get; }

            public IReadOnlyList<string> Nouns { get; }
        }

        private static readonly AskCreateThing[] AllCreateThings =
        {
            new AskCreateThing("measure", "daxlab", "measure, kpi"),
            new AskCreateThing("calculated column", "daxlab", "calculated column, calc column"),
            new AskCreateThing("calculated table", "daxlab", "calculated table, calc table"),
            new AskCreateThing("function", "daxlab", "function, udf"),
            new AskCreateThing("calculation item", "advmodels", "calculation item, calc item"),
            new AskCreateThing("table", "spec", "table"),
            new AskCreateThing("relationship", "diagram", "relationship, join, link"),
            new AskCreateThing("calculation group", "advmodels", "calculation group, calc group"),
            new AskCreateThing("role", "advmodels", "role, rls, row level security"),
            new AskCreateThing("perspective", "advmodels", "perspective"),
            new AskCreateThing("calendar", "advmodels", "calendar, date table"),
        };

        public static IReadOnlyList<AskCreateThing> CreateThings => AllCreateThings;
    }
}
