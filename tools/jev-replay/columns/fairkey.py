# The fair answer key for the third columns replay (26 September; Kane: "re-test fairly").
#
# WHY IT EXISTS. The second replay graded Jev against what each sample model's author happened to leave behind. An
# author's leftover is not a correct answer: a tag on the wrong column, a sum on a key, a sort by an id. This key
# says what is CORRECT, and it says it from the column's own data and the model's structure. The author's setting
# only decides a row where the data cannot (the method is in README.md, "Third run").
#
# WHERE IT RUNS. On this laptop, inside gather-fair.py, while a table's rows are in memory. The values are read here
# and dropped. Only the label, a short reason code and the author's setting leave this module, and they go to the
# private cache dir, never to Jev and never into the repository.
#
# Every function returns (label, why). `why` is a short code naming the clause that decided, so the hand check and
# the results note can count which clauses carry the key.
import math
import re

import geonamescache
import pycountry

# ---- reference lists (independent of the product's small gazetteer in gazetteer.mjs) --------------------------

_up = lambda s: re.sub(r'\s+', ' ', str(s).strip().upper())

COUNTRY = set()
COUNTRY_A2, COUNTRY_A3 = set(), set()
for _c in pycountry.countries:
    for _k in ('name', 'official_name', 'common_name'):
        if getattr(_c, _k, None):
            COUNTRY.add(_up(getattr(_c, _k)))
    COUNTRY_A2.add(_c.alpha_2)
    COUNTRY_A3.add(_c.alpha_3)
COUNTRY |= {'USA', 'US', 'U.S.', 'U.S.A.', 'UNITED STATES OF AMERICA', 'UK', 'U.K.', 'GREAT BRITAIN', 'BRITAIN',
            'ENGLAND', 'SCOTLAND', 'WALES', 'NORTHERN IRELAND', 'RUSSIA', 'SOUTH KOREA', 'NORTH KOREA', 'KOREA',
            'IRAN', 'SYRIA', 'VIETNAM', 'LAOS', 'BOLIVIA', 'VENEZUELA', 'TANZANIA', 'MOLDOVA', 'CZECH REPUBLIC',
            'THE NETHERLANDS', 'HOLLAND', 'IVORY COAST', 'TURKEY', 'TAIWAN', 'MACEDONIA', 'BRUNEI', 'CAPE VERDE',
            'SWAZILAND', 'BURMA', 'UAE', 'HONG KONG', 'MACAU', 'PALESTINE', 'KOSOVO', 'VATICAN', 'CONGO', 'DR CONGO',
            'DEMOCRATIC REPUBLIC OF THE CONGO', 'REPUBLIC OF THE CONGO', 'ST. LUCIA', 'SAINT LUCIA'}

_STATE_TYPES = {'State', 'Province', 'Territory', 'Canton', 'Land', 'Autonomous community', 'Federal district',
                'Emirate', 'Prefecture', 'Autonomous region', 'Country', 'Federal territory', 'Union territory',
                'Autonomous republic', 'Republic', 'Oblast', 'Voivodship', 'Federal entity', 'Governorate'}
_COUNTY_TYPES = {'County', 'Two-tier county', 'Metropolitan district', 'Unitary authority', 'Council area',
                 'London borough', 'District council area'}
STATE, STATE_A2, COUNTY = set(), set(), set()
for _s in pycountry.subdivisions:
    if _s.type in _STATE_TYPES and _s.parent_code is None:
        STATE.add(_up(_s.name))
        if _s.country_code in ('US', 'CA', 'AU', 'IN', 'MX', 'BR'):
            STATE_A2.add(_s.code.split('-', 1)[1])
    if _s.type in _COUNTY_TYPES:
        COUNTY.add(_up(_s.name))
_COMPASS = {'NORTH', 'SOUTH', 'EAST', 'WEST', 'CENTRAL', 'CENTRE', 'CENTER', 'MIDWEST', 'NORTHEAST', 'NORTHWEST',
            'SOUTHEAST', 'SOUTHWEST', 'NORTH EAST', 'NORTH WEST', 'SOUTH EAST', 'SOUTH WEST'}
STATE -= _COMPASS
STATE |= {'WASHINGTON DC', 'WASHINGTON D.C.', 'DISTRICT OF COLUMBIA', 'D.C.'}
_gc = geonamescache.GeonamesCache()
for _cty in _gc.get_us_counties():
    n = _up(_cty['name'])
    COUNTY.add(n)
    COUNTY.add(re.sub(r' (COUNTY|PARISH|BOROUGH|CENSUS AREA|MUNICIPALITY|CITY AND BOROUGH)$', '', n))
