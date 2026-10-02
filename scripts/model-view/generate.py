#!/usr/bin/env python3
"""Regenerate docs/MODEL_VIEW.html from one Lurp index.

Index a solution and render the page (one command):

    python scripts/model-view/generate.py --lurp src/bin/Release/net10.0/Lurp.exe \
        --solution <path>/eNote/eNote.sln --work-dir <empty folder>

Render again from an index you already have (no reindex):

    python scripts/model-view/generate.py --db <folder>/index.db --index-log <folder>/index-run.log

The page styles (fonts, colors, layout) come from the first <style> block of
--template, which defaults to the current docs/MODEL_VIEW.html. Everything else
is read from index.db and from the index run's output. Python standard library
only.
"""

import argparse
import html
import re
import sqlite3
import subprocess
import sys
import time
from collections import Counter, defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PAGE = REPO_ROOT / "docs" / "MODEL_VIEW.html"

# Provenance vocabulary, strongest first (docs/ARCHITECTURE.md, provenance table).
TIERS = [
    ("compiler_proved", "The compiler resolved it. Calls, inheritance, field reads: verified, not guessed."),
    ("framework_derived", "An adapter read a framework convention (DI registration, EF Core mapping, ASP.NET routing, test discovery) and emitted a typed fact."),
    ("global_implementation_relation", "An implementation relationship that holds across the whole graph."),
    ("possible", "An interface dispatch that could not be narrowed to one concrete implementation."),
    ("convention", "Inferred from a convention such as an assembly scan, not from a direct reference."),
    ("name_candidate", "The name argument of a runtime name-binding API (reflection lookup, EF Core string API, binding attribute), resolved semantically. Not compiler-checked."),
    ("runtime_unknown", "The target exists only at run time. Lurp records the blind spot instead of guessing a target."),
]

EDGE_CLASS = {
    "compiler_proved": "edge-cp",
    "global_implementation_relation": "edge-cp",
    "framework_derived": "edge-fw",
    "convention": "edge-cv",
}

ASSEMBLY_NODE_CLASSES = ["node-services", "node-common", "node-webapi", "node-db"]

EXTRA_CSS = """
.tier-global_implementation_relation .tier-line, .tier-swatch.tier-global_implementation_relation { border-color: var(--brass-soft); background: var(--brass-soft); }
.tier-convention .tier-line { border-top-style: dashed; border-color: var(--verdigris-soft); }
.tier-runtime_unknown .tier-line { border-top-style: dotted; border-color: var(--ink-dim); }
.tier-swatch.tier-name_candidate { background: var(--ink-dim); }
.tier-swatch.tier-possible { background: var(--ink-faint); }
.tier-swatch.tier-convention { background: var(--verdigris-soft); }
.tier-swatch.tier-runtime_unknown { background: var(--line-mid); }
.ladder-tier.tier-empty { opacity: 0.5; }
.panel { overflow-x: auto; }
.chart-scroll .edge-cv { stroke: var(--verdigris-soft); stroke-width: 1.1; stroke-dasharray: 6 3; fill: none; opacity: 0.7; }
.chart-scroll .edge-rt { stroke: var(--ink-faint); stroke-width: 1.1; stroke-dasharray: 2 3; fill: none; opacity: 0.8; }
.chart-scroll .node-unknown circle { fill: none; stroke: var(--ink-faint); stroke-width: 1; stroke-dasharray: 2 2; }
.chart-scroll .node-unknown text { font-family: var(--mono); font-size: 10px; fill: var(--ink-faint); font-style: italic; }
.chart-scroll .node-services text, .chart-scroll .node-common text, .chart-scroll .node-webapi text { font-size: 10.5px; }
.chart-scroll .legend { font-family: var(--mono); font-size: 10.5px; fill: var(--ink-faint); }
.chart-scroll .group-label { font-family: var(--display); font-weight: 600; font-size: 15px; fill: var(--ink); }
""".strip("\n")


def esc(text):
    return html.escape(str(text), quote=True)


def num(value):
    return f"{value:,}"


def breakable(name):
    """Escape a snake_case name and let it wrap after each underscore."""
    return esc(name).replace("_", "_<wbr>")


def doc_id(symbol_id):
    return symbol_id.split("|", 1)[0]


def assembly_of(symbol_id):
    return symbol_id.split("|", 1)[1].split(",", 1)[0] if "|" in symbol_id else ""


def split_top_level(fqn):
    parts, depth, current = [], 0, []
    for ch in fqn:
        if ch in "<(":
            depth += 1
        elif ch in ">)":
            depth -= 1
        if ch == "." and depth == 0:
            parts.append("".join(current))
            current = []
        else:
            current.append(ch)
    parts.append("".join(current))
    return parts


