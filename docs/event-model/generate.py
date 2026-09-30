#!/usr/bin/env python3
"""Generate the event model diagram from event-model.yaml, and validate it.

    .venv/bin/python docs/event-model/generate.py

Writes <meta.diagram>.d2 next to the YAML and prints a validation report.
Exits non-zero if any check fails, so it can gate a commit.

The .d2 output is derived - never edit it by hand.

MODEL SHAPE
  Slices own the elements they introduce.  An element is declared once, in
  mapping form with a `name`, at its first appearance in slice order; later
  appearances are bare string references.

ARROWS
  None are written in the YAML.  Intra-slice arrows follow from `pattern`, and
  the only inter-slice arrow is a read model's `reads` list, resolved here to the
  nearest occurrence at or before the consuming slice (dashed if only later).

LAYOUT CONSTRAINTS found by rendering, all recorded in spec.md section 13:
  * ONE flat grid.  Sibling grid containers compute column widths independently,
    so alignment breaks as soon as a cell differs in size.
  * grid-rows: 5 with an exactly-full grid fills ROW-MAJOR, lane by lane.  An
    uneven grid is silently reflowed by D2 rather than rejected.
  * Cards are PLAIN labels.  Markdown labels ignore width/height and size to
    their content, which collapses the grid.
  * A plain label has one font and one size, so a card title cannot be bolder or
    larger than its fields.  Title is distinguished by position only.
  * Every line is padded to meta.columns: equal-length lines in a monospace font
    form a rectangle, and centring a rectangle leaves them flush left.  This
    padding is load-bearing.
  * No colspan, so a multi-event slice's screen and command sit in the first of
    its columns.  No rowspan either, so dividers are one thin cell per row.
"""
import sys, pathlib, yaml

HERE = pathlib.Path(__file__).resolve().parent
PATTERNS = ("command", "view")


class Report:
    def __init__(self):
        self.errors, self.warnings = [], []

    def error(self, m): self.errors.append(m)
    def warn(self, m):  self.warnings.append(m)

    def emit(self):
        for w in self.warnings: print("WARN   %s" % w)
        for e in self.errors:   print("ERROR  %s" % e)
        if not (self.errors or self.warnings):
            print("OK     model is consistent")
        else:
            print("%d error(s), %d warning(s)" % (len(self.errors), len(self.warnings)))
        return 1 if self.errors else 0


def as_element(node):
    """A bare string is a reference; a mapping is a declaration."""
    if node is None:
        return None, None
    if isinstance(node, str):
        return node, None
    return node.get("name"), node


class Model:
    """Resolved model: declarations gathered, references checked, order kept."""

    def __init__(self, raw, rep):
        self.meta = raw["meta"]
        self.slices = raw.get("slices") or []
        self.rep = rep
        self.decl = {}          # (kind, name) -> {"slice": i, "spec": {...}}
        self._resolve()

    def _declare(self, kind, name, spec, i, where):
        if name is None:
            self.rep.error("%s has a %s with no name" % (where, kind))
            return
        key = (kind, name)
        if spec is None:                                   # reference
            if key not in self.decl:
                self.rep.error("%s references %s %r before it is declared"
                               % (where, kind, name))
        elif key in self.decl:
            self.rep.error("%s redeclares %s %r, already declared in slice %d"
                           % (where, kind, name, self.decl[key]["slice"] + 1))
        else:
            self.decl[key] = {"slice": i, "spec": spec}

    def spec(self, kind, name):
        d = self.decl.get((kind, name))
        return (d or {}).get("spec") or {}

    def _resolve(self):
        rep = self.rep
        for i, sl in enumerate(self.slices):
            where = "slice %d (%s)" % (i + 1, sl.get("slice", "?"))
            pattern = sl.get("pattern")
            if pattern not in PATTERNS:
                rep.error("%s has unknown pattern %r" % (where, pattern))

            sname, sspec = as_element(sl.get("screen"))
            if sname is None:
                rep.error("%s has no screen" % where)
            else:
                self._declare("screen", sname, sspec, i, where)

            if pattern == "command":
                cname, cspec = as_element(sl.get("command"))
                if cname is None:
                    rep.error("%s is a command slice with no command" % where)
                else:
                    self._declare("command", cname, cspec, i, where)
                if sl.get("readModel"):
                    rep.error("%s is a command slice but declares a read model" % where)
                events = sl.get("events") or []
                if not events:
                    rep.error("%s is a command slice but emits no events" % where)
                for ev in events:
                    ename, espec = as_element(ev)
                    self._declare("event", ename, espec, i, where)

            elif pattern == "view":
                if sl.get("events"):
                    rep.error("%s is a view slice but emits events" % where)
                if sl.get("command"):
                    rep.error("%s is a view slice but declares a command" % where)
                rname, rspec = as_element(sl.get("readModel"))
                if rname is None:
                    rep.error("%s is a view slice with no read model" % where)
                else:
                    self._declare("readModel", rname, rspec, i, where)
                    if not (rspec or {}).get("reads"):
                        rep.error("%s: read model %s reads no events" % (where, rname))

        # reads may name events emitted in a later slice, so check them last
        read_events = set()
        for i, sl in enumerate(self.slices):
            if sl.get("pattern") != "view":
                continue
            rname, rspec = as_element(sl.get("readModel"))
            for ev in ((rspec or {}).get("reads") or []):
                if ("event", ev) not in self.decl:
                    self.rep.error("slice %d: read model %s reads undeclared event %r"
                                   % (i + 1, rname, ev))
                read_events.add(ev)

        for (kind, name) in self.decl:
            if kind == "event" and name not in read_events:
                self.rep.warn("event %s is not consumed by any read model" % name)


