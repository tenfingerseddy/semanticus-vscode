#!/usr/bin/env python3
# Gather column labels and derived facts from PUBLIC sample models on this machine (the second columns replay).
#
#   python3 gather-pbix.py --sources <list file> [--out <facts file>]
#
# Needs the pbixray package (pip install pbixray) in the interpreter that runs it. The list file holds one path per
# line to a .pbix. Every file is a public sample model; the first replay's one private model is not read here.
#
# Output is the SAME facts file gather.mjs writes (kind jev-columns-facts, version 1), so build-calls.mjs and
# score.mjs run on it unchanged. Models are written under anonymous keys (p0, p1, ...); the source path and file
# name are never written. The key-to-path map goes to a separate file beside the output, which stays in the private
# cache dir with the rest.
#
# Privacy shape, the same as derive.mjs. pbixray decodes each table into this process, the counts below are taken
# from it, and the values are dropped. Only names, types, the five labelled settings and DERIVED facts are written:
# counts, shares and booleans. No row, raw value, minimum, maximum or sample is written, and nothing is printed but
# counts.
#
# The facts follow derive.mjs's DAX probe field for field (columnFactsQuery and deriveMeasured), computed in Python
# instead of DAX because these files have no engine to query. The one difference: one-to-one pairs are tested over
# every row of the table, not capped at 400 pairs, because the rows are already in memory.
import argparse
import hashlib
import json
import math
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent

DATA_TYPE = {1: 'automatic', 2: 'string', 6: 'int64', 8: 'double', 9: 'dateTime', 10: 'decimal', 11: 'boolean',
             17: 'binary', 19: 'unknown', 20: 'variant'}
SUMMARIZE_BY = {1: 'default', 2: 'none', 3: 'sum', 4: 'min', 5: 'max', 6: 'count', 7: 'average', 8: 'distinctcount'}
COLUMN_TYPE = {1: 'data', 2: 'calculated', 3: 'rowNumber', 4: 'calculatedTableColumn'}
NUMERIC = {'int64', 'double', 'decimal'}
SAMPLE_ABOVE = 20_000
SAMPLE_SIZE = 5_000

MONTHS = ['JANUARY', 'FEBRUARY', 'MARCH', 'APRIL', 'MAY', 'JUNE', 'JULY', 'AUGUST', 'SEPTEMBER', 'OCTOBER',
          'NOVEMBER', 'DECEMBER']
MONTH_WORDS = set(MONTHS + [m[:3] for m in MONTHS] + ['SEPT'])
DAYS = ['MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY', 'SUNDAY']
DAY_WORDS = set(DAYS + [d[:3] for d in DAYS] + ['TUES', 'THUR', 'THURS'])
QUARTER_WORDS = {w for q in (1, 2, 3, 4) for w in (f'Q{q}', f'QTR {q}', f'QTR{q}', f'QUARTER {q}')}


def gazetteer():
    """The three lists from gazetteer.mjs, read from that file so there is one source."""
    src = (HERE / 'gazetteer.mjs').read_text(encoding='utf-8')
    out = {}
    for name in ('COUNTRIES', 'REGIONS', 'CITIES'):
        m = re.search(r'export const ' + name + r' = `([^`]*)`', src)
        out[name] = {x.strip() for x in m.group(1).split(',') if x.strip()}
    return out


GAZ = gazetteer()


def is_system_table(name):
    return bool(re.match(r'^(LocalDateTable_|DateTableTemplate_)', name or ''))


def is_blank(v):
    if v is None:
        return True
    if isinstance(v, float) and math.isnan(v):
        return True
    try:
        import pandas as pd
        if v is pd.NA or v is pd.NaT:
            return True
    except ImportError:
        pass
    return False


def nondigits(s):
    return sum(1 for ch in s if not ch.isdigit())


def postcode_like(u):
    L = len(u)
    if nondigits(u) == 0 and L in (4, 5):
        return True
    if L == 10 and u[5] == '-' and nondigits(u.replace('-', '')) == 0:
        return True
    if L in (6, 7, 8) and ' ' in u and nondigits(u) <= L - 3 and nondigits(u) >= L - 6 and u[:1].strip() != '':
        return True
    return False


