# Slice 14 — `RenameGroup`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Rename group — a form with the group's current name, reached from the group page's `⋯` menu |
| Command | `RenameGroup(name, by)` |
| Events | `GroupRenamed` |
| Code | `src/SplitIt/Slices/RenameGroup/` |
| Endpoints | `GET /groups/{group}/rename` — the form; `POST` to the same path — its submit; sign-in required |

Gives a group a new name. Nothing else changes: the id, the members and the money are
untouched, and so is every earlier event — the old name stays in `GroupCreated` and
in any earlier `GroupRenamed`. Every read model that shows the group's name then folds
the rename (below).

Any member may rename the group (spec §5: flat trust, backed by the log).

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13). Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
```

```
1 - renames the group
    WHEN   RenameGroup("Porto trip", alice)
    THEN   GroupRenamed("Porto trip", alice)

2 - the name is trimmed
    WHEN   RenameGroup("  Porto trip  ", alice)
    THEN   GroupRenamed("Porto trip", alice)

3 - any member may rename it, and is recorded as the actor
    WHEN   RenameGroup("Porto trip", bob)
    THEN   GroupRenamed("Porto trip", bob)

4 - a group can be renamed again
    GIVEN  ... AND GroupRenamed("Porto trip", bob)
    WHEN   RenameGroup("Algarve", alice)
    THEN   GroupRenamed("Algarve", alice)

5 - the same name changes nothing, and appends nothing
    WHEN   RenameGroup("Lisbon trip", alice)
    THEN   (no events)

6 - the name is compared with the current one, not the first
    GIVEN  ... AND GroupRenamed("Porto trip", bob)
    WHEN   RenameGroup("Lisbon trip", alice)
    THEN   GroupRenamed("Lisbon trip", alice)

7 - a change of case is a change
    WHEN   RenameGroup("lisbon trip", alice)
    THEN   GroupRenamed("lisbon trip", alice)

8 - the group must exist
    GIVEN  (empty stream)
    WHEN   RenameGroup("Porto trip", alice)
    THEN   rejected (not found) - group not found

9 - only members of the group may rename it
    WHEN   RenameGroup("Porto trip", mallory)
    THEN   rejected (not found) - group not found

10 - an archived group cannot be renamed (built with Archive group, slice-15)
    GIVEN  ... AND GroupArchived(alice)
    WHEN   RenameGroup("Porto trip", alice)
    THEN   rejected - group is archived

11 - a name is required
    WHEN   RenameGroup("   ", alice)
    THEN   rejected - name is required

12 - a name is at most 100 characters
    WHEN   RenameGroup(<101 characters>, alice)
    THEN   rejected - name must be at most 100 characters

13 - exactly 100 characters is fine
    WHEN   RenameGroup(<100 characters>, alice)
    THEN   GroupRenamed(<100 characters>, alice)
```

## Notes

- **Order of checks:** membership, then archived, then the name, then whether it
  changed. A non-member gets `group not found` whatever else is wrong.
- **The rules are Create group's** (slice-01), copied, not shared: trimmed, required, at
  most 100 visible characters (`Names`).
- **"Changed" is the trimmed name against the current one,** exactly (case counts).
  A rename to what it already is is answered as saved, not rejected: a double tap, or
  a save with nothing touched, is not the user's mistake.
- **Last write wins,** as Edit expense: the form says nothing of the name it was opened
  on, and both renames are in the log.
- **Over HTTP:** a non-member, a missing group and a malformed id are the same 404.
  Saved, or nothing to save: back to the group page. Rejected: the form again, with what
  was typed and why, 200. A conflicting save answers 409.

## The screen

- **Reached from the group page:** **Rename group** in the `⋯` menu, an address, not a
  dependency on this slice.
- **One field,** **Group name**, filled in with the current name: `required`,
  `maxlength` 100 (a hint: deciding counts visible characters). **Save** posts it: a
  plain form, no htmx, with antiforgery (spec §3).
- **A Back link** to the group, named by its current name ("← Lisbon trip").
- **Rejected:** the page again with what was typed, the reason under the form
  (`role="alert"`), 200.

## Notes on the screen

- **The screen folds this slice's own `State`:** the group's name, who holds which
  slot (for membership), and whether it is archived.

## Read by (folding `GroupRenamed`)

Every slice that holds or shows the group's name: View group (its title), View balances
and View settlement plan (their read models carry it), View homepage (the stored `UserGroups` document, and the live
invites, which name the group), and the states of Add expense, Edit expense, Remove
expense, Add member, Invite member and Accept invite, where the name is the back
link, or goes into an email. Each states a scenario in its own doc.

**No rebuild is needed** for this slice: no `GroupRenamed` exists before it, so the stored
`GroupActivity` and `UserGroups` documents are already right, and fold each new rename as
it is appended (inline for the group page, by the daemon for the home page). Archive group
(slice-15) is the change that needs one.

## Deferred

- **Archived group** is read-only; reopening it — and so renaming again — waits for
  `UnarchiveGroup` (slice-15).
