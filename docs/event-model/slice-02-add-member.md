# Slice 2 — `AddMember`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Add member — the people in the group, and a form to add one, with an email to invite them at once |
| Command | `AddMember(displayName, email?, by)` + `memberId`, `inviteId`, `now`; looked up, only with an email: `emailHolder`, `invitedTo` |
| Events | `MemberAdded` (owned by CreateGroup); `MemberInvited`, when an email is given |
| Also writes | `Invite` — plain document binding the slot to the address, when invited |
| Code | `src/SplitIt/Slices/AddMember/` |
| Endpoints | `GET /groups/{group}/members/new` — the screen; `POST /groups/{group}/members` — its submit; sign-in required |

Adds a member by name (spec §4). Without an email it is a **placeholder**: someone
expenses can be recorded against immediately, who is invited and claims the slot
later. With one, the member is added **and invited** in the same command: the
screen offers both together, because the common case is a person the group wants
in now. **`MemberInvited` is owned by this slice** — it is the first in the model
to emit it (spec §12) — and InviteMember, which invites a placeholder added
without an email, uses it.

Adding with an email is one command, not two chained: either both events are
recorded or neither, so there is never a member whose invite was refused.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13).

Unless stated otherwise, every scenario starts from the group as CreateGroup leaves it:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice)
  AND  MemberClaimed(m1, alice)
```

```
1 - adds a placeholder member, by name alone
    WHEN   AddMember("Bob", alice)
    THEN   MemberAdded(m2, "Bob", alice)

2 - any member may add, and is recorded as the actor
    GIVEN  ... AND MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
    WHEN   AddMember("Carol", bob)
    THEN   MemberAdded(m3, "Carol", bob)

3 - the group must exist
    GIVEN  (empty stream)
    WHEN   AddMember("Bob", alice)
    THEN   rejected - group not found

4 - only members of the group may add
    WHEN   AddMember("Bob", mallory)
    THEN   rejected - group not found

5 - an unclaimed placeholder confers no membership
    GIVEN  ... AND MemberAdded(m2, "Bob", alice)
    WHEN   AddMember("Carol", bob)
    THEN   rejected - group not found

6 - rejects a blank name
    WHEN   AddMember("   ", alice)
    THEN   rejected - name is required

7 - rejects a name already used in the group
    WHEN   AddMember(" alice ", alice)
    THEN   rejected - a member with that name already exists

8 - rejects a name longer than 50 characters
    WHEN   AddMember(<51 characters>, alice)
    THEN   rejected - name must be at most 50 characters

9 - trims the name
    WHEN   AddMember("  Bob ", alice)
    THEN   MemberAdded(m2, "Bob", alice)

10 - with an email, adds and invites in one
    WHEN   AddMember("Bob", "bob@example.com", alice)
    THEN   MemberAdded(m2, "Bob", alice)
           MemberInvited(m2, i1, t0+30d, alice)

11 - a blank email is no email
    WHEN   AddMember("Bob", "   ", alice)
    THEN   MemberAdded(m2, "Bob", alice)

12 - an email that is not plausibly one adds no one
    WHEN   AddMember("Bob", "bob", alice)
    THEN   rejected - email is not a valid address

13 - the address of a user already in the group adds no one
    WITH   emailHolder(alice@example.com) = alice
    WHEN   AddMember("Bob", "alice@example.com", alice)
    THEN   rejected - alice@example.com has already joined as Alice

14 - an address with an open invite on another slot adds no one
    GIVEN  ... AND MemberAdded(m2, "Bobby", alice) AND MemberInvited(m2, i0, …, alice)
    WITH   invitedTo(bob@example.com) = {m2}
    WHEN   AddMember("Bob", "BOB@example.com", alice)
    THEN   rejected - that email is already invited as Bobby

15 - an invite to a slot since claimed no longer holds its address
    GIVEN  ... AND MemberAdded(m2, "Bobby", alice) AND MemberInvited(m2, i0, …, alice)
           AND MemberClaimed(m2, bob)
    WITH   invitedTo(bob@example.com) = {m2}, emailHolder(bob@example.com) = none
    WHEN   AddMember("Bob", "bob@example.com", alice)
    THEN   MemberAdded(m3, "Bob", alice)
           MemberInvited(m3, i1, t0+30d, alice)

16 - the name is checked before the email
    WHEN   AddMember("", "bob", alice)
    THEN   rejected - name is required

17 - a member id already added is not added twice
    GIVEN  ... AND MemberAdded(m2, "Bob", alice)
    WHEN   AddMember(m2, "Bob", alice)
    THEN   rejected (already recorded) - member already added

18 - an archived group takes no new members
    GIVEN  ... AND GroupArchived(alice)
    WHEN   AddMember("Dave", alice)
    THEN   rejected - group is archived
