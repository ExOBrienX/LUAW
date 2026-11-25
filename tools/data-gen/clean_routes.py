"""Quita de las rutas los números de código compartido dentro de Estados Unidos y Canadá (ADR-167).

La base (vradarserver/standing-data) trae como vuelos propios los números que una aerolínea extranjera vende en vuelos de otra
(LAN6866 KJFK-KMIA lo vuela otra aerolínea). Una aerolínea extranjera no puede llevar pasajeros entre dos ciudades de esos países
(cabotaje), así que un indicativo con solo ese tramo no existe como vuelo. Se conservan:
  - los de varias escalas (RKSI-PANC-KMIA-KJFK: el tramo interno es parte de un vuelo internacional);
  - los que tocan Anchorage, Fairbanks o Guam (escalas técnicas de carga).

Uso: python -I tools/data-gen/clean_routes.py   (reescribe data/world/routes.csv.gz)
"""
import collections
import gzip
import os

US_TERRITORIES = ("PA", "PH", "PG", "TJ", "TI", "PF", "PO", "PP")
TECH_STOPS = {"PANC", "PAFA", "PGUM"}


def region(icao):
    """País aproximado por el prefijo OACI (solo para saber de dónde es una aerolínea y qué tramos son internos)."""
    if icao[0] in "KCY":
        return icao[0]
    if icao[:2] in US_TERRITORIES:
        return "K"
    if icao[0] == "Z" and icao[:2] not in ("ZK", "ZM"):
        return "Z"
    return icao[:2]


def home_regions(routes):
    """La región que más tocan las rutas de cada aerolínea."""
    seen = collections.defaultdict(collections.Counter)
    for callsign, codes in routes:
        for c in codes.split("-"):
            seen[callsign[:3]][region(c)] += 1
    return {a: c.most_common(1)[0][0] for a, c in seen.items()}


def foreign_domestic(callsign, codes, homes):
    stops = codes.split("-")
    if len(stops) != 2 or TECH_STOPS & set(stops):
        return False
    a, b = region(stops[0]), region(stops[1])
    return a == b and a in "KC" and homes[callsign[:3]] != a


def main():
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "data", "world", "routes.csv.gz")
    with gzip.open(path, "rt", encoding="utf-8") as f:
        header, *rows = f.read().splitlines()
    routes = [tuple(r.split(",")) for r in rows]
    homes = home_regions(routes)
    keep = [r for r in routes if not foreign_domestic(r[0], r[1], homes)]
    with open(path, "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=9, mtime=0) as g:
        g.write(("\n".join([header] + [",".join(r) for r in keep]) + "\n").encode("utf-8"))
    print(f"{len(routes)} rutas, {len(routes) - len(keep)} quitadas, quedan {len(keep)}")


if __name__ == "__main__":
    main()
