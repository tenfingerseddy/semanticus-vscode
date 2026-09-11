// Stubs for the four run-time symbols origin/main's Workflow.cs mentions and the PARSER never reaches.
// They exist only so the pre-v2 parser compiles on its own; none of them participates in a fingerprint.
//
// Retained and auditable on purpose (Sol round 6, finding 4): an oracle whose scaffolding nobody can read
// is an oracle nobody can check. Each stub is the smallest thing that satisfies the compiler, so it cannot
// quietly supply behaviour of its own. If one of these ever needs a real body, that means the parser has
// started depending on run-time state and the capture is no longer honest.
namespace Newtonsoft.Json
{
    // Workflow.cs decorates two properties with [Newtonsoft.Json.JsonIgnore] for the RPC wire. Parsing
    // never serializes anything, so an empty attribute is the whole requirement.
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Field)]
    public sealed class JsonIgnoreAttribute : System.Attribute { }
}

namespace Semanticus.Engine
{
    // Carried on MismatchCell, which is a RUN record. Clone() returns this because nothing clones during a
    // parse; an identity clone cannot mask a copying bug that a parse could never trigger.
    public sealed class MismatchContextPart { public MismatchContextPart Clone() => this; }

    // Only AnchorGate.Anchor is referenced, as the element type of a run-state array.
    public static class AnchorGate { public sealed class Anchor { } }

    // WorkflowDef.HasEnforcedGate calls this. It is an ENTITLEMENT question asked at run time, never during
    // a parse, and no fingerprinted field reads it. "off" is the inert answer.
    public static class WorkflowRunner
    {
        public static string EffectiveStrictness(WorkflowDef d, GateSpec g, string settings, string global) => "off";
    }
}
