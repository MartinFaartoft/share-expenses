#!/usr/bin/env python3
"""Generate the event model diagram from event-model.yaml, and validate it.

    .venv/bin/python docs/event-model/generate.py [--check] [--no-png] [model.yaml]

Writes <chapter>.d2 next to the generator, renders it to <chapter>.png with the
d2 CLI (`brew install d2`), and prints a validation report.  Exits non-zero if
any check fails or rendering fails, so it can gate a commit.  --check validates
without writing anything; --no-png writes the .d2 but skips rendering; with a
path, it checks another model file (used to confirm each rule fires, by
mutating a copy).

The .d2 and .png outputs are derived - never edit them by hand.

MODEL SHAPE
  Slices own the elements they introduce.  An element is declared once, in
  mapping form with a `name`, at its first appearance in slice order; later
  appearances are bare string references.

FIELDS AND WHERE THEIR VALUES COME FROM (information completeness)
  A field is `name: Type`, or a mapping when it needs more than a type.
  Events declare shape only: where an event's values come from depends on which
  slice emits it, so that is written on the emitting slice's command instead.
    screen            `inputs:` what the user types or picks;
                      `context:` what the screen already holds (the group being
                      viewed, the slot or invite tapped)
    command fields    `source`, by trust: client (default), system, stream, lookup;
                      `feeds`: event fields it fills beyond the same-named ones
                      (a field always feeds same-named fields of emitted events)
    read model fields `source`: the events the field is built from
    State Read query  `query:` {input: {type, source}}, source client, system or lookup.
                      Drawn on the read model card as `> input: Type (tag)`
  Checks look BACKWARD only - every value used is available from a step before
  it: every event field is fed by exactly one command field of the same type
  (never a lookup); every client value is an input or context of its screen;
  every read model field is built from events the read model reads.  Whether a
  value is then used anywhere is not checked.
  Slices marked `draft: true` are not yet refined and skip these checks.

ARROWS
  None are written in the YAML.  Intra-slice arrows follow from `type`, and
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
  * Every line is padded to the longest line in the model: equal-length lines in a
    monospace font
    form a rectangle, and centring a rectangle leaves them flush left.  This
    padding is load-bearing.
  * No colspan, so a multi-event slice's screen and command sit in the first of
    its columns.  No rowspan either, so dividers are one thin cell per row.
"""
import pathlib, shutil, subprocess, sys

import yaml

HERE = pathlib.Path(__file__).resolve().parent

# Presentation constants - deliberately not in the model file.  The model says
# what the slices are; how big a card is drawn is this script's business.
CARD_WIDTH, CARD_HEIGHT = 250, 160
# a padded line of W monospace characters at font-size 15 needs ~9px each plus
# the card's inner margin; cards widen past CARD_WIDTH only when a line needs it,
# and grow past CARD_HEIGHT only when the longest card needs it
CHAR_PX, CARD_MARGIN_PX, LINE_PX = 9, 16, 17
STATE_CHANGE, STATE_READ = "State Change", "State Read"
AUTOMATION, TRANSLATION = "Automation", "Translation"
# the four canonical Event Modeling slice types
TYPES = (STATE_CHANGE, STATE_READ, AUTOMATION, TRANSLATION)
# Automation and Translation are recognised vocabulary but have no layout yet
RENDERABLE = (STATE_CHANGE, STATE_READ)

# where a command field's or query input's value comes from, by trust:
#   client  sent by the caller (typed, or held by the screen) - untrusted; the default
#   system  supplied by the server: signed-in user, clock, new ids/tokens - trusted
#   stream  derived by deciding, from the folded stream - trusted and consistent,
#           so the only kind an invariant may rest on
#   lookup  read from outside the stream (Identity, plain documents) - trusted but
#           possibly stale, so it may feed guards only, never an event field
COMMAND_SOURCES = ("client", "system", "stream", "lookup")
QUERY_SOURCES = ("client", "system", "lookup")
# the keys a field mapping may carry, per element kind
FIELD_KEYS = {
    "command": {"type", "source", "feeds"},
    "readModel": {"type", "source"},
    "event": {"type"},
    "screen": {"type"},
    "query": {"type", "source"},
}
# drawn after a field's type; client values are the unmarked default
SOURCE_TAGS = {"system": "sys", "stream": "str", "lookup": "lku", "context": "ctx"}


