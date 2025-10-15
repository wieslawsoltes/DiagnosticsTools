using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Language.Xml;

#pragma warning disable CS8600, CS8601

namespace Avalonia.Diagnostics.Xaml
{
    internal interface IXamlAstIndex
    {
        IEnumerable<XamlAstNodeDescriptor> Nodes { get; }

        bool TryGetDescriptor(XamlAstNodeId id, out XamlAstNodeDescriptor descriptor);

        IReadOnlyList<XamlAstNodeDescriptor> FindByName(string name);

        IReadOnlyList<XamlAstNodeDescriptor> FindByResourceKey(string key);
    }

    internal sealed class XamlAstIndex : IXamlAstIndex
    {
        private readonly IReadOnlyList<XamlAstNodeDescriptor> _nodes;
        private readonly Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> _nodesById;
        private readonly Dictionary<string, List<XamlAstNodeDescriptor>> _nodesByName;
        private readonly Dictionary<string, List<XamlAstNodeDescriptor>> _nodesByResourceKey;

        private XamlAstIndex(
            IReadOnlyList<XamlAstNodeDescriptor> nodes,
            Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> nodesById,
            Dictionary<string, List<XamlAstNodeDescriptor>> nodesByName,
            Dictionary<string, List<XamlAstNodeDescriptor>> nodesByResourceKey)
        {
            _nodes = nodes;
            _nodesById = nodesById;
            _nodesByName = nodesByName;
            _nodesByResourceKey = nodesByResourceKey;
        }

        public IEnumerable<XamlAstNodeDescriptor> Nodes => _nodes;

        public bool TryGetDescriptor(XamlAstNodeId id, out XamlAstNodeDescriptor descriptor) =>
            _nodesById.TryGetValue(id, out descriptor);

        public IReadOnlyList<XamlAstNodeDescriptor> FindByName(string name)
        {
            if (string.IsNullOrEmpty(name) ||
                !_nodesByName.TryGetValue(name, out List<XamlAstNodeDescriptor> matches))
            {
                return Array.Empty<XamlAstNodeDescriptor>();
            }

            return matches;
        }

        public IReadOnlyList<XamlAstNodeDescriptor> FindByResourceKey(string key)
        {
            if (string.IsNullOrEmpty(key) ||
                !_nodesByResourceKey.TryGetValue(key, out List<XamlAstNodeDescriptor> matches))
            {
                return Array.Empty<XamlAstNodeDescriptor>();
            }

            return matches;
        }

        public static XamlAstIndex Build(XamlAstDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var nodes = new List<XamlAstNodeDescriptor>();
            var nodesById = new Dictionary<XamlAstNodeId, XamlAstNodeDescriptor>();
            var nodesByName = new Dictionary<string, List<XamlAstNodeDescriptor>>(StringComparer.Ordinal);
            var nodesByKey = new Dictionary<string, List<XamlAstNodeDescriptor>>(StringComparer.Ordinal);

            if (document.Syntax.RootSyntax is { } root)
            {
                var visitor = new Builder(document, nodes, nodesById, nodesByName, nodesByKey);
                visitor.Visit(root.AsNode, new List<int> { 0 });
            }

            var readOnlyNodes = nodes.AsReadOnly();

            return new XamlAstIndex(readOnlyNodes, nodesById, nodesByName, nodesByKey);
        }

        private sealed class Builder
        {
            private readonly XamlAstDocument _document;
            private readonly List<XamlAstNodeDescriptor> _nodes;
            private readonly Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> _nodesById;
            private readonly Dictionary<string, List<XamlAstNodeDescriptor>> _nodesByName;
            private readonly Dictionary<string, List<XamlAstNodeDescriptor>> _nodesByResourceKey;

            public Builder(
                XamlAstDocument document,
                List<XamlAstNodeDescriptor> nodes,
                Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> nodesById,
                Dictionary<string, List<XamlAstNodeDescriptor>> nodesByName,
                Dictionary<string, List<XamlAstNodeDescriptor>> nodesByResourceKey)
            {
                _document = document;
                _nodes = nodes;
                _nodesById = nodesById;
                _nodesByName = nodesByName;
                _nodesByResourceKey = nodesByResourceKey;
            }

            public void Visit(XmlNodeSyntax node, List<int> path)
            {
                if (node is null)
                {
                    return;
                }

                if (node is IXmlElementSyntax element)
                {
                    AppendElement(element, path);
                }
            }

            private void AppendElement(IXmlElementSyntax element, List<int> path)
            {
                var descriptor = CreateDescriptor(element, path);
                _nodes.Add(descriptor);
                _nodesById[descriptor.Id] = descriptor;

                if (!string.IsNullOrEmpty(descriptor.XamlName))
                {
                    if (!_nodesByName.TryGetValue(descriptor.XamlName!, out var list))
                    {
                        list = new List<XamlAstNodeDescriptor>();
                        _nodesByName[descriptor.XamlName!] = list;
                    }

                    list.Add(descriptor);
                }

                if (!string.IsNullOrEmpty(descriptor.ResourceKey))
                {
                    if (!_nodesByResourceKey.TryGetValue(descriptor.ResourceKey!, out var list))
                    {
                        list = new List<XamlAstNodeDescriptor>();
                        _nodesByResourceKey[descriptor.ResourceKey!] = list;
                    }

                    list.Add(descriptor);
                }

                var childIndex = 0;
                foreach (var child in element.Elements)
                {
                    path.Add(childIndex++);
                    Visit(child.AsNode, path);
                    path.RemoveAt(path.Count - 1);
                }
            }

