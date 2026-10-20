using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>What the gate does with an assistant's call to one engine method.</summary>
    internal enum GateKind
    {
        /// <summary>Goes through with nothing asked: it changes neither the person's model, nor its files, nor the notes beside
        /// it, nor the engine's settings (a read, a probe, a preview, in-memory flow state, the sanctioned assistant queue).
        /// Each carries the reason it is allowed.</summary>
        Pass,
        /// <summary>Asked about when the person's policy says so (<see cref="AgentPolicy"/>, the "local" row of the capability):
        /// the person reads words made from the request, and the approval is that call, at that revision, on that model.</summary>
        Hold,
        /// <summary>A write to a live or published target. Gated at its own op by <see cref="LocalEngine.GuardAgent"/>, which
        /// reads the target's label; the decorator adds nothing the op does not already know (the label is read there).</summary>
        Live,
        /// <summary>Reads rows of data. Gated at its own op (QueryData, session grants), as today.</summary>
        Data,
        /// <summary>A control that decides what the assistant may do or work on. The assistant is sent to the person.</summary>
        Person,
        /// <summary>Permission to send the model's details out: always the person's question, in every preset.</summary>
        Consent,
        /// <summary>A setting the assistant may tighten, never loosen: a loosening is refused as <see cref="Person"/>, a
        /// tightening is an ordinary <see cref="Hold"/>.</summary>
        Ratchet,
    }

    [Flags]
    internal enum GateWitness
    {
        None = 0,
        /// <summary>The model's revision: any commit by anyone moves it, so an approval is for the model as the person saw it.</summary>
        Revision = 1,
        /// <summary>The state of each path the call names (see <see cref="GateEntry.PathArgs"/>): realpath of the deepest
        /// existing parent, the rest, and absent or a hash of what is there.</summary>
        Paths = 2,
        /// <summary>The state of the open model's own files on disk.</summary>
        ModelFiles = 4,
    }

    /// <summary>One engine call as the gate sees it: the method, its parameters and the arguments the assistant passed.</summary>
    internal sealed class GateCall
    {
        public LocalEngine Engine { get; init; }
        public MethodInfo Method { get; init; }
        public ParameterInfo[] Parameters { get; init; }
        public object[] Args { get; init; }

        public T Arg<T>(string name)
        {
            var i = Array.FindIndex(Parameters, p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (i < 0 || Args[i] == null) return default;
            return Args[i] is T t ? t : default;
        }

        public bool Has(string name) => Array.FindIndex(Parameters, p => string.Equals(p.Name, name, StringComparison.Ordinal)) >= 0;
    }

    /// <summary>
    /// One row of the registry: for an engine method, the capability it needs, what an approval is bound to, the words the
    /// person is asked with, and an optional read-only hook that works those words out from the model (a script's objects, the
    /// rows an undo takes back). There is no default row: a method without one is refused.
    /// </summary>
    internal sealed class GateEntry
    {
        public string Method { get; init; }
        public GateKind Kind { get; init; }
        public AgentCapability Capability { get; init; } = AgentCapability.Read;
        /// <summary>Further capabilities the call also uses (a restore edits and writes files). All must allow it.</summary>
        public AgentCapability[] Also { get; init; } = Array.Empty<AgentCapability>();
        public GateWitness Witness { get; init; } = GateWitness.None;
        /// <summary>Template for the sentence after "Your assistant wants to ", filled from the request (<see cref="GateWords"/>).</summary>
        public string Words { get; init; }
        /// <summary>Names of the arguments that are files or folders this call writes.</summary>
        public string[] PathArgs { get; init; } = Array.Empty<string>();
        /// <summary>Paths this call writes that sit inside an argument rather than being one (the file an apply_diff target names).</summary>
        public Func<GateCall, string[]> PathsOf { get; init; }
        /// <summary>Content the model's revision does not move with, that an approval must still be bound to (the lines of the assistant's
        /// change plan, which sit in the session's memory): read-only, and read again inside the apply so a line edited after the question
        /// is refused there.</summary>
        public Func<GateCall, Task<string>> Basis { get; init; }
        /// <summary>What this write reads that is NEITHER the open model NOR the call's own arguments (the spec in the session's memory, the
        /// file a copy is taken from, the note an edit replaces). Empty means the row reads only the model and its arguments, which the
        /// revision and the arguments already bind. A row that names an input here must say how the grant is bound to it: a
        /// <see cref="Basis"/> (a hash of what the write will use, checked again under the store's own lock just before the write), or the
        /// path witness for a file. <c>AssistantGateWitnessTests</c> holds every row to that.</summary>
        public string[] OutsideInputs { get; init; } = Array.Empty<string>();
        /// <summary>The name of the model the question is about, when it is not the open one (the start plan's model is not open yet). Null reads the open model.</summary>
        public Func<GateCall, string> ModelName { get; init; }
        /// <summary>Works the sentence out from the model, read-only, when the request alone cannot say what will change.</summary>
        public Func<GateCall, Task<string>> Describe { get; init; }
        /// <summary>For a method whose kind depends on its arguments or the model (an open that may throw away unsaved work, a
        /// run that may or may not save): chooses the entry that applies to this call.</summary>
        public Func<GateCall, Task<GateEntry>> Choose { get; init; }
        /// <summary>True for a call that is the person's question in EVERY preset, not only where the matrix asks (the Allow of the open and
        /// standard presets does not apply): a write whose harm is not a changed file but what the file later makes the engine do. A held
        /// row only; the person's own call is never asked.</summary>
        public Func<GateCall, bool> AsksEveryPreset { get; init; }
        /// <summary>For <see cref="GateKind.Ratchet"/>: true when this call loosens what it sets.</summary>
        public Func<GateCall, Task<bool>> Loosens { get; init; }
        /// <summary>The reason a Pass, Live, Data or Person entry is what it is, in a sentence.</summary>
        public string Why { get; init; }

        public AgentCapability[] Capabilities => new[] { Capability }.Concat(Also).Distinct().ToArray();

        /// <summary>Set to false for a call a rehearsal (dry_run) must not run: it changes something the rehearsal's rollback does not
        /// cover (a session, the undo timeline, an engine setting, a queue, a file), so running it would do it for real under a report
        /// that says nothing changed. Left unset, the answer follows from the row: a read or a model edit may be rehearsed.</summary>
        public bool? Preview { get; init; }

        /// <summary>True when a rehearsal may run this call. A read may, and so may a write that lands in the open model (it is rolled back).
        /// A call that reads rows of data may not: it records the question it asked beside the model. Only a read or a model edit may:
        /// the rule is built into the row (its kind and capability), not kept in a list of the calls that must not.</summary>
        public bool CanPreview => Preview ?? (Kind switch
        {
            GateKind.Pass => true,
            GateKind.Hold => Capabilities.All(c => c == AgentCapability.EditLocal) && PathArgs.Length == 0 && PathsOf == null
                && (Witness & (GateWitness.Paths | GateWitness.ModelFiles)) == 0,
            _ => false,
        });
    }
}
