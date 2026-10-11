#!/usr/bin/env python3
# Labels set B of the round-4 acceptance test (fresh-r4): the 60 questions a separate Claude Code session wrote from the
# models' names only (fresh-r4-writer.md), labelled by the verifier before any run. Northstar is the full UAT fixture, data
# from 1 January 2024 to 31 December 2025; Contoso holds Calendar Year 2023 to 2025 and no dates.
#   python3 make-fresh-r4.py <writer output> <labels out>
import itertools, json, sys

SRC, OUT = sys.argv[1], sys.argv[2]
lines = [l.strip() for l in open(SRC, encoding="utf-8") if l.strip()]
qs = [(l.split("|", 1)[0].strip(), l.split("|", 1)[1].strip()) for l in lines]
assert len(qs) == 60 and sum(1 for m, _ in qs if m == "northstar") == 35, (len(qs))

def R(measures, groupBy, filters, top=None):
    return {"measures": measures, "groupBy": groupBy, "filters": filters, "top": top}

def F(cols, op, values):
    return {"column": cols if isinstance(cols, list) else [cols], "op": op, "values": [str(v) for v in values]}

def Top(n, by, lowest=False):
    return {"n": n, "by": by if isinstance(by, list) else [by], "lowest": lowest}

def expand(measures, groupBy, filter_groups, tops=(None,)):
    out = []
    for combo in itertools.product(*filter_groups) if filter_groups else [()]:
        for t in tops:
            out.append(R(measures, groupBy, [f for part in combo for f in part], t))
    return out

def year(y):
    return [[F("Date[Year]", "is", [y])], [F("Date[Date]", "from", [f"{y}-01-01"]), F("Date[Date]", "before", [f"{y + 1}-01-01"])]]

def month(y, m, name):
    nxt = f"{y + (m == 12)}-{(m % 12) + 1:02d}-01"
    return [[F("Date[YearMonth]", "is", [f"{y}-{m:02d}"])], [F("Date[Year]", "is", [y]), F("Date[MonthName]", "is", [name])],
            [F("Date[Date]", "from", [f"{y}-{m:02d}-01"]), F("Date[Date]", "before", [nxt])]]

def quarter(y, q):
    m0 = 3 * (q - 1) + 1
    nxt = f"{y + (q == 4)}-{(m0 + 3 - 1) % 12 + 1:02d}-01"
    return [[F("Date[Year]", "is", [y]), F("Date[Quarter]", "is", [f"Q{q}"])],
            [F("Date[Date]", "from", [f"{y}-{m0:02d}-01"]), F("Date[Date]", "before", [nxt])],
            [F("Date[YearMonth]", "is", [f"{y}-{m:02d}" for m in range(m0, m0 + 3)])]]

LAST_MONTH = month(2025, 12, "December") + month(2025, 11, "November")
THIS_Q = quarter(2025, 4)
LAST_Q = quarter(2025, 4) + quarter(2025, 3)
LAST_WEEK = [[F("Date[Date]", "from", ["2025-12-22"]), F("Date[Date]", "before", ["2025-12-29"])],
             [F("Date[Date]", "from", ["2025-12-29"]), F("Date[Date]", "before", ["2026-01-01"])],
             [F("Date[Date]", "from", ["2025-12-29"]), F("Date[Date]", "before", ["2026-01-05"])]]
REGION = ["Store[Region]", "Customer[Region]"]
STORE = ["Store[StoreName]", "Store[StoreCode]"]
YM = ["Date[YearMonth]", "Date[MonthStart]"]
MON = ["Date[MonthName]", "Date[MonthNumber]"]
TWO_YEARS = [[], [F("Date[Year]", "is", [2024, 2025])]]

def by_month(measures, extra_filters_groups=(), with_year_filters=(None,)):
    """A measure month by month: by year-month (or month start), or by year and month name/number."""
    out = []
    for yf in with_year_filters:
        yfs = [[]] if yf is None else yf
        for f in yfs:
            for ex in (itertools.product(*extra_filters_groups) if extra_filters_groups else [()]):
                flt = f + [x for part in ex for x in part]
                out.append(R(measures, [YM], flt))
                out.append(R(measures, [["Date[Year]"], MON], flt))
    return out

