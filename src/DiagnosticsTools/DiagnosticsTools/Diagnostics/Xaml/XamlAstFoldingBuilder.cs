using System;
using System.Collections.Generic;
using AvaloniaEdit.Folding;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.Xaml
{
    internal sealed class XamlAstFoldingBuilder
    {
        private const int MinimumFoldLength = 8;

        public IReadOnlyList<NewFolding> BuildFoldings(XamlAstDocument document)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var foldings = new List<NewFolding>();

            if (document.Syntax.RootSyntax is { } root)
            {
                CollectFoldings(root.AsNode, foldings, document);
            }

            if (foldings.Count == 0)
            {
                return Array.Empty<NewFolding>();
            }

            foldings.Sort((left, right) => left.StartOffset.CompareTo(right.StartOffset));
            return foldings;
        }

        private static void CollectFoldings(SyntaxNode node, List<NewFolding> foldings, XamlAstDocument document)
        {
            if (node is null)
            {
                return;
            }

            if (node is IXmlElementSyntax element)
            {
                TryAddFolding(element, foldings, document);
            }

            foreach (var child in node.ChildNodes)
            {
                CollectFoldings(child, foldings, document);
            }
        }

        private static void TryAddFolding(IXmlElementSyntax element, List<NewFolding> foldings, XamlAstDocument document)
        {
            if (element is null)
            {
                return;
            }

            var span = element.AsNode.Span;
            if (span.Length < MinimumFoldLength)
            {
                return;
            }

            var text = document.Text;
            if (!ContainsNewLine(text, span.Start, span.Length))
            {
                return;
            }

            var startOffset = Math.Max(0, span.Start);
            var endOffset = Math.Min(text.Length, span.End);

            if (endOffset - startOffset < MinimumFoldLength)
            {
                return;
            }

            var nameNode = element.NameNode;
            var localName = nameNode?.LocalName ?? string.Empty;
            var prefix = nameNode?.Prefix;
            var qualifiedName = string.IsNullOrEmpty(prefix) ? localName : $"{prefix}:{localName}";
            if (string.IsNullOrWhiteSpace(qualifiedName))
            {
                qualifiedName = "element";
            }

            var display = $"<{qualifiedName}>";
            foldings.Add(new NewFolding(startOffset, endOffset)
            {
                Name = display
            });
        }

        private static bool ContainsNewLine(string text, int start, int length)
        {
            var limit = Math.Min(text.Length, start + length);
            for (var index = start; index < limit; index++)
            {
                if (text[index] is '\r' or '\n')
                {
                    return true;
                }
            }

            return false;
        }
    }
}
