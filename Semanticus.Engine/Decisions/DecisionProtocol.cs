// Wire DTOs for the decision surface. Same shapes on both doors, by construction: the RPC target and the MCP
// tools both return these objects, so there is no second serialisation to drift.

using System;

namespace Semanticus.Engine
{
    /// <summary>One question a registered point asks, as the settings list shows it.</summary>
    public sealed class DecisionQuestionInfo
    {
        public string Name { get; set; }

        /// <summary>"choice", "score" or "noul".</summary>
        public string Shape { get; set; }

        /// <summary>Where the options come from, in plain words.</summary>
        public string OptionSource { get; set; }

        /// <summary>The option ids when they are fixed; empty when they come from the open model.</summary>
        public string[] Options { get; set; } = Array.Empty<string>();
    }

    /// <summary>One registered decision point. This list is the audit surface and the privacy disclosure: what
    /// can be asked, what it would send, how sure it has to be, and what happens when it is not sure enough.</summary>
    public sealed class DecisionPointInfo
    {
        public string Name { get; set; }
        public string Caller { get; set; }
        public string Purpose { get; set; }
        public int SchemaVersion { get; set; }

        /// <summary>Whether this point is switched on for the open model. Each model keeps its own.</summary>
        public bool Enabled { get; set; }

        /// <summary>Said beside the switch when something other than this model's own switch decided it, or when an on
        /// cannot last: an off kept for a model that might be this one, for example a Power BI Desktop file with the
        /// same name that Desktop named no path for. Null when there is nothing to say.</summary>
        public string Note { get; set; }

        /// <summary>Whether this build lets the point act at all. False while its replay has not cleared its bar
        /// (compose.draft, measure.starter and date.table today): it is never asked, whatever <see cref="Enabled"/> says, so a door
        /// shows it as not on yet rather than as a switch that is on and does nothing.</summary>
        public bool Live { get; set; } = true;

        public bool ProOnly { get; set; }
        public double ConfidenceBar { get; set; }
        public string LowConfidenceBehaviour { get; set; }

        /// <summary>The only details this point may send, as "point:field".</summary>
        public string[] Fields { get; set; } = Array.Empty<string>();

        public DecisionQuestionInfo[] Questions { get; set; } = Array.Empty<DecisionQuestionInfo>();
    }

    /// <summary>What the person has agreed may leave their machine, and whether that agreement still stands.</summary>
    public sealed class DecisionConsentInfo
    {
        public bool Granted { get; set; }

        /// <summary>The version the person agreed to. Zero when they never have.</summary>
        public int SchemaVersion { get; set; }

        /// <summary>The version this build is on. When it differs from <see cref="SchemaVersion"/> the person is
        /// asked again, because the list of details has changed.</summary>
        public int SchemaVersionInForce { get; set; }

        /// <summary>True only when permission is granted, at the current version, covering every declared field.</summary>
        public bool Current { get; set; }

        /// <summary>The fields this permission covers, as "point:field".</summary>
        public string[] Fields { get; set; } = Array.Empty<string>();

        /// <summary>Every field the product could send, as "point:field". This is the list to agree to.</summary>
        public string[] FieldsInForce { get; set; } = Array.Empty<string>();

        public DateTimeOffset? GrantedUtc { get; set; }
        public DateTimeOffset? WithdrawnUtc { get; set; }

        /// <summary>Who turned it on: "human" (the person) or "agent" (their assistant, on their word in chat). Null when nothing is granted or an older build wrote the record.</summary>
        public string GrantedBy { get; set; }

        /// <summary>Which model this permission belongs to, as the short stable name the engine keeps it under.
        /// Permission is given for ONE model at a time, so agreeing on one never speaks for another. Null when
        /// the open model has no stable name yet, which is when nothing can be agreed at all.</summary>
        public string AppliesTo { get; set; }

        /// <summary>Why permission is not current, in plain words. Null when it is.</summary>
        public string Reason { get; set; }

        /// <summary>True when this answer is the engine saying it has put the question to the person (an assistant asked to give
        /// permission, which only the person gives). Nothing was agreed. Absent from every other answer.</summary>
        public bool Asked { get; set; }

        /// <summary>The question put to the person, with the list of details, when <see cref="Asked"/>.</summary>
        public string Question { get; set; }
    }

