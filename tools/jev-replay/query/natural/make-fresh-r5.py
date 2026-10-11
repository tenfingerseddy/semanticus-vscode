#!/usr/bin/env python3
# Labels set B of the round-5 acceptance test (fresh-r5): the 60 questions a separate Claude Code session wrote from the
# models' names only (fresh-r5-writer.md), labelled by the round-5 verifier before any run. Northstar is the full UAT
# fixture, data from 1 January 2024 to 31 December 2025 (so "this year" and "last year" are both 2025, "last quarter" is
# Q4 2025 by the coordinator's ruling 2 of round 5, with Q3 2025 right too); Contoso holds Calendar Year 2023 to 2025 and no
# dates, and its Returns table reaches Product only.
#   python3 make-fresh-r5.py <writer output> <labels out>
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
CITY = ["Store[City]", "Customer[City]"]
STORE = ["Store[StoreName]", "Store[StoreCode]"]
CUST = ["Customer[CustomerName]", "Customer[CustomerCode]"]
YM = ["Date[YearMonth]", "Date[MonthStart]"]
MON = ["Date[MonthName]", "Date[MonthNumber]"]
TWO_YEARS = [[], [F("Date[Year]", "is", [2024, 2025])]]

def by_month(measures, filters=()):
    """A measure month by month: by year-month (or month start), or by year and month name/number."""
    out = []
    for f in (filters or [[]]):
        out.append(R(measures, [YM], f))
        out.append(R(measures, [["Date[Year]"], MON], f))
    return out

N = {}
def n(i, kind, accept, why, askOk, harm=None, repeat=None):
    N[i] = dict(type=kind, accept=accept, why=why, askOk=askOk, harm=harm or [], repeat=repeat)

# ---- Northstar (35) ----------------------------------------------------------------------------------------------------
n(1, "time", expand([["Net Sales"]], [], [LAST_MONTH]),
  "Net sales for December 2025 (the last month the data holds, which is complete) or November 2025; the note says which. The data ended long ago, so asking is fair.", True,
  repeat="exact: fresh-r3 nn3-001, fresh-r4 nn4-001")
n(2, "time", expand([["Gross Profit"]], [REGION], [year(2025)]), "Gross profit by the store's or the customer's region for 2025; asking which region is fair.", True,
  repeat="near: fresh-r4 nn4-002")
n(3, "best_worst",
  expand([["Net Sales"]], [STORE], [[[F("Date[Quarter]", "is", ["Q2"])]], [[F("Date[Year]", "is", [2025])], [F("Date[Year]", "is", [2024])]]], tops=(None, Top(1, "Net Sales")))
  + expand([["Net Sales"]], [STORE], [[[F("Date[Date]", "from", [f"{y}-04-01"]), F("Date[Date]", "before", [f"{y}-07-01"])] for y in (2024, 2025)]], tops=(None, Top(1, "Net Sales")))
  + expand([["Net Sales"]], [STORE, ["Date[Year]"]], [[[F("Date[Quarter]", "is", ["Q2"])]]], tops=(None, Top(1, "Net Sales"))),
  "Net sales by store for Q2 of 2025 or of 2024, or Q2 of each year side by side; highest first or the top one. No year is named, so asking is fair.", True,
  repeat="exact: fresh-r4 nn4-003")
n(4, "top_n", expand([["Units"]], [["Product[ProductName]"]], [year(2025)], tops=(Top(10, "Units"),)), "The 10 products with the most units in 2025 (the note says 'this year' is 2025).", True,
  repeat="near: fresh-r4 nn4-004")
