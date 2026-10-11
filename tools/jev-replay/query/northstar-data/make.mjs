// The values the Northstar UAT fixture's tables hold, for the query.summarize replay.
//
// WHY THIS IS HERE. The replay answers the engine's own DISTINCT reads from CSV files beside the fixture
// (JEV_REPLAY_FIXTURES/data). Those files were made on the machine the first runs were done on and are not in any
// repository, so a replay could not be reproduced anywhere else. These are a SMALLER universe than those files, not
// the same one (10 customers where the originals have 1,848, 15 products where they have 52, and a Channel list
// without "Store"), written here so the run is repeatable: every value the committed label sets name is in them
// (labels.json, fresh-labels.json, fresh2-labels.json and
// fresh3-labels.json), plus the few a table needs to look like itself. Placeholder names only: Northstar is the UAT
// fixture, not a real model.
//
//   node tools/jev-replay/query/northstar-data/make.mjs        (rewrites the CSV files beside it)
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const csv = (rows) => rows.map((r) => r.map((c) => (/[",]/.test(String(c)) ? '"' + String(c).replace(/"/g, '""') + '"' : String(c))).join(',')).join('\n') + '\n';
const write = (table, rows) => writeFileSync(join(HERE, table + '.csv'), csv(rows));

// ---- Store ----------------------------------------------------------------------------------------------
const stores = [
    [1, 'ST-001', 'Harborview Flagship', 'Harborview', 'Coast', 'Flagship', '2019-03-01', ''],
    [2, 'ST-002', 'Lakeside Outlet', 'Lakeside', 'North', 'Outlet', '2020-06-15', ''],
    [3, 'ST-003', 'Pine Crest Pop-up', 'Pine Crest', 'Mountain', 'Pop-up', '2022-09-01', '2024-08-31'],
    [4, 'ST-004', 'Riverbend Flagship', 'Riverbend', 'South', 'Flagship', '2018-01-10', ''],
    [5, 'ST-005', 'Sunfield Outlet', 'Sunfield', 'South', 'Outlet', '2021-11-05', ''],
    [6, 'ST-006', 'Northstar Online', 'Harborview', 'Coast', 'Online', '2017-05-20', ''],
    [7, 'ST-007', 'Lakeside Pop-up', 'Lakeside', 'North', 'Pop-up', '2023-02-01', ''],
    [8, 'ST-008', 'Sunfield Depot', 'Sunfield', 'South', 'Outlet', '2022-01-15', ''],
];
write('Store', [['StoreKey', 'StoreCode', 'StoreName', 'City', 'Region', 'Channel', 'OpenDate', 'CloseDate'], ...stores]);

// ---- Customer -------------------------------------------------------------------------------------------
const customers = [
    [1, 'CU-001', 'Alpine Supply Co', 'Trade', 'Lakeside', 'North', '2019-04-02'],
    [2, 'CU-002', 'Bright Harbour Ltd', 'Trade', 'Harborview', 'Coast', '2020-01-18'],
    [3, 'CU-003', 'Cedar Works', 'Trade', 'Pine Crest', 'Mountain', '2021-07-09'],
    [4, 'CU-004', 'Dawn Fields', 'Retail', 'Riverbend', 'South', '2022-02-14'],
    [5, 'CU-005', 'Elm Yard', 'Retail', 'Willow Glen', 'South', '2022-10-30'],
    [6, 'CU-006', 'Fern Hollow', 'Retail', 'Lakeside', 'North', '2023-03-21'],
    [7, 'CU-007', 'Granite Bay Group', 'Trade', 'Harborview', 'Coast', '2018-08-08'],
    [8, 'CU-008', 'Hazel Row', 'Retail', 'Willow Glen', 'South', '2023-11-02'],
    [9, 'CU-009', 'Iron Gate Trading', 'Trade', 'Pine Crest', 'Mountain', '2020-05-25'],
    [10, 'CU-010', 'Juniper House', 'Retail', 'Riverbend', 'South', '2024-01-06'],
];
write('Customer', [['CustomerKey', 'CustomerCode', 'CustomerName', 'Segment', 'City', 'Region', 'JoinDate'], ...customers]);

// ---- Product --------------------------------------------------------------------------------------------
const products = [
    [1, 'NS-1001', 'Trail Jacket', 'Footwear', 'Apparel', 1, 'Northstar Studio', 189.0, 96.0],
    [2, 'NS-1002', 'Summit Boots', 'Footwear', 'Apparel', 1, 'Northstar Studio', 240.0, 121.0],
    [3, 'NS-1003', 'Fleece Vest', 'Footwear', 'Apparel', 1, 'Polaris Line', 95.0, 44.0],
    [4, 'NS-2001', 'Wireless Speaker', 'Audio', 'Electronics', 2, 'Polaris Line', 149.0, 78.0],
    [5, 'NS-2002', 'Studio Headphones', 'Audio', 'Electronics', 2, 'Northstar Studio', 279.0, 142.0],
    [6, 'NS-2003', 'Smart Bulb', 'Smart Home', 'Electronics', 2, 'Polaris Line', 29.0, 11.0],
    [7, 'NS-3001', 'Desk Lamp', 'Lighting', 'Home', 3, 'Polaris Line', 65.0, 27.0],
    [8, 'NS-3002', 'Floor Lamp', 'Lighting', 'Home', 3, 'Northstar Studio', 130.0, 61.0],
    [9, 'NS-4001', 'Copper Pan', 'Cookware', 'Kitchen', 4, 'Northstar Studio', 110.0, 52.0],
    [10, 'NS-4002', 'Travel Mug', 'Cookware', 'Kitchen', 4, 'Polaris Line', 32.0, 12.0],
    [11, 'NS-5001', 'Camp Stove', 'Camping', 'Outdoor', 5, 'Northstar Studio', 175.0, 88.0],
    [12, 'NS-5002', 'Two Person Tent', 'Camping', 'Outdoor', 5, 'Polaris Line', 420.0, 219.0],
    [13, 'NS-5003', 'Walking Poles', 'Hiking', 'Outdoor', 5, 'Northstar Studio', 78.0, 33.0],
    [14, 'NS-5004', 'Daypack', 'Hiking', 'Outdoor', 5, 'Polaris Line', 88.0, 41.0],
    [15, 'NS-9001', 'Sample Pack', 'Cookware', 'Samples', 9, 'Northstar Studio', 0.0, 3.0],
];
write('Product', [['ProductKey', 'SKU', 'ProductName', 'Subcategory', 'Category', 'CategorySortOrder', 'Brand', 'ListPrice', 'UnitCost'], ...products]);

// ---- Date -----------------------------------------------------------------------------------------------
const monthNames = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const dayNames = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const pad = (n) => String(n).padStart(2, '0');
const dates = [];
for (const year of [2024, 2025]) {
    for (let month = 1; month <= 12; month++) {
        const days = new Date(Date.UTC(year, month, 0)).getUTCDate();
        for (let day = 1; day <= days; day++) {
            const d = new Date(Date.UTC(year, month - 1, day));
            const dow = d.getUTCDay();
            dates.push([
                Number(`${year}${pad(month)}${pad(day)}`), `${year}-${pad(month)}-${pad(day)}`, year,
                'Q' + Math.ceil(month / 3), month, monthNames[month - 1], `${year}-${pad(month)}`,
                `${year}-${pad(month)}-01`, dayNames[dow], dow === 0 ? 7 : dow, dow === 0 || dow === 6 ? 1 : 0,
            ]);
        }
    }
}
write('Date', [['DateKey', 'Date', 'Year', 'Quarter', 'MonthNumber', 'MonthName', 'YearMonth', 'MonthStart', 'DayOfWeek', 'DayOfWeekNumber', 'IsWeekend'], ...dates]);

// ---- Sales ----------------------------------------------------------------------------------------------
// Sixty lines, enough for every key to be used and for the readable columns (OrderNumber, Quantity, IsReturn) to
// hold a small, tidy set of values. The numbers themselves are never read by the replay: only DISTINCT is answered.
const sales = [];
for (let i = 0; i < 60; i++) {
    const product = products[i % products.length];
    const store = stores[i % stores.length];
    const customer = customers[i % customers.length];
    const date = dates[(i * 11) % dates.length];
    const quantity = (i % 5) + 1;
    const unitPrice = product[7];
    const discount = i % 7 === 0 ? 10 : 0;
    const isReturn = i % 13 === 0 ? 1 : 0;
    sales.push([
        i + 1, 'SO-' + String(10001 + i), (i % 3) + 1, date[0], product[0], customer[0], store[0], quantity,
        unitPrice, discount, (quantity * unitPrice - discount).toFixed(2), (quantity * product[8]).toFixed(2),
        isReturn, isReturn === 1 ? i : 0, '',
    ]);
}
write('Sales', [['SalesLineKey', 'OrderNumber', 'OrderLine', 'OrderDateKey', 'ProductKey', 'CustomerKey', 'StoreKey', 'Quantity', 'UnitPrice', 'DiscountAmount', 'NetSalesAmount', 'CostAmount', 'IsReturn', 'ReturnOfSalesLineKey', 'AuditNote'], ...sales]);

console.log('wrote Store, Customer, Product, Date and Sales CSV files in ' + HERE);
