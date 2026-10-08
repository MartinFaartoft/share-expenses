# Slice 16 — `ChangeDefaultSplit`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Default split — how a new expense is split unless the form is changed: the mode, and each member's default share. Reached from the group page's `⋯` menu |
| Command | `ChangeDefaultSplit(mode, shares[memberId, shares], by)` |
| Events | `GroupDefaultSplitChanged` |
| Code | `src/SplitIt/Slices/ChangeDefaultSplit/` |
| Endpoints | `GET /groups/{group}/default-split` — the form; `POST` to the same path — its submit; sign-in required |

Most groups split the same way most of the time: equally, but never with Dave; or by
shares, because the couple counts as two. This records that, so the **Add expense** form
opens ready, and only the odd expense needs changing.

**It is a default, not a rule.** It changes what Add expense starts with, nothing else:
existing expenses are untouched, and any expense can still be recorded with any split and
anyone in it. Edit expense opens an expense as it is, not as the default. Exact has no
sensible default (it is amounts, not an intention), so only **equal** and **shares** are
offered.

## What a default is

A mode, and a share for each member who does not count as `1`:

- **`equal`** — everyone is in unless left out. The shares that matter are `0` (left
  out); any other share is read as `1`.
- **`shares`** — each member's weight; `0` is left out. A couple is `2`.
- **A member not listed counts as `1`.** So a member added later starts in the default
  split, and one who was left out stays out.
- **Before any change** the default is `equal`, nobody left out: what the form always did.

The event records the default **normalised**: only members whose share is not `1`, in
member-added order, and for `equal` only the `0`s. That makes "the same default" a plain
comparison, and keeps the log free of a list of ones.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13). Shares are written `[m1×2, m3×0]`. Unless stated
otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
```

```
1 - a shares default: only the shares that are not 1 are recorded
    WHEN   ChangeDefaultSplit(shares, [m1×2, m2×2, m3×1], alice)
    THEN   GroupDefaultSplitChanged(shares, [m1×2, m2×2], alice)

2 - an equal default with someone left out
    WHEN   ChangeDefaultSplit(equal, [m1×1, m2×1, m3×0], alice)
    THEN   GroupDefaultSplitChanged(equal, [m3×0], alice)

3 - an equal default reads any share above zero as one
    GIVEN  ... AND GroupDefaultSplitChanged(shares, [m1×2], alice)
    WHEN   ChangeDefaultSplit(equal, [m1×3, m2×1, m3×1], alice)
    THEN   GroupDefaultSplitChanged(equal, [], alice)

4 - a member who is not listed counts as one
    WHEN   ChangeDefaultSplit(shares, [m1×2], alice)
    THEN   GroupDefaultSplitChanged(shares, [m1×2], alice)

5 - shares are recorded in member-added order
    WHEN   ChangeDefaultSplit(shares, [m3×0, m1×2], alice)
    THEN   GroupDefaultSplitChanged(shares, [m1×2, m3×0], alice)

6 - a default that is the one in force changes nothing, and appends nothing
    GIVEN  ... AND GroupDefaultSplitChanged(shares, [m1×2], alice)
    WHEN   ChangeDefaultSplit(shares, [m1×2, m2×1, m3×1], alice)
    THEN   (no events)

7 - the first default, if it is the original one, changes nothing
    WHEN   ChangeDefaultSplit(equal, [m1×1, m2×1, m3×1], alice)
    THEN   (no events)

8 - back to the original is a change after another
    GIVEN  ... AND GroupDefaultSplitChanged(equal, [m3×0], alice)
    WHEN   ChangeDefaultSplit(equal, [m1×1, m2×1, m3×1], alice)
    THEN   GroupDefaultSplitChanged(equal, [], alice)

9 - the mode is part of the default
    GIVEN  ... AND GroupDefaultSplitChanged(equal, [m3×0], alice)
    WHEN   ChangeDefaultSplit(shares, [m3×0], alice)
    THEN   GroupDefaultSplitChanged(shares, [m3×0], alice)

10 - any member may change it, and is recorded as the actor
    WHEN   ChangeDefaultSplit(equal, [m3×0], bob)
    THEN   GroupDefaultSplitChanged(equal, [m3×0], bob)