chan = [[], [F("Store[Channel]", "is", ["Online", "Store"])], [F("Store[Channel]", "is", ["Online", "Store", "Pop-up"])]]
n(5, "compare", [R([["Margin %"]], [["Store[Channel]"]], f) for f in chan], "Margin % online beside in-store (the Store channel), by channel.", False)
n(6, "plain", [R([["Average Order Value"]], [["Customer[Segment]"]], [])], "Average order value by customer segment.", False, repeat="near: fresh-r3 nn3-007")
n(7, "compare", [R([["Net Sales"]], [["Date[Year]"], MON], f) for f in TWO_YEARS] + [R([["Net Sales"]], [YM], f) for f in TWO_YEARS],
  "Net sales month by month for each year side by side (the engine works out no change, so the year-over-year change is read off; asking is fair).", True)
n(8, "best_worst", [R([["Return Rate %"]], [["Product[Category]"]], [], t) for t in (None, Top(1, "Return Rate %"))],
  "Return rate % by category, highest (worst) first or the top one.", False,
  harm=[{"shape": "the lowest return rate first, or the bottom one", "grade": "MEDIUM", "reason": "the statement says lowest, visibly the best, not the worst"}])
n(9, "misspelling", [R([["Gross Profit"]], [["Product[Brand]"]], [])], "Gross profit by brand (one typo).", False, repeat="near: fresh2 gns-062")
n(10, "plain", [R([["Order Count"]], [["Date[DayOfWeek]", "Date[DayOfWeekNumber]"]], [])], "Orders by the day of the week.", False, repeat="near: fresh-r4 nn4-016")
n(11, "compare", [R([[m]], [["Date[IsWeekend]", "Date[DayOfWeek]", "Date[DayOfWeekNumber]"]], []) for m in ("Net Sales", "Units")],
  "Net sales (or units) for weekend days beside weekdays (by IsWeekend, or every day of the week).", False)
n(12, "top_n", expand([["Margin %"]], [STORE], [LAST_Q], tops=(Top(5, "Margin %", True),)),
  "The 5 stores with the lowest margin % in Q4 2025 (the latest quarter, stated as last quarter) or Q3 2025.", True, repeat="near: fresh-r4 nn4-013")
n(13, "plain", expand([["Order Count"]], [], [month(2025, 3, "March")]), "Orders in March 2025.", False, repeat="near: fresh-r3 nn3-008")
n(14, "two_numbers", [R([["Total Cost"], ["Net Sales"]], [["Product[Category]"]], [])], "Total cost and net sales side by side by category.", False)
opened = [[F("Store[OpenDate]", "after", ["2022-12-31"])], [F("Store[OpenDate]", "from", ["2023-01-01"])]]
n(15, "narrowed_total", expand([["Net Sales"]], [], [opened]) + expand([["Net Sales"]], [STORE], [opened]),
  "Net sales of the stores whose open date is after 2022 (Northstar Sunfield and the Harborview pop-up), as a total or store by store.", False,
  harm=[{"shape": "an open-date bound that keeps Lakeside (after 1 January 2022, or from 2022)", "grade": "MEDIUM", "reason": "the statement states the date, visibly not after 2022"},
        {"shape": "no Store[OpenDate] narrowing (a Date or Year bound, or none)", "grade": "HIGH", "reason": "'stores that opened after 2022' is silently absent; a sales-date bound reads as the stores asked about"}])
n(16, "top_n", expand([["Net Sales"]], [CUST], [year(2025)], tops=(None, Top(1, "Net Sales"), Top(3, "Net Sales"), Top(5, "Net Sales"), Top(10, "Net Sales"), Top(20, "Net Sales"))),
  "Net sales by customer for 2025, the top few or all of them highest first.", True)
n(17, "time", "decline",
  "Region by quarter over two years needs the region, the quarter and the year, and a question splits by two things at most; a quarter split with no year adds both years' quarters. Asking is right.", True,
  harm=[{"shape": "Returns Amount by Region alone, or by Region with one year picked", "grade": "MEDIUM", "reason": "the quarter or the other year visibly missing"}])
n(18, "time", by_month([["Net Sales"]], [[], [F("Date[Year]", "is", [2025])]]) + [R([["Net Sales"]], [MON], [F("Date[Year]", "is", [2025])])],
  "Net sales month by month over 2025 (the last 12 months of data), or every month to read them off (typo).", True)
