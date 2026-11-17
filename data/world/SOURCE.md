# Source of data/world

- Routes by callsign and airports: [vradarserver/standing-data](https://github.com/vradarserver/standing-data),
  **CC0 1.0** (public domain). Copy from 2026-11-01.
- Generated with `python -I tools/data-gen/build_world.py <folder> <date>`.
- Airline callsigns only (3-letter ICAO + number), with all their airports in ICAO and with coordinates.
- 626,412 routes, 3,790 airports.
