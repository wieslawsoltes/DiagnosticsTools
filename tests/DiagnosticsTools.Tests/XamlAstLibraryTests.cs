using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public class XamlAstLibraryTests
    {
        [Fact]
        public async Task Workspace_PropagatesProviderDocumentChanged()
        {
            var initial = CreateDocument("<Root xmlns=\"https://github.com/avaloniaui\"></Root>");
            var provider = new StubProvider(initial);

            using var workspace = new XamlAstWorkspace(provider, NullXamlAstInstrumentation.Instance);
            await workspace.GetDocumentAsync(initial.Path);

            var tcs = new TaskCompletionSource<XamlDocumentChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            workspace.DocumentChanged += (_, e) =>
            {
                if (e.Kind == XamlDocumentChangeKind.Updated && e.Document is { Text: var text } && text.Contains("Updated"))
                {
                    tcs.TrySetResult(e);
                }
            };

            provider.PushDocument(CreateDocument("<Root xmlns=\"https://github.com/avaloniaui\"><Updated/></Root>", initial.Path));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcs.Task.WaitAsync(cts.Token);
        }

        [Fact]
        public void DiagnosticMapper_ReturnsCollection()
        {
            const string text = "<Root xmlns=\"https://github.com/avaloniaui\"/>";
            var syntax = Parser.ParseText(text);

            var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);

            Assert.NotNull(diagnostics);
        }

        [Fact]
        public void NodeDiffer_DetectsAddedNode()
        {
            const string original = "<Root xmlns=\"https://github.com/avaloniaui\"><Child/></Root>";
            const string updated = "<Root xmlns=\"https://github.com/avaloniaui\"><Child/><Other/></Root>";

            var originalDoc = CreateDocument(original);
            var updatedDoc = CreateDocument(updated);

            var originalIndex = XamlAstIndex.Build(originalDoc);
            var updatedIndex = XamlAstIndex.Build(updatedDoc);

            var changes = XamlAstNodeDiffer.Diff(originalIndex, updatedIndex);

            Assert.Contains(changes, change => change.Kind == XamlAstNodeChangeKind.Added);
        }

        private static XamlAstDocument CreateDocument(string text, string path = "temp.axaml")
        {
            var syntax = Parser.ParseText(text);
            var version = new XamlDocumentVersion(DateTimeOffset.UtcNow, text.Length, "checksum");
            return new XamlAstDocument(path, text, syntax, version, Array.Empty<XamlAstDiagnostic>());
        }

        private sealed class StubProvider : IXamlAstProvider
        {
            private XamlAstDocument _current;

            public StubProvider(XamlAstDocument initial)
            {
                _current = initial ?? throw new ArgumentNullException(nameof(initial));
            }

            public event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged;
            public event EventHandler<XamlAstNodesChangedEventArgs>? NodesChanged;

            public ValueTask<XamlAstDocument> GetDocumentAsync(string path, CancellationToken cancellationToken = default)
            {
                return ValueTask.FromResult(_current);
            }

            public void Invalidate(string path)
            {
            }

            public void InvalidateAll()
            {
            }

            public void Dispose()
            {
            }

            public void PushDocument(XamlAstDocument document)
            {
                _current = document ?? throw new ArgumentNullException(nameof(document));
                DocumentChanged?.Invoke(this, new XamlDocumentChangedEventArgs(document.Path, XamlDocumentChangeKind.Updated, document));
            }
        }
    }
}
