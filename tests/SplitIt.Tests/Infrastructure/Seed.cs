using Marten;
using Microsoft.Extensions.DependencyInjection;
using SplitIt.Infrastructure.Invites;
using SplitIt.Shared;
using ExpenseRecorded = SplitIt.Slices.RecordExpense.ExpenseRecorded;
using GroupCreated = SplitIt.Slices.CreateGroup.GroupCreated;
using MemberAdded = SplitIt.Slices.CreateGroup.MemberAdded;
using MemberClaimed = SplitIt.Slices.CreateGroup.MemberClaimed;
using MemberInvited = SplitIt.Slices.AddMember.MemberInvited;
using SettlementRecorded = SplitIt.Slices.RecordSettlement.SettlementRecorded;

namespace SplitIt.Tests.Infrastructure;

/// <summary>
/// Sets up a group by appending its events straight to the store, as the slices that
/// emit them would — so a test of one screen does not depend on any other slice's
/// screen. Inline projections run as on any save; asynchronous ones need
/// <see cref="AppFixture.ProjectionsCaughtUp"/>.
///
/// It decides nothing: the events are taken as given, so keep them to what the
/// emitting slice would accept (its specs check that).
/// </summary>
public sealed class Seed(AppFixture app)
{
    private IDocumentStore Store => app.Services.GetRequiredService<IDocumentStore>();

    /// <summary>A new group, created by <paramref name="creator"/>, who joins it as <paramref name="creatorName"/>.</summary>
    public async Task<SeededGroup> Group(
        UserId creator, string name = "Lisbon trip", string currency = "GBP", string creatorName = "Alice")
    {
        var groupId = GroupId.New();
        var you = MemberId.New();
        await using var session = Store.LightweightSession();
        session.Events.StartStream(groupId.Value,
            new GroupCreated(groupId, name, currency, creator),
            new MemberAdded(you, creatorName, creator),
            new MemberClaimed(you, creator));
        await session.SaveChangesAsync();
        return new SeededGroup(this, groupId, creator, you);
    }

    internal async Task Append(GroupId groupId, params object[] events)
    {
        await using var session = Store.LightweightSession();
        session.Events.Append(groupId.Value, events);
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// An invite: <c>MemberInvited</c> and the <see cref="Invite"/> document that binds the
    /// slot to the address, in one transaction, replacing any earlier invite for the slot.
    /// </summary>
    internal async Task Invite(GroupId groupId, MemberId member, string email, UserId by)
    {
        var inviteId = InviteId.New();
        await using var session = Store.LightweightSession();
        session.DeleteWhere<Invite>(i => i.GroupId == groupId && i.MemberId == member);
        session.Store(new Invite
        {
            Id = inviteId.Value,
            GroupId = groupId,
            MemberId = member,
            Email = email,
            NormalizedEmail = EmailAddress.Normalize(email),
        });
        session.Events.Append(groupId.Value, new MemberInvited(member, inviteId, app.Clock.GetUtcNow() + Invitations.Lifetime, by));
        await session.SaveChangesAsync();
    }
}

/// <summary>A seeded group, acting as <see cref="By"/> (its creator) unless told otherwise.</summary>
/// <param name="You">The creator's own slot.</param>
public sealed record SeededGroup(Seed Seed, GroupId Id, UserId By, MemberId You)
{
    /// <summary>A placeholder slot.</summary>
    public async Task<MemberId> Member(string name)
    {
        var member = MemberId.New();
        await Seed.Append(Id, new MemberAdded(member, name, By));
        return member;
    }

    /// <summary>A slot, claimed by <paramref name="user"/>: a member who has joined.</summary>
    public async Task<MemberId> Joined(string name, UserId user)
    {
        var member = MemberId.New();
        await Seed.Append(Id, new MemberAdded(member, name, By), new MemberClaimed(member, user));
        return member;
    }

    public Task Invite(MemberId member, string email) => Seed.Invite(Id, member, email, By);

    public Task Claim(MemberId member, UserId user) => Seed.Append(Id, new MemberClaimed(member, user));

    /// <summary>An expense split equally, as RecordExpense computes it.</summary>
    public async Task<ExpenseId> Expense(
        string description, long amountMinor, MemberId payer, MemberId[] sharers, DateOnly? paidOn = null)
    {
        var expenseId = ExpenseId.New();
        await Seed.Append(Id, new ExpenseRecorded(
            expenseId, description, amountMinor, payer, new EqualSplit(sharers),
            Splits.SplitEqually(amountMinor, payer, sharers), paidOn ?? new DateOnly(2026, 10, 1), By));
        return expenseId;
    }

    public async Task<SettlementId> Settlement(MemberId from, MemberId to, long amountMinor, DateOnly? paidOn = null)
    {
        var settlementId = SettlementId.New();
        await Seed.Append(Id, new SettlementRecorded(settlementId, from, to, amountMinor, paidOn ?? new DateOnly(2026, 10, 2), By));
        return settlementId;
    }
}
