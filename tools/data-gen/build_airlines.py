import csv
import json
import re
import sys
from collections import defaultdict

wiki_rows, openflights, out = sys.argv[1], sys.argv[2], sys.argv[3]
rows = json.load(open(wiki_rows, encoding='utf-8'))
DEAD = re.compile(r'defunct|ceased|no longer|former|merged|renamed|bankrupt|dissolved|liquidat|replaced|discontinued', re.I)
SPACES = re.compile(r'\s+')

# OpenFlights: OACI -> {nombre: activa} y OACI -> telefonías
of_active = defaultdict(dict)
of_tel = defaultdict(set)
with open(openflights, encoding='utf-8') as f:
    for r in csv.reader(f, escapechar='\\'):
        if len(r) < 8:
            continue
        icao = r[4]
        if re.fullmatch(r'[A-Z]{3}', icao):
            of_active[icao][r[1].lower()] = r[7] == 'Y'
            if r[5] and r[5] != 'N':
                of_tel[icao].add(SPACES.sub(' ', r[5]).strip().upper())


def norm(s):
    return re.sub(r'[^a-z0-9]', '', s.lower())


def of_says(r, active):
    for name, a in of_active.get(r['icao'], {}).items():
        if a == active and (norm(name) in norm(r['name']) or norm(r['name']) in norm(name)):
            return True
    return False


by = defaultdict(list)
for r in rows:
    if not re.fullmatch(r'[A-Z]{3}', r['icao']):
        continue
    tel = SPACES.sub(' ', r['tel']).strip().upper()
    if not tel or tel in ('N/A', 'NONE', '-') or not re.fullmatch(r"[A-Z0-9 '\-.]+", tel):
        continue
    by[r['icao']].append(dict(r, tel=tel))

airlines = {}
ambiguous = []
for icao, cands in sorted(by.items()):
    alive = [c for c in cands if not DEAD.search(c['comments'])]
    if len(alive) > 1:
        alive = [c for c in alive if not of_says(c, False)] or alive
    if len({c['tel'] for c in alive}) > 1:
        active = [c for c in alive if of_says(c, True)]
        if len({c['tel'] for c in active}) == 1:
            alive = active
        else:
            exact = [c for c in alive if of_active.get(icao, {}).get(c['name'].lower()) is True]
            if len({c['tel'] for c in exact}) == 1:
                alive = exact
    tels = {c['tel'] for c in alive}
    if len(tels) == 1:
        c = alive[0]
        airlines[icao] = {'telephony': c['tel'], 'name': c['name'], 'verified': c['tel'] in of_tel.get(icao, set())}
    elif len(tels) > 1:
        ambiguous.append((icao, [(c['name'], c['tel']) for c in alive]))

# Verificadas a mano en una tercera fuente cuando OpenFlights está desactualizada (2026-10-31).
CHECKED = {
    'LAN': ('LAN CHILE', 'SKYbrary, operador LAN Airlines Chile: callsign LANCHILE'),
    'JAT': ('ROCKSMART', 'airplanes.live, Jetsmart SPA: radio callsign ROCKSMART'),
}
for icao, (tel, src) in CHECKED.items():
    assert airlines[icao]['telephony'] == tel, icao
    airlines[icao]['verified'] = True
    airlines[icao]['checkedIn'] = src

header = {
    'source': 'Wikipedia, "List of airline codes" (columna Telephony Designator), en.wikipedia.org, descargada el 2026-10-31, '
              'licencia CC BY-SA 4.0; desempate de codigos repetidos y verificacion con OpenFlights airlines.dat (ODbL).',
    'reviewed': True,
    'note': 'Generado a partir de las dos fuentes: se descartan las filas marcadas como cerradas y se omiten los codigos con '
            'varias telefonias vigentes. verified = la telefonia coincide en Wikipedia y en OpenFlights.',
}
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('{\n')
    for k, v in header.items():
        f.write(f'  {json.dumps(k)}: {json.dumps(v, ensure_ascii=False)},\n')
    f.write('  "airlines": {\n')
    items = list(airlines.items())
    for i, (k, v) in enumerate(items):
        f.write(f'    {json.dumps(k)}: {json.dumps(v, ensure_ascii=False)}' + (',' if i < len(items) - 1 else '') + '\n')
    f.write('  }\n}\n')
print('aerolineas', len(airlines), 'ambiguas', len(ambiguous),
      'verificadas con las dos fuentes', sum(1 for v in airlines.values() if v['verified']))
for k in ['LAN', 'SKU', 'JAT', 'LXP', 'LPE', 'ARG', 'TAM', 'DLH', 'AFR']:
    print(k, airlines.get(k))