n(19, "compare",
  [R([["Gross Profit"]], [["Date[Year]"]], f) for f in ([F("Date[Quarter]", "is", ["Q3"])], [F("Date[Quarter]", "is", ["Q3"]), F("Date[Year]", "is", [2025, 2024])])]
  + [R([["Gross Profit"]], [["Date[Year]"], ["Date[Quarter]"]], f) for f in TWO_YEARS],
  "Gross profit for Q3 2025 beside Q3 2024 (or every quarter of both years side by side).", True)
n(20, "plain", [R([["Margin %"]], [["Product[Subcategory]"]], [F("Product[Category]", "is", ["Electronics"])])], "Margin % by subcategory within Electronics.", False)
n(21, "top_n", [R([["Net Sales"]], [REGION, ["Product[Brand]"]], [])],
  "The model has 3 brands, so the top 3 in each region are every brand in each region: net sales by region and brand. A top per group cannot be built, so asking is fair too.", True,
  harm=[{"shape": "a top 3 over every region and brand pair together", "grade": "MEDIUM", "reason": "the statement says the top 3 is taken over the pairs together"}])
n(22, "not_in_model", "decline", "There is no West region (the regions are North, Coast, Mountain, Central, South and Online).", True,
  harm=[{"shape": "any reading without a West region", "grade": "HIGH", "reason": "'in the West region' is silently absent; the statement reads as the answer"}])
n(23, "time", [R([["Average Order Value"]], [["Date[Year]"], ["Date[Quarter]"]], [])], "Average order value for each quarter of each year.", False)
n(24, "not_in_model", "decline", "No measure counts customers, so the city with the most customers who joined since 2024 cannot be shown; asking is right.", True,
  harm=[{"shape": "Order Count or Net Sales by city (with or without a join-date bound)", "grade": "MEDIUM", "reason": "orders or money named in the statement, visibly not a count of customers"}])
n(25, "plain", [R([["Return Rate %"]], [["Product[Category]"], ["Store[Channel]"]], [])], "Return rate % by category and channel.", False)
n(26, "time", expand([["Net Sales"]], [], [year(2025)]), "Net sales for 2025: the data ends 31 December 2025, so the year to date is all of 2025 (the note says so). The data ended long ago, so asking is fair.", True)
n(27, "best_worst", [R([["Gross Profit"]], [["Product[Subcategory]"]], [])], "Gross profit by subcategory, ranked.", False)
n(28, "misspelling", expand([["Order Count"]], [MON + YM], [year(2025)]) + [R([["Order Count"]], [["Date[Year]"], MON], [F("Date[Year]", "is", [2025])])],
  "Orders month by month for 2025 (a typo).", False, repeat="near: natural nn-054")
closed = [[F("Store[StoreName]", "is", ["Northstar Pop-up Harborview"])], [F("Store[StoreCode]", "is", ["NS-POP"])]]
n(29, "narrowed_total", expand([["Net Sales"]], [["Store[CloseDate]"]], [year(2025)]) + expand([["Net Sales"]], [["Store[CloseDate]"], STORE], [year(2025)])
  + expand([["Net Sales"]], [], [closed, year(2025)]) + expand([["Net Sales"]], [STORE], [closed, year(2025)]),
  "The one closed store (the Harborview pop-up, closed 30 June 2025) and its 2025 net sales, or net sales for 2025 split by close date (the blank row is the open stores). Asking is fair.", True,
  harm=[{"shape": "every store's 2025 net sales by store, no close narrowing", "grade": "MEDIUM", "reason": "all eight stores listed, visibly not only the closed one"},
        {"shape": "Net Sales for 2025 as one total", "grade": "HIGH", "reason": "'closed stores' silently absent; the company total reads as theirs"}])
