# Slice 3 — `InviteMember`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Invite member (modelled; no frontend yet) |
| Command | `InviteMember(memberId, email, tokenHash, now, by)` + looked up: `emailHolder`, `invitedTo` |
| Events | `MemberInvited` |
| Also writes | `InviteDelivery` — plain document, the address the invite went to |
| Code | `src/ShareExpenses/Slices/InviteMember/` |
| Endpoint | `POST /api/groups/{group}/members/{memberId}/invite` |

Issues a link that lets whoever holds it claim one member slot (spec §4: the
invite binds the slot, and the token is the capability), and emails it. The link
is also returned in the response, so it can be shared in a group chat.

**No email address enters the ledger.** `MemberInvited` records the slot, the
token's hash and the actor. The address goes into `InviteDelivery`, a plain,
erasable document (spec §3, §11).

## Specifications

Scenarios run against group `g1`'s stream. The group only selects the stream, so it
is not a command field (spec §13).

`h0`, `h1` are hashes of pinned tokens. Every command is issued at `t0`, so a new
invite's deadline is `t0+30d`; `…` is an earlier invite's deadline, which deciding
never reads. Lookups are written `WITH`: they come from
outside the stream (Identity, and `InviteDelivery`) and feed guards, not
invariants. Unless stated otherwise, every scenario starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice)
  AND  MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice)
```

```
1 - invites a placeholder member
    WHEN   InviteMember(m2, "bob@example.com", alice)
    THEN   MemberInvited(m2, h1, t0+30d, alice)

2 - the group must exist
    GIVEN  (empty stream)
    WHEN   InviteMember(m2, "bob@example.com", alice)
    THEN   rejected - group not found

3 - only members of the group may invite
    WHEN   InviteMember(m2, "bob@example.com", mallory)
    THEN   rejected - group not found

4 - the slot must exist in the group
    WHEN   InviteMember(m9, "bob@example.com", alice)
    THEN   rejected - member not found

5 - a slot that has already joined cannot be invited
    WHEN   InviteMember(m1, "alice@example.com", alice)
    THEN   rejected - member has already joined

6 - rejects an address that is not plausibly an email
    WHEN   InviteMember(m2, "bob", alice)
    THEN   rejected - email is not a valid address

7 - re-inviting replaces the previous invite
    GIVEN  ... AND MemberInvited(m2, h0, …, alice)
    WITH   invitedTo(bob@example.com) = {m2}
    WHEN   InviteMember(m2, "bob@example.com", alice)
    THEN   MemberInvited(m2, h1, t0+30d, alice)

8 - an address with an open invite cannot be invited to another slot
    GIVEN  ... AND MemberAdded(m3, "Bobby", alice) AND MemberInvited(m2, h0, …, alice)
    WITH   invitedTo(bob@example.com) = {m2}
    WHEN   InviteMember(m3, "BOB@example.com", alice)
    THEN   rejected - that email is already invited as Bob

9 - the address of a user already in the group cannot be invited
    GIVEN  ... AND MemberClaimed(m2, bob) AND MemberAdded(m3, "Bobby", alice)
    WITH   emailHolder(bob@example.com) = bob
    WHEN   InviteMember(m3, "bob@example.com", alice)
    THEN   rejected - bob@example.com has already joined as Bob

10 - including the creator's
    GIVEN  ... AND MemberAdded(m3, "Bobby", alice)
    WITH   emailHolder(alice@example.com) = alice
    WHEN   InviteMember(m3, "alice@example.com", alice)
    THEN   rejected - alice@example.com has already joined as Alice

11 - an invite to a slot since claimed no longer holds its address
    GIVEN  ... AND MemberInvited(m2, h0, …, alice) AND MemberClaimed(m2, bob)
           AND MemberAdded(m3, "Bobby", alice)
    WITH   invitedTo(bob@example.com) = {m2}, emailHolder(bob@example.com) = none
    WHEN   InviteMember(m3, "bob@example.com", alice)
    THEN   MemberInvited(m3, h1, t0+30d, alice)

12 - an account outside the group is no obstacle
    WITH   emailHolder(carol@example.com) = carol
    WHEN   InviteMember(m2, "carol@example.com", alice)
    THEN   MemberInvited(m2, h1, t0+30d, alice)
```

Scenario 11 is the "Bob changed his address" case: his account no longer answers
to bob@example.com, so the old address is free. Had he not changed it, scenario 9
would apply.

## Notes

- **Invariants vs guards.** "One user, one slot per group" is an invariant: it
  protects the ledger, is keyed on `UserId`, and is enforced at claim time against
  the stream. "One address, one slot" is a *guard*: it saves the inviter from a
  mistake that the claim would otherwise reject later, on the invitee's side. Guards
  may use data from outside the stream (spec §11); a stale lookup can only let a
  mistake through to claim time, never corrupt the ledger.
- **Lookups:** `emailHolder` is the account whose *current* address this is
  (Identity, case-insensitive); `invitedTo` is the slots whose latest
  `InviteDelivery` went to this address (case-insensitive). Deciding ignores
  deliveries for slots that have since been claimed.
- **Order of checks:** membership, then the slot, then the email. A non-member
  gets `group not found` whatever else is wrong. Over HTTP: 404 for a missing
  group, a non-member and a malformed group id alike; `member not found` (404)
  only once membership is established.
- **The token.** 32 random bytes, base64url. Only its SHA-256 (`tokenHash`) is
  recorded; the raw token exists in the response and the email, nowhere else. The
  link is `{App:PublicOrigin}/invites/{groupId}#{token}`: the token rides in the
  URL *fragment*, which browsers never send to a server, so it stays out of proxy
  logs and `Referer` headers. The landing page reads it client-side and posts it
  (ViewInvite).
- **Email** is trimmed and kept in its original case. "Plausibly an email": at
  most 254 characters, exactly one `@` with something on both sides, no
  whitespace. Real validation is delivery.
- **Expiry** is decided here and recorded: `expiresAt = now + 30 days`, on the
  event (spec §11). It is a deadline, a domain fact — not the append time, which
  stays Marten metadata. Changing the lifetime later never moves the deadline of
  links already sent. It is *enforced* where the link is used (ViewInvite and AcceptInvite).
- **Re-inviting** overwrites the slot's `InviteDelivery` and retires the previous
  link: the newest `MemberInvited` for a slot is the live one.
- **Email is sent after the save.** If sending fails, the response is still 200
  with the link and the failure is logged. A transactional outbox would make
  delivery retry on transient failures (spec §14, DEFERRED).

## Deferred to the slices that introduce the events

- **Archived group** rejects the command — with `GroupArchived`.
- **A removed slot** cannot be invited — with `MemberRemoved`.
- **A released claim** makes a slot invitable again — with `MemberClaimReleased`.
- **A renamed slot** appears under its new name in rejections — with `MemberRenamed`.

## Concurrency

As AddMember: a conflicting save answers **409** and the client retries.
