# Slice 12 — `EditExpense`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Edit expense — the Add expense form, filled in with one expense, reached from the expense's sheet (View expense). Equal, shares and exact splits, as Add expense |
| Command | `EditExpense(expenseId, description, amountMinor, payerMemberId, split, paidOn, now, by)`; `splits` computed by deciding |
| Events | `ExpenseEdited` |
| Code | `src/SplitIt/Slices/EditExpense/` |
| Endpoints | `GET /groups/{group}/expenses/{expense}/edit` — the form; `POST` to the same path — its submit; sign-in required |

Corrects a mistaken expense: the whole expense is submitted again, and the event
carries all of its new values (spec §11). Nothing is deleted (spec §7): the
`ExpenseRecorded` stays, and `ExpenseEdited` is a later fact beside it. Every read
model that folds the expense then folds the edit: the expense is what the edit says,
and the balances and the settle-up plan are as if it had been recorded that way.

Any member may edit any expense (spec §5: flat trust, backed by the log). **Last
write wins**: an edit replaces the expense as it is when the edit is saved, even if
someone edited it after this form was opened. The log has both.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13).

Amounts are in minor units (pence). `EditExpense(…)` below names `e1` and the fields
that differ from the expense as recorded, and omits `now`: every command is issued at
`now = 2026-10-02 12:00 UTC`. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
  AND  ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                       {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
```

and an edit that differs only in one field says so: `EditExpense(e1, amount 12000, alice)`
is `("Dinner", 12000, m1, equal [m1, m2, m3], paidOn 2026-10-01)`.

```
1 - edits the amount: the splits are recomputed
    WHEN   EditExpense(e1, amount 12000, alice)
    THEN   ExpenseEdited(e1, "Dinner", 12000, m1, equal [m1, m2, m3],
                         {m1: 4000, m2: 4000, m3: 4000}, 2026-10-01, alice)

2 - edits the description
    WHEN   EditExpense(e1, description "Team dinner", alice)
    THEN   ExpenseEdited(e1, "Team dinner", 9000, m1, equal [m1, m2, m3],
                         {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)

3 - edits who shares it
    WHEN   EditExpense(e1, split equal [m1, m2], alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, equal [m1, m2],
                         {m1: 4500, m2: 4500}, 2026-10-01, alice)

4 - edits the date
    WHEN   EditExpense(e1, paidOn 2026-09-28, alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                         {m1: 3000, m2: 3000, m3: 3000}, 2026-09-28, alice)

5 - a new payer re-splits: the leftover goes to the payer first
    GIVEN  ... AND ExpenseRecorded(e2, "Coffee", 1000, m1, equal [m1, m2, m3],
                                   {m1: 334, m2: 333, m3: 333}, 2026-10-01, alice)
    WHEN   EditExpense(e2, "Coffee", 1000, payer m2, equal [m1, m2, m3], alice)
    THEN   ExpenseEdited(e2, "Coffee", 1000, m2, equal [m1, m2, m3],
                         {m1: 333, m2: 334, m3: 333}, 2026-10-01, alice)

6 - a placeholder can become the payer, and share
    WHEN   EditExpense(e1, payer m3, split equal [m2, m3], alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m3, equal [m2, m3],
                         {m2: 4500, m3: 4500}, 2026-10-01, alice)

7 - edits everything at once
    WHEN   EditExpense(e1, "Taxi", 3000, m3, equal [m2, m3], paidOn 2026-10-02, alice)
    THEN   ExpenseEdited(e1, "Taxi", 3000, m3, equal [m2, m3],
                         {m2: 1500, m3: 1500}, 2026-10-02, alice)

8 - the split is recorded in member-added order
    WHEN   EditExpense(e1, split equal [m3, m1], alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, equal [m1, m3],
                         {m1: 4500, m3: 4500}, 2026-10-01, alice)

9 - any member may edit it, in its split or not, the payer or not; recorded as the actor
    GIVEN  ... AND MemberAdded(m4, "Dave", alice) AND MemberClaimed(m4, dave)
    WHEN   EditExpense(e1, amount 12000, dave)
    THEN   ExpenseEdited(e1, "Dinner", 12000, m1, equal [m1, m2, m3],
                         {m1: 4000, m2: 4000, m3: 4000}, 2026-10-01, dave)

10 - an expense that has been settled against can be edited: balances replay
    GIVEN  ... AND SettlementRecorded(s1, m2, m1, 3000, 2026-10-02, bob)
    WHEN   EditExpense(e1, amount 12000, alice)
    THEN   ExpenseEdited(e1, "Dinner", 12000, m1, equal [m1, m2, m3],
                         {m1: 4000, m2: 4000, m3: 4000}, 2026-10-01, alice)

11 - last write wins: an expense already edited is edited again, whoever did it
    GIVEN  ... AND ExpenseEdited(e1, "Dinner", 12000, m1, equal [m1, m2, m3],
                                 {m1: 4000, m2: 4000, m3: 4000}, 2026-10-01, bob)
    WHEN   EditExpense(e1, amount 15000, alice)
    THEN   ExpenseEdited(e1, "Dinner", 15000, m1, equal [m1, m2, m3],
                         {m1: 5000, m2: 5000, m3: 5000}, 2026-10-01, alice)

12 - an edit against the latest edit is compared with that, not with the recording
    GIVEN  ... AND ExpenseEdited(e1, "Dinner", 12000, m1, equal [m1, m2, m3],
                                 {m1: 4000, m2: 4000, m3: 4000}, 2026-10-01, bob)
    WHEN   EditExpense(e1, amount 9000, alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                         {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)

13 - an edit that changes nothing appends nothing
    WHEN   EditExpense(e1, "Dinner", 9000, m1, equal [m1, m2, m3], paidOn 2026-10-01, alice)
    THEN   (no events)

14 - nor does one that only reorders the split or pads the description
    WHEN   EditExpense(e1, description "  Dinner ", split equal [m3, m2, m1], alice)
    THEN   (no events)

15 - the group must exist
    GIVEN  (empty stream)
    WHEN   EditExpense(e1, amount 12000, alice)
    THEN   rejected (not found) - group not found

16 - only members of the group may edit
    WHEN   EditExpense(e1, amount 12000, mallory)
    THEN   rejected (not found) - group not found

17 - the expense must be in the group
    WHEN   EditExpense(e9, amount 12000, alice)
    THEN   rejected (not found) - expense not found

18 - a removed expense cannot be edited
    GIVEN  ... AND ExpenseRemoved(e1, bob)
    WHEN   EditExpense(e1, amount 12000, alice)
    THEN   rejected (not found) - expense not found

19 - a non-member gets "group not found" whatever else is wrong
    WHEN   EditExpense(e9, description "", amount 0, mallory)
    THEN   rejected (not found) - group not found

20 - a description is required
    WHEN   EditExpense(e1, description "   ", alice)
    THEN   rejected - description is required

21 - a description is at most 100 characters
    WHEN   EditExpense(e1, description <101 characters>, alice)
    THEN   rejected - description must be at most 100 characters

22 - the amount must be readable
    WHEN   EditExpense(e1, no amount, alice)
    THEN   rejected - amount must be a number

23 - the amount must be positive
    WHEN   EditExpense(e1, amount 0, alice)
    THEN   rejected - amount must be positive

24 - the amount has a ceiling
    WHEN   EditExpense(e1, amount 1000000000001, alice)
    THEN   rejected - amount is too large

25 - the payer must be a member slot of the group
    WHEN   EditExpense(e1, payer m9, alice)
    THEN   rejected - payer is not a member of the group

26 - a payer is required
    WHEN   EditExpense(e1, no payer, alice)
    THEN   rejected - payer is not a member of the group

27 - a split is required
    WHEN   EditExpense(e1, no split, alice)
    THEN   rejected - a split is required

28 - every participant must be a member slot of the group
    WHEN   EditExpense(e1, split equal [m1, m9], alice)
    THEN   rejected - participant is not a member of the group

29 - someone must share it
    WHEN   EditExpense(e1, split equal [], alice)
    THEN   rejected - at least one participant is required

30 - nobody shares it twice
    WHEN   EditExpense(e1, split exact [m1: 5000, m2: 2000, m1: 2000], alice)
    THEN   rejected - a participant appears more than once

31 - every share is a positive whole number
    WHEN   EditExpense(e1, split shares [m1×2, m2×0], alice)
    THEN   rejected - every share must be a positive whole number

32 - no exact amount is negative
    WHEN   EditExpense(e1, split exact [m1: 10000, m2: -1000], alice)
    THEN   rejected - every exact amount must be zero or more

33 - exact amounts must add up to the total
    WHEN   EditExpense(e1, split exact [m1: 5000, m2: 3999], alice)
    THEN   rejected - exact amounts must add up to the total

34 - a date is required
    WHEN   EditExpense(e1, paidOn none, alice)
    THEN   rejected - date is required

35 - the date may be tomorrow, for time zones
    WHEN   EditExpense(e1, paidOn 2026-10-03, alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                         {m1: 3000, m2: 3000, m3: 3000}, 2026-10-03, alice)

36 - but no later
    WHEN   EditExpense(e1, paidOn 2026-10-04, alice)
    THEN   rejected - date cannot be in the future

37 - and any earlier date is fine, before the group too
    WHEN   EditExpense(e1, paidOn 2026-06-15, alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                         {m1: 3000, m2: 3000, m3: 3000}, 2026-06-15, alice)

38 - an edit back to what was recorded is a change, after an edit
    GIVEN  ... AND ExpenseEdited(e1, "Dinner", 12000, m1, equal [m1, m2, m3],
                                 {m1: 4000, m2: 4000, m3: 4000}, 2026-10-01, bob)
    WHEN   EditExpense(e1, "Dinner", 9000, m1, equal [m1, m2, m3], paidOn 2026-10-01, alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                         {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)

39 - a description of exactly 100 characters is fine
    WHEN   EditExpense(e1, description <100 characters>, alice)
    THEN   ExpenseEdited(e1, <100 characters>, 9000, …)

40 - and so is an amount of exactly the ceiling
    WHEN   EditExpense(e1, amount 1000000000000, split equal [m1], alice)
    THEN   ExpenseEdited(e1, "Dinner", 1000000000000, m1, equal [m1], {m1: 1000000000000}, …)

41 - a shares split rounds by largest remainder
    WHEN   EditExpense(e1, amount 100, split shares [m1×1, m2×2], alice)
    THEN   ExpenseEdited(e1, "Dinner", 100, m1, shares [m1×1, m2×2], {m1: 33, m2: 67}, …)

42 - an exact split is its own amounts
    WHEN   EditExpense(e1, split exact [m1: 5000, m2: 4000], alice)
    THEN   ExpenseEdited(e1, "Dinner", 9000, m1, exact [m1: 5000, m2: 4000],
                         {m1: 5000, m2: 4000}, 2026-10-01, alice)

43 - an edit is validated before it is compared
    WHEN   EditExpense(e1, split equal [m1, m2, m3, m9], alice)
    THEN   rejected - participant is not a member of the group
```

## Notes

- **The rules are Record expense's** (slice-06-record-expense.md, scenarios 16–31),
  copied, not shared: slices stay independent. Scenarios 20–37 repeat them here so
  an edit cannot get past a rule the original had to meet. The splitting itself is
  the same pure function in `Shared/`, so an edit rounds exactly as the recording did.
- **Order of checks:** membership, then the expense is known and not removed, then
  description, amount, payer, the split (present, members, not empty, no duplicates,
  then the mode's own rule), the date — Record expense's order — and last, whether
  anything changed. A non-member gets `group not found` whatever else is wrong
  (scenario 19).
- **"Changed" is the inputs:** description (trimmed), amount, payer, split (in
  member-added order) and date, against the expense as it stands — the recording
  with every edit since folded in (scenarios 12–14). The splits follow from these,
  so are not compared. An edit that changes nothing is answered as saved, not
  rejected: a double tap, or a save with nothing touched, is not the user's mistake.
- **Splits are recomputed on every edit,** from the split as entered, payer first
  for leftovers (scenario 5). They are carried in the event whether or not they
  moved: an event is a whole fact, and a fold never has to merge (spec §6).
- **An exact split cannot be re-split** (spec §7): the form submits the amounts it
  shows, and they must add up to the new total (scenario 33): editing only the
  amount of an exact expense is rejected, and the running total (below) shows why.
- **No old values in the event** (spec §11). The activity feed, when built, diffs
  the edit against the expense it holds.
- **Edit is not remove + record:** the expense keeps its id, and its place in the
  history (below).
- **Over HTTP:** a non-member, a missing group, a malformed group id, a malformed
  expense id, an unknown expense and a removed one are the same 404. Saved, or
  nothing to save: back to the group page. Rejected: the form again, with what was
  entered and why, 200. A conflicting save answers 409.

## The screen

- **Reached from the expense's sheet** (View expense): an **Edit** button beside
  **Remove**, an address, not a dependency on this slice.
- **The form is Add expense's:** What for, Amount, Paid by, Split (the mode),
  Shared between, Date; the same names, the three modes and the running total for
  exact (slice-06, "The split modes on the form"), copied, not shared. It differs in being filled in from the
  expense as it stands — its mode selected, its members checked, its shares or amounts
  in; the other modes' fields start from shares `1` and the recorded amounts, so an
  equal expense can become an exact one from what each person owes now — in the title (**Edit expense**), the button (**Save
  changes**), and in having no id field: the expense is named by the address, so
  there is no id to forge or replace.
- **The amount is shown as typed would be,** in the currency's units with its
  decimals and no thousands separator (`1200.50`), so that `Money.TryParse` reads
  it back unchanged. `Money.Format` adds separators, so `Money.Plain`, beside
  `TryParse`, returns the plain form.
- **Payer and participants** are the expense's, each slot in member-added order, as
  Add expense lists them, "(you)" marking the signed-in user's own.
- **A Back link** to the group, named by it ("← Lisbon trip").
- **Rejected:** the page again with what was typed, the reason under the form
  (`role="alert"`), 200. A non-member and a missing or removed expense get the one
  not-found page (404), on the form and the submit alike.
- **Antiforgery** on the submit (`[ValidateAntiforgery]`, spec §3).

## Notes on the screen

- **The screen folds this slice's own `State`:** the group's name and currency, each
  slot's name, which slot each user holds, and each expense as it stands — recorded,
  then edited — with the ids removed. The `GET` folds it live, and the `POST` decides
  on the same `State`.
- **Folds,** besides this slice's: View group replaces the expense in its place in
  the history, keeping where it was recorded (its position among entries paid the
  same day does not change); View balances and View settlement plan take the old
  expense's splits back and book the new ones, and the settle-up pair scores follow
  (the same arithmetic as `ExpenseRemoved` followed by `ExpenseRecorded`). Remove
  expense's confirm page shows the expense as last edited. An edit for an expense a fold does not hold (removed, or
  never recorded) changes nothing there; deciding never produces one.
- **Last write wins** is no code: nothing in the form says which version it was
  opened on, so deciding compares with the expense as it stands (scenarios 11, 12).

## Read by

View group (the history row and your standing), View balances and View settlement
plan (balances, and the shared-history score), Remove expense (its confirm page).

## Deferred to the slices that introduce the events

- **Archived group** rejects the command — with `GroupArchived`.
- **A removed member** cannot become payer or participant; an existing expense that
  has one can still be edited if they are taken out — with `MemberRemoved`.
- **The activity feed's wording** ("Bob changed Dinner: £140 → £120") — with
  `ActivityFeed`.
