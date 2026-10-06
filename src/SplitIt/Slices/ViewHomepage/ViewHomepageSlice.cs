using JasperFx.Events.Projections;
using Marten;

namespace SplitIt.Slices.ViewHomepage;

/// <summary>View homepage: what it contributes to the store (spec §12).</summary>
public static class ViewHomepageSlice
{
    /// <summary>
    /// The user's groups, projected asynchronously by Marten's daemon (spec §11), with an
    /// index for "which groups contain this user". Invites are folded live and need nothing.
    /// </summary>
    public static void Register(StoreOptions opts)
    {
        opts.Projections.Snapshot<Membership>(SnapshotLifecycle.Async, projection => projection.Name = "UserGroups", _ => { });
        opts.Schema.For<Membership>().GinIndexJsonData();
    }
}