class Names:
    """Display names for symbol IDs and for the non-symbol graph nodes."""

    def __init__(self, fqns):
        self.fqns = fqns

    def fqn(self, node):
        if node in self.fqns and self.fqns[node]:
            return self.fqns[node].removeprefix("global::")
        if node.startswith("route://"):
            return "route " + node[len("route://"):]
        if node.startswith("convention:assembly_scan:"):
            return "assembly scan of " + node[len("convention:assembly_scan:"):].split(",", 1)[0]
        if "|" not in node:
            return node
        return doc_id(node)[2:]

    def short(self, node):
        if node.startswith("route://"):
            return node[len("route://"):]
        if node == "runtime:unknown":
            return "runtime:unknown"
        if node.startswith("convention:assembly_scan:"):
            return "assembly scan: " + node[len("convention:assembly_scan:"):].split(",", 1)[0]
        parts = split_top_level(self.fqn(node))
        last = parts[-1]
        if last.startswith("<") and len(parts) > 1:
            last = parts[-2]
        return re.sub(r"<.*>$", "", last)


class Index:
    def __init__(self, db_path):
        self.conn = sqlite3.connect(f"file:{Path(db_path).as_posix()}?mode=ro", uri=True)
        row = self.conn.execute(
            "SELECT snapshot_id, workspace_id, built_at_utc, sdk_version, compiler_version, "
            "database_schema_version, output_schema_version, extractor_version, tool_version "
            "FROM snapshots WHERE status = 'complete' ORDER BY built_at_utc DESC LIMIT 1").fetchone()
        if row is None:
            sys.exit("ERROR: no complete snapshot in the index.")
        (self.snapshot_id, self.workspace_id, self.built_at, self.sdk, self.roslyn,
         self.schema, self.output_schema, self.extractor, self.tool) = row
        sid = self.snapshot_id
        self.solution_path = (self.conn.execute(
            "SELECT solution_path FROM workspaces WHERE workspace_id = ?", (self.workspace_id,)).fetchone() or [""])[0]
        self.git_root = (self.conn.execute(
            "SELECT git_root FROM workspaces WHERE workspace_id = ?", (self.workspace_id,)).fetchone() or [""])[0]
        self.symbol_kinds = Counter(dict(self.conn.execute(
            "SELECT s.kind, COUNT(*) FROM snapshot_symbols ss JOIN symbols s USING (symbol_id) "
            "WHERE ss.snapshot_id = ? GROUP BY s.kind", (sid,)).fetchall()))
        self.names = Names(dict(self.conn.execute(
            "SELECT symbol_id, fqn FROM snapshot_symbols WHERE snapshot_id = ?", (sid,)).fetchall()))
        self.in_snapshot = set(self.names.fqns)
        self.edges = self.conn.execute(
            "SELECT source_symbol_id, target_symbol_id, kind, COALESCE(provenance, '') "
            "FROM edges WHERE snapshot_id = ?", (sid,)).fetchall()
        self.projects = [r[0] for r in self.conn.execute(
            "SELECT name FROM projects WHERE snapshot_id = ? ORDER BY name", (sid,))]
        self.extractors = dict(self.conn.execute("SELECT name, version FROM extractors").fetchall())
        self.diagnostics = self.conn.execute(
            "SELECT project_name, severity, id FROM diagnostics WHERE snapshot_id = ?", (sid,)).fetchall()
        self.files = self.conn.execute(
            "SELECT d.relative_path, COUNT(decl.declaration_id) FROM snapshot_documents sd "
            "JOIN document_versions dv ON dv.document_version_id = sd.document_version_id "
            "JOIN documents d ON d.document_id = dv.document_id "
            "LEFT JOIN declarations decl ON decl.document_version_id = sd.document_version_id "
            "WHERE sd.snapshot_id = ? GROUP BY d.relative_path ORDER BY 2 DESC, 1", (sid,)).fetchall()

    def of_kind(self, kind):
        return [(s, t, p) for s, t, k, p in self.edges if k == kind]

    def test_assemblies(self):
        return {assembly_of(t) for _, t, _ in self.of_kind("TestedBy")} - {""}


def parse_index_log(text):
    projects = [(m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)))
                for m in re.finditer(r"^\s*\[([^\]]+)\] (\d+) symbols, (\d+) edges, (\d+) diagnostics\.", text, re.M)]

    def grab(pattern):
        m = re.search(pattern, text, re.M)
        return int(m.group(1)) if m else None

    dropped = re.search(r"edges_dropped_outside_snapshot_scope:\s*(\d+) \(external=(\d+), compiler_synthesized=(\d+), other=(\d+)\)", text)
    return {
        "projects": projects,
        "after_dedup": grab(r"edge_relations_after_dedup_this_run:\s*(\d+)"),
        "kept": grab(r"edge_relations_in_snapshot:\s*(\d+)"),
        "dropped": tuple(int(g) for g in dropped.groups()) if dropped else None,
        "total_ms": grab(r"Total time \(full rebuild\):\s*(\d+) ms"),
    }