class Report:
    def __init__(self):
        self.errors, self.warnings, self.notes = [], [], []

    def error(self, m): self.errors.append(m)
    def warn(self, m):  self.warnings.append(m)
    def note(self, m):  self.notes.append(m)

    def emit(self):
        for n in self.notes:    print("NOTE   %s" % n)
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
        self.chapter = raw.get("chapter")
        self.slices = raw.get("slices") or []
        self.rep = rep
        if not (self.chapter or "").strip():
            rep.error("the model has no `chapter`")
        self.decl = {}          # (kind, name) -> {"slice": i, "spec": {...}}
        self._resolve()
        self.fields = {}        # (kind, name) -> {field: {type, source, feeds}}
        self._parse_fields()
        self._check_sources()

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
            stype = sl.get("type")
            if stype not in TYPES:
                rep.error("%s has unknown type %r - expected one of: %s"
                          % (where, stype, ", ".join(TYPES)))
            elif stype not in RENDERABLE:
                rep.error("%s is %s %s slice, which is recognised but has no layout yet"
                          % (where, "an" if stype[0] in "AEIOU" else "a", stype))

            sname, sspec = as_element(sl.get("screen"))
            if sname is None:
                rep.error("%s has no screen" % where)
            else:
                self._declare("screen", sname, sspec, i, where)

            if stype == STATE_CHANGE:
                cname, cspec = as_element(sl.get("command"))
                if cname is None:
                    rep.error("%s is a %s slice with no command" % (where, STATE_CHANGE))
                else:
                    self._declare("command", cname, cspec, i, where)
                if sl.get("readModel"):
                    rep.error("%s is a %s slice but declares a read model" % (where, STATE_CHANGE))
                events = sl.get("events") or []
                if not events:
                    rep.error("%s is a %s slice but emits no events" % (where, STATE_CHANGE))
                for ev in events:
                    ename, espec = as_element(ev)
                    self._declare("event", ename, espec, i, where)

            elif stype == STATE_READ:
                if sl.get("events"):
                    rep.error("%s is a %s slice but emits events" % (where, STATE_READ))
                if sl.get("command"):
                    rep.error("%s is a %s slice but declares a command" % (where, STATE_READ))
                rname, rspec = as_element(sl.get("readModel"))
                if rname is None:
                    rep.error("%s is a %s slice with no read model" % (where, STATE_READ))
                else:
                    self._declare("readModel", rname, rspec, i, where)
                    if not (rspec or {}).get("reads"):
                        rep.error("%s: read model %s reads no events" % (where, rname))

        # reads may name events emitted in a later slice, so check them last
        read_events = set()
        for i, sl in enumerate(self.slices):
            if sl.get("type") != STATE_READ:
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

    # ---------------------------------------------------------- fields
    def _parse_fields(self):
        """Normalise every declared field list to {name: {type, source, feeds}}."""
        self.queries = {}       # slice index -> {input: {type, source}}
        for i, sl in enumerate(self.slices):
            raw = sl.get("query")
            if raw is None:
                continue
            where = "slice %d (%s)" % (i + 1, sl.get("slice", "?"))
            if sl.get("type") != STATE_READ:
                self.rep.error("%s: only a %s slice has a `query`; a %s slice's inputs "
                               "are its command's fields" % (where, STATE_READ, STATE_CHANGE))
                continue
            if not isinstance(raw, dict):
                self.rep.error("%s: `query` must be a mapping of input: {type, source}" % where)
                continue
            parsed = {}
            for fname, fv in raw.items():
                f = self._parse_field("query", where, fname, fv)
                if f is not None:
                    parsed[fname] = f
            self.queries[i] = parsed
        for (kind, name), d in self.decl.items():
            spec = d["spec"] or {}
            where = "%s %s" % (kind, name)
            sections = [("inputs", None), ("context", "context")] if kind == "screen" else [("fields", None)]
            parsed = {}
            for key, tag in sections:
                raw = spec.get(key) or {}
                if not isinstance(raw, dict):
                    self.rep.error("%s: `%s` must be a mapping of name: Type" % (where, key))
                    continue
                for fname, fv in raw.items():
                    if fname in parsed:
                        self.rep.error("%s: %s is both an input and context" % (where, fname))
                        continue
                    f = self._parse_field(kind, where, fname, fv)
                    if f is not None:
                        if kind == "screen":
                            f["source"] = tag        # drawn as (ctx) for context, untagged for inputs
                        parsed[fname] = f
            self.fields[(kind, name)] = parsed

    def _parse_field(self, kind, where, fname, fv):
        rep = self.rep
        if isinstance(fv, str):
            fv = {"type": fv}
        if not isinstance(fv, dict) or not isinstance(fv.get("type"), str):
            rep.error("%s: field %s needs a type" % (where, fname))
            return None
        extra = sorted(set(fv) - FIELD_KEYS[kind])
        if extra and kind == "event":
            rep.error("%s: field %s carries %s, but event fields declare only a type - "
                      "where a value comes from depends on the emitting slice, so it is "
                      "written on that slice's command (`source`, `feeds`)"
                      % (where, fname, ", ".join(extra)))
        elif extra:
            rep.error("%s: field %s has unknown key(s) %s - expected: %s"
                      % (where, fname, ", ".join(extra), ", ".join(sorted(FIELD_KEYS[kind]))))

        f = {"type": fv["type"], "source": None, "feeds": []}
        if kind == "command":
            f["source"] = fv.get("source", "client")
            if f["source"] not in COMMAND_SOURCES:
                rep.error("%s: field %s has source %r - expected one of: %s"
                          % (where, fname, f["source"], ", ".join(COMMAND_SOURCES)))
            feeds = fv.get("feeds") or []
            if not isinstance(feeds, list) or not all(isinstance(t, str) and "." in t for t in feeds):
                rep.error("%s: field %s: `feeds` must be a list of Event.field" % (where, fname))
                feeds = []
            f["feeds"] = feeds
        elif kind == "readModel":
            src = fv.get("source") or []
            f["source"] = [src] if isinstance(src, str) else src
            if not isinstance(f["source"], list):
                rep.error("%s: field %s: `source` must be a list of events" % (where, fname))
                f["source"] = []
        elif kind == "query":
            f["source"] = fv.get("source", "client")
            if f["source"] not in QUERY_SOURCES:
                rep.error("%s: query input %s has source %r - expected one of: %s"
                          % (where, fname, f["source"], ", ".join(QUERY_SOURCES)))
        return f

    def _check_sources(self):
        """Information completeness, looking backward: every value used is available."""
        drafts = []
        for i, sl in enumerate(self.slices):
            where = "slice %d (%s)" % (i + 1, sl.get("slice", "?"))
            draft = sl.get("draft", False)
            if not isinstance(draft, bool):
                self.rep.error("%s: `draft` must be true or false" % where)
                draft = False
            if draft:
                drafts.append(str(i + 1))
                continue
            sname, _ = as_element(sl.get("screen"))
            screen = self.fields.get(("screen", sname), {})

            if sl.get("type") == STATE_CHANGE:
                cname, _ = as_element(sl.get("command"))
                cmd = self.fields.get(("command", cname), {})
                self._check_client_values(where, sname, screen, cname, cmd)
                self._check_command(where, sl, cname, cmd)
            elif sl.get("type") == STATE_READ:
                rname, _ = as_element(sl.get("readModel"))
                self._check_read_model(where, rname)
                if i not in self.queries:
                    self.rep.error("%s: %s has no `query` - list the inputs that select what it "
                                   "shows (e.g. the group id the screen holds), or `query: {}` "
                                   "if it needs none" % (where, rname))
                else:
                    self._check_client_values(where, sname, screen, "query", self.queries[i])

        if drafts:
            self.rep.note("draft slice(s) %s: completeness not checked" % ", ".join(drafts))

    def _check_client_values(self, where, sname, screen, owner, values):
        """Every value the client sends must be on its screen: typed there, or held there."""
        for n, f in values.items():
            if f["source"] != "client":
                continue
            src = "%s.%s" % (owner, n)
            if n not in screen:
                self.rep.error("%s: %s comes from the client, but screen %r has no input or "
                               "context %s" % (where, src, sname, n))
            elif screen[n]["type"] != f["type"]:
                self.rep.error("%s: %s is %s, but screen %r has %s as %s"
                               % (where, src, f["type"], sname, n, screen[n]["type"]))

    def _check_command(self, where, sl, cname, cmd):
        rep = self.rep
        if not cmd:
            rep.error("%s: command %s has no fields" % (where, cname))
            return
        events = [as_element(e)[0] for e in sl.get("events") or []]
        evfields = {e: self.fields.get(("event", e), {}) for e in events}
        for e, fs in evfields.items():
            if not fs:
                rep.error("%s: event %s has no fields" % (where, e))

        feeders = {}                                        # (event, field) -> [command field]
        for n, f in cmd.items():
            src = "%s.%s" % (cname, n)
            targets = [(e, n) for e in events if n in evfields[e]]      # by name
            for t in f["feeds"]:
                e, _, ef = t.partition(".")
                if e not in evfields:
                    rep.error("%s: %s feeds %s, but this slice does not emit %s" % (where, src, t, e))
                elif ef not in evfields[e]:
                    rep.error("%s: %s feeds %s, but %s has no field %s" % (where, src, t, e, ef))
                elif (e, ef) in targets:
                    rep.warn("%s: %s lists %s in `feeds`, which it already feeds by name" % (where, src, t))
                else:
                    targets.append((e, ef))
            if f["source"] == "lookup" and targets:
                rep.error("%s: %s is looked up from outside the stream, so it may only feed a "
                          "guard, never an event field" % (where, src))
            for e, ef in targets:
                feeders.setdefault((e, ef), []).append(n)
                if evfields[e][ef]["type"] != f["type"]:
                    rep.error("%s: %s is %s, but feeds %s.%s, which is %s"
                              % (where, src, f["type"], e, ef, evfields[e][ef]["type"]))

        for e in events:
            for ef in evfields[e]:
                fed = feeders.get((e, ef), [])
                if not fed:
                    rep.error("%s: %s.%s has no source - no field of %s is named %s or feeds it"
                              % (where, e, ef, cname, ef))
                elif len(fed) > 1:
                    rep.error("%s: %s.%s is fed by more than one field of %s: %s"
                              % (where, e, ef, cname, ", ".join(fed)))

    def _check_read_model(self, where, rname):
        reads = self.spec("readModel", rname).get("reads") or []
        for n, f in self.fields.get(("readModel", rname), {}).items():
            if not f["source"]:
                self.rep.error("%s: %s.%s has no source - list the events it is built from"
                               % (where, rname, n))
            for e in f["source"]:
                if e not in reads:
                    self.rep.error("%s: %s.%s is built from %s, which %s does not read"
                                   % (where, rname, n, e, rname))


