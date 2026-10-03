# Slice 9 — View settlement plan

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Settle up — the plan, each line with "Paid" (Record settlement) |
| Read model | `SettlementPlanReadModel` — **computed per request** from a live fold of the group stream; never stored, never an event (spec §10, §11) |
| Query | `ViewSettlementPlan(userId)` — the signed-in user; the group from the route selects the stream |
| Code | `src/ShareExpenses/Slices/ViewSettlementPlan/` |
| Endpoint | `GET /api/groups/{group}/settlement-plan` — sign-in required |

Who should pay whom to clear every balance, in as few transfers as the procedure
finds, preferring people who have actually shared expenses (spec §10). A plan that
shifts between viewing and paying costs nothing: the payment is still valid, and
the balances absorb it.

## Specifications — the slice

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a query field (spec §13). Transfers are written `Bob → Alice 3000`, in the
order returned. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
```

```
1 - nothing owed, nothing to do
    WHEN   ViewSettlementPlan(alice)
    THEN   { GBP, you: m1, transfers: [] }

2 - everyone pays back the payer
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                                   {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
    WHEN   ViewSettlementPlan(alice)
    THEN   transfers: [Bob → Alice 3000, Carol → Alice 3000]

3 - a settlement is taken into account
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", …as in 2…)
           AND SettlementRecorded(s1, m2, m1, 3000, 2026-10-02, bob)
    WHEN   ViewSettlementPlan(alice)
    THEN   transfers: [Carol → Alice 3000]

4 - a partial settlement leaves the rest
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", …as in 2…)
           AND SettlementRecorded(s1, m2, m1, 1000, 2026-10-02, bob)
    WHEN   ViewSettlementPlan(alice)
    THEN   transfers: [Bob → Alice 2000, Carol → Alice 3000]

5 - an overpayment is paid back too
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", …as in 2…)
           AND SettlementRecorded(s1, m2, m1, 5000, 2026-10-02, bob)
    WHEN   ViewSettlementPlan(alice)
    THEN   transfers: [Carol → Alice 1000, Carol → Bob 2000]

6 - among equal amounts, people who shared expenses settle with each other
    GIVEN  ... AND MemberAdded(m4, "Dave", alice)
           AND ExpenseRecorded(e1, "Taxi", 3000, m2, equal [m3], {m3: 3000}, …)
           AND ExpenseRecorded(e2, "Museum", 3000, m1, equal [m4], {m4: 3000}, …)
    WHEN   ViewSettlementPlan(alice)
    THEN   transfers: [Carol → Bob 3000, Dave → Alice 3000]
           (by member-added order alone it would be Carol → Alice, Dave → Bob)

7 - the group must exist
    GIVEN  (empty stream)
    WHEN   ViewSettlementPlan(alice)
    THEN   not found

8 - only members may view
    WHEN   ViewSettlementPlan(mallory)
    THEN   not found
```

## Specifications — the procedure

The procedure is a pure function of the balances and the shared-history scores
(spec §10), tested on its own. Members are `A, B, C, D, E` in member-added order;
balances are written `A +6`, scores `score(A, D) = 1` (any pair not listed is 0).

```
P1 - zero balances drop out
    GIVEN  A 0, B 0
    THEN   []

P2 - one debtor pays every creditor
    GIVEN  A +2, B +3, C -5
    THEN   [C → A 2, C → B 3]

P3 - exact matches are taken first
    GIVEN  A +3, B +5, C -5, D -3
    THEN   [C → B 5, D → A 3]

P4 - several exact matches: the higher score first
    GIVEN  A +3, B +3, C -3, D -3, score(B, C) = 1, score(A, D) = 1
    THEN   [C → B 3, D → A 3]

P5 - then the largest creditor with the largest debtor
    GIVEN  A +6, B +4, C -5, D -5
    THEN   [C → A 5, D → A 1, D → B 4]

P6 - equal magnitudes: the higher score wins the pairing
    GIVEN  A +6, B +4, C -5, D -5, score(A, D) = 1
    THEN   [C → A 1, C → B 4, D → A 5]

P7 - equal magnitudes and scores: member-added order
    (P5: C before D, so C pays A first)
```

And over many random balance sets, always:

- after the transfers, every balance is exactly zero;
- at most `n − 1` transfers, for `n` members with a non-zero balance;
- every transfer is positive, from a debtor to a creditor; nobody both pays and
  receives;
- every pair the exact-match pass could take, and does not, is blocked by a pair it
  took — it is maximal;
- the same balances and scores always give the same plan, whatever order they are
  listed in.

## Notes

- **The procedure, pinned** (spec §10, tie rules added here):
  1. Drop members with a zero balance.
  2. **Exact matches.** Every debtor/creditor pair with equal magnitudes is a
     candidate. Take candidates by score (highest first), then debtor, then
     creditor in member-added order, skipping any whose debtor or creditor is
     already matched. Each emits one transfer and clears both.
  3. **Greedy.** Repeat until nobody owes: of the creditors with the largest
     credit and the debtors with the largest debt, pair the two with the highest
     score — ties by creditor, then debtor, in member-added order — and transfer
     the smaller magnitude.
  4. **Order the result** by payer, then recipient, in member-added order: a
     reload never reshuffles it, and each payer's lines sit together.
- **Shared-history score** for a pair: the number of expenses in which both appear,
  as payer or as a member of the split (even with a zero exact amount). Expenses
  only: a settlement is not shared spending (spec §7). Later, removed expenses stop
  counting.
- **Honest bounds** (spec §10): greedy is a heuristic. At most `n − 1` transfers,
  every exact match taken; not a proven minimum.
- **Live, not stored** (spec §11): folded from the one group stream per request,
  like View invites. Slices share only events, so this slice folds its own balances
  rather than read the stored ledger; a test checks the two agree.
- **Each transfer carries both names** as well as both member ids, so the screen
  needs nothing else. `you` lets the screen put the caller's own lines first.
- **Amounts in minor units,** with the group's currency code.
- **Members only:** a non-member, a missing group and a malformed group id are the
  same 404 (spec §4).

## Deferred to the slices that introduce the events

- **Removed expenses and settlements** stop counting — with `ExpenseRemoved`,
  `SettlementRemoved`.
- **Corrected expenses** — with the correction events.
- **Renamed members** show their new names — with `MemberRenamed`.
- **Removed members** — with `MemberRemoved`: a member is removed only at zero
  balance (spec §8), so never in a plan.
