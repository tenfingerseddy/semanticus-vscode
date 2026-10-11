using System;
using System.Collections.Generic;
using System.Linq;
using Semanticus.Engine;
using TabularEditor.TOMWrapper;

namespace Semanticus.Analysis
{
    /// <summary>A formula that points at an object the model does not have. <see cref="Object"/> is the model object that holds the formula (a measure, a calculated column, a
    /// calculation item, a role), <see cref="Formula"/> says which of its formulas, <see cref="Missing"/> says what is not in the model.</summary>
    public sealed class BrokenFormula
    {
        public ITabularNamedObject Object { get; set; }
        public string Formula { get; set; }
        public string[] Missing { get; set; } = Array.Empty<string>();
    }

    /// <summary>The check "a formula refers to an object the model does not have" (plan statement check 4, P2). A model whose measures point at tables and columns that no longer
    /// exist computes nothing, yet every other check reads it as fine, so AI readiness and best practice both read A on it. This is the one place that says which formulas
    /// are broken; the readiness rule (DAX-REFERENCE) and the best-practice rule (FORMULA_REFERENCE_NOT_IN_MODEL) both ask it, so they cannot disagree.
    ///
    /// The verdict is the engine's own DAX validation (<see cref="DaxValidator"/>, the code validate_dax and every DAX write run), not a pattern of this file's own.
    /// Formulas read: a measure's expression, format string expression and detail rows expression, and its KPI's status, target and trend; a calculated column's expression;
    /// a calculation item's expression and format string expression; a role's row filters. A calculated table is left out: its expression makes columns of its own that
    /// a bracketed name can mean, so it cannot be read by name alone.</summary>
    public static class FormulaReferences
    {
        /// <summary>The formulas a model object holds, as (which formula, its text). Empty ones are left out.</summary>
        public static IEnumerable<(string Formula, string Text)> FormulasOf(ITabularNamedObject obj) => HostedFormulasOf(obj).Select(f => (f.Formula, f.Text));

        /// <summary>The formulas of an object with the name of the table each one is written in (a measure's table, a calculated column's table, a calculation item's group, the table a row filter filters).</summary>
        private static IEnumerable<(string Formula, string Text, string Host)> HostedFormulasOf(ITabularNamedObject obj)
        {
            IEnumerable<(string, string, string)> all()
            {
                switch (obj)
                {
                    case Measure me:
                        yield return ("expression", me.Expression, me.Table?.Name);
                        yield return ("format string expression", me.FormatStringExpression, me.Table?.Name);
                        yield return ("detail rows expression", me.DetailRowsExpression, me.Table?.Name);
                        if (me.KPI != null)
                        {
                            yield return ("KPI status", me.KPI.StatusExpression, me.Table?.Name);
                            yield return ("KPI target", me.KPI.TargetExpression, me.Table?.Name);
                            yield return ("KPI trend", me.KPI.TrendExpression, me.Table?.Name);
                        }
                        break;
                    case CalculatedColumn cc:
                        yield return ("expression", cc.Expression, cc.Table?.Name);
                        break;
                    case CalculationItem ci:
                        yield return ("expression", ci.Expression, ci.CalculationGroupTable?.Name);
                        yield return ("format string expression", ci.FormatStringExpression, ci.CalculationGroupTable?.Name);
                        break;
                    case ModelRole role:
                        foreach (var tp in role.TablePermissions)
                            yield return ("row filter on " + tp.Table?.Name, tp.FilterExpression, tp.Table?.Name);
                        break;
                }
            }
            return all().Where(f => !string.IsNullOrWhiteSpace(f.Item2));
        }

        /// <summary>Every model object that holds formulas this check reads.</summary>
        public static IEnumerable<ITabularNamedObject> Holders(Model m)
        {
            foreach (var me in m.AllMeasures) yield return me;
            foreach (var cc in m.AllColumns.OfType<CalculatedColumn>()) yield return cc;
            foreach (var g in m.CalculationGroups)
                foreach (var ci in g.CalculationItems) yield return ci;
            foreach (var r in m.Roles) yield return r;
        }

        /// <summary>How many formulas the check reads in this model. Zero means the check has nothing to look at, so it stays dormant.</summary>
        public static int CountFormulas(Model m) => Holders(m).Sum(h => FormulasOf(h).Count());

        /// <summary>The broken formulas of one model object. <paramref name="check"/> is <see cref="Checker"/>'s answer, built once for a scan.</summary>
        public static IEnumerable<BrokenFormula> BrokenIn(ITabularNamedObject holder, Func<string, string, string[]> check)
        {
            foreach (var (formula, text, host) in HostedFormulasOf(holder))
            {
                var missing = check(text, host);
                if (missing.Length > 0) yield return new BrokenFormula { Object = holder, Formula = formula, Missing = missing };
            }
        }

        /// <summary>The engine's own reference check over this model, with the model's names read once. It answers for a formula text alone (the table it is written in is not known).</summary>
        public static Func<string, string[]> Checker(Model m) { var c = DaxValidator.MissingObjectsChecker(m); return text => c(text, null); }

        /// <summary>The same check, told the table each formula is written in: a formula written in a calculated table is not judged by its bare names.</summary>
        public static Func<string, string, string[]> HostChecker(Model m) => DaxValidator.MissingObjectsChecker(m);

        /// <summary>Every broken formula in the model.</summary>
        public static List<BrokenFormula> Scan(Model m)
        {
            var check = HostChecker(m);
            return Holders(m).SelectMany(h => BrokenIn(h, check)).ToList();
        }

        /// <summary>The same words for every surface: what the formula names that the model does not have, in plain words.</summary>
        public static string Describe(BrokenFormula b)
        {
            var named = string.Join("; ", b.Missing.Take(3).Select(m => m.TrimEnd('.')));
            var more = b.Missing.Length > 3 ? " (and " + (b.Missing.Length - 3) + " more)" : "";
            return "Its " + b.Formula + " refers to an object the model does not have: " + named + more + ". It cannot give a number until the formula points at objects that exist.";
        }
    }
}