CITY = {_up(c['name']) for c in _gc.get_cities().values()}
CITY |= {_up(a) for c in _gc.get_cities().values() for a in (c.get('alternatenames') or []) if a and a.isascii() and len(a) > 3}
CITY -= _COMPASS
CONTINENT = {'AFRICA', 'ASIA', 'EUROPE', 'NORTH AMERICA', 'SOUTH AMERICA', 'OCEANIA', 'ANTARCTICA', 'AUSTRALIA',
             'AUSTRALIA/OCEANIA', 'LATIN AMERICA', 'CENTRAL AMERICA', 'AMERICAS'}

# ---- small helpers ------------------------------------------------------------------------------------------

NUMERIC = {'int64', 'double', 'decimal'}


def _nums(values):
    out = []
    for v in values:
        try:
            x = float(v)
        except (TypeError, ValueError):
            continue
        if not math.isnan(x):
            out.append(x)
    return out


def _whole(xs):
    return bool(xs) and all(x == math.floor(x) for x in xs)


def _contiguous(xs):
    return bool(xs) and _whole(xs) and max(xs) - min(xs) + 1 == len(set(xs))


def _share(us, pred):
    return sum(1 for u in us if pred(u)) / len(us) if us else 0.0


def _gtin_ok(s):
    if not s.isdigit() or len(s) not in (8, 12, 13, 14):
        return False
    d = [int(ch) for ch in s]
    body, check = d[:-1][::-1], d[-1]
    tot = sum(x * (3 if i % 2 == 0 else 1) for i, x in enumerate(body))
    return (10 - tot % 10) % 10 == check


def _norm_cat(c):
    dc = c.get('dataCategory')
    return None if not dc or dc == 'Uncategorized' else dc


# ---- data category -------------------------------------------------------------------------------------------

STRONG, WEAK = 0.9, 0.3

_NAME_HINTS = [(r'latitude|\blat$|^lat\b', 'Latitude'), (r'longitude|\blon$|\blng$|\blong$|^lon\b|^lng\b', 'Longitude'),
               (r'postal|zip|postcode|post code', 'PostalCode'), (r'address', 'Address'),
               (r'continent', 'Continent'), (r'county', 'County'), (r'country', 'Country'), (r'nation', 'Country'),
               (r'state|province|territory', 'StateOrProvince'), (r'city|town|suburb', 'City'),
               (r'barcode|\bean\b|\bupc\b|gtin', 'Barcode'), (r'image|photo|picture|logo|thumbnail|icon', 'ImageUrl'),
               (r'url|link|website|web site|homepage', 'WebUrl')]


def name_hint(name):
    n = (name or '').strip().lower()
    for pat, cat in _NAME_HINTS:
        if re.search(pat, n):
            return cat
    return None


_ADDRESS = re.compile(r'^\d+[A-Z]?\s+\S+.*\b(ST|STREET|AVE|AVENUE|RD|ROAD|BLVD|BOULEVARD|LN|LANE|DR|DRIVE|WAY|CT|COURT|'
                      r'PL|PLACE|PKWY|PARKWAY|HWY|HIGHWAY|TER|TERRACE|CIR|CIRCLE|SQ|SQUARE|ALLEY|CRES|CRESCENT)\b\.?')


def _postcode_like(u):
    if re.fullmatch(r'\d{4,5}', u) or re.fullmatch(r'\d{5}-\d{4}', u):
        return True
    return bool(re.fullmatch(r'[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2}', u) or re.fullmatch(r'[A-Z]\d[A-Z] ?\d[A-Z]\d', u))


def text_shares(values):
    us = [_up(v) for v in values if str(v).strip()]
    low = [str(v).strip().lower() for v in values if str(v).strip()]
    url = lambda s: s.startswith(('http://', 'https://', 'www.', 'mailto:', 'data:image/'))
    img = lambda s: s.startswith('data:image/') or url(s) and re.search(r'\.(png|jpe?g|gif|svg|bmp|webp)(\?.*)?$', s) is not None
    return {
        'Country': _share(us, lambda u: u in COUNTRY or u in COUNTRY_A3 and len(set(us)) > 1),
        'CountryCode': _share(us, lambda u: u in COUNTRY_A2),
        'StateOrProvince': _share(us, lambda u: u in STATE),
        'StateCode': _share(us, lambda u: u in STATE_A2),
        'County': _share(us, lambda u: u in COUNTY),
        'City': _share(us, lambda u: u in CITY),
        'Continent': _share(us, lambda u: u in CONTINENT),
        'WebUrl': _share(low, url),
        'ImageUrl': _share(low, img),
        'PostalCode': _share(us, _postcode_like),
        'Address': _share(us, lambda u: _ADDRESS.match(u) is not None),
        'Barcode': _share(us, _gtin_ok),
    }