def git(cwd, *args):
    try:
        out = subprocess.run(["git", "-C", str(cwd), *args], capture_output=True, text=True, timeout=30)
        return out.stdout.strip() if out.returncode == 0 else ""
    except (OSError, subprocess.TimeoutExpired):
        return ""


def source_info(index):
    root = Path(index.git_root) if index.git_root else None
    if root is None or not root.exists():
        return {"repo": "", "commit": "", "dirty": False, "solution": Path(index.solution_path).name}
    top = Path(git(root, "rev-parse", "--show-toplevel") or root)
    remote = git(top, "remote", "get-url", "origin")
    repo = re.sub(r"\.git$", "", remote.rstrip("/")).split("github.com/")[-1].split(":")[-1] if remote else top.name
    try:
        solution = Path(index.solution_path).resolve().relative_to(top.resolve()).as_posix()
    except ValueError:
        solution = Path(index.solution_path).name
    dirty = bool(git(top, "status", "--porcelain", "--", "*.cs", "*.csproj", "*.props", "*.targets", "*.sln", "*.slnx"))
    return {"repo": repo, "commit": git(top, "rev-parse", "--short", "HEAD"), "dirty": dirty, "solution": solution}


# ---------------------------------------------------------------- survey SVG

class Svg:
    def __init__(self):
        self.parts = []

    def add(self, markup):
        self.parts.append(markup)

    def region(self, x, y, w, h, label):
        self.add(f'<rect class="region" x="{x}" y="{y}" width="{w}" height="{h}"/>')
        self.add(f'<text class="region-label" x="{x + 20}" y="{y + 32}">{esc(label)}</text>')

    def node(self, cls, title, x, y, r, label, anchor="start", dx=None):
        dx = (r + 4) if dx is None else dx
        tx = x + dx if anchor == "start" else x - dx
        self.add(f'<g class="{cls}"><title>{esc(title)}</title><circle cx="{x}" cy="{y}" r="{r}"/>'
                 f'<text x="{tx}" y="{y + 4}" text-anchor="{anchor}">{esc(label)}</text></g>')

    def curve(self, cls, title, x1, y1, x2, y2, bend=0.45):
        cx1 = x1 + (x2 - x1) * bend
        cx2 = x2 - (x2 - x1) * bend
        self.add(f'<path class="{cls}" d="M{x1:.1f},{y1:.1f} C{cx1:.1f},{y1:.1f} {cx2:.1f},{y2:.1f} {x2:.1f},{y2:.1f}">'
                 f'<title>{esc(title)}</title></path>')

    def line(self, cls, title, x1, y1, x2, y2):
        self.add(f'<line class="{cls}" x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}"><title>{esc(title)}</title></line>')


def edge_class(provenance):
    return EDGE_CLASS.get(provenance, "edge-rt")


