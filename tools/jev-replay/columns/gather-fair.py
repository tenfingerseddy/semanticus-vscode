#!/usr/bin/env python3
# Gather facts AND the fair answer key from the third run's fresh public sample models (README, "Third run").
#
#   python3 gather-fair.py --sources <list file> --out <facts file>
#
# Reads each .pbix with pbixray, exactly as gather-pbix.py does, and writes the same facts file (kind
# jev-columns-facts) with three additions per column:
#   - two more derived facts the rules read and Jev is given: usedAsSortTarget and looksLikePeriodNumber;
#   - `key`: the fair answer key from fairkey.py (labels and reason codes only);
#   - nothing else. The key is computed here, while the table's rows are in memory, and the values are dropped.
#
# A second private file, <out>-handcheck.json, holds up to eight distinct values per column for the 10% hand check
# of the key. It stays in the private cache dir with the rest, and nothing in it is ever sent to Jev.
import argparse
import hashlib
import importlib.util
import json
import math
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve().parent
_spec = importlib.util.spec_from_file_location('gather_pbix', HERE / 'gather-pbix.py')
G = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(G)
sys.path.insert(0, str(HERE))
import fairkey as K  # noqa: E402

HANDCHECK_VALUES = 8


def distinct_values(series):
    out = []
    seen = set()
    for v in series.tolist():
        if G.is_blank(v):
            continue
        k = v if not isinstance(v, float) else round(v, 12)
        if k in seen:
            continue
        seen.add(k)
        out.append(v)
    return out


def period_number(values, data_type):
    if data_type not in G.NUMERIC:
        return False
    xs = K._nums(values)
    return bool(xs) and K._whole(xs) and all(1 <= x <= 53 for x in xs) and 2 <= len(set(xs)) <= 53


def partner_map(frame, a, b):
    pair = frame[[a, b]].dropna().drop_duplicates()
    return dict(zip(pair[a].tolist(), pair[b].tolist()))