# --------------------------------------------------------------- rendering
def field_line(name, f):
    """`- name: Type`, tagged unless the value comes from the client (the default)."""
    tag = SOURCE_TAGS.get(f["source"]) if isinstance(f["source"], str) else None
    return "- %s: %s%s" % (name, f["type"], " (%s)" % tag if tag else "")


def card_fields(m, kind, name):
    """A screen with a wireframe shows the wireframe; otherwise its inputs."""
    if kind == "screen" and m.spec("screen", name).get("wireframe"):
        return []
    return [field_line(n, f) for n, f in m.fields.get((kind, name), {}).items()]


def query_lines(m, i):
    """A State Read slice's query inputs, drawn under its read model's fields as `> name`."""
    return ["> %s" % field_line(n, f)[2:] for n, f in m.queries.get(i, {}).items()]


def longest_card(m):
    """Most body lines on any card (fields, inputs, wireframe, query inputs)."""
    most = 0
    for (kind, name), d in m.decl.items():
        most = max(most, len(card_fields(m, kind, name)) + len((d["spec"] or {}).get("wireframe") or []))
    for i, sl in enumerate(m.slices):
        rname, _ = as_element(sl.get("readModel"))
        if rname:
            most = max(most, len(card_fields(m, "readModel", rname)) + len(query_lines(m, i)))
    return most