```

`i0`, `i1` are invite ids (`i1` the new invite's, chosen by the endpoint); every
command is issued at `t0`. Lookups are written `WITH`, as in InviteMember: they come
from outside the stream and feed guards, not invariants. `m2` is the id the endpoint
chose for the new member; scenario 17 passes the same one twice.

## The screen

- **Reached from the group page:** an **Add member** link beside Balances, and from
  the Add expense form's "shared between" list ("Someone missing? Add a member") —
  addresses in other slices' screens, not dependencies on this one.
- **The people in the group,** in member-added order, above the form: each name, and
  a status — **You** (the signed-in user's slot), **Joined** (claimed by someone
  else), **Invited** (an invite in force), **Invite expired**, or **Not invited**.
  It makes a duplicate name visible before it is rejected (scenario 7), is the
  confirmation after adding, and is where InviteMember's screen hangs its
  **Invite** link — on *Not invited* and *Invite expired* rows, and **Re-invite** on *Invited* ones.
- **Two fields:**
  - **Name** — `required`, `maxlength` 50 (a hint: the decider counts visible
    characters, `maxlength` UTF-16 units), autofocus. Hint: "What the others in the
    group see them as."
  - **Email (optional)** — `type="email"`, `autocomplete="off"`. Hint: "Give an
    email to invite them now. Without one they are added and can be invited later."
    The button reads **Add member** either way.
- **Add member** posts the form — a plain form, no htmx. Added (with or without an
  invite): **the same screen again** (302), the new person in the list, the form
  empty — the natural next thing is adding the next person. Rejected: the screen
  again, **with what was typed** and the reason under the form (`role="alert"`),
  200. A non-member, a missing group and a malformed id get the one not-found page
  (404), on the screen and on the submit alike.
- **A Back link** to the group page.

## Notes

- **Scenarios 3, 4 and 5 give the same answer on purpose.** A non-member must not
  be able to tell a group that exists from one that does not, so "no such group"
  and "not a member" are indistinguishable — over HTTP both are 404, as is a
  malformed group id. Membership is checked before any input, so a non-member
  learns nothing from validation messages either.
- **Being added is not being a member** (scenario 5). A placeholder is a slot in
  the ledger; membership — the right to change the group — comes from holding a
  claim on a slot.
- **Names** are trimmed, compared case-insensitively for duplicates, and limited
  to 50 visible characters after trimming. Rejection wording is per context:
  "name" here, "your name" in CreateGroup.
- `by` is the signed-in user, never a member slot (spec §12).
- **Order of checks:** membership, then archived, then the id (already added), then the name, then —
  only when an email is given — the email and its guards. A non-member gets `group not
  found` whatever else is wrong.
- **Why a rejected invite adds no one.** The alternative, adding the placeholder and
  reporting that the invite failed, leaves a member the form cannot be corrected
  against: sending it again is refused as a duplicate name. Nothing is lost by
  refusing both — the form comes back as typed — and the invite can always be sent
  later from the list.
- **The invite rules are InviteMember's,** not a copy of them: the email check and the
  two guards are one pure function in `Shared/`, called by both slices, so the rules
  cannot drift apart. The 30-day lifetime moves there with them. InviteMember's
  scenarios stand unchanged; scenarios 10–15 above are the same rules reached from
  here. The lookups are the same too: Identity for `emailHolder`, `Invite` documents
  for `invitedTo`, both only when an email is given.
- **State grows** to what the screen and the guards need: each slot's name, whether
  it is claimed and its newest invite's deadline; which slot each user holds; the
  group's name (the Back link, the invite email). Slots are no longer just names'
  keys, so `NameKeys` is derived from them.
- **Submitting twice adds one member.** As Add expense: the new member's id is chosen
  when the form is *shown* and carried in a hidden field, and deciding knows the slots
  it has seen (scenario 17), so a double tap, a retry or the back button goes to the
  screen as if it had added them. The id comes from the client, so it is not trusted:
  a malformed one is replaced by a fresh id. The invite's id is the server's alone.
- **With an email,** the endpoint also stores the slot's `Invite` document — in the
  same transaction as the events, its id the event's `inviteId` — and **sends the
  invite email after the commit**, and only if the save succeeded; a send failure is
  logged (ids, not the address) and the invite stands. The email names the group, the
  inviter (the actor's slot) and the new member, and links to the app — no secret
  (slice-03-invite-member.md).
- **Antiforgery** on the submit (`[ValidateAntiforgery]`, spec §3).
- **A concurrent save** answers 409, as Add expense.

## Deferred to the slices that introduce the events

- **A released claim ends membership** (R2) — with `MemberClaimReleased`.
- **A removed member's name may be reused** (R5) — with `MemberRemoved`.
- **A renamed member's old name is free and new name taken** (R5) — with
  `MemberRenamed`.

## Concurrency

Two phones adding at once: both decide against the same stream version, the
first save wins, the second gets **409 Conflict** and the client retries — at
which point the rules run against the new state, so a duplicate name is then
rejected properly. Server-side retry was considered and deferred. On the screen the
409 is the bare response, as Add expense (the open "409 retry UX" task, spec §14).
