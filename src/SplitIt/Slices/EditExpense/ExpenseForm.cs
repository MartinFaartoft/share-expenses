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
/// <param name="Mode">The split mode's name, as stored (<c>equal</c>); which fields the form shows.</param>
/// <param name="Amount">As typed.</param>
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
    string PaidOn,
    string LatestPaidOn,
    string? Error)
{
    public const string DateFormat = "yyyy-MM-dd";

    public const string EqualMode = "equal";

    public static ExpenseForm Of(State state, GroupId groupId, ExpenseId expenseId, CurrentExpense expense, UserId user, DateOnly today) =>
        new(groupId, state.GroupName, state.Currency, expenseId, MembersOf(state, user), EqualMode,
            Description: expense.Description,
            Amount: Money.Plain(expense.AmountMinor, state.Currency),
            Payer: expense.PayerMemberId,
            Participants: expense.Split.Members().ToHashSet(),
            PaidOn: Day(expense.PaidOn), LatestPaidOn: Day(today.AddDays(1)), Error: null);

    public ExpenseForm Rejected(string error) => this with { Error = error };

    private static IReadOnlyList<FormMember> MembersOf(State state, UserId user) =>
    [
        .. state.Slots.Select(slot =>
            new FormMember(slot, state.Names[slot], state.Members.TryGetValue(user, out var own) && own == slot)),
    ];

    private static string Day(DateOnly day) => day.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture);
}
