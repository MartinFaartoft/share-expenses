# Slice 6 — `RecordExpense`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Add expense — the form to record one, reached from the group page. Equal, shares and exact splits (see The screen) |
| Command | `RecordExpense(expenseId, description, amountMinor, payerMemberId, split, paidOn, now, by)`; `splits` computed by deciding |
| Events | `ExpenseRecorded` |
| Code | `src/SplitIt/Slices/RecordExpense/` |
| Endpoints | `GET /groups/{group}/expenses/new` — the form; `POST /groups/{group}/expenses` — its submit; sign-in required |

Records one expense: who paid, how much, who it is split between and how (spec §7).
The event carries both the split as entered — one of three shapes, one per mode —
and the per-person amounts that result, so the amounts the group agreed to are
facts, never re-derived (spec §6).

**The split is a tagged union** (spec §7): exactly one of

```json
{ "mode": "equal",  "participants": ["m1", "m2", "m3"] }
{ "mode": "shares", "shares":  [ { "memberId": "m1", "shares": 2 }, { "memberId": "m2", "shares": 1 } ] }
{ "mode": "exact",  "amounts": [ { "memberId": "m1", "amountMinor": 2000 }, { "memberId": "m2", "amountMinor": 3000 } ] }
```

so a weight on an equal split, or an amount on a shares split, cannot be written.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13).

Amounts are in minor units (pence): `9000` is £90.00. `e1` is the new expense's id,
chosen by the endpoint. Every command is issued at `now = 2026-10-02 12:00 UTC`.
Splits as entered are written `equal [m1, m2]`, `shares [m1×2, m2×1]` and
`exact [m1: 2000, m2: 3000]`; the resulting amounts `{m1: 3000, …}`, in member-added
order. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
```

and `RecordExpense(…)` below omits `e1`, `now` and the date when they are the
defaults: `paidOn = 2026-10-01`.

```
1 - records an equal split
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1, m2, m3], alice)
    THEN   ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                           {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)

2 - a placeholder can pay and share
    WHEN   RecordExpense("Taxi", 3000, m3, equal [m2, m3], alice)
    THEN   ExpenseRecorded(e1, "Taxi", 3000, m3, equal [m2, m3],
                           {m2: 1500, m3: 1500}, 2026-10-01, alice)

3 - the payer need not share
    WHEN   RecordExpense("Bob's ticket", 4500, m1, equal [m2], alice)
    THEN   ExpenseRecorded(e1, "Bob's ticket", 4500, m1, equal [m2],
                           {m2: 4500}, 2026-10-01, alice)

4 - an equal split's leftover goes to the payer first
    WHEN   RecordExpense("Coffee", 1000, m2, equal [m1, m2, m3], alice)
    THEN   ExpenseRecorded(…, {m1: 333, m2: 334, m3: 333}, …)

5 - then in member-added order
    WHEN   RecordExpense("Coffee", 1001, m1, equal [m1, m2, m3], alice)
    THEN   ExpenseRecorded(…, {m1: 334, m2: 334, m3: 333}, …)

6 - and the payer is skipped when not sharing
    WHEN   RecordExpense("Coffee", 1001, m1, equal [m2, m3], alice)
    THEN   ExpenseRecorded(…, {m2: 501, m3: 500}, …)

7 - records a shares split
    WHEN   RecordExpense("Flat", 1000, m1, shares [m1×2, m2×1, m3×1], alice)
    THEN   ExpenseRecorded(e1, "Flat", 1000, m1, shares [m1×2, m2×1, m3×1],
                           {m1: 500, m2: 250, m3: 250}, 2026-10-01, alice)

8 - a shares split's leftovers go by largest remainder
    WHEN   RecordExpense("Flat", 100, m1, shares [m1×1, m2×2], alice)
    THEN   ExpenseRecorded(…, {m1: 33, m2: 67}, …)

9 - equal remainders: payer first, then member-added order
    WHEN   RecordExpense("Flat", 1000, m3, shares [m1×1, m2×1, m3×1], alice)
    THEN   ExpenseRecorded(…, {m1: 333, m2: 333, m3: 334}, …)

10 - records an exact split
    WHEN   RecordExpense("Steak night", 5000, m1, exact [m1: 2000, m2: 3000], alice)
    THEN   ExpenseRecorded(e1, "Steak night", 5000, m1, exact [m1: 2000, m2: 3000],
                           {m1: 2000, m2: 3000}, 2026-10-01, alice)

11 - exact amounts must add up to the total
    WHEN   RecordExpense("Steak night", 5000, m1, exact [m1: 2000, m2: 2999], alice)
    THEN   rejected - exact amounts must add up to the total

