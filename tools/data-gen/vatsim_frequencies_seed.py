"""Semilla de frecuencias vistas en VATSIM para la app (ADR-173): Centros ("_CTR", sin el sufijo) y Aproximación/Salidas
("_APP"/"_DEP", con el sufijo), con el mismo formato que vatsim-centers.json del usuario.

Junta lo que está en línea ahora en el feed v3 y, si se pasan, archivos vatsim-centers.json ya guardados (frecuencias que
LUAW vio antes). Datos de VATSIM: uso no comercial, como las rutas.

Uso: python -I tools/data-gen/vatsim_frequencies_seed.py [vatsim-centers.json ...]
Escribe data/world/vatsim-frequencies-seed.json (lo reescribe sumando lo que ya tenía).
"""
import json
import os
import sys
import urllib.request

FEED = "https://data.vatsim.net/v3/vatsim-data.json"


def add(seen, callsign, mhz, facility):
    cs = (callsign or "").strip().upper()
    center = facility == 6 and cs.endswith("_CTR")
    approach = facility == 5 and (cs.endswith("_APP") or cs.endswith("_DEP"))
    if not center and not approach:
        return
    try:
        khz = round(float(mhz) * 1000)
    except (TypeError, ValueError):
        return
    if not 118_000 <= khz <= 136_990:
        return
    key = cs[:-4] if center else cs
    freqs = seen.setdefault(key, {})
    freqs[str(khz)] = freqs.get(str(khz), 0) + 1


def main(extra):
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "data", "world", "vatsim-frequencies-seed.json")
    seen = {}
    for path in ([out] if os.path.exists(out) else []) + extra:
        with open(path, encoding="utf-8") as f:
            for k, v in json.load(f).items():
                freqs = seen.setdefault(k, {})
                for khz, n in v.items():
                    freqs[khz] = max(freqs.get(khz, 0), n)
    req = urllib.request.Request(FEED, headers={"User-Agent": "LUAW-data-gen"})
    with urllib.request.urlopen(req, timeout=60) as r:
        feed = json.load(r)
    for c in feed.get("controllers", []):
        add(seen, c.get("callsign"), c.get("frequency"), c.get("facility"))
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(dict(sorted(seen.items())), f, separators=(",", ":"))
    apps = sum(1 for k in seen if k.endswith("_APP") or k.endswith("_DEP"))
    print(f"{len(seen)} indicativos ({apps} de Aproximación/Salidas)")


if __name__ == "__main__":
    main(sys.argv[1:])
