// get_model_facts / getModelFacts: the model facts snapshot (M4 groundwork contract, section 1).
//
// Free on both doors: the facts are the product's own measurements, and only a Jev call is Pro. One snapshot
// per (session, model revision, report basis), plus the data source it was measured against, because
// connecting to data changes the answer without moving the revision. The first ask computes it; after that a
// model change recomputes it in the background so the next ask finds it ready. A session nobody has asked for
// facts about is never measured in the background: measuring reads the model's data, and that is not a cost
// to pay for a snapshot nobody reads.
//
// What the snapshot may say about data is decided by the connection relationship, the same gate the Storage
// tab used before this moved into the engine:
//   sameInstance                          measured facts, storage bytes, cardinality, every storage finding
//   a linked working copy                 measured facts from the copy, no bytes (either side may be stale)
//   anything else, or no connection       data "none": no measured blocks at all
//
// Privacy: values are read inside the engine and dropped there (ValueFacts). The snapshot carries names, types,
// settings and derived facts only, never a row, a raw value, a minimum, a maximum or a sample.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Semanticus.Engine.Facts;
using Semanticus.Engine.Lineage;
using Semanticus.Engine.StartPlan;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    // Written Facts.ModelFacts throughout: Semanticus.Engine already has a ModelFacts (the workflow predicate's).
    public sealed partial class LocalEngine
    {
        private readonly object _factsGate = new object();
        private readonly Dictionary<string, Task<Facts.ModelFacts>> _factsByKey = new Dictionary<string, Task<Facts.ModelFacts>>(StringComparer.Ordinal);
        private readonly LinkedList<string> _factsOrder = new LinkedList<string>();
        private const int FactsCacheSize = 4;
        private string _factsSessionId;           // the session somebody has asked for facts about
        private int _factsSubscribed;
        private CancellationTokenSource _factsBackground;
        private bool _factsStopped;

        /// <summary>Test seams: a smaller measuring budget, the background delay, and how many snapshots were built.</summary>
        internal FactsBudget FactsBudgetForTest { get; set; }
        internal int FactsBackgroundDelayMsForTest { get; set; } = 750;
        internal int FactsComputationsForTest => Volatile.Read(ref _factsComputations);
        private int _factsComputations;

        /// <summary>The snapshot already built for the open model at its CURRENT revision, or null. Never builds and
        /// never waits: the plan executor calls this from inside a model write on the single-writer dispatcher, where
        /// awaiting a build that itself reads the model would deadlock. A snapshot about an older revision is not this
        /// model's any more and is not returned. When more than one snapshot exists for this revision (the data
        /// source or the report basis moved without the revision moving) the one asked for most recently wins.</summary>
        internal Facts.ModelFacts TryGetCurrentFacts() => FindCurrentFacts((_, __) => true);

        /// <summary>The snapshot the Jev families may put in a request: <see cref="TryGetCurrentFacts"/>, except that a snapshot carrying facts worked out from a
        /// sample of rows is passed over unless the person's stored permission covers that sample (<see cref="SampleDisclosedAsync"/>, taken when the snapshot
        /// was asked for by <see cref="EnsureFactsForRevisionAsync"/>). The snapshot built without the sample answers instead, and with none the families see no
        /// facts at all. Fails shut: nothing worked out from rows reaches Jev on a guess.</summary>
        internal Facts.ModelFacts TryGetJevAskFacts()
        {
            var session = _sessions.Current;
            if (session == null) return null;
            return FindCurrentFacts((key, facts) =>
                key.EndsWith(WithheldFromJevMark, StringComparison.Ordinal) || facts.Data != ModelFactsData.SourceSample
                || (_jevSampleSession == session.Id && _jevSampleCovered && string.Equals(JevScopeMarkOf(key), _jevSampleMark, StringComparison.Ordinal)));
        }

        private Facts.ModelFacts FindCurrentFacts(Func<string, Facts.ModelFacts, bool> accept)
        {
            var session = _sessions.Current;
            if (session == null) return null;
            var prefix = session.Id + "\n" + session.Revision.ToString(CultureInfo.InvariantCulture) + "\n";
            lock (_factsGate)
            {
                for (var node = _factsOrder.Last; node != null; node = node.Previous)
                {
                    if (!node.Value.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    if (!_factsByKey.TryGetValue(node.Value, out var task) || !task.IsCompletedSuccessfully) continue;
                    var facts = task.Result;
                    if (facts != null && facts.Revision == session.Revision && accept(node.Value, facts)) return facts;
                }
            }
            return null;
        }

        /// <summary>Build (or find) the snapshot for this session's revision, for a caller inside the engine that
        /// needs it ready: the Jev family run, which asks for facts FIRST so the executor's data checks have
        /// something to read when a person applies a line. Waiting stops at <paramref name="ct"/>; the build itself
        /// is shared and cached, so it is not cancelled with the caller. Never throws: a snapshot that could not be
        /// built leaves every data check reading "not measured", which refuses rather than guesses.</summary>
        internal async Task EnsureFactsForRevisionAsync(SessionContext context, Session session, CancellationToken ct)
        {
            try
            {
                if (context == null || session == null || !ReferenceEquals(context.Session, session)) return;
                await FactsFor(context, session, forJev: true).WaitAsync(ct).ConfigureAwait(false);
            }
            catch { /* not measured is the honest answer when the snapshot cannot be had */ }
        }

        // ---- The source sample (L5 of the spec-first start) --------------------------------------------------------
        // A new model starts from a SQL source and has no live connection. A bounded sample of the source is read once
        // (ReadSourceSampleAsync), kept beside the built model (SaveSourceFacts) and served as the model's facts when
        // there is no connection (data "sourceSample"). Derived facts only, keyed by SOURCE table and column.

        /// <summary>Test seams: the executor that answers the sample's two queries (server, database, sql, max rows), so a test
        /// counts every query; and the sign-in, so the token path can fail on purpose.</summary>
        internal Func<string, string, string, int, Task<ResultSet>> SourceSampleExecutorForTest { get; set; }
        internal Func<string> SourceSampleTokenForTest { get; set; }

        /// <summary>Read a bounded sample of the named source tables, as the signed-in person, and return the derived facts
        /// (never a row). Fabric SQL endpoints only. An agent goes through the standard QueryData gate on the SQL target,
        /// exactly like run_sql and the reconciliation run: a Deny runs 0 queries, an Ask queues the person's approval and
        /// runs 0 queries, and the person is never gated. The caller still gets a result either way, with every table
        /// listed as "not measured" and the reason, so the plan that follows is made from the schema alone and says so.
        /// Budget: FactsBudget (20,000 rows and 1,000,000 cells per table).</summary>
        internal async Task<Facts.SourceSampleResult> ReadSourceSampleAsync(Facts.SourceSampleTarget target, IReadOnlyList<Facts.SourceSampleTable> tables,
            string origin, CancellationToken ct)
        {
            var budget = FactsBudgetForTest ?? new FactsBudget();
            if (target == null) return Facts.SourceSample.Refuse(null, tables, "No source was given to read a sample from.", budget);
            if (!Facts.SourceSample.IsFabricEndpoint(target.Server))
                return Facts.SourceSample.Refuse(target, tables,
                    "A sample can only be read from a Fabric SQL endpoint in this release, and this server is not one. The plan is made from the table and column names alone.", budget);

            var gate = GuardAgent(AgentCapability.QueryData, target.Server, target.Database, origin, isCommit: true,
                summary: $"read a sample of the tables in {target.Database} on {target.Server}: counts and patterns worked out inside the engine, never rows",
                approvalId: out _, intentBasis: "querydata", consumeGrant: false);
            if (gate != null) return Facts.SourceSample.Refuse(target, tables, gate, budget);

            Func<string, int, Task<ResultSet>> execute;
            var seam = SourceSampleExecutorForTest;
            if (seam != null) execute = (sql, max) => seam(target.Server, target.Database, sql, max);
            else
            {
                string token;
                try { token = SourceSampleTokenForTest != null ? SourceSampleTokenForTest() : await AcquireSqlTokenAsync(target.AuthMode, target.TenantId, origin, ct).ConfigureAwait(false); }
                catch (Exception ex) { return Facts.SourceSample.Refuse(target, tables, "Could not sign in to read a sample: " + ScrubSchemaError(ex.Message), budget); }
                execute = (sql, max) => FabricSqlQuery.ExecuteAsync(target.Server, target.Database, token, sql, max, budget.TimeoutSeconds, ct);
            }
            return await Facts.SourceSample.ReadAsync(execute, target, tables, budget, ct).ConfigureAwait(false);
        }

        /// <summary>Where a model's source facts live: the model's own .semanticus folder, never the workspace's (one
        /// workspace folder is shared by many models, and a sample belongs to exactly one).</summary>
        private static string SourceFactsDirFor(Session s) =>
            s == null || string.IsNullOrEmpty(s.SourcePath) || ExperienceStore.IsEphemeralAnchor(s.SourcePath) ? null : LayoutStore.DirFor(s.SourcePath);

        /// <summary>Keep the sample beside the built model so it can be served with no live connection. Null when the read was
        /// refused (there is nothing to keep) or the model has no place on disk yet.</summary>
        internal string SaveSourceFacts(Session session, Facts.SourceSampleResult sample, string planId = null)
        {
            if (sample == null || sample.Refused != null) return null;
            if (!string.IsNullOrEmpty(planId)) sample.PlanId = planId;   // how the person's permission for this sample is found again (SampleDisclosedAsync)
            var dir = SourceFactsDirFor(session) ?? throw new InvalidOperationException("Save the model first: source facts are kept beside it.");
            var path = Facts.SourceFactsStore.Save(dir, sample);
            lock (_factsGate) _sourceFactsCache = default;
            return path;
        }

        private (string dir, string signature, Facts.SourceSampleResult result) _sourceFactsCache;

        /// <summary>The saved source facts for this session's model, or null. A single-entry cache keyed by the file's own
        /// signature, so a replaced file is read again and an unchanged one is not parsed on every ask.</summary>
        internal Facts.SourceSampleResult LoadSourceFacts(Session session)
        {
            var dir = SourceFactsDirFor(session);
            if (dir == null) return null;
            var signature = Facts.SourceFactsStore.Signature(dir);
            if (signature == null) return null;
            lock (_factsGate)
                if (_sourceFactsCache.result != null && _sourceFactsCache.dir == dir && _sourceFactsCache.signature == signature) return _sourceFactsCache.result;
            var loaded = Facts.SourceFactsStore.Load(dir);
            if (loaded != null) lock (_factsGate) _sourceFactsCache = (dir, signature, loaded);
            return loaded;
        }

        /// <summary>THE ONE GATE for facts worked out from a sample of rows going to Jev. True only while the model's permission is current AND a stored
        /// start-plan grant, kept beside it, was given for <paramref name="planId"/> and covered the sample of rows (<see cref="DisclosedScope.SampleFacts"/>).
        /// Read fresh from the store each time, never from the plan's own copy. No plan id, no permission, a grant for another plan, a grant that did not
        /// name the sample, or anything unreadable is false: without it Jev sees the model's names and settings only. Every path that builds the facts of
        /// a Jev request asks this before it lets the sample in: the start plan's decide rounds (<see cref="EngineStartScratchHost"/>) and the Plan's own
        /// runs on the built model (<see cref="TryGetJevAskFacts"/>), whether the model was just built, started on Free and upgraded, or opened after
        /// a restart. The person's own view of the facts (get_model_facts) is not Jev and is not gated here.</summary>
        internal async Task<bool> SampleDisclosedAsync(string planId) => await JevSampleGrantAsync(planId).ConfigureAwait(false) != null;

        /// <summary>The stored grant for <paramref name="planId"/> when it is current AND covers the sample of rows, or null. What the grant covers is its source and
        /// its tables (<see cref="DisclosedScope.IncludedTables"/>): the sample is cut to them by <see cref="Facts.SourceSampleResult.RestrictedTo"/> before any fact
        /// from it goes to Jev, so a table the person left out of the grant, or put back later, sends nothing (Sol, join 6, A2).</summary>
        private async Task<DisclosedScope> JevSampleGrantAsync(string planId)
        {
            if (string.IsNullOrEmpty(planId)) return null;
            try
            {
                var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(scope)) return null;
                var consent = Decisions.ReadConsent(scope);
                if (!consent.Granted) return null;
                var granted = StartDisclosureStore.Read(scope, planId, consent.Generation);
                if (granted?.SampleFacts != true) return null;
                // A table the person refused the sample of is never in what is cut, whatever the stored grant still says (it is also narrowed on disk when the refusal is made).
                string[] refused;
                lock (_startPlanLock) refused = _startPlan != null && string.Equals(_startPlan.Id, planId, StringComparison.Ordinal) ? (_startPlan.SampleRefused ?? new List<string>()).ToArray() : Array.Empty<string>();
                return WithoutRefusedSamples(granted, refused);
            }
            catch { return null; }
        }

        private static DisclosedScope WithoutRefusedSamples(DisclosedScope granted, IEnumerable<string> refused)
        {
            var out_ = new HashSet<string>((refused ?? Array.Empty<string>()).Select(DisclosedScope.Norm), StringComparer.Ordinal);
            if (out_.Count == 0 || !granted.SampleTables.Any(t => out_.Contains(t))) return granted;
            return new DisclosedScope(granted.SourceKey, granted.IncludedTables, true, granted.SampleTables.Where(t => !out_.Contains(t)));
        }

        /// <summary>The plan's refusals of a table's sample, written into the stored grant: the narrowed grant is kept on disk, so a restart and every later reader see it.</summary>
        internal async Task NarrowStartGrantToPlanAsync()
        {
            try
            {
                string planId; string[] refused;
                lock (_startPlanLock) { planId = _startPlan?.Id; refused = (_startPlan?.SampleRefused ?? new List<string>()).ToArray(); }
                if (string.IsNullOrEmpty(planId)) return;
                var scope = await DecisionConsentScopeAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(scope)) return;
                var consent = Decisions.ReadConsent(scope);
                if (!consent.Granted) return;
                var granted = StartDisclosureStore.Read(scope, planId, consent.Generation);
                if (granted == null || !granted.SampleFacts) return;
                var narrowed = WithoutRefusedSamples(granted, refused);
                if (ReferenceEquals(narrowed, granted)) return;
                StartDisclosureStore.Grant(scope, planId, narrowed, consent.Generation, DateTime.UtcNow);
            }
            catch (Exception ex) { try { Console.Error.WriteLine("[start-plan] narrowing the grant after a refused sample failed: " + ex.Message); } catch { } }
        }

        /// <summary>The sample as Jev may see it for this plan: cut to the granted source and tables, or null when no grant covers the sample at all. The ONE call
        /// every path that lays a sample over a model Jev reads goes through.</summary>
        internal async Task<Facts.SourceSampleResult> SampleForJevAsync(string planId, Facts.SourceSampleResult sample)
        {
            if (sample == null) return null;
            var grant = await JevSampleGrantAsync(planId).ConfigureAwait(false);
            return grant == null ? null : sample.RestrictedTo(grant);
        }

        /// <summary>What the Jev families may see of the open model's saved sample: nothing (no grant covers it), all of it, or the granted tables of it.
        /// Kept for the synchronous reader (<see cref="TryGetJevAskFacts"/>), which cannot ask the store from inside a model write. True and whole when the model
        /// has no saved sample at all (there is nothing to withhold).</summary>
        private async Task<(bool Covered, DisclosedScope Only)> JevMaySeeSampleAsync(Session session)
        {
            var sample = LoadSourceFacts(session);
            var covered = true; DisclosedScope only = null; string mark = null;
            if (sample != null)
            {
                var grant = await JevSampleGrantAsync(sample.PlanId).ConfigureAwait(false);
                covered = grant != null;
                if (grant != null && !ReferenceEquals(sample.RestrictedTo(grant), sample)) { only = grant; mark = JevScopeMark + grant.Digest; }
            }
            lock (_factsGate) { _jevSampleSession = session.Id; _jevSampleCovered = covered; _jevSampleMark = mark; }
            return (covered, only);
        }

        private string _jevSampleSession;
        private bool _jevSampleCovered;
        private string _jevSampleMark;   // null: the whole sample; else the key mark of the snapshot cut to the granted tables
        private const string WithheldFromJevMark = "|withheld-from-jev";
        private const string JevScopeMark = "|jev-scope|";
        private static string JevScopeMarkOf(string key) { var at = key.LastIndexOf(JevScopeMark, StringComparison.Ordinal); return at < 0 ? null : key.Substring(at); }

        /// <summary>The facts for a model that is not saved yet, or has no live connection: the model's own settings and
        /// structure with the sample's derived facts laid over them by SOURCE table and column. Never cached and never
        /// published: the start plan's scratch model asks for it directly.</summary>
        internal async Task<Facts.ModelFacts> BuildSampleFactsAsync(Session session, Facts.SourceSampleResult sample)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var basis = CaptureScopeBasis(session);
            var read = await session.ReadAsync(m => ReadFactsModel(m, session, basis.Coverage)).ConfigureAwait(false);
            var facts = new Facts.ModelFacts
            {
                ModelIdentity = PaneIdentity(session, null), Revision = read.Revision, MeasuredAt = DateTime.UtcNow,
                Data = ModelFactsData.None, Tables = read.Tables.ToArray(),
            };
            AddModelProducers(facts, read, basis.Coverage);
            ApplySourceSample(facts, read, sample);
            var absent = new FactProducer { Producer = "storage scan", Coverage = "Not read. The model is not connected.", Freshness = null };
            facts.Producers["tables.bytes"] = absent;
            facts.Producers["measured.dictionaryBytes"] = facts.Producers["measured.dataBytes"] = facts.Producers["measured.cardinality"] = absent;
            facts.Producers["storageOpportunities"] = absent;
            return facts;
        }

        /// <summary>Lay the sample's facts over the model's tables. A model table is matched to its source table by the table
        /// the partition reads (so a renamed table keeps its facts), and a model column to its source column by its
        /// SourceColumn (so a renamed column keeps its facts). The table must read the sample's own server and database too
        /// (<see cref="ReadsSampledSource"/>): the same schema and name somewhere else is another table. A table the sample
        /// could not read, or did not include, keeps no facts: not measured is never a measured zero. Sets Data to
        /// sourceSample only when something was matched.</summary>
        private static void ApplySourceSample(Facts.ModelFacts facts, FactsModelRead read, Facts.SourceSampleResult sample)
        {
            int eligible = 0, measured = 0, sampled = 0, failed = 0, absent = 0;
            foreach (var tf in read.Tables)
            {
                if (!read.Sources.TryGetValue(tf, out var source)) continue;
                eligible++;
                var st = ReadsSampledSource(source.server, source.database, sample) ? sample.Find(source.schema, source.item) : null;
                if (st == null) { absent++; continue; }
                if (st.NotMeasured != null) { failed++; continue; }
                measured++;
                if (st.Sampled) sampled++;
                tf.Rows = st.Rows;
                var bySource = new Dictionary<string, ColumnFacts>(StringComparer.OrdinalIgnoreCase);
                foreach (var cf in tf.Columns)
                    if (read.SourceColumns.TryGetValue(cf, out var sc) && !bySource.ContainsKey(sc)) bySource[sc] = cf;
                foreach (var col in st.Columns)
                {
                    if (!bySource.TryGetValue(col.Source ?? "", out var cf)) continue;
                    var partners = col.OneToOneWith.Select(n => bySource.TryGetValue(n, out var other) ? other.Ref : null)
                        .Where(r => r != null).OrderBy(r => r, StringComparer.Ordinal).ToArray();
                    cf.Measured = col.ToMeasured(st.Sampled, partners);
                }
            }

            var producers = facts.Producers;
            if (sample == null || measured == 0)
            {
                var why = sample == null ? "No data. The model is not connected to a copy of it that holds data."
                    : "No data. The sample taken of the source holds nothing for the tables of this model"
                      + (string.IsNullOrWhiteSpace(sample.Refused) ? "." : ": " + sample.Refused);
                foreach (var key in MeasuredKeys) producers["measured." + key] = new FactProducer { Producer = "none", Coverage = why, Freshness = null };
                producers["tables.rows"] = new FactProducer { Producer = "none", Coverage = why, Freshness = null };
                return;
            }

            facts.Data = ModelFactsData.SourceSample;
            var coverage = measured + " of " + eligible + " tables measured from a sample of the source"
                + (sampled > 0 ? ", " + sampled + " of them on a bounded sample of " + sample.RowLimit.ToString("N0", CultureInfo.InvariantCulture) + " rows or fewer" : "")
                + (failed > 0 ? ". " + failed + " could not be read" : "")
                + (absent > 0 ? ". " + absent + " " + (absent == 1 ? "was" : "were") + " not part of the sample" : "")
                + ". Counts and patterns only, never rows.";
            var freshness = "Read from " + sample.Server + (string.IsNullOrEmpty(sample.Database) ? "" : "/" + sample.Database)
                + " at " + FormatSampleTime(sample.MeasuredAtUtc) + ", when the model was started. Not a live read: this describes the source, not a connected copy of this model, and the source may have changed since.";
            foreach (var key in MeasuredKeys)
                producers["measured." + key] = new FactProducer { Producer = "source sample", Coverage = coverage, Freshness = freshness };
            producers["measured.placeShare"] = new FactProducer { Producer = "bundled place list and postcode patterns, on a source sample", Coverage = coverage + " Shares are of distinct text values that are not blank.", Freshness = freshness };
            producers["measured.placeShare.city"] = new FactProducer { Producer = "none", Coverage = "Unknown. No city list is bundled yet, so the city share is always empty.", Freshness = null };
            producers["measured.sampled"] = new FactProducer { Producer = "source sample", Coverage = "Set when a source table had more rows than the read budget allows.", Freshness = freshness };
            producers["tables.rows"] = new FactProducer { Producer = "source sample", Coverage = coverage, Freshness = freshness };
        }

        /// <summary>True when a table that reads <paramref name="server"/> and <paramref name="database"/> reads the source the sample
        /// was taken of. Both must be the same (case aside, and a "tcp:" prefix aside). A side that names neither counts as blank, so a
        /// table whose partition says nothing about where it reads matches only a sample that says nothing either; a table that names a
        /// place never matches a sample that does not, and the other way round. Not knowing is never a match.</summary>
        internal static bool ReadsSampledSource(string server, string database, Facts.SourceSampleResult sample)
        {
            if (sample == null) return false;
            static string Server(string s)
            {
                var v = (s ?? "").Trim();
                return v.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? v.Substring(4).Trim() : v;
            }
            return string.Equals(Server(server), Server(sample.Server), StringComparison.OrdinalIgnoreCase)
                && string.Equals((database ?? "").Trim(), (sample.Database ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatSampleTime(string utc) =>
            DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) : (utc ?? "an unknown time");

        /// <summary>The producer entries that describe the model's own settings and structure, the same for every kind of data.</summary>
        private static void AddModelProducers(Facts.ModelFacts facts, FactsModelRead read, ReportScopeCoverage coverage)
        {
            var producers = facts.Producers;
            var metaFreshness = "Model settings at revision " + read.Revision.ToString(CultureInfo.InvariantCulture) + ".";
            var allColumns = read.Tables.Sum(t => t.Columns.Length);
            producers["columns.settings"] = new FactProducer { Producer = "model", Coverage = allColumns + " of " + allColumns + " columns", Freshness = metaFreshness };
            producers["columns.relationshipRole"] = new FactProducer { Producer = "model relationships", Coverage = "Every column", Freshness = metaFreshness };
            producers["columns.usedByMeasures"] = new FactProducer { Producer = "model dependencies", Coverage = "Every column. Counts measures that use the column directly or through other objects.", Freshness = metaFreshness };
            producers["columns.usedInReports"] = read.ReportUsed == null
                ? new FactProducer { Producer = "report scope", Coverage = coverage.HasChoices ? "Unknown. " + (coverage.Caveat ?? "A chosen report could not be fully read.") : "Unknown. No reports were chosen for this model.", Freshness = metaFreshness }
                : new FactProducer { Producer = "report scope", Coverage = "Every column, against " + coverage.Chosen + (coverage.Chosen == 1 ? " chosen report" : " chosen reports"), Freshness = "Reports read at revision " + read.Revision.ToString(CultureInfo.InvariantCulture) + "." };
        }

        /// <summary>The sentence an agent reads when the person has denied it data reads on this target.</summary>
        internal const string FactsDeniedMessage =
            "Your assistant is not allowed to read facts measured from this model's data. You can allow it in Your assistant > Permissions.";

        /// <summary>Both doors. The facts are counts and patterns worked out from the data, never rows, so for an
        /// agent they are a read like any metadata read: no approval is ever asked for (Kane's standing rule, no
        /// friction; undo is the safety net). The ONE exception is a person who has explicitly DENIED the agent
        /// QueryData on the connected target: that choice is honoured and the agent is refused in words about
        /// facts. "Ask" is not a denial and does not queue an approval. The person (the RPC door) is never gated,
        /// and with no data connection nothing is read from data, so there is nothing to refuse.</summary>
        public Task<Facts.ModelFacts> GetModelFactsAsync(string origin = "human")
        {
            var context = _sessions.CurrentContext;
            var session = context.RequireSession();
            var live = context.Live;
            if (live != null)
            {
                var decision = AgentPolicyGuard.Decide(AgentCapability.QueryData, ResolveTargetLabel(live.DataSource, live.Database),
                    origin, isCommit: true, AgentPolicyStore.Get());
                if (decision.Outcome == GateOutcome.Deny) throw new InvalidOperationException(FactsDeniedMessage);
            }
            else
            {
                // No connection, but facts worked out from a sample of the source: the same refusal an agent gets for a
                // connected model, on the source's own target. The person is never gated.
                var sample = LoadSourceFacts(session);
                if (sample != null && !string.IsNullOrEmpty(sample.Server)
                    && AgentPolicyGuard.Decide(AgentCapability.QueryData, ResolveTargetLabel(sample.Server, sample.Database), origin, isCommit: true, AgentPolicyStore.Get()).Outcome == GateOutcome.Deny)
                    throw new InvalidOperationException(FactsDeniedMessage);
            }
            EnsureFactsSubscription();
            lock (_factsGate) _factsSessionId = session.Id;
            return FactsFor(context, session);
        }

        /// <summary>The cache key, and the pieces of it the computation needs. The report basis is the R2 scope
        /// signature plus whether the chosen reports are all read at this revision, because "used in reports" moves
        /// from unknown to known when the last reading lands, without the signature changing.</summary>
        private (string key, ScopeBasis basis, string relationship) FactsKey(SessionContext context, Session session, bool withSample = true, DisclosedScope only = null)
        {
            var live = context.Live;
            var basis = CaptureScopeBasis(session);
            var relationship = ConnectionContextBuilder.Build(session, "", live).Relationship;
            var sampleSig = live == null ? SourceFactsSignature(session) : "";
            var data = live == null ? "none" + sampleSig + (sampleSig.Length == 0 ? "" : !withSample ? WithheldFromJevMark : only != null ? JevScopeMark + only.Digest : "")
                : live.Kind + "|" + live.DataSource + "|" + live.Database + "|" + RuntimeHelpers.GetHashCode(live).ToString(CultureInfo.InvariantCulture);
            var key = string.Join("\n", session.Id, session.Revision.ToString(CultureInfo.InvariantCulture),
                basis.Signature, basis.Coverage.Complete ? "complete" : "partial", relationship, data);
            return (key, basis, relationship);
        }

        /// <summary>Part of the cache key for a model with no connection: changes when its source facts file does, so a sample
        /// that arrives (or is replaced) without the revision moving is not hidden behind an older snapshot.</summary>
        private static string SourceFactsSignature(Session session)
        {
            var dir = SourceFactsDirFor(session);
            var sig = dir == null ? null : Facts.SourceFactsStore.Signature(dir);
            return sig == null ? "" : "|sample|" + sig;
        }

        /// <summary>The snapshot for this session's revision. <paramref name="forJev"/>: the Jev families' own read, which lays the sample of rows over the
        /// model only when <see cref="SampleDisclosedAsync"/> says the person's permission covers it; the person's and the assistant's read always does (the
        /// assistant's is gated by QueryData, not by this).</summary>
        private async Task<Facts.ModelFacts> FactsFor(SessionContext context, Session session, bool forJev = false)
        {
            var withSample = true; DisclosedScope only = null;
            if (forJev) (withSample, only) = await JevMaySeeSampleAsync(session).ConfigureAwait(false);
            var (key, basis, relationship) = FactsKey(context, session, withSample, only);
            Task<Facts.ModelFacts> found;
            lock (_factsGate)
            {
                if (_factsByKey.TryGetValue(key, out var existing) && !existing.IsFaulted && !existing.IsCanceled)
                {
                    // The one asked for last is the one a reader of "the current snapshot" finds first.
                    _factsOrder.Remove(key);
                    _factsOrder.AddLast(key);
                    found = existing;
                }
                else found = StartFacts(context, session, basis, relationship, key, withSample, only);
            }
            return await found.ConfigureAwait(false);
        }

        /// <summary>Start computing a snapshot and cache it. Called with the facts gate held.</summary>
        private Task<Facts.ModelFacts> StartFacts(SessionContext context, Session session, ScopeBasis basis, string relationship, string key, bool withSample, DisclosedScope only)
        {
            var task = ComputeFactsAsync(context, session, basis, relationship, withSample, only);
            _factsByKey[key] = task;
            _factsOrder.Remove(key);
            _factsOrder.AddLast(key);
            while (_factsOrder.Count > FactsCacheSize)
            {
                _factsByKey.Remove(_factsOrder.First.Value);
                _factsOrder.RemoveFirst();
            }
            return task;
        }

        private void EnsureFactsSubscription()
        {
            if (Interlocked.Exchange(ref _factsSubscribed, 1) == 1) return;
            _sessions.Bus.Changed += OnFactsModelChanged;
        }

        /// <summary>Called from Dispose: no background measuring outlives the engine.</summary>
        private void StopModelFacts()
        {
            CancellationTokenSource pending;
            lock (_factsGate) { _factsStopped = true; pending = _factsBackground; _factsBackground = null; }
            try { pending?.Cancel(); } catch { /* already gone */ }
            if (Volatile.Read(ref _factsSubscribed) == 1) _sessions.Bus.Changed -= OnFactsModelChanged;
        }

        private void OnFactsModelChanged(ChangeNotification n)
        {
            CancellationTokenSource mine;
            lock (_factsGate)
            {
                if (_factsStopped || n == null || n.SessionId == null || n.SessionId != _factsSessionId) return;
                _factsBackground?.Cancel();
                mine = _factsBackground = new CancellationTokenSource();
            }
            var delay = FactsBackgroundDelayMsForTest;
            _ = Task.Run(async () =>
            {
                try
                {
                    // Debounced: a burst of edits measures once, after it settles.
                    await Task.Delay(delay, mine.Token).ConfigureAwait(false);
                    var context = _sessions.CurrentContext;
                    var session = context.Session;
                    if (session == null || session.Id != n.SessionId) return;
                    await FactsFor(context, session).ConfigureAwait(false);
                }
                catch { /* background warm-up only: the next ask computes in the foreground and reports any failure */ }
            });
        }

        private sealed class FactsModelRead
        {
            public long Revision;
            public List<TableFacts> Tables = new List<TableFacts>();
            public List<(TableFacts table, MeasureTable spec, ColumnFacts[] columns, string skip)> Plan
                = new List<(TableFacts, MeasureTable, ColumnFacts[], string)>();
            public List<StorageColumnMeta> StorageColumns = new List<StorageColumnMeta>();
            public UnusedResult Unused;
            public HashSet<string> ReportUsed;     // null = report usage unknown
            /// <summary>The source table each model table reads (from its partition), and the source column each data column
            /// reads: how a sample's facts find their way back to the model after any rename. The server and database the table
            /// reads are kept with it, so a sample of one database is never laid over a table that reads another.</summary>
            public Dictionary<TableFacts, (string schema, string item, string server, string database)> Sources =
                new Dictionary<TableFacts, (string, string, string, string)>();
            public Dictionary<ColumnFacts, string> SourceColumns = new Dictionary<ColumnFacts, string>();
        }

        /// <summary>The source table a model table reads, and the server and database it reads it from: an entity partition's own
        /// schema and name with the Sql.Database of the shared expression it reads; an M partition's navigation step with its own
        /// Sql.Database, or that of a shared expression its M names (a built model's tables read "FabricSource"); and the
        /// partition's structured data source for whatever is still missing. Server and database are null when nothing says.</summary>
        private static (string schema, string item, string server, string database)? SourceTableOf(Table t)
        {
            foreach (var p in t.Partitions)
            {
                string server = null, database = null, schema, item;
                if (p is EntityPartition ep)
                {
                    if (string.IsNullOrWhiteSpace(ep.EntityName)) continue;
                    schema = ep.SchemaName; item = ep.EntityName;
                    if (!string.IsNullOrWhiteSpace(ep.ExpressionSource?.Expression))
                        SchemaSync.TryParseSqlSource(ep.ExpressionSource.Expression, out server, out database, out _, out _);
                }
                else if (p.SourceType == PartitionSourceType.M && !string.IsNullOrWhiteSpace(p.Expression))
                {
                    SchemaSync.TryParseSqlSource(p.Expression, out server, out database, out schema, out item);
                    if (string.IsNullOrWhiteSpace(item)) continue;
                    if (string.IsNullOrWhiteSpace(server) && string.IsNullOrWhiteSpace(database))
                    {
                        var (named, _) = Lineage.MReferenceResolver.Lex(p.Expression);
                        foreach (var shared in t.Model.Expressions)
                            if (named.Contains(shared.Name) && SchemaSync.TryParseSqlSource(shared.Expression, out server, out database, out _, out _)) break;
                    }
                }
                else continue;
                var sds = p.StructuredDataSource;
                if (sds != null)
                {
                    if (string.IsNullOrWhiteSpace(server)) server = sds.Server;
                    if (string.IsNullOrWhiteSpace(database)) database = sds.Database;
                }
                return (schema, item, server, database);
            }
            return null;
        }

        private async Task<Facts.ModelFacts> ComputeFactsAsync(SessionContext context, Session session, ScopeBasis basis, string relationship, bool withSample = true, DisclosedScope only = null)
        {
            await Task.Yield();   // never run a snapshot on the caller's stack while the cache lock is held
            Interlocked.Increment(ref _factsComputations);
            var live = context.Live;
            var budget = FactsBudgetForTest ?? new FactsBudget();
            var evidence = StorageEvidenceLevels.For(relationship);
            var dataKind = relationship switch
            {
                "sameInstance" => string.Equals(live?.Kind, "local", StringComparison.OrdinalIgnoreCase) ? ModelFactsData.Local : ModelFactsData.Live,
                "workingCopyAndLocalRuntime" => ModelFactsData.Local,
                "workingCopyAndPublished" => ModelFactsData.Live,
                _ => ModelFactsData.None,
            };
            if (live == null) dataKind = ModelFactsData.None;

            var coverage = basis.Coverage;
            var read = await session.ReadAsync(m => ReadFactsModel(m, session, coverage)).ConfigureAwait(false);
            var facts = new Facts.ModelFacts
            {
                ModelIdentity = PaneIdentity(session, live),
                Revision = read.Revision,
                MeasuredAt = DateTime.UtcNow,
                Data = dataKind,
                Tables = read.Tables.ToArray(),
            };
            var producers = facts.Producers;
            AddModelProducers(facts, read, coverage);

            // ---- Measured facts: the model's own data, bounded ----
            var measuredAt = facts.MeasuredAt.ToString("u", CultureInfo.InvariantCulture);
            var measuredFrom = relationship == "sameInstance" ? "the connected model, which is the model being edited"
                : "the linked copy of this model, which may be older or newer than the copy being edited";
            var sourceSample = live == null && withSample ? LoadSourceFacts(session) : null;   // withheld: the Jev families' read without the person's permission for the sample
            if (sourceSample != null && only != null) sourceSample = sourceSample.RestrictedTo(only);   // the Jev families' read: only the tables the person's grant names
            if (sourceSample != null)
            {
                // No connection, but a sample of the source was kept beside the model when it was started.
                ApplySourceSample(facts, read, sourceSample);
            }
            else if (dataKind == ModelFactsData.None)
            {
                var why = live == null ? "No data. The model is not connected to a copy of it that holds data."
                    : "No data. The connected model is a different model from the one being edited, so its data says nothing about this one.";
                foreach (var key in MeasuredKeys) producers["measured." + key] = new FactProducer { Producer = "none", Coverage = why, Freshness = null };
                producers["tables.rows"] = new FactProducer { Producer = "none", Coverage = why, Freshness = null };
            }
            else
            {
                int measuredTables = 0, sampledTables = 0, skipped = 0, failed = 0;
                var eligible = read.Plan.Count;
                async Task<ResultSet> Execute(string q, int max) => await live.ExecuteAsync(q, max, budget.TimeoutSeconds).ConfigureAwait(false);
                foreach (var (table, spec, columns, skip) in read.Plan)
                {
                    if (skip != null) { skipped++; continue; }
                    MeasuredTable m;
                    try { m = await FactsMeasurer.MeasureAsync(Execute, spec, budget, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { m = new MeasuredTable { Error = ex.Message }; }
                    if (m.Error != null) { failed++; continue; }
                    measuredTables++;
                    if (m.Sampled) sampledTables++;
                    table.Rows = m.Rows;
                    for (var i = 0; i < columns.Length; i++)
                    {
                        var v = m.Columns[i];
                        columns[i].Measured = FactsMeasurer.ToMeasured(v, m.Sampled);
                        columns[i].Measured.OneToOneWith = m.OneToOne[i].Select(j => columns[j].Ref).OrderBy(r => r, StringComparer.Ordinal).ToArray();
                    }
                }
                var tableCoverage = measuredTables + " of " + eligible + " tables measured"
                    + (sampledTables > 0 ? ", " + sampledTables + " of them on a bounded sample of " + budget.RowLimit.ToString("N0", CultureInfo.InvariantCulture) + " rows or fewer" : "")
                    + (skipped > 0 ? ". " + skipped + " stored as DirectQuery or Direct Lake were not read, because reading them would query the source or load every column into memory" : "")
                    + (failed > 0 ? ". " + failed + " could not be read" : "")
                    + ".";
                var freshness = "Read from " + measuredFrom + " at " + measuredAt + ".";
                foreach (var key in MeasuredKeys)
                    producers["measured." + key] = new FactProducer { Producer = "value read", Coverage = tableCoverage, Freshness = freshness };
                producers["measured.placeShare"] = new FactProducer { Producer = "bundled place list and postcode patterns", Coverage = tableCoverage + " Shares are of distinct text values that are not blank.", Freshness = freshness };
                producers["measured.placeShare.city"] = new FactProducer { Producer = "none", Coverage = "Unknown. No city list is bundled yet, so the city share is always empty.", Freshness = null };
                producers["measured.sampled"] = new FactProducer { Producer = "value read", Coverage = "Set when a table had more rows than the read budget allows.", Freshness = freshness };
                producers["tables.rows"] = new FactProducer { Producer = "row count query", Coverage = tableCoverage, Freshness = freshness };
            }

            // ---- Storage scan: bytes and cardinality, and the storage findings ----
            VpaqReport report = null;
            if (live != null)
            {
                try { report = await ReadVertiPaqAsync(live, int.MaxValue, withCardinality: evidence == StorageEvidenceLevels.CurrentEditingModel).ConfigureAwait(false); }
                catch (Exception ex) { report = VpaqReport.FromError(ex.Message); }
            }
            var bytesTrusted = report != null && string.IsNullOrEmpty(report.Error) && evidence == StorageEvidenceLevels.CurrentEditingModel;
            if (bytesTrusted)
            {
                var byColumn = new Dictionary<string, VpaqColumn>(StringComparer.Ordinal);
                foreach (var c in report.TopColumns) byColumn[StorageOpportunities.ColRef(c.Table, c.Column)] = c;
                var byTable = report.Tables.GroupBy(t => t.Name ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                foreach (var t in facts.Tables)
                {
                    if (byTable.TryGetValue(t.Name, out var vt)) t.Bytes = vt.Size;
                    foreach (var c in t.Columns)
                    {
                        if (c.Measured == null || !byColumn.TryGetValue(c.Ref, out var vc)) continue;
                        c.Measured.DictionaryBytes = vc.DictionarySize;
                        c.Measured.DataBytes = vc.DataSize;
                        c.Measured.Cardinality = vc.Cardinality;
                    }
                }
            }
            var storageCoverage = report == null ? "Not read. The model is not connected."
                : !string.IsNullOrEmpty(report.Error) ? "Not read. The storage scan failed: " + report.Error
                : !bytesTrusted ? "Not used for sizes. The connected model is not the model being edited, so its sizes are not this model's."
                : report.StorageMode == VpaqStorageMode.Import ? "Every column the storage scan reported."
                : report.Caveat ?? "Every column the storage scan reported.";
            var storageFreshness = report == null || !string.IsNullOrEmpty(report.Error) ? null : "Read at " + measuredAt + ".";
            producers["tables.bytes"] = new FactProducer { Producer = "storage scan", Coverage = storageCoverage, Freshness = storageFreshness };
            producers["measured.dictionaryBytes"] = producers["measured.dataBytes"] = new FactProducer { Producer = "storage scan", Coverage = storageCoverage, Freshness = storageFreshness };
            producers["measured.cardinality"] = new FactProducer
            {
                Producer = "column statistics",
                Coverage = bytesTrusted && report.StorageMode == VpaqStorageMode.Import ? storageCoverage
                    : bytesTrusted ? "Not read. Column statistics are read only once the model is proven to be imported, because on Direct Lake they would load every column into memory."
                    : storageCoverage,
                Freshness = storageFreshness,
            };

            if (report != null && string.IsNullOrEmpty(report.Error))
            {
                facts.StorageOpportunities = StorageOpportunities.Compute(report, read.Unused, read.StorageColumns, evidence);
                producers["storageOpportunities"] = new FactProducer
                {
                    Producer = "storage scan, unused sweep and column settings",
                    Coverage = evidence == StorageEvidenceLevels.CurrentEditingModel ? "Every storage rule."
                        : evidence == StorageEvidenceLevels.StaleQueryCopy ? "Linked copy: size findings are observations of the copy; the summarize rule is not run."
                        : "Size findings describe the connected model only; the unused and summarize rules are not run.",
                    Freshness = "Read at " + measuredAt + ".",
                };
            }
            else
            {
                producers["storageOpportunities"] = new FactProducer { Producer = "storage scan", Coverage = storageCoverage, Freshness = null };
            }
            return facts;
        }

        private static readonly string[] MeasuredKeys =
        {
            "distinct", "blankShare", "wholeNumbers", "uniqueOnEveryRow", "looksLikeYear", "looksLikeMonthName",
            "looksLikeMonthNumber", "looksLikeDayName", "looksLikeQuarter", "looksLikePeriodNumber", "urlShare", "imageUrlShare",
            "emailShare", "oneToOneWith",
        };

        private static string StorageModeOf(Table t)
        {
            if (t is CalculatedTable || t is CalculationGroupTable) return "calculated";
            var modes = t.Partitions.Select(p => p.Mode == ModeType.Default ? t.Model.DefaultMode : p.Mode).Distinct().ToList();
            if (modes.Count == 0) return t.Model.DefaultMode == ModeType.DirectLake ? "directLake" : "unknown";
            if (modes.Count > 1) return "unknown";
            return modes[0] switch
            {
                ModeType.Import => "import",
                ModeType.DirectQuery => "directQuery",
                ModeType.Dual => "dual",
                ModeType.DirectLake => "directLake",
                ModeType.Default => "import",
                _ => "unknown",
            };
        }

        private static FactsModelRead ReadFactsModel(Model m, Session session, ReportScopeCoverage coverage)
        {
            var read = new FactsModelRead { Revision = session.Revision };

            // The unused sweep and the report reading, taken on the same model read as the settings so the two can
            // never describe different revisions. Same three states as UnusedObjectsAsync (R1).
            ReportAnalysisResult analysis = null;
            if (coverage.Parts.Count == 0) read.Unused = coverage.NoReports ? LineageGraph.UnusedNoReports(m) : LineageGraph.Unused(m);
            else
            {
                analysis = LineageGraph.AnalyzeReports(m, coverage.Parts);
                read.Unused = analysis.Unused;
            }
            if (coverage.HasChoices && !coverage.Complete)
                read.Unused = LineageGraph.WithMissingCoverage(read.Unused,
                    "A report chosen for this model has not been checked yet, so its use of this field is not known.", coverage.Caveat);
            read.Unused = LineageGraph.WithoutTables(read.Unused);   // the Plan's cleanup row counts columns and measures; unused_objects lists tables too
            // "Used in reports" is a yes or no only when every chosen report was read at this revision and read in
            // full. A report that could not be read, a part that could not be parsed or a field that could not be
            // placed all leave "no" unproven, so those columns stay unknown. Reports that were read in full and use
            // no model field at all prove "no" for every column: an empty used set is an answer (F-240).
            if (analysis != null && coverage.Complete)
            {
                var fullyRead = analysis.ReportsUnreadable == 0 && analysis.Reports.Length > 0
                    && analysis.Reports.All(r => r.Read && r.Error == null && r.Unresolved == 0);
                if (fullyRead) read.ReportUsed = new HashSet<string>(analysis.ModelFieldsUsed, StringComparer.OrdinalIgnoreCase);
            }
            var usedSomewhere = analysis == null ? null : new HashSet<string>(analysis.ModelFieldsUsed, StringComparer.OrdinalIgnoreCase);

            var roles = new Dictionary<Column, string>();
            foreach (var r in m.Relationships.OfType<SingleColumnRelationship>())
            {
                void Mark(Column c, RelationshipEndCardinality card)
                {
                    if (c == null) return;
                    var role = card == RelationshipEndCardinality.One ? "one" : "many";
                    // A column on the one side of ANY relationship is a key: "one" wins over "many".
                    if (!roles.TryGetValue(c, out var had) || (had == "many" && role == "one")) roles[c] = role;
                }
                Mark(r.FromColumn, r.FromCardinality);
                Mark(r.ToColumn, r.ToCardinality);
            }

            foreach (var t in m.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var columns = t.Columns.Where(c => c.Type != ColumnType.RowNumber).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                var mode = StorageModeOf(t);
                var tf = new TableFacts
                {
                    Ref = ObjectRefs.For(t),
                    Name = t.Name,
                    StorageMode = mode,
                    IsDateTableMarked = string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase),
                };
                var cfs = new ColumnFacts[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                {
                    var c = columns[i];
                    var cref = ObjectRefs.For(c);
                    int usedBy;
                    try { usedBy = c.ReferencedBy.Deep().OfType<Measure>().Count(); } catch { usedBy = 0; }
                    cfs[i] = new ColumnFacts
                    {
                        Ref = cref,
                        Name = c.Name,
                        DataType = c.DataType.ToString(),
                        IsHidden = c.IsHidden,
                        SummarizeBy = c.SummarizeBy.ToString(),
                        DataCategory = c.DataCategory,
                        SortByColumn = c.SortByColumn == null ? null : ObjectRefs.For(c.SortByColumn),
                        FormatString = c.FormatString,
                        IsAvailableInMdx = c.IsAvailableInMDX,
                        IsKey = c.IsKey,
                        RelationshipRole = roles.TryGetValue(c, out var role) ? role : "none",
                        UsedByMeasures = usedBy,
                        // A field a report was SEEN to use is used even when coverage is incomplete; only "no" needs
                        // the full reading.
                        UsedInReports = read.ReportUsed != null ? read.ReportUsed.Contains(cref)
                            : usedSomewhere != null && usedSomewhere.Contains(cref) ? true : (bool?)null,
                    };
                    read.StorageColumns.Add(new StorageColumnMeta
                    {
                        Ref = cref, Name = c.Name, Table = t.Name, DataType = c.DataType.ToString(),
                        SummarizeBy = c.SummarizeBy.ToString(), IsKey = c.IsKey,
                    });
                }
                tf.Columns = cfs;
                read.Tables.Add(tf);
                var sourceTable = SourceTableOf(t);
                if (sourceTable != null) read.Sources[tf] = sourceTable.Value;
                for (var i = 0; i < columns.Count; i++)
                    if (columns[i] is DataColumn dc && !string.IsNullOrWhiteSpace(dc.SourceColumn)) read.SourceColumns[cfs[i]] = dc.SourceColumn;

                var skip = mode == "directQuery" || mode == "directLake" ? "not read: " + mode : null;
                if (t is CalculationGroupTable || columns.Count == 0) continue;   // nothing a data fact could describe
                read.Plan.Add((tf, new MeasureTable { Name = t.Name, Columns = columns.Select(c => c.Name).ToArray() }, cfs, skip));
            }
            return read;
        }
    }
}
