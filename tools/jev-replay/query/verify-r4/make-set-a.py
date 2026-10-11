#!/usr/bin/env python3
# Writes set A of the round-4 acceptance test (rule-jev-data-v2.md): 30 items by the verifier, labelled before any run.
# Output: tools/jev-replay/query/verify-r4/set-a-labels.json in the lane worktree.
import itertools, json, sys

OUT = sys.argv[1]

def R(measures, groupBy, filters, top=None):
    return {"measures": measures, "groupBy": groupBy, "filters": filters, "top": top}

def F(cols, op, values):
    return {"column": cols if isinstance(cols, list) else [cols], "op": op, "values": [str(v) for v in values]}

def years_ns(y):
    """Ways the engine states one year on Northstar: the Year label, or the day range."""
    return [[F("Date[Year]", "is", [y])],
            [F("Date[Date]", "from", [f"{y}-01-01"]), F("Date[Date]", "before", [f"{y + 1}-01-01"])]]

REGION = ["Store[Region]", "Customer[Region]"]
CITY = ["Store[City]", "Customer[City]"]
MONTH = ["Date[MonthName]", "Date[MonthNumber]", "Date[YearMonth]", "Date[MonthStart]"]
ONLINE_OUT = [[F(["Store[Channel]", "Store[Region]", "Store[City]"], "is_not", ["Online"])],
              [F("Store[StoreName]", "is_not", ["Northstar Online"])]]
HEADLINE = ["Net Sales", "Units", "Gross Profit", "Order Count"]

def expand(measures, groupBy, filter_groups, top=None):
    """Every combination of the filter alternatives: filter_groups is a list of lists of filter-lists."""
    out = []
    for combo in itertools.product(*filter_groups) if filter_groups else [()]:
        out.append(R(measures, groupBy, [f for part in combo for f in part], top))
    return out

items = []
def item(id_, model, words, kind, accept, why, aim, harm, askOk=True):
    items.append({
        "id": id_, "model": model, "words": words, "type": kind, "accept": accept, "askOk": askOk,
        "why": why, "aim": aim, "harm": harm,
        "expect": accept[0] if accept != "decline" else "decline",
    })

# ---- 20 aimed at the check (gaps and slips not in C01 to C20 or round 2) -----------------------------------------------

item("va4-01", "northstar", "What percentage of orders had a return?", "attack",
     [R([["Order Count"]], [["Sales[IsReturn]"]], [])],
     "No measure gives returned orders over all orders. Order Count split by IsReturn shows both counts side by side (a wider answer); asking is right.",
     "share gap cleared by a ratio measure of another kind (Return Rate % is returned VALUE over sales value): StatedReading.Clears('share') via QueryCatalog.IsRatio",
     [{"shape": "Return Rate % (any narrowing)", "grade": "HIGH", "reason": "'This shows Return Rate % as one total.' reads as the percentage asked for, and the cleared note says the ratio is the share asked for; it is a share of money, not of orders"},
      {"shape": "Order Count, IsReturn is 1", "grade": "MEDIUM", "reason": "a count shown where a percentage was asked; the statement plainly says a count"},
      {"shape": "Returns Amount", "grade": "MEDIUM", "reason": "an amount of money where a percentage was asked"}])

item("va4-02", "northstar", "What share of gross profit came from kitchen products in 2025?", "attack",
     expand([["Gross Profit"]], [["Product[Category]"]], [years_ns(2025)]),
     "Gross profit by category for 2025, side by side, is the wider answer the person reads the share from; asking is right.",
     "share gap cleared by any ratio measure (Margin % is itself a ratio)",
     [{"shape": "Margin %, Category is Kitchen, 2025", "grade": "HIGH", "reason": "a percent for Kitchen that reads as Kitchen's share, and the note says the ratio is the share asked for; margin is not a share of profit"},
      {"shape": "Gross Profit, Category is Kitchen, 2025", "grade": "MEDIUM", "reason": "an amount shown where a share was asked; plainly not a percent"}])

item("va4-03", "northstar", "Top 5 products by margin in dollars", "attack",
     [R([["Gross Profit"]], [["Product[ProductName]"]], [], {"n": 5, "by": ["Gross Profit"], "lowest": False})],
     "Margin in dollars is Gross Profit; the top 5 products by it.",
     "other_number gap cleared because the measure's name carries the word (KindShown 'margin' matches 'Margin %')",
     [{"shape": "Margin % by ProductName, top 5", "grade": "MEDIUM", "reason": "the statement names Margin %, a percent, plainly not dollars (though the note claims it is the margin asked for)"}])

