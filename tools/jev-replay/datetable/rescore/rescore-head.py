# The date table rescore that decides the switch (T268, M4 point 3), kept beside the replay it rescores.
#
# The joint verifier wrote this on 27 September to rescore the RECORDED round 3 answers against the rule as it ships and
# against the population the shipped product asks, without a new live run. Its output is rescore-head.out, the numbers on
# docs/redesign/jev-datetable-replay.md ("The population the product asks").
#
# Inputs:
#   ../key.json          the round 3 answer key (in this repository)
#   head-rule.json       what the engine did for each of the 64 labelled models on 27 September: whether DATE-MARK fires,
#                        the table PlanDateMark names, and whether DateTableFamily.Prepare asks (the verifier's dump; the
#                        asked set is pinned against the engine today by ../asked-population.json and the non-live test
#                        JevDateTableReplay.The_models_the_product_asks_about_are_the_population_written_beside_the_scorer)
#   round3-results.json  the recorded round 3 answers, which live OUTSIDE the repository by design: the first argument, or
#                        ~/.cache/jev-replay-datetable/round3-results.json, where the live replay writes them
#
# Run: python3 tools/jev-replay/datetable/rescore/rescore-head.py [path/to/round3-results.json]
import json, math, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ANSWERS = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.expanduser('~'), '.cache', 'jev-replay-datetable', 'round3-results.json')

def mcnemar(b,c):
    n=b+c
    if n==0: return 1.0
    k=min(b,c); return min(1.0, 2*sum(math.comb(n,i) for i in range(k+1))/2**n)
key={k['id']:k for k in json.load(open(os.path.join(HERE, '..', 'key.json')))['items']}
r3={x['id']:x for x in json.load(open(ANSWERS))['rows']}
head={x['id']:x for x in json.load(open(os.path.join(HERE, 'head-rule.json')))}
tk=lambda t: 'generate' if t=='generate' else 'table:'+t
# sanity: head vs round3 on ruleNeeded
diffNeed=[i for i in r3 if r3[i]['ruleNeeded']!=head[i]['usesTi']]
diffRule=[(i,r3[i]['ruleTable'],head[i]['ruleTableHead']) for i in r3 if r3[i]['ruleTable']!=head[i]['ruleTableHead']]
print('ruleNeeded differs round3 vs head:',diffNeed)
print('ruleTable differs round3 vs head:',diffRule)
print('dateMarkFires==usesTi&&!marked for all:', all(head[i]['dateMarkFires']==(head[i]['usesTi'] and not head[i]['marked']) for i in head))
scored=[i for i in r3 if not key[i]['unverifiable'] and not key[i]['ambiguous']]
# 1) the replay's own table population, with the head rule
offered=[i for i in scored if key[i]['needed'] and any(o.startswith('table:') for o in (r3[i].get('options') or []))]
def tab(ids, rulefield):
    pro=rule=b=c=0
    for i in ids:
        k=tk(key[i]['table']); x=r3[i]
        sure = x['jevTable'] is not None and x['jevTableConfidence']>=0.85
        ru = head[i]['ruleTableHead'] if rulefield=='head' else x['ruleTable']
        p = x['jevTable'] if sure else ru
        pr=p==k; rr=ru==k
        pro+=pr; rule+=rr; b+=pr and not rr; c+=(not pr) and rr
    return len(ids),pro,rule,b,c,mcnemar(b,c)
print('replay population (key needed & offered), round3 rule:', tab(offered,'r3'))
print('replay population (key needed & offered), head rule  :', tab(offered,'head'))
# 2) the shipped population: the head asks (Prepare.Ask != null) -> Jev path; elsewhere the rule row only
asked=[i for i in scored if head[i]['askHead']]
print('models the head asks Jev about:', len(asked), asked)
print('  of which key-needed & offered:', len([i for i in asked if i in offered]))
print('shipped population, head rule:', tab([i for i in asked if i in offered],'head'))
# 3) full shipped behaviour over all 33 replay-population models: Pro = Jev only where asked & sure, else rule
pro=rule=b=c=0
for i in offered:
    k=tk(key[i]['table']); x=r3[i]
    ru=head[i]['ruleTableHead'] if head[i]['dateMarkFires'] else None   # no DATE-MARK -> no date row at all
    sure = head[i]['askHead'] and x['jevTable'] is not None and x['jevTableConfidence']>=0.85
    p = x['jevTable'] if sure else ru
    pr=p==k; rr=ru==k
    pro+=pr; rule+=rr; b+=pr and not rr; c+=(not pr) and rr
print('shipped Pro path vs shipped rule on all 33:', dict(n=len(offered),pro=pro,rule=rule,b=b,c=c,p=mcnemar(b,c)))
print('head question text:', head['ns-1']['questionsHead'])
