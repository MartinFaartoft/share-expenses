# Shared Expenses — Spec

A small app for splitting costs inside a group (a trip, a flatshare, a dinner series),
and for working out who should transfer what to whom to clear the balances.

Built as a deliberate vehicle for learning **event modeling** and **event sourcing**,
using C# with Marten on PostgreSQL.

Status: design in progress. Decisions below are settled unless marked **OPEN**.

---

## 1. Scope

**In scope (v1)**
- Create a group; invite people to it by link/email.
- Record expenses: who paid, how much, who it's split between, how it's split.
- See per-person balances at any time.
- Get a concrete settle-up plan: an ordered list of "X transfers N to Y".
- Record that a transfer actually happened.
- A group activity history: who changed what, when.

**Explicit non-goals (v1)**
- Moving real money. The app produces *instructions*; humans use their own bank.
- Multi-currency within a group, and FX conversion of any kind.
- Receipt photos / OCR / itemised line-item splitting.
- Categories, budgets, charts, spend analytics.
- Recurring or scheduled expenses.
- Push notifications, native apps, offline-first operation.
- Cross-group netting. Every group settles independently.
- Merging duplicate member slots for the same person (see §4).

---

## 2. Learning goals

This project is as much about the technique as the product. Explicit goals:

- Practise **event modeling** as a design activity — building the timeline of
  events, the commands that cause them, and the read models that serve the
  screens, *before* writing code.
- Learn **event sourcing** mechanics in Marten: streams, aggregates, projection
  lifecycles, optimistic concurrency, event schema evolution.
- Develop judgement about **where event sourcing does and does not belong** in
  one codebase.

Consequence: where a choice is close, the more instructive option wins over the
more expedient one. This is an explicit exception to the usual preference for
the simplest thing that works.

---

## 3. Architecture

- **Backend:** C# / .NET, Marten on PostgreSQL as the event store.
- **Datastore:** PostgreSQL. Marten stores events and projected documents as
  `jsonb` in the same database, so read models and the event log commit together.
- **Hosting:** a single self-managed VPS with its own domain, running both the
  app and PostgreSQL on the same host.
- **Frontend:** **OPEN — deferred.** The API and event model come first; the
  client is chosen later. Assume a mobile-web client for UX purposes: the primary
  target is a phone screen, one-handed, and the share-link join flow must not
  require an install.

### Hosting consequences

- TLS termination through a reverse proxy (Caddy or nginx) with Let's Encrypt.
- **Backups are entirely our responsibility.** The event stream *is* the ledger —
  lose it and every balance is gone, with no third party holding a copy.
  Scheduled offsite `pg_dump`, and a restore that has actually been tested rather
  than assumed.
- Restarts are manual and infrequent, which makes the persisted data-protection
  key ring (§4) load-bearing rather than theoretical: without it, every restart
  logs everyone out and invalidates outstanding sign-in links.
- No managed-Postgres safety net — connection limits, vacuum behaviour and disk
  headroom are ours to watch. At this data volume none of it should bite, but
  nobody else is watching either.

### What is event-sourced, and what is not

**Event-sourced (the domain):** groups, members, expenses, settlements.
This is the ledger. History *is* the product here — "why is my balance this
number" is answered by replaying what happened.

**Plain documents, not event-sourced (supporting state):** users, sessions,
login tokens, invite-token lookups.

Rationale: these have no interesting history, no invariants worth auditing, and
high write churn. Event-sourcing a session table produces a firehose of noise
that nobody will ever query. Keeping the boundary explicit is itself one of the
learning goals — the instinct to event-source everything is the main way this
pattern goes wrong.

---

## 4. Identity and access

**Decision: magic email link only — no passwords stored, no external identity
provider.** Built on ASP.NET Core Identity as a local user store.

Rationale for revisiting the "rolling your own" concern: most of this flow is
framework code. Identity supplies token generation, expiry, the user store, email
normalisation, cookie issuance and renewal, and security-stamp-based global
sign-out; ASP.NET supplies rate limiting. What is genuinely owned is single-use
bookkeeping and the choice of mail relay. Rejected alternatives: OAuth via Google
or similar (no appetite for a third-party provider), and local passwords (storing
hashes makes every database backup a credential store, and accounts used twice a
year mean forgotten passwords by the second trip — which requires a reset flow,
so email returns anyway, on top of the passwords).

### Mechanism

