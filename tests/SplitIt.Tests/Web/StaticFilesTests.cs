using System.Net;
using SplitIt.Tests.Infrastructure;

namespace SplitIt.Tests.Web;

/// <summary>The stylesheet has no version in its address, so browsers must revalidate it.</summary>
[Collection(AppCollection.Name)]
public class StaticFilesTests(AppFixture app)
{
    [Theory]
    [InlineData("/css/site.css")]
    [InlineData("/js/sheet.js")]
    [InlineData("/js/htmx-2.0.4.min.js")]
    public async Task Static_files_are_revalidated_on_every_load_and_carry_an_etag(string path)
    {
        var response = await app.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task An_unchanged_file_is_answered_304()
    {
        var client = app.CreateClient();
        var first = await client.GetAsync("/css/site.css");

        var request = new HttpRequestMessage(HttpMethod.Get, "/css/site.css");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }
}
