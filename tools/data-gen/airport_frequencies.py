"""Frecuencias públicas de Aproximación, Salidas y Centro por aeropuerto (ADR-173), de OurAirports (Unlicense).

Respaldo para cuando el simulador no trae esas frecuencias (EGLL en MSFS 2024 solo tiene Torre, Ground y Delivery) y
tampoco se vieron en VATSIM. Se guardan solo los tipos que LUAW usa:
  APP, DIR, A/D -> Approach;  DEP -> Departure;  CNTR, ACC -> Center.

Uso: python -I tools/data-gen/airport_frequencies.py <airport-frequencies.csv>
     (https://davidmegginson.github.io/ourairports-data/airport-frequencies.csv)
Escribe data/world/airport-frequencies.csv.gz (ident,role,khz,name).
"""
import csv
import gzip
import os
import sys

ROLES = {"APP": "Approach", "DIR": "Approach", "A/D": "Approach", "DEP": "Departure", "CNTR": "Center", "ACC": "Center"}


def main(src):
    rows = []
    with open(src, encoding="utf-8", newline="") as f:
        for r in csv.DictReader(f):
            role = ROLES.get((r.get("type") or "").strip().upper())
            ident = (r.get("airport_ident") or "").strip().upper()
            try:
                khz = round(float(r.get("frequency_mhz") or 0) * 1000)
            except ValueError:
                continue
            # Solo OACI de 4 letras y VHF aeronáutica (118-136,975 MHz).
            if role is None or len(ident) != 4 or not ident.isalpha() or not 118_000 <= khz <= 136_990:
                continue
            name = " ".join((r.get("description") or "").replace(",", " ").split())
            rows.append((ident, role, khz, name))
    rows.sort()
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "data", "world", "airport-frequencies.csv.gz")
    with open(out, "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", compresslevel=9, mtime=0) as g:
        g.write(("ident,role,khz,name\n" + "".join(f"{i},{r},{k},{n}\n" for i, r, k, n in rows)).encode("utf-8"))
    print(f"{len(rows)} frecuencias de {len({r[0] for r in rows})} aeropuertos")


if __name__ == "__main__":
    main(sys.argv[1])