    /// <summary>The session's suggestion spend, counted in input tokens.</summary>
    public sealed class DecisionBudgetInfo
    {
        /// <summary>The session's ceiling. NULL in the app: there is no session cap (Kane, 1 Oct), only the counter below and the month's
        /// money at the relay. Never to be rendered as a zero.</summary>
        public long? CapInputTokens { get; set; }
        public long UsedInputTokens { get; set; }
        public long? RemainingInputTokens { get; set; }

        /// <summary>What this session has cost, in US dollars at Jev's price, and the price in words.</summary>
        public double UsedUsd { get; set; }
        public string PriceNote { get; set; } = Semanticus.Decisions.JevPrice.PriceWords;

        /// <summary>How many asks reached a service this session. Counts only; never content.</summary>
        public long Requests { get; set; }

        public bool Exhausted { get; set; }

        /// <summary>Whether a suggestion service is set up at all for this session. False is the default and
        /// means the product behaves exactly as it does with no service.</summary>
        public bool ServiceAvailable { get; set; }

        /// <summary>How this session reaches the service: "relay" on a Pro licence, "vendor" on a development
        /// key, "none" when there is no service. Never a web address and never a credential.</summary>
        public string Route { get; set; } = Decisions.JevDecisionOracle.RouteNone;

        /// <summary>The whole licence's allowance for this calendar month, settled by the service across every
        /// session on every machine it is used from. NULL when there is nothing to report: no relay, or the
        /// relay could not be read, and <see cref="MonthlyReason"/> then says which. A null is never to be
        /// rendered as a zero.
        ///
        /// This is a DIFFERENT number from the four above, which count this one session on this one machine.
        /// The two can disagree without either being wrong.</summary>
        public long? MonthlyUsedInputTokens { get; set; }

        public long? MonthlyCapInputTokens { get; set; }

        /// <summary>The month's figures in US dollars at Jev's price, null when the tokens are.</summary>
        public double? MonthlyUsedUsd { get; set; }
        public double? MonthlyCapUsd { get; set; }

        /// <summary>When the month rolls over, as the service's own instant, or null when it did not say.</summary>
        public string MonthlyResetsAtUtc { get; set; }

        /// <summary>Why the monthly figures are absent, in plain words. Null when they are present.</summary>
        public string MonthlyReason { get; set; }
    }

    /// <summary>A proposed display folder and number format for a measure. Nothing here has been written, and
    /// nothing here will be written unless the person or their assistant asks for it in a separate call.</summary>
    public sealed class MeasureDefaultsProposal
    {
        public string ObjectRef { get; set; }

        /// <summary>True when a service was actually asked. False means the point was skipped and
        /// <see cref="Reason"/> says why, which is the normal case on a free session or with no service.</summary>
        public bool Asked { get; set; }

        /// <summary>Why nothing is proposed, in plain words. Null when a proposal is present.</summary>
        public string Reason { get; set; }

        /// <summary>The proposed folder, or null. Null with <see cref="Asked"/> true means either the answer was
        /// below the bar or the answer was that no folder fits.</summary>
        public string ProposedDisplayFolder { get; set; }

        /// <summary>How sure the answer was, from 0 to 1. Reported even below the bar, so a person can see that
        /// the product had an opinion and did not trust it.</summary>
        public double ProposedDisplayFolderConfidence { get; set; }

        /// <summary>One of currency, percent, whole_number, decimal, date_or_time, text_or_none, or null.</summary>
        public string ProposedFormatFamily { get; set; }

        public double ProposedFormatFamilyConfidence { get; set; }

        /// <summary>True when the proposed format is only a suggestion: THE FORMAT FILL RULE (FormatFill.MayFill) could not prove it fits
        /// what the measure shows, so a person checks it before applying it. False when the rule proves it (a whole-number format on a
        /// count). Null-equivalent (false) when no format is proposed.</summary>
        public bool ProposedFormatIsSuggestion { get; set; }

        /// <summary>The plain note to show beside a suggested format, the same words every other Jev format surface shows. Null when the
        /// format is proved or none is proposed.</summary>
        public string ProposedFormatNote { get; set; }

        /// <summary>How many of the model's display folders were NOT offered to the service, because a question offers at most
        /// thirty (the most relevant ones, this measure's own table first). Zero when they all were.</summary>
        public int FoldersNotOffered { get; set; }

        /// <summary>How sure an answer had to be before it was shown.</summary>
        public double ConfidenceBar { get; set; }

        /// <summary>The model version that answered, when one did.</summary>
        public string ServedModelVersion { get; set; }
    }
}
