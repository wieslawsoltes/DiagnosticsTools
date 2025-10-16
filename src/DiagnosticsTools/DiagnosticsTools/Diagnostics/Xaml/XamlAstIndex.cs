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

        IReadOnlyList<XamlResourceDescriptor> Resources { get; }

        IReadOnlyList<XamlNameScopeDescriptor> NameScopes { get; }

        IReadOnlyList<XamlBindingDescriptor> Bindings { get; }

        IReadOnlyList<XamlStyleDescriptor> Styles { get; }

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
        private readonly IReadOnlyList<XamlResourceDescriptor> _resources;
        private readonly IReadOnlyList<XamlNameScopeDescriptor> _nameScopes;
        private readonly IReadOnlyList<XamlBindingDescriptor> _bindings;
        private readonly IReadOnlyList<XamlStyleDescriptor> _styles;

        private XamlAstIndex(
            IReadOnlyList<XamlAstNodeDescriptor> nodes,
            Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> nodesById,
            Dictionary<string, List<XamlAstNodeDescriptor>> nodesByName,
            Dictionary<string, List<XamlAstNodeDescriptor>> nodesByResourceKey,
            IReadOnlyList<XamlResourceDescriptor> resources,
            IReadOnlyList<XamlNameScopeDescriptor> nameScopes,
            IReadOnlyList<XamlBindingDescriptor> bindings,
            IReadOnlyList<XamlStyleDescriptor> styles)
        {
            _nodes = nodes;
            _nodesById = nodesById;
            _nodesByName = nodesByName;
            _nodesByResourceKey = nodesByResourceKey;
            _resources = resources;
            _nameScopes = nameScopes;
            _bindings = bindings;
            _styles = styles;
        }

        public IEnumerable<XamlAstNodeDescriptor> Nodes => _nodes;

        public IReadOnlyList<XamlResourceDescriptor> Resources => _resources;

        public IReadOnlyList<XamlNameScopeDescriptor> NameScopes => _nameScopes;

        public IReadOnlyList<XamlBindingDescriptor> Bindings => _bindings;

        public IReadOnlyList<XamlStyleDescriptor> Styles => _styles;

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
            var resources = new List<XamlResourceDescriptor>();
            var nameScopes = new List<XamlNameScopeDescriptor>();
            var bindings = new List<XamlBindingDescriptor>();
            var styles = new List<XamlStyleDescriptor>();

            if (document.Syntax.RootSyntax is { } root)
            {
                var visitor = new Builder(
                    document,
                    nodes,
                    nodesById,
                    nodesByName,
                    nodesByKey,
                    resources,
                    nameScopes,
                    bindings,
                    styles);
                visitor.Visit(root.AsNode, new List<int> { 0 });
            }

            var readOnlyNodes = nodes.AsReadOnly();

            return new XamlAstIndex(
                readOnlyNodes,
                nodesById,
                nodesByName,
                nodesByKey,
                resources.AsReadOnly(),
                nameScopes.AsReadOnly(),
                bindings.AsReadOnly(),
                styles.AsReadOnly());
        }

        private sealed class Builder
        {
            private readonly XamlAstDocument _document;
            private readonly List<XamlAstNodeDescriptor> _nodes;
            private readonly Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> _nodesById;
            private readonly Dictionary<string, List<XamlAstNodeDescriptor>> _nodesByName;
            private readonly Dictionary<string, List<XamlAstNodeDescriptor>> _nodesByResourceKey;
            private readonly List<XamlResourceDescriptor> _resources;
            private readonly List<XamlNameScopeDescriptor> _nameScopes;
            private readonly List<XamlBindingDescriptor> _bindings;
            private readonly List<XamlStyleDescriptor> _styles;
            private readonly Stack<ScopeFrame> _scopeStack;

            public Builder(
                XamlAstDocument document,
                List<XamlAstNodeDescriptor> nodes,
                Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> nodesById,
                Dictionary<string, List<XamlAstNodeDescriptor>> nodesByName,
                Dictionary<string, List<XamlAstNodeDescriptor>> nodesByResourceKey,
                List<XamlResourceDescriptor> resources,
                List<XamlNameScopeDescriptor> nameScopes,
                List<XamlBindingDescriptor> bindings,
                List<XamlStyleDescriptor> styles)
            {
                _document = document;
                _nodes = nodes;
                _nodesById = nodesById;
                _nodesByName = nodesByName;
                _nodesByResourceKey = nodesByResourceKey;
                _resources = resources;
                _nameScopes = nameScopes;
                _bindings = bindings;
                _styles = styles;
                _scopeStack = new Stack<ScopeFrame>();
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
                    AddToIndex(_nodesByName, descriptor.XamlName!, descriptor);
                }

                if (!string.IsNullOrEmpty(descriptor.RuntimeName) &&
                    !string.Equals(descriptor.RuntimeName, descriptor.XamlName, StringComparison.Ordinal))
                {
                    AddToIndex(_nodesByName, descriptor.RuntimeName!, descriptor);
                }

                if (!string.IsNullOrEmpty(descriptor.ResourceKey))
                {
                    AddToIndex(_nodesByResourceKey, descriptor.ResourceKey!, descriptor);
                    _resources.Add(new XamlResourceDescriptor(descriptor.ResourceKey!, descriptor));
                }

                var pushedScope = false;
                if (_scopeStack.Count == 0 || StartsNewNameScope(descriptor))
                {
                    _scopeStack.Push(new ScopeFrame(descriptor));
                    pushedScope = true;
                }

                if (_scopeStack.Count > 0)
                {
                    var scope = _scopeStack.Peek();
                    var declaredName = descriptor.XamlName ?? descriptor.RuntimeName;
                    if (!string.IsNullOrEmpty(declaredName))
                    {
                        scope.Entries.Add(new XamlNamedReference(declaredName!, descriptor));
                    }
                }

                if (descriptor.IsStyle)
                {
                    var style = CreateStyleDescriptor(descriptor);
                    if (style is not null)
                    {
                        _styles.Add(style);
                    }
                }

                foreach (var attribute in descriptor.Attributes)
                {
                    var binding = TryCreateBindingDescriptor(descriptor, attribute);
                    if (binding is not null)
                    {
                        _bindings.Add(binding);
                    }
                }

                var childIndex = 0;
                foreach (var child in element.Elements)
                {
                    path.Add(childIndex++);
                    Visit(child.AsNode, path);
                    path.RemoveAt(path.Count - 1);
                }

                if (pushedScope && _scopeStack.Count > 0)
                {
                    var completed = _scopeStack.Pop();
                    _nameScopes.Add(new XamlNameScopeDescriptor(completed.Owner, completed.Entries.AsReadOnly()));
                }
            }

            private static void AddToIndex(
                Dictionary<string, List<XamlAstNodeDescriptor>> index,
                string key,
                XamlAstNodeDescriptor descriptor)
            {
                if (!index.TryGetValue(key, out var list))
                {
                    list = new List<XamlAstNodeDescriptor>();
                    index[key] = list;
                }

                list.Add(descriptor);
            }

            private static bool StartsNewNameScope(XamlAstNodeDescriptor descriptor)
            {
                if (descriptor.IsStyle || descriptor.IsTemplate)
                {
                    return true;
                }

                foreach (var attribute in descriptor.Attributes)
                {
                    var fullName = attribute.FullName;
                    if (string.Equals(fullName, "x:NameScope", StringComparison.Ordinal) ||
                        string.Equals(fullName, "NameScope", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static XamlStyleDescriptor? CreateStyleDescriptor(XamlAstNodeDescriptor descriptor)
            {
                if (!descriptor.IsStyle)
                {
                    return null;
                }

                string? targetType = null;
                string? basedOn = null;

                foreach (var attribute in descriptor.Attributes)
                {
                    if (string.Equals(attribute.LocalName, "TargetType", StringComparison.Ordinal))
                    {
                        targetType ??= NormalizeAttribute(attribute.Value);
                    }
                    else if (string.Equals(attribute.LocalName, "BasedOn", StringComparison.Ordinal))
                    {
                        basedOn ??= NormalizeAttribute(attribute.Value);
                    }
                }

                var resourceKey = descriptor.ResourceKey;
                return new XamlStyleDescriptor(
                    descriptor,
                    targetType,
                    basedOn,
                    resourceKey,
                    string.IsNullOrEmpty(resourceKey));
            }

            private static string? NormalizeAttribute(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                return value.Trim();
            }

            private static XamlBindingDescriptor? TryCreateBindingDescriptor(
                XamlAstNodeDescriptor owner,
                XamlAttributeDescriptor attribute)
            {
                var expression = attribute.Value;
                if (string.IsNullOrWhiteSpace(expression))
                {
                    return null;
                }

                if (!IsBindingExpression(expression))
                {
                    return null;
                }

                var trimmedExpression = expression.Trim();
                var path = ExtractBindingPath(trimmedExpression);
                return new XamlBindingDescriptor(owner, attribute, trimmedExpression, path);
            }

            private static bool IsBindingExpression(string value)
            {
                var trimmed = value?.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    return false;
                }

                var candidate = trimmed!;
                if (candidate[0] != '{')
                {
                    return false;
                }

                return candidate.StartsWith("{Binding", StringComparison.Ordinal) ||
                       candidate.StartsWith("{x:Bind", StringComparison.Ordinal) ||
                       candidate.StartsWith("{TemplateBinding", StringComparison.Ordinal);
            }

            private static string? ExtractBindingPath(string expression)
            {
                var trimmed = expression.Trim();
                if (trimmed.Length == 0)
                {
                    return null;
                }

                var inner = trimmed.TrimStart('{').TrimEnd('}').Trim();
                if (inner.Length == 0)
                {
                    return null;
                }

                if (inner.StartsWith("Binding", StringComparison.Ordinal))
                {
                    inner = inner.Substring("Binding".Length).TrimStart();
                }
                else if (inner.StartsWith("x:Bind", StringComparison.Ordinal))
                {
                    inner = inner.Substring("x:Bind".Length).TrimStart();
                }
                else if (inner.StartsWith("TemplateBinding", StringComparison.Ordinal))
                {
                    inner = inner.Substring("TemplateBinding".Length).TrimStart();
                }

                if (inner.Length == 0)
                {
                    return null;
                }

                if (inner.StartsWith("Path=", StringComparison.Ordinal))
                {
                    return SliceToken(inner.Substring("Path=".Length));
                }

                if (!inner.Contains("="))
                {
                    return SliceToken(inner);
                }

                var pathIndex = inner.IndexOf("Path=", StringComparison.Ordinal);
                if (pathIndex >= 0)
                {
                    var remainder = inner.Substring(pathIndex + "Path=".Length);
                    return SliceToken(remainder);
                }

                return null;
            }

            private static string? SliceToken(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                var trimmed = text.Trim();
                var end = trimmed.Length;
                for (var index = 0; index < trimmed.Length; index++)
                {
                    var ch = trimmed[index];
                    if (ch == ',' || char.IsWhiteSpace(ch))
                    {
                        end = index;
                        break;
                    }
                }

                var token = trimmed.Substring(0, end).Trim();
                return token.Length == 0 ? null : token;
            }

            private sealed class ScopeFrame
            {
                public ScopeFrame(XamlAstNodeDescriptor owner)
                {
                    Owner = owner;
                    Entries = new List<XamlNamedReference>();
                }

                public XamlAstNodeDescriptor Owner { get; }

                public List<XamlNamedReference> Entries { get; }
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

        public bool StructuralEquals(XamlAstNodeDescriptor? other)
        {
            if (other is null)
            {
                return false;
            }

            if (!string.Equals(QualifiedName, other.QualifiedName, StringComparison.Ordinal) ||
                !string.Equals(LocalName, other.LocalName, StringComparison.Ordinal) ||
                !string.Equals(Prefix, other.Prefix, StringComparison.Ordinal) ||
                !string.Equals(XamlName, other.XamlName, StringComparison.Ordinal) ||
                !string.Equals(RuntimeName, other.RuntimeName, StringComparison.Ordinal) ||
                !string.Equals(ResourceKey, other.ResourceKey, StringComparison.Ordinal) ||
                !string.Equals(XUid, other.XUid, StringComparison.Ordinal) ||
                IsStyle != other.IsStyle ||
                IsTemplate != other.IsTemplate)
            {
                return false;
            }

            if (Attributes.Count != other.Attributes.Count)
            {
                return false;
            }

            for (var index = 0; index < Attributes.Count; index++)
            {
                var left = Attributes[index];
                var right = other.Attributes[index];

                if (!string.Equals(left.LocalName, right.LocalName, StringComparison.Ordinal) ||
                    !string.Equals(left.Prefix, right.Prefix, StringComparison.Ordinal) ||
                    !string.Equals(left.Value, right.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }

    public readonly record struct XamlAttributeDescriptor(string LocalName, string? Prefix, string Value)
    {
        public string FullName => string.IsNullOrEmpty(Prefix) ? LocalName : $"{Prefix}:{LocalName}";
    }

    public sealed class XamlResourceDescriptor
    {
        public XamlResourceDescriptor(string key, XamlAstNodeDescriptor node)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            Node = node ?? throw new ArgumentNullException(nameof(node));
        }

        public string Key { get; }

        public XamlAstNodeDescriptor Node { get; }
    }

    public sealed class XamlNameScopeDescriptor
    {
        public XamlNameScopeDescriptor(XamlAstNodeDescriptor owner, IReadOnlyList<XamlNamedReference> entries)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Entries = entries ?? Array.Empty<XamlNamedReference>();
        }

        public XamlAstNodeDescriptor Owner { get; }

        public IReadOnlyList<XamlNamedReference> Entries { get; }
    }

    public sealed class XamlNamedReference
    {
        public XamlNamedReference(string name, XamlAstNodeDescriptor node)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Node = node ?? throw new ArgumentNullException(nameof(node));
        }

        public string Name { get; }

        public XamlAstNodeDescriptor Node { get; }
    }

    public sealed class XamlBindingDescriptor
    {
        public XamlBindingDescriptor(XamlAstNodeDescriptor owner, XamlAttributeDescriptor attribute, string expression, string? path)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Attribute = attribute;
            Expression = expression ?? throw new ArgumentNullException(nameof(expression));
            Path = path;
        }

        public XamlAstNodeDescriptor Owner { get; }

        public XamlAttributeDescriptor Attribute { get; }

        public string Expression { get; }

        public string? Path { get; }
    }

    public sealed class XamlStyleDescriptor
    {
        public XamlStyleDescriptor(
            XamlAstNodeDescriptor node,
            string? targetType,
            string? basedOn,
            string? resourceKey,
            bool isImplicit)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
            TargetType = targetType;
            BasedOn = basedOn;
            ResourceKey = resourceKey;
            IsImplicit = isImplicit;
        }

        public XamlAstNodeDescriptor Node { get; }

        public string? TargetType { get; }

        public string? BasedOn { get; }

        public string? ResourceKey { get; }

        public bool IsImplicit { get; }
    }
}