# Categories the data can confirm on its own, in the order that breaks a tie when two are strong and nothing else
# (the author or the name) picks between them. Postal code and address need the name or the author as well: plenty
# of codes are four or five digits and plenty of labels start with a number.
_SELF_EVIDENT = ['Country', 'StateOrProvince', 'City', 'Continent', 'ImageUrl', 'WebUrl', 'Barcode']


def datacat_key(c, values):
    author, hint = _norm_cat(c), name_hint(c.get('name'))
    dt = c.get('dataType')
    if dt in NUMERIC:
        xs = _nums(values)
        if not xs:
            return 'none', 'no values'
        in_lat, in_lon = all(-90 <= x <= 90 for x in xs), all(-180 <= x <= 180 for x in xs)
        claim = author if author in ('Latitude', 'Longitude') else hint if hint in ('Latitude', 'Longitude') else None
        if claim == 'Latitude':
            return ('Latitude', 'lat in range') if in_lat else ('Longitude', 'lat out of range') if in_lon else ('none', 'not a coordinate')
        if claim == 'Longitude':
            return ('Longitude', 'lon in range') if in_lon else ('none', 'not a coordinate')
        whole = _whole(xs)
        if (author == 'PostalCode' or hint == 'PostalCode') and whole and _share(xs, lambda x: 100 <= x <= 999999) >= STRONG:
            return 'PostalCode', 'numeric zip'
        if whole and _share([str(int(x)) for x in xs], _gtin_ok) >= STRONG:
            return 'Barcode', 'gtin check digits'
        if author == 'Barcode' and whole:
            return 'Barcode', 'author barcode'
        return 'none', 'number, no category'
    if dt != 'string':
        return 'none', 'not text or number'
    vals = [v for v in values if str(v).strip()]
    if vals and len(_nums(vals)) == len(vals):
        lab, why = datacat_key(dict(c, dataType='double'), vals)
        return lab, 'numbers as text: ' + why
    if not vals:
        return 'none', 'no values'
    sh = text_shares(vals)
    strong = set()
    for cat in _SELF_EVIDENT:
        if sh[cat] >= STRONG:
            strong.add(cat)
    if sh['CountryCode'] >= STRONG:
        strong.add('Country')
    if sh['StateCode'] >= STRONG:
        strong.add('StateOrProvince')
    if sh['ImageUrl'] >= 0.5 and sh['WebUrl'] >= STRONG:
        strong.discard('WebUrl')
        strong.add('ImageUrl')
    if 'WebUrl' in strong and author == 'ImageUrl':
        return 'ImageUrl', 'links, author says images'
    # County names are also surnames (Adams, Jackson), so a county needs the name or the author too.
    if sh['County'] >= STRONG and (author == 'County' or hint == 'County'):
        strong.add('County')
    if (sh['PostalCode'] >= STRONG) and (author == 'PostalCode' or hint == 'PostalCode'):
        strong.add('PostalCode')
    if sh['Address'] >= STRONG and (author == 'Address' or hint == 'Address'):
        strong.add('Address')
    if len(strong) == 1:
        return next(iter(strong)), 'data'
    if len(strong) > 1:
        if author in strong:
            return author, 'data ambiguous, author'
        if hint in strong:
            return hint, 'data ambiguous, name'
        return next(c for c in _SELF_EVIDENT + ['PostalCode', 'Address'] if c in strong), 'data ambiguous, precedence'
    # Nothing is strong. The data does not settle it, so an author's tag counts if the values do not contradict it
    # (a list only covers so many towns), then the name. A tag the data cannot check at all (Place) stands.
    weak = lambda cat: sh.get(cat, 0) >= WEAK or (cat == 'Country' and sh['CountryCode'] >= WEAK) \
        or (cat == 'StateOrProvince' and sh['StateCode'] >= WEAK) or (cat == 'ImageUrl' and sh['WebUrl'] >= WEAK)
    if author == 'Place':
        return 'Place', 'author, not checkable'
    if author and weak(author):
        return author, 'weak data, author'
    if hint and hint != 'Latitude' and hint != 'Longitude' and weak(hint):
        return hint, 'weak data, name'
    return 'none', 'data says none' if not author else 'author contradicted'


# ---- summarize by --------------------------------------------------------------------------------------------

# The product's own high-precision "this number is a label" rule, ReadinessRules.cs NonAdditiveDimName, ported.
_NONADD = re.compile(
    r'(?i)(\b(year|month|quarter|week|day|age|rank|order|sort|sequence|latitude|longitude|lat|lon|lng)$'
    r'|\b(month|quarter|week|day)\s*(number|num|no)\b|\b(monthnum|monthnumber|weeknum|weeknumber|quarternum|daynum|daynumber)\b'
    r'|\bpin(?:\s*code)?$|\bpostal\s*code\b|\bpostcode\b|\bzip\s*code\b|\bzip$'
    r'|\b(?:(?<!\btotal\s)(?<!\bcount\s)(?<!\bunits\s)number|code|id|key)$)')
