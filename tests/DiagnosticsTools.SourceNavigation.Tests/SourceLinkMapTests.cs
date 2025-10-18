using System;
using System.Text;
using Avalonia.Diagnostics.SourceNavigation;
using Xunit;

namespace SourceNavigation.Tests
{
    public class SourceLinkMapTests
    {
        [Fact]
        public void TryLoad_ReturnsNullForEmptyPayload()
        {
            var map = SourceLinkMap.TryLoad(ReadOnlySpan<byte>.Empty);
            Assert.Null(map);
        }

        [Fact]
        public void TryResolve_ExpandsWildcards()
        {
            const string json = "{\n  \"documents\": {\n    \"/src/*\": \"https://example.com/repo/*\"\n  }\n}";
            var map = SourceLinkMap.TryLoad(Encoding.UTF8.GetBytes(json));
            Assert.NotNull(map);

            var uri = map!.TryResolve("/src/Views/MainWindow.axaml");
            Assert.NotNull(uri);
            Assert.Equal("https://example.com/repo/Views/MainWindow.axaml", uri!.ToString());
        }
    }
}
