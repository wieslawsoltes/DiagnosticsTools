using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.SourceNavigation;
using DiagnosticsToolsSample;
using Xunit;

namespace SourceNavigation.Tests
{
    public class SourceInfoResolverTests
    {
        [Fact]
        public async Task GetForMemberAsync_ReturnsXamlSource()
        {
            var assembly = typeof(MainWindow).Assembly;
            using var resolver = new SourceInfoResolver();

            var info = await resolver.GetForMemberAsync(typeof(MainWindow));

            Assert.NotNull(info);
            Assert.Equal(SourceOrigin.Local, info!.Origin);
            Assert.NotNull(info.LocalPath);
            Assert.EndsWith("MainWindow.axaml", info.LocalPath, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetForValueFrameAsync_UsesCustomResolver()
        {
            var expected = new SourceInfo(
                LocalPath: "custom-path.axaml",
                RemoteUri: null,
                StartLine: 10,
                StartColumn: 5,
                EndLine: 11,
                EndColumn: 1,
                Origin: SourceOrigin.Generated);

            var invoked = false;
            using var resolver = new SourceInfoResolver(
                valueFrameResolver: (value, token) =>
                {
                    invoked = true;
                    return ValueTask.FromResult<SourceInfo?>(expected);
                });

            var info = await resolver.GetForValueFrameAsync(new object(), CancellationToken.None);

            Assert.True(invoked);
            Assert.Equal(expected, info);
        }
    }
}
