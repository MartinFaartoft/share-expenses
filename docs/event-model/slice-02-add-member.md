# Slice 2 — `AddMember`

Type: **State Change**. Screen → command → events.

| | |
|---|---|
| Screen | Group setup |
| Command | `AddMember(displayName, by)` |
| Events | `MemberAdded` (owned by CreateGroup) |
| Code | `src/ShareExpenses/Slices/AddMember/` |
| Endpoint | `POST /api/groups/{group}/members` |

Adds a **placeholder** member by name alone (spec §4): someone expenses can be
recorded against immediately, who may be invited and claim the slot later. Adding
and inviting are separate commands; a client may send both from one form.

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
```

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

## Deferred to the slices that introduce the events

- **Archived group** rejects the command (R3) — with `GroupArchived`.
- **A released claim ends membership** (R2) — with `MemberClaimReleased`.
- **A removed member's name may be reused** (R5) — with `MemberRemoved`.
- **A renamed member's old name is free and new name taken** (R5) — with
  `MemberRenamed`.

## Concurrency

Two phones adding at once: both decide against the same stream version, the
first save wins, the second gets **409 Conflict** and the client retries — at
which point the rules run against the new state, so a duplicate name is then
rejected properly. Server-side retry was considered and deferred (see `Endpoint.cs`).