def draw_composition_root(svg, index, top, drawn):
    names = index.names
    tests = index.test_assemblies()
    registers = index.of_kind("Registers")
    implements = {(s, t) for s, t, _ in index.of_kind("Implements")}

    hub_targets = defaultdict(Counter)
    for s, t, p in registers:
        if s.startswith("M:") and assembly_of(s) not in tests:
            hub_targets[s][(t, p)] += 1
    if not hub_targets:
        return top
    ifaces_of = defaultdict(list)
    for s, t, _ in registers:
        if s.startswith("T:") and assembly_of(s) not in tests and (s, t) not in ifaces_of[t]:
            ifaces_of[t].append(s)

    impls = {t for targets in hub_targets.values() for t, _ in targets if t.startswith("T:")}
    asm_order = [a for a, _ in Counter(assembly_of(t) for t in impls).most_common()]
    asm_class = {a: ASSEMBLY_NODE_CLASSES[i % len(ASSEMBLY_NODE_CLASSES)] for i, a in enumerate(asm_order)}

    hubs = sorted(hub_targets, key=lambda h: (-sum(hub_targets[h].values()), names.fqn(h)))
    owner = {}
    groups = []
    for hub in hubs:
        rows = []
        for (target, prov), count in sorted(hub_targets[hub].items(), key=lambda kv: names.short(kv[0][0]).lower()):
            if target.startswith("T:"):
                if target in owner:
                    continue
                owner[target] = hub
            rows.append((target, prov, count, max(1, len(ifaces_of.get(target, [])))))
        groups.append((hub, rows))

    header_h, row_h, gap = 30, 20, 22
    col_x = [20, 750]
    col_w = 730
    heights = [70, 70]
    layout = []
    for hub, rows in groups:
        size = header_h + row_h * max(1, sum(r[3] for r in rows)) + gap
        col = 0 if heights[0] <= heights[1] else 1
        layout.append((col, top + heights[col], hub, rows))
        heights[col] += size
    height = max(heights) + 10

    svg.region(20, top, 1460, height, "Composition root · DI registration sites outside test projects")
    lx = 1460
    for asm in reversed(asm_order):
        label = f"{asm} ({sum(1 for t in impls if assembly_of(t) == asm)})"
        svg.add(f'<text class="legend" x="{lx}" y="{top + 32}" text-anchor="end">{esc(label)}</text>')
        lx -= len(label) * 6.4 + 8
        svg.add(f'<g class="{asm_class[asm]}"><circle cx="{lx}" cy="{top + 28}" r="4"/></g>')
        lx -= 18

    position = {}
    hub_pos = {}
    for col, y, hub, rows in layout:
        x0 = col_x[col]
        first = y + header_h
        n_rows = max(1, sum(r[3] for r in rows))
        hy = first + row_h * (n_rows - 1) / 2
        hx = x0 + 44
        total = sum(hub_targets[hub].values())
        unknown = sum(c for (t, p), c in hub_targets[hub].items() if not t.startswith("T:"))
        svg.add(f'<text class="group-label" x="{x0 + 24}" y="{y + 14}">{esc(names.short(hub))}</text>')
        note = f"{total} registration{'s' if total != 1 else ''}" + (f" · {unknown} without a type target" if unknown else "")
        svg.add(f'<text class="annot" x="{x0 + col_w - 20}" y="{y + 14}" text-anchor="end">{esc(note)}</text>')
        svg.add(f'<g class="node-hub"><title>{esc(names.fqn(hub))}  |  {esc(assembly_of(hub))}</title>'
                f'<circle cx="{hx}" cy="{hy:.1f}" r="11"/></g>')
        hub_pos[hub] = (hx, hy)
        row = 0
        for target, prov, count, span in rows:
            ty = first + row_h * row
            ix = x0 + 270
            position[target] = (ix, ty)
            label = names.short(target) + (f" ×{count}" if count > 1 else "")
            if target.startswith("T:"):
                svg.node(asm_class[assembly_of(target)], f"{names.fqn(target)}  |  {assembly_of(target)}", ix, ty, 6, label)
                drawn.add(target)
            else:
                svg.node("node-unknown", f"{names.fqn(target)} ({prov})", ix, ty, 5, label)
            svg.curve(edge_class(prov), f"Registers ({prov}): {names.short(hub)} -> {names.short(target)}",
                      hx + 11, hy, ix - 7, ty)
            for i, iface in enumerate(ifaces_of.get(target, [])):
                iy = ty + row_h * i
                fx = x0 + 515
                if (target, iface) in implements:
                    svg.line("edge-cp", f"Implements: {names.short(target)} -> {names.short(iface)}", ix + 7, iy - 2, fx - 5, iy - 2)
                svg.line("edge-fw", f"Registers: {names.short(iface)} -> {names.short(target)}", ix + 7, iy + 2, fx - 5, iy + 2)
                svg.node("node-iface", names.fqn(iface), fx, iy, 4, names.short(iface))
                drawn.add(iface)
            row += span
        drawn.add(hub)

    # A type registered by more than one site keeps one row; the other sites point at it.
    for hub in hubs:
        for (target, prov) in hub_targets[hub]:
            if target.startswith("T:") and owner[target] != hub:
                ix, ty = position[target]
                hx, hy = hub_pos[hub]
                svg.curve(edge_class(prov), f"Registers ({prov}): {names.short(hub)} -> {names.short(target)}",
                          hx + 11, hy, ix - 7, ty)
    return top + height


