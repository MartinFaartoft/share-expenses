using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShareExpenses.Shared;

// Typed ids (spec §12): a Guid that knows what it identifies, so a member id cannot
// be passed where a user id belongs. Hand-written on purpose — few types, nothing
// generated. Each one serialises as a bare Guid, so stored events and documents
// look exactly as they would with plain Guids; the wrapper exists only in code.
//
// Marten takes Guid stream ids, so stream calls pass `groupId.Value`. Everything
// else — event payloads, folded state, documents, LINQ — uses the typed id.

/// <summary>Implemented by every typed id; lets one JSON converter serve them all.</summary>
public interface ITypedId<TSelf> where TSelf : struct, ITypedId<TSelf>
{
    Guid Value { get; }

    static abstract TSelf From(Guid value);
}

/// <summary>A group, and the id of its event stream.</summary>
[JsonConverter(typeof(TypedIdJsonConverter<GroupId>))]
public readonly record struct GroupId(Guid Value) : ITypedId<GroupId>
{
    public static GroupId New() => new(Guid.CreateVersion7());
    public static GroupId From(Guid value) => new(value);
    public static bool TryParse(string? s, out GroupId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

/// <summary>A member slot within one group. Expenses reference members, never users (spec §4).</summary>
[JsonConverter(typeof(TypedIdJsonConverter<MemberId>))]
public readonly record struct MemberId(Guid Value) : ITypedId<MemberId>
{
    public static MemberId New() => new(Guid.CreateVersion7());
    public static MemberId From(Guid value) => new(value);
    public static bool TryParse(string? s, out MemberId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

/// <summary>A login identity. Recorded as the actor (<c>by</c>) and on claims.</summary>
[JsonConverter(typeof(TypedIdJsonConverter<UserId>))]
public readonly record struct UserId(Guid Value) : ITypedId<UserId>
{
    public static UserId New() => new(Guid.CreateVersion7());
    public static UserId From(Guid value) => new(value);
    public static bool TryParse(string? s, out UserId id) => TypedId.TryParse(s, out id);
    public override string ToString() => Value.ToString();
}

/// <summary>Writes and reads a typed id as a bare Guid, including as a dictionary key.</summary>
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
    /// <summary>Route/query binding: ASP.NET finds each id's static <c>TryParse</c>.</summary>
    public static bool TryParse<TId>(string? s, [MaybeNullWhen(false)] out TId id) where TId : struct, ITypedId<TId>
    {
        id = Guid.TryParse(s, out var guid) ? TId.From(guid) : default;
        return guid != Guid.Empty;
    }
}
