# fresh-r5: how the questions were written

Set B of the round-5 acceptance test (rule-jev-data-v2.md). Written on 30 Sep 2026 (2026-09-29T16:29:10Z to 2026-09-29T16:29:24Z) by a separate
Claude Code session (claude -p, 2.1.284, model claude-opus-5-5, one turn), started by the verifier in an empty folder
(~/.cache/vjq5/writer-empty) with no tools at all (--tools "" --strict-mcp-config), so it could read no code, no rules, no
values and no report. Its whole input is below, word for word the fresh-r3 and fresh-r4 input (the fresh-r4 method), and its
whole output follows it, unedited. The verifier then labelled each question (fresh-r5-labels.json, written by
make-fresh-r5.py) before any run. The Contoso questions are asked of both Contoso copies (raw and AI-ready): 35 + 25 x 2 = 85
items.

Four questions repeat an earlier set word for word (6 items; each carries a "repeat" field starting "exact"), and 17 are near
repeats (27 items, "near"). They are kept, as fresh-r4 kept its repeats, and the report scores the set with and without the
exact repeats.

## Input

```
Two semantic models. These are their table, column and measure names, nothing else.

Model "northstar"
Tables: Sales, Store, Customer, Product, Date, Metric Selector, Time Intelligence
Measures: Net Sales, Total Cost, Gross Profit, Margin %, Units, Order Count, Average Order Value, Returns Amount, Return Rate %, Legacy Discount %
Columns:
Store: StoreName, StoreCode, City, Region, Channel, OpenDate, CloseDate
Customer: CustomerCode, CustomerName, Segment, City, Region, JoinDate
Product: SKU, ProductName, Subcategory, Category, CategorySortOrder, Brand, ListPrice, UnitCost
Date: Date, Year, Quarter, MonthNumber, MonthName, YearMonth, MonthStart, DayOfWeek, DayOfWeekNumber, IsWeekend
Sales: OrderNumber, OrderLine, Quantity, UnitPrice, DiscountAmount, NetSalesAmount, CostAmount, IsReturn, AuditNote
Metric Selector: Metric Selector
Time Intelligence: Time Calculation

Model "contoso"
Tables: Sales, Geography, Product, Calendar, Promotion, Returns
Measures: Total Revenue, Order Quantity, Revenue per Unit, Returned Units
Columns:
Sales: Revenue, Quantity
Geography: Country, State, City
Product: Product Name, Color, Brand, Category
Calendar: Calendar Year, Month Name, Fiscal Quarter
Promotion: Promotion Name
Returns: Returned Qty

Write 60 everyday questions a business person would type, some about time, comparisons, rankings and typos.

Output format only: one question per line, as  model | question  (model is northstar or contoso). No other text.
```

## Output

```
northstar | What were net sales last month?
northstar | Show me gross profit by region for this year
northstar | Which store had the highest net sales in Q2?
northstar | Top 10 products by units sold this year
northstar | How does margin % compare between online and in-store channels?
northstar | What is the average order value by customer segment?
northstar | Net sales year over year by month
northstar | Which category has the worst return rate %?
northstar | whats the gross proft by brand
northstar | Show order count by day of week
northstar | Are weekend sales higher than weekday sales?
northstar | Bottom 5 stores by margin % last quarter
northstar | How many orders did we have in March 2025?
northstar | Total cost vs net sales by category
northstar | Net sales for stores that opened after 2022
northstar | Which customers spent the most this year?
northstar | Returns amount by region and quarter
northstar | show me net sles trend over the last 12 months
northstar | Compare Q3 this year to Q3 last year for gross profit
northstar | What is the margin % for each subcategory in Electronics?
northstar | Top 3 brands by net sales in each region
northstar | How many units did we sell per store in the West region?
northstar | Average order value trend by quarter
northstar | Which city has the most customers joined since 2024?
northstar | Return rate % by product category and channel
northstar | What's our year to date net sales?
northstar | Rank subcategories by gross profit
northstar | order cout by month for 2025
northstar | Which stores have closed and what were their last year sales?
northstar | Net sales by customer segment compared to last year
northstar | Month over month change in units sold
northstar | Which products have list price above 100 and low margin?
northstar | Top selling SKU last week
northstar | avrage order value by city
northstar | Share of net sales by category this quarter
contoso | What was total revenue in 2024?
contoso | Revenue by country
contoso | Top 10 products by order quantity
contoso | Which state has the highest total revenue?
contoso | Compare revenue per unit across product categories
contoso | How many units were returned by brand?
contoso | Total revenue by fiscal quarter
contoso | totl revnue by color
contoso | Which promotion drove the most revenue?
contoso | Revenue this calendar year versus last year
contoso | Bottom 5 cities by total revenue
contoso | Returned units by category and month
contoso | What is the revenue per unit for each brand?
contoso | Monthly order quantity trend for 2025
contoso | Which country had the biggest drop in revenue year over year?
contoso | Top 3 products in each category by revenue
contoso | order quantaty by promotion name
contoso | Revenue share by country for fiscal Q2
contoso | Which colors sell the most units?
contoso | Returned units compared to order quantity by product
contoso | best month for revenue last year
contoso | Rank states by revenue per unit
contoso | How much revenue came from sales with no promotion?
contoso | reveneu by city in california
contoso | Which brand has the most returns this year?
```
