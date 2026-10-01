using ShareExpenses.Shared;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Tests.Specs;

namespace ShareExpenses.Tests.Slices.CreateGroup;

/// <summary>
/// docs/event-model/slice-01-create-group.md, line for line. Scenario 4 needs a
/// real event store and lives in <see cref="CreateGroupIntegrationTests"/>.
/// </summary>
public class CreateGroupSpecs
{
    private static readonly GroupId G1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly MemberId M1 = new(Guid.Parse("00000000-0000-0000-0000-0000000000b1"));
    private static readonly UserId Alice = new(Guid.Parse("00000000-0000-0000-0000-0000000000c1"));

    // CreateGroup decides against no state: history is the store's business (scenario 4).
    private static readonly DecideSpec<Command> Spec = new((history, command) =>
        history.Count == 0
            ? Decider.Decide(command, G1, M1)
            : throw new InvalidOperationException("CreateGroup has no state; scenarios with history belong in the integration tests"));

    private static Command CreateGroup(string? groupName, string? currency, string? memberName) =>
        new(groupName, currency, memberName, Alice);

    [Fact]
    public void S1_creates_the_group_and_its_first_member() =>
        Spec.Given()
            .When(CreateGroup("Lisbon trip", "GBP", "Alice"))
            .Then(
                new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
                new MemberAdded(M1, "Alice", Alice),
                new MemberClaimed(M1, Alice));

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void S2_rejects_a_blank_group_name(string? groupName) =>
        Spec.Given()
            .When(CreateGroup(groupName, "GBP", "Alice"))
            .ThenRejected("name is required");

    [Theory]
    [InlineData("XYZ")]
    [InlineData("XTS")] // a real ISO code, but a test code: not money
    [InlineData("")]
    [InlineData(null)]
    public void S3_rejects_an_unknown_currency(string? currency) =>
        Spec.Given()
            .When(CreateGroup("Lisbon trip", currency, "Alice"))
            .ThenRejected("currency must be a known ISO 4217 code");

    [Theory]
    [InlineData("   ")]
    [InlineData(null)]
    public void S5_rejects_a_blank_member_name(string? memberName) =>
        Spec.Given()
            .When(CreateGroup("Lisbon trip", "GBP", memberName))
            .ThenRejected("your name is required");

    [Fact]
    public void S6_rejects_a_group_name_longer_than_100_characters() =>
        Spec.Given()
            .When(CreateGroup(new string('x', 101), "GBP", "Alice"))
            .ThenRejected("name must be at most 100 characters");

    [Fact]
    public void S7_rejects_a_member_name_longer_than_50_characters() =>
        Spec.Given()
            .When(CreateGroup("Lisbon trip", "GBP", new string('x', 51)))
            .ThenRejected("your name must be at most 50 characters");

    [Fact]
    public void Limits_are_inclusive_and_measured_after_trimming() =>
        Spec.Given()
            .When(CreateGroup($"  {new string('x', 100)}  ", "GBP", $"  {new string('y', 50)}  "))
            .Then(
                new GroupCreated(G1, new string('x', 100), "GBP", Alice),
                new MemberAdded(M1, new string('y', 50), Alice),
                new MemberClaimed(M1, Alice));

    [Fact]
    public void Length_counts_visible_characters_not_utf16_units() =>
        // 50 family emoji: 50 visible characters, 400 UTF-16 code units.
        Spec.Given()
            .When(CreateGroup("Lisbon trip", "GBP", string.Concat(Enumerable.Repeat("👩‍👩‍👧", 50))))
            .Then(
                new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
                new MemberAdded(M1, string.Concat(Enumerable.Repeat("👩‍👩‍👧", 50)), Alice),
                new MemberClaimed(M1, Alice));

    [Fact]
    public void Inputs_are_trimmed_and_currency_upper_cased() =>
        Spec.Given()
            .When(CreateGroup("  Lisbon trip ", " gbp ", " Alice "))
            .Then(
                new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
                new MemberAdded(M1, "Alice", Alice),
                new MemberClaimed(M1, Alice));
}