n(30, "compare", [R([["Net Sales"]], [["Customer[Segment]"], ["Date[Year]"]], f) for f in ([], [F("Date[Year]", "is", [2025, 2024])])],
  "Net sales by segment for 2025 beside 2024.", True)
n(31, "time", by_month([["Units"]]), "Units month by month, so the change is read off; the engine does not work out a month-over-month change, so asking is fair.", True,
  repeat="near: fresh-r4 nn4-030")
price = [[F("Product[ListPrice]", "after", ["100"])], [F("Product[ListPrice]", "from", ["100"])]]
n(32, "narrowed_total", expand([["Margin %"]], [["Product[ProductName]", "Product[SKU]"]], [price],
                                tops=(None,) + tuple(Top(k, "Margin %", True) for k in (1, 3, 5, 10))),
  "Margin % of the products whose list price is above 100, lowest first or the bottom few. 'Low' is vague, so asking is fair.", True,
  harm=[{"shape": "no list-price narrowing", "grade": "HIGH", "reason": "'list price above 100' silently absent; the list reads as the products asked about"}])
n(33, "top_n", [r for m in ("Units", "Net Sales") for g in (["Product[SKU]"], ["Product[ProductName]"]) for r in expand([[m]], [g], [LAST_WEEK], tops=(Top(1, m), None))],
  "The SKU (or product) that sold the most in the last full week the data holds (22 to 28 December 2025) or its last days. The data ended long ago, so asking is fair.", True)
n(34, "misspelling", [R([["Average Order Value"]], [CITY], [])], "Average order value by the store's or the customer's city (a typo); asking which city is fair.", True)
n(35, "share", expand([["Net Sales"]], [["Product[Category]"]], [THIS_Q]),
  "Net sales by category for Q4 2025 side by side is the wider answer the share is read from; the engine works out no share, so asking is fair.", True,
  harm=[{"shape": "Net Sales as one total (or for one category)", "grade": "MEDIUM", "reason": "an amount where a share was asked; plainly not a percent"}])

# ---- Contoso (25, asked of both copies) -----------------------------------------------------------------------------------
C = {}
def c(i, kind, accept, why, askOk, harm=None, repeat=None):
    C[i] = dict(type=kind, accept=accept, why=why, askOk=askOk, harm=harm or [], repeat=repeat)
CY = "Calendar[Calendar Year]"
c(1, "plain", [R([["Total Revenue"]], [], [F(CY, "is", [2024])])], "Revenue for 2024.", False, repeat="near: fresh3 hct-049")
c(2, "plain", [R([["Total Revenue"]], [["Geography[Country]"]], [])], "Revenue by country.", False, repeat="exact: fresh-r4 ncr4-002")
c(3, "top_n", [R([["Order Quantity"]], [["Product[Product Name]"]], [], t) for t in (Top(10, "Order Quantity"), None)],
  "The 10 products with the most units (the model has 4, so all of them).", False, repeat="near: fresh fct-03")
c(4, "best_worst", [R([["Total Revenue"]], [["Geography[State]"]], [], t) for t in (None, Top(1, "Total Revenue"))], "Revenue by state, highest first or the top one.", False)
c(5, "plain", [R([["Revenue per Unit"]], [["Product[Category]"]], [])], "Revenue per unit by category.", False)
c(6, "plain", [R([["Returned Units"]], [["Product[Brand]"]], [])], "Returned units by brand.", False)
c(7, "plain", [R([["Total Revenue"]], [["Calendar[Fiscal Quarter]"]], []), R([["Total Revenue"]], [[CY], ["Calendar[Fiscal Quarter]"]], [])],
  "Revenue by fiscal quarter (or by year and fiscal quarter).", False, repeat="near: fresh-r4 ncr4-012")
c(8, "misspelling", [R([["Total Revenue"]], [["Product[Color]"]], [])], "Revenue by colour (two typos).", False, repeat="near: fresh-r3 ncr3-009")
c(9, "best_worst", [R([["Total Revenue"]], [["Promotion[Promotion Name]"]], [], t) for t in (None, Top(1, "Total Revenue"))], "Revenue by promotion, most first or the top one.", False,
  repeat="exact: fresh-r3 ncr3-008")
