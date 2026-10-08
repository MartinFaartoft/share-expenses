using SplitIt.Shared;

namespace SplitIt.Slices.RecordExpense;

/// <summary>A member slot as the form offers it.</summary>
public sealed record FormMember(MemberId MemberId, string Name, bool IsYou);

/// <summary>
/// What the Add expense form shows: the expense's id, chosen when the form is shown,
/// and what was entered — the defaults at first, as submitted after a rejection, with
/// its reason. Public: the screen takes it as a component parameter (spec §3).
/// </summary>
/// <param name="Mode">The split mode's name, as stored (<c>equal</c>, <c>shares</c>, <c>exact</c>); which row field the form shows.</param>
/// <param name="Amount">As typed.</param>
/// <param name="Participants">Who is in the split, whatever the mode.</param>
/// <param name="Shares">Each slot's shares field, as typed — for every slot, so a mode not selected keeps what was in it.</param>
/// <param name="Amounts">Each slot's exact-amount field, as typed.</param>
/// <param name="PaidOn">As posted: <c>yyyy-MM-dd</c>, or whatever a forged post sent.</param>
/// <param name="LatestPaidOn">The last day deciding accepts, so the date picker stops there.</param>
public sealed record ExpenseForm(
    GroupId GroupId,
    string GroupName,
    string Currency,
    ExpenseId ExpenseId,
    IReadOnlyList<FormMember> Members,
    string Mode,
    string Description,
    string Amount,
    MemberId? Payer,
    IReadOnlySet<MemberId> Participants,
    IReadOnlyDictionary<MemberId, string> Shares,
    IReadOnlyDictionary<MemberId, string> Amounts,
    string PaidOn,
    string LatestPaidOn,
    string? Error)
{
    public const string DateFormat = "yyyy-MM-dd";

    public const string EqualMode = "equal";
    public const string SharesMode = "shares";
    public const string ExactMode = "exact";

    public static bool IsMode(string mode) => mode is EqualMode or SharesMode or ExactMode;

    /// <summary>
    /// The form as it first opens: the group's default split (slice-16) — its mode, the members
    /// left out unchecked, and the shares typed in, 1 for anyone not listed, so a member added
    /// later starts in. Exact is never the default. Until the group sets one, that is Equally,
    /// everyone, shares 1.
    /// </summary>
    public static ExpenseForm Blank(State state, GroupId groupId, ExpenseId expenseId, UserId user, DateOnly today)
    {
        var members = MembersOf(state, user);
        int ShareOf(MemberId member)
        {
            var share = state.DefaultShares.TryGetValue(member, out var listed) ? listed : 1;
            return state.DefaultMode == EqualMode ? Math.Min(share, 1) : share;
        }

        return new ExpenseForm(
            groupId, state.GroupName, state.Currency, expenseId, members, state.DefaultMode,
            Description: "", Amount: "",
            Payer: members.FirstOrDefault(m => m.IsYou)?.MemberId,
            Participants: members.Where(m => ShareOf(m.MemberId) > 0).Select(m => m.MemberId).ToHashSet(),
            Shares: members.ToDictionary(m => m.MemberId,
                m => (ShareOf(m.MemberId) is > 0 and var share ? share : 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Amounts: members.ToDictionary(m => m.MemberId, _ => ""),
            PaidOn: Day(today), LatestPaidOn: Day(today.AddDays(1)), Error: null);
    }

    public ExpenseForm Rejected(string error) => this with { Error = error };

    private static IReadOnlyList<FormMember> MembersOf(State state, UserId user) =>
    [
        .. state.Slots.Select(slot =>
            new FormMember(slot, state.Names[slot], state.Members.TryGetValue(user, out var own) && own == slot)),
    ];

    private static string Day(DateOnly day) => day.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture);
}