_CAMEL_ID = re.compile(r'[a-z0-9](ID|Id|Key|SK|Code|No|Num|Number|Year|Month|Quarter|Week|Level)$')
# An id in other spellings: a leading Id (IdsCliente, ID Factura), snake case (customer_id), other languages.
_ID_OTHER = re.compile(r'(^I[dD]s?(?=[A-Z_\s]|$)|(?i:[_\s\-.](id|key|code|no|num)$)|(?i:^(id|cod|codigo|c\u00f3digo|clave|num)\b)|(?i:\b(level|tier|grade)$))')
# A plural period word is a duration (Years, Days), not a period number, so the period words refuse a trailing s.
_PERIOD_NAME = re.compile(r'(?i)(year|yr|month|mth|quarter|qtr|week|wk|day|period|fiscal|semester|half)(?!s)')


def words(name):
    """The name as lower-case words: split at spaces, punctuation and camel-case joins. Matching words, not
    substrings, is what keeps Operating from reading as rating and Duration as ratio."""
    n = re.sub(r'([a-z0-9])([A-Z])', r'\1 \2', name or '')
    n = re.sub(r'([A-Z]+)([A-Z][a-z])', r'\1 \2', n)
    return [w for w in re.split(r'[^A-Za-z0-9%\u00c0-\u024f]+', n.lower()) if w]


_COUNT_WORDS = {'count', 'counts', 'total', 'totals', 'qty', 'quantity', 'quantities', 'units', 'sum'}
_RATE_WORDS = {'rate', 'rates', 'ratio', 'ratios', 'pct', 'percent', 'percentage', '%', 'share', 'growth', 'avg',
               'average', 'mean', 'score', 'scores', 'rating', 'ratings', 'price', 'prices', 'precio', 'preis', 'prix',
               'temperature', 'temp', 'probability', 'yield', 'efficiency', 'utilization', 'utilisation', 'index',
               'height', 'weight', 'bmi', 'speed', 'pressure', 'humidity', 'concentration', 'satisfaction',
               'involvement', 'happy', 'happiness', 'feeling', 'tenure', 'experience', 'years'}
_LABEL_WORDS = {'coded', 'code', 'phone', 'npi', 'offset', 'date'}
_AMOUNT_YEAR = re.compile(r'(?i)(last|prior|previous|this|next|current)[\s_]*year$')


def labelish_name(name):
    n = (name or '').strip()
    if _AMOUNT_YEAR.search(n):
        return False
    w = words(n)
    if set(w) & _LABEL_WORDS or (w and len(w) <= 2 and w[-1] in ('x', 'y', 'z', 'long')):
        return True
    if len(w) > 1 and w[0] in ('min', 'max'):
        return True
    return bool(_NONADD.search(n) or _CAMEL_ID.search(n) or _ID_OTHER.search(n))


def _count_name(name):
    w = words(name)
    return bool(set(w) & _COUNT_WORDS) or 'number of' in ' '.join(w)


def _rate_name(name):
    w = words(name)
    j = ' '.join(w)
    return bool(set(w) & _RATE_WORDS) or '%' in (name or '') or any(p in j for p in ('per unit', 'per person', 'per capita', 'unit cost'))


def summarize_key(c, values, rows):
    if c.get('dataType') not in NUMERIC:
        return 'none', 'not a number'
    name = c.get('name') or ''
    xs = _nums(values)
    if c.get('relationshipRole', 'none') != 'none' or c.get('isKey'):
        return 'none', 'relationship key'
    if labelish_name(name):
        return 'none', 'label by name'
    if xs and _whole(xs) and all(1900 <= x <= 2100 for x in xs) and len(set(xs)) <= 200:
        return 'none', 'year by data'
    if xs and _whole(xs) and all(1 <= x <= 53 for x in xs) and _PERIOD_NAME.search(name):
        return 'none', 'period number'
    if xs and _contiguous(xs) and rows and len(set(xs)) == rows:
        return 'none', 'row id'
    if _count_name(name):
        return 'sum', 'count or total by name'
    if _rate_name(name):
        return 'average', 'rate by name'
    if xs and not _whole(xs) and all(-1 <= x <= 1 for x in xs) and len(set(xs)) >= 3:
        return 'average', 'ratio by data'
    return 'sum', 'additive'


# ---- hidden --------------------------------------------------------------------------------------------------

_TECH = re.compile(r'(?i)(\b(id|key|guid|uuid|sk|fk|pk)$|rowguid|\bhash\b|checksum|\betl\b|^index$|surrogate|'
                   r'row ?version|timestamp$|\b(load|loaded|modified|created|updated|inserted|insert|refresh)(ed)?'
                   r' ?(date|time|dt|on|at|by)$|^_)')
