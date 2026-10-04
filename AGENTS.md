# Agent instructions

## Workflow
- Design first: update `event-model.yaml`, the slice doc and `spec.md`, then pause for review. Build only on "go".
- Refer to slices by name, not number.
- Never commit; the user does.
- Run commands through `just`; put scratch files in `tmp/`.

## Code
- Comments: default to none. Only explain what the code can't: a non-obvious why, an external constraint. No doc comments restating signatures, no step narration.
- Tests seed events directly (`Seed`) and drive only their own slice's screen.
