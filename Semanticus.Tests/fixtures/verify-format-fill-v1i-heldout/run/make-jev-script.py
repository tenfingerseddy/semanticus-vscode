#!/usr/bin/env python3
"""The scripted Jev's answers for the 'label' family of modes (label, slow, silent), fixed before the run.

For every label (base, and after each change) the family is read off the label's policy, as Sol's judge reads it:
count -> whole_number; money -> currency; percent -> percent; decimal, fx, ratio, days, seconds, milliseconds -> decimal;
date -> date_or_time; year, key, fixed5, fixed6, and every unsure label -> text_or_none.
Usage: make-jev-script.py <set folder> <out.json>
"""
import importlib.util, json, sys
from pathlib import Path

FAMILY = {
    'count': 'whole_number', 'money': 'currency', 'percent': 'percent',
    'decimal': 'decimal', 'fx': 'decimal', 'ratio': 'decimal', 'days': 'decimal', 'seconds': 'decimal', 'milliseconds': 'decimal',
    'date': 'date_or_time',
    'year': 'text_or_none', 'key': 'text_or_none', 'fixed5': 'text_or_none', 'fixed6': 'text_or_none', 'unsure': 'text_or_none',
}

def main(set_dir, out):
    spec = importlib.util.spec_from_file_location('judge', Path(set_dir) / 'judge.py')
    judge = importlib.util.module_from_spec(spec); spec.loader.exec_module(judge)
    base, after, _ = judge.load_indexes()
    def fam(label):
        return 'text_or_none' if label['unsure'] else FAMILY[judge.policy_for_rule(label['rule_in_words'])]
    doc = {'base': {k: fam(v) for k, v in sorted(base.items())}, 'after': {k: fam(v) for k, v in sorted(after.items())}}
    Path(out).write_text(json.dumps(doc, indent=1, sort_keys=True) + '\n')
    print(f"{len(doc['base'])} base and {len(doc['after'])} after answers written to {out}")

if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
