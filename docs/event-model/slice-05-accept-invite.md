# Slice 5 — `AcceptInvite`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Sign in / claim — "Sign me in" on the invite landing page, after signing in |
| Command | `AcceptInvite(token, now, userId)`; the slot is found by deciding |
| Events | `MemberClaimed` (owned by CreateGroup) |
| Also writes | deletes the slot's `InviteDelivery` — the address is not kept once claimed |
| Code | `src/ShareExpenses/Slices/AcceptInvite/` |
| Endpoint | `POST /api/invites/{group}/accept`, body `{ "token": "…" }` — sign-in required |

The invite binds the slot (spec §4): whoever holds a live link may claim the slot
it was issued for, and the signed-in address need not match the invited one.
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

`h1`, `h2` are the hashes of tokens `t1`, `t2`; `tX` matches nothing. Times are
relative to `t0`. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice)
  AND  MemberInvited(m2, h1, t0+30d, alice)
```

```
1 - claims the invited slot
    WHEN   AcceptInvite(t1, bob)  at t0+1d
    THEN   MemberClaimed(m2, bob)

2 - a forwarded link works for whoever holds it
    WHEN   AcceptInvite(t1, carol)  at t0+1d
    THEN   MemberClaimed(m2, carol)

3 - a wrong token claims nothing
    WHEN   AcceptInvite(tX, bob)  at t0+1d
    THEN   rejected (not found) - invite not found

4 - an unknown group
    GIVEN  (empty stream)
    WHEN   AcceptInvite(t1, bob)  at t0+1d
    THEN   rejected (not found) - invite not found

5 - a link is dead at its deadline
    WHEN   AcceptInvite(t1, bob)  at t0+30d
    THEN   rejected (not found) - invite not found
    (and at t0+30d less one second: claimed)

6 - a superseded link claims nothing
    GIVEN  ... AND MemberInvited(m2, h2, t0+31d, alice)
    WHEN   AcceptInvite(t1, bob)  at t0+1d
    THEN   rejected (not found) - invite not found

7 - a used link claims nothing
    GIVEN  ... AND MemberClaimed(m2, carol)
    WHEN   AcceptInvite(t1, dave)  at t0+1d
    THEN   rejected (not found) - invite not found

8 - a member cannot claim a second slot
    GIVEN  ... AND MemberAdded(m3, "Bobby", alice) AND MemberInvited(m3, h2, t0+30d, alice)
    WHEN   AcceptInvite(t2, alice)  at t0+1d
    THEN   rejected (already a member) - you're already in this group as Alice

9 - tapping the link again after joining
    GIVEN  ... AND MemberClaimed(m2, bob)
    WHEN   AcceptInvite(t1, bob)  at t0+1d
    THEN   rejected (already a member) - you're already in this group as Bob
```

## Notes

- **The invariant: one user holds at most one slot per group** (spec §11). Checked
  against the stream only — no lookups — and *before* the token, so no link can
  give a member a second slot. Concurrency closes the race: two claims decided
  against the same version cannot both save.
- **Order of checks:** group exists, then membership, then the token. Every dead
  link — unknown group, wrong token, superseded, used, expired, malformed group
  id, missing token — is the same 404 `invite not found`, as on the landing page.
- **Already a member** is 409 with `groupId` and `memberId` in the body, so the
  client can go straight to the group. Safe to say: the caller is a member.
- **The token** travels in the body, read client-side from the link's fragment and
  kept across the sign-in round trip (e.g. `sessionStorage`). Never in a URL.
- **Expiry** is the recorded deadline: claimable while `now < expiresAt`.
- **`InviteDelivery` is deleted** in the same transaction as the claim: the address
  served only to send the invite and to guard against double invites, and neither
  needs it once the slot is claimed. The document lives in `Infrastructure/`, as
  plain supporting state shared by InviteMember and AcceptInvite.
- **Concurrency:** two people with one forwarded link — one save wins, the other
  gets 409, and its retry finds the invite used: 404.
- `MemberClaimed(memberId, userId)` needs no `by`: the claiming user *is* the actor.

## Deferred to the slices that introduce the events

- **Archived group** rejects the claim — with `GroupArchived`.
- **A removed slot's** invite is dead — with `MemberRemoved`.
- **A released claim** frees the user to claim again, and the slot to be re-invited —
  with `MemberClaimReleased`.