c(10, "compare", [R([["Total Revenue"]], [[CY]], f) for f in ([], [F(CY, "is", [2024, 2025])])],
  "Revenue for this year beside last year: the model has no dates to tell which year is this one, so every year side by side (the note says so), or 2025 beside 2024.", True)
c(11, "top_n", [R([["Total Revenue"]], [["Geography[City]"]], [], Top(5, "Total Revenue", True))], "The 5 cities with the least revenue.", False, repeat="near: fresh-r4 ncr4-011")
c(12, "not_in_model", "decline", "Returns reach Product only, so returned units cannot be split by month; asking or a refusal is right.", True,
  harm=[{"shape": "Returned Units by Category (no month)", "grade": "MEDIUM", "reason": "the month split visibly missing"}])
c(13, "plain", [R([["Revenue per Unit"]], [["Product[Brand]"]], [])], "Revenue per unit by brand.", False, repeat="near: fresh-r3 ncr3-007")
c(14, "time", [R([["Order Quantity"]], [["Calendar[Month Name]"]], [F(CY, "is", [2025])])], "Order quantity by month for 2025.", False)
c(15, "compare", [R([["Total Revenue"]], [["Geography[Country]"], [CY]], [])],
  "Revenue by country for each year side by side, so the drop is read off; the engine works out no change, so asking is fair.", True,
  harm=[{"shape": "a top or bottom 1 over the country and year pairs, under the note to read the years off", "grade": "HIGH", "reason": "one row is left, so no drop can be read, and the note says it can"},
        {"shape": "revenue by country with the lowest first or the bottom one (no year)", "grade": "MEDIUM", "reason": "the lowest revenue named, visibly not a drop"}])
c(16, "top_n", [R([["Total Revenue"]], [["Product[Category]"], ["Product[Product Name]"]], [])],
  "Every product in every category (4 products, so the top 3 per category are read off); a top per group cannot be built, so asking is fair.", True,
  harm=[{"shape": "a top 3 over every category and product pair together", "grade": "MEDIUM", "reason": "the statement says the top 3 is taken over the pairs together"}])
c(17, "misspelling", [R([["Order Quantity"]], [["Promotion[Promotion Name]"]], [])], "Order quantity by promotion (a typo).", False)
c(18, "share", [R([["Total Revenue"]], [["Geography[Country]"], [CY]], [F("Calendar[Fiscal Quarter]", "is", ["FY Q2"])])],
  "Revenue by country in FY Q2 of each year side by side is the wider answer the share is read from; no year is named and the engine works out no share, so asking is fair.", True,
  harm=[{"shape": "revenue by country with no fiscal-quarter narrowing", "grade": "HIGH", "reason": "'fiscal Q2' silently absent; the table reads as FY Q2's"},
        {"shape": "one total, or one country", "grade": "MEDIUM", "reason": "an amount where a share by country was asked"}])
c(19, "best_worst", [R([["Order Quantity"]], [["Product[Color]"]], [], t) for t in (None, Top(1, "Order Quantity"), Top(2, "Order Quantity"), Top(3, "Order Quantity"))],
  "Units by colour, most first or the top few.", False, repeat="near: fresh-r3 ncr3-013")
c(20, "two_numbers", [R([["Returned Units"], ["Order Quantity"]], [["Product[Product Name]"]], [])], "Returned units and order quantity side by side by product.", False,
  repeat="near: fresh-r4 ncr4-018")
c(21, "time", [R([["Total Revenue"]], [[CY], ["Calendar[Month Name]"]], [])]
  + [R([["Total Revenue"]], [["Calendar[Month Name]"]], [F(CY, "is", [y])], t) for y in (2024, 2025) for t in (None, Top(1, "Total Revenue"))],
  "The best month of last year: the model has no dates to tell which year is last, so every year's months side by side, or 2024's (or 2025's) months, most first or the top one. Asking is fair.", True,
  harm=[{"shape": "the top month over every year and month pair together", "grade": "MEDIUM", "reason": "the statement says the top is taken over every year and month pair, visibly not last year's"}])