N = {}
def n(i, kind, accept, why, askOk, harm=None, repeat=None):
    N[i] = dict(type=kind, accept=accept, why=why, askOk=askOk, harm=harm or [], repeat=repeat)

# ---- Northstar (35) ----------------------------------------------------------------------------------------------------
n(1, "time", expand([["Net Sales"]], [], [LAST_MONTH]),
  "Net sales for December 2025 (the last month the data holds, which is complete) or November 2025 (the month before it); the note says which. The data ended long ago, so asking is fair.", True,
  repeat="exact: fresh-r3 nn3-001")
n(2, "plain", expand([["Gross Profit"]], [REGION], [year(2025)]), "Gross profit by the store's or the customer's region for 2025; asking which region is fair.", True)
n(3, "best_worst",
  expand([["Net Sales"]], [STORE], [[[F("Date[Quarter]", "is", ["Q2"])]], [[F("Date[Year]", "is", [2025])], [F("Date[Year]", "is", [2024])]]], tops=(None, Top(1, "Net Sales")))
  + expand([["Net Sales"]], [STORE], [[[F("Date[Date]", "from", [f"{y}-04-01"]), F("Date[Date]", "before", [f"{y}-07-01"])] for y in (2024, 2025)]], tops=(None, Top(1, "Net Sales")))
  + expand([["Net Sales"]], [STORE, ["Date[Year]"]], [[[F("Date[Quarter]", "is", ["Q2"])]]], tops=(None, Top(1, "Net Sales"))),
  "Net sales by store for Q2 of 2025 or of 2024, or Q2 of each year side by side; highest first or the top one. No year is named, so asking is fair.", True)
n(4, "top_n", [R([["Units"]], [["Product[ProductName]"]], [], Top(10, "Units"))], "The 10 products with the most units.", False)
n(5, "compare", [R([["Net Sales"]], [MON, ["Date[Year]"]], f) for f in TWO_YEARS] + [R([["Net Sales"]], [YM], f) for f in TWO_YEARS],
  "Net sales month by month for 2025 beside 2024 (every month of both years, the whole data).", False)
n(6, "plain", [R([["Margin %"]], [["Product[Category]"]], [])], "Margin % by category.", False)
n(7, "time", expand([["Order Count"]], [], [LAST_WEEK]),
  "Orders in the last full week the data holds (22 to 28 December 2025) or in its last days (from 29 December). The data ended long ago, so asking is fair.", True)
n(8, "plain", [R([["Average Order Value"]], [["Store[Channel]"]], [])], "Average order value by channel.", False, repeat="exact: fresh2 gns-007")
n(9, "best_worst", [R([["Return Rate %"]], [["Customer[Segment]"]], [], t) for t in (None, Top(1, "Return Rate %"))],
  "Return rate % by customer segment, highest first or the top one.", False)
n(10, "plain", expand([["Returns Amount"]], [["Product[Brand]"]], [year(2025)]), "Returns amount by brand for 2025.", False)
n(11, "misspelling", [R([["Net Sales"]], [["Store[City]"]], [])], "Net sales by the store's city (one typo).", False, repeat="near: fresh2 gns-042")
n(12, "time", [R([["Gross Profit"]], [YM], f) for f in ([], ) ] + expand([["Gross Profit"]], [YM], [year(2025)])
  + [R([["Gross Profit"]], [["Date[Year]"], MON], f) for f in ([], [F("Date[Year]", "is", [2025])])],
  "Gross profit month by month over 2025 (the last 12 months of data), or every month to read them off.", True)
n(13, "top_n", [R([["Margin %"]], [STORE], [], Top(5, "Margin %", True))], "The 5 stores with the lowest margin %.", False)
n(14, "compare", [R([["Net Sales"]], [["Date[IsWeekend]", "Date[DayOfWeek]", "Date[DayOfWeekNumber]"]], [])],
  "Net sales for weekend days beside weekdays (by IsWeekend, or every day of the week).", False)
