using System.Text.Json;
using System.Text.Json.Serialization;

namespace SplitIt.Shared;

public interface ITypedId<TSelf> where TSelf : struct, ITypedId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

[JsonConverter(typeof(TypedIdJsonConverter<GroupId>))]
public readonly record struct GroupId(Guid Value) : ITypedId<GroupId>
{
    public static GroupId New() => new(Guid.CreateVersion7());
    public static GroupId From(Guid value) => new(value);
    public static bool TryParse(string? s, out GroupId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(TypedIdJsonConverter<MemberId>))]
public readonly record struct MemberId(Guid Value) : ITypedId<MemberId>
{
    public static MemberId New() => new(Guid.CreateVersion7());
    public static MemberId From(Guid value) => new(value);
    public static bool TryParse(string? s, out MemberId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(TypedIdJsonConverter<InviteId>))]
public readonly record struct InviteId(Guid Value) : ITypedId<InviteId>
{
    public static InviteId New() => new(Guid.CreateVersion7());
    public static InviteId From(Guid value) => new(value);
    public static bool TryParse(string? s, out InviteId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(TypedIdJsonConverter<ExpenseId>))]
public readonly record struct ExpenseId(Guid Value) : ITypedId<ExpenseId>
{
    public static ExpenseId New() => new(Guid.CreateVersion7());
    public static ExpenseId From(Guid value) => new(value);
    public static bool TryParse(string? s, out ExpenseId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(TypedIdJsonConverter<SettlementId>))]
public readonly record struct SettlementId(Guid Value) : ITypedId<SettlementId>
{
    public static SettlementId New() => new(Guid.CreateVersion7());
    public static SettlementId From(Guid value) => new(value);
    public static bool TryParse(string? s, out SettlementId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(TypedIdJsonConverter<UserId>))]
public readonly record struct UserId(Guid Value) : ITypedId<UserId>
{
    public static UserId New() => new(Guid.CreateVersion7());
    public static UserId From(Guid value) => new(value);
    public static bool TryParse(string? s, out UserId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

public sealed class TypedIdJsonConverter<TId> : JsonConverter<TId> where TId : struct, ITypedId<TId>
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TId.From(reader.GetGuid());

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TId.From(Guid.Parse(reader.GetString()!));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value.ToString());
}

internal static class TypedId
{
    public static bool TryParse<TId>(string? s, out TId id) where TId : struct, ITypedId<TId>
    {
        id = Guid.TryParse(s, out var guid) ? TId.From(guid) : default;
        return guid != Guid.Empty;
    }
}
