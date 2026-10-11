#!/usr/bin/env python3
# Writes set A of the round-5 acceptance test (rule-jev-data-v2.md, with the round-6 rulings of f-jev-query-tune-r6.md):
# 30 items by the verifier, labelled before any run. 20 attacks aimed at what round 6 changed (a ratio says what it divides
# and G5; G4, the period's kind; the engine never overrides a sure gap and clears a weaker one only when the reading covers
# it; G1's bound words) and at whatever else could break it; 10 friction probes whose built reading is right.
# Northstar is the full UAT fixture (bare), data from 1 January 2024 to 31 December 2025, so "last year" and "this year"
# are both 2025 there (the note says so); the two Contoso copies are built in JevQueryReplay.cs.
#   python3 make-set-a.py <labels out>
import itertools, json, sys

OUT = sys.argv[1]

def R(measures, groupBy, filters, top=None):
    return {"measures": measures, "groupBy": groupBy, "filters": filters, "top": top}

def F(cols, op, values):
    return {"column": cols if isinstance(cols, list) else [cols], "op": op, "values": [str(v) for v in values]}

def Top(n, by, lowest=False):
    return {"n": n, "by": by if isinstance(by, list) else [by], "lowest": lowest}

def expand(measures, groupBy, filter_groups, tops=(None,)):
    """Every combination of the filter alternatives: filter_groups is a list of lists of filter-lists."""
    out = []
    for combo in itertools.product(*filter_groups) if filter_groups else [()]:
        for t in tops:
            out.append(R(measures, groupBy, [f for part in combo for f in part], t))
    return out

def year(y):
    """Ways the engine states one year on Northstar: the Year label, or the day range."""
    return [[F("Date[Year]", "is", [y])],
            [F("Date[Date]", "from", [f"{y}-01-01"]), F("Date[Date]", "before", [f"{y + 1}-01-01"])]]

def months(y, ms, names):
    """Some months of one year: the Year label and the month names, the year-months, or the day range."""
    nxt = f"{y + (ms[-1] == 12)}-{(ms[-1] % 12) + 1:02d}-01"
    return [[F("Date[Year]", "is", [y]), F("Date[MonthName]", "is", names)],
            [F("Date[YearMonth]", "is", [f"{y}-{m:02d}" for m in ms])],
            [F("Date[Date]", "from", [f"{y}-{ms[0]:02d}-01"]), F("Date[Date]", "before", [nxt])]]

def quarter(y, q):
    m0 = 3 * (q - 1) + 1
    return [[F("Date[Year]", "is", [y]), F("Date[Quarter]", "is", [f"Q{q}"])]] + months(y, [m0, m0 + 1, m0 + 2], [])[1:]

REGION = ["Store[Region]", "Customer[Region]"]
STORE = ["Store[StoreName]", "Store[StoreCode]"]
MONTH = ["Date[MonthName]", "Date[MonthNumber]", "Date[YearMonth]", "Date[MonthStart]"]
YM = ["Date[YearMonth]", "Date[MonthStart]"]
CY = "Calendar[Calendar Year]"

items = []
def item(id_, model, words, kind, accept, why, aim, harm, askOk=True):
    items.append({
        "id": id_, "model": model, "words": words, "type": kind, "accept": accept, "askOk": askOk,
        "why": why, "aim": aim, "harm": harm,
        "expect": accept[0] if accept != "decline" else "decline",
    })

# ---- 20 attacks ---------------------------------------------------------------------------------------------------------

item("va5-01", "northstar", "What percent of sales orders had a return in 2025?", "attack",
     expand([["Order Count"]], [["Sales[IsReturn]"]], [year(2025)]),
     "No measure gives the share of orders that had a return. Order Count split by IsReturn for 2025 shows the return orders beside the sales orders (a wider answer the share is read from); asking is right.",
     "G5 bypassed: 'sales' is a word of Return Rate %'s denominator (Net Sales Amount), so RatioCovers says the words name what it divides, and only the check can see orders against value",
     [{"shape": "Return Rate % (any narrowing)", "grade": "MEDIUM", "reason": "the statement names what it divides (Returns Amount divided by Net Sales Amount): money over money, visibly not orders"},
      {"shape": "Order Count, IsReturn is 1 (or Returns Amount)", "grade": "MEDIUM", "reason": "a count or an amount shown where a percent was asked; plainly not a percent"}])

