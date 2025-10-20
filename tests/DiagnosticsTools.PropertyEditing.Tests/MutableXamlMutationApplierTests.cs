using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;
using Xunit;

namespace PropertyEditing.Tests;

public class MutableXamlMutationApplierTests
{
    [Fact]
    public void Upsert_ReplacesRootElement()
    {
        const string original = "<Grid><TextBlock Text=\"Hello\"/></Grid>";
        const string replacement = "<StackPanel><TextBlock Text=\"Updated\"/></StackPanel>";

        using var context = CreateDocumentContext(original);

        var rootDescriptor = GetRootDescriptor(context.Index);

        var operations = new List<ChangeOperation>
        {
            new()
            {
                Type = ChangeOperationTypes.UpsertElement,
                Target = new ChangeTarget
                {
                    DescriptorId = rootDescriptor.Id.Value,
                    NodeType = "Element"
                },
                Payload = new ChangePayload
                {
                    Serialized = replacement
                }
            }
        };

        var result = MutableXamlMutationApplier.TryApply(context.Document, context.Index, context.MutableDocument, operations);

        Assert.True(result.Status == MutableMutationStatus.Applied, result.Failure?.Message ?? "Mutable apply failed.");
        Assert.True(result.Mutated);
        Assert.NotNull(result.Document);

        var serialized = MutableXamlSerializer.Serialize(result.Document!);
        Assert.Equal(replacement, serialized.Trim());
    }

