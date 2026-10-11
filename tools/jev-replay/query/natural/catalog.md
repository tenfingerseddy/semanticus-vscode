# The two models (what a person can see)

## northstar (a RAW model: no descriptions, no synonyms)
Measures (all on table Sales): Net Sales, Total Cost, Gross Profit, Margin %, Units, Order Count, Average Order Value,
Returns Amount, Return Rate %, Legacy Discount %.
Columns you may split or filter by (Table[Column]):
- Store[StoreName], Store[StoreCode], Store[City], Store[Region], Store[Channel], Store[OpenDate], Store[CloseDate]
- Customer[CustomerCode], Customer[CustomerName], Customer[Segment], Customer[City], Customer[Region], Customer[JoinDate]
- Product[SKU], Product[ProductName], Product[Subcategory], Product[Category], Product[CategorySortOrder], Product[Brand], Product[ListPrice], Product[UnitCost]
- Date[Date], Date[Year], Date[Quarter], Date[MonthNumber], Date[MonthName], Date[YearMonth], Date[MonthStart], Date[DayOfWeek], Date[DayOfWeekNumber], Date[IsWeekend]
- Sales[OrderNumber], Sales[OrderLine], Sales[Quantity], Sales[UnitPrice], Sales[DiscountAmount], Sales[NetSalesAmount], Sales[CostAmount], Sales[IsReturn], Sales[AuditNote]
- Metric Selector[Metric Selector], Time Intelligence[Time Calculation]  (disconnected helper tables: NOT related to Sales, so a split by them cannot work)
Relationships: Sales reaches Date, Product, Customer and Store. Every measure reaches all four.
Values the data holds:
- Store[StoreName]: Harborview Flagship, Lakeside Outlet, Lakeside Pop-up, Northstar Online, Pine Crest Pop-up, Riverbend Flagship, Sunfield Depot, Sunfield Outlet
- Store[StoreCode]: ST-001 .. ST-008
- Store[City]: Harborview, Lakeside, Pine Crest, Riverbend, Sunfield
- Store[Region] and Customer[Region]: Coast, Mountain, North, South (the same four words in both tables)
- Store[Channel]: Flagship, Online, Outlet, Pop-up
- Customer[Segment]: Retail, Trade
- Customer[City]: Harborview, Lakeside, Pine Crest, Riverbend, Willow Glen
- Product[Category]: Apparel, Electronics, Home, Kitchen, Outdoor, Samples
- Product[Subcategory]: Audio, Camping, Cookware, Footwear, Hiking, Lighting, Smart Home
- Product[Brand]: Northstar Studio, Polaris Line
- Product[ProductName]: Camp Stove, Copper Pan, Daypack, Desk Lamp, Fleece Vest, Floor Lamp, Sample Pack, Smart Bulb, Studio Headphones, Summit Boots, Trail Jacket, Travel Mug, Two Person Tent, Walking Poles, Wireless Speaker
- Date[Year]: 2024, 2025 (whole numbers). Date[Quarter]: Q1..Q4. Date[MonthName]: January..December. Date[YearMonth]: 2024-01 .. 2025-12. Date[DayOfWeek]: Monday..Sunday

## contoso-pair (asked TWICE: once on a raw copy, once on a copy prepared for AI with descriptions and synonyms)
Measures: Total Revenue, Order Quantity, Revenue per Unit (table Sales); Returned Units (table Returns).
Columns: Geography[Country], Geography[State], Geography[City]; Product[Product Name], Product[Color], Product[Brand], Product[Category];
Calendar[Calendar Year], Calendar[Month Name], Calendar[Fiscal Quarter]; Promotion[Promotion Name]; Sales[Revenue], Sales[Quantity]; Returns[Returned Qty].
Relationships: Sales reaches Geography, Product, Calendar, Promotion. Returns reaches ONLY Product. So Returned Units can be split by
Product columns only; "returns by country" or "returns by year" cannot be answered by this model.
Values: Geography[Country]: Australia, Canada, Germany, France, United States. Geography[State]: Victoria, Queensland, Ontario, Bavaria, Texas.
Geography[City]: Melbourne, Brisbane, Toronto, Munich, Austin. Product[Product Name]: Road Bike, Mountain Bike, Helmet, Water Bottle.
Product[Color]: Red, Blue, Black, Silver. Product[Brand]: Contoso, Fabrikam, Litware, Adventure Works. Product[Category]: Bikes, Accessories, Clothing.
Calendar[Calendar Year]: 2023, 2024, 2025. Calendar[Month Name]: January..December. Calendar[Fiscal Quarter]: FY Q1..FY Q4.
Promotion[Promotion Name]: No Discount, Summer Sale, Holiday Offer.

# What the product can build (the "parts")
A reading is: 1 or 2 measures; split by 0, 1 or 2 columns; filters; optionally a top/bottom N. Ordering is NOT scored.
- A filter exists ONLY when the question's own words say one of the column's values (matching tolerates small typos and
  plurals). Operators: "is" or "is_not" on text/number values; for a year column also "after", "from" (that year or later),
  "before", "up_to" (that year or earlier), with the year in the words. Several values in one filter are allowed ("is" Coast or South).
- A top/bottom N exists ONLY when a number is in the words ("top 5", "worst 3"). "best stores" with no number is a split, not a top N.
- There is NO relative time ("last month", "this year", "recently", "year to date"): the product does not know today's date.
  There is NO growth or change calculation, no "compared to", no forecast, no new ratio. Only the measures listed.
