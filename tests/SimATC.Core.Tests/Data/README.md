# Test airports

The tests use airports saved from Microsoft Flight Simulator 2024 (`<ICAO>.json`: runways, taxiways, stands and
procedures). They are simulator data, so they are **not published**: tests that need one are skipped when the file is
missing.

To run them, copy airport files from your own SimATC cache (`%LOCALAPPDATA%\SimATC\cache\airports`, created when SimATC
loads an airport) into this folder. The tests use SCEL, SCIE, LEMD, LEBL, LEMG, LEVC, RKPK, RKSI and KEWR.