def draw_routing_surface(svg, index, top, drawn):
    names = index.names
    declares = {t: s for s, t, _ in index.of_kind("Declares")}
    routes = index.of_kind("RoutesTo")
    own_routes = Counter(declares[t] for _, t, _ in routes if t in declares)
    if not own_routes:
        return top
    inherits = defaultdict(list)
    for s, t, _ in index.of_kind("Inherits"):
        inherits[s].append(t)

    def parent(t):
        return next((b for b in inherits.get(t, []) if b in index.in_snapshot), None)

    controllers = set(own_routes)
    bases = set()
    for c in controllers:
        b = parent(c)
        while b is not None and b not in bases:
            bases.add(b)
            b = parent(b)
    nodes = controllers | bases

    def depth(t):
        d, b = 0, parent(t)
        while b is not None:
            d, b = d + 1, parent(b)
        return d

    group_heads = sorted(bases, key=lambda b: (depth(b), names.short(b)))
    groups = [(b, sorted((n for n in nodes if parent(n) == b and n not in bases), key=lambda n: names.short(n).lower()))
              for b in group_heads]
    loose = sorted((n for n in nodes if parent(n) is None and n not in bases), key=lambda n: names.short(n).lower())
    if loose:
        groups.append((None, loose))

    max_rows, row_h, header_h = 12, 32, 40
    columns = []  # each: list of (kind, payload, row)
    for head, children in groups:
        chunks = [children[i:i + max_rows] for i in range(0, max(len(children), 1), max_rows)] or [[]]
        for ci, chunk in enumerate(chunks):
            needed = len(chunk) + (2 if ci == 0 else 0)
            if ci == 0 and columns and len(columns[-1]) + needed + 1 <= max_rows + 2:
                col = columns[-1]
                col.append(("gap", None))
            else:
                col = []
                columns.append(col)
            col.append(("head" if ci == 0 else "cont", head))
            col.append(("gap", None))
            for child in chunk:
                col.append(("node", (child, head)))
    col_w = min(480, 1420 // max(len(columns), 1))
    height = 60 + max(len(c) for c in columns) * row_h + 20
    svg.region(20, top, 1460, height, "Routing surface · controllers that own routes, under their in-solution bases")

    # Links between columns run in a lane above the first row, so they never cross a label.
    lane = top + 50
    head_pos = {}
    pending = []
    buses = []
    for ci, col in enumerate(columns):
        x = 70 + ci * col_w
        anchor = None
        for ri, (kind, payload) in enumerate(col):
            y = top + 70 + ri * row_h
            if kind == "cont":
                anchor = (x, y)
                if payload is not None:
                    svg.add(f'<g class="node-iface"><title>{esc(names.fqn(payload))} (continued)</title>'
                            f'<circle cx="{x}" cy="{y}" r="3"/></g>')
                    buses.append((payload, x, y))
                continue
            if kind == "head":
                anchor = None
                head = payload
                if head is None:
                    svg.add(f'<text class="annot" x="{x - 20}" y="{y + 4}">no in-solution base</text>')
                    continue
                routes_here = own_routes.get(head, 0)
                svg.node("node-base", f"{names.fqn(head)}  |  {routes_here} own route{'s' if routes_here != 1 else ''}", x, y, 8, names.short(head))
                if routes_here:
                    svg.add(f'<text class="route-count" x="{x - 16}" y="{y + 4}" text-anchor="end">{routes_here}</text>')
                head_pos[head] = (x, y)
                drawn.add(head)
            elif kind == "node":
                child, head = payload
                count = own_routes.get(child, 0)
                inherited = f" + {own_routes[head]} inherited from {names.short(head)}" if head is not None and own_routes.get(head) else ""
                svg.node("node-webapi", f"{names.fqn(child)}  |  {count} own route{'s' if count != 1 else ''}{inherited}",
                         x + 20, y, 7, names.short(child))
                svg.add(f'<text class="route-count" x="{x + 4}" y="{y + 4}" text-anchor="end">{count}</text>')
                drawn.add(child)
                if head is not None:
                    pending.append((head, child, x + 20, y, anchor))
    for head, child, x, y, anchor in pending:
        hx, hy = anchor or head_pos[head]
        svg.curve("edge-cp", f"Inherits: {names.short(child)} -> {names.short(head)}", hx, hy + (3 if anchor else 8), x - 7, y, bend=0.15)

    def lane_link(title, start, end, end_radius):
        (sx, sy), (ex, ey) = start, end
        svg.add(f'<path class="edge-cp" d="M{sx},{sy - 8} V{lane} H{ex} V{ey - end_radius}"><title>{esc(title)}</title></path>')

    for head, x, y in buses:
        lane_link(f"Inherits: more controllers -> {names.short(head)}", head_pos[head], (x, y), 3)
    for head in group_heads:
        b = parent(head)
        if b in head_pos and head in head_pos:
            lane_link(f"Inherits: {names.short(head)} -> {names.short(b)}", head_pos[b], head_pos[head], 8)
    return top + height


def draw_persistence(svg, index, top, drawn):
    names = index.names
    maps = sorted(index.of_kind("MapsTo"), key=lambda e: (names.short(e[1]).lower(), names.short(e[0]).lower()))
    if not maps:
        return top
    cols, row_h = 3, 24
    rows = -(-len(maps) // cols)
    height = 60 + rows * row_h + 16
    svg.region(20, top, 1460, height, "Persistence bed · EF Core MapsTo, mapping source to entity")
    col_w = 1420 // cols
    for i, (src, target, prov) in enumerate(maps):
        col, row = divmod(i, rows)
        x0 = 40 + col * col_w
        y = top + 66 + row * row_h
        sx, ex = x0 + 190, x0 + 268
        svg.node("node-iface", names.fqn(src), sx, y, 4, names.short(src), anchor="end", dx=10)
        svg.line(edge_class(prov), f"MapsTo: {names.short(src)} -> {names.short(target)}", sx + 5, y, ex - 7, y)
        svg.node("node-db", names.fqn(target), ex, y, 6, names.short(target))
        drawn.update((src, target))
    return top + height


def survey_svg(index):
    svg = Svg()
    drawn = set()
    y = draw_composition_root(svg, index, 20, drawn)
    y = draw_routing_surface(svg, index, y + 30, drawn)
    y = draw_persistence(svg, index, y + 30, drawn)
    body = "\n".join(svg.parts)
    return (f'<svg viewBox="0 0 1500 {y + 20}" xmlns="http://www.w3.org/2000/svg" role="img" '
            f'aria-label="Semantic map of the indexed solution">\n{body}\n</svg>'), len(drawn & index.in_snapshot)


# ---------------------------------------------------------------- tables

def table(headers, rows, total=None, extra_class=""):
    head = "".join(f'<th class="num">{esc(h[1:])}</th>' if h.startswith("#") else f"<th>{esc(h)}</th>" for h in headers)
    body = "".join(f"<tr>{''.join(rows_cells)}</tr>" for rows_cells in rows)
    if total:
        body += total
    return f'<table class="data-table{extra_class}"><thead><tr>{head}</tr></thead><tbody>{body}</tbody></table>'


def td(value, cls=None):
    return f'<td class="{cls}">{value}</td>' if cls else f"<td>{value}</td>"


def node_cell(names, node):
    return td(f'{esc(names.short(node))}<span class="fqn">{esc(names.fqn(node))}</span>', "mono")


def ledger(title, names, edges):
    rows = [[node_cell(names, s), td("&#8594;", "arrow"), node_cell(names, t), td(esc(p), "muted")]
            for s, t, p in sorted(edges, key=lambda e: (names.fqn(e[0]).lower(), names.fqn(e[1]).lower(), e[2]))]
    return (f'    <details class="ledger">\n      <summary><span>{esc(title)} ({num(len(edges))})</span></summary>\n'
            f'      <div class="ledger-body">{table(["Source", "", "Target", "Provenance"], rows, extra_class=" ledger-table")}</div>\n'
            f'    </details>')


def render(index, log, info, style, out_path):
    names = index.names
    sid = index.snapshot_id
    total_edges = len(index.edges)
    prov_counts = Counter(p for _, _, _, p in index.edges)
    tiers = TIERS + [(p, "Not described in this page's vocabulary list.") for p in sorted(prov_counts) if p not in dict(TIERS)]
    used = sum(1 for p, _ in tiers if prov_counts.get(p))
    total_symbols = sum(index.symbol_kinds.values())
    solution_name = Path(index.solution_path).stem or "solution"
    where = f"{info['repo']}/{info['solution']}" if info["repo"] else info["solution"]

    svg, drawn_count = survey_svg(index)

    ladder = "\n".join(
        f'      <div class="ladder-tier tier-{esc(p)}{"" if prov_counts.get(p) else " tier-empty"}">\n'
        f'        <span class="tier-name"><span class="tier-line"></span><span>{breakable(p)}</span></span>\n'
        f'        <span class="tier-count">{num(prov_counts.get(p, 0))}</span>\n'
        f'        <span class="tier-desc">{esc(desc)}</span>\n      </div>' for p, desc in tiers)

    kind_rows = [[td(esc(k)), td(num(n), "num"), td(f"{n / total_symbols:.1%}", "num")]
                 for k, n in index.symbol_kinds.most_common()]
    kinds_table = table(["Symbol kind", "#Count", "#Share"], kind_rows,
                        f'<tr class="total-row"><td>Total declared symbols</td><td class="num">{num(total_symbols)}</td><td class="num">100%</td></tr>')

    tier_rows = [[td(f'<span class="tier-swatch tier-{esc(p)}"></span>{breakable(p)}'), td(num(prov_counts.get(p, 0)), "num"),
                  td(f"{prov_counts.get(p, 0) / total_edges:.1%}", "num")] for p, _ in tiers]
    tiers_table = table(["Evidence tier", "#Edges", "#Share"], tier_rows,
                        f'<tr class="total-row"><td>Total typed edges</td><td class="num">{num(total_edges)}</td><td class="num">100%</td></tr>')

    by_project = Counter((p, s) for p, s, _ in index.diagnostics)
    diag_rows = [[td(esc(p)), td(esc(s), "muted"), td(num(n), "num")] for (p, s), n in sorted(by_project.items())]
    diag_table = table(["Project", "Severity", "#Count"], diag_rows,
                       f'<tr class="total-row"><td colspan="2">Total diagnostics</td><td class="num">{num(len(index.diagnostics))}</td></tr>')
    id_rows = [[td(esc(i), "mono"), td(num(n), "num")] for i, n in Counter(i for _, _, i in index.diagnostics).most_common()]
    diag_ids = table(["Diagnostic ID", "#Occurrences"], id_rows)

    if log and log["projects"]:
        proj_rows = [[td(esc(n)), td(num(s), "num"), td(num(e), "num"), td(num(d), "num")] for n, s, e, d in log["projects"]]
        raw = [sum(p[i] for p in log["projects"]) for i in (1, 2, 3)]
        total_row = (f'<tr class="total-row"><td>{len(log["projects"])} projects (raw, pre-dedup)</td><td class="num">{num(raw[0])}</td>'
                     f'<td class="num">{num(raw[1])}</td><td class="num">{num(raw[2])}</td></tr>')
        if log["after_dedup"] is not None and log["dropped"] and log["kept"] is not None:
            d = log["dropped"]
            total_row += (f'<tr class="muted-row"><td colspan="4">After cross-project dedup: {num(log["after_dedup"])} edges. '
                          f'Scope filtering then drops {num(d[0])} ({num(d[1])} to external symbols, {num(d[2])} compiler-synthesized, '
                          f'{num(d[3])} other), so {num(log["kept"])} edges are kept in the snapshot.</td></tr>')
        per_project = (f'    <p class="section-lede">Raw figures from the index run, one row per project, before cross-project dedup '
                       f'collapses them into the snapshot\'s {num(total_edges)} kept edges.</p>\n'
                       f'    <div class="panel">{table(["Project", "#Symbols", "#Edges extracted", "#Diagnostics"], proj_rows, total_row)}</div>')
    else:
        per_project = '    <p class="section-lede">The index run output was not provided, so the raw per-project figures are not shown.</p>'

    edge_kind_rows = [[td(esc(k)), td(esc(p), "muted"), td(num(n), "num")]
                      for (k, p), n in sorted(Counter((k, p) for _, _, k, p in index.edges).items(), key=lambda kv: (-kv[1], kv[0]))]
    kind_count = len({k for _, _, k, _ in index.edges})
    edge_kinds = table(["Edge kind", "Provenance", "#Count"], edge_kind_rows,
                       f'<tr class="total-row"><td>Total</td><td></td><td class="num">{num(total_edges)}</td></tr>')

    ledgers = [
        ledger("Registers: dependency injection", names, index.of_kind("Registers")),
        ledger("Inherits: class hierarchy", names, index.of_kind("Inherits")),
        ledger("Implements: interface contracts", names, index.of_kind("Implements")),
        ledger("MapsTo: EF Core entity mappings", names, index.of_kind("MapsTo")),
        ledger("RoutesTo: ASP.NET route → action", names, index.of_kind("RoutesTo")),
    ]
    file_rows = [[td(str(i), "mono muted"), td(esc(path), "mono"), td(num(n), "num")] for i, (path, n) in enumerate(index.files, 1)]
    ledgers.append(f'    <details class="ledger">\n      <summary><span>All {num(len(index.files))} indexed files, by declaration count</span></summary>\n'
                   f'      <div class="ledger-body">{table(["#", "File", "#Declarations"], file_rows, extra_class=" ledger-table")}</div>\n'
                   f'    </details>')

    reflection = index.extractors.get("Reflection", "")
    meta = [("built", index.built_at), ("sdk", index.sdk), ("roslyn", index.roslyn), ("schema", f"v{index.schema}"),
            ("output schema", f"v{index.output_schema}"), ("extractor", index.extractor), ("reflection", reflection),
            ("tool", index.tool), ("projects", len(index.projects))]
    meta_html = "\n".join(f"      <span><b>{esc(k)}</b> {esc(v)}</span>" for k, v in meta if v not in ("", None))

    commit = ""
    if info["commit"]:
        commit = f" at commit <code>{esc(info['commit'])}</code>" + (" with uncommitted source changes" if info["dirty"] else "")
    timing = f"Full index took {log['total_ms'] / 1000:.1f}s into an empty output folder. " if log and log.get("total_ms") else ""

    page = f"""<!doctype html>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Chart of {esc(solution_name)}</title>
<style>{style}</style>
<style data-generated="model-view">
{EXTRA_CSS}
</style>

<div class="page">

  <header class="cartouche">
    <p class="eyebrow">A Lurp survey &middot; snapshot {esc(sid[:12])}&hellip;</p>
    <h1 class="hero">Chart of {esc(solution_name)}</h1>
    <p class="hero-sub">What a Roslyn semantic index sees when it looks at <strong>{esc(where)}</strong>: not a UML diagram, a graded record of proof. Every line below carries the evidence level Lurp assigned it, from &ldquo;the compiler checked this&rdquo; down to &ldquo;only known at run time.&rdquo; This page is a point-in-time demo of one snapshot, not living documentation. Run <code style="font-family:var(--mono);color:var(--brass)">lurp --version</code> to see which schema/extractor/contract versions <em>your</em> build reports.</p>
    <div class="meta-strip">
{meta_html}
    </div>
  </header>

  <section>
    <h2 class="section-title">The evidence ladder</h2>
    <p class="section-lede">Lurp never states a relationship without saying how sure it is. The vocabulary has {len(tiers)} levels; this snapshot uses {used} of them across all {num(total_edges)} typed edges. Dimmed levels have no edges here.</p>
    <div class="ladder-grid">
{ladder}
    </div>
  </section>

  <section>
    <h2 class="section-title">The survey</h2>
    <p class="section-lede">A curated cross-section of the graph: the dependency-injection registration sites, the ASP.NET routing surface, and the EF Core mappings. <span class="mono-inline">Brass</span> lines are compiler-proved; <span class="mono-inline">verdigris</span> lines are framework-derived; dashed lines are convention or runtime-unknown. Hover any point or line for its fully-qualified name.</p>
    <div class="chart-frame">
      <div class="chart-scroll">
        {svg}
      </div>
    </div>
    <p class="chart-footnote">This view draws {num(drawn_count)} of the {num(total_symbols)} indexed symbols: the registration sites outside test projects and the types they register, the controllers that own routes and their in-solution bases, and the mapped entities with their mapping sources. The full counts are below; nothing here replaces them.</p>
  </section>

  <section class="stats-grid">
    <div class="panel">
      <h3>Symbols, by kind</h3>
      {kinds_table}
    </div>
    <div class="panel">
      <h3>Evidence, by tier</h3>
      {tiers_table}
    </div>
    <div class="panel">
      <h3>Diagnostics</h3>
      {diag_table}{diag_ids}
    </div>
  </section>

  <section>
    <h2 class="section-title">Per-project extraction</h2>
{per_project}
  </section>

  <section>
    <h2 class="section-title">Edge kinds, complete</h2>
    <p class="section-lede">All {kind_count} relationship kinds Lurp extracted from this solution, with the evidence tier attached to each.</p>
    <div class="panel">{edge_kinds}</div>
  </section>

  <section style="display:flex; flex-direction:column; gap:0.9rem;">
    <h2 class="section-title" style="margin-bottom:0;">The full ledgers</h2>
    <p class="section-lede" style="margin-bottom:0;">Every edge of the five kinds the survey draws, unabridged, including the ones the diagram leaves out (test projects, external bases), and every indexed file.</p>

{chr(10).join(ledgers)}
  </section>

  <footer class="chart-footer">
    <p>Built by running Lurp {esc(index.tool)} from source against <code>{esc(where)}</code>{commit}:</p>
    <p><code>dotnet build src/Lurp.csproj -c Release</code><br>
    <code>python scripts/model-view/generate.py --lurp src/bin/Release/net10.0/Lurp.exe --solution &lt;path&gt;/{esc(info['solution'])} --work-dir &lt;empty folder&gt;</code></p>
    <p>{timing}Snapshot <code>{esc(sid)}</code>, schema v{esc(index.schema)}. Everything on this page comes from that one <code>index.db</code> and the index run's per-project counts: no re-parsing, no re-grepping. <code>scripts/model-view/</code> regenerates it.</p>
    <p class="credit">Lurp &middot; MIT &middot; github.com/t-macabee/lurp</p>
  </footer>

</div>
"""
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(page, encoding="utf-8", newline="\n")
    return drawn_count


def run_index(lurp, solution, work_dir):
    work_dir.mkdir(parents=True, exist_ok=True)
    if (work_dir / "index.db").exists():
        sys.exit(f"ERROR: {work_dir} already holds an index.db. Pass an empty folder so the run is a full index.")
    command = [str(lurp), "--mode=index", f"--solution={solution}", f"--output-dir={work_dir}", "--strategy=full"]
    print("running:", " ".join(command), flush=True)
    started = time.monotonic()
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    log = result.stdout + result.stderr
    (work_dir / "index-run.log").write_text(log, encoding="utf-8")
    if result.returncode != 0:
        sys.exit(f"ERROR: index exited with {result.returncode}. Log: {work_dir / 'index-run.log'}")
    print(f"index done in {time.monotonic() - started:.1f}s", flush=True)
    return work_dir / "index.db", log


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--lurp", type=Path, help="Lurp executable used to index --solution.")
    parser.add_argument("--solution", type=Path, help="Solution to index.")
    parser.add_argument("--work-dir", type=Path, help="Empty folder for the index run (with --solution).")
    parser.add_argument("--db", type=Path, help="Existing index.db to render (instead of --solution).")
    parser.add_argument("--index-log", type=Path, help="Saved output of the index run that built --db.")
    parser.add_argument("--template", type=Path, default=DEFAULT_PAGE, help="Page whose first <style> block is reused.")
    parser.add_argument("--out", type=Path, default=DEFAULT_PAGE, help="Output HTML file.")
    args = parser.parse_args()

    if args.solution:
        if not (args.lurp and args.work_dir):
            parser.error("--solution needs --lurp and --work-dir.")
        db_path, log_text = run_index(args.lurp, args.solution.resolve(), args.work_dir.resolve())
    elif args.db:
        db_path = args.db
        log_text = args.index_log.read_text(encoding="utf-8", errors="replace") if args.index_log else ""
    else:
        parser.error("pass --solution (index and render) or --db (render only).")

    match = re.search(r"<style>(.*?)</style>", args.template.read_text(encoding="utf-8"), re.S)
    if not match:
        sys.exit(f"ERROR: no <style> block in {args.template}.")
    index = Index(db_path)
    log = parse_index_log(log_text) if log_text else None
    drawn = render(index, log, source_info(index), match.group(1), args.out)
    print(f"wrote {args.out} (snapshot {index.snapshot_id}, {len(index.edges)} edges, {drawn} symbols drawn)")


if __name__ == "__main__":
    main()