def as_number(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def column_counts(values, rows, data_type):
    """The counts columnFactsQuery returns, from the column's values. `values` is the list of every row's value."""
    blanks = sum(1 for v in values if is_blank(v))
    distinct_all = {v for v in values if not is_blank(v)}
    distinct = len(distinct_all) + (1 if blanks else 0)          # DISTINCTCOUNT counts a blank as a value
    V = sorted(distinct_all, key=lambda x: (str(type(x)), x))
    sampled = len(V) > SAMPLE_ABOVE
    if sampled:                                                   # evenly spaced over the sorted values, as SAMPLE(n, ..., ASC)
        step = len(V) / SAMPLE_SIZE
        V = [V[int(i * step)] for i in range(SAMPLE_SIZE)]
    numeric, text = data_type in NUMERIC, data_type == 'string'
    c = {'rows': rows, 'distinct': distinct, 'blanks': blanks, 'nb': len(V)}
    zero = dict.fromkeys(['nonWhole', 'yearN', 'month12N', 'quarter4N', 'monthNameN', 'dayNameN', 'countryN',
                          'regionN', 'cityN', 'postcodeN', 'urlN', 'imageUrlN', 'emailN'], 0)
    c.update(zero)
    if numeric:
        nums = [as_number(v) for v in V]
        nums = [x for x in nums if x is not None]
        if data_type != 'int64':
            c['nonWhole'] = sum(1 for x in nums if x != math.floor(x))
        c['yearN'] = sum(1 for x in nums if 1900 <= x <= 2100 and x == math.floor(x))
        c['month12N'] = sum(1 for x in nums if 1 <= x <= 12 and x == math.floor(x))
        c['quarter4N'] = sum(1 for x in nums if 1 <= x <= 4 and x == math.floor(x))
    elif text:
        us = [str(v).strip().upper() for v in V]
        c['yearN'] = sum(1 for u in us if len(u) == 4 and nondigits(u) == 0 and u[:2] in ('19', '20'))
        c['quarter4N'] = sum(1 for u in us if u in QUARTER_WORDS)
        c['monthNameN'] = sum(1 for u in us if u in MONTH_WORDS)
        c['dayNameN'] = sum(1 for u in us if u in DAY_WORDS)
        c['countryN'] = sum(1 for u in us if u in GAZ['COUNTRIES'])
        c['regionN'] = sum(1 for u in us if u in GAZ['REGIONS'])
        c['cityN'] = sum(1 for u in us if u in GAZ['CITIES'])
        c['postcodeN'] = sum(1 for u in us if postcode_like(u))
        low = [str(v).lower() for v in V]
        c['urlN'] = sum(1 for s in low if s.startswith('http://') or s.startswith('https://') or s.startswith('www.'))
        c['imageUrlN'] = sum(1 for s in low if (s.startswith('http') or s.startswith('www.'))
                             and (s[-4:] in ('.png', '.jpg', '.gif', '.svg', '.bmp') or s[-5:] in ('.jpeg', '.webp')))
        raw = [str(v) for v in V]
        c['emailN'] = sum(1 for s in raw if '@' in s and '.' in s and ' ' not in s)
    return c, sampled


def rnd(x):
    return round(x * 1000) / 1000


def derive_measured(c, data_type, sampled):
    """deriveMeasured from derive.mjs, line for line."""
    rows, nb, distinct = c['rows'], c['nb'], c['distinct']
    share = lambda k: rnd(c[k] / nb) if nb > 0 else 0
    every = lambda k: nb > 0 and c[k] == nb
    return {
        'distinct': distinct,
        'blankShare': rnd(c['blanks'] / rows) if rows > 0 else 0,
        'wholeNumbers': c['nonWhole'] == 0 if data_type in NUMERIC else False,
        'uniqueOnEveryRow': rows > 0 and c['blanks'] == 0 and distinct == rows,
        'looksLikeYear': every('yearN') and nb >= 1,
        'looksLikeMonthName': every('monthNameN') and nb >= 2,
        'looksLikeMonthNumber': every('month12N') and 6 <= nb <= 12,
        'looksLikeDayName': every('dayNameN') and nb >= 2,
        'looksLikeQuarter': every('quarter4N') and 2 <= nb <= 4,
        'placeShare': {'country': share('countryN'), 'region': share('regionN'), 'city': share('cityN'),
                       'postcode': share('postcodeN')},
        'urlShare': share('urlN'), 'imageUrlShare': share('imageUrlN'), 'emailShare': share('emailN'),
        'oneToOneWith': [],
        'dictionaryBytes': None, 'dataBytes': None,
        'cardinality': distinct,
        'sampled': bool(sampled),
    }


def count_measure_refs(table, column, measures):
    """countMeasureRefs from derive.mjs."""
    col = ('[' + column.replace(']', ']]') + ']').lower()
    bare = (table + col).lower() if re.match(r'^[A-Za-z_][A-Za-z0-9_]*$', table) else None
    quoted = ("'" + table.replace("'", "''") + "'" + col).lower()
    n = 0
    for expr in measures:
        e = re.sub(r'\s+\[', '[', str(expr or '').lower())
        if quoted in e or (bare and bare in e):
            n += 1
    return n


def shape(conn):
    """shapeModel from derive.mjs, over the file's own metadata tables."""
    cols = conn.execute('SELECT ID, TableID, ExplicitName, InferredName, ExplicitDataType, InferredDataType, DataCategory,'
                        ' IsHidden, IsKey, SummarizeBy, Type, FormatString, SortByColumnID, IsAvailableInMDX FROM [Column]').fetchall()
    keys = ['ID', 'TableID', 'ExplicitName', 'InferredName', 'ExplicitDataType', 'InferredDataType', 'DataCategory',
            'IsHidden', 'IsKey', 'SummarizeBy', 'Type', 'FormatString', 'SortByColumnID', 'IsAvailableInMDX']
    cols = [dict(zip(keys, r)) for r in cols]
    by_id = {c['ID']: c for c in cols}
    name = lambda c: c['ExplicitName'] or c['InferredName']
    role = {}
    for fc, fcard, tc, tcard in conn.execute('SELECT FromColumnID, FromCardinality, ToColumnID, ToCardinality FROM [Relationship]'):
        def put(i, v):
            if role.get(i) != 'one':
                role[i] = v
        put(fc, 'one' if fcard == 1 else 'many')
        put(tc, 'many' if tcard == 2 else 'one')
    measures = conn.execute('SELECT TableID, Expression FROM [Measure]').fetchall()
    exprs = [m[1] for m in measures]
    out = []
    # SystemFlags 0 is a table the author sees; the rest are the engine's own hierarchy and relationship tables.
    for tid, tname in conn.execute('SELECT ID, Name FROM [Table] WHERE SystemFlags = 0').fetchall():
        if is_system_table(tname):
            continue
        tcols = [c for c in cols if c['TableID'] == tid and COLUMN_TYPE.get(c['Type']) != 'rowNumber']
        if not tcols:
            continue
        shaped = []
        for c in tcols:
            dt = DATA_TYPE.get(c['ExplicitDataType']) or DATA_TYPE.get(c['InferredDataType']) or 'unknown'
            if dt == 'automatic':
                dt = DATA_TYPE.get(c['InferredDataType'], 'unknown')
            sb = by_id.get(c['SortByColumnID']) if c['SortByColumnID'] else None
            ct = COLUMN_TYPE.get(c['Type'])
            shaped.append({
                'id': c['ID'], 'name': name(c), 'dataType': dt,
                'isCalculated': ct in ('calculated', 'calculatedTableColumn'),
                'isHidden': bool(c['IsHidden']), 'summarizeBy': SUMMARIZE_BY.get(c['SummarizeBy'], 'default'),
                'dataCategory': c['DataCategory'] or None, 'sortByColumn': name(sb) if sb else None,
                'formatString': c['FormatString'] if isinstance(c['FormatString'], str) and c['FormatString'] else None,
                'isAvailableInMdx': None if c['IsAvailableInMDX'] is None else bool(c['IsAvailableInMDX']),
                'isKey': bool(c['IsKey']), 'relationshipRole': role.get(c['ID'], 'none'),
                'usedByMeasures': count_measure_refs(tname, name(c), exprs),
                'usedInReports': None,
            })
        out.append({'name': tname, 'measures': sum(1 for m in measures if m[0] == tid), 'columns': shaped})
    return out


def link_one_to_one(t, frame):
    """oneToOneWith, same table: distinct(pair) == distinct(each), over every row."""
    measured = [c for c in t['columns'] if c.get('measured') and c['name'] in frame.columns]
    by_d = {}
    for c in measured:
        d = c['measured']['distinct']
        if d > 1:
            by_d.setdefault(d, []).append(c)
    for group in by_d.values():
        for i in range(len(group)):
            for j in range(i + 1, len(group)):
                a, b = group[i], group[j]
                if a['measured']['uniqueOnEveryRow'] and b['measured']['uniqueOnEveryRow']:
                    ok = True
                else:
                    pair = frame[[a['name'], b['name']]].astype(object).where(frame[[a['name'], b['name']]].notna(), None)
                    ok = len(set(map(tuple, pair.itertuples(index=False, name=None)))) == a['measured']['distinct']
                if ok:
                    a['measured']['oneToOneWith'].append(b['name'])
                    b['measured']['oneToOneWith'].append(a['name'])


def gather_one(path, key):
    from pbixray import PBIXRay
    p = PBIXRay(str(path))
    conn = p._metadata.source._db.conn
    tables = shape(conn)
    rec = {'key': key, 'reached': True, 'tables': len(tables), 'columns': sum(len(t['columns']) for t in tables),
           'measured': 0, 'failed': 0, 'sampled': 0}
    reasons = {}
    for t in tables:
        try:
            frame = p.get_table(t['name'])
        except Exception as e:                                    # an unreadable table: its columns stay unmeasured
            k = type(e).__name__
            reasons[k] = reasons.get(k, 0) + len(t['columns'])
            rec['failed'] += len(t['columns'])
            t['rows'] = None
            continue
        rows = len(frame)
        t['rows'] = rows
        for col in t['columns']:
            if col['dataType'] in ('binary', 'variant', 'unknown'):
                continue
            if col['name'] not in frame.columns:
                reasons['column not in the decoded table'] = reasons.get('column not in the decoded table', 0) + 1
                rec['failed'] += 1
                continue
            values = frame[col['name']].tolist()
            counts, sampled = column_counts(values, rows, col['dataType'])
            col['measured'] = derive_measured(counts, col['dataType'], sampled)
            rec['measured'] += 1
            rec['sampled'] += 1 if sampled else 0
        link_one_to_one(t, frame)
        del frame
    if reasons:
        rec['failureReasons'] = reasons
    for t in tables:
        for c in t['columns']:
            c.pop('id', None)
            if c.get('measured'):
                c['measured']['oneToOneWith'].sort()
    p.close()
    return rec, {'model': key, 'data': 'local', 'tables': tables}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--sources', required=True)
    ap.add_argument('--out', default=str(Path.home() / '.cache' / 'jev-replay-columns-more' / 'jev-columns-facts.json'))
    a = ap.parse_args()
    paths = [Path(x.strip()) for x in Path(a.sources).read_text().splitlines() if x.strip()]
    summary, models, keymap = [], [], {}
    for i, path in enumerate(paths):
        key = f'p{i}'
        keymap[key] = {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
        try:
            rec, model = gather_one(path, key)
            summary.append(rec)
            models.append(model)
        except Exception as e:
            summary.append({'key': key, 'reached': False, 'error': type(e).__name__})
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps({'kind': 'jev-columns-facts', 'version': 1, 'summary': summary, 'models': models}))
    out.with_name(out.stem + '-keymap.json').write_text(json.dumps(keymap, indent=1))
    for r in summary:
        print(json.dumps({k: v for k, v in r.items()}))
    print(f"models reached {sum(1 for r in summary if r.get('reached'))} of {len(paths)}; "
          f"columns measured {sum(r.get('measured', 0) for r in summary)}")


if __name__ == '__main__':
    sys.exit(main())
