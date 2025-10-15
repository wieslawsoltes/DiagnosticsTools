using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.Xaml;
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

    private static SourceInfo CreateSourceInfo(string path) =>
        new(path, null, 1, 1, 1, 1, SourceOrigin.Local);

    private static SourceInfo CreateRuntimeInfo() =>
        new("Runtime Snapshot", null, null, null, null, null, SourceOrigin.Generated);

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
}
