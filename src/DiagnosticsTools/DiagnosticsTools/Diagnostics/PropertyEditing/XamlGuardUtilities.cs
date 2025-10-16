using System;
using System.Text;
using System.IO.Hashing;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal static class XamlGuardUtilities
    {
        public static string ComputeAttributeHash(XamlAstDocument document, XamlAstNodeDescriptor descriptor, string attributeName)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (descriptor.LocalName is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var text = document.Text.AsSpan();
            if (document.Syntax.RootSyntax is not SyntaxNode root)
            {
                return HashSpan(text.Slice(descriptor.Span.Start, descriptor.Span.Length));
            }

            var node = FindNodeBySpan(root, descriptor.Span);
            if (node is XmlElementSyntax element)
            {
                var attribute = FindAttribute(element.StartTag.AttributesNode, attributeName);
                if (attribute is not null)
                {
                    return HashSpan(text.Slice(attribute.Span.Start, attribute.Span.Length));
                }

                return HashSpan(text.Slice(element.StartTag.Span.Start, element.StartTag.Span.Length));
            }

            if (node is XmlEmptyElementSyntax emptyElement)
            {
                var attribute = FindAttribute(emptyElement.AttributesNode, attributeName);
                if (attribute is not null)
                {
                    return HashSpan(text.Slice(attribute.Span.Start, attribute.Span.Length));
                }

                return HashSpan(text.Slice(emptyElement.Span.Start, emptyElement.Span.Length));
            }

            return HashSpan(text.Slice(descriptor.Span.Start, descriptor.Span.Length));
        }

        public static string ComputeNodeHash(XamlAstDocument document, XamlAstNodeDescriptor descriptor)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (document.Syntax.RootSyntax is not SyntaxNode root)
            {
                return HashSpan(document.Text.AsSpan(descriptor.Span.Start, descriptor.Span.Length));
            }

            var node = FindNodeBySpan(root, descriptor.Span);
            if (node is null)
            {
                return HashSpan(document.Text.AsSpan(descriptor.Span.Start, descriptor.Span.Length));
            }

            return HashSpan(document.Text.AsSpan(node.Span.Start, node.Span.Length));
        }

        public static bool TryLocateNode(
            XamlAstDocument document,
            XamlAstNodeDescriptor descriptor,
            out SyntaxNode? node)
        {
            return TryLocateNode(document, descriptor, out node, out _);
        }

        public static bool TryLocateNode(
            XamlAstDocument document,
            XamlAstNodeDescriptor descriptor,
            out SyntaxNode? node,
            out SyntaxNode? parent)
        {
            node = null;
            parent = null;

            if (document.Syntax.RootSyntax is not SyntaxNode root)
            {
                return false;
            }

            node = FindNodeBySpan(root, descriptor.Span, out parent);
            return node is not null;
        }

        public static bool TryLocateAttribute(
            XamlAstDocument document,
            XamlAstNodeDescriptor descriptor,
            string attributeName,
            out XmlAttributeSyntax? attribute,
            out SyntaxNode? owningNode)
        {
            attribute = null;
            owningNode = null;

            if (document.Syntax.RootSyntax is not SyntaxNode root)
            {
                return false;
            }

            owningNode = FindNodeBySpan(root, descriptor.Span);

            if (owningNode is XmlElementSyntax element)
            {
                attribute = FindAttribute(element.StartTag.AttributesNode, attributeName);
                return true;
            }

            if (owningNode is XmlEmptyElementSyntax emptyElement)
            {
                attribute = FindAttribute(emptyElement.AttributesNode, attributeName);
                return true;
            }

            owningNode = null;
            attribute = null;
            return false;
        }

        private static XmlAttributeSyntax? FindAttribute(SyntaxList<XmlAttributeSyntax> attributes, string attributeName)
        {
            for (var index = 0; index < attributes.Count; index++)
            {
                if (attributes[index] is XmlAttributeSyntax candidate)
                {
                    var local = candidate.NameNode.LocalName ?? string.Empty;
                    var prefix = candidate.NameNode.Prefix;
                    var fullName = string.IsNullOrEmpty(prefix) ? local : $"{prefix}:{local}";

                    if (string.Equals(fullName, attributeName, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static SyntaxNode? FindNodeBySpan(SyntaxNode node, TextSpan span) =>
            FindNodeBySpan(node, span, out _);

        private static SyntaxNode? FindNodeBySpan(SyntaxNode node, TextSpan span, out SyntaxNode? parent) =>
            FindNodeBySpanCore(node, span, null, out parent);

        private static SyntaxNode? FindNodeBySpanCore(
            SyntaxNode current,
            TextSpan span,
            SyntaxNode? parent,
            out SyntaxNode? foundParent)
        {
            if (current.Span.Start == span.Start && current.Span.End == span.End)
            {
            foundParent = parent;
                return current;
            }

            foreach (var child in current.ChildNodes)
            {
                var match = FindNodeBySpanCore(child, span, current, out foundParent);
                if (match is not null)
                {
                    return match;
                }
            }

            foundParent = null;
            return null;
        }

        private static string HashSpan(ReadOnlySpan<char> span)
        {
            var bytes = Encoding.UTF8.GetBytes(span.ToString());
            var hash = XxHash64.HashToUInt64(bytes);
            return $"h64:{hash:x}";
        }
    }
}