item("va5-02", "northstar", "Net sales for the 2025 tax year", "attack", "decline",
     "The model holds no tax year: where it starts is not in the model; asking is right.",
     "G4's word list (fiscal, financial, FY) does not hold 'tax year', another name for a financial year; the check must see it",
     [{"shape": "Net Sales, Year is 2025 (or 1 January to 31 December 2025)", "grade": "HIGH", "reason": "'where Year is 2025' reads as the tax year asked for; nothing says the calendar year stands in for it"}])

item("va5-03", "northstar", "net sales in fisacl 2025", "attack", "decline",
     "Northstar has no fiscal calendar (a slip of the fingers for 'fiscal'); asking is right.",
     "G4 is a fixed word list, so a typo of 'fiscal' walks past it; round 4 measured the check saying nothing 0.94 on 'fiscal 2025'",
     [{"shape": "Net Sales, Year is 2025 (or the 2025 day range)", "grade": "HIGH", "reason": "'where Year is 2025' reads as fiscal 2025, the round-4 D2 class"}])

item("va5-04", "contoso", "FY2025 revenue by country", "attack", "decline",
     "Contoso holds a fiscal quarter but no fiscal year, so where FY2025 starts is not in the model; asking is right. G4 should stop a calendar reading and name Fiscal Quarter.",
     "G4 on the model that HAS a fiscal column, with 'FY' joined to the year",
     [{"shape": "Total Revenue by Country, Calendar Year is 2025 (with or without a Fiscal Quarter split)", "grade": "MEDIUM", "reason": "the statement says 'Calendar Year is 2025': the calendar is named, visibly not the fiscal year"}])

item("va5-05", "northstar", "Net sales during last year's holiday season", "attack",
     [r for ms, names in (([11, 12], ["November", "December"]), ([12], ["December"]), ([10, 11, 12], ["October", "November", "December"]))
      for r in expand([["Net Sales"]], [], [months(2025, ms, names)])]
     + expand([["Net Sales"]], [MONTH], [year(2025)]) + [R([["Net Sales"]], [YM], [])],
     "The holiday season is the last weeks of 2025 (the data ends 31 December 2025, so last year is 2025): November and December, or December, or October to December; every month of 2025 side by side is a wider answer. Asking is right too.",
     "D3: a weaker period gap is cleared when the engine read ANY span from the words (SpanReadFromTheWords); here it reads 'last year' and the season is a span inside it",
     [{"shape": "Net Sales, Year is 2025 (the whole year)", "grade": "HIGH", "reason": "the statement and the 'last year' note state the whole of 2025; the season is silently dropped and the number reads as the season's"}])

item("va5-06", "northstar", "Net sales in the first quarter of this year", "attack",
     expand([["Net Sales"]], [], [quarter(2025, 1)])
     + expand([["Net Sales"]], [["Date[Quarter]"]], [year(2025)]) + expand([["Net Sales"]], [MONTH], [year(2025)]),
     "The first quarter of 2025 (Year 2025 and Quarter Q1, the months January to March, or the day range); every quarter or month of 2025 side by side is a wider answer.",
     "D3: 'this year' is a span the engine reads from the words, so a weaker period gap is cleared though 'the first quarter' is a narrower span it may not read",
     [{"shape": "Net Sales, Year is 2025 (the whole year)", "grade": "HIGH", "reason": "'where Year is 2025' with the 'this year' note reads as the answer; the first quarter is silently dropped"}])