_CAMEL_TECH = re.compile(r'[a-z0-9](ID|Id|Key|SK|Guid|GUID)$')
_SORT_NAME = re.compile(r'(?i)(sort|orden|ordre|\border$|order$|rank$|\bindex$|\bseq$|sequence$|position$|^order\b)')


def ordering_name(name):
    n = (name or '').strip()
    return bool(_SORT_NAME.search(n)) and not techish_name(n) and not re.search(r'(?i)(number|no|num)$', n)


_ID_LEAD = re.compile(r'(^I[dD]s?(?=[A-Z_\s]|$)|(?i:[_\s\-.](id|key|guid|sk|fk|pk)$)|(?i:^(id|key)[_\s]))')
# Power Query's leftovers: unnamed columns and the folder connector's file columns.
_PQ_LEFTOVER = re.compile(r'(?i)^((column|columna|colonne|spalte|coluna)\s?\d+|folder path|source\.name|date modified|date accessed|date created|extension|attributes)$')
# An id glued to a lower-case word (stateid, territoryid), less the ordinary words that end in id.
_GLUED_ID = re.compile(r'^[a-z]{3,}id$')
_NOT_ID = {'paid', 'unpaid', 'prepaid', 'valid', 'invalid', 'solid', 'fluid', 'humid', 'grid', 'acid', 'avid', 'david',
           'rapid', 'timid', 'hybrid', 'liquid', 'squid', 'vivid', 'void', 'android', 'covid', 'orchid', 'pyramid',
           'druid', 'cupid', 'lipid', 'candid', 'splendid', 'madrid', 'said', 'maid', 'raid', 'laid', 'aid', 'rigid'}


def techish_name(name):
    n = (name or '').strip()
    if _GLUED_ID.match(n) and n not in _NOT_ID:
        return True
    return bool(_TECH.search(n) or _CAMEL_TECH.search(n) or _ID_LEAD.search(n) or _PQ_LEFTOVER.match(n))


def hidden_key(c, values, rows, table_measures=0):
    """True for hidden. `values` are the column's distinct non-blank values."""
    name, dt, role = c.get('name') or '', c.get('dataType'), c.get('relationshipRole', 'none')
    xs = _nums(values) if dt in NUMERIC else []
    if role == 'many':
        return True, 'foreign key'
    if role == 'one':
        if dt == 'dateTime':
            return False, 'date key stays visible'
        if techish_name(name):
            return True, 'id key'
        if xs and _contiguous(xs) and rows and len(set(xs)) == rows:
            return True, 'surrogate key'
        return False, 'meaningful key'
    if techish_name(name):
        return True, 'technical name'
    if dt in NUMERIC and ordering_name(name):
        return True, 'sort helper by name'
    if c.get('usedAsSortTarget'):
        if ordering_name(name):
            return True, 'sort helper'
        return bool(c.get('isHidden')), 'sort target, author'
    if dt in NUMERIC and (c.get('usedByMeasures') or 0) > 0 and table_measures > 0:
        return bool(c.get('isHidden')), 'measure input, author'
    return False, 'attribute'


# ---- format ----------------------------------------------------------------------------------------------------

def format_family(fs):
    """rules.mjs formatFamily, ported."""
    if not fs:
        return 'text_or_none'
    f, low = fs.strip(), fs.strip().lower()
    if low in ('short date', 'long date', 'general date', 'short time', 'long time', 'medium date'):
        return 'date_or_time'
    if re.search(r'(yyyy|yy|mmm|dd|hh)', low) and '#' not in f:
        return 'date_or_time'
    if '%' in f:
        return 'percent'
    if re.search('[$£€¥]', f) or 'currency' in low:
        return 'currency'
    if re.search(r'[#0]\.[#0]', f) or '0.0' in f:
        return 'decimal'
    if re.search(r'[#0]', f):
        return 'whole_number'
    return 'text_or_none'


_MONEY_NAME = re.compile(r'(?i)(amount|\bamt\b|sales|revenue|cost|cogs|price|profit|income|expense|budget|spend|freight|'
                         r'\btax\b|salary|wage|\bpay\b|payment|\bfee\b|invoice|margin(?! ?%)(?! ?pct)|discount amount|'
                         r'\bgross\b|\bnet\b|turnover|balance|\bcash\b|earnings|\bvalue\b|\$)')
_PERCENT_NAME = re.compile(r'(?i)(rate|ratio|pct|percent|%|\bshare\b|growth|margin %|margin pct|conversion|'
                           r'utili[sz]ation|discount(?! amount))')


