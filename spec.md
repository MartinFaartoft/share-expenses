# Shared Expenses — Spec

A small app for splitting costs inside a group (a trip, a flatshare, a dinner series),
and for working out who should transfer what to whom to clear the balances.

Built as a deliberate vehicle for learning **event modeling** and **event sourcing**,
using C# with Marten on PostgreSQL.

Status: design in progress. Decisions below are settled unless marked **OPEN**.

---

## 1. Scope

**In scope (v1)**
- Create a group; invite people to it by email.
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
- **Frontend:** server-rendered HTML, built into each slice (below). The primary
  target is a phone screen, one-handed, and the sign-in and join flow must not
  require an install.

### Frontend: server-rendered screens in the slices

**Decision: each screen is a Razor Component in its slice's folder, rendered on
the server, made interactive with htmx.** `Slices/RecordExpense/AddExpense.razor`
sits beside `Decider.cs` and `Endpoint.cs`; a Wolverine endpoint returns it as a
`RazorComponentResult`. Components are rendered statically — no Blazor
interactivity, so no live server connection and no WebAssembly download. htmx
(HTML attributes, no hand-written JavaScript) posts forms and swaps in the
fragment the endpoint answers with: a rejection shown in place, a list updated.

Rationale: the requirement is that a slice's screen live with the slice's code.
Rendered on the server, that colocation is real — one folder, one language, one
project — and the screen uses the slice's internal `Reader`, read model and
`Decision` reasons directly, with no JSON contract or generated types between
them. It suits the target: small pages, nothing to install or download first,
plain HTML forms (so the sign-in code's autofill is one attribute). One origin, so
the `SameSite=Lax` cookie needs no CORS. §2 holds too: the learning goals are event
modelling and event sourcing, not a frontend stack.

Rejected: a JavaScript SPA (React, Svelte, …) with its files inside the slice
folders — colocation by folder only, the slice's types unreachable without
generating them, and a second toolchain and test runner; interactive Blazor —
server mode needs a live connection that a phone in a restaurant drops, and
WebAssembly a heavy first download. Accepted: each interaction is a round trip, and
offline use is out (already a non-goal, §1).

**The screens are the only way in: there is no JSON API, for now.** It was deleted
once screens existed: nothing called it but its own tests, and every screen
doubled each slice's endpoints and tests. A slice whose screen is not built yet has
no endpoint at all — its `Decide` or `Read` and its specs stand, unreachable until
the screen arrives (Add member, Invite member, Record settlement, View settlement
plan; §14). A native app may want an API later; it
would be designed then, for that client, and the endpoints removed here are in the
git history. **Tests set up through events:** an integration test appends the
events it needs straight to the store (`tests/…/Infrastructure/Seed.cs`) and drives
only its own slice's screen, so no test depends on another slice's screen.
**Routes are written in full,** each screen at its own address (`/`,
`/groups/{group}`, `/invites/{group}/join`); there is no global route prefix.
- **A component's parameters must be public,** and so must their types — a screen's
  read model, when it takes one whole. The architecture rule counts them as contract
  types, as it does an endpoint's signature.

**Verified by spike (Wolverine 6.44, .NET 10), not assumed:**
- A Wolverine endpoint returns a `RazorComponentResult`, page or fragment, and a
  form endpoint can use `[WriteAggregate]` and return `(IResult, Events)` exactly as
  a JSON one does.
- **Antiforgery must be opted into per form endpoint, with `[ValidateAntiforgery]`.**
  Wolverine documents that a `[FromForm]` parameter adds it automatically; in 6.44
  it did not, and a post with no token was accepted. Forms render
  `<AntiforgeryToken />` (namespace `Microsoft.AspNetCore.Components.Forms`).
- **A failed antiforgery check must be answered by middleware.** ASP.NET's
  middleware only records the failure; Wolverine's generated code then reads the
  form, which throws — a 500. A small middleware right after `UseAntiforgery()`
  answers 400 when the request's `IAntiforgeryValidationFeature` is invalid.
- **Components are always public.** The generated class cannot be made
  `internal`, so the slice architecture rule must allow them, as it allows
  Wolverine endpoints (§12).

### Hosting consequences

- TLS termination through a reverse proxy (Caddy or nginx) with Let's Encrypt.
- **The app applies its own schema on startup, in every environment.** The VPS runs
  no migration hook, so a deploy is a new binary and a restart. Before the app
  listens, one startup step brings all three stores up to date, and a failure stops
  the process rather than serving against a half-made schema:
  - **Identity:** EF Core's `Database.MigrateAsync()`, applying the committed migrations.
  - **Ledger and Wolverine:** Marten's `Storage.ApplyAllConfiguredChangesToDatabaseAsync()`
    with `AutoCreate.None`, so nothing is created lazily behind a request. It creates
    the `ledger` schema, the event tables and every slice's documents. It diffs the
    configured model against the database and only adds; it never drops or rewrites.
    **No `wolverine` schema is made or needed:** Wolverine runs mediator-only, with no
    inbox or outbox. (The 8 tables seen in Development came from the old
    `CreateOrUpdate`, and sat unused.) The durable outbox for emails (§14) would need
    them, and this step would have to create them.
  - **One path for every environment.** Development and the tests take the same step
    as the VPS, so a missing table cannot hide behind a dev-only shortcut. The dev-only
    `AutoCreate.CreateOrUpdate` and `MigrateIdentityInDevelopmentAsync` go.
  - **Migrating is not rebuilding.** A changed fold in a stored projection (the group
    activity) leaves the old documents stale until `projections rebuild` runs; that
    stays a deliberate step after such a deploy (§11).
  - **The database role has DDL rights,** so the app creates its own schemas and
    tables; nothing is prepared by hand on the VPS.
  - **A destructive change is not automatic.** EF migrations are reviewed files; Marten
    only adds. Anything that removes or rewrites data is a hand-written migration,
    with a `pg_dump` taken first (backups below).
  - **One instance at a time.** Both tools take a lock, but a rolling deploy with two
    instances is not a case this app is built for.
- **Backups are entirely our responsibility.** The event stream *is* the ledger —
  lose it and every balance is gone, with no third party holding a copy.
  Scheduled offsite `pg_dump`, and a restore that has actually been tested rather
  than assumed.
- Restarts are manual and infrequent, which makes the persisted data-protection
  key ring (§4) load-bearing rather than theoretical: without it, every restart
  logs everyone out.
- No managed-Postgres safety net — connection limits, vacuum behaviour and disk
  headroom are ours to watch. At this data volume none of it should bite, but
  nobody else is watching either.

### What is event-sourced, and what is not

**Event-sourced (the domain):** groups, members, expenses, settlements.
This is the ledger. History *is* the product here — "why is my balance this
number" is answered by replaying what happened.

**Plain documents, not event-sourced (supporting state):** users, sessions,
sign-in codes, and invites — the `Invite` document binding a member slot to the
address it was invited at.

An invite is split across the line. *That* a slot was invited, by whom, until when,
is a ledger fact: `MemberInvited(memberId, inviteId, expiresAt, by)`. The address
is personal data, so it lives on the erasable `Invite` document the event's
`inviteId` names, and never enters the ledger. The stream decides which invite is
a slot's current one, so re-inviting and claiming retire old invites with no
second store to keep in step; the document only answers "which slots were invited
at this address" (§11).

Rationale: these have no interesting history, no invariants worth auditing, and
high write churn. Event-sourcing a session table produces a firehose of noise
that nobody will ever query. Keeping the boundary explicit is itself one of the
learning goals — the instinct to event-source everything is the main way this
pattern goes wrong.

---

## 4. Identity and access

**Decision: sign in with a six-digit code sent by email — the only way in. No
passwords stored, no links, no external identity provider.** Built on ASP.NET Core
Identity as a local user store.

Rationale for revisiting the "rolling your own" concern: most of this flow is
framework code. Identity supplies the user store, email normalisation, cookie
issuance and renewal, and security-stamp-based global sign-out; ASP.NET supplies
rate limiting. What is genuinely owned is the code itself — issuing, checking and
retiring it — and the choice of mail relay. Rejected alternatives: OAuth via Google
or similar (no appetite for a third-party provider), and local passwords (storing
hashes makes every database backup a credential store, and accounts used twice a
year mean forgotten passwords by the second trip — which requires a reset flow,
so email returns anyway, on top of the passwords).

**Why a code and not a link** (reversed from an earlier link-plus-code design).
A typed code works on any device — email on the phone, browser on the laptop —
and keeps the user in the tab they started in, so nothing has to survive a
round trip through a mail client. And there is nothing for a mail client or
corporate scanner to prefetch and burn. A link needed a landing page with a POST
button to defend against prefetchers, and a way to carry state across the tab the
mail client opens; a code needs neither.

### Mechanism

Both steps are forms on the sign-in screen (`/sign-in`).

- **Request:** `POST /sign-in/code` with `email`. Generates six uniformly random
  digits, stores a `SignInCode` — `{ email, SHA-256(code), expiresAt, attemptsLeft }`,
  one per address, replacing any earlier one — and emails the code. The response is
  the same whether or not the address has an account (below).
- **Verify:** `POST /sign-in` with `email` and `code`. The code must match, be unexpired
  and have attempts left. A match deletes the `SignInCode`, finds the user by
  address — creating them if there is none — and signs them in. A miss spends an
  attempt; the last one deletes the code. Every failure is the same answer: the
  code is wrong or has expired.
- **Lifetime: 10 minutes. Attempts: 5 per code.**
- Sessions: sliding 30-day cookie, `HttpOnly`, `Secure`, `SameSite=Lax`.
  Re-authentication is one short email away, so a modest window is cheap.

### Secure by default

**Decision: every endpoint requires a signed-in user unless it is explicitly
anonymous.** An authorization `FallbackPolicy` requires an authenticated user for
every endpoint without authorization metadata; the public ones say so with
`[AllowAnonymous]` / `.AllowAnonymous()`. A screen asked for while signed out
redirects to `/sign-in?returnUrl=…`, and signing in returns there — or home. Only a local path is
accepted as `returnUrl`: anything else would let a crafted link send a just-signed-in
user to another site (an open redirect). The explicit `[Authorize]` on slice endpoints stays — redundant now, but
it states the intent.

Rationale: endpoints used to opt in with `[Authorize]`, so a new slice that forgot
it was silently public — and a group's data leaked rather than 404'd. With the
fallback, forgetting is safe rather than merely caught.

**The allow-list is pinned by an architecture test.** Today: the sign-in screen's
`GET /sign-in`, `POST /sign-in/code` and `POST /sign-in`, and `GET /health`; candidates later, an
invite landing page if one returns. `AuthorizationTests` walks the running app's
`EndpointDataSource` — Wolverine's endpoints and minimal APIs alike — and requires
the set carrying `IAllowAnonymous` to equal the allow-list exactly, so it fails on
an unexpected public endpoint and on an allow-listed one that is gone (the list
cannot rot). It also checks the fallback policy denies anonymous users, and that an
unauthenticated request is sent to sign in.

### The code is ours, not Identity's

**Decision: our own `SignInCode` record, not Identity's `EmailTokenProvider`.**
Identity's provider issues codes only to an existing user, so the account would be
created when a code is *requested* — leaving rows behind for every address anyone
types. Its codes stay valid for several minutes after use, and it counts no
attempts. Our record is single-use by deletion, counts attempts, and needs no user
until the code is proven: the account is created on first successful sign-in.

`SignInCode` is login state, so it lives with Identity: an EF table in the
`identity` schema (below), never in the ledger. The code is stored hashed, so a
backup holds no live code in the clear; with a million possible codes the hash
does not stop a determined reader of the live database, but such a reader would
have to act within the code's ten minutes.

### Guessing is what the limits are for

A six-digit code is one in a million. **Five attempts per code** bound guessing
per code; **ASP.NET's rate limiter on the request endpoint**, keyed per address
and per IP, bounds how many codes — and so how many attempts — anyone can get for
an address, and stops the endpoint being used to flood someone's inbox. Both are
load-bearing: without them a code is brute-forceable in minutes.

### Data protection keys must be persisted

Auth cookies are validated against the data-protection key ring. The default in a
container is in-memory, so **every restart would sign everyone out.**

**Decision:** persist keys to Postgres via `PersistKeysToDbContext`.

Recorded as a decision rather than an implementation note because getting it
wrong produces a bug that presents as random, intermittent logouts — the kind
that gets misdiagnosed for weeks.

### Self-registration and user enumeration

- **Decision: anyone can sign up.** Requesting a code for an address with no
  account sends a code that, once used, creates the account. A new user can
  create a group straight away; an invite is not a precondition for an account.
  (Reversed from an earlier "nobody self-registers" decision: it made the first
  sign-in of every invitee a special case, and blocked anyone from starting a
  group without being invited to one first.)
- **Every account's address is verified**, because an account only comes into
  being by proving the address with a code. Invites rely on this (below).
- **Self-registration grants no visibility.** An account sees only groups it holds
  a slot in — its own, and those it has claimed via an invite — and invites
  addressed to it. Everything else answers "not found", exactly as for a group that
  does not exist (§5; AddMember, InviteMember, AcceptInvite).
- **No user enumeration:** requesting a code returns the same response whether or
  not the address has an account, and both cases send an email. Nothing is
  written for an address until its code is used, apart from the short-lived
  `SignInCode`.

### Persistence: EF Core, firewalled

Identity's default store is EF Core, which puts two persistence stacks in one
codebase alongside Marten.

**Decision: accept EF Core, strictly confined to Identity** — its own
`DbContext`, its own `identity` schema, never joined to domain data. Marten owns
the event store, projections and the domain's plain documents; EF owns users,
sign-in codes and the key ring, and nothing else.

Rationale: §3 already fences identity off as non-event-sourced, so the boundary
exists and this respects it. The alternative — implementing `IUserStore` and its
siblings over Marten documents — eliminates EF, but means writing and testing
auth plumbing, which §2 puts explicitly off the learning path. Revisit only if
the two stacks genuinely chafe.

### Mail relay — deferred

**Decision: deferred.** Development and early verification use a
`LogEmailSender` implementation of `IEmailSender` that writes each sign-in code
and invite straight to the application log. Entirely adequate while the only
users are the author and deliberate testers.

**Safety gate:** the log sender must refuse to start when the environment is
Production. Otherwise it silently locks out every real user, and the failure
presents as "nobody can log in" with no error anywhere in the system — the log
sender is working perfectly, it just isn't sending email to anyone. Choosing a
relay is a release blocker for the first real group, and nothing before that.

Shortlist for when that time comes: Postmark (best-in-class transactional
deliverability), Resend (best developer experience), Amazon SES (cheapest, most
setup). Free-tier terms shift; verify before committing.

Running our own outbound SMTP is explicitly rejected: cold IP reputation and
unaligned DMARC put sign-in emails in spam folders, and a sign-in code in spam is
a total lockout that the user blames on the app. SPF, DKIM and DMARC must be
configured on the sending domain whichever provider is eventually chosen.

### Consequences accepted

- Email delivery is a hard dependency. If it breaks, nobody can log in.
- Deliverability remains a real user-facing failure mode — mitigated by a
  reputable relay, never eliminated.
- No offline or LAN-only login.
- An account *is* its address. Changing address, holding several, or merging two
  accounts are not v1 features (§14).
- Browsers evict data for sites unused for months, so a returning user on iOS may
  find their session gone between trips. This is acceptable precisely because
  re-authentication is self-service: request a code, type it.

### Members vs users

`member` (in the group's event stream) is deliberately distinct from `user` (a
login identity). Expenses reference `member_id` and never `user_id`, which is
what makes everything below cheap.

**Decision: placeholder members are allowed and claimable.**
Add someone by name alone and record expenses against them immediately. When they
are invited and sign in with the invited address, they claim that member slot and
the existing history stays intact.

Rationale: without this the app fails at the exact moment it is meant to be
useful — standing in a restaurant, wanting to enter the bill now, with one person
who will not open their invite until Thursday.

#### Claiming is driven by the invite, not by a picker

**Decision: the invite binds the slot.** `InviteMember` ties an email address to
one specific member slot, so the address identifies which slot is being claimed.
There is no "who are you?" list to mis-tap.

Consequences:
- Claiming is not a free choice; it is accepting an invite addressed to you.
- A placeholder added by name but never invited is not claimable until someone
  invites it. This is a feature — every claim traces back to a deliberate act by
  an existing member.

**Decision: the invited address is the key — only a user signed in with it may
claim.** Signing in proves control of the address (above), so an invite is
claimed by whoever can read the mail it was sent to, and by nobody else. Reversed
from an earlier design where the invite carried a secret token and "the token is
the capability": whoever held the link could claim, whatever address they signed
in with. That design accepted forwarded links and the "you invited my old
address" case; it also needed the token kept out of logs, carried across sign-in
in the browser, and looked up by an anonymous endpoint. Matching the address
removes all of that.

Accepted cost: an invite to the wrong address — an old one, a work one — can only
be fixed by the inviter re-inviting the right one. A forwarded invite email does
nothing for its new reader. Letting a signed-in user prove a *second* address to
claim an invite sent there would cover both, but it opens account addresses, email
change and account merging; deferred (§14). Claims are still announced in the
activity feed, consistent with §5: accountability by visibility.

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
  participants in member-added order. A payer who does not share the expense gets
  none: leftovers go only to participants. In a shares split the same order breaks
  ties between equal fractional remainders.
- **Exact split:** amounts must sum to the total exactly, or the command is
  rejected. No rounding is applied.

Rationale for payer-first: the payer absorbing the stray penny means they are
owed a penny less, which is socially invisible and never worth arguing about.
The rule is fully deterministic — the same input always yields the same split.

**Decision: splitting is one pure function in `Shared/`**, used by every slice
that computes splits — recording an expense now, correcting its amount or split
later — so a correction can never round differently from the original. It
computes in 128-bit integers, so `total × share` cannot overflow.

**Decision: an amount is at most 10¹² minor units** (ten billion pounds). Far
beyond any shared bill, it keeps every amount exact as a JSON number in any client
(2⁵³), and catches a typo with extra zeros.

### Events carry computed splits, not just inputs

**Decision:** an expense event records *both* the split as entered — its mode
and that mode's inputs (§7) — *and* the resulting per-person amounts.

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

**Decision: the split is a tagged union — one shape per mode**, in the API and in
the event alike:

```
{ "mode": "equal",  "participants": [memberId, …] }
{ "mode": "shares", "shares":  [ { memberId, shares }, … ] }
{ "mode": "exact",  "amounts": [ { memberId, amountMinor }, … ] }
```

Rationale: with one flat participant shape carrying an optional weight *and* an
optional amount beside a mode string, every combination that makes no sense —
weights on an equal split, amounts on a shares split — had to be written and then
rejected. A union makes them unwritable. A malformed split (unknown or missing
`mode`, a share or amount missing) fails to read and is answered 400 before any
deciding. The `mode` values are stored, so — like event type names — they are
fixed forever. An explicit converter reads the union, not System.Text.Json's
built-in polymorphism: that needs `mode` first in the object, and answers a missing
one with a server error rather than a bad request.

**Decision: the server computes equal and shares; exact is the client's
arithmetic.** Equal and shares record an *intention* the server can replay: a
corrected amount re-splits by it, and an edit form can show the split as it was
entered. Exact records only amounts — nothing says whose part shrinks when the
total changes — so a corrected exact expense needs new amounts. Rejected: having
the client compute every split and the server check only that the amounts sum.
It loses the intention, unless kept as an unchecked label that a careless client
could make false; and every client would carry its own copy of the rounding rule.

**Decision: a settlement payment is just another transaction in the ledger.**
"Bob paid Alice 50" is an event with a payer, a single beneficiary, and the full
amount.

It is **not an expense**, though its balance effect is exactly that of an expense
paid by Bob and shared by Alice alone. It is a different fact, recorded as its own
`SettlementRecorded` (§11): "Bob added an expense" would be false; settle-up's
shared-history tiebreak (§10) counts expenses only, and a repayment is not shared
spending; and the rules differ — one recipient who is not the payer, no
description, no split, no corrections. The two meet in the balance read model,
which reduces both to "a payer, credited; debits per member".

Rationale: this is the load-bearing simplification of the whole design. Balances
are always derived from the stream. There is no stored balance, no `settled`
flag, and no state machine. Correcting an old expense after a settlement simply
replays, and the residual difference appears as a new balance rather than as
corruption. **Partial settlements need no code at all**: owe 50, record 30,
balance is 20.

**Decision: settlements are recorded by any member, with no confirmation step.**
Usually one of the two parties records it; the event is appended immediately and
the balance clears. Any member can remove it if it was a mistake. Any member, not
only the parties: trust is flat (§5), and a placeholder cannot sign in — if Carol,
a placeholder, pays Alice, someone else records it. The activity feed shows who.

**Decision: a settlement carries the day the money moved** (`paidOn`), as an
expense does. Payments are often recorded days later, and the history shows when
the money moved, not when someone got round to saying so; the recording time stays
Marten metadata (§11).

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

**The tie rules, pinned** (with View settlement plan, so the procedure can be
tested exactly):

- **Exact matches:** every debtor/creditor pair of equal magnitude is a candidate.
  Candidates are taken by score (highest first), then debtor, then creditor in
  member-added order, skipping any whose debtor or creditor is already matched.
  Simple and deterministic; it does not search for the set of pairs with the
  greatest total score — not worth the complexity for a tiebreak.
- **Greedy:** of the creditors with the largest credit and the debtors with the
  largest debt, the pair with the highest score; then creditor, then debtor, in
  member-added order.
- **The plan is listed** by payer, then recipient, in member-added order, so each
  payer's lines sit together.

**Shared-history score** for a pair: the number of live expenses in which both
members appear, as payer or as a member of the split (even with a zero exact
amount). Expenses only: a settlement is a repayment, not shared spending (§7).

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

**Decision: one stream per group.** The stream id is the group id. Members,
expenses, corrections and settlements are all events on that one stream, and the
stream is the consistency boundary: every command decides against state
rehydrated from it, and appends at the version it read. There is no single
`Group` aggregate class — each slice folds its own state from the stream (§12).

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
AddMember, InviteMember, AcceptInvite, ReleaseMemberClaim, RenameMember,
  RemoveMember
RecordExpense, RemoveExpense
CorrectExpenseDescription, CorrectExpenseAmount, CorrectExpensePayer,
  ChangeExpenseSplit, CorrectExpenseDate
RecordSettlement, RemoveSettlement
```

Commands are named for the user's intention, events for the resulting fact — they
need not match. `AcceptInvite` emits `MemberClaimed`, the same fact CreateGroup
records for the creator, who accepted no invite; naming the event after the
command would make CreateGroup record something false.

### Events

Group and membership:
```
GroupCreated(name, currency, createdBy)
GroupRenamed(name, by)
GroupArchived(by) / GroupUnarchived(by)
MemberAdded(memberId, displayName, by)
MemberInvited(memberId, inviteId, expiresAt, by)
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

The same line runs the other way: **metadata never drives a domain decision.** A
rule that needs a time reads it from the payload. A *deadline* is such a fact —
`MemberInvited.expiresAt` — because it was decided when the event happened; it
is not a copy of the append time, and it does not move if a setting changes
later. "When was it recorded" stays metadata, fit for display (the activity feed)
but not for rules.

Note on enforcement: "a group is created exactly once" is guaranteed by Marten's
stream semantics and optimistic concurrency — appending `GroupCreated` at expected
version 0 to a stream that already exists fails — rather than by aggregate
validation.

**Decision: no email address enters the ledger.** `MemberInvited` records the
slot, the invite's id and the actor; the address the invite went to lives on the
`Invite` plain document that id names (§3), and a user's address lives only in
Identity. Events are immutable, so personal data in them could never be corrected
or erased; keeping it out removes the problem rather than managing it, and email
changes stay an account matter instead of an event on every group the user is in.
Cost accepted: the ledger cannot answer "which address was this invite sent to" —
the `Invite` document can, for as long as it is kept (until the slot is claimed or
re-invited).

**Decision: invariants use only the stream; guards may use lookups.** An
*invariant* protects the ledger and must always hold — "one user holds at most
one slot per group" is keyed on `UserId`, enforced at claim time, inside the
stream's transaction. A *guard* saves a person from a mistake that an invariant
would otherwise reject later and less helpfully — "one address, one slot" at
invite time is one. Guards may read data outside the stream (Identity, `Invite`
documents), passed into `Decide` as looked-up command fields so deciding stays
pure. Such lookups can be stale or race; the worst outcome is a mistake reaching
the invariant, never a corrupt ledger.

**One lookup does more than guard: claiming.** Which slot a user may claim is
decided by address, and the address is not in the ledger — so AcceptInvite looks
up the `Invite` documents addressed to the user and passes their ids into
`Decide`. The lookup *proposes*; the stream *confirms*: a slot is claimed only if
its current invite (its newest `MemberInvited`) is one of those ids, unclaimed and
unexpired. A lookup still never supplies an event's values — `MemberClaimed`'s
slot comes from the stream — but here it authorises them. That is the price of
keeping addresses out of the ledger, and it is bounded: a stale or leftover
document can only name an invite the stream has already retired.

This resolves the creator's email and email changes together: InviteMember asks
Identity which account *currently* owns the address and rejects it if that user
holds a slot — the creator included, whatever address they use today — and
rejects an address with an open invite on another slot. A changed-away address is
free again; one still in use is caught early. Email change itself is not a v1
feature (the address *is* the login), but nothing depends on that.

**Invites expire after 30 days, recorded as a deadline on the event:**
InviteMember decides `expiresAt = now + 30 days` (the clock passed in, so
deciding stays pure) and `MemberInvited` carries it. An invite is live while
`now < expiresAt`. Enforced where the invite is used (viewing and claiming); an
invite is retired earlier by re-inviting the slot or by the slot being claimed.
Shortening the lifetime later affects only new invites — invites already sent keep
the deadline their email promised.

**The invite email carries no secret.** It names the group, the slot and the
inviter, and asks the invitee to sign in with the invited address; a plain link to
the app is safe to prefetch, forward or log. (Reversed from an earlier design whose
invite link carried a token in its URL fragment, to keep that secret out of server
logs.)

Transactions:
```
ExpenseRecorded(expenseId, description, amountMinor, payerMemberId, split,
                splits[memberId, amountMinor], paidOn, by)
ExpenseRemoved(expenseId, by)
SettlementRecorded(settlementId, fromMemberId, toMemberId, amountMinor, paidOn, by)
SettlementRemoved(settlementId, by)
```

Corrections — **decision: intention-revealing, one event per kind of change**:
```
ExpenseDescriptionCorrected(expenseId, description, by)
ExpenseAmountCorrected(expenseId, amountMinor, splits[], by)
ExpensePayerCorrected(expenseId, payerMemberId, by)
ExpenseSplitChanged(expenseId, split, splits[], by)
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
  not derivations. For an equal or shares split, correcting the amount re-splits
  by the recorded inputs; an exact split cannot be re-split, so correcting its
  amount requires new amounts (§7).
- Correction events carry only the **new** value, never the old one. A projection
  is a fold that already holds current state when the event arrives, so it can
  render "£140 → £120" without the payload duplicating history. Storing
  before-values is a common reflex worth resisting.

**Settlements have no correction events.** A wrong settlement is removed and
re-recorded. Rationale: it has five fields, and "that transfer never happened" is
the truthful statement anyway.

### Read models and projection lifecycles

| Read model | Lifecycle | Serves |
|---|---|---|
| `GroupActivityReadModel` | **inline** — the stored `GroupActivity` | the group page: the money history (expenses and settlements), your standing |
| `GroupBalancesReadModel` | **live** — folded per request | everyone's balance |
| `ActivityFeed` | **inline** | who did what, when |
| `HomepageReadModel` | groups **async** — the stored `UserGroups`, one document per group; invites **live** | the home screen: a user's groups, and invites waiting |
| `SettlementPlanReadModel` | **live** — computed per request (§10) | the settle-up plan: who pays whom |

**One read model, two lifecycles.** The home screen's two parts are each kept the
way their data needs. Its groups cannot be computed per request — "every group I am
in" spans every stream — so they come from `UserGroups`, stored and projected by
Marten's async daemon (solo mode, in the app's process). **One document per group,
not per user:** the group's name and the users holding a slot, queried for those
containing the signed-in user (a GIN index). Planned as a per-user, multi-stream
projection, it turned out `MemberClaimed` carries no group name: a per-user document
would have to look the name up as each claim arrives. Per group, the name is
folded in order, and "which groups am I in" is a query. Its invites are
live, the third lifecycle: the user's `Invite` documents name their groups, so
showing them means folding a handful of group streams of a few hundred events each
— cheap enough per request, and nothing stored that could go stale. Neither part
needs the other's lifecycle, and the screen needs both, so one slice (View
homepage) owns both.

`GroupActivity` is what is stored: View group's state, a Marten snapshot projected
inline as the `group_activity` document. The read model is shaped from it per
request, because part of it depends on the reader — "you", your standing. A stored
projection holds facts; anything that depends on the time or the reader is
computed on reading. (Everyone's balances, View balances, are folded live per
request: a few hundred events, a member list, nothing worth storing.)

Rationale for inline on the group page: the core phone interaction is "add the
expense, then immediately look at the group". Inline projections commit in the
same transaction as the events, making read-your-writes guaranteed rather than
hopeful. Async would open a window where you enter £120 and your standing has not
moved — the most alarming bug this app could plausibly ship. (A live fold, as View
balances', reads the events themselves, so it is read-your-writes too.)

`UserGroups` is async deliberately, so the project exercises both lifecycles and
the async daemon. Staleness is harmless there: after creating or joining a group
you are redirected straight into it by id, never via the list. It is never a reason
to redirect to home after a write.

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

### Invariants enforced on the group stream

Each invariant is enforced by the slice whose command it constrains, against that
slice's own state (§12). The consistency guarantee comes from the stream version,
not from a shared class, so splitting the checks across slices weakens nothing.

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
  is ambiguous. Enforced by AcceptInvite against the stream alone, before
  any invite is even looked at; the stream version closes the race.

Two of these cut across nearly every slice — "not archived" and "is a current
member". Each slice folds the few events involved itself. The duplication is a
handful of lines per slice and is accepted; it is the price of slices that can be
read, changed and deleted in isolation.

### Concurrency

Optimistic concurrency on the group stream version. Two people adding an expense
from different phones at the same restaurant table is the expected case, not an
edge case — the loser retries. Marten's `FetchForWriting` is the intended
mechanism.

**Decision (for now): the client retries.** A conflict is answered with 409 and
nothing else; the client resends, and the rules run against the new state.
Server-side retry — re-fetch, re-decide, re-append, a few times — was considered
and deferred: it would be correct rather than a blind overwrite, since the rules
run again, and would turn most conflicts into one round trip. Kept visible while
learning; the reminder lived in `Slices/AddMember/Endpoint.cs` and returns with its
screen, where "the client retries" becomes a form asking to (§14).

### Event schema evolution

**Decision: additive-only by default.** Adding an optional field to an existing
event is fine. Renames and shape changes get a **new type** (`ExpenseRecordedV2`)
plus a Marten upcaster from the old one. The old type is never deleted and never
repurposed, and no event type name is ever reused for a different meaning.

**The one exception, before any real data:** events changed shape in place while
no stream outside development and tests held them — `MemberInvited` when invites
moved from tokens to addresses (`tokenHash` → `inviteId`, §4), and
`ExpenseRecorded` when its split became a tagged union (`splitMode` and
`participants` → `split`, §7). Their data was reset rather than `…V2` types and
upcasters written for nothing. From the first real group on, the rule above holds
without exception.

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

**Done: every slice runs on Wolverine** (WolverineFx 6.44, `WolverineFx.Http.Marten`),
ported once AcceptInvite made four state-change slices. What it took over, and what
it cost:

- **Gone from every slice:** the handler — fetch, append, save, catch the conflict —
  and its `Outcome` type. Wolverine generates that code around each endpoint
  method. What stays in a slice is deciding, and mapping the decision to HTTP.
- **Cost: visibility.** Wolverine discovers only public endpoint classes and
  generates code against their signatures, so the endpoint class and every type in
  its signature — request, state, and the state's own member types — must be
  public. "Only events are public" became "only events couple slices" (below).
- **Cost: explicit wiring.** Endpoints are found by scanning, against the earlier
  decision to stitch them together by name (below).
- **Smaller costs:** each fetched `State` needs an `Id`; internal services need a
  service-location allow-list entry; Wolverine adds 8 tables in its own `wolverine`
  schema even in mediator-only mode; generated code is compiled with Roslyn at
  startup (`WolverineFx.RuntimeCompilation`) unless pre-generated with `codegen write`.

### Code structure: vertical slices

**Decision: one folder per event-model slice, and the folder owns everything about
it** — events, command, state, decision logic, read model, projection and HTTP
endpoints. Folder and namespace are named after the slice in `event-model.yaml`,
in PascalCase: `Slices/CreateGroup`, namespace `ShareExpenses.Slices.CreateGroup`.

Rationale: the event model is already cut into slices, so the code should be cut
the same way. A slice can then be read, changed or deleted as one unit, and a
diff touching two slice folders is a signal worth noticing in review.

```
src/ShareExpenses/
  Program.cs          hosting only
  AllSlices.cs        what the slices register with the store
  Infrastructure/     Identity (EF, sign-in; its screen in Identity/Screens/), Marten store, Wolverine
  Shared/             pure, framework-free code used by several slices
  Web/                the HTML side every screen shares: page shell, wiring (§3)
  Slices/<Name>/      one folder per slice
  wwwroot/            static files: the stylesheet, htmx (pinned, served by the app)
```

`Web/` is to screens what `Shared/` is to domain code — shared by several slices —
but framework code, so it is kept apart and `Shared/` stays pure. Outside the
slices, a screen sits in a `Screens/` folder beside the code it serves — sign-in's
in `Infrastructure/Identity/Screens/` — so frontend and backend code are told apart
at a glance without drifting apart.

**Decision: a single application project.** A slice owns its endpoint, so it needs
ASP.NET and Marten; a framework-free domain project would split every slice
across two projects. Purity is kept where it pays: decision logic is a pure
function inside the slice, and `Shared/` must not reference ASP.NET, Marten or
any slice.

**Decision: only events couple slices.** Originally "only events are public":
every other type in a slice was `internal`. Wolverine (phase 2) forces more types
public, so visibility no longer marks the boundary, and the architecture tests
classify instead. Public types in a slice may be:

- its **events** — the only types another slice may use;
- its **Wolverine endpoint** class, and its **contract**: the types in the
  endpoint's public method signatures (request, folded state), and the public
  member types of those, transitively (a state's `Slot`);
- an optional **entry point**, `<Name>Slice`, with `Register(StoreOptions)` — only
  for slices that own events or projections. The suffix avoids the namespace
  `CreateGroup` colliding with a type of the same name.

Everything else stays `internal`: `Command`, `Decider`, read models, responses
that are returned as an `IResult` rather than in a signature.

- **An event is owned by the slice that first emits it** — the same rule as
  "declared once, at first appearance" in `event-model.yaml`. `AddMember` emits
  `MemberAdded` but does not declare it; it uses `CreateGroup`'s public record.
- Events are the only coupling between slices, which is exactly what the event
  model draws.

**Decision: per-slice private state, no shared aggregate.** A state-change slice
folds only the events it needs — from any slice, since events are public — into
its own internal state type, and decides with a pure function
`Decide(state, command) → events | rejection`. `RecordExpense` knows members and
removals; `RemoveMember` additionally folds balances.

Rationale: a shared `Group` aggregate would be the one fat type every slice
depends on, which is precisely the coupling the slices exist to avoid. It also
couples every slice to more than events. The state a command needs is a
projection of the stream like any other; there is no reason it must be the *same*
projection for every command.

**Enforcement: an architecture test.** C# has no folder-level visibility, so
`internal` alone means assembly-wide. A test fails the build when:

- a public type in a slice is none of: event, endpoint, contract, entry point;
- a slice depends on anything of another slice's but its events — internal or
  public;
- `Shared/` depends on a slice, ASP.NET, Marten or Wolverine;
- a `<Name>Slice` has no `Register(StoreOptions)`, or `AllSlices` does not call it;
- an event is not registered by any slice's `Register` — unregistered, Marten
  would quietly store it under a name derived from the class, identical today and
  wrong after a rename (§11).

**Marten constraint, verified by spike rather than assumed:** internal state
types, internal projected documents, inline projections over them, LINQ queries
and `FetchForWriting` concurrency conflicts all work. But Marten 9 dispatches
conventional `Create`/`Apply` methods through a compile-time source generator,
which silently declines methods that are not `public` — the failure only surfaces
at runtime, as "no source-generated dispatcher found". So: **convention methods
`public`**, whatever the type's visibility. (Every `State` is public now anyway —
see phase 2 — but the constraint holds for internal folded types.)

Tests reach internal types through `InternalsVisibleTo`. Rejected: private nested
types in a `static partial class` per slice (compiler-enforced, but untestable
below HTTP and hostile to Marten's code generation), and a project per slice
(real enforcement, roughly twenty projects for v1).

**Decision: endpoints are discovered; store registrations are explicit.**
Originally `AllSlices.cs` called every slice's `Register` and `Map` by name, and
reflection-based discovery was rejected as saving a line per slice at the cost of
knowing what is wired up. Wolverine discovers endpoints by scanning, with no
explicit alternative, so that decision is reversed for endpoints: the API surface
is listed by `dotnet run --project src/ShareExpenses -- describe` (or the OpenAPI
document) instead of read from one file.

Store registrations stay explicit, because they protect stored data: `AllSlices`
calls the `Register` of every slice that has a `<Name>Slice`, and the architecture
test checks every event is registered. Rejected: an attribute on each event
(`[StoredAs("group_created")]`) found by a scan — it would remove the last
per-slice file, but leave nothing in the app wired up by name. (Named `AllSlices`,
not `Slices`: a class cannot share a name with the `ShareExpenses.Slices`
namespace.)

The architecture tests run every rule against the application *and* against
fixture slices in the test project — one conforming, several deliberately
violating — so a rule that silently stops firing fails the build instead of
passing vacuously.

### Anatomy of a state-change slice

Set by `CreateGroup`, the first slice built, and reshaped by the Wolverine port;
later slices follow it unless they have a reason not to.

| File | Visibility | Contents |
|---|---|---|
| `Events.cs` | public | the events this slice owns, as `sealed record`s |
| `<Name>Slice.cs` | public | only if the slice owns events or projections: `Register` |
| `State.cs` | public once an endpoint fetches it, internal before | what the slice folds, with an `Id` for Wolverine |
| `Decider.cs` | internal | `Command` and the pure `Decide` returning `Decision` |
| `Endpoint.cs` | public class | only once the slice has a screen: the Wolverine endpoints that render it and take its forms (§3); decides and maps `Decision` to a page, fragment or redirect; `OnException` for the 409 |
| `<Screen>.razor` | public (forced) | the slice's screen, rendered on the server (§3) |

A State Read slice has `Reader.cs` (internal `Query`, read model and pure `Read`)
in place of `Decider.cs`, and no events.

- **Wolverine does the fetch and the save.** `[WriteAggregate]` fetches the
  state with `FetchForWriting`; the endpoint returns `(IResult, Events)`, and
  Wolverine appends the events at the version it read and saves. A read slice over
  one stream would use `[ReadAggregate]`, which folds live; View homepage reads
  several, chosen by a lookup, so it folds each itself with `AggregateStreamAsync`.
  CreateGroup has no stream to fetch: it starts one on the session, under
  `[Transactional]`.
- **`Required = false` on every fetch:** a missing stream arrives as `null` and is
  answered by `Decide` or `Read`, so "no such group" and "not a member" (or
  "invite not found") give the same response, body included.
- **The group id comes from the route as a string**, resolved by the endpoint's
  `GroupStream` method (`FromMethod`). A malformed id becomes one no stream has,
  so it takes the unknown-group path. Wolverine's own typed-route parsing would
  answer a malformed id with an empty 404 — a different body from a missing group.
  The route parameter is `{group}`, not `{groupId}`: Wolverine names the resolved
  variable `groupId` after its type, and the two would collide in generated code.
- **Ids are generated in the endpoint and passed in**, so `Decide` is
  deterministic and spec tests can pin them. Ids are `Guid.CreateVersion7()`.
- **A form that creates something carries its new id, chosen when the form is
  shown** (a hidden field), so submitting it twice — a double tap, a retry, back
  and resubmit — creates one thing, not two: the second submit finds it exists and
  goes to it. The id comes from the client, so it is not trusted: a malformed one
  is replaced by a fresh id, and an existing one only ever leads to what the user
  could reach anyway. Set by New group (slice-01-create-group.md), followed by Add expense
  (slice-06-record-expense.md), which also keeps the ids recorded in its state so
  deciding can tell; Add member and Record settlement should follow it. Ids the server alone needs (the
  creator's member slot) stay chosen on submit.
- **`Decision` (in `Shared/`) is the decide result; each endpoint maps it to
  HTTP,** because the mapping differs per slice (AcceptInvite's 409 carries the
  way in). What can go wrong *after* deciding — a stream collision, a concurrency
  conflict — is an exception from Wolverine's save, answered by the endpoint's own
  `OnException`, so each slice keeps its own wording.
- **Side effects after the commit go in `AfterCommitAsync`.** InviteMember's email
  must only be sent once the invite is saved: the endpoint fills a `PendingEmail`
  (created by its `Load()`), and `AfterCommitAsync` sends it. Wolverine's `After`
  runs *before* the commit, and would email an invite that was never saved when the
  save loses a race.
- **Event types are registered with explicit stored names** (`group_created`),
  so a class or folder rename can never change what is in the database.
- **Naming: every slice folds a `State`; a read slice's output is its read model.**
  A slice's `State` is whatever it folds from the stream — what a decide function
  decides against, or what a read slice reads from (Emmett likewise uses
  `evolve`/state for both). A state-read slice's `Reader` then turns its `State`
  into the **read model** named in `event-model.yaml`, which is exactly what the
  screen receives, named `<Thing>ReadModel` (e.g. `HomepageReadModel`). The
  two differ whenever the answer depends on query inputs: View homepage folds each
  invited group's state — every open invite, its id and deadline; its read model is
  only the invites the user's address selects, at the current time. Event Modeling draws only the events and the read
  model — the state in between is an implementation detail. "View" is not used in
  code: Event Modeling and Marten use it as a synonym for read model, so a type
  named `View` that is *not* the read model invites confusion (tried briefly, and
  reverted for that reason). `Projection` is reserved for the process — a
  projection class, such as `GroupActivity`'s inline projection in View group.
- **Every slice's `State` carries a unique `[DocumentAlias("<slice>_state")]`.**
  Marten names a type by its bare class name, so two slices' `State` types
  collide on `ledger.state` — found while building InviteMember, where whichever slice was used
  second failed with a 500. The attribute, not `Schema.For<State>()` in
  `Register`: registering would make Marten treat a live-folded state as a stored
  document. Enforced by the architecture tests.
- **Tests per slice:** `<Name>Specs` mirror `slice-NN-*.md` line for line against
  `Decide` alone; `<Name>IntegrationTests` cover what needs a store (e.g. "created
  exactly once") and the HTTP mapping, against a throwaway PostgreSQL container
  (Testcontainers), never the development database. With no handler of our own to
  call, integration tests go through HTTP. Ids generated per request are read back
  from the response. A race is staged by `BeforeNextSave`, a store-wide Marten
  listener that runs a competing write just before the next save — including the
  sessions Wolverine opens.
- **Specs read as `Given(...).When(...).Then(...)` / `.ThenRejected(reason)` /
  `.ThenNotFound(reason)`.**
  `DecideSpec` runs a scenario against `Decide`; `ReadSpec` against a read slice's
  pure read. `Given()` is the empty stream. (`StreamSpec`, the same shape against a
  real stream through a slice's handler, went with the handlers.)
- **Rejections are typed, never matched by wording.** `Decision.Reject(reason)` is
  an invalid command (400); `Decision.NotFound(reason)` is a target that does not
  exist for this actor (404). Endpoints branch on the kind, so rewording a message
  cannot change an HTTP status; specs assert both the wording and the kind.
- **One test fold for every slice:** `Fold.Of<State>(history)` drives the state's
  `Create`/`Apply` methods by the same convention Marten uses. Stricter than Marten
  on purpose — an event the state has no method for throws unless the spec lists
  it in `ignoring:`, so a state cannot fall behind a new event silently.
- **Still repeated per slice, deliberately:** the `Decision`-to-HTTP mapping and
  the 409's `OnException`. Wolverine could take the 409 for every slice at once
  (`MapMartenConcurrencyFailuresToConflict`), at the cost of each slice's wording.
  The handler skeleton (fetch → decide → append → save → 409) that the hand-rolled
  slices repeated is what phase 2 removed. Other frameworks' answers, for reference:
  Marten's `WriteToAggregate`; Emmett's `CommandHandler` + `DeciderSpecification`;
  Eventuous's `CommandService` with `On<Cmd>().InState(...)`; Equinox's
  `Transact` — all a generic runner around a pure decide, plus typed errors.
- **User ids are `Guid`s.** Identity is keyed on `Guid` (`User : IdentityUser<Guid>`)
  because user ids are recorded in events and need a stable, typed shape.

### Typed ids

**Decision: `GroupId`, `MemberId` and `UserId` are distinct types, in code and in
`event-model.yaml`** — `readonly record struct`s wrapping a `Guid`, hand-written in
`Shared/Ids.cs`, plus `InviteId`, `ExpenseId` and `SettlementId`.

Rationale: events carry several ids side by side — `MemberClaimed(memberId,
userId)`, later `MemberClaimReleased(memberId, userId, releasedBy)` and an
expense's payer and participants. As bare `Guid`s, a swapped pair compiles and
silently corrupts the ledger. It also makes the decision that every `by` is a
`UserId` enforced rather than documented.

- **Invisible in storage.** A JSON converter writes each id as a bare Guid, so
  stored events and documents are exactly what plain `Guid`s would produce. The
  decision is therefore reversible without touching data. Tested.
- **Marten, verified by spike on 9.43:** typed ids work in event payloads, folded
  state (`FetchForWriting`), projected documents — as dictionary keys too — and
  LINQ (`==`, `Contains`) once registered with `RegisterValueType`. Stream ids are
  the exception: Marten takes a `Guid`, so stream calls pass `groupId.Value`.
- **Hand-written, not generated** (Vogen, StronglyTypedId): a handful of types,
  nothing hidden.
- **Identity stays on `Guid`**; `ClaimsPrincipal.UserId()` is the single
  conversion point.

### Who did it: `by` is a `UserId`

**Decision: the actor recorded on an event is the signed-in user, not their member
slot.** It is the true fact, and it stays true when a claim turns out to be wrong
and is released (§4) — recording the member slot would attribute an impostor's
actions to the slot's rightful owner, which is exactly what the activity feed
exists to expose. It is also consistent with `GroupCreated.createdBy`, which can
only be a user. Rendering a name means joining user → member through the claim
events, which the activity feed reads anyway.

---

## 13. Event model

The event model lives in `docs/event-model/event-model.yaml` and is refined slice
by slice, ahead of the code: a slice is modelled, its given-when-then specs written
in `slice-NN-*.md`, and only then built. The happy path is laid out in full;
slices not yet refined are marked `draft: true`. The model as a whole is a
timeline / swimlane covering:

1. The happy path end to end: create group → invite → claim → record expenses →
   view balances → settle up.
2. Each step laid out as **command → event(s) → read model → screen**.
3. Wireframe stubs per screen, to check that each read model actually serves one.
4. The awkward paths: correcting an expense after a settlement, removing a member
   mid-trip, claiming the wrong member slot, two phones writing at once. Not yet
   modelled.

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
the slice grows wider rather than taller — CreateGroup emits three events and occupies
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

**Decision: `event-model.yaml` is the source of truth; `happy-path.d2` and
`happy-path.png` are generated and must never be hand-edited.**

```
.venv/bin/python docs/event-model/generate.py
```

One command: it validates the model, writes the `.d2`, and renders the `.png`
with the d2 CLI (`d2 --pad 30`). `--no-png` skips rendering; a missing `d2` or a
failed render is an error, so a stale picture cannot pass silently.

Promoted from a deferred plan once the grid reached 80 cells with load-bearing
padding — past the point where hand-editing is safe. The generated `.d2` was
verified to render identically to the hand-built version before the switch.

**Structure: slices own the elements they introduce.** An element is *declared
once*, in mapping form with a `name`, at its first appearance in slice order;
every later appearance is a bare string reference. So fields are written exactly
once, and a misspelled reference is an undeclared name rather than a silently
created new element.

**Decision: shapes are declared once; sources are written per field — and never
on an event.** Every field says where its value comes from, so the generator can
check *information completeness*: that nothing on an event or a read model
appears from nowhere.

- **Screens** list their `fields` — what the screen holds, shows or takes in:
  typed, picked, or already there (the group being viewed, the slot tapped).
  **A screen field has a type and no source.** A screen is where values meet,
  not where they come from: on a State Read slice they come from the read model,
  on a State Change slice they go into the command. (Merged from separate
  `inputs` and `context` lists, and a State Read slice's own `query` block.)
- **Command fields** carry `source:`, classified by **trust**, not transport:
  - `client` (the default) — sent by the caller, typed or held by the screen.
    Untrusted: deciding validates it.
  - `system` — supplied by the server: the signed-in user, the clock, new ids.
  - `stream` — derived by deciding, from the folded stream. Trusted and
    consistent: the only kind an invariant may rest on.
  - `lookup` — read from outside the stream (Identity, plain documents). Trusted
    but possibly stale: it may feed guards, or propose candidates that deciding
    confirms against the stream (§11), but never an event field.

  **Server-supplied values stay with the command or read model they serve,**
  never on a screen: the signed-in user, the clock and a lookup are added by the
  server, out of the user's sight.

  `stream` vs `lookup` is the invariants-vs-guards line of §11, made visible.
  Route vs body vs link fragment is transport, not model: it is recorded in the
  slice's `.md` and enforced in code (e.g. a sign-in code only ever travels in a
  body, never a URL). The id that selects the stream is not a
  command or read model input either: it is a field of the screen, and the
  stream is fetched before deciding (by Wolverine, §12), so deciding receives the
  group as its folded state rather than as an input.
- **`feeds:`** lists the event fields a command field fills; a field always feeds
  same-named fields of the events its slice emits, so `feeds` lists only the
  exceptions (`createdBy` → `MemberAdded.by`).
- **Read model fields** carry `source:` — the events they are built from, or
  `system` / `lookup` for a value the server supplies to the read (the signed-in
  user, the clock, View homepage's `Invite` documents). Those are inputs to the read,
  not output, but listing them on the read model keeps every server-supplied value
  beside what it serves, as on a command.
- **Event fields carry a type only.** Where an event's values come from is not a
  property of the event: `MemberAdded.by` is `createdBy` when CreateGroup emits it
  and `by` when AddMember does. A source on the event's single declaration would
  be true for one emitting slice and false for the rest, so provenance is written
  on each slice's command, pointing forward. This is what lets an event be both
  declared once and filled differently per slice.

**Decision: checks look backward only — every value used is available from a step
before it.** Every field of every emitted event is fed by exactly one command
field, of the same type, and never by a `lookup`; every `feeds` target exists;
every `client` command field is a field of its screen, of the same type; every
field of a screen declared by a State Read slice is a field of its read model, of
the same type — the values a screen shows come from the read model; every read
model field is built from events the read model reads. Misspelled keys and unknown sources are errors; a source on an
event field is an error that explains why.

Whether a value is *used* afterwards is deliberately not checked: a command field
that feeds no event (an address for an email, a guard's lookup) is legitimate,
and declaring every such use (`stream: true`, `uses:`) was tried and dropped as
bookkeeping that caught nothing. Each rule was confirmed to fire by mutating a
copy of the model (`generate.py --check <copy>`).

The screen rule applies where a State Read slice *declares* the screen. A screen
declared by a State Change slice is about input; a read slice that reuses it
(Settle up, shared by Record settlement and View settlement plan) adds what it
shows, unchecked. Values a State Change screen holds (the group being viewed) are
not traced back to a read model yet.

**`draft: true`** marks a slice not yet refined. It keeps every structural check
but skips completeness, and is labelled "(draft)" on the diagram. Refining a slice
ends with removing the flag. On cards, a field's source is tagged unless it comes
from the client: `(sys)`, `(str)`, `(lku)`. Screen fields are never tagged.

Deferred: field-level checks inside composite types such as `MemberBalance[]`, and
tracing a State Change screen's held values back to a read model (above).

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
endpoints. The same mechanism will serve automation slices (`reads: GroupActivity`)
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
adding it to `GroupActivity`. That is the check predicted to be most valuable, and
it was.

Tooling note: the generator needs PyYAML, which macOS's system Python refuses to
install into (PEP 668), so it runs from a project-local `.venv`, gitignored. It is
a documentation tool, not application code.

---

## 14. Open questions

- **OPEN** Frontend — decided: server-rendered Razor Components in the slices, with
  htmx (§3). Built with the sign-in screen: the wiring (`Web/WebSetup.cs`:
  components, antiforgery and its 400 middleware, static files ahead of
  authentication, a redirect to `/sign-in` for screens asked for while signed out);
  the page shell and stylesheet (`Web/Page.razor`, `wwwroot/css/site.css`, phone
  first); htmx 2.0.4 served from `wwwroot`; the slice architecture rule allowing
  components, with a fixture. Conventions set by sign-in, for later screens to
  follow or revise:
  - **A plain form is the default; htmx where swapping in place pays.** Sign-in
    (two steps in one page) uses it; New group, one step that redirects on
    success, does not. Where a screen uses htmx it still works without: forms
    carry `method`/`action` as well as `hx-post`; an endpoint answers an htmx
    request with the fragment it swaps in, and a plain post with the whole page
    (`request.IsHtmx()`); success after a post redirects — `HX-Redirect` for htmx,
    a 302 otherwise. A rejection re-renders the page with what was typed, 200.
  - **A rejection shows in place,** under the form, as `role="alert"`, with the
    reason `Decide` gives.
  - **Screens are tested over HTTP** as a browser would use them: load the page,
    keep its cookies, post the form with the token it carries, with and without the
    `HX-Request` header; assertions on the HTML, decoded as the user reads it.
  - **Amounts on screen** go through `Web/Money`: minor units to the currency's
    decimals, with its symbol where it reads cleanly in a left-to-right line (£, €,
    $, R$), the ISO code otherwise (CHF, KWD). The only place minor units become
    decimals. **Amounts typed** go through `Money.TryParse`, its mirror: the currency's
    units with one `.` or `,` and at most the currency's decimals, no thousands
    separators (ambiguous); anything else is unreadable and deciding rejects it.
  - **One "not found" page** (`Web/NotFoundPage`) for anything missing or not
    yours: one 404, whatever the reason.
  - **A screen whose read model is polymorphic** (the group history) needs its
    subtypes public too; the architecture rule follows subtypes of contract types.
  Still to do:
  - **How a 409 asks to retry** in a form — with the first slice screen.
  - **Server gaps every screen hits:** a group list to land on after signing in
    (`UserGroups`, §11); each currency's decimal places for typing and showing
    amounts (`Shared/Currency.cs` has them; the screens can use it directly).
  Sign-in and joining need no state carried between pages or tabs: the user types
  an address, then the code, in the same page; after signing in they land on their
  invites. (An earlier link-based design had to keep an invite token across sign-in
  in `localStorage`; codes and address-matched invites removed that.)
  - **Friendly URLs — decide with the first screens.** The screens' routes are the
    addresses users see and share: `/groups/{guid}` until decided.
    Invite emails carry no ids. **Preferred, if short shareable addresses are wanted: a separate
    short public id** — e.g. `/groups/k3Xb9a` — carried on `GroupCreated` and
    resolved by a lookup document (short id → Guid, unique index), optionally with
    a decorative name slug after it (`/groups/k3Xb9a/lisbon-trip`, the id
    authoritative). The Guid stays the stream id and every internal reference; only
    the edge changes. The short id comes from a sequence or is random with a retry
    on collision, and its format is fixed forever once links exist. Members, and
    later expenses, need no global short id: a number within the group
    (`/members/4`) suffices.
    Considered: the Guid in a shorter encoding (base64url, 22 characters — still
    noise); slugs unique only among the user's own groups (no new id, but members
    get different URLs for one group, so links do not share); sqids of 6–8
    characters as the ids themselves (too small for random ids, so a sequence,
    string stream ids in Marten, and every id-carrying event reshaped); a Guid prefix
    (v7 Guids start with a timestamp, so prefixes collide).
  - **OPEN — sign-in code autofill.** Make the "paste code from email" flow work on
    Apple devices (iOS and macOS offer a code found in a recent Mail message as an
    AutoFill suggestion above the keyboard): the code input carries
    `autocomplete="one-time-code"`, with `inputmode="numeric"` and `maxlength="6"`
    for the numeric keypad and a clean paste — **done, in the sign-in screen's code
    step, and pinned by a test.** Still to do: detection in email is Apple's
    heuristic, not a standard, so the sign-in email should state the code plainly
    and early — e.g. "Your sign-in code is 123456", in the subject and the first
    line — with the mail relay (§4); then verify on a real iPhone and Mac. The log
    sender sends nothing to detect.
  - **Answered — several pending invites** (by View homepage). Signing in lands on
    the home screen, which lists every invite waiting, soonest deadline first, each
    with Join, above the user's groups; a single invite is shown the same way, and a
    single group is never jumped into. Joining one leaves the others — joining is
    per group — and goes straight to the group page. Still open: how a user already
    signed in learns of a new invite (the notifications task, below).
- **OPEN** Enforce consistency between `event-model.yaml` and the code, failing
  the build on any mismatch, so the model cannot drift from the code (§12, §13).
  For every non-draft slice:
  - **Events:** each event in the model exists as a public record in the slice
    that first emits it, with the same field names and types (typed ids
    included), and no extra fields.
  - **Commands:** the internal `Command` record carries the model's fields, with
    names and types matching. Fields with `source: system | stream | lookup`
    must not come from the request at all — neither route nor body; `source:
    client` fields must (where in the request is transport, per §13). Each
    `feeds` (explicit or by name) must hold in code: the command field's value
    ends up in that event field. This last part is the hard one — it needs either
    a convention the test can read, or a check that runs `Decide` with marked
    values and traces where they land.
  - **Specs:** every scenario in `slice-NN-*.md` has a test, and every spec test
    corresponds to a scenario. Needs a stable scenario id (the leading number) and
    a naming convention for tests (`S<n>_…`, already in use).
  - **Read models:** fields and types match the projected document (from
    ViewBalances).
  
  Mechanism: an xUnit test that reads the YAML and the `.md` files and reflects
  over the assembly, so `dotnet test` — and therefore the build gate — fails on a
  mismatch. Draft slices are skipped, as in the generator.
- **OPEN** Optional guardrail: only the *active* slice may change. Finished
  slices are settled code, and an AI agent working on slice N can easily "tidy"
  slice N−2 on the way past. A validation script — runnable by hand, as a
  pre-commit hook, or by an agent before finishing — compares the working tree
  (or a branch) against its base and fails if files outside the active slice
  changed. To decide:
  - **Declaring the active slice:** a flag in `event-model.yaml` (e.g.
    `active: true`, at most one), an argument to the script, or the branch name.
  - **What a slice owns:** `Slices/<Name>/`, `tests/…/Slices/<Name>/`, and
    `docs/event-model/slice-NN-*.md`.
  - **Shared files every slice touches** — `AllSlices.cs`, `event-model.yaml`, the
    generated `.d2`/`.png`, `ShareExpenses.http`, `spec.md`: allowed, but listed
    in the output so the change is visible; ideally only the active slice's
    section of the YAML may change.
  - **Everything else** (`Shared/`, `Infrastructure/`, other slices): a failure,
    unless overridden explicitly (e.g. `--allow Shared/Names.cs`), so cross-slice
    changes are deliberate and named — like the name-limit change to CreateGroup
    made while building AddMember.
  - Optional means advisory by default: it reports, and the caller decides
    whether to gate on it.
- **OPEN** Screens for the slices that lost their endpoint with the JSON API (§3):
  **Add member**, **Invite member**, **Settle up** (View settlement plan and Record
  settlement). Until then a group cannot gain members or settle; the group page's
  Settle up button points at a screen that does not exist, and nothing links to
  adding a member yet.
  Each comes back with a design pass, and with the behaviour its old endpoint had
  that the specs do not cover — in the git history: the invite's `Invite` document
  and email after commit (Invite member), and every write's concurrency 409.
  **New group** (Create group) is built: `/groups/new`, from Home. **Add expense**
  (Record expense) is built: `/groups/{group}/expenses/new`, from the group
  page, equal split first; shares and exact follow as a second and third pass over
  the same screen (slice-06-record-expense.md, "Prepared for the other split
  modes").
- **OPEN** Default currency from the browser's locale. The New group form
  preselects DKK for everyone. Better: guess from the request — the
  `Accept-Language` header's first region (`da-DK` → DKK, `en-GB` → GBP), mapped
  through .NET's `RegionInfo.ISOCurrencySymbol`, falling back to DKK when there is
  no region (`en`), the region's currency is not one the app accepts, or the header
  is absent. To decide: whether a language without a region guesses a country
  (`da` → DK), and whether the last currency the user chose for a group should win
  over the locale. Server-side only; no script needed.
- **OPEN** Slice: **Decline invite.** An invitee can say no, rather than leave the
  invite on their home page until its deadline. State Change, from the home page,
  beside Join. Sketch, to refine as a slice:
  - **Command** `DeclineInvite(now, userId)` + looked up `invitedAs`, for the group in
    the route — found exactly as AcceptInvite finds the slot: the lookup proposes,
    the stream confirms the slot's current, unexpired invite (§11).
  - **Event** `InviteDeclined(memberId, inviteId, by)`: the invite is closed and the
    slot is a placeholder again; its `Invite` document is deleted in the same
    transaction, so the address is erased (§3). The slot may be invited again — the
    same address included.
  - **Folds to update:** AcceptInvite and View homepage (the invite is no longer
    open), View balances (the slot's status), and later the activity feed and
    notifications ("Bob declined your invite to Lisbon trip" — telling the inviter
    is what makes declining worth more than ignoring).
  - **To decide:** whether `by` — a user who is not a member — belongs on a group's
    event (it is pseudonymous, and `MemberClaimed` already records a non-member
    becoming one); whether declining needs a confirmation step on the screen; what
    a member who declines an invite into their own group gets (the home page never
    shows such invites).
- **OPEN** Explore user notifications: telling a person about something that
  concerns them, without their having to look. Examples: "Bob accepted your invite
  to Lisbon trip"; "could not send the invite to Bob — check the address for
  typos"; "Bob changed your expense Dinner from £120 to £140". §1 rules out *push*
  notifications for v1, not notifications as such. To explore:
  - **Who is told:** the person an event concerns — the inviter when an invite is
    claimed, the recorder or payer when an expense is corrected, the parties to a
    settlement — never the actor themselves. Needs a rule per event; the slot→user
    join already exists in the claim events.
  - **Channel:** in-app first (a list, an unread count), email for some (the
    sign-in mail path exists), push not in v1. Per-user preferences, and
    batching — a busy evening must not send ten emails.
  - **Model:** an Automation slice (spec §13: recognised, no layout yet) reacting to
    events, and a per-user read model (`Notifications`, async, multi-stream — the
    first true one, since `UserGroups` became per group). Read/unread is supporting state, not ledger: a plain
    document (§3). Most notifications are a filtered activity feed (§11
    `ActivityFeed`) addressed to one person, so design the two together.
  - **Failed delivery** is different: "could not send" is not a domain event but an
    outcome of the mail relay, which today only logs (InviteMember). It depends on
    the transactional outbox and delivery status on `Invite` (deferred above), and a
    relay that reports bounces (§4). Typos often surface only as a bounce, minutes
    later.
  - **Corrections need the old value**, which correction events deliberately do not
    carry (§11): the notification is composed from the fold, which holds the state
    before the event — as the activity feed does.
- **OPEN** Give State Read screens the fields they show. Today the read slices'
  screens (Balances) declare at most a `groupId`, and Settle up only
  its command inputs; what each screen displays lives only in its wireframe. List,
  per screen, the read-model fields it needs — group name and currency, each
  member's name, status and balance, the history, the plan's transfers — so the
  generator's read-screen check (§13) proves the read model serves the screen, not
  just that it has no stray fields. To decide along the way:
  - **Composite fields:** a screen shows parts of `MemberBalance[]` or
    `PendingInvite[]`. Either list the composite (`members: MemberBalance[]`), or
    reach inside it — which needs the deferred field-level checks inside composite
    types (below) and a notation for them.
  - **Shared screens:** Settle up is declared by Record settlement (inputs) and
    reused by View settlement plan (display). Its displayed fields are unchecked
    today (§13); a way to say "these fields come from this read model" on a shared
    screen, or a screen split, is needed for the check to reach them.
  - **Wireframe and fields together:** a card shows its wireframe instead of its
    fields; decide whether listing fields changes what the diagram draws.
- **OPEN** Move the `slice-NN-*.md` specs into `event-model.yaml`, so each slice's
  given-when-thens sit next to its shape and the generator can check them: every
  event in a scenario is one the slice emits or reads, every payload matches the
  event's declared fields, and every command matches the command's fields. This
  would also replace the `.md` side of the consistency check above, and drop the
  slice numbers from file names.
  **Precondition: define the format first, before moving any spec.** Plain
  strings copied over from the `.md` files would be just as unchecked in YAML. To
  decide:
  - **What a spec is:** a stable id (tests are named `S<n>_…`), a title, given,
    when, and then. Then is one of: events, `rejected` or `not found` (the typed
    rejections, §12), or a read model for State Read slices (`ReadSpec`) — the
    read slices' query inputs, including `now`, need a place in the when.
  - **Event payloads:** named fields (`MemberAdded: {memberId: m2, displayName:
    Bob, by: alice}`) are checkable but long; positional ones
    (`MemberAdded(m2, "Bob", alice)`) read like the current specs but rely on
    field order. Possibly positional in the source, checked against declared field
    order.
  - **Symbolic values:** ids and actors (`g1`, `m2`, `alice`), system-supplied
    values (new ids, `now`), and placeholders
    such as `<51 characters>` — what each one means and how a test turns it into
    a real value.
  - **Shared givens:** the "unless stated otherwise" background, and extending it
    (`GIVEN ... AND …`) versus replacing it (`GIVEN (empty stream)`).
  - **Prose:** each `.md` also has notes, rules deferred to later slices, the
    endpoint, and concurrency notes. These need a home in the YAML (free-text
    fields) or stay in a slimmer `.md`. Decide which before moving anything, so
    the reasoning is not lost along the way.
  - **The diagram:** specs stay off the grid (§13: a wide text block wrecks
    alignment). Either the generator ignores them, or it renders them separately.
  Once the format is settled, move every non-draft slice in one go. Slices refined
  after that write their specs straight into the YAML.
- **DEFERRED** Transactional outbox for emails. Invite and sign-in emails are
  sent after the commit, best-effort: a transient relay failure loses the email,
  though the invite stands (the invitee can sign in with the address regardless)
  and a sign-in code can be requested again. An outbox would record "send this
  email" in the same transaction as the event and the `Invite` document, and a
  background worker would deliver and retry it — `Invite` is the natural place for
  delivery status. Revisit with the choice of relay (§4). Wolverine's durable
  outbox does exactly this, but it runs in mediator-only mode today (no
  inbox/outbox); adopting it means switching durability mode, and the email becomes
  a message handled after the commit. A superseded invite needs no special care:
  the worker can check its `Invite` still exists before sending.
- **DEFERRED** Completeness inside composite read model types (`MemberBalance[]`),
  and tracing the values a State Change screen holds back to a read model (§13).
- **DEFERRED** Email change, several addresses per account, and account merging.
  An account *is* its address (§4), and invites are claimed by address — so an
  invite to an old or second address can today only be fixed by re-inviting. A
  signed-in user proving a second address (with a code sent to it) would let them
  claim invites sent there, but raises the rest: which address signs in, what
  happens when two accounts turn out to be one person.
- **DEFERRED** Transactional relay — shortlisted in §4; `LogEmailSender` until then.
- **DEFERRED** `PeriodClosed` / stream archival, until a stream is actually long.
- **OPEN** Wolverine's code in production: pre-generate it (`codegen write`,
  `TypeLoadMode.Static`) instead of compiling it with Roslyn at startup? Its tables
  are settled: none in mediator-only mode (§3, Hosting consequences), checked by
  booting outside Development on an empty database and recording a write
  (`StartupMigrationTests`).
- **OPEN** `/health` checks that both databases answer, not that the schema is there.
  With the startup migration it only answers once the schema is applied, which makes
  it true; whether it should also check the schema is left until it is seen to lie.
- **OPEN** Production will not start yet: no mail relay (§4) and `App:PublicOrigin`
  unset, both by design until chosen. The first boot of an empty database also logs
  one `fail:` from EF, probing the migrations table it is about to create; it is not a
  failure.
- **DEFERRED** Frozen settle-up plan, unless a shifting plan bites in practice.
- **DEFERRED** Marten-backed `IUserStore`, if EF Core and Marten genuinely chafe.
- **DEFERRED** `MemberMergedInto` for duplicate slots — out of scope for v1.
- **DEFERRED** `MemberClaimReassigned`, unless a real handover happens.
