# Slice 5 — `AcceptInvite`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Your invites — "Join" on one of them |
| Command | `AcceptInvite(now, userId)` + looked up: `invitedAs`; the slot is found by deciding |
| Events | `MemberClaimed` (owned by CreateGroup) |
| Also writes | deletes the slot's `Invite` — the address is not kept once claimed |
| Code | `src/ShareExpenses/Slices/AcceptInvite/` |
| Endpoint | `POST /api/invites/{group}/accept`, no body — sign-in required |

The invite binds the slot, and the invited address is the key (spec §4): a user
signed in with that address — proven by a code sent to it — may claim the slot.
Claiming is what turns a placeholder into a member — the right to change the group.

**Why the slice is "Accept invite" but the event is `MemberClaimed`.** The command
names the user's intention — Bob accepts an invite. The event names the resulting
fact about the group — a user now holds this slot. CreateGroup records the same
fact for the creator, who accepted no invite, so the event keeps the name that is
true for both, and so does its reversal, `MemberClaimReleased`. Which invite was
accepted is told by the `MemberInvited` before it; nothing needs a second event.

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13).

`i1`, `i2` are invite ids. The lookup is written `WITH`: `invitedAs` is the
`Invite` documents in this group addressed to the user's account email. Times are
relative to `t0`. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice)
  AND  MemberInvited(m2, i1, t0+30d, alice)
```

```
1 - claims the slot invited at the user's address
    WITH   invitedAs = {i1}
    WHEN   AcceptInvite(bob)  at t0+1d
    THEN   MemberClaimed(m2, bob)

2 - another address claims nothing
    WITH   invitedAs = {}
    WHEN   AcceptInvite(carol)  at t0+1d
    THEN   rejected (not found) - invite not found

3 - an unknown group
    GIVEN  (empty stream)
    WITH   invitedAs = {i1}
    WHEN   AcceptInvite(bob)  at t0+1d
    THEN   rejected (not found) - invite not found

4 - an invite is dead at its deadline
    WITH   invitedAs = {i1}
    WHEN   AcceptInvite(bob)  at t0+30d
    THEN   rejected (not found) - invite not found
    (and at t0+30d less one second: claimed)

5 - only the slot's current invite counts
    GIVEN  ... AND MemberInvited(m2, i2, t0+31d, alice)
    WITH   invitedAs = {i1}
    WHEN   AcceptInvite(bob)  at t0+1d
    THEN   rejected (not found) - invite not found

6 - a claimed slot's invite is used up
    GIVEN  ... AND MemberClaimed(m2, carol)
    WITH   invitedAs = {i1}
    WHEN   AcceptInvite(bob)  at t0+1d
    THEN   rejected (not found) - invite not found

7 - a member cannot claim a second slot
    GIVEN  ... AND MemberAdded(m3, "Bobby", alice) AND MemberInvited(m3, i2, t0+30d, alice)
    WITH   invitedAs = {i2}
    WHEN   AcceptInvite(alice)  at t0+1d
    THEN   rejected (already a member) - you're already in this group as Alice

8 - joining again after joining
    GIVEN  ... AND MemberClaimed(m2, bob)
    WITH   invitedAs = {}
    WHEN   AcceptInvite(bob)  at t0+1d
    THEN   rejected (already a member) - you're already in this group as Bob

9 - two slots invited at one address: the newest invite wins
    GIVEN  ... AND MemberAdded(m3, "Bobby", alice) AND MemberInvited(m3, i2, t0+31d, alice)
    WITH   invitedAs = {i1, i2}
    WHEN   AcceptInvite(bob)  at t0+1d
    THEN   MemberClaimed(m3, bob)
```

## Notes

- **The invariant: one user holds at most one slot per group** (spec §11). Checked
  against the stream only — no lookups — and *before* any invite, so no invite can
  give a member a second slot. Concurrency closes the race: two claims decided
  against the same version cannot both save.
- **The lookup proposes; the stream decides.** `invitedAs` only narrows which
  invites deciding considers. The claimed slot is one whose *current* invite — its
  newest `MemberInvited` — is among them, unclaimed and unexpired. A stale or
  leftover `Invite` document (scenario 5) claims nothing. This is the one place a
  lookup shapes an event: it never supplies `MemberClaimed`'s values, but it does
  authorise them (spec §11).
- **Scenario 9 should not happen** — InviteMember's guard refuses a second slot for
  an address with an open invite — but the guard works from a lookup and may be
  stale. Rather than refuse, deciding takes the newest invite: the most recent
  intention of the group.
- **Order of checks:** group exists, then membership, then the invites. Every
  dead invite — unknown group, another address, superseded, used, expired,
  malformed group id — is the same 404 `invite not found`.
- **Already a member** is 409 with `groupId` and `memberId` in the body, so the
  client can go straight to the group. Safe to say: the caller is a member.
- **Expiry** is the recorded deadline: claimable while `now < expiresAt`.
- **`Invite` is deleted** in the same transaction as the claim: the address served
  to bind the slot until claimed, and nothing needs it after. The document lives in
  `Infrastructure/`, as plain supporting state shared by InviteMember, View invites
  and AcceptInvite.
- **Concurrency:** two claims on one slot — one save wins, the other gets 409, and
  its retry finds the invite used: 404.
- `MemberClaimed(memberId, userId)` needs no `by`: the claiming user *is* the actor.

## Deferred to the slices that introduce the events

- **Archived group** rejects the claim — with `GroupArchived`.
- **A removed slot's** invite is dead — with `MemberRemoved`.
- **A released claim** frees the user to claim again, and the slot to be re-invited —
  with `MemberClaimReleased`.