11 - the group must exist
    GIVEN  (empty stream)
    WHEN   ChangeDefaultSplit(equal, [], alice)
    THEN   rejected (not found) - group not found

12 - only members of the group may change it
    WHEN   ChangeDefaultSplit(equal, [m3×0], mallory)
    THEN   rejected (not found) - group not found

13 - an archived group cannot change its default
    GIVEN  ... AND GroupArchived(alice)
    WHEN   ChangeDefaultSplit(equal, [m3×0], alice)
    THEN   rejected - group is archived

14 - the mode must be equal or shares
    WHEN   ChangeDefaultSplit(exact, [], alice)
    THEN   rejected - default split must be equal or shares

15 - or something that is a mode at all
    WHEN   ChangeDefaultSplit(no mode, [], alice)
    THEN   rejected - default split must be equal or shares

16 - every member listed must be a member slot of the group
    WHEN   ChangeDefaultSplit(shares, [m1×2, m9×1], alice)
    THEN   rejected - participant is not a member of the group

17 - nobody is listed twice
    WHEN   ChangeDefaultSplit(shares, [m1×2, m1×3], alice)
    THEN   rejected - a participant appears more than once

18 - a share is a whole number, zero or more
    WHEN   ChangeDefaultSplit(shares, [m1×-1], alice)
    THEN   rejected - every share must be zero or more

19 - someone must share by default
    WHEN   ChangeDefaultSplit(shares, [m1×0, m2×0, m3×0], alice)
    THEN   rejected - at least one member must share by default

20 - a member not listed counts as sharing, so this is not "nobody"
    GIVEN  ... AND MemberAdded(m4, "Dave", alice)
    WHEN   ChangeDefaultSplit(equal, [m1×0, m2×0, m3×0], alice)
    THEN   GroupDefaultSplitChanged(equal, [m1×0, m2×0, m3×0], alice)
```

## Notes

- **Order of checks:** membership, then archived, then the mode, the members (slots,
  distinct), the shares, that someone shares, and last whether it changed. A non-member
  gets `group not found` whatever else is wrong.
- **Normalised before compared:** the command's shares are normalised (ones dropped, the
  order fixed, equal read as zero or one) and compared with the default in force — the
  last `GroupDefaultSplitChanged`, or the original — scenarios 6–8.
- **Someone must share by default,** counting those not listed: leaving out every member
  there is would open the Add expense form with nobody in it (scenario 19).
- **Exact is not a default:** `exact` is not accepted (scenario 14).

## The screen

- **The form is Add expense's split section,** without Exact: **Split** (*Equally* /
  *By shares*, Equally preselected), and one row per member with a checkbox ("shares by
  default") and, for shares, a number. It is the same markup and CSS, copied, not shared
  (slice-06, "The split modes on the form").
  - **Filled in with the default in force:** its mode, the members left out unchecked,
    the shares of the others.
  - **The command is built from the rows:** a member not checked is `0`; a member checked
    has the number typed (equal ignores it). A share that cannot be read as a whole number is
    a shape error, answered by the form before deciding, as in Add expense.
- **Reached from the group page:** **Default split** in the `⋯` menu, an address, not a
  dependency on this slice.
- **Save** posts it: a plain form, with antiforgery. Saved, or nothing to save: back to
  the group page. Rejected: the page again with what was typed, the reason under the form.
  A conflicting save answers 409.
- **A Back link** to the group.
- **A non-member, a missing group and a malformed id** get the one not-found page (404).

## Notes on the screen

- **The screen folds this slice's own `State`:** the slots with their names, who holds
  which slot, whether the group is archived, and the default in force.
- **A new member starts in the default** (not listed counts as one), so adding Dave
  needs no visit here.

## Read by

**Record expense:** its `State` folds `GroupDefaultSplitChanged`, and the Add expense form
opens with it — the mode, the members left out unchecked, the shares typed in (`1` for
the unlisted). Add expense's own scenarios stay about recording; the form's start is
checked at the screen (slice-06, "The split modes on the form").

## Deferred

- **A removed member** is dropped from the default — with `MemberRemoved`.
- **A renamed member** needs nothing: the default names slots, not names.
