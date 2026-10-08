# Slice 13 — `ViewExpense`

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Expense — one expense: what it was, who paid, who shares it and what each owes, and its actions. A **bottom sheet** over the group page; its own page without script |
| Read model | `ExpenseReadModel` — **live**: the group stream folded per request, nothing stored |
| Query | `ViewExpense(expenseId, userId)` — the expense from the route, and the signed-in user; the group from the route selects the stream |
| Code | `src/SplitIt/Slices/ViewExpense/` |
| Endpoint | `GET /groups/{group}/expenses/{expense}` — the screen; sign-in required. The sheet for htmx, the page otherwise |

The expense cards in the group's history say little: a name, an amount, who paid. This
is the rest, and where the rarely used actions live — **Edit** (Edit expense) and
**Remove** (Remove expense) — so the cards need not carry them. The actions are
addresses, not dependencies on those slices.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it is
not a query field (spec §13).

Amounts are in minor units (pence). Lines are written `Alice 3000`: name, amount;
`Alice ×2 500` where the split is by shares. Unless stated otherwise, every scenario
starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
  AND  ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                       {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
```

```
1 - shows the expense and who shares it
    WHEN   ViewExpense(e1, alice)
    THEN   { g1, e1, GBP, you: m1, "Dinner", 9000, paid by m1 "Alice", 2026-10-01,
             equal: [Alice 3000, Bob 3000, Carol 3000] }

2 - the payer need not share
    GIVEN  ... AND ExpenseRecorded(e2, "Bob's ticket", 4500, m1, equal [m2], {m2: 4500}, 2026-10-01, alice)
    WHEN   ViewExpense(e2, alice)
    THEN   { …, "Bob's ticket", 4500, paid by m1 "Alice", equal: [Bob 4500] }

3 - a placeholder can have paid it
    GIVEN  ... AND ExpenseRecorded(e2, "Taxi", 3000, m3, equal [m1, m3], {m1: 1500, m3: 1500}, 2026-10-01, alice)
    WHEN   ViewExpense(e2, alice)
    THEN   { …, "Taxi", 3000, paid by m3 "Carol", equal: [Alice 1500, Carol 1500] }

4 - a shares split shows each weight
    GIVEN  ... AND ExpenseRecorded(e2, "Flat", 1000, m1, shares [m1×2, m2×1, m3×1],
                                   {m1: 500, m2: 250, m3: 250}, 2026-10-01, alice)
    WHEN   ViewExpense(e2, alice)
    THEN   { …, "Flat", 1000, shares: [Alice ×2 500, Bob ×1 250, Carol ×1 250] }

5 - an exact split shows each amount
    GIVEN  ... AND ExpenseRecorded(e2, "Steak night", 5000, m1, exact [m1: 2000, m2: 3000],
                                   {m1: 2000, m2: 3000}, 2026-10-01, alice)
    WHEN   ViewExpense(e2, alice)
    THEN   { …, "Steak night", 5000, exact: [Alice 2000, Bob 3000] }

6 - an edited expense shows its latest values
    GIVEN  ... AND ExpenseEdited(e1, "Team dinner", 6000, m2, equal [m1, m2],
                                 {m1: 3000, m2: 3000}, 2026-09-28, bob)
    WHEN   ViewExpense(e1, alice)
    THEN   { …, "Team dinner", 6000, paid by m2 "Bob", 2026-09-28, equal: [Alice 3000, Bob 3000] }

7 - "you" is the caller's own slot, whoever the caller is
    WHEN   ViewExpense(e1, bob)
    THEN   { …, you: m2, … }

8 - the group must exist
    GIVEN  (empty stream)
    WHEN   ViewExpense(e1, alice)
    THEN   not found

9 - only members may view
    WHEN   ViewExpense(e1, mallory)
    THEN   not found

10 - the expense must be in the group
    WHEN   ViewExpense(e9, alice)
    THEN   not found

11 - a removed expense is not found
    GIVEN  ... AND ExpenseRemoved(e1, bob)
    WHEN   ViewExpense(e1, alice)
    THEN   not found

12 - an archived group's expense is shown, marked archived
    GIVEN  ... AND GroupArchived(bob)
    WHEN   ViewExpense(e1, alice)
    THEN   { …, "Dinner", 9000, …, archived: true }
```

## The screen

- **The sheet,** over the group page, from a tap on an expense's card: the
  description and amount, "You paid · 1 Oct 2026" (or "Bob paid …"), the split —
  "Shared equally", "By shares" or "Exact amounts" — with a line per person and what
  they owe ("(you)" marking the caller), then **Edit** (a pencil) and **Remove** (a
  trash bin), right-aligned icon buttons: links to their screens, named for assistive
  technology (`aria-label`, and `title` for a hover). A small **×** in the upper right
  corner closes the sheet; it is the only thing that stays on the page. The page
  version has the icons and no ×.
- **Where it sits:** at the bottom of the screen on a phone (below 600px wide), the
  usual bottom sheet; centred, with rounded corners all round, on anything wider,
  where a sheet stuck to the bottom of a laptop window reads as misplaced.
- **Without htmx it is a page,** the same content: the top bar with a back chevron
  to the group, and the same actions. The card's link goes to the same address
  whether or not htmx is there; htmx upgrades it (below).
- **Not a member, no such group, malformed id, no such expense, a removed one:** the
  same "not found", 404 — as the page; as the sheet, the same message in a sheet
  (below).
- **Settlements have no sheet** until there is something to do with one: their cards
  are not links. Remove settlement will give them one, and this pattern.

## Notes on the screen

- **One address, two answers.** `GET /groups/{group}/expenses/{expense}` answers an
  htmx request (`request.IsHtmx()`, spec §14) with the sheet — a fragment — and any
  other request with the whole page. Both are one component for the content, in a
  sheet or in the page shell. The answer depends on the header, so every answer
  carries `Vary: HX-Request`: a cache, or the back button, never shows the fragment
  as a page.
- **The card is a link** (`<a href>`) to that address, with `hx-get` to the same
  address, `hx-target="#sheet"`: a tap opens the sheet; a middle-click or
  "open in new tab" opens the page. `#sheet` is an empty container at the end of the
  group page (View group's), which the fragment replaces — View group knows an
  address and an element id, not this slice.
- **The sheet is a `<dialog>` opened as a modal by a few lines of script**
  (`wwwroot/js/sheet.js`): on `htmx:afterSwap` it calls `showModal()`, so Escape
  closes it, focus stays inside it and the page behind is inert; a tap on the
  backdrop closes it. The × is a `<form method="dialog">`, which closes a dialog by
  itself. The fragment arrives with `open` already set, and the script clears it
  before `showModal()`: if the script fails, the sheet is still a sheet, fixed to the
  bottom of the screen (non-modal), and the × still works. The first control takes
  focus; Edit and Remove are ordinary links. Checked in Chrome, by hand, not by a
  test: no test here runs script.
- **The sheet does not change the address.** Back leaves the group page, as it would
  without the sheet. Edit and Remove are full navigations; Edit's and Remove's back
  links and redirects go to the group page, as before.
- **A stale card:** the expense was removed on another phone while this one showed
  the group. The tap answers 404. htmx does not swap a 4xx by default, so the tap
  would do nothing; the page shell therefore tells htmx to swap a 404
  (`<meta name="htmx-config">`, `responseHandling`), and a 404 to an htmx request is
  a sheet saying "Expense not found", with the ×. Nothing else changes: sign-in's
  answers are 200s, a conflict is a 409.
- **Folds the group stream live,** its own `State`: the group's currency, each slot's
  name, which slot each user holds, and each expense as it stands (recorded, then
  edited), dropping a removed one. Duplicated from the other expense slices, as ever
  (spec §12). Slot names are the names at present; the history never shows the name
  an event was recorded under (renaming is `MemberRenamed`'s to fold).
- **The line for a person** is from the recorded `splits`, never re-derived (spec
  §6); the weight for a shares split, from the `split` as entered.
- **No authorship yet.** Who recorded or last edited the expense is in the events
  (`by`), and belongs with the activity feed (spec §11, `ActivityFeed`).

## Archived groups

The read model gains `archived`, from `GroupArchived`. For an archived group the sheet
shows the expense as before, **without Edit and Remove**: the forms would reject them.
Nothing else changes (scenario 12).

## Deferred to the slices that introduce the events

- **Renamed members** show their new names — with `MemberRenamed`.
- **A released claim** — the user loses access — with `MemberClaimReleased`.
- **Authorship and history of the expense** — with `ActivityFeed`.