n(15, "compare", [R([[m]], [["Product[Subcategory]"], ["Date[Year]"]], f) for m in ("Net Sales", "Units", "Gross Profit") for f in TWO_YEARS],
  "Each subcategory's sales for each year side by side, so growth is read off; the model has no growth measure, so asking is fair.", True, repeat="near: fresh-r3 nn3-023")
n(16, "plain", [R([["Order Count"]], [["Date[DayOfWeek]", "Date[DayOfWeekNumber]"]], [])], "Orders by day of the week.", False)
n(17, "top_n", [R([["Net Sales"]], [["Product[Category]"], ["Product[Brand]"]], [])],
  "The model has 3 brands, so the top 3 brands in each category are every brand in each category: net sales by category and brand. A top per group cannot be built, so asking is fair too.", True,
  harm=[{"shape": "a top 3 over every category and brand pair together", "grade": "MEDIUM", "reason": "the statement says the top 3 is taken over the pairs together, visibly not per category"}])
n(18, "two_numbers", [R([["Total Cost"], ["Net Sales"]], [["Date[Year]"], ["Date[Quarter]"]], [])],
  "Total cost and net sales side by side for each quarter of each year.", False, repeat="exact: fresh-r3 nn3-011")
open24 = [[F("Store[OpenDate]", "from", ["2024-01-01"]), F("Store[OpenDate]", "before", ["2025-01-01"])],
          [F("Store[OpenDate]", "after", ["2023-12-31"]), F("Store[OpenDate]", "before", ["2025-01-01"])],
          [F("Store[OpenDate]", "from", ["2024-01-01"]), F("Store[OpenDate]", "up_to", ["2024-12-31"])]]
n(19, "narrowed_total", expand([["Net Sales", "Gross Profit", "Units"]], [], [open24, year(2025)]) + expand([["Net Sales", "Gross Profit", "Units"]], [STORE], [open24, year(2025)])
  + expand([["Net Sales", "Gross Profit", "Units"]], [], [[[F("Store[StoreName]", "is", ["Northstar Sunfield"])]], year(2025)]),
  "Sales in 2025 of the stores whose open date falls in 2024 (only Northstar Sunfield), as a total or store by store. Not the stores' 2024 sales.", True,
  harm=[{"shape": "OpenDate 2024 or later (keeps the 2025 pop-up)", "grade": "MEDIUM", "reason": "the statement states '2024 or later'"},
        {"shape": "the store narrowing dropped (every store in 2025)", "grade": "HIGH", "reason": "'where Year is 2025' reads as the answer; 'stores opened in 2024' silently absent"}])
n(20, "time", expand([["Return Rate %"]], [["Product[Category]"]], [LAST_Q]), "Return rate % by category for Q4 2025 (or Q3 2025); the note says which. Asking is fair.", True)
n(21, "top_n", [R([["Net Sales"]], [["Customer[CustomerName]", "Customer[CustomerCode]"]], [], Top(20, "Net Sales"))], "The 20 customers with the most net sales.", False)
join25 = [[F("Customer[JoinDate]", "from", ["2025-01-01"])], [F("Customer[JoinDate]", "after", ["2024-12-31"])]]
n(22, "misspelling", expand([["Average Order Value"]], [], [join25]) + expand([["Average Order Value"]], [], [join25, year(2025)]),
  "Average order value of customers whose join date is in 2025 (two typos). Not every customer's 2025 orders.", True,
  harm=[{"shape": "Average Order Value with Year 2025 and no join-date narrowing", "grade": "HIGH", "reason": "'where Year is 2025' reads as 'this year'; 'new customers who joined' silently absent"}])
n(23, "narrowed_total", [R([["Net Sales"]], g, []) for g in ([["Store[CloseDate]"]], [["Store[CloseDate]"], STORE])]
  + [R([["Net Sales"]], [], [F("Store[StoreName]", "is", ["Northstar Pop-up Harborview"])])],
  "Net sales of the stores with a close date (only the Harborview pop-up), or split by close date (the blank row is the open stores); asking is fair.", True,
  harm=[{"shape": "every store's net sales, no close narrowing", "grade": "HIGH", "reason": "'as one total' with no narrowing reads as the closed stores' number"}])