def pad_width(m):
    """Widest line anywhere in the model.

    Every card line is padded to this, which is what left-aligns the field lists:
    equal-length lines in a monospace font form a rectangle, and centring a
    rectangle leaves every line flush left.  Computed rather than configured, so
    adding a longer field name cannot silently break the alignment.
    """
    widest = 0
    for (kind, name), d in m.decl.items():
        spec = d["spec"] or {}
        widest = max(widest, len(name))
        for line in card_fields(m, kind, name):
            widest = max(widest, len(line))
        for raw in spec.get("wireframe") or []:
            widest = max(widest, len(raw))
    for i in m.queries:
        for line in query_lines(m, i):
            widest = max(widest, len(line))
    return widest


def card(key, cls, title, lines=None, wireframe=None, W=21):
    out = [title.ljust(W), " " * W]
    out += list(lines or [])
    out += list(wireframe or [])
    while len(out) < 6:
        out.append(" " * W)
    return '%s: "%s" { class: %s }\n' % (
        key, "\\n".join(l.ljust(W) for l in out), cls)


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
    W = pad_width(m)
    cw = max(CARD_WIDTH, W * CHAR_PX + CARD_MARGIN_PX)
    ch = max(CARD_HEIGHT, (2 + longest_card(m)) * LINE_PX + 2 * CARD_MARGIN_PX)
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
                         ("%d - %s%s" % (i + 1, sl["slice"],
                                         " (draft)" if sl.get("draft") else "")).ljust(40)))
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
                                lines=card_fields(m, "screen", name),
                                wireframe=sp.get("wireframe"), W=W))
            elif row == 3 and sl.get("type") == STATE_READ:
                out.append(card("rm%d" % i, "view", name,
                                lines=card_fields(m, "readModel", name) + query_lines(m, i), W=W))
            elif row == 4 and sl.get("type") == STATE_CHANGE:
                out.append(card("c%d" % i, "command", name,
                                lines=card_fields(m, "command", name), W=W))
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
                            lines=card_fields(m, "event", name), W=W))

    # intra-slice arrows: implied by pattern, never declared
    out.append("\n# --- State Change slices: screen -> command -> event(s)\n")
    for i, sl in enumerate(slices):
        if sl.get("type") != STATE_CHANGE:
            continue
        out.append("s%d -> c%d\n" % (i, i))
        for j in range(len(sl.get("events") or [])):
            out.append("c%d -> e%d_%d\n" % (i, i, j))

    # inter-slice arrows: a read model's `reads`, resolved by time order
    out.append("\n# --- State Read slices: events -> read model -> screen\n")
    for i, sl in enumerate(slices):
        if sl.get("type") != STATE_READ:
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


