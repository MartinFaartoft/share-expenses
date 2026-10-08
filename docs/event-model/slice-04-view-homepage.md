# Slice 4 — View homepage

Type: **State Read**. Events → read model → screen.

| | |
|---|---|
| Screen | Home — where signing in lands: invites waiting (each with Join), then your groups |
| Read model | `HomepageReadModel`, in two parts kept differently (spec §11): **groups** from the stored, **asynchronous** `UserGroups` projection; **invites** **live**, each invited group's stream folded per request |
| Query | `ViewHomepage(userId, now)` + looked up: `invitedAs`, the `Invite` documents addressed to the user's account email |
| Code | `src/SplitIt/Slices/ViewHomepage/` |
| Endpoint | `GET /` — the screen; sign-in required |

Where a signed-in user starts. Invites waiting for them come first — who invited
them, to which group, as whom, each with "Join" (AcceptInvite) — then the groups
they are in, by name, each opening its group page, and a **New group** button
(Create group's form, `/groups/new`). With neither, the screen says so and
suggests creating a group: "You're not in any groups yet. Start one, or wait for
an invite."

Joining one invite leaves the others: joining is per group. A single group is
still shown in the list, never jumped into — a home that sometimes skips itself
is disorienting.

## Specifications — invites

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
    WHEN   ViewHomepage(bob)  at t0+1d
    THEN   [ { group: g1, groupName: "Lisbon trip", memberName: "Bob", invitedBy: "Alice" } ]

2 - nobody invited this address
    WITH   invites(carol) = {}
    WHEN   ViewHomepage(carol)  at t0+1d
    THEN   []

3 - an invite into an unknown group is not shown
    GIVEN  g1: (empty stream)
    WHEN   ViewHomepage(bob)  at t0+1d
    THEN   []

4 - an invite is dead at its deadline
    WHEN   ViewHomepage(bob)  at t0+30d
    THEN   []
    (and at t0+30d less one second: shown)

5 - only the slot's current invite counts
    GIVEN  g1: ... AND MemberInvited(m2, i2, t0+31d, alice)
    WHEN   ViewHomepage(bob)  at t0+1d
    THEN   []

6 - a claimed slot's invite is used up
    GIVEN  g1: ... AND MemberClaimed(m2, carol)
    WHEN   ViewHomepage(bob)  at t0+1d
    THEN   []

7 - an invite into a group the user is already in is not shown
    GIVEN  g1: ... AND MemberAdded(m3, "Bobby", alice) AND MemberClaimed(m3, bob)
    WHEN   ViewHomepage(bob)  at t0+1d
    THEN   []

8 - the inviter is named by their slot in that group
    GIVEN  g1: ... AND MemberAdded(m3, "Carol", alice) AND MemberClaimed(m3, carol)
               AND MemberInvited(m2, i2, t0+30d, carol)
    WITH   invites(bob) = { g1: i2 }
    WHEN   ViewHomepage(bob)  at t0+1d
    THEN   [ { g1, "Lisbon trip", "Bob", "Carol" } ]

9 - invites from several groups, soonest deadline first
    GIVEN  g2: GroupCreated(g2, "Porto", "EUR", carol)
               AND MemberAdded(m4, "Carol", carol) AND MemberClaimed(m4, carol)
               AND MemberAdded(m5, "Bob", carol)
               AND MemberInvited(m5, i2, t0+20d, carol)
    WITH   invites(bob) = { g1: i1, g2: i2 }
    WHEN   ViewHomepage(bob)  at t0+1d
    THEN   [ { g2, "Porto", "Bob", "Carol" }, { g1, "Lisbon trip", "Bob", "Alice" } ]

10 - an invite names the group by its current name
    GIVEN  g1: ... AND GroupRenamed("Porto trip", alice)
    WITH   invites(bob) = { g1: i1 }
    WHEN   ViewHomepage(bob)
    THEN   invites: [{ g1, "Porto trip", "Bob", "Alice" }]

11 - an invite into an archived group is not shown
    GIVEN  g1: ... AND GroupArchived(alice)
    WITH   invites(bob) = { g1: i1 }
    WHEN   ViewHomepage(bob)
    THEN   invites: []
```

## Specifications — groups

`UserGroups` is keyed by user and folded across every group's stream; scenarios
list the streams involved. Read after the asynchronous projection has caught up.

```
G1 - a brand-new user has no groups and no invites
    WITH   invites(dave) = {}
    WHEN   ViewHomepage(dave)
    THEN   { groups: [], invites: [] }

G2 - the creator's group is listed
    GIVEN  g1: GroupCreated(g1, "Lisbon trip", "GBP", alice)
               AND MemberAdded(m1, "Alice", alice) AND MemberClaimed(m1, alice)
    WHEN   ViewHomepage(alice)
    THEN   groups: [{ g1, "Lisbon trip" }]

G3 - a joined group is listed
    GIVEN  g1: ... AND MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob)
    WHEN   ViewHomepage(bob)
    THEN   groups: [{ g1, "Lisbon trip" }]