n(24, "best_worst", expand([["Margin %", "Gross Profit"]], [REGION], [year(2025)], tops=(None, Top(1, ["Margin %", "Gross Profit"], True))),
  "Margin % (or gross profit) by region for 2025, lowest first or the bottom one; asking which region column is fair.", True)
n(25, "plain", by_month([["Units"]], [[[F("Product[Category]", "is", ["Electronics"])]]]), "Units month by month for Electronics.", False)
chan = [[], [F("Store[Channel]", "is", ["Online", "Store"])], [F("Store[Channel]", "is", ["Online", "Store", "Pop-up"])]]
n(26, "compare", [R([["Gross Profit"]], [["Store[Channel]"]], f) for f in chan], "Gross profit online beside in-store (the Store channel), by channel.", False)
n(27, "plain", [R([["Legacy Discount %"]], [STORE], [])], "Legacy discount % by store.", False)
n(28, "time", [R([["Net Sales"]], [["Date[Year]"]], f) for f in TWO_YEARS],
  "The data runs to 31 December 2025, so 2025 to date is all of 2025 and the same period last year is all of 2024: net sales by year, side by side. The engine does not read 'same period last year' yet, so asking is fair.", True,
  repeat="exact: fresh-r3 nn3-028 (labelled decline there, for data ending 11 October)",
  harm=[{"shape": "one year only", "grade": "MEDIUM", "reason": "one period shown where two were asked, visibly"}])
n(29, "best_worst", [R([["Returns Amount"]], [["Product[ProductName]"]], [], t) for t in (None, Top(1, "Returns Amount"), Top(3, "Returns Amount"), Top(5, "Returns Amount"), Top(10, "Returns Amount"))],
  "Returns amount by product, most first or the top few; whether 'returns' means money or units is fair to ask.", True)
n(30, "time", by_month([["Order Count"]]), "Orders month by month, so the change is read off; the engine does not work out a month-over-month change, so asking is fair.", True)
n(31, "plain", [R([["Margin %"]], [["Customer[Region]"], ["Customer[Segment]"]], [])], "Margin % by the customer's region and segment.", False)
n(32, "plain", [R([["Net Sales"]], [["Date[YearMonth]"]], [F(REGION, "is", ["North"])])], "Net sales by year-month for the North region (store or customer region: fair to ask).", True)
n(33, "misspelling", [R([["Return Rate %"]], [["Product[Brand]"]], [], t) for t in (None, Top(1, "Return Rate %", True))],
  "Return rate % by brand; the best is the lowest (two typos).", False,
  harm=[{"shape": "the top 1 by Return Rate % (the highest)", "grade": "MEDIUM", "reason": "the statement says the top 1 by Return Rate %, the highest rate, where the best is the lowest"}])
n(34, "time", expand([["Gross Profit"]], [["Product[Category]"]], [THIS_Q]), "Gross profit by category for Q4 2025, ranked; the data ended long ago, so asking is fair.", True)
n(35, "not_in_model", "decline", "There is no store 1042 (store codes are NS-...).", True,
  harm=[{"shape": "any reading without that store", "grade": "HIGH", "reason": "the store is silently absent; the statement reads as the answer"}])

# ---- Contoso (25, asked of both copies) -----------------------------------------------------------------------------------
C = {}
def c(i, kind, accept, why, askOk, harm=None, repeat=None):
    C[i] = dict(type=kind, accept=accept, why=why, askOk=askOk, harm=harm or [], repeat=repeat)
CY = "Calendar[Calendar Year]"
c(1, "time", [R([["Total Revenue"]], [], [F(CY, "is", [y])]) for y in (2024, 2025)] + [R([["Total Revenue"]], [[CY]], [])],
  "Revenue for last year: 2024 or 2025 (the model has no dates to tell which year is complete), or by year to read it off. Asking is fair.", True,
  repeat="exact: fresh-r3 ncr3-001")
