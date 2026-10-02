using Marten;
using ShareExpenses.Slices.CreateGroup;
using ShareExpenses.Slices.InviteMember;
using ShareExpenses.Slices.RecordExpense;

namespace ShareExpenses;

/// <summary>
/// What the slices contribute to the store (spec §12): stored event names, and later
/// projections. Only slices that own events or projections have a <c>&lt;Name&gt;Slice</c>;
/// slices that only fold state live need nothing here. Endpoints are not listed:
/// Wolverine discovers them. The architecture tests fail if a slice's events are not
/// registered, or a <c>&lt;Name&gt;Slice</c> is not called.
/// </summary>
public static class AllSlices
{
    public static void Register(StoreOptions opts)
    {
        CreateGroupSlice.Register(opts);
        InviteMemberSlice.Register(opts);
        RecordExpenseSlice.Register(opts);
    }
}
