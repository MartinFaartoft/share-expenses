using Marten;
using Mono.Cecil;

namespace ShareExpenses.Tests.Architecture;

/// <summary>
/// Slice boundaries (spec §12). C# has no folder-level visibility, so these tests are
/// what keeps slices apart: events are the only thing one slice may use of another.
/// Each rule runs against the real application, against a conforming fixture, and
/// against a violating fixture — so a rule that silently stopped firing would fail
/// here rather than pass vacuously.
/// </summary>
public class SliceBoundaryTests
{
    private const string Fixtures = "ShareExpenses.Tests.Architecture.Fixtures";

    private static readonly System.Reflection.Assembly AppAssembly = typeof(AllSlices).Assembly;
    private static readonly System.Reflection.Assembly TestAssembly = typeof(SliceBoundaryTests).Assembly;
    private static readonly ModuleDefinition App = SliceRules.Load(AppAssembly);
    private static readonly ModuleDefinition Tests = SliceRules.Load(TestAssembly);

    private static readonly SliceLayout AppLayout =
        new("ShareExpenses.Slices", "ShareExpenses.Shared", typeof(AllSlices).FullName!);

    private static readonly SliceLayout Good =
        new($"{Fixtures}.Good.Slices", $"{Fixtures}.Good.Shared", $"{Fixtures}.Good.Catalog");

    private static readonly SliceLayout Bad =
        new($"{Fixtures}.Bad.Slices", $"{Fixtures}.Bad.Shared", $"{Fixtures}.Bad.Catalog");

    public static TheoryData<string> Rules =>
    [
        "PublicSurface", "CrossSliceDependencies", "SharedIsPure", "RegistrationsWired", "EventsRegistered",
        "StateAliases",
    ];

    private static IReadOnlyList<string> Check(
        string rule, System.Reflection.Assembly assembly, ModuleDefinition module, SliceLayout layout) => rule switch
    {
        "PublicSurface" => SliceRules.PublicSurface(module, layout),
        "CrossSliceDependencies" => SliceRules.CrossSliceDependencies(module, layout),
        "SharedIsPure" => SliceRules.SharedIsPure(module, layout),
        "RegistrationsWired" => SliceRules.RegistrationsWired(module, layout),
        "EventsRegistered" => UnregisteredEvents(assembly, module, layout),
        "StateAliases" => SliceRules.StateAliases(module, layout),
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    [Theory, MemberData(nameof(Rules))]
    public void Application_obeys(string rule) => Assert.Empty(Check(rule, AppAssembly, App, AppLayout));

    [Theory, MemberData(nameof(Rules))]
    public void Conforming_fixture_passes(string rule) => Assert.Empty(Check(rule, TestAssembly, Tests, Good));

    [Fact]
    public void Only_events_entry_points_wolverine_endpoints_and_their_signatures_are_public() =>
        Assert.Equal(
            [
                $"Epsilon: {Fixtures}.Bad.Slices.Epsilon.EpsilonSlice is public, but is neither a sealed record event, the EpsilonSlice entry point, a Wolverine endpoint, nor in an endpoint's signature",
                $"Gamma: {Fixtures}.Bad.Slices.Gamma.Endpoint is public, but is neither a sealed record event, the GammaSlice entry point, a Wolverine endpoint, nor in an endpoint's signature",
                $"Gamma: {Fixtures}.Bad.Slices.Gamma.Leaky is public, but is neither a sealed record event, the GammaSlice entry point, a Wolverine endpoint, nor in an endpoint's signature",
                $"Gamma: {Fixtures}.Bad.Slices.Gamma.Unsealed is public, but is neither a sealed record event, the GammaSlice entry point, a Wolverine endpoint, nor in an endpoint's signature",
            ],
            SliceRules.PublicSurface(Tests, Bad).Order());

    [Fact]
    public void Public_records_in_an_endpoint_signature_are_not_events() =>
        Assert.Equal([$"{Fixtures}.Bad.Slices.Gamma.GammaHappened"], SliceRules.Events(Tests, Bad));

    [Fact]
    public void A_slice_may_use_only_another_slices_events() =>
        Assert.Equal(
            [
                $"Delta: {Fixtures}.Bad.Slices.Delta.Peek depends on {Fixtures}.Bad.Slices.Gamma.GammaRequest, which is not an event of Gamma",
                $"Delta: {Fixtures}.Bad.Slices.Delta.Peek depends on {Fixtures}.Bad.Slices.Gamma.State, which is not an event of Gamma",
            ],
            SliceRules.CrossSliceDependencies(Tests, Bad).Order(StringComparer.Ordinal));

    [Fact]
    public void Shared_cannot_depend_on_slices_or_frameworks() =>
        Assert.Equal(
            [
                $"Shared: {Fixtures}.Bad.Shared.Impure depends on Microsoft.AspNetCore.Routing",
                $"Shared: {Fixtures}.Bad.Shared.Impure depends on {Fixtures}.Bad.Slices.Gamma",
            ],
            SliceRules.SharedIsPure(Tests, Bad).Order(StringComparer.Ordinal));

    [Fact]
    public void An_entry_point_is_optional_but_must_register_and_be_called() =>
        Assert.Equal(
            [
                "Delta: Catalog.Register does not call DeltaSlice.Register",
                "Epsilon: EpsilonSlice is not a public static class with Register(StoreOptions)",
            ],
            SliceRules.RegistrationsWired(Tests, Bad));

    [Fact]
    public void Every_event_is_registered_with_the_store() =>
        Assert.Equal(
            [$"{Fixtures}.Bad.Slices.Gamma.GammaHappened is not registered by any slice's Register"],
            UnregisteredEvents(TestAssembly, Tests, Bad));

    [Fact]
    public void Every_slice_state_has_a_unique_marten_alias() =>
        Assert.Equal(
            [
                "Delta.State, Epsilon.State: alias 'shared_state' is not unique",
                "Gamma.State has no [DocumentAlias]",
            ],
            SliceRules.StateAliases(Tests, Bad).Order(StringComparer.Ordinal));

    /// <summary>
    /// Every event is mapped by the catalog's <c>Register</c>, so it is stored under an
    /// explicit name (spec §11). Unmapped, Marten would quietly fall back to a name
    /// derived from the class — identical today, wrong after a rename.
    /// </summary>
    private static IReadOnlyList<string> UnregisteredEvents(
        System.Reflection.Assembly assembly, ModuleDefinition module, SliceLayout layout)
    {
        var opts = new StoreOptions();
        assembly.GetType(layout.CatalogType)!.GetMethod("Register")!.Invoke(null, [opts]);
        var registered = ((Marten.Events.IReadOnlyEventStoreOptions)opts.Events).AllKnownEventTypes()
            .Select(e => e.EventType.FullName).ToHashSet();

        return SliceRules.Events(module, layout)
            .Where(e => !registered.Contains(e))
            .Select(e => $"{e} is not registered by any slice's Register")
            .ToList();
    }
}
