# TUNING SET SPLIT (r-jev-unsure, 28 Sep). Expands each contoso-pair question into its raw (contoso) and prepared
# (contoso-ai) rows, then splits the questions into a tuning half and a held-out half, stratified by model and type,
# with a fixed seed, BEFORE any run. Both arms of one contoso question always land in the same half.
#   python3 split.py <reviewed.json> <outdir>
import json, random, sys, collections, os

src, outdir = sys.argv[1], sys.argv[2]
items = json.load(open(src))['items']
groups = collections.defaultdict(list)
for it in items:
    groups[(it['model'], it['type'])].append(it)
rng = random.Random(20260928)
half = {'tune': [], 'held': []}
flip = 0
for key in sorted(groups):
    g = sorted(groups[key], key=lambda x: x['id'])
    rng.shuffle(g)
    for i, it in enumerate(g):
        # Alternate, with an odd group's extra item going to the halves in turn so the halves stay even overall.
        side = 'tune' if (i + flip) % 2 == 0 else 'held'
        half[side].append(it)
    if len(g) % 2: flip ^= 1

def expand(it):
    base = {k: v for k, v in it.items() if k != 'model'}
    base['expect'] = it['accept'] if it['accept'] == 'decline' else it['accept'][0]
    if it['model'] != 'contoso-pair':
        return [dict(base, model=it['model'])]
    n = it['id'].split('-')[1]
    return [dict(base, id='ncr-' + n, model='contoso', pair=it['id']), dict(base, id='nca-' + n, model='contoso-ai', pair=it['id'])]

for side, its in half.items():
    rows = [r for it in sorted(its, key=lambda x: x['id']) for r in expand(it)]
    doc = {'point': 'query.summarize', 'set': f'natural-{side}',
           'note': ('Natural business questions, every reasonable reading accepted, reviewed by an independent labeller, split '
                    'before any run (seed 20260928, stratified by model and type). ' +
                    ('TUNING half: fixes may be tried and measured on it.' if side == 'tune' else
                     'HELD-OUT half: run once on the current reader and once at the very end on the final patch. Nothing is tuned on it.')),
           'items': rows}
    json.dump(doc, open(os.path.join(outdir, f'natural-{side}-labels.json'), 'w'), indent=1)
    c = collections.Counter((r['model'], r['type']) for r in rows)
    print(side, len(its), 'questions,', len(rows), 'rows;', dict(collections.Counter(r['model'] for r in rows)))
