# Slice 4 — View invites

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Your invites ("Alice invited you to Lisbon trip as Bob — [Join]") |
| Read model | `PendingInvitesReadModel` — **live**: each invited group's stream folded per request, nothing stored |
| Query | `ViewInvites(userId, now)` + looked up: `invites`, the `Invite` documents addressed to the user's account email |
| Code | `src/ShareExpenses/Slices/ViewInvites/` |
| Endpoint | `GET /api/invites` — sign-in required |

The invites waiting for the signed-in user: who invited them, to which group, as
whom — each with "Join", which claims the slot (AcceptInvite). Shown after signing
in, so an invitee who signs in with the invited address lands here.

## Specifications

Each scenario lists the streams of the groups involved. `i1`, `i2` are invite ids.
The lookup is written `WITH`: `invites(bob)` is the `Invite` documents addressed to
bob's account email, as `group: invite`. Times are relative to `t0`. Unless stated
otherwise, every scenario starts from:

```
GIVEN  g1: GroupCreated(g1, "Lisbon trip", "GBP", alice)
       AND MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
       AND MemberAdded(m2, "Bob", alice)
       AND MemberInvited(m2, i1, t0+30d, alice)
WITH   invites(bob) = { g1: i1 }
```

```
1 - shows a live invite
    WHEN   ViewInvites(bob)  at t0+1d
    THEN   [ { group: g1, groupName: "Lisbon trip", memberName: "Bob", invitedBy: "Alice" } ]

2 - nobody invited this address
    WITH   invites(carol) = {}
    WHEN   ViewInvites(carol)  at t0+1d
    THEN   []

3 - an invite into an unknown group is not shown
    GIVEN  g1: (empty stream)
    WHEN   ViewInvites(bob)  at t0+1d
    THEN   []

4 - an invite is dead at its deadline
    WHEN   ViewInvites(bob)  at t0+30d
    THEN   []
    (and at t0+30d less one second: shown)

5 - only the slot's current invite counts
    GIVEN  g1: ... AND MemberInvited(m2, i2, t0+31d, alice)
    WHEN   ViewInvites(bob)  at t0+1d
    THEN   []

6 - a claimed slot's invite is used up
    GIVEN  g1: ... AND MemberClaimed(m2, carol)
    WHEN   ViewInvites(bob)  at t0+1d
    THEN   []

7 - an invite into a group the user is already in is not shown
    GIVEN  g1: ... AND MemberAdded(m3, "Bobby", alice) AND MemberClaimed(m3, bob)
    WHEN   ViewInvites(bob)  at t0+1d
    THEN   []

8 - the inviter is named by their slot in that group
    GIVEN  g1: ... AND MemberAdded(m3, "Carol", alice) AND MemberClaimed(m3, carol)
               AND MemberInvited(m2, i2, t0+30d, carol)
    WITH   invites(bob) = { g1: i2 }
    WHEN   ViewInvites(bob)  at t0+1d
    THEN   [ { g1, "Lisbon trip", "Bob", "Carol" } ]

9 - invites from several groups, soonest deadline first
    GIVEN  g2: GroupCreated(g2, "Porto", "EUR", carol)
               AND MemberAdded(m4, "Carol", carol) AND MemberClaimed(m4, carol)
               AND MemberAdded(m5, "Bob", carol)
               AND MemberInvited(m5, i2, t0+20d, carol)
    WITH   invites(bob) = { g1: i1, g2: i2 }
    WHEN   ViewInvites(bob)  at t0+1d
    THEN   [ { g2, "Porto", "Bob", "Carol" }, { g1, "Lisbon trip", "Bob", "Alice" } ]
```

## Notes

- **Matched by verified address.** Accounts exist only once their address has been
  proven with a sign-in code (spec §4), so the account email is verified. The
  lookup compares it case-insensitively with each `Invite`'s address.
- **The lookup proposes; the streams decide.** An `Invite` document is shown only
  if its group's stream agrees: the document's invite is its slot's newest
  `MemberInvited`, the slot is unclaimed, and the deadline has not passed.
  Scenarios 3 and 5 are documents the stream disowns — stale, or left behind.
- **Nothing to hide.** The caller sees only invites addressed to their own address,
  and what each shows — group, slot and inviter names — is what the invite email
  already told them. An empty list is the answer for "nothing", not a 404.
- **Expiry is the recorded deadline:** live while `now < expiresAt` (spec §11).
  Nothing here reads Marten metadata.
- **Live, deliberately** (spec §11): a user has a handful of invites, each group
  stream is a few hundred events, so they are folded per request and nothing is
  stored that could go stale.
- **The inviter** is the slot the inviting user held when inviting, named as that
  slot is named now.

## Deferred to the slices that introduce the events

- **A removed slot's** invite is dead — with `MemberRemoved`.
- **A renamed slot or group** shows its new name — with `MemberRenamed`, `GroupRenamed`.
- **An archived group's** invites: shown or not — with `GroupArchived`.
- **A released claim** leaves its old invites dead — with `MemberClaimReleased`.
