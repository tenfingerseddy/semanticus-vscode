import json
import uuid
from pathlib import Path

ROOT = Path(__file__).parent

POLICIES = {
    'year': ('Show the four-digit calendar or fiscal year as ungrouped digits, with no decimals, currency, percent, or scale.', ['0', '0000'], ['#,0', '#,##0', '0.00', '0%']),
    'key': ('Show this numeric identifier as ungrouped integer digits; never group, round, scale, or add a unit.', ['0', '000000'], ['#,0', '#,##0', '0.0', '$0']),
    'fixed5': ('Show exactly five ungrouped integer digits, retaining leading zeroes; no decimals, scale, currency, or percent.', ['00000'], ['0', '#,##0', '0000', '0.00']),
    'fixed6': ('Show exactly six ungrouped integer digits, retaining leading zeroes; no decimals, scale, currency, or percent.', ['000000'], ['0', '#,##0', '00000', '0.00']),
    'count': ('Show a whole-number count, optionally with thousands grouping, and no decimals, currency, percent, or scale.', ['0', '#,0', '#,##0'], ['0.00', '0%', '$#,##0.00']),
    'money': ('Show a dollar amount with exactly two decimal places; grouping and negative parentheses are allowed, but percent or scaling is not.', ['$0.00', '$#,##0.00'], ['0', '#,##0', '0.00', '0.0%']),
    'percent': ('Show the fractional value as a percent with one or two decimal places, not as an unscaled decimal or rounded whole percent.', ['0.0%', '0.00%'], ['0', '0.00', '0%', '$0.00']),
    'decimal': ('Show a noncurrency quantity with two or three decimal places, optionally grouped, without percent or scaling.', ['0.00', '#,##0.000'], ['0', '#,##0', '0%', '$0.00']),
    'fx': ('Show the exchange rate with four to six decimal places, unscaled and without currency or percent.', ['0.0000', '0.000000'], ['0', '0.00', '0%', '$0.00']),
    'ratio': ('Show a dimensionless multiple with two or three decimal places and an x suffix; do not turn it into a percent.', ['0.00"x"', '0.000"x"'], ['0', '0.00', '0%', '#,##0']),
    'days': ('Show days with one or two decimal places and a days suffix; no percent, currency, or scaling.', ['0.0" days"', '0.00" days"'], ['0', '0.0%', '$0.00']),
    'seconds': ('Show seconds with one or two decimal places and an s suffix; no percent, currency, or scaling.', ['0.0" s"', '0.00" s"'], ['0', '0.0%', '$0.00']),
    'milliseconds': ('Show milliseconds with one or two decimal places and an ms suffix; no percent, currency, or scaling.', ['0.0" ms"', '0.00" ms"'], ['0', '0.0%', '$0.00']),
    'date': ('Show the complete calendar date, including year, month, and day, rather than a numeric serial or year alone.', ['yyyy-MM-dd', 'dd mmm yyyy'], ['0', '#,##0', 'yyyy']),
    'text': ('This is text, so numeric format suitability cannot be judged; classify every proposed format U.', [], []),
    'ambiguous': ('The model does not identify a single display unit or currency; classify every proposed format U.', [], []),
}

models = {}
labels = []

def model(name, description):
    models[name] = {'description': description, 'tables': {}, 'relationships': []}

def table(m, name, cols, measures=(), calc_group=False):
    models[m]['tables'][name] = {'cols': cols, 'measures': measures, 'calc_group': calc_group}
    for c in cols:
        register(m, name, 'column', c)
    for q in measures:
        register(m, name, 'measure', q)

def C(name, dtype, policy, what, source=None, adv=None, prov=True, shipped=None):
    return dict(name=name, dtype=dtype, policy=policy, what=what, source=source or name.replace(' ', ''), adv=adv, prov=prov, shipped=shipped)

def Q(name, expr, policy, what, adv=None, prov=True, shipped=None):
    return dict(name=name, expr=expr, policy=policy, what=what, adv=adv, prov=prov, shipped=shipped)