- Sign-in request → `UserManager.GenerateUserTokenAsync` with
  `DataProtectorTokenProvider` and a custom purpose, `"passwordless-login"`.
  These tokens are stateless and self-validating, so **there is no token table**.
- Token lifespan: 15 minutes.
- Verify with `VerifyUserTokenAsync`, then `SignInManager.SignInAsync`.
- Sessions: sliding 30-day cookie, `HttpOnly`, `Secure`, `SameSite=Lax`.
  Re-authentication is one tap on an email, so a modest window is cheap.

### Every sign-in email carries a link *and* a code

**Decision:** each email contains a tappable link and a six-digit code (Identity's
`EmailTokenProvider`).

Rationale: the cross-device case is common — email on the phone, browser open on
the laptop. The code path costs little because the provider already exists, and a
typed code cannot be consumed by a link prefetcher. Two verification paths, both
framework-supplied.

### Single-use enforcement — the one genuinely owned piece

Identity's login tokens are **not** single-use by default. A password-reset token
is effectively single-use only because changing the password bumps the security
stamp; a login changes nothing, so the token stays valid for its full lifespan.

**Decision:** track consumption explicitly — store `SHA-256(token)` with an
expiry, reject any token already present, prune expired rows.

Explicitly rejected: forcing single-use via `UpdateSecurityStampAsync`. It does
work, but it signs the user out of every other device, which is the wrong
behaviour for a login.

### Link prefetching

Mail clients and corporate security scanners fetch URLs found in email, which
would burn a single-use token before the human ever clicks it.

**Decision:** the link lands on a page with a "Sign me in" button that POSTs.
A prefetch only ever hits the GET, and consumes nothing.

### Data protection keys must be persisted

DataProtector tokens *and* auth cookies are both validated against the
data-protection key ring. The default in a container is in-memory, so **every
restart would invalidate every outstanding link and every active session.**

**Decision:** persist keys to Postgres via `PersistKeysToDbContext`.

Recorded as a decision rather than an implementation note because getting it
wrong produces a bug that presents as random, intermittent logouts — the kind
that gets misdiagnosed for weeks.

### Rate limiting and user enumeration

- ASP.NET's built-in rate limiter on the request-link endpoint, keyed per email
  address and per IP.
- **Decision:** requesting a link for an unknown address returns exactly the same
  response as for a known one, and sends nothing. No user enumeration.
- Consequence: nobody self-registers. New people arrive via an invite link
  carrying the `MemberInvited` email address, and sign in from there.

### Persistence: EF Core, firewalled

Identity's default store is EF Core, which puts two persistence stacks in one
codebase alongside Marten.

**Decision: accept EF Core, strictly confined to Identity** — its own
`DbContext`, its own `identity` schema, never joined to domain data. Marten owns
the event store and projections; EF owns users, logins and the key ring, and
nothing else.

Rationale: §3 already fences identity off as non-event-sourced, so the boundary
exists and this respects it. The alternative — implementing `IUserStore` and its
siblings over Marten documents — eliminates EF, but means writing and testing
auth plumbing, which §2 puts explicitly off the learning path. Revisit only if
the two stacks genuinely chafe.

### Mail relay — deferred

**Decision: deferred.** Development and early verification use a
`LogEmailSender` implementation of `IEmailSender` that writes the sign-in link
and code straight to the application log. Entirely adequate while the only users
are the author and deliberate testers.

**Safety gate:** the log sender must refuse to start when the environment is
Production. Otherwise it silently locks out every real user, and the failure
presents as "nobody can log in" with no error anywhere in the system — the log
sender is working perfectly, it just isn't sending email to anyone. Choosing a
relay is a release blocker for the first real group, and nothing before that.

Shortlist for when that time comes: Postmark (best-in-class transactional
deliverability), Resend (best developer experience), Amazon SES (cheapest, most
setup). Free-tier terms shift; verify before committing.

Running our own outbound SMTP is explicitly rejected: cold IP reputation and
unaligned DMARC put login links in spam folders, and a login link in spam is a
total lockout that the user blames on the app. SPF, DKIM and DMARC must be
configured on the sending domain whichever provider is eventually chosen.

### Consequences accepted

- Email delivery is a hard dependency. If it breaks, nobody can log in.
- Deliverability remains a real user-facing failure mode — mitigated by a
  reputable relay, never eliminated.
- No offline or LAN-only login.
- Browsers evict data for sites unused for months, so a returning user on iOS may
  find their session gone between trips. This is acceptable precisely because
  re-authentication is self-service: request a link, tap it.