def format_key(c, values):
    dt = c.get('dataType')
    if dt == 'dateTime':
        return 'date_or_time', 'date type'
    if dt not in NUMERIC:
        return 'text_or_none', 'not a number'
    name, author = c.get('name') or '', format_family(c.get('formatString'))
    xs = _nums(values)
    small = bool(xs) and all(-1.5 <= x <= 1.5 for x in xs)
    ratio = small and not _whole(xs)
    if author == 'currency':
        return 'currency', 'author money'
    if (author == 'percent' and (ratio or not xs)) or (_PERCENT_NAME.search(name) and (small or not xs)):
        return 'percent', 'ratio values'
    if _MONEY_NAME.search(name):
        if author in ('decimal', 'whole_number'):
            return author, 'money name, author plain'
        return 'currency', 'money name'
    if xs and _whole(xs):
        return 'whole_number', 'whole values'
    return 'decimal', 'fractional values'


# ---- sort by ---------------------------------------------------------------------------------------------------

_MONTHS = ['JANUARY', 'FEBRUARY', 'MARCH', 'APRIL', 'MAY', 'JUNE', 'JULY', 'AUGUST', 'SEPTEMBER', 'OCTOBER',
           'NOVEMBER', 'DECEMBER']
_MONTH_IDX = {m: i for i, m in enumerate(_MONTHS)}
_MONTH_IDX.update({m[:3]: i for i, m in enumerate(_MONTHS)})
_MONTH_IDX['SEPT'] = 8
for _names in (['ENERO', 'FEBRERO', 'MARZO', 'ABRIL', 'MAYO', 'JUNIO', 'JULIO', 'AGOSTO', 'SEPTIEMBRE', 'OCTUBRE', 'NOVIEMBRE', 'DICIEMBRE'],
               ['JANVIER', 'F\u00c9VRIER', 'MARS', 'AVRIL', 'MAI', 'JUIN', 'JUILLET', 'AO\u00dbT', 'SEPTEMBRE', 'OCTOBRE', 'NOVEMBRE', 'D\u00c9CEMBRE'],
               ['JANUAR', 'FEBRUAR', 'M\u00c4RZ', 'APRIL', 'MAI', 'JUNI', 'JULI', 'AUGUST', 'SEPTEMBER', 'OKTOBER', 'NOVEMBER', 'DEZEMBER'],
               ['JANEIRO', 'FEVEREIRO', 'MAR\u00c7O', 'ABRIL', 'MAIO', 'JUNHO', 'JULHO', 'AGOSTO', 'SETEMBRO', 'OUTUBRO', 'NOVEMBRO', 'DEZEMBRO']):
    for _i, _m in enumerate(_names):
        _MONTH_IDX.setdefault(_m, _i)
_DAYS = ['MONDAY', 'TUESDAY', 'WEDNESDAY', 'THURSDAY', 'FRIDAY', 'SATURDAY', 'SUNDAY']
_DAY_IDX = {d: i for i, d in enumerate(_DAYS)}
_DAY_IDX.update({d[:3]: i for i, d in enumerate(_DAYS)})
_DAY_IDX.update({'TUES': 1, 'THUR': 3, 'THURS': 3, 'WEDS': 2})
for _names in (['LUNES', 'MARTES', 'MI\u00c9RCOLES', 'JUEVES', 'VIERNES', 'S\u00c1BADO', 'DOMINGO'],
               ['LUNDI', 'MARDI', 'MERCREDI', 'JEUDI', 'VENDREDI', 'SAMEDI', 'DIMANCHE'],
               ['MONTAG', 'DIENSTAG', 'MITTWOCH', 'DONNERSTAG', 'FREITAG', 'SAMSTAG', 'SONNTAG'],
               ['SEGUNDA', 'TER\u00c7A', 'QUARTA', 'QUINTA', 'SEXTA', 'S\u00c1BADO', 'DOMINGO']):
    for _i, _d in enumerate(_names):
        _DAY_IDX.setdefault(_d, _i)
_ORDINAL_SETS = [
    ['VERY LOW', 'LOW', 'MEDIUM LOW', 'MEDIUM', 'MEDIUM HIGH', 'HIGH', 'VERY HIGH', 'CRITICAL'],
    ['LOW', 'MODERATE', 'HIGH', 'SEVERE'],
    ['LOW', 'MID', 'HIGH'],
    ['XXS', 'XS', 'S', 'M', 'L', 'XL', 'XXL', 'XXXL'],
    ['EXTRA SMALL', 'SMALL', 'MEDIUM', 'LARGE', 'EXTRA LARGE'],
    ['TINY', 'SMALL', 'MEDIUM', 'LARGE', 'HUGE'],
    ['VERY POOR', 'POOR', 'FAIR', 'AVERAGE', 'GOOD', 'VERY GOOD', 'EXCELLENT'],
    ['STRONGLY DISAGREE', 'DISAGREE', 'NEUTRAL', 'AGREE', 'STRONGLY AGREE'],
    ['NEVER', 'RARELY', 'SOMETIMES', 'OFTEN', 'ALWAYS'],
    ['BRONZE', 'SILVER', 'GOLD', 'PLATINUM', 'DIAMOND'],
    ['FIRST', 'SECOND', 'THIRD', 'FOURTH', 'FIFTH', 'SIXTH'],
    ['JUNIOR', 'MID', 'SENIOR', 'LEAD', 'PRINCIPAL'],
    ['NEW', 'OPEN', 'IN PROGRESS', 'RESOLVED', 'CLOSED'],
    ['NOT STARTED', 'IN PROGRESS', 'COMPLETED'],
    ['BEFORE', 'DURING', 'AFTER'],
    ['EARLY', 'ON TIME', 'LATE'],
    ['Q1', 'Q2', 'Q3', 'Q4'],
    ['H1', 'H2'],
]


