# Slice 7 — View group

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Group — the group page: what has been spent and paid, chat style, and where you stand |
| Read model | `GroupActivityReadModel`, from the **stored, inline** `GroupActivity` projection |
| Query | `ViewGroup(userId)` — the signed-in user; the group from the route selects the stream |
| Code | `src/SplitIt/Slices/ViewGroup/` |
| Endpoint | `GET /groups/{group}` — the screen; sign-in required |

The group as its members use it: the money that has moved — expenses and
settlements — as a chat-style list, your own standing in one line, and the actions:
add an expense, settle up, see everyone's balances. Where Join and (later) creating
a group land.

The **stored** read model of the group (spec §11): projected *inline* — in the same
transaction that appends the events — so an expense appears, and your standing
moves, the moment it is saved. No window where you enter £120 and nothing changes.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a query field (spec §13).

Amounts are in minor units (pence). History entries are written `expense e1 …` or
`settlement s1 …`, newest first. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
  AND  MemberAdded(m3, "Carol", alice)
```

```
1 - a new group: nothing yet, and you are settled up
    WHEN   ViewGroup(alice)
    THEN   { g1, "Lisbon trip", GBP, you: m1, balance: 0, history: [] }

2 - an expense joins the history, with who paid
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, equal [m1, m2, m3],
                                   {m1: 3000, m2: 3000, m3: 3000}, 2026-10-01, alice)
    WHEN   ViewGroup(alice)
    THEN   history: [expense e1 "Dinner" 9000 paid by m1 "Alice" on 2026-10-01,
                     equal [m1, m2, m3], {m1: 3000, m2: 3000, m3: 3000}]

3 - your standing: owed, after paying for others
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
    WHEN   ViewGroup(alice)
    THEN   balance: +6000

4 - and owing, after others paid for you
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
    WHEN   ViewGroup(bob)
    THEN   you: m2, balance: -3000

5 - a settlement joins the history, and moves your standing
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
           AND SettlementRecorded(s1, m2, m1, 3000, 2026-10-02, bob)
    WHEN   ViewGroup(bob)
    THEN   balance: 0
           history: [settlement s1 m2 "Bob" → m1 "Alice" 3000 on 2026-10-02, expense e1 "Dinner"]

6 - history newest first: by date, then most recently recorded
    GIVEN  ... AND ExpenseRecorded(e1, "Flights", …, 2026-06-15, …)
           AND ExpenseRecorded(e2, "Dinner",  …, 2026-10-01, …)
           AND SettlementRecorded(s1, m2, m1, 3000, 2026-10-01, bob)
           AND ExpenseRecorded(e3, "Coffee",  …, 2026-10-01, …)
    WHEN   ViewGroup(alice)
    THEN   history: [expense e3 "Coffee", settlement s1, expense e2 "Dinner", expense e1 "Flights"]

7 - the group must exist
    GIVEN  (empty stream)
    WHEN   ViewGroup(alice)
    THEN   not found

8 - only members may view
    WHEN   ViewGroup(mallory)
    THEN   not found

9 - an unclaimed placeholder confers no access
    WHEN   ViewGroup(carol)
    THEN   not found

10 - a renamed group shows its current name
    GIVEN  ... AND GroupRenamed("Porto trip", bob)
    WHEN   ViewGroup(alice)
    THEN   { g1, "Porto trip", … }

11 - an archived group is shown as archived, with its history and standing as before
    GIVEN  ... AND ExpenseRecorded(e1, "Dinner", 9000, m1, …, {m1: 3000, m2: 3000, m3: 3000}, …)
           AND GroupArchived(bob)
    WHEN   ViewGroup(alice)
    THEN   { g1, "Lisbon trip", GBP, you: m1, balance: +6000, archived: true,
             history: [expense e1 "Dinner" …] }

12 - a group is not archived until it is
    WHEN   ViewGroup(alice)
    THEN   { …, archived: false }
```

## The screen

- **Your standing, beside Settle up:** "You owe £70.00", "You are owed £45.00", or
  "You're settled up" — and **Settle up** always, to `/groups/{group}/settle-up`: someone owed can see who will pay them, and record it when they do.
- **Balances** opens `/groups/{group}/balances` — everyone's standing (View balances).
- **The history, chat style:** a scrollable list, newest at the bottom and scrolled
  into view on arrival. What *you paid* sits on the left — an expense whose payer is
  your slot, a settlement you paid; everyone else's on the right, with who paid.
  Newest first in the markup, shown bottom-up by CSS (`column-reverse`), so it needs
  no script.
- **Add expense** (to `/groups/{group}/expenses/new` — not yet built), and a link
  back home.
- **Amounts** are shown with the currency's decimals and symbol (`Web/Money`).
- **Not a member, no such group, malformed id:** the same "not found" page, 404.

## Archived groups

An archived group (slice-15) stays readable and is marked, not hidden:

- **A banner** under the top bar — "This group is archived." — in place of the actions.
- **No Add expense button, no Settle up button,** and **no `⋯` menu**: nothing on the page
  leads to a form that would be rejected. The cards still open their sheets (View expense),
  without Edit and Remove.
- **Your standing** and **Balances** stay, as before.

The `⋯` menu of a group that is not archived offers **Add member** (slice-02),
**Rename group** (slice-14), **Default split** (slice-16) and **Archive group** (slice-15):
addresses, not dependencies on those slices.

The read model gains `archived`, from `GroupArchived`; the stored `GroupActivity`
must be rebuilt (`just rebuild-projections`).

## Notes

- **Stored, inline** (spec §11). The slice's state is a Marten snapshot projected
  inline, saved as the `group_activity` document in the same transaction as the
  events. The endpoint loads it; a pure `Reader` turns it into the read model.
- **History is the money, in one list:** each entry marked by `kind` — `expense`
  (with its split as entered and the amounts it produced, so a later edit form can
  show the split as it was entered) or `settlement` (from, to, amount) — and named:
  who paid, who was paid. Ordered by the day the money moved (`paidOn`), newest
  first, then most recently recorded. Not the activity log: who did what, including
  membership changes and corrections, is a separate read model (spec §11,
  `ActivityFeed`).
- **Your standing is spec §9's balance, for you only.** Everyone's is View
  balances'. Both slices fold balances from the events, each its own (slices share
  only events); a test checks they agree.
- **Members only:** a non-member, a missing group and a malformed group id are the
  same 404 — the group's existence is not disclosed (spec §4).

- **A removed expense leaves the history and stops counting** (`ExpenseRemoved`,
  Remove expense): its row goes, and the balances of everyone in it move back, as if
  it had never been recorded. Entries keep their order when something is recorded
  after a removal.
- **An edited expense is changed where it stands** (`ExpenseEdited`, Edit expense):
  its row shows the new description, amount, payer, date, split and amounts, in the
  place it was recorded — a later edit does not move it to the end of its day — and
  the balances of everyone in it move to the new amounts.
- **An expense's card is a link to its sheet** (View expense): the card says what,
  how much, who paid and when, and nothing more; Edit and Remove are on the sheet,
  where the split is too. The link is an address, and an empty `#sheet` container at
  the end of the page is where the sheet appears: the address and the id are all this
  slice knows of it.

## Deferred to the slices that introduce the events

- **A removed settlement** leaves the history and stops counting — with
  `SettlementRemoved`.
- **Renamed members** show their new names — with `MemberRenamed`.
- **A released claim** — the user loses access — with `MemberClaimReleased`.
