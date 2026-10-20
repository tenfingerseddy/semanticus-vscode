using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>
    /// The model a request was made for, as the page that sent it was showing it (<c>SessionInfo.ModelIdentity</c>). The RPC door reads it from the
    /// request's top-level <c>modelIdentity</c> member and holds it here for the length of that request, so <see cref="PersonFence"/> can check it without
    /// one method of the engine having to name it. Null or empty means the caller named no model (an older client, a native command, the MCP door, a test).
    /// </summary>
    internal static class RequestModelIdentity
    {
        /// <summary>The wire name of the member a request carries it in.</summary>
        public const string WireName = "modelIdentity";

        /// <summary>The wire name of the second member: <c>namesModel: true</c> says the sender is the extension's own writer, which stamps the model on EVERY request it
        /// makes. A request that says so and carries no identity is therefore one whose sender could not say which model it meant (drafts-sol round 3).</summary>
        public const string StrictWireName = "namesModel";

        private static readonly AsyncLocal<string> Held = new AsyncLocal<string>();
        private static readonly AsyncLocal<bool> HeldStrict = new AsyncLocal<bool>();
        public static string Current => Held.Value;

        /// <summary>True when the request came from a writer that names a model on every request (see <see cref="StrictWireName"/>).</summary>
        public static bool Strict => HeldStrict.Value;

        public static IDisposable Begin(string identity, bool strict = false)
        {
            var before = Held.Value;
            var beforeStrict = HeldStrict.Value;
            Held.Value = string.IsNullOrWhiteSpace(identity) ? null : identity;
            HeldStrict.Value = strict;
            return new Restore(before, beforeStrict);
        }

        private sealed class Restore : IDisposable
        {
            private readonly string _before;
            private readonly bool _beforeStrict;
            public Restore(string before, bool beforeStrict) { _before = before; _beforeStrict = beforeStrict; }
            public void Dispose() { Held.Value = _before; HeldStrict.Value = _beforeStrict; }
        }
    }

    internal enum FenceClass
    {
        /// <summary>A write (or anything that acts on the open model): refused when the request names another model than the one open.</summary>
        Fenced,
        /// <summary>A read, or a signal that writes nothing the person typed: never refused for this.</summary>
        ReadOnly,
        /// <summary>Written down as exempt, with its reason: it does not act on the model that is open.</summary>
        Exempt,
    }

    /// <summary>
    /// The ONE decision of which engine methods the person-door fence covers (drafts-sol round 2, C1). It is read from the gate registry, never listed editor by
    /// editor: a method is a write when the registry does not say it is a plain read, so a method added tomorrow is fenced until someone writes down why it is not.
    /// <c>ModelFenceTests</c> fails on a method with no class and on an exempt row with no reason.
    /// </summary>
    internal static class ModelFence
    {
        /// <summary>The methods that are not fenced although the registry holds them, each with the reason. Few, and each acts on no open model's content.</summary>
        internal static readonly IReadOnlyDictionary<string, string> Exemptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OpenAsync"] = "It replaces the session. It names a new model, not a change to the open one; unsaved work is guarded by its own check.",
            ["CreateModelAsync"] = "It replaces the session with a new empty model; unsaved work is guarded by its own check.",
            ["OpenLocalAsync"] = "It replaces the session with the model running in Power BI Desktop; unsaved work is guarded by its own check.",
            ["OpenLiveAsync"] = "It replaces the session with a published model; unsaved work is guarded by its own check.",
            ["CancelDaxAsync"] = "Stopping a running query must always work, whichever model the page thinks is open.",
            ["WithdrawConsentAsync"] = "Taking permission back only narrows what leaves the machine, so it is never refused.",
        };

        /// <summary>Methods that change engine state the registry could not call a plain read, yet that carry no person's words and no model content: a poll, a signal,
        /// a check that rolls itself back. They are not fenced.</summary>
        internal static readonly IReadOnlyDictionary<string, string> Signals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PublishActivityAsync"] = "The engine's own activity signal.",
            ["PullAgentHealthAsync"] = "A poll: it moves the reader's own cursor.",
            ["PullForeignChangesAsync"] = "A poll: it moves the reader's own cursor.",
            ["PullPlanOfferAsync"] = "A poll: it moves the reader's own cursor.",
            ["ReplayCheckWorkflowAsync"] = "A check: it runs rehearsals that roll themselves back.",
            ["DryRunOpAsync"] = "A rehearsal: the model's changes are rolled back before it returns.",
            ["ClearCacheAsync"] = "It empties a cache; no word and no model content is written.",
            ["GetStartPlanAsync"] = "A read that may start the plan's background work.",
            ["GetStartWordsAsync"] = "A read of the start plan's open words.",
            ["ShowInStudioAsync"] = "A note for the person: it changes nothing in the model.",
            ["SuggestNextAsync"] = "Buttons for the person: they change nothing in the model.",
            ["SetNextStepAsync"] = "The person's suggestion preference: it changes nothing in the model.",
            ["NoteAgentCallAsync"] = "A signal that a call began: it changes nothing in the model.",
            ["SetStudioDriverAsync"] = "The Studio reports its own screen: it writes nothing in the model.",
            ["QueryNotebookAsync"] = "It keeps or drops rows of the question history beside the model: it changes nothing in the model.",
            ["RecordQueryRunAsync"] = "A row of the question history beside the model: it changes nothing in the model.",
            ["NoteQueryRowAsync"] = "A note on a row of the question history beside the model: it changes nothing in the model.",
            ["GetHostSettingsAsync"] = "A read of the person's personality and sound settings.",
            ["SetHostSettingsAsync"] = "The person's Studio settings: they change nothing in the model.",
            ["ReportHostSettingsAsync"] = "The window reports its own settings: it writes nothing in the model.",
            ["StartTrialAsync"] = "It starts the free Pro month: it changes nothing in the model.",
        };

        /// <summary>The Fenced methods a request from the extension's own writer may make with NO model named while a model is open, each with the reason. A method
        /// the extension sends from outside any person's action (a timer, an engine start, a licence swap) and that acts on no open model's content. FEW: a row added
        /// because a command forgot to name its model hides the very defect this fence exists for, so a new row needs a reason that is not "the caller did not know".</summary>
        internal static readonly IReadOnlyDictionary<string, string> Unnamed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RememberXmlaConnectionAsync"] = "The window's start-up import of the connections an older build kept (migrateLegacyXmlaHistory). It runs before and outside any person's action and writes the saved connections list, never a model.",
        };

        public static FenceClass Classify(string method)
        {
            if (Exemptions.ContainsKey(method)) return FenceClass.Exempt;
            if (Signals.ContainsKey(method)) return FenceClass.ReadOnly;
            var row = GateRegistry.Find(method);
            if (row == null) return FenceClass.Fenced;   // no row is not a license to skip the fence
            if (row.Kind == GateKind.Data) return FenceClass.ReadOnly;
            if (row.Kind == GateKind.Pass && row.CanPreview) return FenceClass.ReadOnly;
            return FenceClass.Fenced;
        }

        public const string Refusal = "This page was showing another model than the one open now, so nothing was changed. Anything you typed is kept.";
    }

    /// <summary>
    /// THE fence of the person's door. Every JSON-RPC connection that authenticated as the person is served through this proxy over the whole engine interface,
    /// so it cannot forget a method: when the request names the model its page was showing, and the call is a write (<see cref="ModelFence.Classify"/>), the call is
    /// refused before it reaches the engine unless that model is the one open. A request that names none keeps today's behaviour, unless it comes from the extension's own writer (it says so with `namesModel`), which names a model on every request: then none is refused while a model is open, except the few <see cref="ModelFence.Unnamed"/> methods. Reads are never fenced.
    /// It is a sibling of <see cref="AssistantEngine"/> (the assistant's gate) and, like it, wraps only an engine that owns the model.
    /// </summary>
    internal class PersonFence : DispatchProxy
    {
        private IEngine _inner;
        private Func<string, bool> _isOpen;
        private Func<bool> _hasOpen;

        /// <summary>The engine the person's connection is served through. An engine in another process, or a test double, is not wrapped: it owns no session to compare with.</summary>
        public static IEngine For(IEngine engine) => engine is LocalEngine local ? For(local, local.IsModelIdentity, () => local.HasOpenModel) : engine;

        /// <summary>The same fence over any engine, told how to ask whether a named model is the open one (a test seam: a recording engine shows which calls get through).
        /// A model is taken to be open: the strict rule is only ever stricter.</summary>
        internal static IEngine For(IEngine inner, Func<string, bool> isOpen) => For(inner, isOpen, () => true);

        /// <summary>The same fence, also told whether ANY model is open (the strict rule: a write that names no model is refused only when there is a model to hit).</summary>
        internal static IEngine For(IEngine inner, Func<string, bool> isOpen, Func<bool> hasOpen)
        {
            var proxy = Create<IEngine, PersonFence>();
            var fence = (PersonFence)(object)proxy;
            fence._inner = inner;
            fence._isOpen = isOpen;
            fence._hasOpen = hasOpen;
            return proxy;
        }

        protected override object Invoke(MethodInfo target, object[] args)
        {
            var named = RequestModelIdentity.Current;
            if (ModelFence.Classify(target.Name) == FenceClass.Fenced)
            {
                // A write that names another model than the open one is refused (round 2). A write from the extension's own writer that names NO model is refused too while a
                // model is open (round 3): the writer names a model on every request, so a request with none is a command that could not say which model it meant.
                if (!string.IsNullOrEmpty(named))
                {
                    if (!_isOpen(named)) return Refused(target.ReturnType);
                }
                else if (RequestModelIdentity.Strict && !ModelFence.Unnamed.ContainsKey(target.Name) && _hasOpen())
                    return Refused(target.ReturnType);
            }
            // The person's door is where a press is told apart from everything the page starts by itself (SignInPress): only a press may open a sign-in window.
            using var press = SignInPress.IsPress(target, args) ? SignInPress.Press() : null;
            try { return target.Invoke(_inner, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        private static object Refused(Type returnType)
        {
            var error = new InvalidOperationException(ModelFence.Refusal);
            if (returnType == typeof(Task)) return Task.FromException(error);
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                return FromExceptionOf.MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, new object[] { error });
            throw error;
        }

        private static readonly MethodInfo FromExceptionOf = typeof(Task).GetMethod(nameof(Task.FromException), 1, new[] { typeof(Exception) });
    }
}