def _year_of(tok):
    if re.fullmatch(r'(19|20)\d\d', tok):
        return int(tok)
    if re.fullmatch(r"'?\d\d", tok):
        return 2000 + int(tok.strip("'"))
    return None


def _parse_month(u):
    toks = [t for t in re.split(r"[\s\-/,.]+", u) if t]
    m = [t for t in toks if t in _MONTH_IDX]
    if len(m) != 1:
        return None
    rest = [t for t in toks if t not in _MONTH_IDX]
    if len(rest) > 1:
        return None
    y = 0
    if rest:
        y = _year_of(rest[0])
        if y is None:
            return None
    return (y, _MONTH_IDX[m[0]])


def _parse_day(u):
    return (_DAY_IDX[u],) if u in _DAY_IDX else None


def _parse_quarter(u):
    m = re.fullmatch(r"(?:(\S+)\s*[- ]?\s*)?(?:Q|QTR|QUARTER)\s*-?\s*([1-4])(?:\s*[- ]?\s*(\S+))?", u)
    if not m:
        return None
    ys = [x for x in (m.group(1), m.group(3)) if x]
    y = 0
    for x in ys:
        yy = _year_of(x.replace('FY', ''))
        if yy is None:
            return None
        y = yy
    return (y, int(m.group(2)))


_PERIOD = re.compile(r'(?:FY\s*\S+\s*)?(?:P|PER|PERIOD|WK|WEEK|W|M|MONTH|H|HALF|SEM|SEMESTER|DAY|D|SPRINT|STAGE|STEP|'
                     r'PHASE|LEVEL|GRADE|YEAR|TIER|ROUND)\s*-?\s*\d+(?:\s*\S+)?')
_BAND = re.compile(r"^\s*(?:(?:UNDER|BELOW|LESS THAN|OVER|ABOVE|MORE THAN|UP TO|<|>|<=|>=)\s*)?[$£€]?\d[\d,.]*\s*"
                   r"(?:K|M|%|YRS|YEARS|DAYS|HRS|HOURS|MIN|MINS)?\s*"
                   r"(?:(?:-|–|TO)\s*[$£€]?\d[\d,.]*\s*(?:K|M|%|YRS|YEARS|DAYS|HRS|HOURS|MIN|MINS)?|\+|"
                   r"AND (?:OVER|ABOVE|UP|OLDER|MORE|UNDER|BELOW|LESS)|OR (?:MORE|LESS|OVER|UNDER))?\s*$")


def _first_numbers(u):
    return [float(x.replace(',', '')) for x in re.findall(r'\d[\d,]*(?:\.\d+)?', u)]


def _parse_band(u):
    if not _BAND.match(u):
        return None
    ns = _first_numbers(u)
    if not ns:
        return None
    first = ns[0]
    if re.match(r'^\s*(UNDER|BELOW|LESS THAN|UP TO|<)', u):
        first -= 0.5
    if re.match(r'^\s*(OVER|ABOVE|MORE THAN|>)', u):
        first += 0.5
    return (first,)


def _parse_period(u):
    if not _PERIOD.fullmatch(u):
        return None
    toks = re.findall(r'\d+', u)
    ys = [int(t) for t in toks if re.fullmatch(r'(19|20)\d\d', t)]
    rest = [int(t) for t in toks if not re.fullmatch(r'(19|20)\d\d', t)]
    fy = re.match(r'FY\s*(\d+)', u)
    if fy and not ys:
        ys = [int(fy.group(1))]
        rest = rest[1:]
    return tuple(ys + rest)


def _parse_number_text(u):
    return (float(u.replace(',', '')),) if re.fullmatch(r'-?\d[\d,]*(?:\.\d+)?', u) else None