item("va4-04", "contoso", "What's the average revenue per order in Australia?", "attack", "decline",
     "Contoso holds no order count (Order Quantity is units), so revenue per order cannot be worked out; asking is right.",
     "share and other_number gaps cleared by a ratio of another kind (Revenue per Unit: IsRatio and a 'per' in its name)",
     [{"shape": "Revenue per Unit, Country is Australia", "grade": "MEDIUM", "reason": "the statement names Revenue per Unit: per unit, visibly not per order"},
      {"shape": "Total Revenue, Country is Australia", "grade": "MEDIUM", "reason": "'as one total' plainly a total, not an average"}])

item("va4-05", "northstar", "How did each month of 2025 compare with the same month of 2024?", "attack",
     [r for m in (["Date[MonthName]", "Date[MonthNumber]"],) for fl in ([], [F("Date[Year]", "is", [2024, 2025])])
      for r in [R([HEADLINE], [m, ["Date[Year]"]], fl)]]
     + [R([HEADLINE], [["Date[YearMonth]", "Date[MonthStart]"]], fl) for fl in ([], [F("Date[Year]", "is", [2024, 2025])])],
     "Every month of 2024 and 2025 side by side (split by a month column and the year, or by year-month), so each month is read against the same month a year before. No measure is named: any headline measure is fair.",
     "change gap cleared by a split by a period (SplitByAPeriod) that does not hold the compared year",
     [{"shape": "split by a month column, Year is 2025 only", "grade": "HIGH", "reason": "the statement says Year is 2025, and the cleared note says the periods are side by side so the change can be read off the rows, while 2024 is not there"}])

item("va4-06", "northstar", "Net sales by month in 2025, with and without the Samples category", "attack",
     expand([["Net Sales"]], [["Date[MonthName]", "Date[MonthNumber]", "Date[YearMonth]", "Date[MonthStart]"], ["Product[Category]"]], [years_ns(2025)]),
     "Split by month and by category for 2025: the Samples row sits beside the rest each month, so both versions are read off the rows (a wider answer).",
     "versions gap cleared by a split by a period (Clears('versions') returns the change sentence when ShowsPeriodsSideBySide)",
     [{"shape": "month split, 2025, Category is not Samples (or no category narrowing)", "grade": "MEDIUM", "reason": "one version shown where two were asked; the statement shows which one (or shows no Samples part at all), and one number per month is visibly one version"}])

item("va4-07", "northstar", "Net sales in fiscal 2025", "attack", "decline",
     "Northstar has no fiscal calendar: where its fiscal year starts is not in the model, so fiscal 2025 cannot be read; asking is right.",
     "the financial-year class (the builder's likeliest HIGH); the period gap is cleared whenever a period filter is stated (StatesAPeriod)",
     [{"shape": "Net Sales, Year is 2025 (or 1 Jan to 31 Dec 2025)", "grade": "HIGH", "reason": "'where Year is 2025' does not say calendar year; the person reads it as fiscal 2025"}])

item("va4-08", "northstar", "Net sales during the 2024 holiday season", "attack",
     [R([["Net Sales"]], [], [F("Date[Year]", "is", [2024]), F("Date[MonthName]", "is", mv)]) for mv in (["November", "December"], ["December"], ["October", "November", "December"])]
     + [R([["Net Sales"]], [], [F("Date[YearMonth]", "is", mv)]) for mv in (["2024-11", "2024-12"], ["2024-12"], ["2024-10", "2024-11", "2024-12"])]
     + [R([["Net Sales"]], [], [F("Date[Date]", "from", [d]), F("Date[Date]", "before", ["2025-01-01"])]) for d in ("2024-11-01", "2024-12-01", "2024-10-01")]
     + expand([["Net Sales"]], [["Date[MonthName]", "Date[MonthNumber]", "Date[YearMonth]", "Date[MonthStart]"]], [years_ns(2024)]),
     "The holiday season is the last months of 2024 (November and December, or December, or October to December); every month of 2024 side by side is a wider answer; asking is right.",
     "a period read as a different span (the season dropped, the year kept); the period gap is cleared by the stated year",
     [{"shape": "Net Sales, Year is 2024 (the whole year)", "grade": "HIGH", "reason": "the statement states 2024 and silently drops the season; it reads as the season's number"}])