# --------------------------------------------------------------- rendering
def card(key, cls, title, fields=None, wireframe=None, W=21):
    lines = [title.ljust(W), " " * W]
    for k, v in (fields or {}).items():
        lines.append("- %s: %s" % (k, v))
    lines += list(wireframe or [])
    while len(lines) < 6:
        lines.append(" " * W)
    return '%s: "%s" { class: %s }\n' % (
        key, "\\n".join(l.ljust(W) for l in lines), cls)


def plain(key, cls, label=""):
    return '%s: "%s" { class: %s }\n' % (key, label, cls)


HEADER = '''# GENERATED from event-model.yaml by generate.py - do not edit.
#
# Rows: 1 slice names, 2 SCREENS, 3 READ MODELS, 4 COMMANDS, 5 EVENT STREAM.
# Read models sit by the screens they feed and commands by the events they emit,
# so the arrow for either pattern is only ever one row long.
# Column 1 holds lane labels, slices are separated by a 2px divider column, and
# every other column is one event slot: time runs left to right.

grid-rows: 5
horizontal-gap: 22
vertical-gap: 22

classes: {
  lanelabel: {
    width: 170; height: %(ch)d
    style: { fill: transparent; stroke-width: 0; font-color: "#6b7280"; bold: true }
  }
  slicename: {
    width: %(cw)d; height: 50
    style: { fill: transparent; stroke-width: 0; font-color: "#374151"; bold: true }
  }
  lgapN: { width: 170;    height: 50;  style: { opacity: 0 } }
  gapN:  { width: %(cw)d; height: 50;  style: { opacity: 0 } }
  divN:  { width: 2;      height: 50;  style: { fill: "#c9ced8"; stroke-width: 0 } }
  div:   { width: 2;      height: %(ch)d; style: { fill: "#c9ced8"; stroke-width: 0 } }
  gap:   { width: %(cw)d; height: %(ch)d; style: { opacity: 0 } }
  screen: {
    width: %(cw)d; height: %(ch)d
    style: { font: mono; font-size: 15; fill: "#fbfbfd"; stroke: "#8c93a0"; stroke-width: 2; border-radius: 6 }
  }
  command: {
    width: %(cw)d; height: %(ch)d
    style: { font: mono; font-size: 15; fill: "#4a90d9"; stroke: "#2b6cb0"; font-color: "#ffffff"; border-radius: 6 }
  }
  event: {
    width: %(cw)d; height: %(ch)d
    style: { font: mono; font-size: 15; fill: "#f5a623"; stroke: "#c07d12"; font-color: "#1f1f1f"; border-radius: 6 }
  }
  view: {
    width: %(cw)d; height: %(ch)d
    style: { font: mono; font-size: 15; fill: "#54b054"; stroke: "#357935"; font-color: "#ffffff"; border-radius: 6 }
  }
}

'''


