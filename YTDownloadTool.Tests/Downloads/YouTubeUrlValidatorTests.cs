using Xunit;
using YTDownloadTool.Core.Downloads;

namespace YTDownloadTool.Tests.Downloads;

public sealed class YouTubeUrlValidatorTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=tracking123", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/abcdefghijk", "abcdefghijk", "https://www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://www.youtube.com/live/1234567890A?feature=share", "1234567890A", "https://www.youtube.com/live/1234567890A")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&list=RD123", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void Validate_ValidVideoUrls_ExtractsIdAndNormalizes(string input, string expectedId, string expectedUrl)
    {
        var result = YouTubeUrlValidator.Validate(input);
        Assert.True(result.IsValid);
        Assert.Equal(expectedId, result.VideoId);
        Assert.Equal(expectedUrl, result.NormalizedUrl);
        Assert.False(result.IsChannelLiveUrl);
    }

    [Fact]
    public void Validate_ChannelLiveUrl_IsAccepted()
    {
        var result = YouTubeUrlValidator.Validate("https://www.youtube.com/@NASA/live");
        Assert.True(result.IsValid);
        Assert.True(result.IsChannelLiveUrl);
        Assert.Equal("https://www.youtube.com/@NASA/live", result.NormalizedUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--exec calc.exe")]
    [InlineData("https://evil.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ\n--exec calc")]
    [InlineData("https://www.youtube.com/playlist?list=PL12345")]
    public void Validate_InvalidOrDangerousInputs_AreRejected(string input)
    {
        var result = YouTubeUrlValidator.Validate(input);
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }
}