G4 - a slot added for you, or invited, is not membership
    GIVEN  g1: ... AND MemberAdded(m2, "Bob", alice) AND MemberInvited(m2, i1, t0+30d, alice)
    WITH   invites(bob) = { g1: i1 }
    WHEN   ViewHomepage(bob)
    THEN   { groups: [], invites: [{ g1, "Lisbon trip", "Bob", "Alice" }] }

G5 - several groups, by name
    GIVEN  g1: GroupCreated(g1, "Lisbon trip", …, alice) …claimed by alice
           g2: GroupCreated(g2, "Barcelona", …, carol) … AND MemberClaimed(m5, alice)
           g3: GroupCreated(g3, "Porto", …, alice) …claimed by alice
    WHEN   ViewHomepage(alice)
    THEN   groups: [{ g2, "Barcelona" }, { g1, "Lisbon trip" }, { g3, "Porto" }]

G6 - other people's groups are not listed
    GIVEN  g1: GroupCreated(g1, "Lisbon trip", …, alice) …claimed by alice
    WHEN   ViewHomepage(carol)
    THEN   groups: []

G7 - a renamed group is listed by its current name
    GIVEN  g1: ... AND GroupRenamed("Porto trip", bob)
    WHEN   ViewHomepage(alice)
    THEN   groups: [{ g1, "Porto trip" }]

G8 - an archived group leaves the main list and is listed as archived
    GIVEN  g1: ... AND GroupArchived(alice)
           g3: GroupCreated(g3, "Porto", …, alice) …claimed by alice
    WHEN   ViewHomepage(alice)
    THEN   groups: [{ g3, "Porto" }], archived: [{ g1, "Lisbon trip" }]

G9 - archiving moves a group for everyone in it
    GIVEN  g1: ... AND MemberAdded(m2, "Bob", alice) AND MemberClaimed(m2, bob) AND GroupArchived(alice)
    WHEN   ViewHomepage(bob)
    THEN   groups: [], archived: [{ g1, "Lisbon trip" }]
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
- **Two parts, two lifecycles, one screen** (spec §11). Invites are live: a user
  has a handful, each group stream is a few hundred events, so they are folded per
  request and nothing stored could go stale. Groups cannot be: "every group I am
  in" spans every stream, so it is a stored projection, `UserGroups` — one
  document per group, naming it and listing the users holding a slot, queried for
  those containing the signed-in user. (Per group, not per user: `MemberClaimed`
  carries no group name to fold into a user's document.)
- **Groups lag, harmlessly.** `UserGroups` is projected asynchronously, by Marten's
  daemon, deliberately (spec §11): a group created or joined a moment ago may not
  be listed yet. Nobody sees that: Create and Join go straight into the group,
  never via home. Invites never lag — they are live.
- **Group names only, for now** — no balances in the list.
- **Join** posts AcceptInvite (`POST /invites/{group}/accept`, as a form) and
  redirects to the group page, `/groups/{group}` — not yet built: until it is, that
  address answers 404.
- **Signing in returns where you were going:** a screen asked for while signed out
  redirects to `/sign-in?returnUrl=…`; after signing in, the user goes there, or
  home. Only a local path is accepted — anything else would let a crafted link send
  a just-signed-in user to another site.
- **The inviter** is the slot the inviting user held when inviting, named as that
  slot is named now.

## Renamed and archived groups

- **A renamed group** shows its current name, in the list and in an invite (scenarios 10
  and G7): `UserGroups` folds `GroupRenamed`, and the live invites read the name from the
  group's stream as it now is.
- **An archived group** leaves the main list for an **Archived** list under it — a
  collapsed `<details>` headed "Archived (1)", shown only when there is one — where each
  group still opens its (read-only) group page. It is archived for everyone in it
  (scenario G9). No Unarchive is offered (slice-15).
- **An invite into an archived group** is not shown (scenario 11): Accept invite treats
  it as dead.
- **No rebuild is needed:** `UserGroups` is stored and now folds two more events, but a
  stored document without the flag reads as not archived, and the daemon folds each
  `GroupArchived` as it is appended. `just rebuild-projections` would give the same result.

## Deferred to the slices that introduce the events

- **A released claim** removes the group from the list — with `MemberClaimReleased`.
- **A declined invite** disappears — with `InviteDeclined` (spec §14).

- **A removed slot's** invite is dead — with `MemberRemoved`.
- **A renamed slot** shows its new name — with `MemberRenamed`.
- **A released claim** leaves its old invites dead — with `MemberClaimReleased`.