def register(m, t, kind, item):
    rule, right, wrong = POLICIES[item['policy']]
    uncertain = item['policy'] in ('text', 'ambiguous')
    why = ('The expression, name, data type, and model relationships identify the unit.' if item['prov'] else
           'The model alone does not establish the complete business meaning or display convention.')
    if uncertain:
        why = 'A numeric format cannot be determined for this text field.' if item['policy'] == 'text' else 'Multiple display units remain possible from the model alone.'
    labels.append(dict(id=f'{m}|{t}|{kind}|{item["name"]}', what=item['what'], shipped_format=item['shipped'],
                       unsure=uncertain, right=right, wrong=wrong, rule_in_words=rule,
                       provable_from_model=item['prov'] and not uncertain, why=why,
                       adversarial=item['adv']))

def rel(m, from_table, from_col, to_table, to_col):
    models[m]['relationships'].append((from_table, from_col, to_table, to_col))

model('SalesOps', 'Order-line sales, products, customers, and an unmarked calendar.')
table('SalesOps', 'Sales', [
    C('Order ID','int64','key','order identifier'), C('Order Date Key','int64','key','YYYYMMDD date join key'),
    C('Product Key','int64','key','product join key'), C('Customer Key','int64','key','customer join key'),
    C('Gross Amount','double','money','gross line amount in dollars'),
    C('Discount Amount','double','money','line discount in dollars'),
    C('Net Amount','double','money','net line amount in dollars'),
    C('Units','int64','count','units sold on this line'),
    C('Promotion Code','int64','fixed6','six-digit promotion code', adv='integer_code', prov=False),
], [
    Q('Revenue','SUM(Sales[Net Amount])','money','net sales dollars'),
    Q('Gross Sales','SUM(Sales[Gross Amount])','money','gross sales dollars'),
    Q('Units Sold','SUM(Sales[Units])','count','units sold'),
    Q('Order Count','DISTINCTCOUNT(Sales[Order ID])','count','distinct orders'),
    Q('Average Selling Price','DIVIDE([Revenue], [Units Sold])','money','dollars per unit'),
    Q('Discount Rate','DIVIDE(SUM(Sales[Discount Amount]), [Gross Sales])','percent','discount divided by gross sales',adv='ticked_then_expression_changes'),
    Q('Revenue Share','DIVIDE([Revenue], CALCULATE([Revenue], ALL(Product)))','percent','share of total product revenue',adv='ratio_hidden_behind_measures'),
    Q('Revenue YoY','DIVIDE([Revenue] - CALCULATE([Revenue], DATEADD(Calendar[Date], -1, YEAR)), CALCULATE([Revenue], DATEADD(Calendar[Date], -1, YEAR)))','percent','year over year revenue change',adv='ratio_hidden_behind_measures'),
])
table('SalesOps','Product',[
    C('Product Key','int64','key','product join key'), C('Product Code','int64','fixed6','six-digit product code',adv='integer_code',prov=False),
    C('Category','string','text','product category'), C('List Price','double','money','list price in dollars'),
])
table('SalesOps','Customer',[
    C('Customer Key','int64','key','customer join key'), C('Segment','string','text','customer segment'),
    C('Postal Code','int64','fixed5','five-digit postal code',adv='integer_code',prov=False),
])
table('SalesOps','Calendar',[
    C('Date Key','int64','key','YYYYMMDD join key',adv='date_key_integer'),
    C('Date','dateTime','date','full calendar date',shipped='yyyy-MM-dd'),
    C('Calendar Year','int64','year','calendar year stored as integer',adv='year_integer',shipped='0'),
    C('Fiscal Year','int64','year','fiscal year stored as integer',adv='unmarked_date_table'),
    C('Fiscal Period','int64','count','fiscal period number 1 through 12',adv='unmarked_date_table'),
])
for a,b,c,d in [('Sales','Order Date Key','Calendar','Date Key'),('Sales','Product Key','Product','Product Key'),('Sales','Customer Key','Customer','Customer Key')]: rel('SalesOps',a,b,c,d)