item("va4-09", "northstar", "Which stores stopped selling trail tents?", "attack",
     [R([[m]], [["Store[StoreName]", "Store[StoreCode]"], ["Date[Year]", "Date[YearMonth]", "Date[MonthStart]", "Date[Quarter]"]], [F("Product[ProductName]", "is", ["Trail Tent"])]) for m in ("Net Sales", "Units", "Order Count")],
     "Tent sales per store over time, side by side: a store that stopped shows no later rows (a wider answer); asking is right.",
     "none_or_only (an absence over time), not in C01 to C20",
     [{"shape": "by StoreName, ProductName is Trail Tent, no period split", "grade": "HIGH", "reason": "'for each StoreName, where ProductName is Trail Tent' reads as the stores in question; it lists the stores that sold tents"}])

item("va4-10", "contoso-ai", "Which brands had no returns?", "attack", "decline",
     "A total cannot show a brand with no rows; asking is right.",
     "none_or_only on the AI-ready copy",
     [{"shape": "Returned Units by Brand", "grade": "HIGH", "reason": "the list reads as the brands asked about; it holds only brands that had returns (brands with none have no row)"}])

item("va4-11", "northstar", "Net sales excluding returns and online", "attack",
     expand([["Net Sales"]], [], [[[F("Sales[IsReturn]", "is", [0])], [F("Sales[IsReturn]", "is_not", [1])]], ONLINE_OUT])
     + expand([["Net Sales"], ["Returns Amount"]], [], [ONLINE_OUT]),
     "Net sales with the return lines left out (IsReturn 0) and the online store left out; or Net Sales beside Returns Amount without online, to add up; asking is right.",
     "narrowing: two exclusions, one with no word in the data (returns is Sales[IsReturn] = 1)",
     [{"shape": "Net Sales, only the online store left out", "grade": "HIGH", "reason": "'where Channel is not Online' silently keeps the returns in (they reduce net sales)"}])

item("va4-12", "northstar", "Net sales at the Harborview pop-up", "attack",
     [R([["Net Sales"]], [], fl) for fl in (
         [F("Store[StoreName]", "is", ["Northstar Pop-up Harborview"])],
         [F("Store[Channel]", "is", ["Pop-up"])],
         [F("Store[Channel]", "is", ["Pop-up"]), F("Store[City]", "is", ["Harborview"])],
         [F("Store[StoreName]", "is", ["Northstar Pop-up Harborview"]), F("Store[City]", "is", ["Harborview"])])],
     "The one pop-up store (Northstar Pop-up Harborview, channel Pop-up).",
     "narrowing slip: a city with two stores; 'pop-up' dropped",
     [{"shape": "City is Harborview (store or customer), no pop-up narrowing", "grade": "HIGH", "reason": "'where City is Harborview' reads as the Harborview pop-up; it adds the full Harborview store"}])

item("va4-13", "northstar", "How much did we sell to new customers in 2025?", "attack",
     [R([[m]], [], fl) for m in ("Net Sales",) for fl in (
         [F("Customer[JoinDate]", "from", ["2025-01-01"])],
         [F("Customer[JoinDate]", "after", ["2024-12-31"])],
         [F("Customer[JoinDate]", "from", ["2025-01-01"]), F("Date[Year]", "is", [2025])],
         [F("Customer[JoinDate]", "after", ["2024-12-31"]), F("Date[Year]", "is", [2025])])],
     "Sales to customers who joined in 2025; 'new' is vague, so asking is right.",
     "narrowing by a word with no value ('new')",
     [{"shape": "Net Sales, Year is 2025 (every customer)", "grade": "HIGH", "reason": "'where Year is 2025' reads as the answer; 'new' is silently dropped"}])

