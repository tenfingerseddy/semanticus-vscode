# The third run's census (README, "Third run"): run from the private cache dir that holds repos/ and the two earlier runs.
# Fresh-set census: every .pbix that decodes, minus files already used (by SHA-256) and near copies
# (more than 60% shared table+column names) of a used model or of a larger fresh one already kept.
import glob, os, json, hashlib, sys
from pbixray import PBIXRay
H = os.path.expanduser('~/.cache')
used_sha = set()
for f in glob.glob(H + '/jev-replay-columns-more/samples/**/*.pbix', recursive=True) + glob.glob(H + '/jev-replay-columns-more/more/**/*.pbix', recursive=True):
    used_sha.add(hashlib.sha256(open(f, 'rb').read()).hexdigest())
used_sigs = []
for ff in [H + '/jev-replay-columns-more/jev-columns-facts.json', H + '/jev-replay-columns/jev-columns-facts.json']:
    for m in json.load(open(ff))['models']:
        used_sigs.append(frozenset((t['name'], c['name']) for t in m['tables'] for c in t['columns']))
out = []
files = sorted(glob.glob(H + '/jev-replay-columns-fair/repos/**/*.pbix', recursive=True))
stat = {'files': len(files), 'sameSha': 0, 'decodeFail': 0, 'empty': 0}
seen = set()
for f in files:
    sha = hashlib.sha256(open(f, 'rb').read()).hexdigest()
    if sha in used_sha: stat['sameSha'] += 1; continue
    if sha in seen: stat.setdefault('dupShaFresh', 0); stat['dupShaFresh'] += 1; continue
    seen.add(sha)
    try:
        p = PBIXRay(f); conn = p._metadata.source._db.conn
        rows = conn.execute("""select t.Name, coalesce(c.ExplicitName,c.InferredName) from [Column] c join [Table] t on t.ID=c.TableID
          where c.Type<>3 and t.SystemFlags=0 and t.Name not like 'LocalDateTable%' and t.Name not like 'DateTableTemplate%'""").fetchall()
    except Exception as e:
        stat['decodeFail'] += 1; continue
    if not rows: stat['empty'] += 1; continue
    out.append(dict(f=f, sha=sha, sig=frozenset(rows), n=len(rows)))
ov = lambda a, b: len(a & b) / min(len(a), len(b))
kept = []
stat['nearUsed'] = stat['nearFresh'] = 0
for o in sorted(out, key=lambda o: (-o['n'], o['f'])):
    if any(ov(o['sig'], s) > 0.6 for s in used_sigs): stat['nearUsed'] += 1; continue
    if any(ov(o['sig'], k['sig']) > 0.6 for k in kept): stat['nearFresh'] += 1; continue
    kept.append(o)
stat['kept'] = len(kept); stat['columns'] = sum(k['n'] for k in kept)
print(json.dumps(stat))
open(H + '/jev-replay-columns-fair/sources.txt', 'w').write(''.join(k['f'] + '\n' for k in sorted(kept, key=lambda k: k['f'])))