model('FinanceLedger','General ledger with multi-currency facts and a calculation group.')
table('FinanceLedger','Journal',[
    C('Entry ID','int64','key','journal entry identifier'), C('Posting Date Key','int64','key','posting date join key'),
    C('Account Key','int64','key','account join key'), C('Department Key','int64','key','department join key'),
    C('Amount','double','ambiguous','transaction amount in the row currency',prov=False,adv='mixed_currency'),
    C('Reporting USD','double','money','translated amount in USD'),
    C('Currency Code','string','text','ISO currency code'),
    C('Exchange Rate','double','fx','source currency units per USD',prov=False),
    C('Document Number','int64','fixed6','six-digit accounting document number',adv='integer_code',prov=False),
], [
    Q('Reporting Revenue','CALCULATE(SUM(Journal[Reporting USD]), Accounts[Account Type] = "Revenue")','money','reported revenue in USD'),
    Q('Reporting Expense','CALCULATE(SUM(Journal[Reporting USD]), Accounts[Account Type] = "Expense")','money','reported expense in USD'),
    Q('Operating Profit','[Reporting Revenue] - [Reporting Expense]','money','operating profit in USD'),
    Q('Operating Margin','DIVIDE([Operating Profit], [Reporting Revenue])','percent','operating profit divided by revenue',adv='ratio_hidden_behind_measures'),
    Q('Expense Ratio','DIVIDE([Reporting Expense], [Reporting Revenue])','percent','expense divided by revenue'),
    Q('Entry Count','DISTINCTCOUNT(Journal[Entry ID])','count','count of journal entries'),
    Q('Average FX Rate','AVERAGE(Journal[Exchange Rate])','fx','average exchange rate',prov=False),
    Q('Raw Amount','SUM(Journal[Amount])','ambiguous','sum of amounts with multiple currencies',adv='mixed_currency',prov=False),
])
table('FinanceLedger','Accounts',[
    C('Account Key','int64','key','account join key'), C('Account Code','int64','fixed6','six-digit general ledger account code',adv='integer_code',prov=False),
    C('Account Type','string','text','revenue or expense account type'), C('Account Name','string','text','account description'),
])
table('FinanceLedger','Department',[
    C('Department Key','int64','key','department join key'), C('Cost Center','int64','fixed5','five-digit cost center code',adv='integer_code',prov=False),
    C('Department Name','string','text','department description'),
])
table('FinanceLedger','FiscalCalendar',[
    C('Date Key','int64','key','YYYYMMDD date join key'), C('Date','dateTime','date','posting calendar date'),
    C('Fiscal Year','int64','year','fiscal year stored as integer',adv='year_integer'),
    C('Fiscal Quarter','int64','count','fiscal quarter number 1 through 4'),
])
table('FinanceLedger','Time Calc',[
    C('Name','string','text','calculation item name',prov=False),
    C('Ordinal','int64','count','calculation item sort ordinal',adv='calculation_group'),
],calc_group=True)
for a,b,c,d in [('Journal','Posting Date Key','FiscalCalendar','Date Key'),('Journal','Account Key','Accounts','Account Key'),('Journal','Department Key','Department','Department Key')]: rel('FinanceLedger',a,b,c,d)

model('PeopleOps','Workforce, departments, absence, and an unmarked employee calendar.')
table('PeopleOps','Employees',[
    C('Employee Number','int64','fixed6','six-digit employee number',adv='integer_code',prov=False),
    C('Hire Date Key','int64','key','hire date join key'), C('Termination Date Key','int64','key','termination date join key'),
    C('Department Key','int64','key','department join key'), C('Annual Salary','double','money','annual salary in dollars',prov=False),
    C('FTE','double','decimal','full time equivalent fraction, such as 0.75',prov=False),
    C('Birth Year','int64','year','birth year stored as integer',adv='year_integer'),
    C('Employment Type','string','text','employment type'),
], [
    Q('Headcount','DISTINCTCOUNT(Employees[Employee Number])','count','people in workforce'),
    Q('Total Salary','SUM(Employees[Annual Salary])','money','sum of annual salaries in dollars'),
    Q('Average Salary','AVERAGE(Employees[Annual Salary])','money','average annual salary in dollars'),
    Q('Total FTE','SUM(Employees[FTE])','decimal','full time equivalents'),
    Q('Leaver Count','COUNTROWS(FILTER(Employees, NOT ISBLANK(Employees[Termination Date Key])))','count','employees with a termination date'),
    Q('Attrition Rate','DIVIDE([Leaver Count], [Headcount])','percent','leavers divided by headcount',adv='ticked_then_rename'),
    Q('Absence Rate','DIVIDE(SUM(Absence[Hours]), SUM(Absence[Scheduled Hours]))','percent','absence hours divided by scheduled hours'),
    Q('Absence Hours','SUM(Absence[Hours])','decimal','hours absent, including partial hours',prov=False),
])
table('PeopleOps','Department',[
    C('Department Key','int64','key','department join key'), C('Cost Center','int64','fixed5','five-digit cost center code',adv='integer_code',prov=False),
    C('Department Name','string','text','department description'),
])
table('PeopleOps','Calendar',[
    C('Date Key','int64','key','YYYYMMDD date join key'), C('Date','dateTime','date','full calendar date'),
    C('Year','int64','year','calendar year stored as integer',adv='unmarked_date_table'),
    C('Month Number','int64','count','calendar month number 1 through 12'),
])
table('PeopleOps','Absence',[
    C('Absence ID','int64','key','absence record identifier'), C('Employee Number','int64','fixed6','six-digit employee number',adv='integer_code',prov=False),
    C('Date Key','int64','key','absence date join key'), C('Hours','double','decimal','hours absent, including partial hours'),
    C('Scheduled Hours','double','decimal','scheduled hours, including partial hours'),
    C('Reason','string','text','absence reason'),
])
for a,b,c,d in [('Employees','Department Key','Department','Department Key'),('Employees','Hire Date Key','Calendar','Date Key'),('Absence','Employee Number','Employees','Employee Number'),('Absence','Date Key','Calendar','Date Key')]: rel('PeopleOps',a,b,c,d)

