using System.Collections.Immutable;
using Marten.Schema;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;
using SplitIt.Slices.ArchiveGroup;

namespace SplitIt.Slices.ChangeDefaultSplit;

/// <summary>The default in force: a mode, and the shares that are not 1 (0 is left out).</summary>
public sealed record DefaultSplit(string Mode, IReadOnlyList<MemberShares> Shares)
{
    public const string Equal = "equal";
    public const string SharesMode = "shares";

    /// <summary>What every group has until it changes it: equal, nobody left out.</summary>
    public static DefaultSplit Original { get; } = new(Equal, []);

    /// <summary>Records compare lists by reference; a default compares by its entries.</summary>
    public bool Equals(DefaultSplit? other) => other is not null && Mode == other.Mode && Shares.SequenceEqual(other.Shares);

    public override int GetHashCode() => HashCode.Combine(Mode, Shares.Count);

    /// <summary>A member's share in this default: what is listed, otherwise 1; for equal, left out or in.</summary>
    public int ShareOf(MemberId member)
    {
        var listed = Shares.FirstOrDefault(s => s.MemberId == member);
        var share = listed?.Shares ?? 1;
        return Mode == Equal ? Math.Min(share, 1) : share;
    }
}

/// <summary>
/// What ChangeDefaultSplit needs to know about a group: who may change it, the slots a
/// default can name, and the default in force. The Default split screen is built from it as
/// well as decided against.
///
/// <see cref="Slots"/> keeps member-added order, in which the default is recorded.
///
/// FOLD CHECKLIST — when these slices are built, fold their events here and add the
/// deferred specs in slice-16-change-default-split.md:
///   MemberClaimReleased → remove from Members        (a released claim ends membership)
///   MemberRemoved       → drop the slot and its share
///   GroupUnarchived     → clear Archived (deferred, spec §11)
///
/// Public: Wolverine fetches it for the endpoint (spec §12). The alias is required:
/// every slice has a State.
/// </summary>
[DocumentAlias("change_default_split_state")]
public sealed record State(
    ImmutableList<MemberId> Slots,
    ImmutableDictionary<MemberId, string> Names,
    ImmutableDictionary<UserId, MemberId> Members,
    DefaultSplit Default)
{
    /// <summary>The stream id, set by Marten; Wolverine needs it to type the aggregate's identity.</summary>
    public GroupId Id { get; init; }

    /// <summary>Whether the group is archived: it takes no command (slice-15-archive-group.md).</summary>
    public bool Archived { get; init; }

    public static State Create(GroupCreated e) =>
        new([], ImmutableDictionary<MemberId, string>.Empty, ImmutableDictionary<UserId, MemberId>.Empty, DefaultSplit.Original);

    public State Apply(GroupArchived e) => this with { Archived = true };

    public State Apply(MemberAdded e) =>
        this with { Slots = Slots.Add(e.MemberId), Names = Names.SetItem(e.MemberId, e.DisplayName) };

    public State Apply(MemberClaimed e) => this with { Members = Members.SetItem(e.UserId, e.MemberId) };

    public State Apply(GroupDefaultSplitChanged e) => this with { Default = new DefaultSplit(e.Mode, e.Shares) };
}
