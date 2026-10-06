using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitIt.Shared;

/// <summary>
/// How an expense is split, as entered — a tagged union, one shape per mode (spec §7),
/// in the API and in the event alike. Its <c>mode</c> is the JSON discriminator, and
/// it is stored: like an event type name, it is fixed forever.
///
/// A malformed split — an unknown or missing mode, a share or amount missing — does
/// not read at all, so deciding only ever sees well-formed ones.
/// </summary>
[JsonConverter(typeof(ExpenseSplitJsonConverter))]
public abstract record ExpenseSplit
{
    /// <summary>Who shares the expense, in the order entered.</summary>
    internal abstract IReadOnlyList<MemberId> Members();

    /// <summary>The same split with its entries in <paramref name="order"/>.</summary>
    internal abstract ExpenseSplit OrderedBy(Func<MemberId, int> order);
}

/// <summary>Split evenly between <paramref name="Participants"/>.</summary>
public sealed record EqualSplit(IReadOnlyList<MemberId> Participants) : ExpenseSplit
{
    internal override IReadOnlyList<MemberId> Members() => Participants;

    internal override ExpenseSplit OrderedBy(Func<MemberId, int> order) =>
        new EqualSplit([.. Participants.OrderBy(order)]);

    // Records compare lists by reference; a split compares by its entries.
    public bool Equals(EqualSplit? other) => other is not null && Participants.SequenceEqual(other.Participants);

    public override int GetHashCode() => Participants.Count;
}

/// <summary>Split in proportion to each member's whole-number share.</summary>
public sealed record SharesSplit(IReadOnlyList<MemberShares> Shares) : ExpenseSplit
{
    internal override IReadOnlyList<MemberId> Members() => [.. Shares.Select(s => s.MemberId)];

    internal override ExpenseSplit OrderedBy(Func<MemberId, int> order) =>
        new SharesSplit([.. Shares.OrderBy(s => order(s.MemberId))]);

    public bool Equals(SharesSplit? other) => other is not null && Shares.SequenceEqual(other.Shares);

    public override int GetHashCode() => Shares.Count;
}

/// <summary>Exact amounts per member, in minor units: the client did the arithmetic (spec §7).</summary>
public sealed record ExactSplit(IReadOnlyList<MemberAmount> Amounts) : ExpenseSplit
{
    internal override IReadOnlyList<MemberId> Members() => [.. Amounts.Select(a => a.MemberId)];

    internal override ExpenseSplit OrderedBy(Func<MemberId, int> order) =>
        new ExactSplit([.. Amounts.OrderBy(a => order(a.MemberId))]);

    public bool Equals(ExactSplit? other) => other is not null && Amounts.SequenceEqual(other.Amounts);

    public override int GetHashCode() => Amounts.Count;
}

/// <summary>
/// Reads and writes <see cref="ExpenseSplit"/> by its <c>mode</c>. Explicit rather than
/// System.Text.Json's built-in polymorphism, which needs the discriminator first in the
/// object and fails with a server error, not a bad request, when it is missing. Here
/// <c>mode</c> may be anywhere, and anything unreadable is a <see cref="JsonException"/>:
/// a 400 over HTTP. Used by Marten and the API alike, whatever their options.
/// </summary>
public sealed class ExpenseSplitJsonConverter : JsonConverter<ExpenseSplit>
{
    private const string Mode = "mode";

    // Stored in every recorded expense: never rename one.
    private static readonly Dictionary<string, Type> Modes = new(StringComparer.Ordinal)
    {
        ["equal"] = typeof(EqualSplit),
        ["shares"] = typeof(SharesSplit),
        ["exact"] = typeof(ExactSplit),
    };