model('StockFlow','Daily warehouse snapshots, products, and replenishment metrics.')
table('StockFlow','Stock',[
    C('Snapshot ID','int64','key','snapshot row identifier'), C('Date Key','int64','key','snapshot date join key'),
    C('Product Key','int64','key','product join key'), C('Warehouse Key','int64','key','warehouse join key'),
    C('On Hand Units','int64','count','units physically on hand'), C('Safety Stock Units','int64','count','target safety stock units'),
    C('Inventory Value','double','money','inventory valuation in dollars'),
    C('Batch Code','int64','fixed6','six-digit batch code',adv='integer_code',prov=False),
], [
    Q('On Hand','SUM(Stock[On Hand Units])','count','units on hand'),
    Q('Safety Stock','SUM(Stock[Safety Stock Units])','count','safety stock units'),
    Q('Inventory Value Total','SUM(Stock[Inventory Value])','money','inventory value in dollars'),
    Q('Average Unit Cost','DIVIDE([Inventory Value Total], [On Hand])','money','dollars per unit'),
    Q('Stock to Safety Multiple','DIVIDE([On Hand], [Safety Stock])','ratio','on hand divided by safety stock, shown as x multiple',adv='ratio_hidden_behind_measures'),
    Q('Reorder Coverage','DIVIDE([On Hand], 24.0)','days','days of cover at 24 units per day',adv='ticked_then_expression_changes',prov=False),
    Q('Low Stock Product Count','COUNTROWS(FILTER(Product, [On Hand] < [Safety Stock]))','count','number of products under safety stock'),
    Q('Shrinkage Rate','DIVIDE([Safety Stock] - [On Hand], [Safety Stock])','percent','shortfall divided by safety stock'),
])
table('StockFlow','Product',[
    C('Product Key','int64','key','product join key'), C('SKU Numeric','int64','fixed6','six-digit stock keeping unit code',adv='ticked_then_deleted',prov=False),
    C('Product Label','string','text','product description'), C('Unit Cost','double','money','unit cost in dollars'),
    C('Lead Time Days','double','days','supplier lead time in days',prov=False),
])
table('StockFlow','Warehouse',[
    C('Warehouse Key','int64','key','warehouse join key'), C('Location Code','int64','fixed5','five-digit location code',adv='integer_code',prov=False),
    C('Region','string','text','warehouse region'),
])
table('StockFlow','Calendar',[
    C('Date Key','int64','key','YYYYMMDD date join key'), C('Date','dateTime','date','full snapshot date'),
    C('Year','int64','year','snapshot year stored as integer',adv='year_integer'),
])
for a,b,c,d in [('Stock','Date Key','Calendar','Date Key'),('Stock','Product Key','Product','Product Key'),('Stock','Warehouse Key','Warehouse','Warehouse Key')]: rel('StockFlow',a,b,c,d)

