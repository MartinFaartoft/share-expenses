using ShareExpenses.Infrastructure;
using ShareExpenses.Shared;

namespace ShareExpenses.Tests.Shared;

public class InviteTokenTests
{
    [Fact]
    public void Tokens_are_url_safe_256_bit_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => InviteToken.Generate().Token).ToList();

        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void The_hash_is_lowercase_hex_sha256_of_the_token()
    {
        var (token, hash) = InviteToken.Generate();

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(InviteToken.Hash(token), hash);
        Assert.DoesNotContain(token, hash);
    }

    [Fact]
    public void Matches_accepts_the_token_and_nothing_else()
    {
        var (token, hash) = InviteToken.Generate();

        Assert.True(InviteToken.Matches(token, hash));
        Assert.False(InviteToken.Matches(token + "x", hash));
        Assert.False(InviteToken.Matches(InviteToken.Generate().Token, hash));
        Assert.False(InviteToken.Matches(null, hash));
        Assert.False(InviteToken.Matches(hash, hash)); // the hash is not itself a token
    }
}

public class PublicOriginTests
{
    [Fact]
    public void Required_outside_development() =>
        Assert.Throws<InvalidOperationException>(() => PublicOrigin.Validate(null, isDevelopment: false));

    [Fact]
    public void Optional_in_development() =>
        Assert.Null(PublicOrigin.Validate("", isDevelopment: true));

    [Theory]
    [InlineData("https://share.example.com", "https://share.example.com")]
    [InlineData("https://share.example.com/", "https://share.example.com")]
    [InlineData("http://localhost:5263", "http://localhost:5263")]
    public void Normalises_to_scheme_and_host(string configured, string expected) =>
        Assert.Equal(expected, PublicOrigin.Validate(configured, isDevelopment: false));

    [Theory]
    [InlineData("share.example.com")]
    [InlineData("ftp://share.example.com")]
    [InlineData("https://share.example.com/app")]
    [InlineData("https://share.example.com/?x=1")]
    public void Rejects_anything_but_scheme_and_host(string configured) =>
        Assert.Throws<InvalidOperationException>(() => PublicOrigin.Validate(configured, isDevelopment: false));
}
