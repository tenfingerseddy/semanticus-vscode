#!/usr/bin/env python3
"""Judge written tabular formats against the held-out semantic labels."""

import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).parent


def policy_for_rule(rule):
    """The labels' controlled English rules are the source of policy."""
    clues = (
        ('four-digit calendar or fiscal year', 'year'),
        ('numeric identifier as ungrouped', 'key'),
        ('exactly five ungrouped', 'fixed5'),
        ('exactly six ungrouped', 'fixed6'),
        ('whole-number count', 'count'),
        ('dollar amount with exactly two', 'money'),
        ('fractional value as a percent', 'percent'),
        ('noncurrency quantity with two or three', 'decimal'),
        ('exchange rate with four to six', 'fx'),
        ('dimensionless multiple with two or three', 'ratio'),
        ('Show days with one or two', 'days'),
        ('Show seconds with one or two', 'seconds'),
        ('Show milliseconds with one or two', 'milliseconds'),
        ('complete calendar date', 'date'),
        ('This is text', 'unsure'),
        ('does not identify a single display unit', 'unsure'),
    )
    hits = [policy for clue, policy in clues if clue in rule]
    if len(hits) != 1:
        raise ValueError(f'Uncoded or ambiguous rule_in_words: {rule!r}')
    return hits[0]


def split_sections(fmt):
    """Split custom-format sections, preserving semicolons inside literals."""
    quote = False
    escaped = False
    out = []
    sections = []
    for ch in fmt:
        if escaped:
            out.append(ch)
            escaped = False
        elif ch == '\\':
            out.append(ch)
            escaped = True
        elif ch == '"':
            quote = not quote
            out.append(ch)
        elif ch == ';' and not quote:
            sections.append(''.join(out).strip())
            out = []
        else:
            out.append(ch)
    sections.append(''.join(out).strip())
    return sections


def components(section):
    """Return the numeric picture and explicit literals of one section."""
    s = section.strip()
    literals = []
    skeleton = []
    i = 0
    while i < len(s):
        if s[i] == '"':
            j = i + 1
            literal = []
            while j < len(s):
                if s[j] == '"':
                    if j + 1 < len(s) and s[j + 1] == '"':
                        literal.append('"')
                        j += 2
                        continue
                    break
                literal.append(s[j])
                j += 1
            if j == len(s):
                return None
            literals.append(''.join(literal))
            i = j + 1
        elif s[i] == '\\' and i + 1 < len(s):
            literals.append(s[i + 1])
            i += 2
        elif s[i] == '[':
            j = s.find(']', i + 1)
            if j < 0:
                return None
            # Color tags and locale tags do not determine numeric scale.
            tag = s[i + 1:j]
            if tag.lower() not in ('red', 'black', 'blue', 'green', 'white') and not tag.startswith('$-'):
                return None
            i = j + 1
        else:
            skeleton.append(s[i])
            i += 1
    return ''.join(skeleton).strip(), ''.join(literals)


def numeric_picture(fmt, unit=None):
    parts = components(fmt)
    if parts is None:
        return None
    picture, literal = parts
    if unit == 'money':
        if picture.count('$') + literal.count('$') != 1:
            return None
        picture = picture.replace('$', '')
        literal = literal.replace('$', '')
    elif unit == 'percent':
        if picture.count('%') != 1 or '%' in literal:
            return None
        picture = picture.replace('%', '')
    elif unit in ('ratio', 'days', 'seconds', 'milliseconds'):
        suffix = {'ratio':'x', 'days':'days', 'seconds':'s', 'milliseconds':'ms'}[unit]
        if literal.strip().lower() != suffix:
            return None
    elif literal.strip():
        return None
    if unit not in ('ratio', 'days', 'seconds', 'milliseconds') and literal.strip():
        return None
    if '$' in picture or '%' in picture:
        return None
    picture = picture.strip()
    # A negative section is separate; the positive section may have optional sign or parentheses.
    if picture.startswith('(') and picture.endswith(')'):
        picture = picture[1:-1]
    picture = picture.lstrip('+-').strip()
    if not re.fullmatch(r'[#,0?]+(?:\.[#0?]+)?', picture):
        return None
    if picture.endswith(',') or ',,' in picture:
        return None
    if picture.count('.') > 1:
        return None
    whole, _, fraction = picture.partition('.')
    if whole.count('0') == 0:
        return None
    if fraction and any(ch != '0' for ch in fraction):
        return None
    return whole, fraction