12 - the split is recorded in member-added order
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m3, m1, m2], alice)
    THEN   ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                           {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)

13 - any member may record, and is recorded as the actor
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1, m2], bob)
    THEN   ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2],
                           {m1: 4500, m2: 4500}, 2026-10-01, bob)

14 - the group must exist
    GIVEN  (empty stream)
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1], alice)
    THEN   rejected (not found) - group not found

15 - only members of the group may record
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1], mallory)
    THEN   rejected (not found) - group not found

16 - a description is required
    WHEN   RecordExpense("   ", 9000, m1, equal [m1], alice)
    THEN   rejected - description is required

17 - a description is at most 100 characters
    WHEN   RecordExpense(<101 characters>, 9000, m1, equal [m1], alice)
    THEN   rejected - description must be at most 100 characters

18 - the amount must be positive
    WHEN   RecordExpense("Dinner", 0, m1, equal [m1], alice)
    THEN   rejected - amount must be positive

19 - the amount has a ceiling
    WHEN   RecordExpense("Dinner", 1000000000001, m1, equal [m1], alice)
    THEN   rejected - amount is too large

20 - the payer must be a member slot of the group
    WHEN   RecordExpense("Dinner", 9000, m9, equal [m1], alice)
    THEN   rejected - payer is not a member of the group

21 - a split is required
    WHEN   RecordExpense("Dinner", 9000, m1, no split, alice)
    THEN   rejected - a split is required

22 - every participant must be a member slot of the group, in any mode
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1, m9], alice)
    THEN   rejected - participant is not a member of the group

23 - someone must share it
    WHEN   RecordExpense("Dinner", 9000, m1, shares [], alice)
    THEN   rejected - at least one participant is required

24 - nobody shares it twice
    WHEN   RecordExpense("Steak night", 5000, m1, exact [m1: 2500, m2: 2000, m1: 500], alice)
    THEN   rejected - a participant appears more than once

25 - every share is a positive whole number
    WHEN   RecordExpense("Flat", 1000, m1, shares [m1×2, m2×0], alice)
    THEN   rejected - every share must be a positive whole number

26 - no exact amount is negative
    WHEN   RecordExpense("Steak night", 5000, m1, exact [m1: 6000, m2: -1000], alice)
    THEN   rejected - every exact amount must be zero or more

27 - the date may be tomorrow, for time zones
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1], paidOn 2026-10-03, alice)
    THEN   ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1], {m1: 9000}, 2026-10-03, alice)

28 - but no later
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1], paidOn 2026-10-04, alice)
    THEN   rejected - date cannot be in the future

29 - and any earlier date is fine, before the group too
    WHEN   RecordExpense("Flights", 60000, m1, equal [m1, m2], paidOn 2026-06-15, alice)
    THEN   ExpenseRecorded(e1, "Flights", 60000, m1, equal [m1, m2],
                           {m1: 30000, m2: 30000}, 2026-06-15, alice)

30 - a date is required
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1], paidOn none, alice)
    THEN   rejected - date is required

31 - the amount must be readable
    WHEN   RecordExpense("Dinner", no amount, m1, equal [m1], alice)
    THEN   rejected - amount must be a number