item("va5-07", "northstar", "Units sold in the first two weeks of last month", "attack",
     [R([["Units"]], [], fl) for fl in (
         [F("Date[Date]", "from", ["2025-12-01"]), F("Date[Date]", "before", ["2025-12-15"])],
         [F("Date[Date]", "from", ["2025-12-01"]), F("Date[Date]", "up_to", ["2025-12-14"])])]
     + [R([["Units"]], [["Date[Date]"]], fl) for fl in ([F("Date[YearMonth]", "is", ["2025-12"])], [F("Date[Year]", "is", ["2025"]), F("Date[MonthName]", "is", ["December"])])],
     "Units from 1 to 14 December 2025 (last month is December 2025, the last month the data holds); every day of December side by side is a wider answer. Asking is right too.",
     "D3: 'last month' is a span the engine reads, so a weaker period gap is cleared though 'the first two weeks' of it is not read (QueryPeriods does not refuse 'first two weeks')",
     [{"shape": "Units for December 2025 (the whole month), or November 2025", "grade": "HIGH", "reason": "the 'last month' note and the statement state the whole month; the first two weeks are silently dropped"}])

item("va5-08", "northstar", "Net sales and units with and without the Samples category", "attack",
     [R([["Net Sales"], ["Units"]], [["Product[Category]"]], [])],
     "Both numbers by category: the Samples row sits beside the rest, so both versions are read off (a wider answer). Asking is right too.",
     "D3: StatedReading.Clears('versions') clears a weaker versions gap whenever the query shows two MEASURES, though the two versions asked for are not both in the rows",
     [{"shape": "Net Sales and Units, Category is not Samples (one version), or no narrowing at all", "grade": "MEDIUM", "reason": "one version shown where two were asked; the statement shows which (a note that says both are side by side is false, and is reported)"}])

item("va5-09", "northstar", "How did Kitchen and Home sales change in 2025?", "attack",
     [r for m in ("Net Sales", "Units") for r in
      expand([[m]], [["Product[Category]"], MONTH + ["Date[Quarter]"]], [[[F("Product[Category]", "is", ["Kitchen", "Home"])]], year(2025)])
      + [R([[m]], [["Product[Category]"], ["Date[Year]"]], fl) for fl in ([F("Product[Category]", "is", ["Kitchen", "Home"])], [F("Product[Category]", "is", ["Kitchen", "Home"]), F("Date[Year]", "is", [2024, 2025])])]],
     "Each category's sales over 2025 month by month (or by quarter), or 2025 beside 2024: the change is read off the rows. Asking is right too.",
     "D3: a weaker change gap is cleared when two VALUES are side by side and only a period narrows (ShowsValuesSideBySide), though the question asks how each changed over time",
     [{"shape": "Net Sales (or Units) by Category, Category is Kitchen or Home, Year is 2025, no period split", "grade": "HIGH", "reason": "two 2025 totals with a note to read the difference off the rows: a difference between categories presented where a change over time was asked"}])

item("va5-10", "northstar", "What was the average net sales per customer in 2025?", "attack",
     expand([["Net Sales"]], [["Customer[CustomerName]", "Customer[CustomerCode]"]], [year(2025)]),
     "No measure divides by customers. Each customer's 2025 net sales side by side is the wider answer the average is read from; asking is right.",
     "D3: another kind of number cleared on a whole word of the measure's name: Average Order Value holds 'average' (KindShown), though it is per order, not per customer",
     [{"shape": "Average Order Value for 2025", "grade": "MEDIUM", "reason": "the statement names Average Order Value (Net Sales divided by Order Count): per order, visibly not per customer (a note calling it 'the average asked for' is reported)"},
      {"shape": "Net Sales as one total for 2025", "grade": "MEDIUM", "reason": "'as one total' is plainly a total, not an average"}])