model('WebPulse','Event analytics, campaign spend, devices, and site performance.')
table('WebPulse','Events',[
    C('Event ID','int64','key','event identifier'), C('Session ID','int64','key','session identifier'),
    C('Event Date Key','int64','key','event date join key'), C('Campaign Key','int64','key','campaign join key'),
    C('Device Key','int64','key','device join key'), C('Event Type','string','text','event type'),
    C('Revenue','double','money','commerce revenue in dollars'),
    C('Page Load Ms','double','milliseconds','page load duration in milliseconds'),
    C('Duration Seconds','double','seconds','event duration in seconds'),
    C('Session Year','int64','year','year of session stored as integer',adv='year_integer'),
], [
    Q('Sessions','DISTINCTCOUNT(Events[Session ID])','count','distinct sessions'),
    Q('Purchases','CALCULATE(COUNTROWS(Events), Events[Event Type] = "purchase")','count','purchase events'),
    Q('Checkout Conversion','DIVIDE([Purchases], [Sessions])','percent','purchases divided by sessions',adv='ticked_then_expression_changes'),
    Q('Bounce Rate','DIVIDE(CALCULATE([Sessions], Events[Event Type] = "bounce"), [Sessions])','percent','bounce sessions divided by sessions'),
    Q('Average Page Load','AVERAGE(Events[Page Load Ms])','milliseconds','mean page load in milliseconds'),
    Q('Average Session Duration','AVERAGE(Events[Duration Seconds])','seconds','mean session duration in seconds'),
    Q('Revenue Per Session','DIVIDE(SUM(Events[Revenue]), [Sessions])','money','dollars per session'),
    Q('Return on Ad Spend','DIVIDE(SUM(Events[Revenue]), SUM(Campaign[Spend]))','ratio','revenue divided by campaign spend, shown as x multiple',adv='ratio_hidden_behind_measures'),
])
table('WebPulse','Campaign',[
    C('Campaign Key','int64','key','campaign join key'), C('Campaign Code','int64','fixed6','six-digit campaign code',adv='integer_code',prov=False),
    C('Channel','string','text','marketing channel'), C('Spend','double','money','campaign spend in dollars'),
])
table('WebPulse','Calendar',[
    C('Date Key','int64','key','YYYYMMDD date join key'), C('Date','dateTime','date','full event date'),
    C('Year','int64','year','event year stored as integer',adv='unmarked_date_table'),
    C('Week Number','int64','count','week number 1 through 53'),
])
table('WebPulse','Device',[
    C('Device Key','int64','key','device join key'), C('Device Code','int64','fixed5','five-digit device code',adv='integer_code',prov=False),
    C('Device Name','string','text','device class name'),
])
for a,b,c,d in [('Events','Event Date Key','Calendar','Date Key'),('Events','Campaign Key','Campaign','Campaign Key'),('Events','Device Key','Device','Device Key')]: rel('WebPulse',a,b,c,d)

def quote(s):
    return "'" + s.replace("'", "''") + "'" if ' ' in s or '-' in s else s

def model_description(what, policy):
    hints = {
        'money': 'Cents must remain visible.',
        'percent': 'Report to at least a tenth of a percentage point.',
        'decimal': 'Hundredths matter.',
        'fx': 'At least four fractional digits matter.',
        'ratio': 'Display as an x multiple to hundredths.',
        'days': 'Tenths of a day matter.',
        'seconds': 'Tenths of a second matter.',
        'milliseconds': 'Tenths of a millisecond matter.',
    }
    return what + ('. ' + hints[policy] if policy in hints else '')

def col_tmdl(c):
    lines = [f'\tcolumn {quote(c["name"])}', f'\t\tdataType: {c["dtype"]}', f'\t\tsourceColumn: {c["source"]}', '\t\tsummarizeBy: none']
    if c['prov'] and c['policy'] not in ('text', 'ambiguous'): lines.insert(0, f'\t/// {model_description(c["what"], c["policy"])}')
    if c['shipped'] is not None: lines.append(f'\t\tformatString: {c["shipped"]}')
    return '\n'.join(lines)

def measure_tmdl(q):
    lines = [f'\tmeasure {quote(q["name"])} = {q["expr"]}']
    if q['prov'] and q['policy'] not in ('text', 'ambiguous'): lines.insert(0, f'\t/// {model_description(q["what"], q["policy"])}')
    if q['shipped'] is not None: lines.append(f'\t\tformatString: {q["shipped"]}')
    return '\n'.join(lines)

def m_type(dtype):
    return {'int64':'Int64.Type','double':'type number','dateTime':'type date','string':'type text'}[dtype]

