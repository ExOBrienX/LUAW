import glob
import json
import os
import re
import sys

src = sys.argv[1]
rows = []
for path in sorted(glob.glob(os.path.join(src, 'p-*.wiki'))):
    text = open(path, encoding='utf-8').read()
    for block in re.split(r'\n\|-[^\n]*', text):
        cells = []
        for line in block.split('\n'):
            line = line.strip()
            if not line.startswith('|') or line.startswith('|}') or line.startswith('|+'):
                continue
            for c in line[1:].split('||'):
                cells.append(c.strip())
        if len(cells) < 4:
            continue
        def clean(s):
            s = re.sub(r'<ref[^>]*/>', '', s)
            s = re.sub(r'<ref[^>]*>.*?</ref>', '', s, flags=re.S)
            s = re.sub(r'\{\{[^}]*\}\}', '', s)
            s = re.sub(r'\[\[(?:[^|\]]*\|)?([^\]]*)\]\]', r'\1', s)
            s = re.sub(r'<[^>]+>', '', s)
            s = s.replace("'''", '').replace("''", '')
            # atributos de celda: "style=...| valor"
            if '|' in s:
                s = s.split('|')[-1]
            return s.strip()
        c = [clean(x) for x in cells] + [''] * 6
        rows.append({'iata': c[0], 'icao': c[1], 'name': c[2], 'tel': c[3], 'country': c[4], 'comments': c[5]})
json.dump(rows, open(os.path.join(src, 'rows.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print('filas', len(rows))
icao = [r for r in rows if re.fullmatch(r'[A-Z]{3}', r['icao'])]
print('con OACI', len(icao), 'con telefonía', sum(1 for r in icao if r['tel']))
from collections import Counter
dup = Counter(r['icao'] for r in icao)
print('OACI repetidos', sum(1 for k, v in dup.items() if v > 1))
words = Counter()
for r in icao:
    for w in re.findall(r'[a-z]+', r['comments'].lower()):
        words[w] += 1
print(words.most_common(40))
for k in sys.argv[2:]:
    for r in icao:
        if r['icao'] == k:
            print(r)