### Members vs users

`member` (in the group's event stream) is deliberately distinct from `user` (a
login identity). Expenses reference `member_id` and never `user_id`, which is
what makes everything below cheap.

**Decision: placeholder members are allowed and claimable.**
Add someone by name alone and record expenses against them immediately. When they
later sign in through their invite, they claim that member slot and the existing
history stays intact.

Rationale: without this the app fails at the exact moment it is meant to be
useful — standing in a restaurant, wanting to enter the bill now, with one person
who will not click their invite until Thursday.

#### Claiming is driven by the invite, not by a picker

**Decision: the invite binds the slot.** `MemberInvited(memberId, email)` already
ties an email address to one specific member slot, so the invite token identifies
which slot is being claimed. There is no "who are you?" list to mis-tap.

Consequences:
- Claiming is not a free choice; it is the automatic result of signing in through
  an invite.
- A placeholder added by name but never invited is not claimable until someone
  invites it. This is a feature — every claim traces back to a deliberate act by
  an existing member.

**Decision: the token is the capability — the signed-in email need not match the
invited one.** Requiring a match would block a forwarded link, but it would also
break the very common "you invited my old address" case. Claims are announced in
the activity feed instead, consistent with §5: accountability by visibility
rather than by enforcement.

#### Reversing a claim

**Decision: one event — `MemberClaimReleased(memberId, userId, releasedBy)`.**
The slot returns to placeholder state and becomes claimable again. Any member may
release a claim, and so may the claiming user — "this isn't me" is the most likely
way the mistake gets noticed. Reassignment, if ever needed, is a release followed
by a fresh claim.

This is cheap precisely because expenses reference `member_id`: releasing a claim
touches no expense, no split and no balance. It is pure identity re-binding.

Rejected alternatives:
- **Two-phase `MemberClaimProposed` → `MemberClaimConfirmed`.** The same
  pending-state complexity argued down for settlements in §7, and it does nothing
  about a deliberate wrong claimer, who simply confirms.
- **`MemberRemoved` + `MemberAdded`.** Looks like the obvious answer and is
  actively wrong: §8 forbids removing a member with a non-zero balance, and even
  without that rule the replacement carries a different `member_id`, orphaning
  every historical split. Avoiding exactly this is why `member` and `user` are
  separate concepts.
- **A dedicated `MemberClaimReassigned` atomic move.** Deferred: it buys explicit
  handover intent for a scenario that may never arise, and release-then-claim
  already covers it.

#### Duplicate member slots are out of scope

If someone adds both "Bob" and "Bobby" for the same human and both accumulate
expenses, no amount of claim-releasing fixes it. The splits are genuinely
attributed to two different members, so this is a **merge** — and unlike claim
reversal, it moves money.

**Decision: out of scope for v1.** If it happens, correct the affected expenses by
hand with `ExpenseSplitChanged`, then remove the emptied slot.

Noted for later, because it is the most instructive idea in this area: a
`MemberMergedInto(loserMemberId, winnerMemberId)` event would change how
projections *interpret* past events — attributing the loser's splits to the winner
— without rewriting any of them. That property, changing the meaning of history
without touching history, is what separates event sourcing from a CRUD table with
an audit log beside it. Its real costs: every projection must honour the aliasing,
including the settle-up shared-history score, and merges can chain (A→B, then B→C).

## 5. Permissions

**Decision: flat trust within a group — any member may edit or remove any
expense — backed by a complete activity log.**

Rationale: typos are usually spotted by whoever *wasn't* holding the phone.
Author-only editing leaves a wrong number wrong until its author wakes up. There
is no admin role: for a group of friends, the escalation path is conversation.

This decision is nearly free here: the activity log is a projection over the
event stream that already exists. In a CRUD design it would have been a separate
audit-table mechanism to build and maintain.

---

## 6. Money

**Decision: all amounts are integers in the currency's minor unit** (pence,
cents). Floating point never touches a monetary value — not in an event payload,
not in a projection, not in application code.

**Decision: one currency per group**, chosen at creation and immutable
thereafter. Stored as an ISO 4217 code.

Rationale: multi-currency forces answers to "which rate", "on what date", "who
absorbs the FX spread", and "what happens when the rate moves between expense
and settlement". That is a whole project, not a feature.

### Rounding rule

Splits must sum **exactly** to the expense total — no lost or invented minor units.