32 - an expense id already recorded is not recorded twice
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                                   {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
    WHEN   RecordExpense(e1, "Dinner", 9000, m1, equal [m1, m2, m3], alice)
    THEN   rejected (already recorded) - expense already recorded

33 - an archived group records no expense
    GIVEN  ... AND GroupArchived(alice)
    WHEN   RecordExpense("Dinner", 9000, m1, equal [m1, m2, m3], alice)
    THEN   rejected - group is archived
```

## Notes

- **Splits always sum to the total** — no lost or invented minor units (spec §6).
  Equal: `total / n` each, the remainder one each. Shares: `floor(total × share /
  total shares)` each, the leftover one each by largest fractional remainder.
  Leftovers go payer first if the payer shares, then in member-added order; for
  shares, that order breaks ties between equal remainders. Exact: no rounding; the
  amounts must sum to the total.
- **Splitting is a pure function in `Shared/`**, not in the slice: correcting an
  expense's amount or split (later slices) must compute exactly the same way.
  Computed in 128-bit arithmetic, so `total × share` cannot overflow.
- **Exact is the mode where the client did the arithmetic.** Equal and shares
  record an intention the server can replay — a corrected amount re-splits by it.
  Exact records only amounts; a corrected exact expense needs new amounts (spec §11).
- **Shape errors are not domain rules.** A split with an unknown or missing `mode`,
  or a share or amount missing from its entry, is not a split at all: the request
  fails to read and is answered 400 before anything is decided. What deciding
  checks is what a well-formed split can still get wrong (scenarios 21–26).
- **Order of checks:** membership, then archived, then the id (already recorded), description, amount, payer, the split
  (present, members, not empty, no duplicates, then the mode's own rule), the date.
  A non-member gets `group not found` whatever else is wrong. Once a member,
  rejections may name what is wrong — including that a slot is not in the group:
  the caller can see every slot anyway.
- **Member slots, not users.** Payer and participants are `MemberId`s, and a
  placeholder slot is as good as a claimed one (scenario 2): that is what
  placeholders are for (spec §4).
- **Description:** trimmed, required, at most 100 visible characters (as names
  count them).
- **Amount:** a positive integer in the group currency's minor unit; the client
  converts from what the user types, using the currency's exponent. At most 10¹²
  minor units — ten billion pounds — so amounts stay exact in a JSON number in any
  client (2⁵³), and a typo with extra zeros is caught.
- **The date** is the day the money was spent, a domain fact (`DateOnly`) — not when
  it was recorded, which stays Marten metadata (spec §11). Up to one day after today
  (UTC), so a user ahead of UTC is never told their today is the future; any earlier
  date, including before the group was created — the flights were booked first.
- **The split is recorded as entered, but sorted** into member-added order, as are
  the amounts: the event's shape does not depend on the order of a request.
- **A zero exact amount** is allowed (`exact [m1: 5000, m2: 0]`): recorded as
  entered, though it changes no balance.

## The screen

- **Reached from the group page:** the **Add expense** button under the history,
  and the empty state ("Nothing yet. Add the first expense."). A link in View
  group's screen: an address, not a dependency on this slice.
- **The fields,** top to bottom, as in the model:
  - **What for** — the description; `required`, `maxlength` 100 (a hint: the
    decider counts visible characters, `maxlength` UTF-16 units).
  - **Amount** — a text input, `inputmode="decimal"`, labelled with the group's
    currency (`GBP £`, as `Web/Money.Label`). The user types it in the currency's
    units; `Money.TryParse` (beside `Money.Format`, the only place minor units become
    decimals) turns it into `amountMinor`: digits with at most one `.` or `,` and at
    most the currency's decimals after it (2 for GBP, 0 for JPY, 3 for KWD); no
    thousands separators, which would be ambiguous (`1,234`). Anything else is
    unreadable, and goes to deciding as no amount (scenario 31).
  - **Paid by** — a `<select>` of the group's member slots in member-added order,
    **the signed-in user's own slot preselected**, marked "(you)". Placeholders are
    listed like anyone (scenario 2).
  - **Split** — *Equally*, *By shares* or *Exact amounts* (see The split modes on
    the form), **the group's default** (slice-16), Equally until one is set.
  - **Shared between** — one row per slot, in member-added order, **all checked**:
    a checkbox, the name, and the mode's field (scenarios 1–12). Unchecking
    everyone is rejected by deciding (scenario 23), not prevented by the page.
  - **Date** — `<input type="date">`, preselected to today (UTC) and capped at
    tomorrow (UTC), as deciding is (scenario 27). A person east of UTC late in the
    evening sees yesterday preselected and changes it; a browser-local default would
    need script.
- **Add expense** posts the form — a plain form, no htmx, as New group. Recorded:
  **back to the group page** (302), where the expense is the newest entry and the
  user's standing has moved (View group's projection is inline, so it is already
  there). Rejected: the page again, **with what was typed** and the reason under
  the form (`role="alert"`), 200.
- **A Back link** to the group, named by it ("← Lisbon trip").
- **A non-member, a missing group and a malformed id** get the one not-found page
  (404), on the form and on the submit alike.

### The split modes on the form

All three modes are offered, on this form and on Edit expense's (slice-12), in the
same markup. Equal is the preselected one until the group sets a default.

- **One list of members, one row each.** A checkbox (is in the split — "Shared
  between") and the name, as now; the mode adds a field to the row:
  - *Equally* — nothing more.
  - *By shares* — a whole number, preset to `1` ("a couple counts as 2"):
    `shares-<memberId>`, a text input with `inputmode="numeric"` (a numeric keypad,
    but no `type="number"`, `min` or `step`: a hidden field that fails its own
    constraint blocks the whole submit, and cannot say why).
  - *Exact amounts* — an amount in the currency's units, as the Amount field is read:
    `amount-<memberId>`, `inputmode="decimal"`, blank at first.
  A member who is not checked is not in the split, whatever their row holds.
- **The mode is three radio buttons** (`mode`: `equal`, `shares`, `exact`; "Equally",
  "By shares", "Exact amounts") in a fieldset under *Split*, replacing the hidden
  field. **Switching is CSS, not a round trip:** every mode's fields are in the page,
  and `form:has(input[name=mode][value=shares]:checked)` shows the shares inputs, and
  so on for the others. No script, no htmx, nothing to fall back from; values typed
  under one mode stay there if the user switches away and back. A browser without
  `:has` shows every field, which is ugly but works: the endpoint reads the selected
  mode's fields and ignores the rest. Mode-only fields carry no `required` and no other
  constraint, for the reason above; deciding checks them. A member who is not checked has
  their field dimmed: it is ignored.
- **The endpoint reads the fields by name** (the seam slice-06 left): one function
  turns mode plus the posted form into an `ExpenseSplit`, or says why it cannot.
  - Equal: the checked members.
  - Shares: the checked members with their `shares-<id>`, read as a whole number
    (a leading `-` allowed, so `-1` and `0` reach deciding, which rejects them,
    scenario 25).
  - Exact: the checked members with their `amount-<id>`, read by `Money.TryParse`.
  - **A field that cannot be read is a shape error, answered by the form before
    deciding, like an unknown mode:** the form again with what was typed, 200, and
    "every share must be a whole number" or "every exact amount must be a number" —
    for a blank, a decimal share, text. Not a domain rule; a split with an unreadable
    entry is not a split (see Shape errors above). An unknown or missing mode is
    "split mode not supported", as now.
  - A shape error replaces deciding's reason, as "split mode not supported" always
    has: the form says the first thing to fix about the split.
- **Exact shows its running total,** from a few lines of script
  (`wwwroot/js/exact-total.js`, the app's second): under the exact rows, "Assigned
  £45.00 of £50.00 · £5.00 left" (or "£5.00 over"), updated as amounts or the Amount
  change, in the currency's decimals (`data-places` on the line). Without script the
  line is absent, and deciding's "exact amounts must add up to the total" is the
  check, shown with the entered values. Nothing is filled in for the user.
- **The form opens with the group's default split** (slice-16): the mode, the members
  left out unchecked, the shares typed in — `1` for anyone not listed, so a member added
  later starts in. Until a default is set, that is Equally, everyone checked, shares `1`,
  as before. Exact is never the default. After a rejection the form shows what was typed,
  not the default.
- **After a rejection** the form shows everything as typed: the mode, the checked
  members, every share and every amount, including those of a mode not selected.
- **Edit expense starts from the expense:** its mode selected, its members checked,
  its shares or amounts filled in. The fields of the other modes start from what is
  useful: shares `1`, exact the expense's recorded amounts, so turning an equal
  expense into an exact one begins from what each person owes now.

## Notes on the screen

- **The screen folds this slice's own `State`,** not View group's read model: a
  slice does not reach into another's (spec §12). `State` grows what the form
  needs — the group's name and currency, each slot's name, and which slot each user
  holds (for the preselected payer) — and the ids of expenses already recorded. The
  form is built from it for the `GET` (folded live, `[ReadAggregate]`), and the same
  `State` is what the `POST` decides on.
- **Submitting twice records one expense.** As New group: the expense's id is
  chosen when the form is *shown* and carried in a hidden field, and `State` keeps
  the ids recorded, so deciding knows one it has seen (scenario 32). A double tap, a
  retry after a dropped connection, the back button and resubmit all name an expense
  that now exists, and go **back to the group page** as if they had recorded it. The id
  comes from the client, so it is not trusted: a malformed one is replaced by a fresh
  id (the form still works, without the guard), and the worst a forged one does is
  record under an id the forger chose — within a group they are a member of.
- **The amount's text is kept** as typed when the page is shown again, not
  reformatted from the number.
- **Antiforgery** on the submit (`[ValidateAntiforgery]`, spec §3).
- **A removed expense stays recorded:** its id remains known to this slice after
  `ExpenseRemoved` (Remove expense), so the same form submitted again is "already
  recorded" and does not bring the expense back.
- **A concurrent save** answers 409, as AddMember (the open "409 retry UX in forms"
  task, spec §14).

## Deferred to the slices that introduce the events

- **A removed member** cannot pay or share — with `MemberRemoved`.

## Concurrency

As AddMember: a conflicting save answers **409** and the client retries. Two phones
adding the same dinner both succeed: they are two expenses, and someone removes one.