c(2, "plain", [R([["Total Revenue"]], [["Geography[Country]"]], [])], "Revenue by country.", False, repeat="near: natural ncr-019")
c(3, "top_n", [R([["Total Revenue"]], [["Product[Product Name]"]], [], Top(10, "Total Revenue"))], "The 10 products with the most revenue.", False, repeat="exact: fresh-r3 ncr3-002")
c(4, "best_worst", [R([["Order Quantity"]], [["Geography[State]"]], [], t) for t in (None, Top(1, "Order Quantity"))], "Order quantity by state, highest first or the top one.", False)
c(5, "compare", [R([["Total Revenue"]], [[CY]], f) for f in ([], [F(CY, "is", [2024, 2025])])], "Revenue for 2024 beside 2025.", False)
c(6, "misspelling", [R([["Total Revenue"]], [["Product[Color]"]], [])], "Revenue by colour (one typo).", False)
c(7, "plain", [R([["Returned Units"]], [["Product[Category]"]], [])], "Returned units by category.", False)
c(8, "plain", [R([["Revenue per Unit"]], [["Product[Brand]"]], [])], "Revenue per unit by brand.", False, repeat="exact: fresh-r3 ncr3-007")
c(9, "best_worst", [R([["Total Revenue"]], [["Promotion[Promotion Name]"]], [], t) for t in (None, Top(1, "Total Revenue"))], "Revenue by promotion, most first or the top one.", False,
  repeat="exact: fresh-r3 ncr3-008")
c(10, "time", [R([["Total Revenue"]], [["Calendar[Month Name]"]], [F(CY, "is", [2025])]), R([["Total Revenue"]], [[CY], ["Calendar[Month Name]"]], [])],
  "Revenue month by month for 2025 (the latest year the model holds), or every year's months side by side. The model has no dates, so asking which year is fair.", True)
c(11, "top_n", [R([["Total Revenue"]], [["Geography[City]"]], [], Top(5, "Total Revenue", True))], "The 5 cities with the least revenue.", False)
c(12, "plain", [R([["Total Revenue"]], [["Calendar[Fiscal Quarter]"]], []), R([["Total Revenue"]], [[CY], ["Calendar[Fiscal Quarter]"]], [])],
  "Revenue by fiscal quarter (or by year and fiscal quarter).", False, repeat="near: fresh-r3 ncr3-011")
c(13, "best_worst", [R([["Returned Units"]], [["Product[Brand]"]], [], t) for t in (None, Top(1, "Returned Units"))], "Returned units by brand, most first or the top one.", False)
c(14, "plain", [R([["Order Quantity"]], [["Calendar[Month Name]"]], [F(CY, "is", [2025])])], "Order quantity by month name for 2025.", False, repeat="near: fresh-r3 ncr3-006")
c(15, "misspelling", [R([[m]], [["Product[Category]"]], [F("Geography[Country]", "is", ["Germany"])], t) for m in ("Total Revenue", "Order Quantity") for t in (None, Top(1, m))],
  "The category that sells the most in Germany, by revenue or units (one typo).", False)
c(16, "plain", [R([["Revenue per Unit"]], [["Product[Color]"]], [])], "Revenue per unit by colour.", False)
c(17, "share", [R([["Total Revenue"]], [["Promotion[Promotion Name]"]], [])],
  "Revenue by promotion side by side is the wider answer the share is read from; the engine works out no share, so asking is fair.", True,
  harm=[{"shape": "Total Revenue with the promotions only (Promotion Name is not No Discount)", "grade": "MEDIUM", "reason": "an amount where a share was asked; plainly not a percent"}])
c(18, "two_numbers", [R([["Returned Units"], ["Order Quantity"]], [["Product[Product Name]"]], [])], "Returned units and order quantity side by side by product.", False)
c(19, "time", [R([["Total Revenue"]], [["Geography[Country]"]], [F(CY, "is", [2025])]), R([["Total Revenue"]], [["Geography[Country]"], [CY]], [])],
  "Revenue by country for 2025 (the latest year the model holds), or by country and year. The model has no dates, so asking which year is fair.", True)
