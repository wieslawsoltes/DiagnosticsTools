using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Threading;
using Microsoft.Language.Xml;
using Xunit;

namespace DiagnosticsTools.Tests;

public class SourcePreviewViewModelTests
{
    public SourcePreviewViewModelTests()
    {
        ResetSplitState();
    }

    [Fact]
    public async Task LoadAsync_UsesXamlAstSelectionAndSetsPreciseHighlight()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid>
    <Button x:Name="Foo" Content="Bar" />
  </Grid>
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        if (!ReferenceEquals(normalized, xaml))
        {
            xaml = normalized;
        }

        var syntax = Parser.ParseText(xaml);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xaml)));
        var version = new XamlDocumentVersion(DateTimeOffset.UtcNow, xaml.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", xaml, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptor = index.Nodes.First(node => node.LocalName == "Button");
        var selection = new XamlAstSelection(document, descriptor);

        var sourceInfo = new SourceInfo(
            LocalPath: "/tmp/MainWindow.axaml",
            RemoteUri: null,
            StartLine: descriptor.LineSpan.Start.Line,
            StartColumn: descriptor.LineSpan.Start.Column,
            EndLine: descriptor.LineSpan.End.Line,
            EndColumn: descriptor.LineSpan.End.Column,
            Origin: SourceOrigin.Local);

        var navigator = new StubSourceNavigator();
        var viewModel = new SourcePreviewViewModel(sourceInfo, navigator, selection);

        await viewModel.LoadAsync();

        Assert.Equal(xaml, viewModel.Snippet);
        Assert.False(viewModel.IsLoading);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Equal(descriptor.LineSpan.Start.Line, viewModel.HighlightedStartLine);
        Assert.Equal(descriptor.LineSpan.End.Line, viewModel.HighlightedEndLine);
        Assert.Equal(descriptor.Span.Start, viewModel.HighlightSpanStart);
        Assert.Equal(descriptor.Span.Length, viewModel.HighlightSpanLength);
    }

    [Fact]
    public void SplitState_PersistsAcrossInstances()
    {
        var navigator = new StubSourceNavigator();
        var info = CreateSourceInfo("view.axaml");

        var first = new SourcePreviewViewModel(info, navigator);
        first.IsSplitViewEnabled = true;
        first.SplitRatio = 0.7;
        first.SplitOrientation = SourcePreviewSplitOrientation.Vertical;
        first.SplitRatio = 0.3;

        var second = new SourcePreviewViewModel(info, navigator);

        Assert.True(second.IsSplitViewEnabled);
        Assert.Equal(SourcePreviewSplitOrientation.Vertical, second.SplitOrientation);
        Assert.True(Math.Abs(second.SplitRatio - 0.3) < 0.0001);

        second.SplitOrientation = SourcePreviewSplitOrientation.Horizontal;
        Assert.True(Math.Abs(second.SplitRatio - 0.7) < 0.0001);
    }

    [Fact]
    public void RuntimeComparison_ReEnablesSplitViewWhenHistoryRequestsIt()
    {
        SetSplitDefaults(isEnabled: true, horizontalRatio: 0.6, verticalRatio: 0.4, orientation: SourcePreviewSplitOrientation.Horizontal);

        var navigator = new StubSourceNavigator();
        var info = CreateSourceInfo("primary.axaml");
        var primary = new SourcePreviewViewModel(info, navigator);

        SuppressSplitPersistence(primary, suppress: true);
        primary.IsSplitViewEnabled = false;
        SuppressSplitPersistence(primary, suppress: false);

        Assert.False(primary.IsSplitViewEnabled);

        var runtime = new SourcePreviewViewModel(CreateRuntimeInfo(), navigator);
        runtime.SetManualSnippet("Runtime snapshot");

        primary.RuntimeComparison = runtime;

        Assert.True(primary.IsSplitViewEnabled);
        Assert.Same(runtime, primary.RuntimeComparison);
    }

    [Fact]
    public async Task ManualSnippet_SkipsLoad()
    {
        var navigator = new StubSourceNavigator();
        var info = CreateSourceInfo("manual.axaml");
        var viewModel = new SourcePreviewViewModel(info, navigator);

        viewModel.SetManualSnippet("Line1", 10);

        await viewModel.LoadAsync();

        Assert.Equal("Line1", viewModel.Snippet);
        Assert.Equal(10, viewModel.SnippetStartLine);
        Assert.True(viewModel.HasSnippet);
        Assert.False(viewModel.IsLoading);
        Assert.Null(viewModel.ErrorMessage);
        Assert.Null(viewModel.HighlightedStartLine);
        Assert.Null(viewModel.HighlightSpanStart);
    }

    [Fact]
    public async Task UpdateSelectionFromTree_RefreshesHighlight()
    {
        var (document, index) = CreateDocument();
        var gridDescriptor = index.Nodes.First(node => node.LocalName == "Grid");
        var buttonDescriptor = index.Nodes.First(node => node.LocalName == "Button");
        var selection = new XamlAstSelection(document, gridDescriptor, index.Nodes.ToList());
        var navigator = new StubSourceNavigator();
        var info = CreateSourceInfo(document.Path);
        var viewModel = new SourcePreviewViewModel(info, navigator, selection);

        await viewModel.LoadAsync();

        var updatedSelection = new XamlAstSelection(document, buttonDescriptor, index.Nodes.ToList());
        viewModel.UpdateSelectionFromTree(updatedSelection);

        Assert.Same(buttonDescriptor, viewModel.AstSelection?.Node);
        Assert.Equal(buttonDescriptor.LineSpan.Start.Line, viewModel.HighlightedStartLine);
        Assert.Equal(buttonDescriptor.LineSpan.End.Line, viewModel.HighlightedEndLine);
    }

    [Fact]
    public void NotifyEditorSelectionChanged_PropagatesToSynchronizer()
    {
        var (document, index) = CreateDocument();
        var gridDescriptor = index.Nodes.First(node => node.LocalName == "Grid");
        var buttonDescriptor = index.Nodes.First(node => node.LocalName == "Button");
        var selection = new XamlAstSelection(document, gridDescriptor, index.Nodes.ToList());
        var navigator = new StubSourceNavigator();
        var info = CreateSourceInfo(document.Path);
        XamlAstSelection? forwarded = null;
        var viewModel = new SourcePreviewViewModel(
            info,
            navigator,
            selection,
            navigateToAst: null,
            synchronizeSelection: sel => forwarded = sel);

        viewModel.NotifyEditorSelectionChanged(buttonDescriptor);

        Assert.Same(buttonDescriptor, forwarded?.Node);
        Assert.Same(buttonDescriptor, viewModel.AstSelection?.Node);
    }

    [Fact]
    public async Task WorkspaceDiagnosticsUpdate_PropagatesToErrorMessage()
    {
        var (document, index) = CreateDocument();
        var provider = new TestXamlAstProvider();
        provider.SetDocument(document);
        var workspace = new XamlAstWorkspace(provider);
        var descriptor = index.Nodes.First(node => node.LocalName == "Button");
        var selection = new XamlAstSelection(document, descriptor, index.Nodes.ToList());
        var navigator = new StubSourceNavigator();
        var info = CreateSourceInfo(document.Path);
        var viewModel = new SourcePreviewViewModel(info, navigator, selection, xamlAstWorkspace: workspace);

        await viewModel.LoadAsync();
        Assert.Null(viewModel.ErrorMessage);

        var diagnostics = new[]
        {
            new XamlAstDiagnostic(new TextSpan(0, 1), XamlDiagnosticSeverity.Error, ERRID.ERR_None, "Parse failure")
        };
        var errorDocument = BuildDocument(document.Text, document.Path, document.Version.TimestampUtc.AddSeconds(1), diagnostics);
        provider.RaiseDocumentChanged(errorDocument);

        await FlushDispatcherAsync();

        Assert.Equal("Parse failure", viewModel.ErrorMessage);

        var cleanDocument = BuildDocument(document.Text, document.Path, errorDocument.Version.TimestampUtc.AddSeconds(1));
        provider.RaiseDocumentChanged(cleanDocument);

        await FlushDispatcherAsync();
        await FlushDispatcherAsync();

        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task WorkspaceNodesChanged_RefreshesSelectionAndSnippet()
    {
        var (document, index) = CreateDocument();
        var provider = new TestXamlAstProvider();
        provider.SetDocument(document);
        var workspace = new XamlAstWorkspace(provider);
        var descriptor = index.Nodes.First(node => node.LocalName == "Button");
        var selection = new XamlAstSelection(document, descriptor, index.Nodes.ToList());
        var navigator = new StubSourceNavigator();
        var info = CreateSourceInfo(document.Path);
        var viewModel = new SourcePreviewViewModel(info, navigator, selection, xamlAstWorkspace: workspace);

        await viewModel.LoadAsync();
        Assert.Equal(document.Text, viewModel.Snippet);

        var updatedXaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid>
    <Border x:Name="RuntimeBorder" />
    <Button x:Name="Foo" Content="Baz" />
  </Grid>
</UserControl>
""";
        var updatedDocument = BuildDocument(updatedXaml, document.Path, document.Version.TimestampUtc.AddSeconds(1));
        var updatedIndex = XamlAstIndex.Build(updatedDocument);
        var changes = XamlAstNodeDiffer.Diff(index, updatedIndex);
        provider.SetDocument(updatedDocument);
        provider.RaiseNodesChanged(updatedDocument.Path, updatedDocument.Version, changes);

        for (var attempt = 0; attempt < 5 && viewModel.Snippet != updatedDocument.Text; attempt++)
        {
            await FlushDispatcherAsync();
        }

        var updatedDescriptor = updatedIndex.Nodes.First(node => node.LocalName == "Button");

        Assert.NotNull(viewModel.AstSelection);
        Assert.Equal(updatedDocument.Version, viewModel.AstSelection?.Document.Version);
        Assert.Equal(updatedDescriptor.Span, viewModel.AstSelection?.Node?.Span);
        Assert.Equal(updatedDocument.Text, viewModel.Snippet);
    }

    private static SourceInfo CreateSourceInfo(string path) =>
        new(path, null, 1, 1, 1, 1, SourceOrigin.Local);

    private static SourceInfo CreateRuntimeInfo() =>
        new("Runtime Snapshot", null, null, null, null, null, SourceOrigin.Generated);

    private static (XamlAstDocument Document, XamlAstIndex Index) CreateDocument()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid>
    <Button x:Name="Foo" Content="Bar" />
  </Grid>
</UserControl>
""";
        var document = BuildDocument(xaml, "/tmp/MainWindow.axaml");
        var index = XamlAstIndex.Build(document);
        return (document, index);
    }

    private static XamlAstDocument BuildDocument(string xaml, string path, DateTimeOffset? timestamp = null, IReadOnlyList<XamlAstDiagnostic>? diagnostics = null)
    {
        var normalized = xaml.Replace("\r\n", "\n");
        if (!ReferenceEquals(normalized, xaml))
        {
            xaml = normalized;
        }

        var syntax = Parser.ParseText(xaml);
        diagnostics ??= XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xaml)));
        var version = new XamlDocumentVersion(timestamp ?? DateTimeOffset.UtcNow, xaml.Length, checksum);
        return new XamlAstDocument(path, xaml, syntax, version, diagnostics);
    }

    private static void ResetSplitState()
    {
        SetStaticField("s_lastSplitEnabled", false);
        SetStaticField("s_lastHorizontalRatio", 0.5);
        SetStaticField("s_lastVerticalRatio", 0.5);
        SetStaticField("s_lastOrientation", SourcePreviewSplitOrientation.Horizontal);
    }

    private static void SetSplitDefaults(bool isEnabled, double horizontalRatio, double verticalRatio, SourcePreviewSplitOrientation orientation)
    {
        SetStaticField("s_lastSplitEnabled", isEnabled);
        SetStaticField("s_lastHorizontalRatio", horizontalRatio);
        SetStaticField("s_lastVerticalRatio", verticalRatio);
        SetStaticField("s_lastOrientation", orientation);
    }

    private static void SetStaticField(string name, object value)
    {
        var field = typeof(SourcePreviewViewModel).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Field {name} not found.");
        field.SetValue(null, value);
    }

    private static void SuppressSplitPersistence(SourcePreviewViewModel vm, bool suppress)
    {
        var field = typeof(SourcePreviewViewModel).GetField("_suppressSplitEnabledPersistence", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Field _suppressSplitEnabledPersistence not found.");
        field.SetValue(vm, suppress);
    }

    private static async Task FlushDispatcherAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class TestXamlAstProvider : IXamlAstProvider
    {
        private static readonly StringComparer PathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private readonly Dictionary<string, XamlAstDocument> _documents = new(PathComparer);

        public event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged;
        public event EventHandler<XamlAstNodesChangedEventArgs>? NodesChanged;

        public void SetDocument(XamlAstDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            _documents[Normalize(document.Path)] = document;
        }

        public ValueTask<XamlAstDocument> GetDocumentAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!_documents.TryGetValue(Normalize(path), out var document))
            {
                throw new FileNotFoundException(path);
            }

            return new ValueTask<XamlAstDocument>(document);
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

        public void RaiseDocumentChanged(XamlAstDocument document)
        {
            SetDocument(document);
            DocumentChanged?.Invoke(this, new XamlDocumentChangedEventArgs(document.Path, XamlDocumentChangeKind.Updated, document));
        }

        public void RaiseNodesChanged(string path, XamlDocumentVersion version, IReadOnlyList<XamlAstNodeChange> changes)
        {
            NodesChanged?.Invoke(this, new XamlAstNodesChangedEventArgs(path, version, changes));
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
