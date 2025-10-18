using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Diagnostics.SourceNavigation;
using DiagnosticsToolsSample;
using Xunit;

namespace SourceNavigation.Tests
{
    public class PortablePdbResolverTests
    {
        [Fact]
        public async Task Resolve_InitializeComponent_ReturnsAxamlDocument()
        {
            var assembly = typeof(MainWindow).Assembly;
            Assert.False(string.IsNullOrEmpty(assembly.Location));

            using var resolver = new PortablePdbResolver(assembly.Location);
            var sourceInfo = await resolver.TryGetSourceInfoAsync(typeof(MainWindow));

            Assert.NotNull(sourceInfo);
            Assert.Equal(SourceOrigin.Local, sourceInfo!.Origin);
            Assert.NotNull(sourceInfo.LocalPath);
            Assert.EndsWith("MainWindow.axaml", sourceInfo.LocalPath, System.StringComparison.OrdinalIgnoreCase);
            Assert.True(sourceInfo.HasLocation);
        }
    }
}