- **Equal split:** `base = total / n` (integer division), `remainder = total % n`.
  The remaining `remainder` minor units are distributed one each.
- **Shares split:** each participant gets `floor(total * share_i / total_shares)`,
  then leftover minor units are distributed by largest fractional remainder.
- **Distribution order for leftovers:** the payer first, then remaining
  participants in member-added order.
- **Exact split:** amounts must sum to the total exactly, or the command is
  rejected. No rounding is applied.

Rationale for payer-first: the payer absorbing the stray penny means they are
owed a penny less, which is socially invisible and never worth arguing about.
The rule is fully deterministic — the same input always yields the same split.

### Events carry computed splits, not just inputs

**Decision:** an expense event records *both* the split inputs (mode,
participants, weights) *and* the resulting per-person amounts.

Rationale: this is redundant, and deliberately so. The per-person amounts are the
facts the group agreed to. If the amounts were re-derived from inputs at
projection time, then fixing a rounding bug two years from now would silently
rewrite historical balances. Events are immutable records of what was decided,
not instructions to recompute.

---

## 7. Expenses

**Decision: a single payer per expense.** Bills where several people paid are
recorded as several expenses.

Rationale: multiple payers doubles both the data model and the add-expense form,
to serve a case that decomposes cleanly into separate entries.

**Decision: three split modes.**

| Mode | Input | Use |
|---|---|---|
| `equal` | set of participants | the common case |
| `exact` | amount per participant | someone's steak cost more |
| `shares` | integer weight per participant | a couple counts as 2, singles as 1 |

Percentages are deliberately not a separate mode — they are shares out of 100.

**Decision: a settlement payment is just another transaction in the ledger.**
"Bob paid Alice 50" is an event with a payer, a single beneficiary, and the full
amount.

Rationale: this is the load-bearing simplification of the whole design. Balances
are always derived from the stream. There is no stored balance, no `settled`
flag, and no state machine. Correcting an old expense after a settlement simply
replays, and the residual difference appears as a new balance rather than as
corruption. **Partial settlements need no code at all**: owe 50, record 30,
balance is 20.

**Decision: settlements are recorded by either party, with no confirmation step.**
Whoever gets there first records it; the event is appended immediately and the
balance clears. Any member can remove it if it was a mistake.