def classify_section(policy, section):
    if policy == 'date':
        parts = components(section)
        if parts is None:
            return False
        s, literal = parts
        s = s.lower()
        # Require a four-digit year, a month field, and a day field.
        valid = bool(re.search(r'(?<!y)yyyy(?!y)', s) and re.search(r'(?<!m)m{1,4}(?!m)', s)
                     and re.search(r'(?<!d)d{1,4}(?!d)', s))
        extra = re.sub(r'[ymdhsap]+', '', s)
        return (valid and not re.search(r'[a-z0-9%$#]', extra)
                and not re.search(r'[a-z0-9]', literal.lower()))
    unit = policy if policy in ('money', 'percent', 'ratio', 'days', 'seconds', 'milliseconds') else None
    parsed = numeric_picture(section, unit)
    if parsed is None:
        return False
    whole, fraction = parsed
    grouped = ',' in whole
    if policy == 'year':
        return not grouped and not fraction and bool(re.fullmatch(r'0{1,4}', whole))
    if policy == 'key':
        return not grouped and not fraction and bool(re.fullmatch(r'0+', whole))
    if policy == 'fixed5':
        return whole == '00000' and not fraction
    if policy == 'fixed6':
        return whole == '000000' and not fraction
    if policy == 'count':
        return not fraction
    if policy == 'money':
        return len(fraction) == 2
    if policy == 'percent':
        return not grouped and len(fraction) in (1, 2)
    if policy == 'decimal':
        return len(fraction) in (2, 3)
    if policy == 'fx':
        return not grouped and 4 <= len(fraction) <= 6
    if policy == 'ratio':
        return len(fraction) in (2, 3)
    if policy in ('days', 'seconds', 'milliseconds'):
        return len(fraction) in (1, 2)
    raise ValueError(f'Unhandled policy {policy}')


def classify(label, fmt):
    policy = policy_for_rule(label['rule_in_words'])
    if policy == 'unsure' or label['unsure']:
        return 'U'
    if not isinstance(fmt, str) or not fmt.strip():
        return 'WRONG'
    sections = split_sections(fmt)
    if len(sections) > 4 or (len(sections) == 4 and sections[3] != '@'):
        return 'WRONG'
    return 'RIGHT' if all(classify_section(policy, s) for s in sections[:3]) else 'WRONG'


def load_indexes():
    base = {x['id']: x for x in json.loads((HERE / 'labels.json').read_text())}
    after = dict(base)
    changes = json.loads((HERE / 'changes.json').read_text())
    for change in changes:
        if change['tmdl_after'] == 'deleted' or 'new_id' in change:
            after.pop(change['target'], None)
        for label in change['labels_after']:
            after[label['id']] = label
    return base, after, changes


def judge(row, base, after):
    index = after if row.get('after_changes') is True else base
    label = index.get(row.get('id'))
    return 'UNKNOWN_ID' if label is None else classify(label, row.get('format'))


def self_test():
    base, after, changes = load_indexes()
    assert len(base) >= 80
    assert len(changes) >= 8
    tested = 0
    for index in (base, after):
        for ident, label in index.items():
            for expected, examples in (('RIGHT', label['right']), ('WRONG', label['wrong'])):
                for fmt in examples:
                    actual = classify(label, fmt)
                    assert actual == expected, (ident, fmt, actual, expected)
                    tested += 1
            if label['unsure']:
                assert classify(label, '0.00') == 'U', ident
                tested += 1
    for ch in changes:
        if ch['tmdl_after'] == 'deleted' or 'new_id' in ch:
            assert ch['target'] not in after
        if 'new_id' in ch:
            assert ch['new_id'] not in base and ch['new_id'] in after
    assert classify(base['SalesOps|Calendar|column|Calendar Year'], '#,0') == 'WRONG'
    assert classify(base['SalesOps|Sales|measure|Order Count'], '#,0') == 'RIGHT'
    assert classify(after['SalesOps|Sales|measure|Discount Rate'], '0.0%') == 'WRONG'
    assert classify(base['SalesOps|Sales|measure|Revenue'], '$0.00;($0.00)') == 'RIGHT'
    assert classify(base['SalesOps|Sales|measure|Revenue'], '$0.00;0') == 'WRONG'
    assert classify(base['SalesOps|Calendar|column|Date'], '"yyyy-MM-dd"') == 'WRONG'
    assert classify(base['SalesOps|Sales|measure|Order Count'], '0,,') == 'WRONG'
    print(f'PASS: {tested} format examples across {len(base)} baseline labels, {len(changes)} changes')


def main(argv):
    if len(argv) == 2 and argv[1] == '--self-test':
        self_test()
        return 0
    if len(argv) != 2:
        print('Usage: python3 judge.py <written.json> | --self-test', file=sys.stderr)
        return 2
    rows = json.loads(Path(argv[1]).read_text())
    if not isinstance(rows, list):
        raise ValueError('Input must be a JSON list')
    base, after, _ = load_indexes()
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError('Each list element must be an object')
        print(f'{row.get("id", "<missing id>")}\t{judge(row, base, after)}')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
