# fresh-r4: how the questions were written

Set B of the round-4 acceptance test (rule-jev-data-v2.md). Written on 29 Sep 2026 (2026-09-29T08:21:35Z to 2026-09-29T08:21:51Z) by a separate
Claude Code session (claude -p, 2.1.284), started by the verifier in an empty folder (~/.cache/vjq4/writer-empty) with no
tools at all (--tools "" --strict-mcp-config), so it could read no code, no rules, no values and no report. Its whole input
is below, word for word the fresh-r3 input (natural/fresh-r3-writer.md), and its whole output follows it, unedited. The
verifier then labelled each question (fresh-r4-labels.json, written by make-fresh-r4.py) before any run. The Contoso
questions are asked of both Contoso copies (raw and AI-ready): 35 + 25 x 2 = 85 items.

Eight questions repeat an earlier set word for word (12 items; each carries a "repeat" field), and seven are near repeats.
They are kept, as fresh3 kept its near repeats, and the report scores the set with and without them.

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
northstar | Show gross profit by region for this year
northstar | Which store had the highest net sales in Q2?
northstar | Top 10 products by units sold
northstar | Compare net sales this year vs last year by month
northstar | What is the margin % by category?
northstar | How many orders did we get last week?
northstar | Average order value by channel
northstar | Which customer segment has the highest return rate?
northstar | Show returns amount by brand for 2025
northstar | net slaes by store city
northstar | What's our gross proft trend over the last 12 months?
northstar | Bottom 5 stores by margin %
northstar | Weekend vs weekday net sales
northstar | Which subcategory grew the most year over year?
northstar | Order count by day of week
northstar | Top 3 brands in each category by net sales
northstar | Show total cost vs net sales by quarter
northstar | How did stores opened in 2024 perform this year?
northstar | Return rate % by product category last quarter
northstar | Who are our top 20 customers by net sales?
northstar | avg order valu for new customers who joined this year
northstar | Net sales for closed stores
northstar | Which region had the lowest margin in 2025?
northstar | Units sold by month for the Electronics category
northstar | Compare online vs in-store gross profit
northstar | What is the legacy discount % by store?
northstar | Year to date net sales vs same period last year
northstar | Which products have the most returns?
northstar | Month over month change in order count
northstar | Margin % by customer region and segment
northstar | Show net sales by YearMonth for the North region
northstar | wich brand has the best return rate
northstar | Rank categories by gross profit this quarter
northstar | How many units did store 1042 sell in March?
contoso | What was total revenue last year?
contoso | Revenue by country
contoso | Top 10 products by total revenue
contoso | Which state had the highest order quantity?
contoso | Compare revenue in 2024 vs 2025
contoso | Total revenu by product color
contoso | How many units were returned by category?
contoso | Revenue per unit by brand
contoso | Which promotion drove the most revenue?
contoso | Monthly revenue trend for this calendar year
contoso | Bottom 5 cities by revenue
contoso | Revenue by fiscal quarter
contoso | Which brand has the most returned units?
contoso | Order quantity by month name for 2025
contoso | top selling categroy in Germany
contoso | Compare revenue per unit across colors
contoso | What share of revenue came from promotions?
contoso | Returned units vs order quantity by product
contoso | Rank countries by total revenue this year
contoso | Revenue for Contoso brand in Q3 fiscal
contoso | Which city in California sold the most units?
contoso | show me retuned units last month
contoso | Year over year revenue growth by category
contoso | Top 3 products per country by order quantity
contoso | Which color has the highest return ratio?
```
