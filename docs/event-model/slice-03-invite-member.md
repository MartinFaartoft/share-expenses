# Slice 3 — `InviteMember`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Invite member (modelled; no frontend yet) |
| Command | `InviteMember(groupId, memberId, email, tokenHash, by)` |
| Events | `MemberInvited` |
| Code | `src/ShareExpenses/Slices/InviteMember/` |
| Endpoint | `POST /api/groups/{groupId}/members/{memberId}/invite` |

Ties an email address to one member slot and issues a link that lets whoever
holds it claim that slot (spec §4: the invite binds the slot, and the token is the
capability). The link is both emailed and returned in the response, so it can be
shared in a group chat as well.

## Specifications

`h0`, `h1` are hashes of pinned tokens. Unless stated otherwise, every scenario
starts from:

```
GIVEN  GroupCreated(g1, "Lisbon trip", "GBP", alice)
  AND  MemberAdded(m1, "Alice", alice)
  AND  MemberClaimed(m1, alice)
  AND  MemberAdded(m2, "Bob", alice)
```

```
1 - invites a placeholder member
    WHEN   InviteMember(g1, m2, "bob@example.com", alice)
    THEN   MemberInvited(m2, "bob@example.com", h1, alice)

2 - trims the email and keeps its case
    WHEN   InviteMember(g1, m2, "  Bob@Example.com ", alice)
    THEN   MemberInvited(m2, "Bob@Example.com", h1, alice)

3 - the group must exist
    GIVEN  (empty stream)
    WHEN   InviteMember(g1, m2, "bob@example.com", alice)
    THEN   rejected - group not found

4 - only members of the group may invite
    WHEN   InviteMember(g1, m2, "bob@example.com", mallory)
    THEN   rejected - group not found

5 - the slot must exist in the group
    WHEN   InviteMember(g1, m9, "bob@example.com", alice)
    THEN   rejected - member not found

6 - a slot that has already joined cannot be invited
    WHEN   InviteMember(g1, m1, "alice@example.com", alice)
    THEN   rejected - member has already joined

7 - rejects an address that is not plausibly an email
    WHEN   InviteMember(g1, m2, "bob", alice)
    THEN   rejected - email is not a valid address

8 - re-inviting replaces the previous invite
    GIVEN  ... AND MemberInvited(m2, "bob@old.com", h0, alice)
    WHEN   InviteMember(g1, m2, "bob@new.com", alice)
    THEN   MemberInvited(m2, "bob@new.com", h1, alice)

9 - an email cannot be invited to two slots
    GIVEN  ... AND MemberAdded(m3, "Bobby", alice)
           AND MemberInvited(m2, "bob@example.com", h0, alice)
    WHEN   InviteMember(g1, m3, "BOB@example.com", alice)
    THEN   rejected - that email is already invited as Bob

10 - re-inviting the same slot with the same email is fine
    GIVEN  ... AND MemberInvited(m2, "bob@example.com", h0, alice)
    WHEN   InviteMember(g1, m2, "bob@example.com", alice)
    THEN   MemberInvited(m2, "bob@example.com", h1, alice)

11 - an email that has joined cannot be invited to another slot
    GIVEN  ... AND MemberInvited(m2, "bob@example.com", h0, alice)
           AND MemberClaimed(m2, bob)
           AND MemberAdded(m3, "Bobby", alice)
    WHEN   InviteMember(g1, m3, "bob@example.com", alice)
    THEN   rejected - bob@example.com has already joined as Bob
```

## Notes

- **Order of checks:** membership, then the slot, then the email. A non-member
  gets `group not found` whatever else is wrong, so they learn nothing — over HTTP,
  404 for a missing group, a non-member and a malformed group id alike. Once
  membership is established, `member not found` (also 404) reveals nothing new.
- **The token.** 32 random bytes, base64url. Only its SHA-256 (`tokenHash`) is
  recorded; the raw token exists in the response and the email, nowhere else. The
  link is `{App:PublicOrigin}/invites/{groupId}/{token}` — scoped to the group, so
  looking it up folds one stream (spec §11).
- **Email** is trimmed, kept in its original case in the event, and compared
  case-insensitively. "Plausibly an email": at most 254 characters, exactly one
  `@` with something on both sides, no whitespace. Real validation is delivery.
- **One email, one slot.** An email held by another slot — invited or already
  joined through an invite — is rejected, naming that slot, so the inviter can see
  what happened. The rejection would otherwise surface later and more confusingly,
  at claim time, on the invitee's side. Known gap: the group creator's slot has no
  recorded email (spec §14, OPEN).
- **Expired invites still hold their email.** Expiry (30 days) is checked where
  the link is used, so deciding never needs the clock; the inviter re-invites the
  named slot instead.
- **Re-inviting** retires the previous link: the newest `MemberInvited` for a slot
  is the live one. That half of scenario 8 is tested where links are used.
- **Email is sent after the save.** If sending fails, the response is still 200
  with the link and the failure is logged: the invite happened, delivery did not.

## Deferred to the slices that introduce the events

- **Archived group** rejects the command — with `GroupArchived`.
- **A removed slot** cannot be invited — with `MemberRemoved`.
- **A released claim** makes a slot invitable again — with `MemberClaimReleased`.
- **A renamed slot** appears under its new name in rejections — with `MemberRenamed`.

## Concurrency

As slice 2: a conflicting save answers **409** and the client retries.