def gather_one(path, key):
    from pbixray import PBIXRay
    p = PBIXRay(str(path))
    conn = p._metadata.source._db.conn
    tables = G.shape(conn)
    rec = {'key': key, 'reached': True, 'tables': len(tables), 'columns': sum(len(t['columns']) for t in tables),
           'measured': 0, 'failed': 0, 'sampled': 0}
    hand = {}
    # A calculation group's column orders its items by the engine's own ordinal; it is not an author's column, so it is
    # left out of every population, like Power BI's automatic date tables.
    calc = {r[0] for r in conn.execute('SELECT t.Name FROM [Table] t JOIN [CalculationGroup] g ON g.TableID = t.ID')}
    rec['calculationGroupTables'] = len(calc)
    for t in tables:
        if t['name'] in calc:
            t['calculationGroup'] = True
            continue
        targets = {c['sortByColumn'] for c in t['columns'] if c.get('sortByColumn')}
        for c in t['columns']:
            c['usedAsSortTarget'] = c['name'] in targets
        try:
            frame = p.get_table(t['name'])
        except Exception as e:
            rec['failed'] += len(t['columns'])
            rec.setdefault('failureReasons', {})[type(e).__name__] = len(t['columns'])
            t['rows'] = None
            continue
        rows = len(frame)
        t['rows'] = rows
        values = {}
        for col in t['columns']:
            if col['dataType'] in ('binary', 'variant', 'unknown') or col['name'] not in frame.columns:
                if col['name'] not in frame.columns and col['dataType'] not in ('binary', 'variant', 'unknown'):
                    rec['failed'] += 1
                continue
            vals = frame[col['name']].tolist()
            counts, sampled = G.column_counts(vals, rows, col['dataType'])
            col['measured'] = G.derive_measured(counts, col['dataType'], sampled)
            values[col['name']] = distinct_values(frame[col['name']])
            col['measured']['looksLikePeriodNumber'] = period_number(values[col['name']], col['dataType'])
            rec['measured'] += 1
            rec['sampled'] += 1 if sampled else 0
        G.link_one_to_one(t, frame)
        by_name = {c['name']: c for c in t['columns']}
        for col in t['columns']:
            if not col.get('measured'):
                continue
            v = values[col['name']]
            k = {}
            hid, why = K.hidden_key(col, v, rows, t.get('measures') or 0)
            k['hidden'] = {'label': hid, 'why': why}
            if col['dataType'] in G.NUMERIC:
                lab, why = K.summarize_key(col, v, rows)
                k['summarize_by'] = {'label': lab, 'why': why}
            if col['dataType'] in G.NUMERIC or col['dataType'] == 'string':
                lab, why = K.datacat_key(col, v)
                k['data_category'] = {'label': lab, 'why': why}
            if col['dataType'] in G.NUMERIC or col['dataType'] == 'dateTime':
                lab, why = K.format_key(col, v)
                k['format_family'] = {'label': lab, 'why': why}
            partners = col['measured']['oneToOneWith']
            if col['dataType'] == 'string' and partners:
                pm = {pn: (by_name[pn]['dataType'], partner_map(frame, col['name'], pn)) for pn in partners if pn in frame.columns}
                need, ok, why = K.sort_key(col, v, pm)
                k['sort'] = {'needs': need, 'partners': ok, 'why': why}
                # For the hand check only: up to 15 of this column's values with each partner's value beside them.
                few = sorted(v, key=lambda x: str(x))[:15]
                hand[f"{key}|{t['name']}|{col['name']}|pairs"] = {pn: [[str(x), str(pm[pn][1].get(x))] for x in few] for pn in pm}
            col['key'] = k
            sample = sorted(v, key=lambda x: str(x))
            if len(sample) > HANDCHECK_VALUES:
                step = len(sample) / HANDCHECK_VALUES
                sample = [sample[int(i * step)] for i in range(HANDCHECK_VALUES)]
            hand[f"{key}|{t['name']}|{col['name']}"] = [x if isinstance(x, (int, str)) else
                                                         (None if isinstance(x, float) and math.isnan(x) else str(x))
                                                         for x in sample]
        del frame
    for t in tables:
        for c in t['columns']:
            c.pop('id', None)
            if c.get('measured'):
                c['measured']['oneToOneWith'].sort()
    p.close()
    return rec, {'model': key, 'data': 'local', 'tables': tables}, hand


def _safe_gather(path, key):
    try:
        return gather_one(path, key)
    except Exception as e:
        return {'key': key, 'reached': False, 'error': type(e).__name__}, None, {}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--jobs', type=int, default=6)
    ap.add_argument('--sources', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()
    paths = [Path(x.strip()) for x in Path(a.sources).read_text().splitlines() if x.strip()]
    summary, models, keymap, hand = [], [], {}, {}
    keys = [f'f{i}' for i in range(len(paths))]
    for key, path in zip(keys, paths):
        keymap[key] = {'path': str(path), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
    # One process per model; results are collected in source order, so the output does not depend on timing.
    with ProcessPoolExecutor(max_workers=a.jobs) as pool:
        futures = [pool.submit(_safe_gather, path, key) for key, path in zip(keys, paths)]
        for f in futures:
            rec, model, h = f.result()
            summary.append(rec)
            if model:
                models.append(model)
                hand.update(h)
    out = Path(a.out)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps({'kind': 'jev-columns-facts', 'version': 1, 'summary': summary, 'models': models}, default=str))
    out.with_name(out.stem + '-keymap.json').write_text(json.dumps(keymap, indent=1))
    out.with_name(out.stem + '-handcheck.json').write_text(json.dumps(hand, default=str))
    print(f"models reached {sum(1 for r in summary if r.get('reached'))} of {len(paths)}; "
          f"columns measured {sum(r.get('measured', 0) for r in summary)}; failed {sum(r.get('failed', 0) for r in summary)}")


if __name__ == '__main__':
    sys.exit(main())