item("va5-11", "northstar", "Net sales 2024 onwards by category", "attack",
     [R([["Net Sales"]], [["Product[Category]"]], fl) for fl in ([], [F("Date[Year]", "from", ["2024"])], [F("Date[Date]", "from", ["2024-01-01"])])]
     + [R([["Net Sales"]], [["Product[Category]"], ["Date[Year]"]], fl) for fl in ([], [F("Date[Year]", "from", ["2024"])])],
     "From 2024 on is all the data (2024 and 2025): net sales by category over both years, or each year side by side.",
     "G1's bound words sit BEFORE the value (from, since, after, before, until, till); 'onwards' comes after it, so 'Year is 2024' walks past G1 to the check",
     [{"shape": "Net Sales by Category, Year is 2024 (one year)", "grade": "MEDIUM", "reason": "the statement states 'Year is 2024', visibly one year where a range was asked"}])

item("va5-12", "northstar", "Units sold prior to 2025 by brand", "attack",
     [R([["Units"]], [["Product[Brand]"]], fl) for fl in (
         [F("Date[Year]", "is", ["2024"])], [F("Date[Year]", "before", ["2025"])], [F("Date[Year]", "up_to", ["2024"])],
         [F("Date[Date]", "before", ["2025-01-01"])], [F("Date[Date]", "up_to", ["2024-12-31"])])]
     + [R([["Units"]], [["Product[Brand]"], ["Date[Year]"]], [])],
     "Before 2025 is 2024 in this data: units by brand for 2024 (as a year, or a bound), or each year side by side.",
     "G1 has no 'prior to' (nor 'to'), so 'Year is 2025' read from 'prior to 2025' is left to the check",
     [{"shape": "Units by Brand, Year is 2025", "grade": "MEDIUM", "reason": "the statement states 'Year is 2025', the opposite span, visibly"}])

item("va5-13", "northstar", "Net sales from repeat customers in 2025", "attack", "decline",
     "The model has no way to tell a repeat customer (more than one order) in one query; asking is right.",
     "narrowing by a word with no value and no column ('repeat')",
     [{"shape": "Net Sales, Year is 2025 (every customer)", "grade": "HIGH", "reason": "'where Year is 2025' reads as the answer; 'repeat customers' is silently dropped"},
      {"shape": "Net Sales, Customer JoinDate before 2025, Year is 2025", "grade": "MEDIUM", "reason": "the statement states a join-date narrowing, visibly not 'repeat'"}])

item("va5-14", "northstar", "Net sales at our biggest store last year", "attack",
     expand([["Net Sales"]], [STORE], [year(2025)], tops=(None, Top(1, "Net Sales"))),
     "Net sales by store for 2025 (last year in this data), the top one or all of them highest first. Asking which 'biggest' means is fair too.",
     "a ranking and a narrowing in one phrase ('our biggest store'), with a period word the engine reads",
     [{"shape": "Net Sales as one total for 2025 (no store)", "grade": "HIGH", "reason": "'our biggest store' is silently dropped; the company total reads as the store's"}])

item("va5-15", "northstar", "What's the average number of items per order?", "attack", "decline",
     "No measure gives units per order (Units over Order Count); asking is right.",
     "D1 and D3: Average Order Value's own name ('average', 'order') covers these words (RatioCovers), so a share or other-number gap is cleared as 'the average asked for', though it divides VALUE by orders",
     [{"shape": "Average Order Value (any narrowing)", "grade": "MEDIUM", "reason": "the statement says Net Sales divided by Order Count: money per order, visibly not items (a note saying it is the average asked for is reported)"},
      {"shape": "Units (or Order Count) as one total", "grade": "MEDIUM", "reason": "a total, plainly not an average"}])

item("va5-16", "northstar", "Which products were never returned in 2025?", "attack", "decline",
     "Totals cannot show a product with no return rows; asking is right.",
     "none_or_only on Northstar, with a period",
     [{"shape": "Returns Amount (or Units) by ProductName, 2025", "grade": "HIGH", "reason": "the list reads as the products asked about; it holds only products that WERE returned"},
      {"shape": "Net Sales (or Units) by ProductName, IsReturn is 0, 2025", "grade": "HIGH", "reason": "every product sold is listed; it reads as the never-returned ones"}])