def slug(text):
    out = "".join(c.lower() if c.isalnum() else "-" for c in text)
    while "--" in out:
        out = out.replace("--", "-")
    return out.strip("-")


def main(argv):
    check_only = "--check" in argv
    paths = [a for a in argv if not a.startswith("--")]
    source = pathlib.Path(paths[0]) if paths else HERE / "event-model.yaml"
    raw = yaml.safe_load(source.read_text())
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
        if sl.get("type") != STATE_READ:
            continue
        rname, _ = as_element(sl.get("readModel"))
        for ev in model.spec("readModel", rname).get("reads") or []:
            if ev in first and first[ev] > i:
                rep.warn("%s reads %s, first emitted in slice %d - drawn dashed"
                         % (rname, ev, first[ev] + 1))

    rc = rep.emit()
    d2, ncols = render(model)
    cells = d2.count("{ class: ")
    if cells != ncols * 5:
        print("ERROR  grid is not exactly full - D2 will silently reflow it")
        rc = 1
    if check_only:
        return rc
    target = HERE / ("%s.d2" % slug(model.chapter or "chapter"))
    target.write_text(d2)
    print("wrote  %s  (%d columns x 5 rows = %d cells, %d emitted, pad %d)"
          % (target.name, ncols, ncols * 5, cells, pad_width(model)))
    if "--no-png" not in argv:
        rc = render_png(target) or rc
    return rc


def render_png(source):
    """Render the .d2 to a .png beside it with the d2 CLI; non-zero on failure."""
    png = source.with_suffix(".png")
    d2_cli = shutil.which("d2")
    if d2_cli is None:
        print("ERROR  d2 not found on PATH - install it (brew install d2), or pass --no-png")
        return 1
    result = subprocess.run([d2_cli, "--pad", "30", str(source), str(png)], capture_output=True, text=True)
    if result.returncode != 0:
        print("ERROR  d2 failed to render %s:\n%s" % (png.name, (result.stderr or result.stdout).strip()))
        return 1
    print("wrote  %s" % png.name)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
