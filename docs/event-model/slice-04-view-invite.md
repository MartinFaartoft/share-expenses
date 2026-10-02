# Slice 4 — View invite

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Invite landing ("Alice invited you to Lisbon trip as Bob — [Sign me in]") |
| Read model | `InviteReadModel` — **live**: folded from one group stream per request, nothing stored |
| Query | `ViewInvite(token, now)` — token from the link's fragment, the clock; the group from the route selects the stream |
| Code | `src/ShareExpenses/Slices/ViewInvite/` |
| Endpoint | `POST /api/invites/{group}/lookup`, body `{ "token": "…" }` — no sign-in |

The page an invite link lands on. It shows who invited you, to which group, as
whom — and offers "Sign me in", which leads to claiming (AcceptInvite). Viewing changes
nothing, so a mail client or scanner prefetching the page consumes nothing (spec §4).

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a query field (spec §13).

`h1`, `h2` are the hashes of tokens `t1`, `t2`; `tX` matches nothing. Times are
relative to `t0`. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice)
  AND  MemberInvited(m2, h1, t0+30d, alice)
```

```
1 - shows a live invite
    WHEN   ViewInvite(t1)  at t0+1d
    THEN   { groupName: "Lisbon trip", memberName: "Bob", invitedBy: "Alice" }

2 - a wrong token finds nothing
    WHEN   ViewInvite(tX)  at t0+1d
    THEN   not found

3 - an unknown group finds nothing
    GIVEN  (empty stream)
    WHEN   ViewInvite(t1)  at t0+1d
    THEN   not found

4 - an invite is dead at its deadline
    WHEN   ViewInvite(t1)  at t0+30d
    THEN   not found
    (and at t0+30d less one second: shown)

5 - re-inviting retires the previous link
    GIVEN  ... AND MemberInvited(m2, h2, t0+31d, alice)
    WHEN   ViewInvite(t1)  at t0+1d
    THEN   not found

6 - the new link has its own deadline
    GIVEN  ... AND MemberInvited(m2, h2, t0+59d, alice)
    WHEN   ViewInvite(t2)  at t0+40d
    THEN   { "Lisbon trip", "Bob", "Alice" }

7 - a claimed slot's invite is used up
    GIVEN  ... AND MemberClaimed(m2, bob)
    WHEN   ViewInvite(t1)  at t0+1d
    THEN   not found

8 - the inviter is named by their slot in this group
    GIVEN  ... AND MemberAdded(m3, "Carol", alice) AND MemberClaimed(m3, carol)
           AND MemberInvited(m2, h2, t0+30d, carol)
    WHEN   ViewInvite(t2)  at t0+1d
    THEN   { "Lisbon trip", "Bob", "Carol" }
```

## Notes

- **Every dead link is the same "not found".** Wrong token, unknown group,
  expired, superseded or used: one 404, `invite not found`. So is a malformed group
  id and a missing token. Simpler than telling them apart, and it reveals nothing.
- **No sign-in.** The person holding the link usually has no account yet; the
  token is the capability (spec §4). What the page shows — group, slot and inviter
  names — is exactly what the link already entitles its holder to.
- **The token never travels in a URL.** The link carries it in the fragment
  (`/invites/{groupId}#{token}`); the landing page reads it client-side and posts
  it in the body. It is matched against `MemberInvited.tokenHash` in constant time.
- **Expiry is the recorded deadline:** live while `now < expiresAt` (spec §11).
  Nothing here reads Marten metadata.
- **Live, deliberately** (spec §11): a group stream is a few hundred events, so it
  is folded per request and nothing is stored that could go stale.
- **The inviter** is the slot the inviting user held when inviting, named as that
  slot is named now.
- **Not rate limited yet:** deferred to the sign-in work (spec §14).

## Deferred to the slices that introduce the events

- **A removed slot's** invite is dead — with `MemberRemoved`.
- **A renamed slot or group** shows its new name — with `MemberRenamed`, `GroupRenamed`.
- **An archived group's** invites: viewable or not — with `GroupArchived`.
- **A released claim** leaves its old invites dead — with `MemberClaimReleased`.