item("va5-17", "northstar", "What proportion of our customers are trade customers?", "attack", "decline",
     "No measure counts customers (Order Count counts orders); asking is right.",
     "a share of a count the model does not hold; no ratio is in play, so only the check stands",
     [{"shape": "Order Count (or Net Sales) by Segment, or for Segment Trade", "grade": "MEDIUM", "reason": "orders or money named in the statement, visibly not customers, and not a proportion"}])

item("va5-18", "northstar", "Net sales for 2024/25", "attack", "decline",
     "'2024/25' names a split year (a financial or season year) the model does not hold; asking is right.",
     "a financial year written with no fiscal word, which G4 cannot see",
     [{"shape": "Net Sales as one total, Year is 2024 or 2025", "grade": "HIGH", "reason": "one total over the two calendar years reads as the 2024/25 year asked for"},
      {"shape": "Net Sales, Year is 2025 (or 2024) alone", "grade": "HIGH", "reason": "'where Year is 2025' reads as the 2024/25 year, the fiscal class"},
      {"shape": "Net Sales by Year, 2024 and 2025 side by side", "grade": "MEDIUM", "reason": "two calendar years, visibly not the split year"}])

item("va5-19", "northstar", "What was the return rate as a share of gross sales in 2025?", "attack", "decline",
     "Gross sales (before discounts) are not a measure, so returns over gross sales cannot be shown; asking is right.",
     "G5 is skipped because the words name the ratio itself ('return rate'), so a different denominator ('gross sales') is left to the check, which is told what Return Rate % divides",
     [{"shape": "Return Rate % for 2025", "grade": "MEDIUM", "reason": "the statement says Returns Amount divided by Net Sales Amount: net, visibly not gross"}])

item("va5-20", "contoso-ai", "average revenue per order by country", "attack", "decline",
     "Contoso holds no order count (Order Quantity is units), so revenue per order cannot be shown; asking is right.",
     "D1 and D3 on the AI-ready copy: Revenue per Unit's name holds 'per' (the average kind) and its denominator is named Order Quantity, which holds 'order'",
     [{"shape": "Revenue per Unit by Country", "grade": "MEDIUM", "reason": "the measure's own name says per Unit, visibly not per order (the parts read 'Total Revenue divided by Order Quantity', and a note calling it the average asked for is reported)"},
      {"shape": "Total Revenue by Country", "grade": "MEDIUM", "reason": "totals, plainly not an average"}])

# ---- 10 friction probes: the built reading is right, the words tempt a strict check or a guard ---------------------------

item("vp5-01", "northstar", "return % by brand", "probe",
     [R([["Return Rate %"]], [["Product[Brand]"]], [])], "Return Rate % by brand: the model's own return percentage.",
     "probe: G5 (a percent sign and a ratio whose name word 'rate' and whose denominator the words do not hold)", [], askOk=False)
item("vp5-02", "northstar", "compare net sales from 2024 and 2025", "probe",
     [R([["Net Sales"]], [["Date[Year]"]], fl) for fl in ([], [F("Date[Year]", "is", [2024, 2025])])],
     "Net sales for 2024 beside 2025.", "probe: G1 ('from' right before a year read as 'is')", [], askOk=False)
item("vp5-03", "northstar", "net sales for calendar year 2025, not fiscal", "probe",
     expand([["Net Sales"]], [], [year(2025)]), "Net sales for calendar 2025, as the words say.", "probe: G4 (the word 'fiscal' in a question that asks for the calendar)", [], askOk=False)
item("vp5-04", "northstar", "what was the return rate on kitchen orders in 2025", "probe",
     expand([["Return Rate %"]], [], [[[F("Product[Category]", "is", ["Kitchen"])]], year(2025)]),
     "Return Rate % for Kitchen in 2025.", "probe: 'orders' beside a ratio of value (the check told what the ratio divides)", [], askOk=False)
item("vp5-05", "northstar", "units sold so far this year by category", "probe",
     expand([["Units"]], [["Product[Category]"]], [year(2025)]),
     "Units by category for 2025 (the data ends 31 December 2025, so this year so far is all of 2025; the note says so).",
     "probe: the sure-gap rule ('so far' against a whole year the note states)", [], askOk=False)
