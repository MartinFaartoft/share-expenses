#!/usr/bin/env python3
"""Generate the event model diagram from event-model.yaml, and validate it.

    .venv/bin/python docs/event-model/generate.py

Writes <meta.diagram>.d2 next to the YAML and prints a validation report.
Exits non-zero if any check fails, so it can gate a commit.

The .d2 output is derived - never edit it by hand.

Layout constraints discovered by rendering, all recorded in spec.md section 13:
  * ONE flat grid.  Sibling grid containers compute column widths independently,
    so alignment breaks as soon as a cell differs in size.
  * grid-rows: 5 with an exactly-full grid fills ROW-MAJOR, lane by lane.
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


# --------------------------------------------------------------- validation
class Report:
    def __init__(self):
        self.errors, self.warnings = [], []

    def error(self, msg):   self.errors.append(msg)
    def warn(self, msg):    self.warnings.append(msg)

    def emit(self):
        for w in self.warnings: print("WARN   %s" % w)
        for e in self.errors:   print("ERROR  %s" % e)
        if not self.errors and not self.warnings:
            print("OK     model is consistent")
        else:
            print("%d error(s), %d warning(s)" % (len(self.errors), len(self.warnings)))
        return 1 if self.errors else 0


def validate(m, rep):
    screens    = m.get("screens") or {}
    commands   = m.get("commands") or {}
    events     = m.get("events") or {}
    readModels = m.get("readModels") or {}
    slices     = m.get("slices") or []

    emitted = {}          # event type -> [slice index]
    consumed = set()      # event types read by some read model
    rm_used = set()       # read models rendered on some screen

    for i, sl in enumerate(slices):
        where = "slice %d (%s)" % (i + 1, sl.get("name", "?"))
        pattern = sl.get("pattern")

        if sl.get("screen") not in screens:
            rep.error("%s references unknown screen %r" % (where, sl.get("screen")))

        if pattern == "command":
            if sl.get("command") not in commands:
                rep.error("%s references unknown command %r" % (where, sl.get("command")))
            if not sl.get("emits"):
                rep.error("%s is a command slice but emits no events" % where)
            for ev in sl.get("emits") or []:
                if ev not in events:
                    rep.error("%s emits unknown event %r" % (where, ev))
                emitted.setdefault(ev, []).append(i)
        elif pattern == "view":
            rm = sl.get("readModel")
            if rm not in readModels:
                rep.error("%s references unknown read model %r" % (where, rm))
            else:
                rm_used.add(rm)
            if sl.get("emits"):
                rep.error("%s is a view slice but emits events" % where)
        else:
            rep.error("%s has unknown pattern %r" % (where, pattern))

    for name, rm in readModels.items():
        for ev in rm.get("from") or []:
            if ev not in events:
                rep.error("read model %s consumes unknown event %r" % (name, ev))
            consumed.add(ev)
        if name not in rm_used:
            rep.warn("read model %s is not shown on any screen" % name)

    # every event should feed at least one read model, else nobody ever reads it
    for ev in events:
        if ev not in emitted:
            rep.warn("event %s is defined but never emitted" % ev)
        if ev not in consumed:
            rep.warn("event %s is not consumed by any read model" % ev)

    # a read model consuming an event emitted only later than its own slice
    for i, sl in enumerate(slices):
        if sl.get("pattern") != "view":
            continue
        rm = readModels.get(sl.get("readModel")) or {}
        for ev in rm.get("from") or []:
            occ = emitted.get(ev) or []
            if occ and min(occ) > i:
                rep.warn("%s consumes %s, first emitted in slice %d - drawn dashed"
                         % (sl.get("readModel"), ev, min(occ) + 1))
    return rep


# --------------------------------------------------------------- rendering
def card(key, cls, title, fields=None, wireframe=None, W=21):
    lines = [title.ljust(W), " " * W]
    for k, v in (fields or {}).items():
        lines.append("- %s: %s" % (k, v))
    for raw in wireframe or []:
        lines.append(raw)
    while len(lines) < 6:
        lines.append(" " * W)
    label = "\\n".join(l.ljust(W) for l in lines)
    return '%s: "%s" { class: %s }\n' % (key, label, cls)


def plain(key, cls, label=""):
    return '%s: "%s" { class: %s }\n' % (key, label, cls)


def render(m):
    W = m["meta"]["columns"]
    cw, ch = m["meta"]["card"]["width"], m["meta"]["card"]["height"]
    screens, commands = m["screens"], m["commands"]
    events, readModels, slices = m["events"], m["readModels"], m["slices"]

    slots = [max(1, len(sl.get("emits") or [])) for sl in slices]
    ncols = 1 + sum(slots) + (len(slices) - 1)

    out = ['''# GENERATED from event-model.yaml by generate.py - do not edit.
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

''' % {"cw": cw, "ch": ch}]

    # ---- row 1: slice names
    out.append("# ===== row 1: slice names =====\n")
    out.append(plain("q0", "lgapN"))
    for i, sl in enumerate(slices):
        if i:
            out.append(plain("dvN%d" % i, "divN"))
        out.append(plain("n%d" % i, "slicename",
                         ("%d - %s" % (i + 1, sl["name"])).ljust(40)))
        for j in range(1, slots[i]):
            out.append(plain("gpN%d_%d" % (i, j), "gapN"))

    # ---- rows 2-4: screens, read models, commands
    for row, lane, label in ((2, "screen", "SCREENS"),
                             (3, "view", "READ MODELS"),
                             (4, "command", "COMMANDS")):
        out.append("\n# ===== row %d: %s =====\n" % (row, label.lower()))
        out.append(plain("l%d" % row, "lanelabel", label))
        for i, sl in enumerate(slices):
            if i:
                out.append(plain("dv%d_%d" % (row, i), "div"))
            if row == 2:
                sc = screens[sl["screen"]]
                out.append(card("s%d" % i, "screen", sc["title"],
                                wireframe=sc.get("wireframe"), W=W))
            elif row == 3 and sl.get("pattern") == "view":
                rm = readModels[sl["readModel"]]
                out.append(card("rm%d" % i, "view", sl["readModel"],
                                fields=rm.get("fields"), W=W))
            elif row == 4 and sl.get("pattern") == "command":
                cd = commands[sl["command"]]
                out.append(card("c%d" % i, "command", sl["command"],
                                fields=cd.get("fields"), W=W))
            else:
                out.append(plain("gp%d_%d" % (row, i), "gap"))
            for j in range(1, slots[i]):
                out.append(plain("gp%d_%d_%d" % (row, i, j), "gap"))

    # ---- row 5: event stream
    out.append("\n# ===== row 5: event stream =====\n")
    out.append(plain("l5", "lanelabel", "EVENT STREAM"))
    occurrences = {}          # event type -> [(slice index, id)]
    for i, sl in enumerate(slices):
        if i:
            out.append(plain("dv5_%d" % i, "div"))
        emits = sl.get("emits") or []
        if not emits:
            out.append(plain("gp5_%d" % i, "gap"))
            continue
        for j, ev in enumerate(emits):
            eid = "e%d_%d" % (i, j)
            occurrences.setdefault(ev, []).append((i, eid))
            out.append(card(eid, "event", ev, fields=events[ev].get("fields"), W=W))

    # ---- arrows
    out.append("\n# --- command slices: screen -> command -> event(s)\n")
    for i, sl in enumerate(slices):
        if sl.get("pattern") != "command":
            continue
        out.append("s%d -> c%d\n" % (i, i))
        for j in range(len(sl["emits"])):
            out.append("c%d -> e%d_%d\n" % (i, i, j))

    out.append("\n# --- view slices: events -> read model -> screen\n")
    for i, sl in enumerate(slices):
        if sl.get("pattern") != "view":
            continue
        rm = readModels[sl["readModel"]]
        for ev in rm.get("from") or []:
            occ = occurrences.get(ev) or []
            before = [e for (k, e) in occ if k <= i]
            if before:
                out.append("%s -> rm%d\n" % (before[-1], i))
            elif occ:
                # emitted only later: the read model picks it up on a later pass
                out.append('%s -> rm%d: "loops back" { style.stroke-dash: 4 }\n'
                           % (occ[0][1], i))
        out.append("rm%d -> s%d\n" % (i, i))

    return "".join(out), ncols


def main():
    model = yaml.safe_load((HERE / "event-model.yaml").read_text())
    rc = validate(model, Report()).emit()
    d2, ncols = render(model)
    target = HERE / ("%s.d2" % model["meta"]["diagram"])
    target.write_text(d2)
    cells = d2.count("{ class: ")
    print("wrote  %s  (%d columns x 5 rows = %d cells, %d emitted)"
          % (target.name, ncols, ncols * 5, cells))
    if cells != ncols * 5:
        print("ERROR  grid is not exactly full - D2 will reflow it")
        rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