def _ordinal_parser(us):
    best = None
    for s in _ORDINAL_SETS:
        idx = {w: i for i, w in enumerate(s)}
        share = _share(us, lambda u: u in idx)
        if share >= STRONG and len(set(us)) >= 2 and (best is None or share > best[0]):
            best = (share, idx)
    return (lambda u: (best[1][u],) if u in best[1] else None) if best else None


_DATE_FORMATS = ['%d-%b-%Y', '%d %b %Y', '%b %d, %Y', '%d/%m/%Y', '%m/%d/%Y', '%Y/%m/%d', '%d.%m.%Y', '%d-%m-%Y',
                 '%m-%d-%Y', '%B %d, %Y', '%d %B %Y', '%A, %B %d, %Y', '%a, %d %b %Y', '%d-%b-%y', '%b-%y', '%B %Y']


def _parse_date(u):
    from datetime import datetime
    for fmt in _DATE_FORMATS:
        try:
            d = datetime.strptime(u.title(), fmt)
            return (d.year, d.month, d.day)
        except ValueError:
            continue
    return None


_VOCABS = [('date as text', _parse_date), ('month', _parse_month), ('day', _parse_day), ('quarter', _parse_quarter), ('period', _parse_period),
           ('band', _parse_band), ('number text', _parse_number_text)]


_BUCKETS = {'UNKNOWN', 'N/A', 'NA', 'NONE', '-', '(BLANK)', 'BLANK', 'OTHER', 'OTHERS', 'TOTAL', 'ALL', 'UNDEFINED',
            'NOT APPLICABLE', 'NULL', '?', 'DESCONOCIDO', 'SIN DATO', 'OTROS'}


def vocabulary(values):
    """(kind, {value: canonical key}) when 90% or more of the distinct values read as one ordered vocabulary.
    Catch-all buckets such as Unknown or Other are left out of the share; they have no place in the order."""
    us = {v: _up(v) for v in values if str(v).strip() and _up(v) not in _BUCKETS}
    if len(us) < 2:
        return None, {}
    for kind, parse in _VOCABS:
        keys = {v: parse(u) for v, u in us.items()}
        ok = {v: k for v, k in keys.items() if k is not None}
        if len(ok) / len(us) >= STRONG:
            return kind, ok
    op = _ordinal_parser(list(us.values()))
    if op:
        return 'ranked words', {v: op(u) for v, u in us.items() if op(u) is not None}
    return None, {}


def _alpha(vs):
    return sorted(vs, key=lambda v: (str(v).casefold(), str(v)))


def _pkey(dtype, p):
    if dtype in NUMERIC:
        try:
            return (0, float(p))
        except (TypeError, ValueError):
            return (1, str(p))
    if dtype == 'dateTime':
        return (0, str(p))
    return (0, str(p).casefold())


def _order_by(partner, vs):
    dtype, mapping = partner
    return sorted(vs, key=lambda v: _pkey(dtype, mapping.get(v)))


def _consistent(seq, canon, cyclic):
    ks = [canon[v] for v in seq]
    descents = sum(1 for a, b in zip(ks, ks[1:]) if b < a)
    if descents == 0:
        return True
    return cyclic and descents == 1 and ks[-1] <= ks[0]


def sort_key(c, values, partners):
    """(needs a sort, the partners that order it correctly, why). `partners` maps each one-to-one partner's name to
    (its data type, {this column's value: the partner's value})."""
    vs = [v for v in values if str(v).strip()]
    if c.get('dataType') != 'string' or len(vs) < 2 or not partners:
        return False, [], 'not asked'
    if techish_name(c.get('name')) or labelish_name(c.get('name')) and re.search(r'(?i)(id|key|code|number|no|num)$', c.get('name') or ''):
        return False, [], 'an identifier'
    kind, canon = vocabulary(vs)
    if kind:
        parsed = [v for v in vs if v in canon]
        canon_order = sorted(parsed, key=lambda v: canon[v])
        alpha_parsed = _alpha(parsed)
        cyclic = kind == 'day'
        if _consistent(alpha_parsed, canon, cyclic) and [canon[v] for v in alpha_parsed] == [canon[v] for v in canon_order]:
            return False, [], f'{kind}, already alphabetical'
        ok = sorted(p for p, part in partners.items() if _consistent(_order_by(part, parsed), canon, cyclic))
        if ok:
            return True, ok, f'{kind}, partner orders it'
        return False, [], f'{kind}, no partner orders it'
    alpha = _alpha(vs)
    ordering = {p: part for p, part in partners.items() if ordering_name(p) and _order_by(part, vs) != alpha}
    author = c.get('sortByColumn')
    if author in ordering:
        same = sorted(p for p, part in ordering.items() if _order_by(part, vs) == _order_by(ordering[author], vs))
        return True, same, 'author ordering column'
    if ordering:
        return True, sorted(ordering), 'ordering column'
    return False, [], 'no ordered vocabulary'