for m, info in models.items():
    folder = ROOT / 'models' / m
    (folder / 'tables').mkdir(parents=True, exist_ok=True)
    (folder / 'database.tmdl').write_text(f'database {m}\n\tcompatibilityLevel: 1601\n', encoding='utf-8')
    model_lines = ['model Model', '\tculture: en-US', '\tdefaultPowerBIDataSourceVersion: powerBI_V3', '\tsourceQueryCulture: en-US']
    if m == 'FinanceLedger': model_lines.append('\tdiscourageImplicitMeasures: true')
    (folder / 'model.tmdl').write_text('\n'.join(model_lines)+'\n', encoding='utf-8')
    for t, spec in info['tables'].items():
        lines = [f'table {quote(t)}']
        if spec['calc_group']:
            lines += ['\tcalculationGroup', '\t\tprecedence: 20', '\t\tcalculationItem Current = SELECTEDMEASURE()', '\t\tcalculationItem PriorYear = CALCULATE(SELECTEDMEASURE(), DATEADD(FiscalCalendar[Date], -1, YEAR))', '']
        for c in spec['cols']: lines += [col_tmdl(c), '']
        for q in spec['measures']: lines += [measure_tmdl(q), '']
        if spec['calc_group']:
            lines.append(f'\tpartition {quote(t)} = calculationGroup')
        else:
            schema = ', '.join(f'{c["source"]} = {m_type(c["dtype"])}' for c in spec['cols'])
            lines += [f'\tpartition {quote(t)} = m', '\t\tmode: import', '\t\tsource =', '\t\t\tlet', f'\t\t\t\tSource = #table(type table [{schema}], {{}})', '\t\t\tin', '\t\t\t\tSource']
        (folder / 'tables' / f'{t}.tmdl').write_text('\n'.join(lines)+'\n', encoding='utf-8')
    rel_lines = []
    for ft,fc,tt,tc in info['relationships']:
        rid = uuid.uuid5(uuid.NAMESPACE_DNS, f'{m}:{ft}.{fc}->{tt}.{tc}')
        rel_lines += [f'relationship {rid}', f'\tfromColumn: {quote(ft)}.{quote(fc)}', f'\ttoColumn: {quote(tt)}.{quote(tc)}', '']
    (folder / 'relationships.tmdl').write_text('\n'.join(rel_lines), encoding='utf-8')

(ROOT/'labels.json').write_text(json.dumps(labels, indent=2)+'\n', encoding='utf-8')

by_id = {x['id']:x for x in labels}

def after_label(oldid, policy=None, newid=None, what=None, adv='changed_after_suggestion', prov=None):
    old = by_id[oldid]
    new = dict(old)
    if newid: new['id'] = newid
    if what: new['what'] = what
    if policy:
        rule, right, wrong = POLICIES[policy]
        new.update(unsure=policy in ('text','ambiguous'), right=right, wrong=wrong, rule_in_words=rule)
        if policy in ('text', 'ambiguous'):
            new['provable_from_model'] = False
    if prov is not None:
        new['provable_from_model'] = prov
    new['adversarial'] = adv
    if new['unsure']:
        new['why'] = 'The changed object no longer has a determinate numeric display format.'
    elif new['provable_from_model']:
        new['why'] = 'The current expression and metadata establish the unit and precision; the earlier suggestion is stale.'
    else:
        new['why'] = 'The changed meaning is supplied by the change script; current metadata alone does not establish a safe display.'
    return new

changes = []
def change(cid,m,when,target,words,tmdl,after=(),new_id=None):
    d = dict(id=cid,model=m,when=when,target=target,change=words,tmdl_after=tmdl,labels_after=list(after))
    if new_id: d['new_id']=new_id
    changes.append(d)

change('S1','SalesOps','after the person ticks, before the write','SalesOps|Sales|measure|Discount Rate',
       'Replace the percentage expression with total discount dollars; a ticked percent format is now wrong.',
       '\tmeasure \'Discount Rate\' = SUM(Sales[Discount Amount])',
       [after_label('SalesOps|Sales|measure|Discount Rate','money',what='total discount dollars after expression edit')])
change('F1','FinanceLedger','after the person ticks, before the write','FinanceLedger|FiscalCalendar|column|Fiscal Year',
       'Change the fiscal year column from integer to text in the source and model; update the FiscalCalendar partition schema to type text.',
       '\tcolumn \'Fiscal Year\'\n\t\tdataType: string\n\t\tsourceColumn: FiscalYear\n\t\tsummarizeBy: none',
       [after_label('FinanceLedger|FiscalCalendar|column|Fiscal Year','text',what='fiscal year now stored as text')])
