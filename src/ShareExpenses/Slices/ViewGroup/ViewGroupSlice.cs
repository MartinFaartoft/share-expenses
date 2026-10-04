using JasperFx.Events.Projections;
using Marten;

namespace ShareExpenses.Slices.ViewGroup;

/// <summary>View group: what it contributes to the store (spec §12).</summary>
public static class ViewGroupSlice
{
    /// <summary>
    /// The group activity, projected inline: saved in the same transaction as the events
    /// that change it, so reading after writing always sees the write (spec §11).
    /// </summary>
    public static void Register(StoreOptions opts) =>
        // Named, as projections are listed and rebuilt by name — and every slice's state is a "State".
        opts.Projections.Snapshot<State>(SnapshotLifecycle.Inline, projection => projection.Name = "GroupActivity", _ => { });
}