def render(m):
    W = m.meta["columns"]
    cw, ch = m.meta["card"]["width"], m.meta["card"]["height"]
    slices = m.slices
    slots = [max(1, len(sl.get("events") or [])) for sl in slices]
    ncols = 1 + sum(slots) + (len(slices) - 1)

    out = [HEADER % {"cw": cw, "ch": ch}]

    out.append("# ===== row 1: slice names =====\n")
    out.append(plain("q0", "lgapN"))
    for i, sl in enumerate(slices):
        if i:
            out.append(plain("dvN%d" % i, "divN"))
        out.append(plain("n%d" % i, "slicename",
                         ("%d - %s" % (i + 1, sl["slice"])).ljust(40)))
        for j in range(1, slots[i]):
            out.append(plain("gpN%d_%d" % (i, j), "gapN"))

    for row, label in ((2, "SCREENS"), (3, "READ MODELS"), (4, "COMMANDS")):
        out.append("\n# ===== row %d: %s =====\n" % (row, label.lower()))
        out.append(plain("l%d" % row, "lanelabel", label))
        for i, sl in enumerate(slices):
            if i:
                out.append(plain("dv%d_%d" % (row, i), "div"))
            name, _ = as_element(sl.get("screen") if row == 2 else
                                 sl.get("readModel") if row == 3 else sl.get("command"))
            if row == 2:
                sp = m.spec("screen", name)
                out.append(card("s%d" % i, "screen", name,
                                wireframe=sp.get("wireframe"), W=W))
            elif row == 3 and sl.get("pattern") == "view":
                out.append(card("rm%d" % i, "view", name,
                                fields=m.spec("readModel", name).get("fields"), W=W))
            elif row == 4 and sl.get("pattern") == "command":
                out.append(card("c%d" % i, "command", name,
                                fields=m.spec("command", name).get("fields"), W=W))
            else:
                out.append(plain("gp%d_%d" % (row, i), "gap"))
            for j in range(1, slots[i]):
                out.append(plain("gp%d_%d_%d" % (row, i, j), "gap"))

    out.append("\n# ===== row 5: event stream =====\n")
    out.append(plain("l5", "lanelabel", "EVENT STREAM"))
    occurrences = {}
    for i, sl in enumerate(slices):
        if i:
            out.append(plain("dv5_%d" % i, "div"))
        events = sl.get("events") or []
        if not events:
            out.append(plain("gp5_%d" % i, "gap"))
            continue
        for j, ev in enumerate(events):
            name, _ = as_element(ev)
            eid = "e%d_%d" % (i, j)
            occurrences.setdefault(name, []).append((i, eid))
            out.append(card(eid, "event", name,
                            fields=m.spec("event", name).get("fields"), W=W))

    # intra-slice arrows: implied by pattern, never declared
    out.append("\n# --- command slices: screen -> command -> event(s)\n")
    for i, sl in enumerate(slices):
        if sl.get("pattern") != "command":
            continue
        out.append("s%d -> c%d\n" % (i, i))
        for j in range(len(sl.get("events") or [])):
            out.append("c%d -> e%d_%d\n" % (i, i, j))

    # inter-slice arrows: a read model's `reads`, resolved by time order
    out.append("\n# --- view slices: events -> read model -> screen\n")
    for i, sl in enumerate(slices):
        if sl.get("pattern") != "view":
            continue
        rname, _ = as_element(sl.get("readModel"))
        for ev in m.spec("readModel", rname).get("reads") or []:
            occ = occurrences.get(ev) or []
            before = [e for (k, e) in occ if k <= i]
            if before:
                out.append("%s -> rm%d\n" % (before[-1], i))
            elif occ:
                out.append('%s -> rm%d: "loops back" { style.stroke-dash: 4 }\n'
                           % (occ[0][1], i))
        out.append("rm%d -> s%d\n" % (i, i))

    return "".join(out), ncols


def main():
    raw = yaml.safe_load((HERE / "event-model.yaml").read_text())
    rep = Report()
    model = Model(raw, rep)

    # a read model reading an event first emitted later is legal but is drawn
    # dashed, so it cannot be mistaken for an ordinary backwards dependency
    first = {}
    for i, sl in enumerate(model.slices):
        for ev in sl.get("events") or []:
            n, _ = as_element(ev)
            first.setdefault(n, i)
    for i, sl in enumerate(model.slices):
        if sl.get("pattern") != "view":
            continue
        rname, _ = as_element(sl.get("readModel"))
        for ev in model.spec("readModel", rname).get("reads") or []:
            if ev in first and first[ev] > i:
                rep.warn("%s reads %s, first emitted in slice %d - drawn dashed"
                         % (rname, ev, first[ev] + 1))

    rc = rep.emit()
    d2, ncols = render(model)
    target = HERE / ("%s.d2" % model.meta["diagram"])
    target.write_text(d2)
    cells = d2.count("{ class: ")
    print("wrote  %s  (%d columns x 5 rows = %d cells, %d emitted)"
          % (target.name, ncols, ncols * 5, cells))
    if cells != ncols * 5:
        print("ERROR  grid is not exactly full - D2 will silently reflow it")
        rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
