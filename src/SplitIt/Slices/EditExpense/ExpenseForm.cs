using SplitIt.Shared;
using SplitIt.Web;

namespace SplitIt.Slices.EditExpense;

/// <summary>A member slot as the form offers it.</summary>
public sealed record FormMember(MemberId MemberId, string Name, bool IsYou);

/// <summary>
/// What the Edit expense form shows: the expense as it stands at first, as submitted
/// after a rejection, with its reason. Public: the screen takes it as a component
/// parameter (spec §3).
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
    /// The form for an expense as it stands: its mode, members, shares or amounts. The fields
    /// of the other modes start from something useful — shares 1, and what each person owes
    /// now — so an equal expense can become an exact one from where it is.
    /// </summary>
    public static ExpenseForm Of(State state, GroupId groupId, ExpenseId expenseId, CurrentExpense expense, UserId user, DateOnly today)
    {
        var members = MembersOf(state, user);
        var weights = (expense.Split as SharesSplit)?.Shares.ToDictionary(s => s.MemberId, s => s.Shares);
        var owed = expense.Splits.ToDictionary(s => s.MemberId, s => s.AmountMinor);
        return new ExpenseForm(groupId, state.GroupName, state.Currency, expenseId, members,
            Mode: expense.Split switch
            {
                SharesSplit => SharesMode,
                ExactSplit => ExactMode,
                _ => EqualMode,
            },
            Description: expense.Description,
            Amount: Money.Plain(expense.AmountMinor, state.Currency),
            Payer: expense.PayerMemberId,
            Participants: expense.Split.Members().ToHashSet(),
            Shares: members.ToDictionary(m => m.MemberId,
                m => weights is not null && weights.TryGetValue(m.MemberId, out var weight) ? weight.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1"),
            Amounts: members.ToDictionary(m => m.MemberId,
                m => owed.TryGetValue(m.MemberId, out var minor) ? Money.Plain(minor, state.Currency) : ""),
            PaidOn: Day(expense.PaidOn), LatestPaidOn: Day(today.AddDays(1)), Error: null);
    }

    public ExpenseForm Rejected(string error) => this with { Error = error };

    private static IReadOnlyList<FormMember> MembersOf(State state, UserId user) =>
    [
        .. state.Slots.Select(slot =>
            new FormMember(slot, state.Names[slot], state.Members.TryGetValue(user, out var own) && own == slot)),
    ];

    private static string Day(DateOnly day) => day.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture);
}
