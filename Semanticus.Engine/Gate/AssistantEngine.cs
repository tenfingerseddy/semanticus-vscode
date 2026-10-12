using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// THE gate: the only <see cref="IEngine"/> an assistant ever holds. The MCP door registers it, and every JSON-RPC connection
    /// that authenticated as an assistant is served through it (<see cref="EngineRpcTarget"/>), so a call that arrives over the
    /// pipe from another process is gated by the engine that owns the model.
    ///
    /// It is a proxy over the whole interface, so it cannot forget a method: each call is looked up in
    /// <see cref="GateRegistry"/> (no row, no call), the argument named <c>origin</c> is set to "agent" whatever was passed (so
    /// no call can pose as the person's), and a write the person's policy asks about is answered with a question made from the
    /// REQUEST, before the inner engine has seen it. A refused write never reaches the inner engine, so it leaves no trace to
    /// clean up. An approved write runs under an <see cref="AssistantTicket"/> in <see cref="GateContext"/>; the sinks check it
    /// again inside their own turn (see <see cref="LocalEngine"/>.Gate.cs).
    /// </summary>
    internal class AssistantEngine : DispatchProxy
    {
        private LocalEngine _inner;
        // Test seam: told the name of every call the gate lets through to the inner engine, so a test can show a refused write never
        // reached it. Null in every real run.
        private Action<string> _forwarded;

        /// <summary>The engine an assistant is handed. Wrapping a wrapped engine is the same engine.</summary>
        public static IEngine For(LocalEngine inner) => For(inner, null);

        internal static IEngine For(LocalEngine inner, Action<string> forwarded)
        {
            if (inner == null) throw new ArgumentNullException(nameof(inner));
            var proxy = Create<IEngine, AssistantEngine>();
            var gate = (AssistantEngine)(object)proxy;
            gate._inner = inner;
            gate._forwarded = forwarded;
            return proxy;
        }

        /// <summary>Gate an engine handed to an assistant: the owner's engine is wrapped, a proxy to another process is not (the
        /// gate runs in the process that owns the model, so one call is asked about once) and a wrapped one is left alone.</summary>
        public static IEngine ForAssistant(IEngine engine) => engine switch
        {
            AssistantEngine => engine,
            LocalEngine local => For(local),
            _ => engine,
        };

        public static bool IsGated(IEngine engine) => engine is AssistantEngine;

        /// <summary>The engine behind a gated one, for the door's own plumbing (health lines, the change fold). Never an assistant's.</summary>
        public static IEngine Inner(IEngine engine) => engine is AssistantEngine g ? g._inner : engine;

        protected override object Invoke(MethodInfo target, object[] args)
        {
            var rt = target.ReturnType;
            if (rt == typeof(Task)) return CallAsync(target, args);
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
                return CallOfMethod.MakeGenericMethod(rt.GetGenericArguments()[0]).Invoke(this, new object[] { target, args });
            throw new NotSupportedException("The engine method " + target.Name + " does not return a Task, so the gate cannot place its answer.");
        }

        private static readonly MethodInfo CallOfMethod = typeof(AssistantEngine).GetMethod(nameof(CallAsyncOf), BindingFlags.Instance | BindingFlags.NonPublic);

        private async Task CallAsync(MethodInfo target, object[] args)
        {
            var (ticket, forced) = await ClearAsync(target, args).ConfigureAwait(false);
            using (GateContext.Enter(target.Name, ticket, (IEngine)(object)this))
            {
                _forwarded?.Invoke(target.Name);
                try { await ((Task)target.Invoke(_inner, forced)).ConfigureAwait(false); }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
            }
        }

        private async Task<T> CallAsyncOf<T>(MethodInfo target, object[] args)
        {
            var (ticket, forced) = await ClearAsync(target, args).ConfigureAwait(false);
            using (GateContext.Enter(target.Name, ticket, (IEngine)(object)this))
            {
                _forwarded?.Invoke(target.Name);
                try { return await ((Task<T>)target.Invoke(_inner, forced)).ConfigureAwait(false); }
                catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
            }
        }

        // ---- the decision --------------------------------------------------------------------------------------------

        private async Task<(AssistantTicket Ticket, object[] Args)> ClearAsync(MethodInfo target, object[] given)
        {
            var args = given == null ? Array.Empty<object>() : (object[])given.Clone();
            var ps = target.GetParameters();
            // The assistant's call is the assistant's, whatever the call says. Every parameter named origin is overwritten.
            for (var i = 0; i < ps.Length && i < args.Length; i++)
                if (ps[i].ParameterType == typeof(string) && string.Equals(ps[i].Name, "origin", StringComparison.Ordinal)) args[i] = "agent";

            var entry = GateRegistry.Find(target.Name)
                ?? throw new InvalidOperationException("The assistant's door has no rule for " + target.Name + ", so it is not done. That is a gap in Semanticus, not something to retry.");
            var call = new GateCall { Engine = _inner, Method = target, Parameters = ps, Args = args };
            if (entry.Choose != null) entry = await entry.Choose(call).ConfigureAwait(false);

            // Inside a rehearsal (dry_run) the model's own edits are rolled back, so they run without a question, and nothing else may run:
            // a write the rollback does not cover would be done for real under a report that says nothing changed.
            var rehearsing = DryRunScope.Current?.FromTool == true;
            switch (entry.Kind)
            {
                case GateKind.Pass:
                case GateKind.Live:
                case GateKind.Data:
                case GateKind.Consent:
                    if (rehearsing && !entry.CanPreview) throw new InvalidOperationException(GateRegistry.CannotPreviewSentence);
                    return (null, args);
                case GateKind.Person:
                    throw new InvalidOperationException(PersonSentenceFor(entry.Method));
                case GateKind.Ratchet:
                    if (entry.Loosens != null && await entry.Loosens(call).ConfigureAwait(false))
                        throw new InvalidOperationException(GateRegistry.PersonOnlySentence);
                    break;
            }
            if (rehearsing)
            {
                if (!entry.CanPreview) throw new InvalidOperationException(GateRegistry.CannotPreviewSentence);
                return (null, args);
            }
            return (await HoldAsync(entry, call).ConfigureAwait(false), args);
        }

        private static string PersonSentenceFor(string method) => GateRegistry.PersonOnlySentence;

        /// <summary>The ask-approve-retry step for a write the matrix does not simply allow. Returns the ticket to run under, or
        /// null when the matrix allows it outright (the sinks read the same matrix), and throws the refusal when it does not.</summary>
        private async Task<AssistantTicket> HoldAsync(GateEntry entry, GateCall call)
        {
            var policy = AgentPolicyStore.Get();
            var caps = entry.Capabilities;
            PolicyDecision ask = default;
            var asking = false;
            foreach (var cap in caps)
            {
                var d = AgentPolicyGuard.Decide(cap, "local", "agent", isCommit: true, policy);
                if (d.Outcome == GateOutcome.Deny) throw new InvalidOperationException(d.Reason);
                if (d.Outcome == GateOutcome.Ask && !asking) { ask = d; asking = true; }
            }
            if (!asking) return null;

            var witness = await _inner.GateWitnessAsync(entry, call).ConfigureAwait(false);
            var (plain, model) = await QuestionAsync(_inner, entry, call).ConfigureAwait(false);
            var primary = entry.Capability;
            var key = Canonical(entry, call, witness);
            var intent = ApprovalLedger.IntentHash("gate", primary.ToString(), key);
            try
            {
                if (ApprovalLedger.TryConsume(primary, "local", intent))
                    return new AssistantTicket
                    {
                        Capabilities = caps, Method = entry.Method, GrantKey = intent, SessionId = witness.SessionId,
                        ExpectedRevision = witness.Revision, Paths = witness.Paths, ContentBasis = entry.Basis == null ? null : witness.Basis,
                        ModelFilesState = witness.Paths.TryGetValue(LocalEngine.ModelFilesKey, out var files) ? files : null,
                    };
            }
            catch { /* fail closed: refuse below */ }
            try { ApprovalLedger.Request(primary, "local", intent, plain, model ?? "the open model"); } catch { /* queueing is best-effort; the refusal still stands */ }
            throw new InvalidOperationException(ask.Reason);
        }

        /// <summary>The question a person is asked about a held call and the model it names (null when none is open): the words the row makes from the
        /// request, then the model. One place, so no row can ask without saying where the change would land.</summary>
        internal static async Task<(string Question, string Model)> QuestionAsync(LocalEngine inner, GateEntry entry, GateCall call)
        {
            var words = entry.Describe != null
                ? await entry.Describe(call).ConfigureAwait(false)
                : GateWords.Fill(entry.Words ?? entry.Method, call.Parameters, call.Args);
            string model;
            try { model = entry.ModelName?.Invoke(call) ?? inner.GateModelName(); } catch { model = inner.GateModelName(); }
            return (GateWords.Question(words, model), model);
        }

        /// <summary>Everything an approval is bound to, as one string: the capability, the method, every argument whole (a
        /// path as what it points at, never as the text that was typed), the model it was asked about, and what it was asked
        /// at. Any difference is a different question.</summary>
        private static string Canonical(GateEntry entry, GateCall call, GateWitnessData witness)
        {
            var sb = new StringBuilder();
            sb.Append(entry.Capability).Append('\u001e').Append(entry.Method);
            for (var i = 0; i < call.Parameters.Length; i++)
            {
                var p = call.Parameters[i];
                if (string.Equals(p.Name, "origin", StringComparison.Ordinal) || p.ParameterType == typeof(System.Threading.CancellationToken)) continue;
                sb.Append('\u001e').Append(p.Name).Append('=').Append(Canon(call.Args[i]));
            }
            sb.Append('\u001e').Append("session=").Append(witness.SessionId);
            sb.Append('\u001e').Append("model=").Append(witness.ModelIdentity);
            sb.Append('\u001e').Append("revision=").Append(witness.Revision.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(witness.Basis)) sb.Append('\u001e').Append("basis=").Append(witness.Basis);
            foreach (var kv in witness.Paths.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append('\u001e').Append("path=").Append(kv.Key).Append('@').Append(kv.Value);
            return sb.ToString();
        }

        private static string Canon(object value)
        {
            switch (value)
            {
                case null: return "∅";
                case string s: return "s:" + s;
                case bool b: return b ? "true" : "false";
                case IFormattable f when value is int || value is long || value is double || value is decimal || value is float || value is short || value is byte:
                    return f.ToString(null, CultureInfo.InvariantCulture);
                case Enum e: return e.ToString();
            }
            if (value is System.Collections.IEnumerable list && value is not System.Collections.IDictionary && value is not string)
            {
                var items = new List<string>();
                foreach (var item in list) items.Add(Canon(item));
                return "[" + string.Join("\u001f", items) + "]";
            }
            try { return "j:" + JsonSerializer.Serialize(value); } catch { return "o:" + value; }
        }
    }

    /// <summary>What an approval was asked at: the model, its revision, and the state of each path the call names.</summary>
    internal sealed class GateWitnessData
    {
        public string SessionId { get; init; } = "";
        public string ModelIdentity { get; init; } = "";
        public long Revision { get; init; } = -1;
        public IReadOnlyDictionary<string, string> Paths { get; init; } = new Dictionary<string, string>();
        /// <summary>The content the call's row binds besides the revision (see <see cref="GateEntry.Basis"/>), or "".</summary>
        public string Basis { get; init; } = "";
    }
}
