using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Diagnostics.Xaml;
using AvaloniaEdit.Folding;
using Microsoft.Language.Xml;
using Xunit;

namespace DiagnosticsTools.Tests;

public class XamlAstFoldingBuilderTests
{
    [Fact]
    public void BuildFoldings_ReturnsFoldForNestedElements()
    {
        var xaml = """
<Grid xmlns="https://github.com/avaloniaui">
  <StackPanel>
    <Button Content="Primary" />
  </StackPanel>
</Grid>
""";

        var syntax = Parser.ParseText(xaml);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xaml)));
        var version = new XamlDocumentVersion(DateTimeOffset.UtcNow, xaml.Length, checksum);
        var document = new XamlAstDocument("/tmp/View.axaml", xaml, syntax, version, diagnostics);

        var builder = new XamlAstFoldingBuilder();
        var foldings = builder.BuildFoldings(document);

        Assert.NotEmpty(foldings);
        Assert.Contains(foldings, folding => folding.Name == "<Grid>" && folding.StartOffset == 0);
        Assert.All(foldings, folding => Assert.True(folding.EndOffset > folding.StartOffset));
    }
}