    public override ExpenseSplit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object
            || !json.TryGetProperty(Mode, out var mode)
            || mode.ValueKind != JsonValueKind.String
            || !Modes.TryGetValue(mode.GetString()!, out var type))
            throw new JsonException($"a split needs a mode: {string.Join(", ", Modes.Keys)}");

        return (ExpenseSplit)(json.Deserialize(type, options) ?? throw new JsonException("a split cannot be null"));
    }

    public override void Write(Utf8JsonWriter writer, ExpenseSplit value, JsonSerializerOptions options)
    {
        var mode = Modes.Single(m => m.Value == value.GetType()).Key;
        writer.WriteStartObject();
        writer.WriteString(Mode, mode);
        foreach (var property in JsonSerializer.SerializeToElement(value, value.GetType(), options).EnumerateObject())
            property.WriteTo(writer);
        writer.WriteEndObject();
    }
}

public sealed record MemberShares(MemberId MemberId, [property: JsonRequired] int Shares);

public sealed record MemberAmount(MemberId MemberId, [property: JsonRequired] long AmountMinor);

/// <summary>One person's part of an expense, in minor units: a recorded fact (spec §6).</summary>
public sealed record Split(MemberId MemberId, long AmountMinor);

/// <summary>
/// How an expense is split (spec §6) — one pure function, shared by every slice that
/// computes splits, so a correction can never round differently from the original.
///
/// Splits sum exactly to the total. Leftover minor units go one each: payer first, if
/// the payer shares the expense, then the other participants in member-added order.
/// For shares, leftovers go by largest fractional remainder, and that same order
/// breaks ties. Arithmetic is 128-bit, so <c>total × share</c> cannot overflow.
/// </summary>
internal static class Splits
{
    /// <summary>Splits <paramref name="total"/> evenly.</summary>
    /// <param name="participants">In member-added order; distinct; at least one.</param>
    /// <returns>One split per participant, in the same order.</returns>
    public static IReadOnlyList<Split> SplitEqually(long total, MemberId payer, IReadOnlyList<MemberId> participants)
    {
        var count = participants.Count;
        var share = total / count;
        var leftover = (int)(total % count);
        var extra = LeftoverOrder(payer, participants).Take(leftover).ToHashSet();

        return participants.Select(m => new Split(m, share + (extra.Contains(m) ? 1 : 0))).ToList();
    }

    /// <summary>Splits <paramref name="total"/> in proportion to positive whole-number shares.</summary>
    /// <param name="participants">In member-added order; distinct; at least one; every share positive.</param>
    /// <returns>One split per participant, in the same order.</returns>
    public static IReadOnlyList<Split> SplitByShares(
        long total, MemberId payer, IReadOnlyList<(MemberId Member, int Share)> participants)
    {
        Int128 totalShares = participants.Sum(p => (long)p.Share);
        var exact = participants
            .Select(p => (p.Member, Product: (Int128)total * p.Share))
            .Select(p => (p.Member, Floor: (long)(p.Product / totalShares), Remainder: p.Product % totalShares))
            .ToList();

        // Every remainder shares the denominator, so comparing them compares the fractions.
        var leftover = (int)(total - exact.Sum(p => p.Floor));
        var order = LeftoverOrder(payer, participants.Select(p => p.Member).ToList())
            .Select((member, index) => (member, index))
            .ToDictionary(x => x.member, x => x.index);
        var extra = exact
            .OrderByDescending(p => p.Remainder)
            .ThenBy(p => order[p.Member])
            .Take(leftover)
            .Select(p => p.Member)
            .ToHashSet();

        return exact.Select(p => new Split(p.Member, p.Floor + (extra.Contains(p.Member) ? 1 : 0))).ToList();
    }

    /// <summary>Who gets leftover minor units first: the payer, if sharing, then member-added order.</summary>
    private static IEnumerable<MemberId> LeftoverOrder(MemberId payer, IReadOnlyList<MemberId> participants) =>
        participants.Contains(payer)
            ? participants.Where(m => m == payer).Concat(participants.Where(m => m != payer))
            : participants;
}
