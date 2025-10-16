using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;
using Xunit;

namespace DiagnosticsTools.Tests;

public class XamlAstIndexTests
{
    [Fact]
    public void Build_ProducesCrossIndexesForResourcesNameScopesBindingsAndStyles()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:controls="clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls">
  <UserControl.Resources>
    <SolidColorBrush x:Key="PrimaryBrush" Color="Red" />
    <Style x:Key="AccentButtonStyle" TargetType="{x:Type Button}" BasedOn="{StaticResource BaseButton}">
      <Setter Property="Foreground" Value="Red" />
    </Style>
  </UserControl.Resources>
  <Grid x:Name="LayoutRoot">
    <TextBlock Name="GreetingText" Text="{Binding Greeting}" />
    <ContentControl>
      <ContentControl.Template>
        <ControlTemplate>
          <Border x:Name="TemplateBorder" Background="{StaticResource PrimaryBrush}" />
        </ControlTemplate>
      </ContentControl.Template>
    </ContentControl>
  </Grid>
</UserControl>
""";

        var document = BuildDocument(xaml, "/tmp/CrossIndex.axaml");
        var index = XamlAstIndex.Build(document);

        Assert.Contains(index.Resources, r => r.Key == "PrimaryBrush");
        Assert.Contains(index.Resources, r => r.Key == "AccentButtonStyle");

        var rootScope = index.NameScopes.First(scope => scope.Owner.LocalName == "UserControl");
        Assert.Contains(rootScope.Entries, entry => entry.Name == "LayoutRoot");
        Assert.Contains(rootScope.Entries, entry => entry.Name == "GreetingText");

        var templateScope = index.NameScopes.First(scope => scope.Owner.LocalName.EndsWith("Template", StringComparison.Ordinal));
        Assert.Contains(templateScope.Entries, entry => entry.Name == "TemplateBorder");

        var binding = index.Bindings.FirstOrDefault();
        Assert.NotNull(binding);
        Assert.Equal("Greeting", binding!.Path);

        var style = index.Styles.FirstOrDefault(s => s.Node.ResourceKey == "AccentButtonStyle");
        Assert.NotNull(style);
        Assert.Equal("{x:Type Button}", style!.TargetType);
        Assert.Equal("AccentButtonStyle", style.ResourceKey);
        Assert.False(style.IsImplicit);
    }

    [Fact]
    public void Diff_DetectsAddedUpdatedAndRemovedNodes()
    {
        var originalXaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid>
    <TextBlock x:Name="GreetingText" Text="{Binding Greeting}" />
    <Button x:Name="ActionButton" Content="Click" />
  </Grid>
</UserControl>
""";

        var updatedXaml = """
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Grid>
    <Border x:Name="HeaderBorder" />
    <Button x:Name="ActionButton" Content="Updated" Tag="42" />
  </Grid>
</UserControl>
""";

        var originalDocument = BuildDocument(originalXaml, "/tmp/Diff.axaml", DateTimeOffset.UtcNow);
        var updatedDocument = BuildDocument(updatedXaml, "/tmp/Diff.axaml", originalDocument.Version.TimestampUtc.AddSeconds(1));

        var originalIndex = XamlAstIndex.Build(originalDocument);
        var updatedIndex = XamlAstIndex.Build(updatedDocument);

        var changes = XamlAstNodeDiffer.Diff(originalIndex, updatedIndex).ToList();

        Assert.Contains(changes, change =>
            change.Kind == XamlAstNodeChangeKind.Updated &&
            change.OldNode?.LocalName == "Button" &&
            change.NewNode?.Attributes.Any(a => a.LocalName == "Content" && a.Value == "Updated") == true);

        Assert.Contains(changes, change =>
            change.Kind == XamlAstNodeChangeKind.Removed &&
            change.OldNode?.LocalName == "TextBlock");

        Assert.Contains(changes, change =>
            change.Kind == XamlAstNodeChangeKind.Added &&
            change.NewNode?.LocalName == "Border");
    }

    private static XamlAstDocument BuildDocument(
        string xaml,
        string path,
        DateTimeOffset? timestamp = null)
    {
        var normalized = xaml.Replace("\r\n", "\n");
        if (!ReferenceEquals(normalized, xaml))
        {
            xaml = normalized;
        }

        var syntax = Parser.ParseText(xaml);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xaml)));
        var version = new XamlDocumentVersion(timestamp ?? DateTimeOffset.UtcNow, xaml.Length, checksum);
        return new XamlAstDocument(path, xaml, syntax, version, diagnostics);
    }
}
