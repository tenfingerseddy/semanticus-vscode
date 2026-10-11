# fresh-r3: how the questions were written

Written on 29 Sep 2026 by a separate Claude Code session (claude -p, 2.1.283), started in an empty folder with no tools at all (--tools "" --strict-mcp-config), so it could read no code, no rules, no values and no report. Its whole input is below, word for word, and its whole output follows it, unedited. The builder then labelled each question (fresh-r3-labels.json) before any engine change of round 3 and before any run. The Contoso questions are asked of both Contoso copies (raw and AI-ready).

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
northstar | Show gross profit by region for 2025
northstar | Which store had the highest net sales this year?
northstar | Top 10 products by units sold last quarter
northstar | Compare margin % this year vs last year by category
northstar | What's the return rate % by channel?
northstar | Average order value by customer segment
northstar | How many orders did we get in March?
northstar | net sales by month for the last 12 months
northstar | Which brand has the worst margin?
northstar | Show total cost vs net sales by quarter
northstar | Bottom 5 stores by gross profit in 2025
northstar | what was our retrun rate last year
northstar | Net sales on weekends vs weekdays
northstar | Which city brings in the most revenue?
northstar | gross proft by subcategory for Q2
northstar | How did units sold change month over month this year?
northstar | Returns amount by store in the North region
northstar | Top 3 customers by net sales in 2026
northstar | What day of the week has the most orders?
northstar | Show margin % trend by month
northstar | Compare net sales for Online vs In-Store channel
northstar | Which category grew the most year over year?
northstar | average ordr value by region last quarter
northstar | How many units of each brand did we sell this year?
northstar | Net sales for stores that opened in 2024
northstar | Rank subcategories by return rate %
northstar | Year to date net sales vs same period last year
northstar | Which products have the highest list price?
northstar | order count by customer city
northstar | What's the gross profit for the Premium segment this quarter?
northstar | Show net sales by year and quarter
northstar | top 5 regoins by margin %
northstar | How much did we lose to returns in Q1?
northstar | Net sales by category sorted by category order
contoso | What was total revenue last year?
contoso | Top 10 products by total revenue
contoso | Revenue by country for 2025
contoso | Which state had the most returned units?
contoso | Compare total revenue this year vs last year
contoso | Order quantity by month name
contoso | Revenue per unit by brand
contoso | Which promotion drove the most revenue?
contoso | total revenu by color
contoso | Bottom 5 cities by order quantity
contoso | Show revenue by fiscal quarter
contoso | How many units were returned in 2024?
contoso | Which category sells the most units?
contoso | Revenue per unit by category and year
contoso | top 3 brnads by revenue in Germany
contoso | What's the trend of total revenue by calendar year?
contoso | Returned units by product category
contoso | Rank states by total revenue in the United States
contoso | order qty by promotion name last year
contoso | Which color has the highest revenue per unit?
contoso | Compare revenue in Q1 vs Q2 fiscal quarter
contoso | Top 5 products by returned units
contoso | How much revenue did we make in December?
contoso | revenue by city in California
contoso | Which country grew revenue the most year over year?
```
