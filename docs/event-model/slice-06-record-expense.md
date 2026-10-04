# Slice 6 — `RecordExpense`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Add expense |
| Command | `RecordExpense(expenseId, description, amountMinor, payerMemberId, split, paidOn, now, by)`; `splits` computed by deciding |
| Events | `ExpenseRecorded` |
| Code | `src/ShareExpenses/Slices/RecordExpense/` |
| Endpoint | none yet — its screen is not built, so nothing reaches it (spec §3). The slice's logic and specs stand |

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
- **Order of checks:** membership, then description, amount, payer, the split
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

## Deferred to the slices that introduce the events

- **Archived group** rejects the command — with `GroupArchived`.
- **A removed member** cannot pay or share — with `MemberRemoved`.

## Concurrency

As AddMember: a conflicting save answers **409** and the client retries. Two phones
adding the same dinner both succeed: they are two expenses, and someone removes one.
