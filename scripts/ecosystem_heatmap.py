#!/usr/bin/env python3
"""
Ecosystem heatmap & timeline report generator.

Reads the EcosystemLogger CSVs (events_*.csv + population_*.csv) and produces a single
self-contained HTML file (inline SVG, no external dependencies) with:

  * Spatial heatmaps — where chosen events happen (all deaths, by cause, kills, births),
    binned into a grid over the world, so you can see where species cluster and die.
  * Population timeline — total + top species over time (line chart).
  * Event-rate timeline — births / deaths-by-cause / kills per tick bin (stacked area).

Stdlib only (csv, html, math) so it runs anywhere Python 3.8+ does.

Usage:
    python3 scripts/ecosystem_heatmap.py EVENTS.csv POPULATION.csv [-o report.html]
                                         [--grid 56] [--species Deer,Wolf]
If paths are omitted it picks the newest events_*.csv / population_*.csv under ./logs.
"""
from __future__ import annotations
import argparse, csv, glob, html, math, os, sys
from collections import defaultdict, Counter

# Event types whose detail column carries extra info we surface.
DEATH_EVENTS = {"starvation", "age_death", "environment_death"}  # plus 'kill' (predation)


def newest(pattern: str):
    files = glob.glob(pattern)
    return max(files, key=os.path.getmtime) if files else None


def load_events(path: str):
    rows = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            try:
                r["tick"] = int(r["tick"])
                r["x"] = float(r["x"]); r["y"] = float(r["y"])
            except (ValueError, KeyError):
                continue
            rows.append(r)
    return rows


def load_population(path: str):
    with open(path, newline="") as f:
        reader = csv.reader(f)
        header = next(reader, [])
        species = header[2:]  # tick,total,<species...>
        ticks, series = [], {s: [] for s in species}
        totals = []
        for row in reader:
            if len(row) < 2:
                continue
            try:
                ticks.append(int(row[0])); totals.append(int(row[1]))
            except ValueError:
                continue
            for i, s in enumerate(species):
                try:
                    series[s].append(int(row[2 + i]))
                except (ValueError, IndexError):
                    series[s].append(0)
    return ticks, totals, series


# ── SVG helpers ─────────────────────────────────────────────────────────────
def heat_color(t: float) -> str:
    """t in 0..1 -> blue→cyan→yellow→red ramp."""
    if t <= 0:
        return "#10131a"
    stops = [(0.0, (16, 19, 26)), (0.25, (30, 90, 160)), (0.5, (40, 170, 170)),
             (0.75, (230, 200, 60)), (1.0, (220, 50, 40))]
    for (a, ca), (b, cb) in zip(stops, stops[1:]):
        if t <= b:
            f = (t - a) / (b - a) if b > a else 0
            return "#%02x%02x%02x" % tuple(int(ca[i] + (cb[i] - ca[i]) * f) for i in range(3))
    return "#dc3228"


def spatial_svg(points, minx, miny, maxx, maxy, grid, size=360, title=""):
    if maxx <= minx: maxx = minx + 1
    if maxy <= miny: maxy = miny + 1
    counts = [[0] * grid for _ in range(grid)]
    for x, y in points:
        cx = min(grid - 1, int((x - minx) / (maxx - minx) * grid))
        cy = min(grid - 1, int((y - miny) / (maxy - miny) * grid))
        counts[cy][cx] += 1
    peak = max((c for row in counts for c in row), default=0)
    cell = size / grid
    rects = []
    for cy in range(grid):
        for cx in range(grid):
            c = counts[cy][cx]
            if not c:
                continue
            t = math.log1p(c) / math.log1p(peak) if peak > 1 else (1.0 if c else 0)
            rects.append(f'<rect x="{cx*cell:.1f}" y="{cy*cell:.1f}" width="{cell:.2f}" '
                         f'height="{cell:.2f}" fill="{heat_color(t)}"/>')
    n = sum(c for row in counts for c in row)
    return (f'<div class="cell"><h3>{html.escape(title)} '
            f'<span class="sub">n={n}, peak/cell={peak}</span></h3>'
            f'<svg width="{size}" height="{size}" style="background:#10131a;border:1px solid #333">'
            f'{"".join(rects)}</svg></div>')


