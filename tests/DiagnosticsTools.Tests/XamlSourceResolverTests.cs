using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Diagnostics.SourceNavigation;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public class XamlSourceResolverTests
    {
        private sealed class StubPathBuilder : ILogicalTreePathBuilder
        {
            public bool TryBuildPath(object candidate, out object? root, out IReadOnlyList<int> path)
            {
                root = new StubRoot();
                path = new[] { 0 };
                return true;
            }
        }

        private sealed class StubLocator : IXamlDocumentLocator
        {
            public ValueTask<XamlDocumentResult?> GetDocumentAsync(XamlDocumentRequest request, CancellationToken cancellationToken = default)
            {
                const string markup = "<Root>\n  <Child />\n</Root>";
                var document = XDocument.Parse(markup, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
                var source = new SourceInfo(
                    LocalPath: "Stub.axaml",
                    RemoteUri: null,
                    StartLine: null,
                    StartColumn: null,
                    EndLine: null,
                    EndColumn: null,
                    Origin: SourceOrigin.Local);

                return ValueTask.FromResult<XamlDocumentResult?>(new XamlDocumentResult(document, source));
            }
        }

        private sealed class StubRoot
        {
        }

        [Fact]
        public async Task TryResolveAsync_ReturnsSourceInfoWithLocalPath()
        {
            var resolver = new XamlSourceResolver(
                new StubPathBuilder(),
                new StubLocator(),
                (type, cancellationToken) => ValueTask.FromResult<SourceInfo?>(new SourceInfo(
                    LocalPath: "Stub.axaml",
                    RemoteUri: null,
                    StartLine: null,
                    StartColumn: null,
                    EndLine: null,
                    EndColumn: null,
                    Origin: SourceOrigin.Local)));

            var sourceInfo = await resolver.TryResolveAsync(new object());

            Assert.NotNull(sourceInfo);
            Assert.Equal("Stub.axaml", sourceInfo!.LocalPath);
            Assert.Equal(SourceOrigin.Local, sourceInfo.Origin);
        }
    }
}
