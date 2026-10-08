using Marten;
using SplitIt.Slices.AddMember;
using SplitIt.Slices.ChangeDefaultSplit;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.EditExpense;
using SplitIt.Slices.RecordExpense;
using SplitIt.Slices.RecordSettlement;
using SplitIt.Slices.RenameGroup;
using SplitIt.Slices.RemoveExpense;
using SplitIt.Slices.ViewGroup;
using SplitIt.Slices.ViewHomepage;

namespace SplitIt;

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
        AddMemberSlice.Register(opts);
        ChangeDefaultSplitSlice.Register(opts);
        RecordExpenseSlice.Register(opts);
        EditExpenseSlice.Register(opts);
        RecordSettlementSlice.Register(opts);
        RemoveExpenseSlice.Register(opts);
        RenameGroupSlice.Register(opts);
        ViewGroupSlice.Register(opts);
        ViewHomepageSlice.Register(opts);
    }
}