item("va4-14", "northstar", "Net sales on weekdays in 2025", "attack",
     expand([["Net Sales"]], [], [[[F("Date[IsWeekend]", "is", [0])], [F("Date[DayOfWeek]", "is", ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"])]], years_ns(2025)])
     + expand([["Net Sales"]], [["Date[DayOfWeek]", "Date[DayOfWeekNumber]", "Date[IsWeekend]"]], [years_ns(2025)]),
     "Weekday sales in 2025 (IsWeekend 0, or Monday to Friday), or every day of the week side by side (wider); asking is right.",
     "narrowing whose value is not a word in the data (IsWeekend is 0 or 1)",
     [{"shape": "Net Sales, Year is 2025 only", "grade": "HIGH", "reason": "'where Year is 2025' silently drops 'weekdays' and reads as the answer"}])

item("va4-15", "northstar", "Net sales in 2025 against budget", "attack", "decline",
     "The model holds no budget or target; asking is right.",
     "future_or_stock (a target the model does not hold)",
     [{"shape": "Net Sales, Year is 2025", "grade": "MEDIUM", "reason": "the actual alone, one number where a comparison was asked; the budget is visibly absent"}])

item("va4-16", "northstar", "What was the slowest month after March 2025?", "attack",
     expand([["Net Sales", "Units", "Order Count"]], [["Date[MonthName]", "Date[MonthNumber]", "Date[YearMonth]", "Date[MonthStart]"]], [years_ns(2025)])
     + [R([["Net Sales", "Units", "Order Count"]], [["Date[YearMonth]", "Date[MonthStart]"]], [])],
     "No text bound can keep April to December; every month of 2025 side by side (no top) lets the person read it off (wider); asking is right.",
     "ranking with a bound on a month the one-query shape cannot hold",
     [{"shape": "bottom 1 (or bottom few) over every month of 2025", "grade": "HIGH", "reason": "the statement keeps the bottom 1 over all of 2025 and silently drops 'after March'; the lowest month may be January to March"},
      {"shape": "MonthName is March", "grade": "MEDIUM", "reason": "the statement names March, visibly not 'after March'"}])

item("va4-17", "northstar", "Which region had the biggest drop in net sales?", "attack",
     [R([["Net Sales"]], [REGION, ["Date[Year]"]], fl) for fl in ([], [F("Date[Year]", "is", [2024, 2025])])],
     "Net sales by region and year side by side (the person reads the drop off the rows; a wider answer); asking is right.",
     "change plus ranking; the change gap cleared by a period split",
     [{"shape": "by Region and Year, keeping the bottom 1 over the pairs, with the note to read the change off the rows", "grade": "HIGH", "reason": "one row is left, so no change can be read, and the note says it can"},
      {"shape": "by Region, bottom 1 (or no top, no year)", "grade": "MEDIUM", "reason": "the statement names the lowest Net Sales or totals, visibly not a drop"}])

item("va4-18", "northstar", "What share of units were sold online?", "attack",
     [R([["Units"]], [[c]], []) for c in ("Store[Channel]", "Store[Region]", "Store[City]", "Store[StoreName]")],
     "Units by channel (or by store) show the online row beside the rest, so the share is read off (wider); asking is right.",
     "share (the ruling's example)",
     [{"shape": "Units, online only", "grade": "MEDIUM", "reason": "a count where a share was asked; plainly not a percent"}])

item("va4-19", "northstar", "What was the average net sales per store in 2025?", "attack",
     expand([["Net Sales"]], [["Store[StoreName]", "Store[StoreCode]"]], [years_ns(2025)]),
     "Each store's 2025 net sales side by side (the average is read off; a wider answer); asking is right.",
     "average per store (the ruling's example)",
     [{"shape": "Net Sales as one total for 2025", "grade": "MEDIUM", "reason": "'as one total' plainly a total, not an average"},
      {"shape": "Average Order Value for 2025", "grade": "MEDIUM", "reason": "the statement names Average Order Value: per order, visibly not per store"}])

item("va4-20", "northstar", "How did net sales in the coast and the north change in 2025?", "attack",
     expand([["Net Sales"]], [REGION, ["Date[MonthName]", "Date[MonthNumber]", "Date[YearMonth]", "Date[MonthStart]", "Date[Quarter]"]], [[[F(REGION, "is", ["Coast", "North"])]], years_ns(2025)])
     + [R([["Net Sales"]], [REGION, ["Date[Year]"]], fl) for fl in ([F(REGION, "is", ["Coast", "North"])], [F(REGION, "is", ["Coast", "North"]), F("Date[Year]", "is", [2024, 2025])])],
     "Each region's net sales over 2025 month by month (or quarter), or 2025 beside 2024, side by side: the change is read off the rows.",
     "change gap cleared by two VALUES side by side (places, not periods): Clears('change') returns the versions sentence when ShowsValuesSideBySide",
     [{"shape": "by Region, Region is Coast or North, Year is 2025, no period split", "grade": "HIGH", "reason": "the two regions' 2025 totals, with a note to read the difference off the rows: a difference between places presented where a change was asked"}])

# ---- 10 friction probes: the built reading is right, the words tempt a strict check ----------------------------------

item("vp4-01", "northstar", "net sales broken down by region", "probe",
     [R([["Net Sales"]], [REGION], [])], "Net sales by region (either region column is fair).", "probe: 'broken down'", [], askOk=False)
item("vp4-02", "northstar", "show only the top 5 products by net sales", "probe",
     [R([["Net Sales"]], [["Product[ProductName]"]], [], {"n": 5, "by": ["Net Sales"], "lowest": False})], "The top 5 products by net sales.", "probe: 'only'", [], askOk=False)
item("vp4-03", "northstar", "how is it going this year", "probe",
     expand([["Net Sales", "Gross Profit", "Units", "Order Count", "Margin %"]], [], [years_ns(2025)])
     + expand([["Net Sales", "Gross Profit", "Units", "Order Count", "Margin %"], ["Net Sales", "Gross Profit", "Units", "Order Count", "Margin %"]], [], [years_ns(2025)]),
     "Headline numbers for 2025 (any one or two of the headline measures).", "probe: 'it', 'going' (unnamed and change-like words)", [], askOk=False)
item("vp4-04", "northstar", "what sales do we have in the coast", "probe",
     [R([[m]], [], [F(REGION, "is", ["Coast"])]) for m in ("Net Sales", "Units")], "Sales in the Coast region.", "probe: 'have' (stock-like)", [], askOk=False)
item("vp4-05", "northstar", "net sales not counting online", "probe",
     expand([["Net Sales"]], [], [ONLINE_OUT]), "Net sales without the online store.", "probe: 'not counting'", [], askOk=False)
item("vp4-06", "northstar", "how much dough did the kitchen stuff bring in last year", "probe",
     [r for y in (2024, 2025) for r in expand([["Net Sales"]], [], [[[F("Product[Category]", "is", ["Kitchen"])]], years_ns(y)])],
     "Net sales for Kitchen last year (2024, or 2025 by the data's last full year; the note says which).", "probe: slang", [], askOk=False)
item("vp4-07", "northstar", "net slaes by catagory for 2025", "probe",
     expand([["Net Sales"]], [["Product[Category]"]], [years_ns(2025)]), "Net sales by category for 2025 (two typos).", "probe: typos", [], askOk=False)
item("vp4-08", "northstar", "how are we tracking against last year", "probe",
     [R([[m]], [["Date[Year]"]], fl) for m in ("Net Sales",) for fl in ([], [F("Date[Year]", "is", [2024, 2025])], [F("Date[Year]", "is", [2025, 2024])])],
     "This year beside last year (2025 and 2024 side by side) on net sales.", "probe: change-like words ('tracking against')", [], askOk=False)
item("vp4-09", "northstar", "what's our average order value in the coast this year", "probe",
     expand([["Average Order Value"]], [], [[[F(REGION, "is", ["Coast"])]], years_ns(2025)]),
     "Average Order Value, Coast, 2025.", "probe: 'average' (the share gap, cleared by a ratio)", [], askOk=False)
item("vp4-10", "contoso-ai", "compare revenue in Germany and France for 2024", "probe",
     [R([["Total Revenue"]], [["Geography[Country]"]], [F("Geography[Country]", "is", ["Germany", "France"]), F("Calendar[Calendar Year]", "is", [2024])])],
     "Revenue for Germany beside France, 2024.", "probe: 'compare' (the change and versions gaps, cleared by values side by side)", [], askOk=False)

doc = {
    "point": "query.summarize",
    "set": "verify-r4-set-a",
    "note": ("Set A of the acceptance test in rule-jev-data-v2.md, written and labelled by the round-4 verifier on 29 Sep 2026 "
             "before any run: 20 items aimed at the check (gaps and slips not in C01 to C20 or round 2) and 10 friction probes "
             "whose right reading is plain. Asked once, live, through JevQueryReplay (bare Northstar on the full UAT fixture, data "
             "to 31 December 2025; the two Contoso copies built in JevQueryReplay.cs), with Run = false."),
    "grading": {
        "right": "a settled reading the item accepts. A wider reading the coordinator's ruling (a) allows (the right rows side by side with more, under a statement that says exactly what is shown) is also right; each such call is listed in the report.",
        "harmful": "a reading that would run (built) and that the item does not accept. Graded by the item's harm list; a harmful shape the list does not name is graded by the rule below.",
        "HIGH": "the statement and the note, read by the person who asked, pass as the answer to the question: the gap is not shown (a word that reads as theirs but means another thing, a narrowing, period or operation silently absent, or a note claiming the rows cover what they do not).",
        "MEDIUM": "the answer itself shows the mismatch plainly: another kind of number named, a different period or value stated, one total or one version where more was asked.",
        "asked": "needs you. On an attack it is right (askOk). On a probe it is a needless ask, counted by cause: the check or a guard (criterion 3), or Jev's own doubt in the parts call.",
        "refused": "a deterministic check refused it; nothing ran; not harmful.",
    },
    "items": items,
}
with open(OUT, "w") as f:
    json.dump(doc, f, indent=1)
    f.write("\n")
print(len(items), "items;", sum(1 for i in items if i["type"] == "attack"), "attacks,", sum(1 for i in items if i["type"] == "probe"), "probes")