Rationale: accepts a window where the app reads "settled" while money is still in
transit. That failure mode is small and socially self-correcting ("mate, nothing
has arrived"). A pending-until-confirmed state is permanent complexity — an extra
state in the model, a nudge mechanism to chase confirmations, and rows that get
stuck forever when someone never bothers.

**Decision: nothing is ever deleted.** Removal is an event. This is not a
"soft delete" flag — the original record and its removal are both facts in the
stream.

---

## 8. Membership rules

- **Joining mid-trip:** splits are explicit snapshots. A member added on day 3
  is *not* retroactively added to day-1 expenses. Anyone may correct an old
  expense to include them if that is genuinely what happened.
  Rationale: retroactive auto-inclusion would silently rewrite what people
  already agreed to.
- **Removing a member:** permitted only at a zero balance; otherwise they must
  settle first. Removed members remain in history forever, because their
  expenses do. A removed member cannot be a payer or participant in new expenses.
- **Archiving a group:** makes it read-only and drops it out of the main list.
  Reversible. Warns if balances are not zero, but does not block.

---

## 9. Balances

For each member, over the group's event stream:

```
balance(m) = Σ amount of transactions where m is the payer
           − Σ m's recorded split amount across all transactions
```

- `balance > 0` — m is owed money (creditor).
- `balance < 0` — m owes money (debtor).
- Balances always sum to exactly zero across the group. This is an invariant
  worth asserting in tests, and a good property to fuzz against replayed streams.

---

## 10. Settle-up algorithm

**Decision: minimise transfer count, break ties toward real shared history.**

Rationale: pure greedy netting minimises transfers but will cheerfully tell Bob
to wire money to Carol when the two never shared a single expense — the most
common complaint about apps in this category. Pairwise-only netting keeps every
transfer explainable but never collapses chains, so `A→B→C` stays two transfers
when it could be one. The tiebreak buys the transfer count of the first with the
social legibility of the second.

### Procedure

1. Compute net balances in minor units; discard members at zero.
2. **Exact-match pass:** find debtor/creditor pairs whose magnitudes are equal
   and emit a transfer for each. Each clears two people with one transfer, which
   is always optimal to take. Where several exact matches are available to the
   same member, prefer the pair with the higher shared-history score.
3. **Greedy pass:** repeatedly pair the largest remaining creditor with the
   largest remaining debtor, transferring `min(credit, debt)`. Where magnitudes
   tie, choose the pairing with the higher shared-history score.
4. Deterministic final tiebreak on member-added order, so identical group state
   always produces an identical plan — reloading never reshuffles it.

**Shared-history score** for a pair: the number of live expenses in which both
members appear, as payer or as participant.

### Honest bounds

Greedy is a heuristic, not a proven optimum — minimum-transfer settlement is
NP-hard in general (it reduces to subset-sum style partitioning). What this
procedure guarantees is at most `n−1` transfers, plus every exact match taken.
Exhaustive search for a provably minimal plan is a non-goal; the difference is
at most a transfer or two at realistic group sizes.

The algorithm is a **pure function of the balances** — no I/O, no framework, no
persistence. It should be the most heavily unit-tested code in the project.

The plan is a query, never stored and never an event (see §11).

---

## 11. Event model

### Stream boundary

**Decision: one stream per group.** The stream id is the group id, and the
`Group` aggregate is rehydrated from it. Members, expenses, corrections and
settlements are all events on that one stream.

Rationale: aggregate boundaries follow **invariants**, not entities. The standard
advice — keep aggregates small — would put each expense in its own stream, but
look at the invariant list below: "a member with a non-zero balance cannot be
removed" needs the member list *and* every expense inside a single consistency
boundary. Split expenses into separate streams and that check becomes eventually
consistent, requiring a process manager to repair violations after they have
already happened. The usual argument for small streams is unbounded growth, and
it barely applies here: group size is capped by human social limits, and a
two-week trip is a few hundred events.

Known limitation: a flatshare running for years will accumulate. The escape hatch
is deliberate and deferred — append a `PeriodClosed` event carrying opening
balances, then archive the stream behind it. Literally closing the books. Not
built in v1; revisit when a real stream actually gets long.

### Commands

```
CreateGroup, RenameGroup, ArchiveGroup, UnarchiveGroup
AddMember, InviteMember, ClaimMember, ReleaseMemberClaim, RenameMember,
  RemoveMember
RecordExpense, RemoveExpense
CorrectExpenseDescription, CorrectExpenseAmount, CorrectExpensePayer,
  ChangeExpenseSplit, CorrectExpenseDate
RecordSettlement, RemoveSettlement
```

### Events

Group and membership:
```
GroupCreated(name, currency, createdBy)
GroupRenamed(name, by)
GroupArchived(by) / GroupUnarchived(by)
MemberAdded(memberId, displayName, by)
MemberInvited(memberId, email, by)
MemberClaimed(memberId, userId)
MemberClaimReleased(memberId, userId, releasedBy)
MemberRenamed(memberId, displayName, by)
MemberRemoved(memberId, by)
```

**Decision: `CreateGroup` emits three events** — `GroupCreated`, `MemberAdded`
and `MemberClaimed` — seating the creator as an already-claimed member.

Rationale: surfaced by writing the given-when-then specs for the slice. A group
whose creator is not a member is a meaningless state — they could not be a payer
or a participant, and the very next action would have to fix it. Emitting all
three reuses the existing vocabulary instead of adding a "member born claimed"
special case, and it means the creator's slot can be released (§4) exactly like
anyone else's.

Consequence: a slice is **not** one command to one event. `AddMember` exists for
adding *other* people, not for seating the creator.

**Decision: events carry no timestamp.** Marten records the append time as stream
metadata, so an `at` field would duplicate it. Contrast `paidOn` on an expense,
which is genuine domain data: the date the expense happened is not the date it was
recorded. Metadata belongs to the store; domain facts belong in the payload.

Note on enforcement: "a group is created exactly once" is guaranteed by Marten's
stream semantics and optimistic concurrency — appending `GroupCreated` at expected
version 0 to a stream that already exists fails — rather than by aggregate
validation.

Transactions:
```
ExpenseRecorded(expenseId, description, amountMinor, payerMemberId, splitMode,
                participants[memberId, weight?], splits[memberId, amountMinor],
                paidOn, by)
ExpenseRemoved(expenseId, by)
SettlementRecorded(settlementId, fromMemberId, toMemberId, amountMinor, by)
SettlementRemoved(settlementId, by)
```

Corrections — **decision: intention-revealing, one event per kind of change**:
```
ExpenseDescriptionCorrected(expenseId, description, by)
ExpenseAmountCorrected(expenseId, amountMinor, splits[], by)
ExpensePayerCorrected(expenseId, payerMemberId, by)
ExpenseSplitChanged(expenseId, splitMode, participants[], splits[], by)
ExpenseDateCorrected(expenseId, paidOn, by)
```

Rationale: the stream records *why* something changed, not merely *that* it did,
which is what makes the activity feed readable — "Bob corrected the amount of
Dinner: £140 → £120" instead of "Bob updated Dinner". Under flat-trust editing
(§5) the quality of that feed is the entire accountability mechanism, so it earns
the extra event types.

Two consequences worth stating explicitly:

- Any correction that changes the amount, the participants or the mode **must
  carry the recomputed per-person splits**, per §6. Splits are recorded facts,
  not derivations.
- Correction events carry only the **new** value, never the old one. A projection
  is a fold that already holds current state when the event arrives, so it can
  render "£140 → £120" without the payload duplicating history. Storing
  before-values is a common reflex worth resisting.

**Settlements have no correction events.** A wrong settlement is removed and
re-recorded. Rationale: it has four fields, and "that transfer never happened" is
the truthful statement anyway.

### Read models and projection lifecycles

| Read model | Lifecycle | Serves |
|---|---|---|
| `GroupLedger` | **inline** | members, live expenses, per-member balances |
| `ActivityFeed` | **inline** | who did what, when |
| `UserGroups` | **async** (multi-stream) | a user's group list |
| `InviteLookup` | plain document | token → group + member slot |

Rationale for inline on the ledger: the core phone interaction is "add the
expense, then immediately look at the balances". Inline projections commit in the
same transaction as the events, making read-your-writes guaranteed rather than
hopeful. Async would open a window where you enter £120 and the balance has not
moved — the most alarming bug this app could plausibly ship.

`UserGroups` is async deliberately, so the project exercises both lifecycles and
the async daemon. Staleness is harmless there: after creating or joining a group
you are redirected straight into it by id, never via the list.

Both are Marten configuration choices and cheap to flip. Flipping one to feel the
difference is a legitimate use of an afternoon.

### The settle-up plan is not a projection

**Decision: the settle-up plan is a pure query, computed on demand, never stored
and never an event.**

Rationale: consistent with settlements-are-just-transactions. A plan that shifts
between viewing and paying costs nothing — the payment is still a valid
transaction, balances absorb it, and at worst one extra transfer shows up later.
Nothing to invalidate, no lifecycle, no superseded state.

Rejected alternative: a `SettlementPlanProposed` event that freezes the plan for
a settle-up round. It models a genuine social ritual ("right, everyone pay up")
and is the more instructive option, but it introduces a plan lifecycle —
proposed / partly paid / superseded / abandoned — to solve a problem that
self-heals. Revisit if a shifting plan actually bites in practice.

### Invariants the Group aggregate enforces

- Expense amount is positive.
- `exact` split amounts sum exactly to the total.
- Share weights are positive integers.
- Payer and all participants are current, non-removed members.
- A member with a non-zero balance cannot be removed.
- Currency is immutable after creation.
- No commands accepted against an archived group.
- A member slot has at most one *current* claim; a released slot may be
  claimed again.
- A user holds at most one member slot per group — otherwise their balance
  is ambiguous.

### Concurrency

Optimistic concurrency on the group stream version. Two people adding an expense
from different phones at the same restaurant table is the expected case, not an
edge case — the loser retries. Marten's `FetchForWriting` is the intended
mechanism.

### Event schema evolution

**Decision: additive-only by default.** Adding an optional field to an existing
event is fine. Renames and shape changes get a **new type** (`ExpenseRecordedV2`)
plus a Marten upcaster from the old one. The old type is never deleted and never
repurposed, and no event type name is ever reused for a different meaning.

---

## 12. Implementation approach

**Decision: hand-rolled command handling first, Wolverine second — two
deliberate passes over the same code.**

- **Phase 1:** ASP.NET minimal APIs calling Marten directly — `FetchForWriting`,
  decide, `AppendOne`, `SaveChangesAsync`. Nothing hidden.
- **Phase 2:** once the repetition genuinely grates, port to Wolverine's
  aggregate-handler workflow and observe precisely what it removes.

Rationale: §2 applied honestly. The rework *is* the exercise, not waste — a
framework is far easier to judge once you have written the code it replaces.
Phase 2 is a conscious checkpoint, not a backlog item.

---

## 13. Next step: an event modeling session

Nothing gets implemented yet. The next artifact is a full event model — a
timeline / swimlane covering:

1. The happy path end to end: create group → invite → claim → record expenses →
   view balances → settle up.
2. Each step laid out as **command → event(s) → read model → screen**.
3. Wireframe stubs per screen, to check that each read model actually serves one.
4. The awkward paths: correcting an expense after a settlement, removing a member
   mid-trip, claiming the wrong member slot, two phones writing at once.

The test of the model: every screen is served by a named read model, and every
read model is derivable from the events in §11. Anything failing that test is a
gap — and much cheaper to find on paper.

---

### Tooling

**Decision: D2 (`brew install d2`), with `.d2` files hand-written as the source of
truth.** Iterate with `d2 --watch docs/event-model/happy-path.d2` for a
live-reloading browser view while editing.

Rationale: the notation requires horizontal time and fixed swimlanes, which most
text-to-diagram tools cannot express. D2's grid layout gives exact column
alignment across lanes, and — verified by rendering rather than assumed —
connections *do* draw across grid cells in 0.9.0. A layout-engine approach (one
container per slice, dagre/elk) was tried and rejected: it reordered the slices
and destroyed both the lane alignment and the time axis, producing a decent
flowchart and a useless event model.

Structure — **one flat grid, no nested containers.** `grid-rows: 5` with exactly
80 children yields 16 columns, filled row-major. Rows, top to bottom: slice names,
SCREENS, READ MODELS, COMMANDS, EVENT STREAM. Read models sit next to the screens
they feed and commands next to the events they emit, which keeps the arrow for
both slice types one row long. Column 1 holds the lane names, slices are separated by
a 2px divider column, and **every other column is one event slot**, so time runs
left to right across the whole stream. Every element is 250x160, or 250x50 in the
slice-name row.

A command emitting several events spreads them horizontally along the stream, so
the slice grows wider rather than taller — slice 1 emits three events and occupies
columns 2-4. **D2 grids have no colspan**, so that slice's screen and command sit
in the first of its three columns rather than spanning all three. This is the one
visible compromise in the layout.

Four constraints found by rendering rather than by reading documentation, each of
which silently produces a wrong diagram:

- **Sibling grid containers compute their column widths independently.** Modelling
  each lane as its own grid container looks correct and holds together only while
  every cell happens to be the same size. The moment one lane's cell differs, that
  lane's columns shift out of step with the others. A single flat grid is the only
  structure that guarantees alignment.
- **Grid cells stretch to fill their row**, and an explicit `height` does not stop
  it. So a multi-event slice cannot nest a taller container: it would stretch every
  other cell in that row. Growing a multi-event slice sideways along the stream
  sidesteps this entirely, and fits the notation better: the event stream is a
  time-ordered line, so consecutive events belong side by side.
- **Fill order is row-major under `grid-rows`**, so children are listed lane by
  lane. `grid-columns` fills differently when the child count does not divide
  evenly by the column count — prefer `grid-rows` and keep the grid exactly full.
- **There is no rowspan either**, so a slice divider has to be one thin cell per
  row. It reads as a segmented rule rather than a single continuous line — close
  enough at normal zoom, but it is a compromise, not the intended drawing.
- **Shape labels centre-align, and markdown blocks size to their content**, so
  neither gives left-aligned text in a fixed-width cell: an `md` block shrinks to
  its text and the cell then centres it, ignoring `width`. Padding a plain label
  with trailing spaces widens its bounding box, so centring that box leaves the
  visible text at the left. A hack, and the only thing that works.
- **A shape label has one font and one size, and plain labels are the only thing
  that respects explicit dimensions.** Uniform cards and per-line styling are
  therefore mutually exclusive. Fields are `- name: Type` lines in
  `style.font: mono` under the card's title, left-aligned by padding every line to
  the longest line in the model: equal-length lines in a monospace font form a
  rectangle, and
  centring a rectangle leaves them flush left. That padding is load-bearing, not
  cosmetic. Making the title alone bold or larger was attempted three ways and is
  not possible:
  - `style.bold` / `font-size` apply to the whole label, fields included;
  - a markdown label supports `**bold**` and headings, but ignores `width` and
    `height` and sizes to its content — which collapses the grid entirely, and
    `&nbsp;` padding adds no width to bring it back;
  - Mathematical Sans-Serif Bold codepoints have no glyphs in the monospace font,
    so they fall back to another face: not bold, and badly spaced.

  The title is distinguished by position alone.
- **A fenced code block inside a markdown label paints its own background**,
  covering the element's fill and leaving white label text unreadable. Use
  `style.font: mono` on the element instead.
- **Given-when-then specs cannot live in the grid.** A wide text block dictates its
  column's width and wrecks the alignment. They live in `slice-NN-*.md` instead,
  which is a better home anyway: they are test specifications, and they will map
  to xUnit tests more or less line by line.

Known limitation: a read model consuming many events produces long diagonal
arrows across the lanes, and it gets noisy quickly. Mitigation when it does: list
the source events inside the read model box and draw fewer edges. The source of
truth keeps every edge; the *rendering* is allowed to elide.

**Decision: `event-model.yaml` is the source of truth; `happy-path.d2` is
generated and must never be hand-edited.**

```
.venv/bin/python docs/event-model/generate.py
d2 --pad 30 docs/event-model/happy-path.d2 docs/event-model/happy-path.png
```

Promoted from a deferred plan once the grid reached 80 cells with load-bearing
padding — past the point where hand-editing is safe. The generated `.d2` was
verified to render identically to the hand-built version before the switch.

**Structure: slices own the elements they introduce.** An element is *declared
once*, in mapping form with a `name`, at its first appearance in slice order;
every later appearance is a bare string reference. So fields are written exactly
once, and a misspelled reference is an undeclared name rather than a silently
created new element.

**Slices are typed with the four canonical Event Modeling types**, spelled
verbatim in the `type` field: `State Change` (a user action that changes state and
records events), `State Read` (a screen or API response projected from events),
`Automation` (reacts to a state with no human involved) and `Translation` (an
integration point where the outside world pushes data in). The last two are
recognised vocabulary but have no layout yet — the generator errors rather than
guessing, because their rendering is best designed against a real slice.

**Arrows: none are written.** Intra-slice arrows follow from `type` — a
`State Change` slice is always screen → command → events, a `State Read` slice is
always events → read model → screen — so declaring them would merely restate the
type, and any arrow contradicting it would be a modelling error rather than a
drawing choice. The only inter-slice arrow is a read model's `reads` list of event
*type names*: the generator resolves each to the nearest occurrence at or before
the consuming slice, and dashes it when the only occurrence is later. No ids, no
endpoints. The same mechanism will serve automation slices (`reads: GroupLedger`)
without new syntax.

The generator validates before it renders and exits non-zero on any error, so it
can gate a commit:

- a slice with an unknown `type`, a type with no layout yet, or no screen
- a `State Change` slice with no command, no events, or declaring a read model
- a `State Read` slice that emits events, declares a command, or whose read
  model reads nothing
- an element referenced before it is declared — which is how typos surface
- an element redeclared after its first declaration
- a read model reading an undeclared event
- **an event not consumed by any read model**
- a read model reading an event first emitted in a *later* slice, which is
  legitimate but is drawn dashed rather than left looking like an ordinary
  backwards dependency
- the grid not being exactly full, which D2 silently reflows rather than rejecting

Each of those was confirmed to fire by mutating the model and re-running, rather
than assumed from reading the code.

Still to come: checking event names against the C# record names, so the model
cannot drift from the code.

The first run earned the exercise. `MemberInvited` was consumed by nothing, which
means no screen could distinguish a member who has been invited from one who has
merely been added — exactly the "invited / joined" state §4 depends on. Fixed by
adding it to `GroupLedger`. That is the check predicted to be most valuable, and
it was.

Tooling note: the generator needs PyYAML, which macOS's system Python refuses to
install into (PEP 668), so it runs from a project-local `.venv`, gitignored. It is
a documentation tool, not application code.

---

## 14. Open questions

- **OPEN** Frontend framework and rendering approach. Deferred deliberately.
- **DEFERRED** Transactional relay — shortlisted in §4; `LogEmailSender` until then.
- **DEFERRED** `PeriodClosed` / stream archival, until a stream is actually long.
- **DEFERRED** Wolverine port (phase 2 above).
- **DEFERRED** Frozen settle-up plan, unless a shifting plan bites in practice.
- **DEFERRED** Marten-backed `IUserStore`, if EF Core and Marten genuinely chafe.
- **DEFERRED** `MemberMergedInto` for duplicate slots — out of scope for v1.
- **DEFERRED** `MemberClaimReassigned`, unless a real handover happens.
