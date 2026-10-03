# Slice 8 — `RecordSettlement`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Settle up — "Paid" on a line of the plan, or any payment entered by hand |
| Command | `RecordSettlement(settlementId, fromMemberId, toMemberId, amountMinor, paidOn, now, by)` |
| Events | `SettlementRecorded` |
| Code | `src/ShareExpenses/Slices/RecordSettlement/` |
| Endpoint | `POST /api/groups/{group}/settlements` — 201 with `settlementId` |

Records that one member paid another, outside the app (spec §1: the app produces
instructions; people use their own bank). A settlement is a transaction in the
ledger — it moves balances exactly as an expense paid by `from` and shared by `to`
alone would — but it is its own fact, not an expense (spec §7).

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13).

Amounts are in minor units (pence). `s1` is the new settlement's id, chosen by the
endpoint. Every command is issued at `now = 2026-10-02 12:00 UTC`, and
`RecordSettlement(…)` omits `s1`, `now`, and the date when it is `paidOn =
2026-10-01`. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
```

```
1 - records a settlement
    WHEN   RecordSettlement(m2, m1, 3000, bob)
    THEN   SettlementRecorded(s1, m2, m1, 3000, 2026-10-01, bob)

2 - any member may record it, party to it or not
    WHEN   RecordSettlement(m2, m1, 3000, alice)
    THEN   SettlementRecorded(s1, m2, m1, 3000, 2026-10-01, alice)

3 - a placeholder can pay, and be paid
    WHEN   RecordSettlement(m3, m1, 3000, bob)
    THEN   SettlementRecorded(s1, m3, m1, 3000, 2026-10-01, bob)

4 - nothing need be owed: balances absorb any payment
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                                   {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
    WHEN   RecordSettlement(m1, m2, 5000, alice)
    THEN   SettlementRecorded(s1, m1, m2, 5000, 2026-10-01, alice)

5 - the group must exist
    GIVEN  (empty stream)
    WHEN   RecordSettlement(m2, m1, 3000, bob)
    THEN   rejected (not found) - group not found

6 - only members of the group may record
    WHEN   RecordSettlement(m2, m1, 3000, mallory)
    THEN   rejected (not found) - group not found

7 - the payer must be a member slot of the group
    WHEN   RecordSettlement(m9, m1, 3000, bob)
    THEN   rejected - payer is not a member of the group

8 - so must the recipient
    WHEN   RecordSettlement(m2, m9, 3000, bob)
    THEN   rejected - recipient is not a member of the group

9 - nobody pays themselves
    WHEN   RecordSettlement(m2, m2, 3000, bob)
    THEN   rejected - a member cannot pay themselves

10 - the amount must be positive
    WHEN   RecordSettlement(m2, m1, 0, bob)
    THEN   rejected - amount must be positive

11 - the amount has a ceiling
    WHEN   RecordSettlement(m2, m1, 1000000000001, bob)
    THEN   rejected - amount is too large

12 - the date may be tomorrow, for time zones
    WHEN   RecordSettlement(m2, m1, 3000, paidOn 2026-10-03, bob)
    THEN   SettlementRecorded(s1, m2, m1, 3000, 2026-10-03, bob)

13 - but no later
    WHEN   RecordSettlement(m2, m1, 3000, paidOn 2026-10-04, bob)
    THEN   rejected - date cannot be in the future

14 - a date is required
    WHEN   RecordSettlement(m2, m1, 3000, paidOn none, bob)
    THEN   rejected - date is required
```

## Notes

- **Any member may record a settlement** — a party to it or not (spec §7). Trust is
  flat (§5), and a placeholder cannot sign in: if Carol, a placeholder, pays Alice,
  someone else records it. The activity feed shows who did.
- **No confirmation step** (spec §7): the event is appended at once, and the
  balance clears. A mistaken one is removed, not corrected (spec §11) — with
  `SettlementRemoved`, a later slice.
- **Not a rule: that `from` owes `to`.** Deciding folds no balances. A partial
  payment leaves a balance; an overpayment reverses one; either is a valid fact
  the balances absorb (spec §7). Scenario 4 records £50 from Alice, who is owed.
- **Same money rules as an expense:** a positive integer in minor units, at most
  10¹² (spec §6).
- **The date** is the day the money moved, a domain fact (`DateOnly`), recorded
  often days later; when it was recorded is Marten metadata (spec §11). Same
  window as an expense: up to a day ahead of UTC, any earlier date.
- **Order of checks:** membership, then payer, recipient, not to themselves,
  amount, date. A non-member gets `group not found` whatever else is wrong.
- **From the plan:** "Paid" on a plan line sends exactly its from, to and amount.
  The plan may have shifted since it was shown; that costs nothing — the payment
  is still valid, and the balances absorb it (spec §11).

## Changes to View balances

The ledger folds `SettlementRecorded` — moving `from`'s balance up and `to`'s down
by the amount — and its expenses list becomes a **history** of both kinds. See
`slice-07-view-balances.md`; the stored ledgers are rebuilt from the events.

## Deferred to the slices that introduce the events

- **Archived group** rejects the command — with `GroupArchived`.
- **A removed member** cannot pay or be paid — with `MemberRemoved`.

## Concurrency

As AddMember: a conflicting save answers **409** and the client retries. Two
phones recording the same payment both succeed: they are two settlements, and
someone removes one.