c(22, "best_worst", [R([["Revenue per Unit"]], [["Geography[State]"]], [])], "Revenue per unit by state, ranked.", False)
c(23, "narrowed_total", [R([["Total Revenue"]], [], [F("Promotion[Promotion Name]", "is", ["No Discount"])])],
  "Revenue sold under no offer (the No Discount promotion). The value is not the words, so asking is fair.", True,
  harm=[{"shape": "Total Revenue with no promotion narrowing", "grade": "HIGH", "reason": "'with no promotion' silently absent; the total reads as the answer"},
        {"shape": "Promotion Name is not No Discount", "grade": "MEDIUM", "reason": "the statement states the opposite narrowing, visibly"}])
c(24, "not_in_model", "decline", "The data holds no California (two typos).", True, repeat="near: fresh-r3 ncr3-024",
  harm=[{"shape": "any reading without California", "grade": "HIGH", "reason": "the place is silently absent; the statement reads as the answer"}])
c(25, "not_in_model", "decline", "Returns reach Product only, so 'this year' cannot be applied to returned units; asking is right.", True,
  harm=[{"shape": "Returned Units by Brand with no year", "grade": "HIGH", "reason": "'this year' silently absent; reads as this year's returns"}])

items = []
ni = ci = 0
for model, words in qs:
    if model == "northstar":
        ni += 1
        L = N[ni]
        items.append(dict(id=f"nn5-{ni:03d}", model="northstar", words=words, **{k: L[k] for k in ("type", "accept", "askOk", "why", "harm")},
                          expect=L["accept"][0] if L["accept"] != "decline" else "decline", **({"repeat": L["repeat"]} if L["repeat"] else {})))
    else:
        ci += 1
        L = C[ci]
        for tag, m in (("ncr5", "contoso"), ("nca5", "contoso-ai")):
            items.append(dict(id=f"{tag}-{ci:03d}", model=m, words=words, pair=f"nc5-{ci:03d}", **{k: L[k] for k in ("type", "accept", "askOk", "why", "harm")},
                              expect=L["accept"][0] if L["accept"] != "decline" else "decline", **({"repeat": L["repeat"]} if L["repeat"] else {})))
assert ni == 35 and ci == 25 and len(items) == 85

doc = {
    "point": "query.summarize",
    "set": "fresh-r5",
    "note": ("Set B of the round-5 acceptance test (rule-jev-data-v2.md): 60 everyday questions written on 30 Sep 2026 by a separate "
             "Claude Code session given only the two models' table, column and measure names (fresh-r5-writer.md, the fresh-r3 input word "
             "for word, the fresh-r4 method), 35 on Northstar and 25 on Contoso asked of both copies: 85 items. Labelled by the round-5 "
             "verifier before any run, for the full UAT fixture (Northstar data 1 January 2024 to 31 December 2025). Run once, live, "
             "through JevQueryReplay. Questions that repeat an earlier set word for word or nearly are kept and flagged ('repeat')."),
    "grading": "As set A (verify-r5/set-a-labels.json 'grading'): right, harmful (HIGH or MEDIUM by the item's harm list, else by the rule), asked (fair when askOk, else needless, by cause), refused.",
    "items": items,
}
with open(OUT, "w", encoding="utf-8") as f:
    json.dump(doc, f, indent=1)
    f.write("\n")
print(len(items), "items;", sum(1 for i in items if i.get("repeat", "").startswith("exact")), "exact repeats;",
      sum(1 for i in items if i.get("repeat", "").startswith("near")), "near;",
      sum(1 for i in items if i["askOk"]), "askOk;", sum(1 for i in items if i["accept"] == "decline"), "decline")
