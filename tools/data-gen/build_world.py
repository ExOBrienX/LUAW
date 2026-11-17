"""Arma data/world/ desde una copia de vradarserver/standing-data (CC0), para el tráfico mundial (RF-12d, capa 1).

Uso: python -I tools/data-gen/build_world.py <carpeta standing-data> [fecha AAAA-MM-DD]

Salida:
  data/world/routes.csv.gz   Callsign,AirportCodes   solo indicativos de aerolínea (OACI de 3 letras + número)
  data/world/airports.csv    ICAO,Lat,Lon,ElevFt     solo los aeropuertos que aparecen en alguna ruta
  data/world/SOURCE.md       fuente, licencia y fecha
"""
import csv
import gzip
import io
import os
import re
import sys

CALLSIGN = re.compile(r"^[A-Z]{3}[0-9]{1,4}[A-Z]{0,2}$")
ICAO = re.compile(r"^[A-Z]{4}$")


def main():
    src = sys.argv[1]
    date = sys.argv[2] if len(sys.argv) > 2 else "?"
    out = os.path.join(os.path.dirname(__file__), "..", "..", "data", "world")
    os.makedirs(out, exist_ok=True)

    routes = []
    used = set()
    for root, _, files in os.walk(os.path.join(src, "routes", "schema-01")):
        for name in sorted(files):
            if not name.endswith(".csv"):
                continue
            with open(os.path.join(root, name), encoding="utf-8-sig", newline="") as f:
                for row in csv.DictReader(f):
                    cs = row["Callsign"].strip().upper()
                    codes = row["AirportCodes"].strip().upper().split("-")
                    # Solo rutas con códigos OACI (hay algunas con IATA) y al menos dos aeropuertos distintos.
                    if not CALLSIGN.match(cs) or len(codes) < 2 or not all(ICAO.match(c) for c in codes):
                        continue
                    if len(set(codes)) < 2:
                        continue
                    routes.append((cs, "-".join(codes)))
                    used.update(codes)
    routes.sort()

    airports = {}
    for root, _, files in os.walk(os.path.join(src, "airports", "schema-01")):
        for name in files:
            if not name.endswith(".csv"):
                continue
            with open(os.path.join(root, name), encoding="utf-8-sig", newline="") as f:
                for row in csv.DictReader(f):
                    icao = (row.get("ICAO") or "").strip().upper()
                    if icao in used and row["Latitude"] and row["Longitude"]:
                        airports[icao] = (float(row["Latitude"]), float(row["Longitude"]), int(float(row["AltitudeFeet"] or 0)))

    # Rutas cuyos aeropuertos tienen coordenadas (si falta uno, la ruta no sirve para ubicar el vuelo).
    routes = [r for r in routes if all(c in airports for c in r[1].split("-"))]

    buf = io.StringIO()
    buf.write("Callsign,AirportCodes\n")
    for cs, codes in routes:
        buf.write(f"{cs},{codes}\n")
    # mtime=0: el mismo contenido da el mismo archivo (sin cambios falsos en git).
    with open(os.path.join(out, "routes.csv.gz"), "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=9, mtime=0) as g:
        g.write(buf.getvalue().encode("utf-8"))

    with open(os.path.join(out, "airports.csv"), "w", encoding="utf-8", newline="\n") as f:
        f.write("ICAO,Lat,Lon,ElevFt\n")
        for icao in sorted(airports):
            lat, lon, elev = airports[icao]
            f.write(f"{icao},{lat:.6f},{lon:.6f},{elev}\n")

    with open(os.path.join(out, "SOURCE.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("# Fuente de data/world\n\n")
        f.write("- Rutas por indicativo y aeropuertos: [vradarserver/standing-data](https://github.com/vradarserver/standing-data), ")
        f.write("licencia **CC0 1.0** (dominio público). Copia del " + date + ".\n")
        f.write("- Generado con `python -I tools/data-gen/build_world.py <carpeta> <fecha>` (ADR-029, RF-12d capa 1).\n")
        f.write("- Solo indicativos de aerolínea (OACI de 3 letras + número) con todos sus aeropuertos en OACI y con coordenadas.\n")
        f.write(f"- {len(routes)} rutas, {len(airports)} aeropuertos.\n")
    print(f"{len(routes)} rutas, {len(airports)} aeropuertos")


if __name__ == "__main__":
    main()
