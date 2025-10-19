using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;
using Xunit;

namespace DiagnosticsTools.Tests;

public class SelectionCoordinatorTests
{
    [Fact]
    public void TryBeginPublish_ReturnsChanged_ForDocumentOnlySelection()
    {
        var coordinator = new SelectionCoordinator();
        var document = BuildDocument(SampleXaml, "/tmp/DocumentOnly.axaml");
        var selection = new XamlAstSelection(document, null);

        var success = coordinator.TryBeginPublish("Owner", selection, out var token, out var changed);

        Assert.True(success);
        Assert.True(changed);
        Assert.NotNull(token);

        token?.Dispose();
    }

    [Fact]
    public void TryBeginPublish_DetectsChanges_AcrossDocumentsWithMatchingNodeIds()
    {
        var coordinator = new SelectionCoordinator();

        var (document1, index1) = CreateDocument("/tmp/View1.axaml");
        var descriptor1 = index1.Nodes.First(node => node.LocalName == "Button");
        var selection1 = new XamlAstSelection(document1, descriptor1, index1.Nodes.ToList());

        var success1 = coordinator.TryBeginPublish("Owner", selection1, out var token1, out var changed1);
        Assert.True(success1);
        Assert.True(changed1);
        token1?.Dispose();

        var (document2, index2) = CreateDocument("/tmp/View2.axaml");
        var descriptor2 = index2.Nodes.First(node => node.LocalName == "Button");
        var selection2 = new XamlAstSelection(document2, descriptor2, index2.Nodes.ToList());

        var success2 = coordinator.TryBeginPublish("Owner", selection2, out var token2, out var changed2);
        Assert.True(success2);
        Assert.True(changed2);
        token2?.Dispose();
    }

    private static (XamlAstDocument Document, XamlAstIndex Index) CreateDocument(string path)
    {
        var document = BuildDocument(SampleXaml, path);
        var index = XamlAstIndex.Build(document);
        return (document, index);
    }

    private static XamlAstDocument BuildDocument(string xaml, string path)
    {
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UtcNow, normalized.Length, checksum);
        return new XamlAstDocument(path, normalized, syntax, version, diagnostics);
    }

    private const string SampleXaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid>
    <Button x:Name="Foo" Content="Bar" />
  </Grid>
</UserControl>
""";
}
