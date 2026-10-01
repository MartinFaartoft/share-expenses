using Mono.Cecil;

namespace ShareExpenses.Tests.Architecture;

/// <summary>
/// Slice boundaries (spec §12). C# has no folder-level visibility, so these tests are
/// what makes "only events are public" true. Each rule runs against the real
/// application, against a conforming fixture, and against a violating fixture —
/// so a rule that silently stopped firing would fail here rather than pass vacuously.
/// </summary>
public class SliceBoundaryTests
{
    private const string Fixtures = "ShareExpenses.Tests.Architecture.Fixtures";

    private static readonly ModuleDefinition App = SliceRules.Load(typeof(AllSlices).Assembly);
    private static readonly ModuleDefinition Tests = SliceRules.Load(typeof(SliceBoundaryTests).Assembly);

    private static readonly SliceLayout AppLayout =
        new("ShareExpenses.Slices", "ShareExpenses.Shared", typeof(AllSlices).FullName!);

    private static readonly SliceLayout Good =
        new($"{Fixtures}.Good.Slices", $"{Fixtures}.Good.Shared", $"{Fixtures}.Good.Catalog");

    private static readonly SliceLayout Bad =
        new($"{Fixtures}.Bad.Slices", $"{Fixtures}.Bad.Shared", $"{Fixtures}.Bad.Catalog");

    public static TheoryData<string> Rules => ["PublicSurface", "CrossSliceDependencies", "SharedIsPure", "EntryPointsWired", "StateAliases"];

    private static IReadOnlyList<string> Check(string rule, ModuleDefinition module, SliceLayout layout) => rule switch
    {
        "PublicSurface" => SliceRules.PublicSurface(module, layout),
        "CrossSliceDependencies" => SliceRules.CrossSliceDependencies(module, layout),
        "SharedIsPure" => SliceRules.SharedIsPure(module, layout),
        "EntryPointsWired" => SliceRules.EntryPointsWired(module, layout),
        "StateAliases" => SliceRules.StateAliases(module, layout),
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    [Theory, MemberData(nameof(Rules))]
    public void Application_obeys(string rule) => Assert.Empty(Check(rule, App, AppLayout));

    [Theory, MemberData(nameof(Rules))]
    public void Conforming_fixture_passes(string rule) => Assert.Empty(Check(rule, Tests, Good));

    [Fact]
    public void Only_sealed_record_events_and_the_entry_point_are_public() =>
        Assert.Equal(
            [
                $"Gamma: {Fixtures}.Bad.Slices.Gamma.Leaky is public, but is neither a sealed record event nor the GammaSlice entry point",
                $"Gamma: {Fixtures}.Bad.Slices.Gamma.Unsealed is public, but is neither a sealed record event nor the GammaSlice entry point",
            ],
            SliceRules.PublicSurface(Tests, Bad).Order());

    [Fact]
    public void A_slice_cannot_reach_into_another_slices_internals() =>
        Assert.Equal(
            [$"Delta: {Fixtures}.Bad.Slices.Delta.Peek depends on {Fixtures}.Bad.Slices.Gamma.State, which is internal to Gamma"],
            SliceRules.CrossSliceDependencies(Tests, Bad));

    [Fact]
    public void Shared_cannot_depend_on_slices_or_frameworks() =>
        Assert.Equal(
            [
                $"Shared: {Fixtures}.Bad.Shared.Impure depends on Microsoft.AspNetCore.Routing",
                $"Shared: {Fixtures}.Bad.Shared.Impure depends on {Fixtures}.Bad.Slices.Gamma",
            ],
            SliceRules.SharedIsPure(Tests, Bad).Order(StringComparer.Ordinal));

    [Fact]
    public void Every_slice_has_an_entry_point_called_by_the_catalog() =>
        Assert.Equal(
            [
                "Delta: Catalog.Map does not call DeltaSlice.Map",
                "Epsilon: no public static EpsilonSlice with Register(StoreOptions) and Map(IEndpointRouteBuilder)",
            ],
            SliceRules.EntryPointsWired(Tests, Bad));

    [Fact]
    public void Every_slice_state_has_a_unique_marten_alias() =>
        Assert.Equal(
            [
                "Delta, Epsilon: State alias 'shared_state' is not unique",
                "Gamma: State has no [DocumentAlias]",
            ],
            SliceRules.StateAliases(Tests, Bad).Order(StringComparer.Ordinal));
}