            private XamlAstNodeDescriptor CreateDescriptor(IXmlElementSyntax element, IReadOnlyList<int> path)
            {
                var nameNode = element.NameNode;
                var localName = nameNode?.LocalName ?? string.Empty;
                var prefix = nameNode?.Prefix;
                var qualifiedName = string.IsNullOrEmpty(prefix) ? localName : $"{prefix}:{localName}";

                var attributes = new List<XamlAttributeDescriptor>();
                foreach (var attribute in element.Attributes)
                {
                    var attributeNameNode = attribute.NameNode;
                    var attributeLocalName = attributeNameNode?.LocalName ?? string.Empty;
                    var attributePrefix = attributeNameNode?.Prefix;
                    attributes.Add(new XamlAttributeDescriptor(attributeLocalName, attributePrefix, attribute.Value ?? string.Empty));
                }

                var xName = element.GetAttributeValue("Name", "x");
                var runtimeName = element.GetAttributeValue("Name");
                var resourceKey = element.GetAttributeValue("Key", "x") ?? element.GetAttributeValue("Key");
                var xUid = element.GetAttributeValue("Uid", "x");

                var span = element.AsNode.Span;
                _document.TryGetLinePositionSpan(span, out var lineSpan);

                var pathCopy = new int[path.Count];
                for (var index = 0; index < path.Count; index++)
                {
                    pathCopy[index] = path[index];
                }

                var id = CreateNodeId(qualifiedName, pathCopy, xName, resourceKey);
                var isStyle = string.Equals(localName, "Style", StringComparison.Ordinal);
                var isTemplate = localName.EndsWith("Template", StringComparison.Ordinal);

                return new XamlAstNodeDescriptor(
                    id,
                    qualifiedName,
                    localName,
                    prefix,
                    xName,
                    runtimeName,
                    resourceKey,
                    xUid,
                    attributes,
                    span,
                    lineSpan,
                    pathCopy,
                    isStyle,
                    isTemplate);
            }

            private static XamlAstNodeId CreateNodeId(string qualifiedName, IReadOnlyList<int> path, string? xName, string? resourceKey)
            {
                var builder = new StringBuilder(qualifiedName.Length + path.Count * 3 + 16);
                builder.Append(qualifiedName);
                builder.Append('@');

                for (var index = 0; index < path.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append('/');
                    }

                    builder.Append(path[index]);
                }

                if (!string.IsNullOrEmpty(xName))
                {
                    builder.Append("#n:");
                    builder.Append(xName);
                }
                else if (!string.IsNullOrEmpty(resourceKey))
                {
                    builder.Append("#k:");
                    builder.Append(resourceKey);
                }

                return new XamlAstNodeId(builder.ToString());
            }
        }
    }

    public readonly record struct XamlAstNodeId(string Value)
    {
        public override string ToString() => Value;
    }

    public sealed class XamlAstNodeDescriptor
    {
        public XamlAstNodeDescriptor(
            XamlAstNodeId id,
            string qualifiedName,
            string localName,
            string? prefix,
            string? xamlName,
            string? runtimeName,
            string? resourceKey,
            string? xUid,
            IReadOnlyList<XamlAttributeDescriptor> attributes,
            TextSpan span,
            LinePositionSpan lineSpan,
            IReadOnlyList<int> path,
            bool isStyle,
            bool isTemplate)
        {
            Id = id;
            QualifiedName = qualifiedName;
            LocalName = localName;
            Prefix = prefix;
            XamlName = xamlName;
            RuntimeName = runtimeName;
            ResourceKey = resourceKey;
            XUid = xUid;
            Attributes = attributes ?? Array.Empty<XamlAttributeDescriptor>();
            Span = span;
            LineSpan = lineSpan;
            Path = path ?? Array.Empty<int>();
            IsStyle = isStyle;
            IsTemplate = isTemplate;
        }

        public XamlAstNodeId Id { get; }

        public string QualifiedName { get; }

        public string LocalName { get; }

        public string? Prefix { get; }

        public string? XamlName { get; }

        public string? RuntimeName { get; }

        public string? ResourceKey { get; }

        public string? XUid { get; }

        public IReadOnlyList<XamlAttributeDescriptor> Attributes { get; }

        public TextSpan Span { get; }

        public LinePositionSpan LineSpan { get; }

        public IReadOnlyList<int> Path { get; }

        public bool IsStyle { get; }

        public bool IsTemplate { get; }

        public bool IsResource => !string.IsNullOrEmpty(ResourceKey);
    }

    public readonly record struct XamlAttributeDescriptor(string LocalName, string? Prefix, string Value)
    {
        public string FullName => string.IsNullOrEmpty(Prefix) ? LocalName : $"{Prefix}:{LocalName}";
    }

}