def line_svg(ticks, named_series, w=820, h=300, title=""):
    if not ticks:
        return ""
    tmin, tmax = ticks[0], ticks[-1]
    vmax = max((max(v) for v in named_series.values() if v), default=1) or 1
    pad = 40
    def sx(t): return pad + (t - tmin) / (tmax - tmin or 1) * (w - 2 * pad)
    def sy(v): return h - pad - v / vmax * (h - 2 * pad)
    palette = ["#e63", "#6cf", "#7d7", "#fc6", "#c9f", "#f99", "#9cc", "#fd9", "#9f9", "#caf"]
    paths, legend = [], []
    for i, (name, vals) in enumerate(named_series.items()):
        col = palette[i % len(palette)]
        pts = " ".join(f"{sx(t):.1f},{sy(v):.1f}" for t, v in zip(ticks, vals))
        paths.append(f'<polyline fill="none" stroke="{col}" stroke-width="1.5" points="{pts}"/>')
        legend.append(f'<span style="color:{col}">&#9632; {html.escape(name)}</span>')
    axis = (f'<line x1="{pad}" y1="{h-pad}" x2="{w-pad}" y2="{h-pad}" stroke="#555"/>'
            f'<line x1="{pad}" y1="{pad}" x2="{pad}" y2="{h-pad}" stroke="#555"/>'
            f'<text x="{pad}" y="{pad-8}" fill="#999" font-size="11">max {vmax}</text>'
            f'<text x="{w-pad}" y="{h-pad+18}" fill="#999" font-size="11" text-anchor="end">tick {tmax}</text>')
    return (f'<div class="cell wide"><h3>{html.escape(title)}</h3>'
            f'<svg width="{w}" height="{h}" style="background:#10131a;border:1px solid #333">'
            f'{axis}{"".join(paths)}</svg><div class="legend">{" ".join(legend)}</div></div>')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("events", nargs="?", default=newest("logs/events_*.csv"))
    ap.add_argument("population", nargs="?", default=newest("logs/population_*.csv"))
    ap.add_argument("-o", "--out", default="ecosystem_report.html")
    ap.add_argument("--grid", type=int, default=56, help="spatial heatmap resolution")
    ap.add_argument("--species", default="", help="comma list to focus spatial death maps")
    args = ap.parse_args()

    if not args.events or not os.path.exists(args.events):
        sys.exit("No events CSV found (pass it as the first argument).")
    events = load_events(args.events)
    if not events:
        sys.exit("Events CSV had no usable rows.")

    xs = [e["x"] for e in events]; ys = [e["y"] for e in events]
    minx, maxx, miny, maxy = min(xs), max(xs), min(ys), max(ys)

    # Spatial maps: all deaths, predation (kill), starvation, births.
    def pts(pred): return [(e["x"], e["y"]) for e in events if pred(e)]
    blocks = [
        spatial_svg(pts(lambda e: e["event"] in DEATH_EVENTS or e["event"] == "kill"),
                    minx, miny, maxx, maxy, args.grid, title="All deaths"),
        spatial_svg(pts(lambda e: e["event"] == "kill"),
                    minx, miny, maxx, maxy, args.grid, title="Predation (kills)"),
        spatial_svg(pts(lambda e: e["event"] == "starvation"),
                    minx, miny, maxx, maxy, args.grid, title="Starvation"),
        spatial_svg(pts(lambda e: e["event"] == "reproduce" or e["event"] == "birth"),
                    minx, miny, maxx, maxy, args.grid, title="Births / reproduction"),
    ]
    for sp in [s.strip() for s in args.species.split(",") if s.strip()]:
        blocks.append(spatial_svg(
            pts(lambda e, sp=sp: e["species"] == sp and (e["event"] in DEATH_EVENTS or e["event"] == "kill")),
            minx, miny, maxx, maxy, args.grid, title=f"{sp} deaths"))

    # Timelines from population CSV (+ event-rate from events).
    charts = []
    if args.population and os.path.exists(args.population):
        ticks, totals, series = load_population(args.population)
        top = sorted(series, key=lambda s: max(series[s]) if series[s] else 0, reverse=True)[:8]
        named = {"total": totals}; named.update({s: series[s] for s in top})
        charts.append(line_svg(ticks, named, title="Population over time (total + top 8)"))

    # Event-rate over time (binned).
    if events:
        tmax = max(e["tick"] for e in events); nbins = 60
        binw = max(1, (tmax + 1) // nbins)
        cats = ["reproduce", "kill", "starvation", "age_death", "environment_death"]
        binned = {c: defaultdict(int) for c in cats}
        for e in events:
            if e["event"] in binned:
                binned[e["event"]][e["tick"] // binw] += 1
        bticks = [b * binw for b in range(tmax // binw + 1)]
        named = {c: [binned[c].get(b, 0) for b in range(len(bticks))] for c in cats}
        charts.append(line_svg(bticks, named, title=f"Events per {binw}-tick bin"))

    summary = Counter(e["event"] for e in events)
    summ = ", ".join(f"{k}={v}" for k, v in summary.most_common())

    out = f"""<!doctype html><meta charset=utf-8><title>Ecosystem report</title>
<style>body{{background:#0b0d12;color:#cdd;font:14px system-ui,sans-serif;margin:18px}}
h1{{font-size:20px}} h3{{font-size:14px;margin:6px 0}} .sub{{color:#888;font-weight:400}}
.grid{{display:flex;flex-wrap:wrap;gap:16px}} .cell{{}} .wide{{flex-basis:100%}}
.legend{{font-size:12px;margin-top:4px}} .legend span{{margin-right:12px}}</style>
<h1>Ecosystem report</h1>
<p class=sub>events: {html.escape(os.path.basename(args.events))} &nbsp; ({summ})</p>
<div class=grid>{''.join(charts)}{''.join(blocks)}</div>
"""
    with open(args.out, "w") as f:
        f.write(out)
    print(f"Wrote {args.out}  ({len(events)} events, world x[{minx:.0f},{maxx:.0f}] y[{miny:.0f},{maxy:.0f}])")


if __name__ == "__main__":
    main()
