#!/usr/bin/env python3
"""Score the V1i held-out run: every format written, judged by Sol's judge.py (unedited), and the stop rule.

Usage: score.py <set folder> <runs folder> <out folder>

Pre-registered (protocol.md):
  SAFETY   every format any path wrote, in every run, judged against the object's label after the changes. A WRONG write
           fails the stop rule; it is attributed to "after the person's tick" when the object carried a tick the product
           recorded as the person's, else "automatic".
  RECALL   primary (the brief's "objects with a RIGHT format that a rule proves"): objects blank before the writes, not U,
           for which the format fill rule proves a whole-number fill at that moment (FormatFill.MayFill(obj, "#,0"), read
           before the writes) and "#,0" is RIGHT by the judge; the share of them that got a RIGHT format written or
           suggested. Secondary: the same over Sol's provable_from_model objects (and over every non-U object), reported.
  U        what was written or suggested on the objects Sol marked unsure.
"""
import collections, importlib.util, json, sys
from pathlib import Path

SET, RUNS, OUT = map(Path, sys.argv[1:4])
spec = importlib.util.spec_from_file_location('judge', SET / 'judge.py')
judge = importlib.util.module_from_spec(spec); spec.loader.exec_module(judge)
BASE, AFTER, CHANGES = judge.load_indexes()


def verdict(obj_id, fmt, after=True):
    return judge.judge({'id': obj_id, 'format': fmt, 'after_changes': after}, BASE, AFTER)


def touched_ids():
    t = set()
    for ch in CHANGES:
        t.add(ch['target'])
        if ch.get('new_id'): t.add(ch['new_id'])
        for l in ch['labels_after']: t.add(l['id'])
    return t


TOUCHED = touched_ids()
SUGGEST_PHASES_ANY = ('S2', 'S3', 'S4')          # after every change
SUGGEST_PHASES_UNTOUCHED = ('S0', 'S1')          # only for objects no change touched


def suggestions(run):
    """(id, format, phase, src) for every format the product put in front of someone for an object."""
    out = []
    for key in ('S0', 'S1', 'S2', 'S3slow', 'S4'):
        for s in run.get(key) or []:
            if s.get('src') == 'model_plan_rows' or not s.get('id') or not isinstance(s.get('after'), str) or not s['after'].strip():
                continue
            if s.get('src') == 'change_plan' and s.get('status') in ('applied', 'failed', 'skipped', 'rejected'):
                continue
            if s.get('src') == 'model_plan' and s.get('op') == 'bpa_fix' and not _is_format_bpa(run, s):
                continue
            phase = s.get('phase') or key
            if phase in SUGGEST_PHASES_UNTOUCHED and s['id'] in TOUCHED:
                continue
            out.append((s['id'], s['after'], phase, s['src']))
    return out


def _is_format_bpa(run, line):
    pid = line.get('planItemId')
    for key in ('S0', 'S1', 'S2', 'S3slow', 'S4'):
        for s in run.get(key) or []:
            if s.get('src') == 'change_plan' and s.get('planItemId') == pid:
                return 'formatstring' in (s.get('target') or '').lower()
    return False


