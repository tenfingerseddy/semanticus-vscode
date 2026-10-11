// Is this ask a request to MAKE a new model from a source? (m11 SQL start, round 7.)
//
// The Model Spec is where a new model is drafted, so an ask that says "make me a model from the warehouse" belongs
// there and an ask about a model that already exists never does. Rounds 1 to 6 told the two apart with one long
// pattern ("a making verb, filler words, model, from, up to eight words, the literal word sql"). It caught five of
// Sol's thirty everyday asks (verifier round 6), because people do not say the phrase: "make a new model from the data
// warehouse", "new dataset from the SQL source please", "connect to SQL and create a model". So this reads the ask by
// its INTENT, in the order a person builds a sentence:
//
//   1. Lead-ins are stepped over ("please", "can you", "how do I", "I want to").
//   2. The MAIN VERB is a making verb (build, create, make, generate, draft, start, begin, set up ...), a request word
//      with an indefinite object ("I need a new model"), "turn / import ... into a new model", the verb "model" on a
//      source ("model this database"), or a bare "new model ...". It may follow a connecting clause that names the
//      source ("connect to SQL and create a model", "use my SQL connection to generate a model").
//   3. Its OBJECT is a model or dataset: the first noun phrase, up to a preposition, must end in one. "make a
//      report off this database model" makes a report; "create a description for the SQL model I have open" makes a
//      description; neither is asked to make a model. "model" followed by another noun only names what is made
//      ("generate model documentation"), and a convert verb whose source is itself a model ("turn the sql model into a
//      direct lake model") changes a model that exists (m11 join part 2, item 7).
//   4. A SOURCE is named (sql, warehouse, database, endpoint, lakehouse, source ...), and nothing else is asked
//      of the model: after the last source word only a polite end may follow, and no other verb ("and publish it"),
//      relative clause ("that refreshes daily") or complement ("refresh faster") sits after the object.
//
// Three answers. YES routes to the Model Spec. NO never does. UNSURE is the one case the words cannot settle: the ask
// names a model and a source and something that sounds like making one, but has no main verb the rules know ("get my
// warehouse into a semantic model"). Only then is the decision service asked (once, typed: a new model, this model, or
// neither; AskRoutePoint.IntentQuestions), and with no service the answer is NO, which is today's routing.
//
// The webview carries the same rule in composer-model.ts (classifyMakeIntent), written for the Ctrl J bar, which since
// Kane's ruling of 27 Sep routes nothing; the page's route is the Plan's field, which asks this engine. test/sql-start.test.mjs
// holds the lists equal, and two lists of asks (test/model-from-sql-asks.json and test/sql-start-r7-asks.json) are run through both.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Semanticus.Engine
{
    /// <summary>What the words say about making a model.</summary>
    public enum MakeIntent
    {
        /// <summary>Not a request to make a model, or an ask about one that exists.</summary>
        No,
        /// <summary>A request to make a new model from a source.</summary>
        Yes,
        /// <summary>The words cannot say; the decision service may be asked.</summary>
        Unsure,
    }

    public static class AskMakeIntent
    {
        /// <summary>Request words, which make an ask a request to make one only with an indefinite or new object ("I need a new model").</summary>
        public static readonly IReadOnlyList<string> RequestWords =
            new[] { "i need", "we need", "i want", "we want", "i d like", "we d like", "i would like", "we would like", "i d love", "we d love", "i would love",
                    "i m after", "i am after", "looking for", "i m looking for", "i am looking for", "give me", "get me", "i require" };

        /// <summary>Verbs that turn a source into a model: "turn these tables into a new model", "import my database as a new model", "convert the tables to a new model".</summary>
        public static readonly IReadOnlyList<string> ConvertWords =
            new[] { "turn", "convert", "transform", "import", "load", "pull", "bring" };

        /// <summary>Verbs that may open a clause naming the source before the making verb: "connect to SQL and create a model".</summary>
        public static readonly IReadOnlyList<string> PreVerbs =
            new[] { "connect", "link", "point", "hook up", "use", "using", "import", "load", "pull", "take", "grab", "read" };

        /// <summary>Words that open an ask without changing what it asks for.</summary>
        public static readonly IReadOnlyList<string> LeadIns =
            new[] { "i want you to", "i need you to", "i d like you to", "i want to", "i need to", "i would like to", "i d like to", "we want to", "we need to",
                    "we d like to", "i wanna", "i m trying to", "i am trying to", "i m looking to", "i am looking to", "i have to", "open the page to",
                    "open the wizard to", "show me how to", "walk me through how to", "walk me through", "help me to", "help me", "let s", "let us", "lets",
                    "can you", "could you", "would you", "will you", "can i", "could i", "should i", "shall i", "should we", "how do i", "how can i",
                    "how would i", "how should i", "how do we", "how can we", "how to", "is it possible to", "is it possible for me to", "is there a way to",
                    "what is the way to", "what is the best way to", "what s the way to", "what s the best way to", "tell me how to", "please", "pls", "hey", "ok", "okay", "so", "now", "just", "kindly", "go ahead and", "want to", "need to" };

        /// <summary>Words that end a noun phrase: what follows is a source, a tool or a complement, not the thing made.</summary>
        public static readonly IReadOnlyList<string> PrepositionWords =
            new[] { "from", "out", "using", "with", "off", "on", "in", "at", "over", "via", "by", "through", "based", "into", "for", "against", "atop", "under",
                    "connected", "pointing", "backed", "sitting", "of", "as" };

        /// <summary>The only words that may follow a model or dataset for it to be the thing made: a preposition, a joiner or "and" (checked
        /// separately), a source word ("new model, SQL backend"), a polite word, a name ("a model called Sales") or how it is made ("a new
        /// model built from the SQL endpoint"). Any other word after it is what is made, and "model" only
        /// names it: "model documentation", "model measures", "a model diagram" (m11 join part 2, item 7: the verifier's round 7
        /// found 11 of 40 asks about a model that exists opening the Model Spec this way).</summary>
        public static readonly IReadOnlyList<string> ObjectFollowers =
            new[] { "please", "pls", "thanks", "thank", "now", "for", "called", "named", "built", "made", "created", "generated", "sourced", "fed", "drawn", "pulled", "loaded", "powered" };

        /// <summary>Words that start another clause: after the object they mean something else is asked of it.</summary>
        public static readonly IReadOnlyList<string> JoinerWords =
            new[] { "that", "which", "so", "but", "while", "when", "if", "because", "then" };

        /// <summary>What is made: a model or a dataset ("modeling" stands for the model in "start modeling from these tables").</summary>
        public static readonly IReadOnlyList<string> HeadWords =
            new[] { "model", "dataset", "modeling", "modelling" };

        /// <summary>A source: what a model is made from. "table" and "schema" are not here on their own, because a date table or a star
        /// schema is part of a model, not where one comes from.</summary>
        public static readonly IReadOnlyList<string> SourceWords =
            new[] { "sql", "warehouse", "database", "db", "endpoint", "lakehouse", "source", "server", "fabric", "synapse", "datamart", "rdbms",
                    "postgres", "postgresql", "mysql", "oracle", "snowflake", "bigquery", "redshift", "databricks" };

        /// <summary>The only words that may follow the last source word: what the source is called ("sql database", "source tables",
        /// "warehouse metadata", "sql schema") and a polite end. Anything else is another thing asked of the model.</summary>
        public static readonly IReadOnlyList<string> TailWords =
            new[] { "database", "databases", "db", "warehouse", "warehouses", "endpoint", "endpoints", "analytics", "server", "servers", "source", "sources",
                    "table", "tables", "view", "views", "lakehouse", "instance", "for me", "please", "schema", "schemas", "metadata", "keys", "key",
                    "connection", "connections", "backend", "thanks", "thank you", "now", "from scratch", "to start", "again" };

        /// <summary>A verb that acts on a model that exists. As the main verb, the ask is never a request to make one.</summary>
        public static readonly IReadOnlyList<string> ActionWords =
            new[] { "refresh", "reload", "restart", "publish", "deploy", "update", "check", "add", "remove", "delete", "rename", "fix", "document", "compare",
                    "validate", "test", "run", "schedule", "sync", "secure", "protect", "apply", "enable", "disable", "mark", "hide", "show", "list", "open",
                    "edit", "change", "move", "copy", "share", "explain", "describe", "review", "optimize", "optimise", "tune", "analyse", "analyze", "audit",
                    "profile", "monitor", "undo", "redo", "revert", "save", "export", "reconcile", "rebuild", "repair", "patch", "diagnose", "debug", "inspect",
                    "view", "browse", "query", "find", "search", "count", "sort", "filter", "group", "tag", "label", "translate", "format", "clean", "merge",
                    "split", "lock", "unlock", "grant", "revoke", "back", "restore" };

        /// <summary>Words that say the model is the one already open.</summary>
        public static readonly IReadOnlyList<string> ExistingWords =
            new[] { "this", "that", "current", "currently", "open", "opened", "existing", "already", "active", "loaded", "same", "present" };

        /// <summary>A question word that opens an ask: it asks about something, it does not ask for it.</summary>
        public static readonly IReadOnlyList<string> QuestionStarts =
            new[] { "what", "where", "which", "who", "whom", "whose", "why", "when", "is", "are", "does", "do", "did", "should", "will", "has", "have" };

        private static readonly string[] Indefinite = { "a", "an", "new", "fresh", "another", "brand", "blank" };
        private static readonly string[] MakeIdioms = { "sure", "certain", "sense", "room", "time", "it", "that", "do", "up", "way", "use", "progress", "peace" };
        private static readonly string[] StartIdioms = { "up", "over", "again" };
        private static readonly string[] Polite = { "for me", "thank you", "from scratch", "to start" };
        private static readonly string[] NewWords = { "new", "fresh", "blank", "brand", "another" };
        private static readonly string[] Pronouns = { "it", "them", "this", "that" };
        private static readonly string[] LookedBack = { "i have", "we have", "we built", "we made", "i built", "i made", "we created", "already" };

        private enum Parsed { No, Yes, NoVerb }

        /// <summary>Whether the ask is a request to make a model from a source. Lower-cased, punctuation as spaces, as the engine's other
        /// word matching does it (letters are Unicode on purpose: an accented name is a word).</summary>
        public static MakeIntent Classify(string text)
        {
            var tok = Tokens(text);
            if (tok.Length == 0) return MakeIntent.No;
            var i = SkipLeadIns(tok);
            if (i >= tok.Length) return MakeIntent.No;
            if (i == 0 && QuestionStarts.Contains(tok[0])) return MakeIntent.No;
            if (ActionWords.Contains(tok[i])) return MakeIntent.No;
            switch (Parse(tok, i))
            {
                case Parsed.Yes: return MakeIntent.Yes;
                case Parsed.No: return MakeIntent.No;
                default: return SoundsLikeMaking(tok, i) ? MakeIntent.Unsure : MakeIntent.No;
            }
        }

        private static string[] Tokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            var chars = text.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
            return new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>How many tokens of the longest phrase in the list start at index at, or 0.</summary>
        private static int PhraseAt(string[] tok, int at, IReadOnlyList<string> phrases)
        {
            var best = 0;
            foreach (var p in phrases)
            {
                var w = p.Split(' ');
                if (w.Length <= best || at + w.Length > tok.Length) continue;
                var all = true;
                for (var k = 0; k < w.Length && all; k++) all = string.Equals(tok[at + k], w[k], StringComparison.Ordinal);
                if (all) best = w.Length;
            }
            return best;
        }

        private static int SkipLeadIns(string[] tok)
        {
            var i = 0;
            while (i < tok.Length)
            {
                var n = PhraseAt(tok, i, LeadIns);
                if (n == 0) break;
                i += n;
            }
            return i;
        }

        private static bool IsSource(string w) => SourceWords.Contains(w);

        private static Parsed Parse(string[] tok, int i)
        {
            var n = tok.Length;

            // 1. A making verb, first one in the ask. Before it only a clause that names the source may stand.
            for (var m = i; m < Math.Min(n, i + 14); m++)
            {
                var k = PhraseAt(tok, m, AskRouteCatalog.MakingWords);
                if (k == 0) continue;
                var pre = tok.Skip(i).Take(m - i).ToArray();
                if (m > i && !(PhraseAt(pre, 0, PreVerbs) > 0 && (pre[^1] == "and" || pre[^1] == "then" || pre[^1] == "to") && pre.Any(IsSource))) break;
                var j = m + k;
                if (tok[m] == "make" && j < n && MakeIdioms.Contains(tok[j])) return Parsed.No;
                if (tok[m] == "start" && j < n && StartIdioms.Contains(tok[j])) return Parsed.No;
                if (j < n && (tok[j] == "me" || tok[j] == "us")) j++;
                if ((tok[m] == "start" || tok[m] == "begin") && j < n && tok[j] == "with") j++;
                var sourceBefore = pre.Any(IsSource);
                if ((tok[m] == "start" || tok[m] == "begin") && j < n && tok[j] == "from")
                    return RestOk(tok, j, sourceBefore) ? Parsed.Yes : Parsed.No;
                var end = ObjectEnd(tok, j);
                if (end < 0) return Parsed.No;
                var np = tok.Skip(j).Take(end - j).ToArray();
                if (np.Any(w => ExistingWords.Contains(w))) return Parsed.No;
                // "build the model from sql again": made before, so it exists. "build a new model from sql again" is still a new one.
                if (tok.Skip(end).Contains("again") && !np.Any(w => Indefinite.Contains(w))) return Parsed.No;
                return RestOk(tok, end, sourceBefore || np.Any(IsSource)) ? Parsed.Yes : Parsed.No;
            }

            // 2. The verb "model" on a source: "model this database from scratch".
            if (tok[i] == "model")
            {
                var j = i + 1;
                var k = j;
                while (k < n && !PrepositionWords.Contains(tok[k]) && !JoinerWords.Contains(tok[k]) && tok[k] != "and") k++;
                if (!tok.Skip(j).Take(k - j).Any(IsSource)) return Parsed.No;
                return RestOk(tok, k, true) ? Parsed.Yes : Parsed.No;
            }

            // 3. "I need a new model from ...": a request word, and an indefinite or new object.
            var r = PhraseAt(tok, i, RequestWords);
            if (r > 0)
            {
                var j = i + r;
                if (j >= n || !Indefinite.Contains(tok[j])) return Parsed.No;
                var end = ObjectEnd(tok, j);
                if (end < 0) return Parsed.No;
                var np = tok.Skip(j).Take(end - j).ToArray();
                if (np.Any(w => ExistingWords.Contains(w))) return Parsed.No;
                return RestOk(tok, end, np.Any(IsSource)) ? Parsed.Yes : Parsed.No;
            }

            // 4. A bare "new model please, from SQL Server".
            if (NewWords.Contains(tok[i]))
            {
                var end = ObjectEnd(tok, i);
                if (end < 0) return Parsed.No;
                return RestOk(tok, end, tok.Skip(i).Take(end - i).Any(IsSource)) ? Parsed.Yes : Parsed.No;
            }

            // 5. "turn these tables into a new model", "import my database as a new model", "convert the tables to a new model".
            if (ConvertWords.Contains(tok[i]))
            {
                var k = i + 1;
                while (k < n && tok[k] != "into" && tok[k] != "as" && tok[k] != "to")
                {
                    if (JoinerWords.Contains(tok[k])) return Parsed.No;
                    k++;
                }
                if (k >= n || !tok.Skip(i + 1).Take(k - i - 1).Any(IsSource)) return Parsed.No;
                // What is turned is a source. A model turned into another kind of model ("turn the sql model into a direct lake
                // model", "convert the warehouse model to a composite model") is a change to a model that exists.
                if (tok.Skip(i + 1).Take(k - i - 1).Any(w => HeadWords.Contains(w))) return Parsed.No;
                var j = k + 1;
                if (j >= n || !Indefinite.Contains(tok[j])) return Parsed.No;
                var end = ObjectEnd(tok, j);
                if (end < 0) return Parsed.No;
                return RestOk(tok, end, true) ? Parsed.Yes : Parsed.No;
            }

            return Parsed.NoVerb;
        }

        /// <summary>Where the object's noun phrase ends: just after the model or dataset, or -1 when a preposition, another clause or the end of
        /// the ask comes first (the object is then something else: a report, a description, a query). "tables and relationships" is the
        /// other thing a draft makes.</summary>
        private static int ObjectEnd(string[] tok, int j)
        {
            for (var k = j; k < tok.Length; k++)
            {
                var w = tok[k];
                if (HeadWords.Contains(w) && ObjectIsHead(tok, k + 1)) return k + 1;
                if (w == "and" && k > j && tok[k - 1] == "tables" && k + 1 < tok.Length && tok[k + 1] == "relationships") return k + 2;
                if (PrepositionWords.Contains(w) || JoinerWords.Contains(w) || w == "and") return -1;
                if (k - j > 7) return -1;
            }
            return -1;
        }

        /// <summary>Whether a model or dataset ending at the word before `next` is the thing made: nothing follows it, or a word that ends
        /// the noun phrase does. Otherwise it only names what is made ("generate model documentation", "create a model perspective").</summary>
        private static bool ObjectIsHead(string[] tok, int next)
        {
            if (next >= tok.Length) return true;
            var w = tok[next];
            return PrepositionWords.Contains(w) || JoinerWords.Contains(w) || w == "and" || ObjectFollowers.Contains(w) || IsSource(w);
        }

        /// <summary>After the object: a source is named somewhere, no other verb or clause follows it, and only the source's own name and a
        /// polite end come after the last source word.</summary>
        private static bool RestOk(string[] tok, int end, bool sourceBefore)
        {
            var rest = tok.Skip(end).ToArray();
            if (!sourceBefore && !rest.Any(IsSource)) return false;
            foreach (var w in rest)
                if (JoinerWords.Contains(w) || ActionWords.Contains(w)) return false;
            for (var a = 0; a + 1 < rest.Length; a++)
                if (rest[a] == "and" && (ActionWords.Contains(rest[a + 1]) || Pronouns.Contains(rest[a + 1])
                    || AskRouteCatalog.MakingWords.Any(v => v.Split(' ')[0] == rest[a + 1]))) return false;
            var last = -1;
            for (var a = 0; a < rest.Length; a++) if (IsSource(rest[a])) last = a;
            var tail = rest.Skip(last + 1).ToArray();
            var at = 0;
            while (at < tail.Length)
            {
                var polite = Polite.FirstOrDefault(p => { var w = p.Split(' '); return at + w.Length <= tail.Length && w.Select((x, q) => tail[at + q] == x).All(b => b); });
                if (polite != null) { at += polite.Split(' ').Length; continue; }
                if (TailWords.Contains(tail[at]) || tail[at].All(char.IsDigit)) { at++; continue; }
                return false;
            }
            return true;
        }

        /// <summary>No main verb the rules know, yet the ask names a model, a source and something that sounds like making one, and nothing
        /// says the model is the one that is open. This is the only case the decision service is asked about.</summary>
        private static bool SoundsLikeMaking(string[] tok, int i)
        {
            if (!tok.Any(w => HeadWords.Contains(w)) || !tok.Any(IsSource)) return false;
            for (var k = 0; k < tok.Length; k++)
            {
                if (!HeadWords.Contains(tok[k])) continue;
                for (var x = Math.Max(0, k - 3); x < k; x++) if (ExistingWords.Contains(tok[x])) return false;
                var after = string.Join(" ", tok.Skip(k + 1).Take(3));
                if (LookedBack.Any(p => after.Contains(p, StringComparison.Ordinal))) return false;
            }
            var makeish = AskRouteCatalog.MakingWords.Select(v => v.Split(' ')[0])
                .Concat(new[] { "new", "fresh", "into", "turn", "convert", "transform", "import" });
            return tok.Any(w => makeish.Contains(w));
        }
    }
}
