# Slice 1 — `CreateGroup`

Pattern: **command**. Screen → command → events.

| | |
|---|---|
| Screen | New group |
| Command | `CreateGroup(name, currency, createdBy)` |
| Events | `GroupCreated`, `MemberAdded`, `MemberClaimed` |

## Specifications

```
1 - creates the group and its first member
    GIVEN  (empty stream)
    WHEN   CreateGroup("Lisbon trip", "GBP", alice)
    THEN   GroupCreated(g1, "Lisbon trip", "GBP", alice)
     AND   MemberAdded(m1, "Alice")
     AND   MemberClaimed(m1, alice)

2 - rejects a blank name
    GIVEN  (empty stream)
    WHEN   CreateGroup("   ", "GBP", alice)
    THEN   rejected - name is required

3 - rejects an unknown currency
    GIVEN  (empty stream)
    WHEN   CreateGroup("Lisbon trip", "XYZ", alice)
    THEN   rejected - currency must be a known ISO 4217 code

4 - a group is created exactly once
    GIVEN  GroupCreated(g1, ...)
    WHEN   CreateGroup("Lisbon trip", "GBP", alice)
    THEN   rejected - group already exists
```

Scenario 1 seats the creator as an already-claimed member: a group whose creator
is not a member is a meaningless state, since they could be neither payer nor
participant. Scenario 4 is enforced by Marten's stream semantics and optimistic
concurrency — appending at expected version 0 to an existing stream fails — not by
aggregate validation.