def ticked_by_person(run):
    return {t['id'] for t in run.get('ticks') or [] if t.get('keptByPerson') is True and t.get('status') in ('approved', 'applied') and t.get('id')}


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    runs = [json.loads(p.read_text()) for p in sorted(RUNS.glob('*.json'))]
    writes_rows, wrong_rows, run_rows, errors = [], [], [], []
    per_object = collections.defaultdict(dict)
    recall_rows = []
    for r in runs:
        tag = f"{r['model']}-{r['door']}-{r['mode']}-{r['path']}"
        if r.get('errors'):
            errors.append((tag, r['errors']))
        start = r.get('start') or {}
        final = r.get('final') or {}
        ticks = ticked_by_person(r)
        run_writes = []
        for p in r.get('paths') or []:
            for w in p['writes']:
                oid = w['id']
                kind = oid.split('|')[2] if oid.count('|') == 3 else '?'
                after = w.get('after')
                if kind == 'calcitem' or after is None:
                    run_writes.append((p['path'], oid, w.get('before'), after, 'NOTE'))
                    continue
                fmt = after.split(' fse=')[0]
                v = verdict(oid, fmt) if fmt.strip() else 'CLEARED'
                who = 'after the person\'s tick' if oid in ticks else 'automatic'
                row = (tag, p['path'], oid, w.get('before'), after, v, who)
                writes_rows.append(row)
                run_writes.append((p['path'], oid, w.get('before'), after, v))
                if v == 'WRONG':
                    wrong_rows.append(row)
        # recall, on the whole-flow runs and the isolated ones alike (the stop rule reads the whole-flow runs)
        may = r.get('mayFillBeforeWrites') or {}
        before_writes = r.get('beforeWrites') or {}
        sugg = suggestions(r)
        right_sugg = collections.defaultdict(list)
        for (oid, fmt, phase, src) in sugg:
            if verdict(oid, fmt) == 'RIGHT':
                right_sugg[oid].append((phase, src, fmt))
        right_written = {oid for (_, oid, b, a, v) in run_writes if v == 'RIGHT'}
        # every object in the model before the writes (ids as they stand after the changes)
        objs = [oid for oid in before_writes if oid.count('|') == 3 and oid.split('|')[2] in ('measure', 'column')]
        den = {'rule': [], 'provable': [], 'all': []}
        for oid in objs:
            label = AFTER.get(oid)
            if label is None or label['unsure'] or judge.policy_for_rule(label['rule_in_words']) == 'unsure':
                continue
            blank = not (before_writes.get(oid) or '').strip()
            if not blank:
                continue
            if label['right']:
                den['all'].append(oid)
                if label['provable_from_model']:
                    den['provable'].append(oid)
            if may.get(oid) is True and verdict(oid, '#,0') == 'RIGHT':
                den['rule'].append(oid)
        def got(oid, with_advice=True):
            if oid in right_written: return 'written'
            s = [x for x in right_sugg.get(oid, []) if with_advice or x[1] != 'bpa_advice']
            return 'suggested' if s else None
        rec = {'tag': tag, 'model': r['model'], 'door': r['door'], 'mode': r['mode'], 'path': r['path']}
        for k, ids in den.items():
            hit = [o for o in ids if got(o)]
            hit_na = [o for o in ids if got(o, with_advice=False)]
            rec[k] = (len(hit), len(ids)); rec[k + '_noadvice'] = (len(hit_na), len(ids))
            rec[k + '_missed'] = [o for o in ids if not got(o)]
        recall_rows.append(rec)
        # the object-by-object table, whole-flow runs only
        if r['path'] == 'all':
            col = f"{r['mode']}/{r['door']}"
            for oid in set(objs) | set(start):
                if oid.count('|') != 3 or oid.split('|')[2] not in ('measure', 'column'):
                    continue
                w = [x for x in run_writes if x[1] == oid and x[4] not in ('NOTE',)]
                cell = ''
                if w:
                    last = w[-1]
                    cell = f"{last[3]!r}={last[4][0]}"
                elif oid in right_sugg:
                    cell = 'sugg-R'
                else:
                    cell = '-'
                per_object[oid][col] = cell
        run_rows.append((tag, len(run_writes), sum(1 for x in run_writes if x[4] == 'WRONG'), len(r.get('ticks') or []), r.get('budget')))

    # ---- outputs -----------------------------------------------------------------------------------------------------
    with open(OUT / 'writes.tsv', 'w') as f:
        f.write('run\tpath\tobject\tbefore\tafter\tverdict\twho\n')
        for row in writes_rows: f.write('\t'.join(map(str, row)) + '\n')
    with open(OUT / 'recall.tsv', 'w') as f:
        f.write('run\trule(hit/den)\trule_noadvice\tprovable\tprovable_noadvice\tall\tall_noadvice\trule_missed\n')
        for r in recall_rows:
            f.write(f"{r['tag']}\t{r['rule'][0]}/{r['rule'][1]}\t{r['rule_noadvice'][0]}/{r['rule_noadvice'][1]}\t{r['provable'][0]}/{r['provable'][1]}\t{r['provable_noadvice'][0]}/{r['provable_noadvice'][1]}\t{r['all'][0]}/{r['all'][1]}\t{r['all_noadvice'][0]}/{r['all_noadvice'][1]}\t{','.join(r['rule_missed'])}\n")
    cols = sorted({c for d in per_object.values() for c in d})
    with open(OUT / 'objects.tsv', 'w') as f:
        f.write('object\tlabel\t' + '\t'.join(cols) + '\n')
        for oid in sorted(per_object):
            lab = AFTER.get(oid) or BASE.get(oid)
            pol = ('U' if lab and lab['unsure'] else judge.policy_for_rule(lab['rule_in_words'])) if lab else '?'
            f.write(f"{oid}\t{pol}{'' if not lab or lab['provable_from_model'] else '(np)'}\t" + '\t'.join(per_object[oid].get(c, '') for c in cols) + '\n')
    summary = {
        'runs': len(runs),
        'writes': len(writes_rows),
        'verdicts': dict(collections.Counter(r[5] for r in writes_rows)),
        'wrong': [dict(zip(('run', 'path', 'object', 'before', 'after', 'verdict', 'who'), r)) for r in wrong_rows],
        'errors': errors,
    }
    by_mode = collections.defaultdict(lambda: [0, 0, 0, 0, 0, 0, 0, 0])
    for r in recall_rows:
        if r['path'] != 'all': continue
        k = f"{r['mode']}/{r['door']}"
        for i, key in enumerate(('rule', 'provable', 'all', 'rule_noadvice')):
            by_mode[k][2 * i] += r[key][0]; by_mode[k][2 * i + 1] += r[key][1]
    summary['recall_by_mode'] = {k: {'rule': f"{v[0]}/{v[1]}", 'rule_pct': round(100 * v[0] / v[1], 1) if v[1] else None,
                                     'rule_noadvice': f"{v[6]}/{v[7]}", 'provable': f"{v[2]}/{v[3]}", 'provable_pct': round(100 * v[2] / v[3], 1) if v[3] else None,
                                     'all': f"{v[4]}/{v[5]}"} for k, v in sorted(by_mode.items())}
    (OUT / 'summary.json').write_text(json.dumps(summary, indent=1))
    print(json.dumps({k: summary[k] for k in ('runs', 'writes', 'verdicts')}, indent=None))
    print('WRONG writes:', len(wrong_rows))
    for w in wrong_rows: print('  ', w)
    print('recall by mode (whole-flow runs):')
    for k, v in summary['recall_by_mode'].items(): print('  ', k, v)
    print('errors in', len(errors), 'runs')
    for t, e in errors[:10]: print('  ', t, str(e)[:300])


if __name__ == '__main__':
    main()
