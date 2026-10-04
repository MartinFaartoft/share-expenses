using System.Net;
using System.Text.RegularExpressions;

namespace ShareExpenses.Tests.Infrastructure;

/// <summary>What a browser does with a form: take the antiforgery token a page carries, and post it back.</summary>
public static partial class Forms
{
    public const string TokenField = "__RequestVerificationToken";

    /// <summary>The token on the page at <paramref name="path"/>, for the browser's own antiforgery cookie.</summary>
    public static async Task<string> TokenFrom(HttpClient browser, string path) =>
        TokenIn(await (await browser.GetAsync(path)).Content.ReadAsStringAsync())
        ?? throw new InvalidOperationException($"No antiforgery token on {path}");

    public static string? TokenIn(string html) =>
        TokenPattern().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    /// <summary>Posts a form, as a browser submitting it would.</summary>
    public static Task<HttpResponseMessage> Post(
        HttpClient browser, string path, string token, params (string Name, string Value)[] fields) =>
        browser.PostAsync(path, new FormUrlEncodedContent(
            fields.Append((TokenField, token)).Select(f => KeyValuePair.Create(f.Item1, f.Item2))));

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