c(20, "narrowed_total", [R([["Total Revenue"]], [], [F("Product[Brand]", "is", ["Contoso"]), F("Calendar[Fiscal Quarter]", "is", ["FY Q3"])]),
                         R([["Total Revenue"]], [[CY]], [F("Product[Brand]", "is", ["Contoso"]), F("Calendar[Fiscal Quarter]", "is", ["FY Q3"])])],
  "Revenue for the Contoso brand in FY Q3 (every year's FY Q3, or year by year). No year is named, so asking is fair.", True)
c(21, "not_in_model", "decline", "The data holds no California.", True,
  harm=[{"shape": "any reading without California", "grade": "HIGH", "reason": "the place is silently absent; the statement reads as the answer"}])
c(22, "not_in_model", "decline", "The model holds no dates, and returns are counted by product only (no calendar reaches them), so 'last month' cannot be applied. Two typos.", True,
  harm=[{"shape": "Returned Units with no month narrowing", "grade": "HIGH", "reason": "'last month' silently absent; reads as last month's returns"}])
c(23, "compare", [R([["Total Revenue"]], [["Product[Category]"], [CY]], [])], "Revenue by category for each year side by side, so growth is read off; asking is fair.", True)
c(24, "top_n", [R([["Order Quantity"]], [["Geography[Country]"], ["Product[Product Name]"]], [])],
  "Every product in every country (4 products, so the top 3 per country are read off); a top per group cannot be built, so asking is fair.", True,
  harm=[{"shape": "a top 3 over every country and product pair together", "grade": "MEDIUM", "reason": "the statement says the top 3 is taken over the pairs together"}])
c(25, "share", [R([["Returned Units"], ["Order Quantity"]], [["Product[Color]"]], [])],
  "Returned units beside order quantity by colour (the ratio is read off); the model has no return ratio, so asking is fair.", True,
  harm=[{"shape": "Returned Units (or Revenue per Unit) by colour, top 1", "grade": "MEDIUM", "reason": "a count or another ratio named in the statement where a return ratio was asked"}])

items = []
ni = ci = 0
for model, words in qs:
    if model == "northstar":
        ni += 1
        L = N[ni]
        items.append(dict(id=f"nn4-{ni:03d}", model="northstar", words=words, **{k: L[k] for k in ("type", "accept", "askOk", "why", "harm")},
                          expect=L["accept"][0] if L["accept"] != "decline" else "decline", **({"repeat": L["repeat"]} if L["repeat"] else {})))
    else:
        ci += 1
        L = C[ci]
        for tag, m in (("ncr4", "contoso"), ("nca4", "contoso-ai")):
            items.append(dict(id=f"{tag}-{ci:03d}", model=m, words=words, pair=f"nc4-{ci:03d}", **{k: L[k] for k in ("type", "accept", "askOk", "why", "harm")},
                              expect=L["accept"][0] if L["accept"] != "decline" else "decline", **({"repeat": L["repeat"]} if L["repeat"] else {})))
assert ni == 35 and ci == 25 and len(items) == 85

doc = {
    "point": "query.summarize",
    "set": "fresh-r4",
    "note": ("Set B of the round-4 acceptance test (rule-jev-data-v2.md): 60 everyday questions written on 29 Sep 2026 by a separate "
             "Claude Code session given only the two models' table, column and measure names (fresh-r4-writer.md, the fresh-r3 input word "
             "for word), 35 on Northstar and 25 on Contoso asked of both copies: 85 items. Labelled by the round-4 verifier before any "
             "run, for the full UAT fixture (Northstar data 1 January 2024 to 31 December 2025). Run once, live, through JevQueryReplay. "
             "Questions that repeat an earlier set word for word are kept and flagged ('repeat'), the fresh3 way."),
    "grading": "As set A (verify-r4/set-a-labels.json 'grading'): right, harmful (HIGH or MEDIUM by the item's harm list, else by the rule), asked (fair when askOk, else needless, by cause), refused.",
    "items": items,
}
with open(OUT, "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=1)
    f.write("\n")
print(len(items), "items;", sum(1 for i in items if i.get("repeat", "").startswith("exact")), "exact repeats;",
      sum(1 for i in items if i["askOk"]), "askOk;", sum(1 for i in items if i["accept"] == "decline"), "decline")
