"""Prueba en lote del Headless en muchos aeropuertos (ADR-093).

Por cada aeropuerto de la caché de LUAW (%LOCALAPPDATA%/LUAW/cache/airports) arma un escenario con reparto
automático de pistas, llegadas y salidas según sus puertas, y viento por su pista principal (y a 180° con --both-winds);
corre el Headless y junta las métricas en TestResults/batch-<fecha>.md y .json.

Uso: python tools/headless-batch.py [--top 40] [--small 10] [--seeds 1] [--hours 0.75] [--speed 40] [--both-winds]
     [--only LEMD,EGLL]
"""
import argparse
import datetime
import json
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(os.environ["LOCALAPPDATA"], "LUAW", "cache", "airports")
HEADLESS = os.path.join(ROOT, "tools", "SimATC.Headless")
SCEN_DIR = os.path.join(HEADLESS, "bin", "Debug", "net10.0", "Scenarios")
OUT_DIR = os.path.join(ROOT, "TestResults")
GATES = {"GateSmall", "GateMedium", "GateHeavy", "GateExtra"}


def survey():
    rows = []
    for f in os.listdir(CACHE):
        if not f.endswith(".json"):
            continue
        try:
            with open(os.path.join(CACHE, f), encoding="utf-8") as h:
                d = json.load(h)
        except (OSError, ValueError):
            continue
        a = d.get("Airport", d)
        gates = sum(1 for p in a.get("Parkings", []) if p.get("Type") in GATES)
        runways = a.get("Runways", [])
        if not runways or gates < 2:
            continue
        longest = max(runways, key=lambda r: r["LengthM"])
        # Pistas para aerolíneas (como RunwayPlanner.PlannerRunwayIndices): al menos 1800 m o el 80 % de la más larga.
        airline = sum(1 for r in runways if r["LengthM"] >= min(1800, longest["LengthM"] * 0.8) - 1)
        rows.append({"icao": a["Icao"], "gates": gates, "runways": airline,
                     "heading": longest["Primary"]["HeadingTrueDeg"] if "HeadingTrueDeg" in longest["Primary"] else longest["HeadingTrueDeg"]})
    rows.sort(key=lambda r: -r["gates"])
    return rows


def scenario(a, wind, seed, hours):
    # Demanda por puertas, para llegadas y para salidas. APROXIMADO.
    # Con el tope de TrafficDirector (ADR-130): 14 por pista de llegada en uso y hora (ADR-131). Es el peor caso de la app con una pista.
    per_hour = max(3, min(14, a["gates"] * 0.36))
    interval = max(90, round(3600 / per_hour))
    return {"airport": a["icao"], "seed": seed, "hours": hours, "runways": ["auto"], "departures": [],
            "windFromDeg": wind % 360, "windKt": 8,
            "flow": {"startS": 0, "intervalS": interval, "maxOnGround": max(3, min(16, a["gates"] // 3))},
            "arrivals": {"startS": 30, "intervalS": interval, "runways": ["auto"]}}


def run(name, speed):
    out = os.path.join(OUT_DIR, f"hl-{name}.json")
    subprocess.run(["dotnet", "run", "--no-build", "--project", HEADLESS, "--", "--scenario", name, "--speed", str(speed),
                    "--out", out], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=1800)
    try:
        with open(out, encoding="utf-8") as h:
            return json.load(h)
    except (OSError, ValueError):
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--top", type=int, default=40)
    ap.add_argument("--small", type=int, default=10)
    ap.add_argument("--seeds", type=int, default=1)
    ap.add_argument("--hours", type=float, default=0.75)
    ap.add_argument("--speed", type=float, default=40)
    ap.add_argument("--both-winds", action="store_true")
    ap.add_argument("--only", default="")
    args = ap.parse_args()
    os.makedirs(OUT_DIR, exist_ok=True)
    rows = survey()
    if args.only:
        wanted = set(args.only.upper().split(","))
        picked = [r for r in rows if r["icao"] in wanted]
    else:
        small = [r for r in rows if r["gates"] < 10]
        step = max(1, len(small) // max(1, args.small))
        picked = rows[: args.top] + small[::step][: args.small]
    results = []
    for a in picked:
        winds = [a["heading"] + 180, a["heading"]] if args.both_winds else [a["heading"] + 180]
        for wi, wind in enumerate(winds):
            for seed in range(1, args.seeds + 1):
                name = f"batch-{a['icao']}-{wi}-{seed}"
                with open(os.path.join(SCEN_DIR, name + ".json"), "w", encoding="utf-8") as h:
                    json.dump(scenario(a, wind, seed, args.hours), h)
                m = run(name, args.speed)
                os.remove(os.path.join(SCEN_DIR, name + ".json"))
                row = {"icao": a["icao"], "gates": a["gates"], "wind": round(wind % 360), "seed": seed, "metrics": m}
                results.append(row)
                if m:
                    print(f"{a['icao']:5} viento {round(wind % 360):3}° s{seed}: {'VERDE' if m.get('Green') else 'FALLA'} "
                          f"| {m.get('RunwayPlan', '')} | salidas {m['Departed']}/{m['Launched']} | llegadas "
                          f"{m.get('ArrivalsLanded', 0)}/{m.get('ArrivalsSpawned', 0)} ga {m.get('GoArounds', 0)} | "
                          f"pérdidas {m['SeparationLosses']} | bloqueos sin resolver {m['DeadlocksUnresolved']} | "
                          f"excepciones {m['Exceptions']}", flush=True)
                else:
                    print(f"{a['icao']:5} viento {round(wind % 360):3}° s{seed}: SIN RESULTADO (el Headless falló)", flush=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M")
    with open(os.path.join(OUT_DIR, f"batch-{stamp}.json"), "w", encoding="utf-8") as h:
        json.dump(results, h, indent=1)
    ok = sum(1 for r in results if r["metrics"] and r["metrics"].get("Green"))
    print(f"\nTOTAL: {ok}/{len(results)} en verde. Detalle en TestResults/batch-{stamp}.json")


if __name__ == "__main__":
    sys.exit(main())
