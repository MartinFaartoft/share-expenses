# Slice 5 — `ClaimMember`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Sign in / claim — "Sign me in" on the invite landing page, after signing in |
| Command | `ClaimMember(groupId, token, now, userId)`; the slot is found by deciding |
| Events | `MemberClaimed` (owned by slice 1) |
| Also writes | deletes the slot's `InviteDelivery` — the address is not kept once claimed |
| Code | `src/ShareExpenses/Slices/ClaimMember/` |
| Endpoint | `POST /api/invites/{groupId}/claim`, body `{ "token": "…" }` — sign-in required |

The invite binds the slot (spec §4): whoever holds a live link may claim the slot
it was issued for, and the signed-in address need not match the invited one.
Claiming is what turns a placeholder into a member — the right to change the group.

## Specifications

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
    WHEN   ClaimMember(g1, t1, bob)  at t0+1d
    THEN   MemberClaimed(m2, bob)

2 - a forwarded link works for whoever holds it
    WHEN   ClaimMember(g1, t1, carol)  at t0+1d
    THEN   MemberClaimed(m2, carol)

3 - a wrong token claims nothing
    WHEN   ClaimMember(g1, tX, bob)  at t0+1d
    THEN   rejected (not found) - invite not found

4 - an unknown group
    GIVEN  (empty stream)
    WHEN   ClaimMember(g1, t1, bob)  at t0+1d
    THEN   rejected (not found) - invite not found

5 - a link is dead at its deadline
    WHEN   ClaimMember(g1, t1, bob)  at t0+30d
    THEN   rejected (not found) - invite not found
    (and at t0+30d less one second: claimed)

6 - a superseded link claims nothing
    GIVEN  ... AND MemberInvited(m2, h2, t0+31d, alice)
    WHEN   ClaimMember(g1, t1, bob)  at t0+1d
    THEN   rejected (not found) - invite not found

7 - a used link claims nothing
    GIVEN  ... AND MemberClaimed(m2, carol)
    WHEN   ClaimMember(g1, t1, dave)  at t0+1d
    THEN   rejected (not found) - invite not found

8 - a member cannot claim a second slot
    GIVEN  ... AND MemberAdded(m3, "Bobby", alice) AND MemberInvited(m3, h2, t0+30d, alice)
    WHEN   ClaimMember(g1, t2, alice)  at t0+1d
    THEN   rejected (already a member) - you're already in this group as Alice

9 - tapping the link again after joining
    GIVEN  ... AND MemberClaimed(m2, bob)
    WHEN   ClaimMember(g1, t1, bob)  at t0+1d
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
  plain supporting state shared by slices 3 and 5.
- **Concurrency:** two people with one forwarded link — one save wins, the other
  gets 409, and its retry finds the invite used: 404.
- `MemberClaimed(memberId, userId)` needs no `by`: the claiming user *is* the actor.

## Deferred to the slices that introduce the events

- **Archived group** rejects the claim — with `GroupArchived`.
- **A removed slot's** invite is dead — with `MemberRemoved`.
- **A released claim** frees the user to claim again, and the slot to be re-invited —
  with `MemberClaimReleased`.
