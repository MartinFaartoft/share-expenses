# Slice 15 — `ArchiveGroup`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Archive group — a confirm page, reached from the group page's `⋯` menu, that warns if the group is not settled up |
| Command | `ArchiveGroup(by)` |
| Events | `GroupArchived` |
| Code | `src/SplitIt/Slices/ArchiveGroup/` |
| Endpoints | `GET /groups/{group}/archive` — the confirm page; `POST` to the same path — its submit; sign-in required |

Makes a finished group read-only and takes it out of the main list (spec §8). Nothing is
deleted (spec §7): `GroupArchived` is a fact beside the rest, and a group, its members
and its money are read exactly as before. What changes is what is accepted: from now on
**no command is accepted on the group** (the invariant in spec §11), and every slice
that decides says so itself, with the same message (below).

**It warns; it does not block** (spec §8). A group that is not settled up can be
archived: the confirm page says who is owed and who owes, and asks again. Settling up is
the user's to do first, not the rule's to force.

**There is no way back yet.** Reopening a group (`UnarchiveGroup`, spec §11) is
deferred: no screen has been found for it, and without one it would be a command nobody
can send. Until then archiving is one-way, and the confirm page says so. Any member may
archive (spec §5: flat trust, backed by the log), so the page is plain about it.

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
1 - archives the group
    WHEN   ArchiveGroup(alice)
    THEN   GroupArchived(alice)

2 - any member may archive it, and is recorded as the actor
    WHEN   ArchiveGroup(bob)
    THEN   GroupArchived(bob)

3 - a group that is not settled up can be archived: the page warns, the rule does not
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                                   {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
    WHEN   ArchiveGroup(alice)
    THEN   GroupArchived(alice)

4 - the group must exist
    GIVEN  (empty stream)
    WHEN   ArchiveGroup(alice)
    THEN   rejected (not found) - group not found

5 - only members of the group may archive it
    WHEN   ArchiveGroup(mallory)
    THEN   rejected (not found) - group not found

6 - a group already archived is not archived twice
    GIVEN  ... AND GroupArchived(bob)
    WHEN   ArchiveGroup(alice)
    THEN   (no events) - group already archived
```

## What the page knows

The confirm page is built from this slice's `State`, which folds the group's balances
(spec §9) for the warning — its own copy, as View balances and View settlement plan
each have theirs; slices share only events. They are scenarios of the screen, not of
deciding, and run against the same fold:

```
S1 - settled up: no warning
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
           AND SettlementRecorded(s1, m2, m1, 3000, …) AND SettlementRecorded(s2, m3, m1, 3000, …)
    THEN   owing: []

S2 - not settled up: who owes and who is owed, in member-added order
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
    THEN   owing: [Alice +6000, Bob -3000, Carol -3000]

S3 - an edited and a removed expense count as they stand
    GIVEN  ... AND ExpenseRecorded(e1, …) AND ExpenseEdited(e1, …, 6000, …) AND ExpenseRemoved(e2, …)
    THEN   owing: as View balances would say
```

## The screen

- **Reached from the group page:** **Archive group** in the `⋯` menu, an address, not a
  dependency on this slice.
- **The page** names the group and says what archiving does — "Nobody will be able to
  add expenses, settle up or change the group. It will move to your archived groups, and
  you can still open it." — and, because there is no way back yet, "This can't be undone
  yet."
- **The warning,** only when the group is not settled up: "Not everyone is settled up:
  Alice is owed £60.00, Bob owes £30.00, Carol owes £30.00. You can archive it anyway."
  Amounts through `Web/Money`. A settled-up group shows no warning.
- **One button,** **Archive group**, in a plain form with antiforgery; **Cancel** goes
  back to the group. Saved, or already archived (a double tap, or two phones): back to the
  group page, where the archived banner is the answer.
- **The page of an archived group** is not shown: the address goes to the group page, where
  the banner is the answer.
- **A non-member, a missing group and a malformed id** get the one not-found page (404).
  A group already archived, posted again, is answered as saved.

## Notes

- **Order of checks:** membership, then whether it is already archived. Nothing else can
  be wrong with this command.
- **Why the rule is not here.** Every other slice checks `GroupArchived` itself, with
  `group is archived` right after membership: the duplication is a handful of lines per
  slice and is accepted (spec §11). This slice owns the event, not the rule.
- **A concurrent save** answers 409; the client retries.

## Read by

- **View group:** a banner, no add or settle-up buttons, and no `⋯` menu. The group stays
  readable.
- **View homepage:** the group leaves the main list and is listed under **Archived**.
- **View expense:** the sheet shows the expense without Edit and Remove.
- **Every command slice:** `group is archived` (below).

## The archived rule, in the other slices

Built, and added right after membership in the decider of **Record expense, Edit expense, Remove
expense, Record settlement, Add member, Invite member, Rename group** and **Change default
split**: `rejected - group is archived`, with one scenario each in their own docs.
**Accept invite** treats an invite into an archived group as dead (`invite not found`,
home) — the group no longer takes members. Over HTTP a rejected form shows the reason in
place, as for any other; the confirm page of Remove expense answers with the group page;
Record settlement goes back to Settle up with the reason in `?error=`.

## Deferred

- **UnarchiveGroup** and `GroupUnarchived` — with a screen that can hold it (spec §11).
  The `GroupArchived` folds already written are most of the work then: each also folds
  the reverse.