change('P1','PeopleOps','before the person ticks','PeopleOps|Employees|measure|Attrition Rate',
       'Rename Attrition Rate to Leaver Share, keeping the expression.',
       '\tmeasure \'Leaver Share\' = DIVIDE([Leaver Count], [Headcount])',
       [after_label('PeopleOps|Employees|measure|Attrition Rate',newid='PeopleOps|Employees|measure|Leaver Share',what='leavers divided by headcount after rename')],
       new_id='PeopleOps|Employees|measure|Leaver Share')
change('I1','StockFlow','after the person ticks, before the write','StockFlow|Product|column|SKU Numeric',
       'Delete the SKU Numeric column after a format was ticked.', 'deleted')
change('W1','WebPulse','after the person ticks, before the write','WebPulse|Events|measure|Checkout Conversion',
       'Replace conversion with purchase event count.',
       '\tmeasure \'Checkout Conversion\' = [Purchases]',
       [after_label('WebPulse|Events|measure|Checkout Conversion','count',what='purchase event count after expression edit')])
change('I2','StockFlow','after the person ticks, before the write','StockFlow|Stock|measure|Reorder Coverage',
       'Replace days of cover with on-hand-to-safety-stock multiple.',
       '\tmeasure \'Reorder Coverage\' = DIVIDE([On Hand], [Safety Stock])',
       [after_label('StockFlow|Stock|measure|Reorder Coverage','ratio',what='on hand divided by safety stock after expression edit')])
change('F2','FinanceLedger','before the person ticks','FinanceLedger|Journal|measure|Operating Margin',
       'Replace profit divided by revenue with operating profit dollars.',
       '\tmeasure \'Operating Margin\' = [Operating Profit]',
       [after_label('FinanceLedger|Journal|measure|Operating Margin','money',what='operating profit dollars after expression edit')])
change('P2','PeopleOps','after the person ticks, before the write','PeopleOps|Employees|column|Birth Year',
       'Rename Birth Year to Year of Birth without changing the integer year values.',
       '\tcolumn \'Year of Birth\'\n\t\tdataType: int64\n\t\tsourceColumn: BirthYear\n\t\tsummarizeBy: none',
       [after_label('PeopleOps|Employees|column|Birth Year',newid='PeopleOps|Employees|column|Year of Birth',what='birth year stored as integer after rename')],
       new_id='PeopleOps|Employees|column|Year of Birth')
change('W2','WebPulse','before the person ticks','WebPulse|Events|column|Page Load Ms',
       'Source switches from milliseconds to seconds while retaining the column name temporarily; replace PageLoadMs with PageLoadSeconds in the Events partition schema and source. The Average Page Load measure now also returns seconds, despite its stale description.',
       '\tcolumn \'Page Load Ms\'\n\t\tdataType: double\n\t\tsourceColumn: PageLoadSeconds\n\t\tsummarizeBy: none',
       [after_label('WebPulse|Events|column|Page Load Ms','seconds',what='page load time in seconds after source substitution',prov=False),
        after_label('WebPulse|Events|measure|Average Page Load','seconds',what='mean page load in seconds after source substitution',prov=False)])
change('S2','SalesOps','after the person ticks, before the write','SalesOps|Sales|column|Promotion Code',
       'Change the six-digit promotion code to a dollar discount value; replace PromotionCode with PromotionDiscountDollars in the Sales partition schema and source.',
       '\tcolumn \'Promotion Code\'\n\t\tdataType: double\n\t\tsourceColumn: PromotionDiscountDollars\n\t\tsummarizeBy: none',
       [after_label('SalesOps|Sales|column|Promotion Code','money',what='promotion discount dollars after source substitution')])

for entry in changes:
    if entry['tmdl_after'] != 'deleted' and entry['labels_after']:
        new_label = entry['labels_after'][0]
        if new_label['provable_from_model']:
            policy = next(k for k, v in POLICIES.items() if v[0] == new_label['rule_in_words'])
            entry['tmdl_after'] += '\n\t\tdescription: ' + model_description(new_label['what'], policy)

changes.sort(key=lambda entry: 0 if entry['when'] == 'before the person ticks' else 1)
(ROOT/'changes.json').write_text(json.dumps(changes, indent=2)+'\n', encoding='utf-8')
print(f'{len(models)} models, {len(labels)} labels, {len(changes)} changes')