    [Fact]
    public void Upsert_InsertsChildElement()
    {
        const string original = "<Grid><TextBlock Text=\"First\"/></Grid>";
        const string insertion = "<Button Content=\"Second\"/>";

        using var context = CreateDocumentContext(original);
        var rootDescriptor = GetRootDescriptor(context.Index);

        var operations = new List<ChangeOperation>
        {
            new()
            {
                Type = ChangeOperationTypes.UpsertElement,
                Target = new ChangeTarget
                {
                    DescriptorId = rootDescriptor.Id.Value,
                    NodeType = "Element"
                },
                Payload = new ChangePayload
                {
                    Serialized = insertion,
                    InsertionIndex = 1,
                    SurroundingWhitespace = Environment.NewLine
                }
            }
        };

        var result = MutableXamlMutationApplier.TryApply(context.Document, context.Index, context.MutableDocument, operations);

        Assert.True(result.Status == MutableMutationStatus.Applied, result.Failure?.Message ?? "Mutable apply failed.");
        Assert.True(result.Mutated);
        Assert.NotNull(result.Document);

        var serialized = MutableXamlSerializer.Serialize(result.Document!);
        Assert.Contains("<Button Content=\"Second\"/>", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void RenameElement_RenameTagName()
    {
        const string original = "<Grid><TextBlock Text=\"Hello\" /></Grid>";

        using var context = CreateDocumentContext(original);

        var descriptor = Assert.Single(context.Index.Nodes, d => d.LocalName == "TextBlock");

        var operation = new ChangeOperation
        {
            Type = ChangeOperationTypes.RenameElement,
            Target = new ChangeTarget
            {
                DescriptorId = descriptor.Id.Value,
                NodeType = "Element"
            },
            Payload = new ChangePayload
            {
                NewValue = "Label"
            },
            Guard = new ChangeOperationGuard
            {
                SpanHash = XamlGuardUtilities.ComputeNodeHash(context.Document, descriptor)
            }
        };

        var result = MutableXamlMutationApplier.TryApply(context.Document, context.Index, context.MutableDocument, new[] { operation });

        Assert.Equal(MutableMutationStatus.Applied, result.Status);
        Assert.True(result.Mutated);
        Assert.NotNull(result.Document);
        var renamedElement = Assert.Single(result.Document!.Elements, e => e.LocalName == "Label");
        Assert.Equal("Label", renamedElement.LocalName);
    }

    [Fact]
    public void RenameResource_UpdatesCascadeTargets()
    {
        const string original = "<Grid xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                                "<Grid.Resources>" +
                                "  <SolidColorBrush x:Key=\"Primary\" Color=\"Red\"/>" +
                                "</Grid.Resources>" +
                                "<TextBlock Foreground=\"{StaticResource Primary}\"/>" +
                                "</Grid>";

        using var context = CreateDocumentContext(original);

        var resourceDescriptor = Assert.Single(context.Index.Nodes, d => d.LocalName == "SolidColorBrush");
        var textBlockDescriptor = Assert.Single(context.Index.Nodes, d => d.LocalName == "TextBlock");
        var keyAttribute = Assert.Single(resourceDescriptor.Attributes, a => string.Equals(a.LocalName, "Key", StringComparison.Ordinal));

        var operation = new ChangeOperation
        {
            Type = ChangeOperationTypes.RenameResource,
            Target = new ChangeTarget
            {
                DescriptorId = resourceDescriptor.Id.Value,
                NodeType = "Element"
            },
            Payload = new ChangePayload
            {
                OldKey = "Primary",
                NewKey = "Accent",
                CascadeTargets = new[] { textBlockDescriptor.Id.Value }
            },
            Guard = new ChangeOperationGuard
            {
                SpanHash = XamlGuardUtilities.ComputeAttributeHash(context.Document, resourceDescriptor, keyAttribute.FullName)
            }
        };

        var result = MutableXamlMutationApplier.TryApply(context.Document, context.Index, context.MutableDocument, new[] { operation });

        Assert.Equal(MutableMutationStatus.Applied, result.Status);
        Assert.True(result.Mutated);
        Assert.NotNull(result.Document);

        var mutable = result.Document!;
        var resourceElement = Assert.Single(mutable.Elements, e => e.LocalName == "SolidColorBrush");
        Assert.Equal("Accent", resourceElement.FindAttribute("Key", "x")?.Value);

        var textElement = Assert.Single(mutable.Elements, e => e.LocalName == "TextBlock");
        Assert.Equal("{StaticResource Accent}", textElement.FindAttribute("Foreground")?.Value);
    }

    [Fact]
    public void SetNamespace_And_Attribute_Applies_To_Element()
    {
        const string original = "<UserControl xmlns=\"https://github.com/avaloniaui\">" +
                                "<ContentControl x:Name=\"Host\" />" +
                                "</UserControl>";

        using var context = CreateDocumentContext(original);
        var descriptor = Assert.Single(context.Index.Nodes, n => string.Equals(n.LocalName, "ContentControl", StringComparison.Ordinal));

        var namespaceOperation = new ChangeOperation
        {
            Id = "op-namespace",
            Type = ChangeOperationTypes.SetNamespace,
            Target = new ChangeTarget
            {
                DescriptorId = descriptor.Id.Value,
                NodeType = "Element"
            },
            Payload = new ChangePayload
            {
                Name = "xmlns:local",
                NewValue = "clr-namespace:Avalonia.Controls;assembly:Avalonia.Controls"
            },
            Guard = new ChangeOperationGuard
            {
                SpanHash = XamlGuardUtilities.ComputeAttributeHash(context.Document, descriptor, "xmlns:local")
            }
        };

        var attributeOperation = new ChangeOperation
        {
            Id = "op-attribute",
            Type = ChangeOperationTypes.SetAttribute,
            Target = new ChangeTarget
            {
                DescriptorId = descriptor.Id.Value,
                NodeType = "Element"
            },
            Payload = new ChangePayload
            {
                Name = "Content",
                NewValue = "{local:DemoContent}",
                ValueKind = "Literal"
            },
            Guard = new ChangeOperationGuard
            {
                SpanHash = XamlGuardUtilities.ComputeAttributeHash(context.Document, descriptor, "Content")
            }
        };

        var operations = new[] { namespaceOperation, attributeOperation };
        var result = MutableXamlMutationApplier.TryApply(context.Document, context.Index, context.MutableDocument, operations);

        Assert.True(result.Status == MutableMutationStatus.Applied, result.Failure?.Message ?? "Mutable apply failed.");
        Assert.True(result.Mutated);
        Assert.NotNull(result.Document);

        var serialized = MutableXamlSerializer.Serialize(result.Document!);
        var element = Assert.Single(result.Document!.Elements, e => string.Equals(e.LocalName, "ContentControl", StringComparison.Ordinal));
        var namespaceAttribute = element.FindAttribute("local", "xmlns");

        Assert.NotNull(namespaceAttribute);
        Assert.Equal("clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls", namespaceAttribute!.Value);
        Assert.NotNull(element.FindAttribute("Content"));
        Assert.Contains("xmlns:local=\"clr-namespace:Avalonia.Controls;assembly=Avalonia.Controls\"", serialized, StringComparison.Ordinal);
        Assert.Contains("Content=\"{local:DemoContent}\"", serialized, StringComparison.Ordinal);
    }

    private static XamlAstNodeDescriptor GetRootDescriptor(IXamlAstIndex index)
    {
        foreach (var node in index.Nodes)
        {
            return node;
        }

        throw new InvalidOperationException("Document did not produce any AST descriptors.");
    }

    private static DocumentContext CreateDocumentContext(string text)
    {
        var syntax = Parser.ParseText(text);
        var version = new XamlDocumentVersion(DateTimeOffset.UtcNow, text.Length, Guid.NewGuid().ToString("N"));
        var document = new XamlAstDocument("test.xaml", text, syntax, version, Array.Empty<XamlAstDiagnostic>());
        var mutable = MutableXamlDocument.FromDocument(document);
        var index = XamlAstIndex.Build(document);
        return new DocumentContext(document, mutable, index);
    }

    private sealed record DocumentContext(
        XamlAstDocument Document,
        MutableXamlDocument MutableDocument,
        IXamlAstIndex Index) : IDisposable
    {
        public void Dispose()
        {
            // XamlAstDocument holds no unmanaged resources, but the pattern keeps tests flexible.
        }
    }
}
