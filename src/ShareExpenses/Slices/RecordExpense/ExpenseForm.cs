using ShareExpenses.Shared;

namespace ShareExpenses.Slices.RecordExpense;

/// <summary>A member slot as the form offers it.</summary>
public sealed record FormMember(MemberId MemberId, string Name, bool IsYou);

/// <summary>
/// What the Add expense form shows: the expense's id, chosen when the form is shown,
/// and what was entered — the defaults at first, as submitted after a rejection, with
/// its reason. Public: the screen takes it as a component parameter (spec §3).
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

    public static ExpenseForm Blank(State state, GroupId groupId, ExpenseId expenseId, UserId user, DateOnly today)
    {
        var members = MembersOf(state, user);
        return new ExpenseForm(
            groupId, state.GroupName, state.Currency, expenseId, members, EqualMode,
            Description: "", Amount: "",
            Payer: members.FirstOrDefault(m => m.IsYou)?.MemberId,
            Participants: members.Select(m => m.MemberId).ToHashSet(),
            PaidOn: Day(today), LatestPaidOn: Day(today.AddDays(1)), Error: null);
    }

    public const string EqualMode = "equal";

    public ExpenseForm Rejected(string error) => this with { Error = error };

    private static IReadOnlyList<FormMember> MembersOf(State state, UserId user) =>
    [
        .. state.Slots.Select(slot =>
            new FormMember(slot, state.Names[slot], state.Members.TryGetValue(user, out var own) && own == slot)),
    ];

    private static string Day(DateOnly day) => day.ToString(DateFormat, System.Globalization.CultureInfo.InvariantCulture);
}
