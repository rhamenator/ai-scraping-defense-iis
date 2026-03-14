using System.Net.Http.Headers;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class HttpCommunityBlocklistFeedClientTests
{
    [Fact]
    public void IsPlainText_ReturnsTrueForTextPlain()
    {
        var contentType = MediaTypeHeaderValue.Parse("text/plain");
        Assert.True(HttpCommunityBlocklistFeedClient.IsPlainText(contentType));
    }

    [Fact]
    public void IsPlainText_ReturnsTrueForTextPlainWithCharset()
    {
        var contentType = MediaTypeHeaderValue.Parse("text/plain; charset=utf-8");
        Assert.True(HttpCommunityBlocklistFeedClient.IsPlainText(contentType));
    }

    [Fact]
    public void IsPlainText_ReturnsFalseForApplicationJson()
    {
        var contentType = MediaTypeHeaderValue.Parse("application/json");
        Assert.False(HttpCommunityBlocklistFeedClient.IsPlainText(contentType));
    }

    [Fact]
    public void IsPlainText_ReturnsFalseForNull()
    {
        Assert.False(HttpCommunityBlocklistFeedClient.IsPlainText(null));
    }

    [Fact]
    public void ReadPlainTextIps_ParsesNewlineDelimitedList()
    {
        var text = "1.2.3.4\n5.6.7.8\n9.10.11.12\n";
        var result = HttpCommunityBlocklistFeedClient.ReadPlainTextIps(text);
        Assert.Equal(["1.2.3.4", "5.6.7.8", "9.10.11.12"], result);
    }

    [Fact]
    public void ReadPlainTextIps_StripsCommentLines()
    {
        var text = "# This is a comment\n1.2.3.4\n# Another comment\n5.6.7.8\n";
        var result = HttpCommunityBlocklistFeedClient.ReadPlainTextIps(text);
        Assert.Equal(["1.2.3.4", "5.6.7.8"], result);
    }

    [Fact]
    public void ReadPlainTextIps_SkipsBlankLines()
    {
        var text = "1.2.3.4\n\n\n5.6.7.8\n";
        var result = HttpCommunityBlocklistFeedClient.ReadPlainTextIps(text);
        Assert.Equal(["1.2.3.4", "5.6.7.8"], result);
    }

    [Fact]
    public void ReadPlainTextIps_TrimsWhitespace()
    {
        var text = "  1.2.3.4  \n  5.6.7.8  \n";
        var result = HttpCommunityBlocklistFeedClient.ReadPlainTextIps(text);
        Assert.Equal(["1.2.3.4", "5.6.7.8"], result);
    }

    [Fact]
    public void ReadPlainTextIps_HandlesCrLfLineEndings()
    {
        var text = "1.2.3.4\r\n5.6.7.8\r\n";
        var result = HttpCommunityBlocklistFeedClient.ReadPlainTextIps(text);
        Assert.Equal(["1.2.3.4", "5.6.7.8"], result);
    }

    [Fact]
    public void ReadPlainTextIps_ReturnsEmptyForEmptyInput()
    {
        var result = HttpCommunityBlocklistFeedClient.ReadPlainTextIps(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadPlainTextIps_ReturnsEmptyForCommentOnlyInput()
    {
        var text = "# only comments\n# another comment\n";
        var result = HttpCommunityBlocklistFeedClient.ReadPlainTextIps(text);
        Assert.Empty(result);
    }
}
