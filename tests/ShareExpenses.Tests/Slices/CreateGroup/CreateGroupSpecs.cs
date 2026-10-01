using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Tests.Specs;

namespace ShareExpenses.Tests.Slices.CreateGroup;

/// <summary>
/// docs/event-model/slice-01-create-group.md, line for line. Scenario 4 needs a
/// real event store and lives in <see cref="CreateGroupIntegrationTests"/>.
/// </summary>
public class CreateGroupSpecs
{
    private static readonly Guid G1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid M1 = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-0000000000c1");

    // CreateGroup decides against no state: history is the store's business (scenario 4).
    private static readonly DecideSpec<Command> Spec = new((history, command) =>
        history.Count == 0
            ? Decider.Decide(command, G1, M1)
            : throw new InvalidOperationException("CreateGroup has no state; scenarios with history belong in the integration tests"));

    private static Command CreateGroup(string? name, string? currency, string? displayName) =>
        new(name, currency, displayName, Alice);

    [Fact]
    public void S1_creates_the_group_and_its_first_member() =>
        Spec.Given()
            .When(CreateGroup("Lisbon trip", "GBP", "Alice"))
            .Then(
                new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
                new MemberAdded(M1, "Alice"),
                new MemberClaimed(M1, Alice));

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void S2_rejects_a_blank_name(string? name) =>
        Spec.Given()
            .When(CreateGroup(name, "GBP", "Alice"))
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
    public void S5_rejects_a_blank_display_name(string? displayName) =>
        Spec.Given()
            .When(CreateGroup("Lisbon trip", "GBP", displayName))
            .ThenRejected("your name is required");

    [Fact]
    public void Inputs_are_trimmed_and_currency_upper_cased() =>
        Spec.Given()
            .When(CreateGroup("  Lisbon trip ", " gbp ", " Alice "))
            .Then(
                new GroupCreated(G1, "Lisbon trip", "GBP", Alice),
                new MemberAdded(M1, "Alice"),
                new MemberClaimed(M1, Alice));
}
