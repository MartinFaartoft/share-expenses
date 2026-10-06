using System.Text.Json;
using SplitIt.Shared;
using SplitIt.Slices.CreateGroup;

namespace SplitIt.Tests.Shared;

/// <summary>
/// Typed ids must be invisible in storage: stored events and documents look exactly
/// as they would with plain Guids, so the wrapper can be changed or removed without
/// touching data.
/// </summary>
public class IdsTests
{
    private static readonly Guid Raw = Guid.Parse("01a0f734-90c1-761a-87e3-07c09ef9be9f");

    [Fact]
    public void Serialises_as_a_bare_guid() =>
        Assert.Equal($"\"{Raw}\"", JsonSerializer.Serialize(GroupId.From(Raw)));

    [Fact]
    public void Round_trips_inside_an_event() =>
        Assert.Equal(
            new MemberClaimed(MemberId.From(Raw), UserId.From(Raw)),
            JsonSerializer.Deserialize<MemberClaimed>(JsonSerializer.Serialize(new MemberClaimed(MemberId.From(Raw), UserId.From(Raw)))));

    [Fact]
    public void Event_json_matches_the_plain_guid_shape() =>
        Assert.Equal(
            $$"""{"MemberId":"{{Raw}}","UserId":"{{Raw}}"}""",
            JsonSerializer.Serialize(new MemberClaimed(MemberId.From(Raw), UserId.From(Raw))));

    [Fact]
    public void Works_as_a_dictionary_key()
    {
        var json = JsonSerializer.Serialize(new Dictionary<MemberId, long> { [MemberId.From(Raw)] = 42 });

        Assert.Equal($$"""{"{{Raw}}":42}""", json);
        Assert.Equal(42, JsonSerializer.Deserialize<Dictionary<MemberId, long>>(json)![MemberId.From(Raw)]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TryParse_rejects_missing_malformed_and_empty(string? input) =>
        Assert.False(GroupId.TryParse(input, out _));

    [Fact]
    public void TryParse_accepts_a_guid()
    {
        Assert.True(GroupId.TryParse(Raw.ToString(), out var id));
        Assert.Equal(GroupId.From(Raw), id);
    }

    [Fact]
    public void Ids_of_different_kinds_never_compare_equal() =>
        Assert.False(MemberId.From(Raw).Equals((object)UserId.From(Raw)));
}
