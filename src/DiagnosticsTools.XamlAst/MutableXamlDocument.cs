using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.Xaml
{
    /// <summary>
    /// Represents a writable view of a XAML document that preserves trivia and element identity.
    /// </summary>
    public sealed class MutableXamlDocument
    {
        private readonly Dictionary<XamlAstNodeId, MutableXamlElement> _elementsById;
        private readonly Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> _descriptorsById;
        private readonly Dictionary<string, XamlAstNodeDescriptor> _runtimeNodesById;
        private readonly IReadOnlyDictionary<XamlAstNodeId, MutableXamlElement> _elementLookup;
        private readonly IReadOnlyDictionary<XamlAstNodeId, XamlAstNodeDescriptor> _descriptorLookup;
        private readonly IReadOnlyDictionary<string, XamlAstNodeDescriptor> _runtimeLookup;

        private MutableXamlDocument(
            XamlAstDocument source,
            MutableXamlElement? root,
            Dictionary<XamlAstNodeId, MutableXamlElement> elementsById,
            Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> descriptorsById,
            Dictionary<string, XamlAstNodeDescriptor> runtimeNodesById)
        {
            SourceDocument = source ?? throw new ArgumentNullException(nameof(source));
            Root = root;
            _elementsById = elementsById ?? throw new ArgumentNullException(nameof(elementsById));
            _descriptorsById = descriptorsById ?? throw new ArgumentNullException(nameof(descriptorsById));
            _runtimeNodesById = runtimeNodesById ?? throw new ArgumentNullException(nameof(runtimeNodesById));
            _elementLookup = new ReadOnlyDictionary<XamlAstNodeId, MutableXamlElement>(_elementsById);
            _descriptorLookup = new ReadOnlyDictionary<XamlAstNodeId, XamlAstNodeDescriptor>(_descriptorsById);
            _runtimeLookup = new ReadOnlyDictionary<string, XamlAstNodeDescriptor>(_runtimeNodesById);
        }

        /// <summary>
        /// Gets the immutable source document used to build this mutable tree.
        /// </summary>
        public XamlAstDocument SourceDocument { get; }

        /// <summary>
        /// Gets the path of the underlying XAML document.
        /// </summary>
        public string Path => SourceDocument.Path;

        /// <summary>
        /// Gets the version metadata of the underlying XAML document.
        /// </summary>
        public XamlDocumentVersion Version => SourceDocument.Version;

        /// <summary>
        /// Gets the root element of the mutable tree.
        /// </summary>
        public MutableXamlElement? Root { get; }

        /// <summary>
        /// Enumerates all mutable elements in the document.
        /// </summary>
        public IEnumerable<MutableXamlElement> Elements => _elementsById.Values;

        /// <summary>
        /// Gets a read-only lookup of mutable elements keyed by descriptor id.
        /// </summary>
        public IReadOnlyDictionary<XamlAstNodeId, MutableXamlElement> ElementLookup => _elementLookup;

        /// <summary>
        /// Gets a read-only lookup of descriptors keyed by descriptor id.
        /// </summary>
        public IReadOnlyDictionary<XamlAstNodeId, XamlAstNodeDescriptor> DescriptorLookup => _descriptorLookup;

        /// <summary>
        /// Gets a read-only lookup of descriptors keyed by runtime node id.
        /// </summary>
        public IReadOnlyDictionary<string, XamlAstNodeDescriptor> RuntimeDescriptorLookup => _runtimeLookup;

        /// <summary>
        /// Creates a mutable view from a parsed <see cref="XamlAstDocument"/>.
        /// </summary>
        public static MutableXamlDocument FromDocument(XamlAstDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var builder = new Builder(document);
            var root = builder.Build();
            return new MutableXamlDocument(document, root, builder.ElementsById, builder.DescriptorsById, builder.RuntimeNodesById);
        }

        /// <summary>
        /// Attempts to resolve a mutable element for the given descriptor.
        /// </summary>
        public bool TryGetElement(XamlAstNodeDescriptor descriptor, out MutableXamlElement? element)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            return TryGetElement(descriptor.Id, out element);
        }

        /// <summary>
        /// Attempts to resolve a mutable element for the given node id.
        /// </summary>
        public bool TryGetElement(XamlAstNodeId id, out MutableXamlElement? element) =>
            _elementsById.TryGetValue(id, out element);

        /// <summary>
        /// Attempts to resolve a descriptor snapshot for the given node id.
        /// </summary>
        public bool TryGetDescriptor(XamlAstNodeId id, out XamlAstNodeDescriptor descriptor)
        {
            if (_descriptorsById.TryGetValue(id, out var found) && found is not null)
            {
                descriptor = found;
                return true;
            }

            descriptor = null!;
            return false;
        }

        /// <summary>
        /// Attempts to resolve a descriptor snapshot for the given runtime node id.
        /// </summary>
        public bool TryGetDescriptor(string runtimeNodeId, out XamlAstNodeDescriptor descriptor)
        {
            if (!string.IsNullOrEmpty(runtimeNodeId) && _runtimeNodesById.TryGetValue(runtimeNodeId, out var found))
            {
                descriptor = found;
                return true;
            }

            descriptor = null!;
            return false;
        }

        /// <summary>
        /// Creates a fresh descriptor snapshot for the supplied element.
        /// </summary>
        public XamlAstNodeDescriptor ToDescriptor(MutableXamlElement element)
        {
            if (element is null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            return MutableXamlDescriptorFactory.CreateDescriptor(SourceDocument, element);
        }

        public void UpdateDescriptorMapping(MutableXamlElement element, XamlAstNodeDescriptor descriptor)
        {
            if (element is null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            var previous = element.Descriptor;

            if (!previous.Id.Equals(descriptor.Id))
            {
                _descriptorsById.Remove(previous.Id);
                _elementsById.Remove(previous.Id);
            }

            RemoveRuntimeEntry(previous);

            element.SetDescriptor(descriptor);
            _descriptorsById[descriptor.Id] = descriptor;
            _elementsById[descriptor.Id] = element;

            AddRuntimeEntry(descriptor);
        }

        private void AddRuntimeEntry(XamlAstNodeDescriptor descriptor)
        {
            if (!string.IsNullOrEmpty(descriptor.RuntimeName))
            {
                _runtimeNodesById[descriptor.RuntimeName] = descriptor;
            }

            if (!string.IsNullOrEmpty(descriptor.XamlName) && !_runtimeNodesById.ContainsKey(descriptor.XamlName))
            {
                _runtimeNodesById[descriptor.XamlName] = descriptor;
            }
        }

        private void RemoveRuntimeEntry(XamlAstNodeDescriptor descriptor)
        {
            if (!string.IsNullOrEmpty(descriptor.RuntimeName))
            {
                _runtimeNodesById.Remove(descriptor.RuntimeName);
            }

            if (!string.IsNullOrEmpty(descriptor.XamlName))
            {
                _runtimeNodesById.Remove(descriptor.XamlName);
            }
        }

        public void RemoveDescriptorMapping(MutableXamlElement element)
        {
            if (element is null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            RemoveDescriptorMappingRecursive(element);
        }

        private void RemoveDescriptorMappingRecursive(MutableXamlElement element)
        {
            foreach (var child in element.Children)
            {
                if (child is MutableXamlElement childElement)
                {
                    RemoveDescriptorMappingRecursive(childElement);
                }
            }

            RemoveRuntimeEntry(element.Descriptor);

            _elementsById.Remove(element.Id);
            _descriptorsById.Remove(element.Descriptor.Id);
        }

        private sealed class Builder
        {
            private readonly XamlAstDocument _document;
            private readonly Dictionary<XamlAstNodeId, MutableXamlElement> _elementsById;
            private readonly Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> _descriptorsById;
            private readonly Dictionary<string, XamlAstNodeDescriptor> _runtimeNodesById;
            private readonly List<int> _path;

            public Builder(XamlAstDocument document)
            {
                _document = document;
                _elementsById = new Dictionary<XamlAstNodeId, MutableXamlElement>();
                _descriptorsById = new Dictionary<XamlAstNodeId, XamlAstNodeDescriptor>();
                _runtimeNodesById = new Dictionary<string, XamlAstNodeDescriptor>(StringComparer.Ordinal);
                _path = new List<int>();
            }

            public Dictionary<XamlAstNodeId, MutableXamlElement> ElementsById => _elementsById;

            public Dictionary<XamlAstNodeId, XamlAstNodeDescriptor> DescriptorsById => _descriptorsById;

            public Dictionary<string, XamlAstNodeDescriptor> RuntimeNodesById => _runtimeNodesById;

            public MutableXamlElement? Build()
            {
                if (_document.Syntax.RootSyntax is not IXmlElementSyntax rootElement)
                {
                    return null;
                }

                _path.Add(0);
                var result = BuildElement(rootElement, parent: null);
                _path.Clear();
                return result;
            }

            private MutableXamlElement BuildElement(IXmlElementSyntax syntax, MutableXamlElement? parent)
            {
                var descriptor = MutableXamlDescriptorFactory.CreateDescriptor(_document, syntax, _path);
                var element = new MutableXamlElement(parent, syntax, descriptor);
                _elementsById[descriptor.Id] = element;
                _descriptorsById[descriptor.Id] = descriptor;
                if (!string.IsNullOrEmpty(descriptor.RuntimeName) && !_runtimeNodesById.ContainsKey(descriptor.RuntimeName))
                {
                    _runtimeNodesById[descriptor.RuntimeName] = descriptor;
                }

                if (!string.IsNullOrEmpty(descriptor.XamlName) && !_runtimeNodesById.ContainsKey(descriptor.XamlName))
                {
                    _runtimeNodesById[descriptor.XamlName] = descriptor;
                }

                var childElementIndex = 0;
                foreach (var content in syntax.Content)
                {
                    if (content is IXmlElementSyntax childElement)
                    {
                        _path.Add(childElementIndex++);
                        var child = BuildElement(childElement, element);
                        element.AddChild(child);
                        _path.RemoveAt(_path.Count - 1);
                    }
                    else
                    {
                        element.AddChild(new MutableXamlContentNode(element, content));
                    }
                }

                return element;
            }
        }
    }

    /// <summary>
    /// Base class for mutable XAML nodes.
    /// </summary>
    public abstract class MutableXamlNode
    {
        private MutableXamlElement? _parent;
        private SyntaxNode _syntax;

        protected MutableXamlNode(MutableXamlElement? parent, SyntaxNode syntax)
        {
            _parent = parent;
            _syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
            LeadingTrivia = syntax.GetLeadingTrivia();
            TrailingTrivia = syntax.GetTrailingTrivia();
        }

        public MutableXamlElement? Parent => _parent;

        public SyntaxTriviaList LeadingTrivia { get; private set; }

        public SyntaxTriviaList TrailingTrivia { get; private set; }

        internal SyntaxNode SyntaxNode => _syntax;

        public abstract MutableXamlNodeKind Kind { get; }

        internal void SetParent(MutableXamlElement? parent) => _parent = parent;

        internal virtual void ReplaceSyntax(SyntaxNode syntax)
        {
            if (syntax is null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            var previous = _syntax;
            _syntax = syntax;
            LeadingTrivia = syntax.GetLeadingTrivia();
            TrailingTrivia = syntax.GetTrailingTrivia();

            _parent?.NotifyChildSyntaxChanged(this, previous);
        }
    }

    public enum MutableXamlNodeKind
    {
        Element,
        Content
    }

    /// <summary>
    /// Represents a mutable XAML element.
    /// </summary>
    public sealed class MutableXamlElement : MutableXamlNode
    {
        private readonly List<MutableXamlAttribute> _attributes;
        private readonly List<MutableXamlNode> _children;
        private int[] _path;
        private XamlAstNodeDescriptor _descriptor;

        internal MutableXamlElement(MutableXamlElement? parent, IXmlElementSyntax syntax, XamlAstNodeDescriptor descriptor)
            : base(parent, syntax.AsNode)
        {
            Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
            _attributes = new List<MutableXamlAttribute>();
            _children = new List<MutableXamlNode>();
            _descriptor = descriptor;
            _path = descriptor.Path.ToArray();

            LocalName = descriptor.LocalName;
            Prefix = descriptor.Prefix;

            foreach (var attribute in syntax.Attributes)
            {
                _attributes.Add(new MutableXamlAttribute(this, attribute));
            }
        }

        public override MutableXamlNodeKind Kind => MutableXamlNodeKind.Element;

        /// <summary>
        /// Gets or sets the local name of the element.
        /// </summary>
        public string LocalName { get; set; }

        /// <summary>
        /// Gets or sets the namespace prefix of the element, if any.
        /// </summary>
        public string? Prefix { get; set; }

        /// <summary>
        /// Gets the qualified name (<c>prefix:local</c>) of the element.
        /// </summary>
        public string QualifiedName =>
            string.IsNullOrEmpty(Prefix) ? LocalName : $"{Prefix}:{LocalName}";

        /// <summary>
        /// Gets the descriptor associated with this element.
        /// </summary>
        public XamlAstNodeDescriptor Descriptor => _descriptor;

        /// <summary>
        /// Gets the stable identifier for the element.
        /// </summary>
        public XamlAstNodeId Id => _descriptor.Id;

        /// <summary>
        /// Gets the immutable path that identifies this element in the original tree.
        /// </summary>
        public IReadOnlyList<int> Path => new ReadOnlyCollection<int>(_path);

        /// <summary>
        /// Gets the underlying syntax for the element.
        /// </summary>
        public IXmlElementSyntax Syntax { get; private set; }

        /// <summary>
        /// Enumerates attributes declared on this element.
        /// </summary>
        public IReadOnlyList<MutableXamlAttribute> Attributes => _attributes;

        /// <summary>
        /// Enumerates child nodes (elements and trivia-preserving content).
        /// </summary>
        public IReadOnlyList<MutableXamlNode> Children => _children;

        public new MutableXamlElement? Parent => base.Parent;

        public bool TryReplaceAttribute(MutableXamlAttribute attribute, XmlAttributeSyntax syntax)
        {
            if (attribute is null)
            {
                throw new ArgumentNullException(nameof(attribute));
            }

            if (syntax is null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            if (!ReferenceEquals(attribute.Owner, this))
            {
                throw new InvalidOperationException("Attribute does not belong to this element.");
            }

            var replaced = false;

            switch (Syntax)
            {
                case XmlElementSyntax elementSyntax:
                {
                    var startTag = elementSyntax.StartTag;
                    var attributes = ReplaceAttributeList(startTag.AttributesNode, attribute.Syntax, syntax, out replaced);
                    if (!replaced)
                    {
                        return false;
                    }

                    var updatedStartTag = startTag.WithAttributes(attributes);
                    var updatedElement = elementSyntax.WithStartTag(updatedStartTag);
                    base.ReplaceSyntax((SyntaxNode)updatedElement);
                    Syntax = updatedElement;
                    break;
                }

                case XmlEmptyElementSyntax emptyElementSyntax:
                {
                    var attributes = ReplaceAttributeList(emptyElementSyntax.AttributesNode, attribute.Syntax, syntax, out replaced);
                    if (!replaced)
                    {
                        return false;
                    }

                    var updatedElement = emptyElementSyntax.WithAttributes(attributes);
                    base.ReplaceSyntax((SyntaxNode)updatedElement);
                    Syntax = updatedElement;
                    break;
                }

                default:
                    return false;
            }

            attribute.ReplaceSyntax(syntax);
            return true;
        }

        public MutableXamlAttribute AddAttribute(XmlAttributeSyntax syntax)
        {
            if (syntax is null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            switch (Syntax)
            {
                case XmlElementSyntax elementSyntax:
                {
                    var updatedAttributes = InsertAttribute(elementSyntax.StartTag.AttributesNode, syntax);
                    var updatedStartTag = elementSyntax.StartTag.WithAttributes(updatedAttributes);
                    var updatedElement = elementSyntax.WithStartTag(updatedStartTag);
                    base.ReplaceSyntax((SyntaxNode)updatedElement);
                    Syntax = updatedElement;
                    break;
                }

                case XmlEmptyElementSyntax emptyElementSyntax:
                {
                    var updatedAttributes = InsertAttribute(emptyElementSyntax.AttributesNode, syntax);
                    var updatedElement = emptyElementSyntax.WithAttributes(updatedAttributes);
                    base.ReplaceSyntax((SyntaxNode)updatedElement);
                    Syntax = updatedElement;
                    break;
                }

                default:
                    throw new InvalidOperationException("Unsupported element type for attribute insertion.");
            }

            var attribute = new MutableXamlAttribute(this, syntax);
            _attributes.Add(attribute);
            return attribute;
        }

        public bool RemoveAttribute(MutableXamlAttribute attribute)
        {
            if (attribute is null)
            {
                throw new ArgumentNullException(nameof(attribute));
            }

            if (!ReferenceEquals(attribute.Owner, this))
            {
                return false;
            }

            var removed = false;

            switch (Syntax)
            {
                case XmlElementSyntax elementSyntax:
                {
                    var updatedAttributes = RemoveAttribute(elementSyntax.StartTag.AttributesNode, attribute.Syntax, out removed);
                    if (!removed)
                    {
                        return false;
                    }

                    var updatedStartTag = elementSyntax.StartTag.WithAttributes(updatedAttributes);
                    var updatedElement = elementSyntax.WithStartTag(updatedStartTag);
                    base.ReplaceSyntax((SyntaxNode)updatedElement);
                    Syntax = updatedElement;
                    break;
                }

                case XmlEmptyElementSyntax emptyElementSyntax:
                {
                    var updatedAttributes = RemoveAttribute(emptyElementSyntax.AttributesNode, attribute.Syntax, out removed);
                    if (!removed)
                    {
                        return false;
                    }

                    var updatedElement = emptyElementSyntax.WithAttributes(updatedAttributes);
                    base.ReplaceSyntax((SyntaxNode)updatedElement);
                    Syntax = updatedElement;
                    break;
                }

                default:
                    return false;
            }

            _attributes.Remove(attribute);
            return true;
        }

        public int IndexOfChild(MutableXamlNode child)
        {
            if (child is null)
            {
                throw new ArgumentNullException(nameof(child));
            }

            return _children.IndexOf(child);
        }

        public void InsertRawContent(int index, IReadOnlyList<XmlNodeSyntax> nodes)
        {
            if (nodes is null || nodes.Count == 0)
            {
                return;
            }

            if (Syntax is not XmlElementSyntax elementSyntax)
            {
                throw new InvalidOperationException("Only non-empty elements accept child content.");
            }

            if (index < 0 || index > elementSyntax.Content.Count)
            {
                index = elementSyntax.Content.Count;
            }

            var updatedContent = InsertNodes(elementSyntax.Content, nodes, index);
            var updatedElement = elementSyntax.WithContent(updatedContent);
            base.ReplaceSyntax((SyntaxNode)updatedElement);
            Syntax = updatedElement;
        }

        public bool RemoveChild(MutableXamlNode child)
        {
            if (child is null)
            {
                throw new ArgumentNullException(nameof(child));
            }

            if (!_children.Remove(child))
            {
                return false;
            }

            if (Syntax is XmlElementSyntax elementSyntax)
            {
                var updatedContent = RemoveChildNode(elementSyntax.Content, child.SyntaxNode);
                var updatedElement = elementSyntax.WithContent(updatedContent);
                base.ReplaceSyntax((SyntaxNode)updatedElement);
                Syntax = updatedElement;
            }

            child.SetParent(null);
            return true;
        }

        internal void SetDescriptor(XamlAstNodeDescriptor descriptor)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            _descriptor = descriptor;
            _path = descriptor.Path.ToArray();
        }

        internal void AddChild(MutableXamlNode child)
        {
            if (child is null)
            {
                throw new ArgumentNullException(nameof(child));
            }

            child.SetParent(this);
            _children.Add(child);
        }

        internal override void ReplaceSyntax(SyntaxNode syntax)
        {
            if (syntax is not IXmlElementSyntax elementSyntax)
            {
                throw new ArgumentException("Syntax must represent an XML element.", nameof(syntax));
            }

            base.ReplaceSyntax(syntax);
            Syntax = elementSyntax;
        }

        internal int[] GetPathSnapshot()
        {
            var copy = new int[_path.Length];
            Array.Copy(_path, copy, _path.Length);
            return copy;
        }

        public MutableXamlAttribute? FindAttribute(string localName, string? prefix = null)
        {
            foreach (var attribute in _attributes)
            {
                if (string.Equals(attribute.LocalName, localName, StringComparison.Ordinal) &&
                    string.Equals(attribute.Prefix, prefix, StringComparison.Ordinal))
                {
                    return attribute;
                }
            }

            return null;
        }

        public string? GetAttributeValue(string localName, string? prefix = null) =>
            FindAttribute(localName, prefix)?.Value;

        public void UpdateName(XmlNameSyntax nameSyntax, string localName, string? prefix)
        {
            if (nameSyntax is null)
            {
                throw new ArgumentNullException(nameof(nameSyntax));
            }

            switch (Syntax)
            {
                case XmlElementSyntax elementSyntax:
                {
                    var updatedStart = elementSyntax.StartTag.WithName(nameSyntax);
                    if (updatedStart.AttributesNode.Count > 0)
                    {
                        var firstAttribute = updatedStart.AttributesNode[0];
                        if (!firstAttribute.GetLeadingTrivia().Any(trivia => trivia.Kind == SyntaxKind.WhitespaceTrivia))
                        {
                            var spacedAttribute = firstAttribute.WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.WhitespaceTrivia(" ")));
                            var attributes = updatedStart.AttributesNode.Replace(firstAttribute, spacedAttribute);
                            updatedStart = updatedStart.WithAttributes(attributes);
                        }
                    }
                    var endTagName = nameSyntax.WithTrailingTrivia(SyntaxFactory.TriviaList()) as XmlNameSyntax ?? nameSyntax;
                    var updatedEnd = elementSyntax.EndTag is not null
                        ? SyntaxFactory.XmlElementEndTag(elementSyntax.EndTag.LessThanSlashToken, endTagName, elementSyntax.EndTag.GreaterThanToken)
                        : null;
                    var updatedElement = elementSyntax.WithStartTag(updatedStart).WithEndTag(updatedEnd);
                    ReplaceSyntax((SyntaxNode)updatedElement);
                    break;
                }

                case XmlEmptyElementSyntax emptyElementSyntax:
                {
                    var updatedElement = emptyElementSyntax.WithName(nameSyntax);
                    ReplaceSyntax((SyntaxNode)updatedElement);
                    break;
                }

                default:
                    throw new InvalidOperationException("Unsupported element type for renaming.");
            }

            LocalName = localName;
            Prefix = prefix;
        }

        private static SyntaxList<XmlAttributeSyntax> ReplaceAttributeList(
            SyntaxList<XmlAttributeSyntax> attributes,
            XmlAttributeSyntax original,
            XmlAttributeSyntax replacement,
            out bool replaced)
        {
            replaced = false;

            if (attributes.Count == 0)
            {
                return attributes;
            }

            var list = new List<XmlAttributeSyntax>(attributes.Count);
            foreach (var attribute in attributes)
            {
                if (!replaced && attribute == original)
                {
                    list.Add(replacement);
                    replaced = true;
                }
                else
                {
                    list.Add(attribute);
                }
            }

            return SyntaxFactory.List(list);
        }

        private static SyntaxList<XmlAttributeSyntax> InsertAttribute(
            SyntaxList<XmlAttributeSyntax> attributes,
            XmlAttributeSyntax attribute)
        {
            var list = new List<XmlAttributeSyntax>(attributes.Count + 1);
            foreach (var existing in attributes)
            {
                list.Add(existing);
            }

            list.Add(attribute);
            return SyntaxFactory.List(list);
        }

        private static SyntaxList<XmlAttributeSyntax> RemoveAttribute(
            SyntaxList<XmlAttributeSyntax> attributes,
            XmlAttributeSyntax syntax,
            out bool removed)
        {
            removed = false;

            if (attributes.Count == 0)
            {
                return attributes;
            }

            var list = new List<XmlAttributeSyntax>(attributes.Count);
            foreach (var attribute in attributes)
            {
                if (!removed && attribute == syntax)
                {
                    removed = true;
                    continue;
                }

                list.Add(attribute);
            }

            return SyntaxFactory.List(list);
        }

        private static SyntaxList<XmlNodeSyntax> InsertNodes(
            SyntaxList<XmlNodeSyntax> content,
            IReadOnlyList<XmlNodeSyntax> nodes,
            int index)
        {
            var list = new List<XmlNodeSyntax>(content.Count + nodes.Count);

            for (var i = 0; i < index && i < content.Count; i++)
            {
                list.Add(content[i]);
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                list.Add(nodes[i]);
            }

            for (var i = index; i < content.Count; i++)
            {
                list.Add(content[i]);
            }

            return SyntaxFactory.List(list);
        }

        private static SyntaxList<XmlNodeSyntax> RemoveChildNode(
            SyntaxList<XmlNodeSyntax> nodes,
            SyntaxNode childSyntax)
        {
            var list = new List<XmlNodeSyntax>(Math.Max(nodes.Count - 1, 0));
            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, childSyntax))
                {
                    continue;
                }

                list.Add(node);
            }

            return SyntaxFactory.List(list);
        }

        private static SyntaxList<XmlNodeSyntax> ReplaceChildNode(
            SyntaxList<XmlNodeSyntax> nodes,
            SyntaxNode original,
            SyntaxNode replacement,
            out bool replaced)
        {
            replaced = false;

            if (nodes.Count == 0)
            {
                return nodes;
            }

            if (replacement is not XmlNodeSyntax replacementNode)
            {
                throw new ArgumentException("Replacement syntax must represent an XML node.", nameof(replacement));
            }

            var list = new List<XmlNodeSyntax>(nodes.Count);
            foreach (var node in nodes)
            {
                if (!replaced && ReferenceEquals(node, original))
                {
                    list.Add(replacementNode);
                    replaced = true;
                }
                else
                {
                    list.Add(node);
                }
            }

            return replaced ? SyntaxFactory.List(list) : nodes;
        }

        internal void NotifyChildSyntaxChanged(MutableXamlNode child, SyntaxNode previousSyntax)
        {
            if (child is null || previousSyntax is null)
            {
                return;
            }

            if (Syntax is not XmlElementSyntax elementSyntax)
            {
                return;
            }

            var updatedContent = ReplaceChildNode(elementSyntax.Content, previousSyntax, child.SyntaxNode, out var replaced);
            if (!replaced)
            {
                return;
            }

            var updatedElement = elementSyntax.WithContent(updatedContent);
            base.ReplaceSyntax((SyntaxNode)updatedElement);
            Syntax = updatedElement;
        }
    }

    /// <summary>
    /// Represents a mutable attribute on a XAML element.
    /// </summary>
    public sealed class MutableXamlAttribute
    {
        internal MutableXamlAttribute(MutableXamlElement owner, XmlAttributeSyntax syntax)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));

            var nameNode = syntax.NameNode;
            LocalName = nameNode?.LocalName ?? string.Empty;
            Prefix = nameNode?.Prefix;
            Value = syntax.Value ?? string.Empty;
            LeadingTrivia = syntax.GetLeadingTrivia();
            TrailingTrivia = syntax.GetTrailingTrivia();
        }

        public MutableXamlElement Owner { get; }

        public string LocalName { get; set; }

        public string? Prefix { get; set; }

        public string Value { get; set; }

        public SyntaxTriviaList LeadingTrivia { get; private set; }

        public SyntaxTriviaList TrailingTrivia { get; private set; }

        public string FullName =>
            string.IsNullOrEmpty(Prefix) ? LocalName : $"{Prefix}:{LocalName}";

        internal XmlAttributeSyntax Syntax { get; private set; }

        internal void ReplaceSyntax(XmlAttributeSyntax syntax)
        {
            Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
            LeadingTrivia = syntax.GetLeadingTrivia();
            TrailingTrivia = syntax.GetTrailingTrivia();
        }
    }

    /// <summary>
    /// Represents non-element content such as text or comments.
    /// </summary>
    public sealed class MutableXamlContentNode : MutableXamlNode
    {
        internal MutableXamlContentNode(MutableXamlElement? parent, SyntaxNode syntax)
            : base(parent, syntax)
        {
            if (syntax is null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            SyntaxKind = syntax.Kind;
            Text = syntax.ToFullString();
        }

        public override MutableXamlNodeKind Kind => MutableXamlNodeKind.Content;

        public SyntaxKind SyntaxKind { get; }

        public string Text { get; private set; }

        public void SetText(string text)
        {
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }
    }

    internal static class MutableXamlDescriptorFactory
    {
        public static XamlAstNodeDescriptor CreateDescriptor(
            XamlAstDocument document,
            IXmlElementSyntax syntax,
            IReadOnlyList<int> path)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (syntax is null)
            {
                throw new ArgumentNullException(nameof(syntax));
            }

            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            var nameNode = syntax.NameNode;
            var localName = nameNode?.LocalName ?? string.Empty;
            var prefix = nameNode?.Prefix;
            var qualifiedName = string.IsNullOrEmpty(prefix) ? localName : $"{prefix}:{localName}";

            var attributes = BuildAttributeDescriptors(syntax.Attributes);
            var xName = syntax.GetAttributeValue("Name", "x");
            var runtimeName = syntax.GetAttributeValue("Name");
            var resourceKey = syntax.GetAttributeValue("Key", "x") ?? syntax.GetAttributeValue("Key");
            var xUid = syntax.GetAttributeValue("Uid", "x");

            var span = syntax.AsNode.Span;
            document.TryGetLinePositionSpan(span, out var lineSpan);

            var pathCopy = path.ToArray();
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

        public static XamlAstNodeDescriptor CreateDescriptor(XamlAstDocument document, MutableXamlElement element)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (element is null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            var syntax = element.Syntax;
            var nameNode = syntax.NameNode;
            var localName = nameNode?.LocalName ?? string.Empty;
            var prefix = nameNode?.Prefix;
            var qualifiedName = string.IsNullOrEmpty(prefix) ? localName : $"{prefix}:{localName}";

            var attributes = BuildAttributeDescriptors(element.Attributes);

            var xName = element.GetAttributeValue("Name", "x");
            var runtimeName = element.GetAttributeValue("Name");
            var resourceKey = element.GetAttributeValue("Key", "x") ?? element.GetAttributeValue("Key");
            var xUid = element.GetAttributeValue("Uid", "x");

            var span = element.Syntax.AsNode.Span;
            document.TryGetLinePositionSpan(span, out var lineSpan);

            var pathCopy = element.GetPathSnapshot();
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

        private static IReadOnlyList<XamlAttributeDescriptor> BuildAttributeDescriptors(IEnumerable<XmlAttributeSyntax> attributes)
        {
            if (attributes is null)
            {
                return Array.Empty<XamlAttributeDescriptor>();
            }

            var list = new List<XamlAttributeDescriptor>();
            foreach (var attribute in attributes)
            {
                var nameNode = attribute?.NameNode;
                var localName = nameNode?.LocalName ?? string.Empty;
                var prefix = nameNode?.Prefix;
                var value = attribute?.Value ?? string.Empty;
                list.Add(new XamlAttributeDescriptor(localName, prefix, value));
            }

            return list.AsReadOnly();
        }

        private static XamlAstNodeId CreateNodeId(
            string qualifiedName,
            IReadOnlyList<int> path,
            string? xName,
            string? resourceKey)
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

        private static IReadOnlyList<XamlAttributeDescriptor> BuildAttributeDescriptors(IReadOnlyList<MutableXamlAttribute> attributes)
        {
            if (attributes is null || attributes.Count == 0)
            {
                return Array.Empty<XamlAttributeDescriptor>();
            }

            var list = new List<XamlAttributeDescriptor>(attributes.Count);
            foreach (var attribute in attributes)
            {
                list.Add(new XamlAttributeDescriptor(attribute.LocalName, attribute.Prefix, attribute.Value));
            }

            return list.AsReadOnly();
        }
    }

    public static class MutableXamlSerializer
    {
        public static string Serialize(MutableXamlDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (document.Root is null)
            {
                return document.SourceDocument.Text;
            }

            var sourceSyntax = document.SourceDocument.Syntax;
            if (sourceSyntax.RootSyntax is not IXmlElementSyntax originalRoot)
            {
                return document.Root.Syntax.AsNode.ToFullString();
            }

            var updatedSyntax = sourceSyntax.ReplaceNode(originalRoot.AsNode, document.Root.Syntax.AsNode);
            return updatedSyntax.ToFullString();
        }
    }
}