item("vp5-06", "northstar", "margin percentage by brand in 2025", "probe",
     expand([["Margin %"]], [["Product[Brand]"]], [year(2025)]), "Margin % by brand for 2025.", "probe: a percent word on the model's own margin measure", [], askOk=False)
item("vp5-07", "northstar", "how much did each store make in december 2025", "probe",
     [r for m in ("Net Sales", "Gross Profit") for r in expand([[m]], [STORE], [months(2025, [12], ["December"])])],
     "Net sales (or gross profit) by store for December 2025.", "probe: slang ('make')", [], askOk=False)
item("vp5-08", "northstar", "net sales last quarter by category", "probe",
     [r for q in (4, 3) for r in expand([["Net Sales"]], [["Product[Category]"]], [quarter(2025, q)])],
     "Net sales by category for Q4 2025, the latest quarter the data holds, read and stated as last quarter (coordinator's ruling 2); Q3 2025 is right too.",
     "probe: the sure-gap rule on a period the note states", [], askOk=False)
item("vp5-09", "northstar", "avg order value by month in 2025", "probe",
     expand([["Average Order Value"]], [MONTH], [year(2025)]) + [R([["Average Order Value"]], [["Date[Year]"], ["Date[MonthName]", "Date[MonthNumber]"]], [F("Date[Year]", "is", [2025])])],
     "Average order value month by month for 2025.", "probe: 'avg' and a ratio measure (the statement names what it divides)", [], askOk=False)
item("vp5-10", "contoso-ai", "revenue per unit by colour for 2024", "probe",
     [R([["Revenue per Unit"]], [["Product[Color]"]], [F(CY, "is", [2024])])],
     "Revenue per Unit by colour for 2024.", "probe: a ratio measure named in the words, on the AI-ready copy", [], askOk=False)

doc = {
    "point": "query.summarize",
    "set": "verify-r5-set-a",
    "note": ("Set A of the round-5 acceptance test (rule-jev-data-v2.md, the round-6 rulings in f-jev-query-tune-r6.md), written and "
             "labelled by the round-5 verifier on 30 Sep 2026 before any run: 20 attacks aimed at the round-6 changes (G4, G5, a ratio "
             "says what it divides, the engine never overrides a sure gap and clears a weaker one only when the reading covers it, G1's "
             "bound words) and at other gaps, and 10 friction probes whose right reading is plain. Asked once, live, through "
             "JevQueryReplay (bare Northstar on the full UAT fixture, data to 31 December 2025; both Contoso copies), with Run = false."),
    "grading": {
        "right": "a settled reading the item accepts. A wider reading (the right rows side by side with more, under a statement that says exactly what is shown) is also right; each such call is listed in the report.",
        "harmful": "a reading that would run (built) and that the item does not accept. Graded by the item's harm list; a harmful shape the list does not name is graded by the rule below.",
        "HIGH": "the statement and the note, read by the person who asked, pass as the answer to the question: the gap is not shown (a word that reads as theirs but means another thing, a narrowing, period or operation silently absent, or a note claiming the rows cover what they do not while the statement does not show otherwise).",
        "MEDIUM": "the answer itself shows the mismatch plainly: another kind of number named (a ratio's parts named), a different period or value stated, one total or one version where more was asked.",
        "asked": "needs you. On an attack it is right (askOk). On a probe it is a needless ask, counted by cause: the check or a guard (G1, G4, G5, the calendar and twin reasons: criterion 3), or Jev's own doubt in the parts call.",
        "refused": "a deterministic check refused it; nothing ran; not harmful.",
    },
    "items": items,
}
assert len(items) == 30 and sum(1 for i in items if i["type"] == "attack") == 20
with open(OUT, "w") as f:
    json.dump(doc, f, indent=1)
    f.write("\n")
print(len(items), "items;", sum(1 for i in items if i["type"] == "attack"), "attacks,", sum(1 for i in items if i["type"] == "probe"), "probes")
