# Slice 11 — `RemoveExpense`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Remove expense — a confirm page for one expense, reached from the expense's sheet (View expense) |
| Command | `RemoveExpense(expenseId, by)` |
| Events | `ExpenseRemoved` |
| Code | `src/SplitIt/Slices/RemoveExpense/` |
| Endpoints | `GET /groups/{group}/expenses/{expense}/remove` — the confirm page; `POST` to the same path — its submit; sign-in required |

Removes a mistaken expense. Nothing is deleted (spec §7): `ExpenseRecorded` stays in
the stream, and `ExpenseRemoved` is a second fact beside it. Every read model that
folds the expense then folds its removal, and the expense stops counting: it leaves
the group's history, and the balances and the settle-up plan are as if it had never
been recorded. A wrong amount or split is fixed by Edit expense, not by removing and
recording again (spec §11).

Any member may remove any expense — the payer, someone in its split, or neither
(spec §5: flat trust, backed by the log).

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13).

`e1` is the expense removed. Every command is issued by the user in the scenario.
Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
  AND  ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                       {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
```

```
1 - removes an expense
    WHEN   RemoveExpense(e1, alice)
    THEN   ExpenseRemoved(e1, alice)

2 - any member may remove it, in its split or not, the payer or not
    GIVEN  ... AND MemberAdded(m4, "Dave", alice) AND MemberClaimed(m4, dave)
    WHEN   RemoveExpense(e1, dave)
    THEN   ExpenseRemoved(e1, dave)

3 - an expense paid by a placeholder can be removed too
    GIVEN  ... AND ExpenseRecorded(e2, "Taxi", 3000, m3, equal [m1, m3],
                                   {m1: 1500, m3: 1500}, 2026-10-01, alice)
    WHEN   RemoveExpense(e2, bob)
    THEN   ExpenseRemoved(e2, bob)

4 - an expense that has been settled against can be removed: balances replay
    GIVEN  ... AND SettlementRecorded(s1, m2, m1, 3000, 2026-10-02, bob)
    WHEN   RemoveExpense(e1, alice)
    THEN   ExpenseRemoved(e1, alice)

5 - the group must exist
    GIVEN  (empty stream)
    WHEN   RemoveExpense(e1, alice)
    THEN   rejected (not found) - group not found

6 - only members of the group may remove
    WHEN   RemoveExpense(e1, mallory)
    THEN   rejected (not found) - group not found

7 - the expense must be in the group
    WHEN   RemoveExpense(e9, alice)
    THEN   rejected (not found) - expense not found

8 - an expense already removed is not removed twice
    GIVEN  ... AND ExpenseRemoved(e1, bob)
    WHEN   RemoveExpense(e1, alice)
    THEN   rejected (already removed) - expense already removed

9 - an archived group removes no expense
    GIVEN  ... AND GroupArchived(alice)
    WHEN   RemoveExpense(e1, alice)
    THEN   rejected - group is archived
```

## Notes

- **Order of checks:** membership, then archived, then the expense is known, then not
  already removed. A non-member gets `group not found` whatever the expense id is.
- **Over HTTP:** a non-member, a missing group, a malformed group id, a malformed
  expense id and an expense not in the group are the same 404. Removed, or already
  removed (a double tap, or two phones): back to the group page, one event. A
  conflicting save answers 409; the client retries.
- **The confirm page** shows the expense as it stands (description, amount, who paid,
  the day — after any `ExpenseEdited`),
  says balances will update, and has one button. A plain form with an antiforgery
  token; no script. For an expense already removed, the page is the 404: the group
  page no longer lists it.
- **No reason field**, as for `SettlementRemoved` later: who removed what is in the
  event (`by`), and the activity feed will show it (spec §11).
- **Not undone by re-recording the same form:** the expense's id stays recorded
  (Record expense), so an Add expense form submitted again after the removal is
  still "already recorded" and does not bring the expense back.
- **An archived group** rejects the command (scenario 9). Its confirm page, posted, answers
  with the group page, where the archived banner says why.

## Read by

View group (the expense leaves the history, and your standing moves), View balances
and View settlement plan (balances, and the shared-history score, as if never
recorded).

## Deferred to the slices that introduce the events

- **A removed member's expenses** — with `MemberRemoved`.
