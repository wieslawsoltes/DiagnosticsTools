using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal static class XamlMutationEditBuilder
    {
        public static bool TryBuildEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (index is null)
            {
                throw new ArgumentNullException(nameof(index));
            }

            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            failure = default;

            switch (operation.Type)
            {
                case ChangeOperationTypes.SetAttribute:
                    return TryBuildSetAttributeEdits(document, index, operation, edits, out failure);

                case ChangeOperationTypes.UpsertElement:
                    return TryBuildUpsertElementEdits(document, index, operation, edits, out failure);

                case ChangeOperationTypes.RemoveNode:
                    return TryBuildRemoveNodeEdits(document, index, operation, edits, out failure);

                case ChangeOperationTypes.RenameResource:
                    return TryBuildRenameResourceEdits(document, index, operation, edits, out failure);

                case ChangeOperationTypes.RenameElement:
                    return TryBuildRenameElementEdits(document, index, operation, edits, out failure);

                case ChangeOperationTypes.ReorderNode:
                    return TryBuildReorderNodeEdits(document, index, operation, edits, out failure);

                case ChangeOperationTypes.SetNamespace:
                    return TryBuildSetNamespaceEdits(document, index, operation, edits, out failure);

                case ChangeOperationTypes.SetContentText:
                    return TryBuildSetContentTextEdits(document, index, operation, edits, out failure);

                default:
                    failure = ChangeDispatchResult.MutationFailure(
                        operation.Id,
                        $"Unsupported change type '{operation.Type}'.");
                    return false;
            }
        }

        private static bool TryBuildSetAttributeEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for SetAttribute operation.");
                return false;
            }

            if (string.IsNullOrEmpty(payload.Name))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Attribute name is required for SetAttribute operations.");
                return false;
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeAttributeHash(document, descriptor, payload.Name),
                operation.Id,
                "Attribute span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (payload.Name.Contains('.') &&
                !ValidateParentGuard(document, index, descriptor, operation, out failure))
            {
                return false;
            }

            if (!XamlGuardUtilities.TryLocateAttribute(document, descriptor, payload.Name, out var attribute, out var ownerNode))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate target element in syntax tree.");
                return false;
            }

            var valueKind = payload.ValueKind ?? string.Empty;
            var isUnset = string.Equals(valueKind, "Unset", StringComparison.OrdinalIgnoreCase) || payload.NewValue is null;

            if (isUnset)
            {
                if (attribute is null)
                {
                    // Attribute already absent; treat as no-op.
                    return true;
                }

                edits.Add(BuildAttributeRemovalEdit(document.Text, attribute.Span));
                return true;
            }

            var newValue = payload.NewValue;
            if (string.IsNullOrEmpty(newValue))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "SetAttribute requires a non-empty newValue.");
                return false;
            }

            if (attribute is not null && attribute.ValueNode is { } valueNode)
            {
                var text = document.Text;
                var valueSpan = valueNode.Span;
                var original = text.Substring(valueSpan.Start, valueSpan.Length);
                var quote = DetermineQuoteCharacter(original);
                var replacement = string.Concat(quote, newValue, quote);

                edits.Add(new XamlTextEdit(valueSpan.Start, valueSpan.Length, replacement));
                return true;
            }

            if (ownerNode is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to determine element owning the attribute.");
                return false;
            }

            var insertionIndex = DetermineAttributeInsertionIndex(ownerNode);
            if (insertionIndex < 0)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Failed to determine attribute insertion location.");
                return false;
            }

            var attributeText = BuildAttributeInsertionText(document.Text, insertionIndex, payload.Name, newValue!);
            edits.Add(new XamlTextEdit(insertionIndex, 0, attributeText));
            return true;
        }

        private static bool TryBuildUpsertElementEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for UpsertElement operation.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(payload.Serialized))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "UpsertElement requires a serialized fragment.");
                return false;
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeNodeHash(document, descriptor),
                operation.Id,
                "Element span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (!ValidateParentGuard(document, index, descriptor, operation, out failure))
            {
                return false;
            }

            if (payload.InsertionIndex is int insertionIndex)
            {
                if (!XamlGuardUtilities.TryLocateNode(document, descriptor, out var owningNode) || owningNode is null)
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target element not found in syntax tree.");
                    return false;
                }

                if (owningNode is XmlElementSyntax element)
                {
                    var position = DetermineChildInsertionPosition(element, insertionIndex);
                    if (position < 0)
                    {
                        failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate insertion position for element content.");
                        return false;
                    }

                    var insertionText = BuildElementInsertionText(payload);
                    edits.Add(new XamlTextEdit(position, 0, insertionText));
                    return true;
                }
                else if (owningNode is XmlEmptyElementSyntax emptyElement)
                {
                    var name = BuildQualifiedName(emptyElement.NameNode);
                    var indent = GetIndentation(document.Text, emptyElement.Span.Start);
                    var newline = DetermineLineEnding(document.Text);
                    var childrenPrefix = payload.SurroundingWhitespace ?? newline;
                    var serializedChildren = payload.Serialized ?? string.Empty;
                    var builder = new StringBuilder();
                    builder.Append('<');
                    builder.Append(name);
                    builder.Append('>');
                    builder.Append(childrenPrefix);
                    builder.Append(serializedChildren);

                    if (!serializedChildren.EndsWith(newline, StringComparison.Ordinal))
                    {
                        builder.Append(newline);
                    }

                    builder.Append(indent);
                    builder.Append("</");
                    builder.Append(name);
                    builder.Append('>');

                    edits.Add(new XamlTextEdit(emptyElement.Span.Start, emptyElement.Span.Length, builder.ToString()));
                    return true;
                }

                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target element does not support child insertion.");
                return false;
            }

            // Replace the existing element span with the serialized fragment.
            var replacementText = BuildElementReplacementText(payload);
            edits.Add(new XamlTextEdit(descriptor.Span.Start, descriptor.Span.Length, replacementText));
            return true;
        }

        private static bool TryBuildRemoveNodeEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeNodeHash(document, descriptor),
                operation.Id,
                "Node span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (!ValidateParentGuard(document, index, descriptor, operation, out failure))
            {
                return false;
            }

            if (!XamlGuardUtilities.TryLocateNode(document, descriptor, out var node) || node is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target node not found in syntax tree.");
                return false;
            }

            edits.Add(BuildNodeRemovalEdit(document.Text, node.Span));
            return true;
        }

        private static bool TryBuildRenameResourceEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for RenameResource operation.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(payload.OldKey) || string.IsNullOrWhiteSpace(payload.NewKey))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "RenameResource requires both oldKey and newKey.");
                return false;
            }

            var oldKey = payload.OldKey!;
            var newKey = payload.NewKey!;

            if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                // Nothing to do.
                return true;
            }

            var keyAttributeName = DetermineResourceKeyAttributeName(descriptor);
            if (string.IsNullOrEmpty(keyAttributeName))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target resource does not declare a key attribute.");
                return false;
            }

            var keyAttribute = keyAttributeName!;
            var occupiedSpans = CreateOccupiedSpanSet(edits);

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeAttributeHash(document, descriptor, keyAttribute),
                operation.Id,
                "Resource key span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (!ValidateParentGuard(document, index, descriptor, operation, out failure))
            {
                return false;
            }

            if (!XamlGuardUtilities.TryLocateAttribute(document, descriptor, keyAttribute, out var attribute, out _)
                || attribute is null
                || attribute.ValueNode is not { } valueNode)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate resource key attribute in syntax tree.");
                return false;
            }

            var text = document.Text;
            var currentValue = ExtractAttributeValue(text, valueNode.Span);
            if (!string.Equals(currentValue, oldKey, StringComparison.Ordinal))
            {
                failure = ChangeDispatchResult.GuardFailure(operation.Id, "Resource key mismatch.");
                return false;
            }

            var original = text.Substring(valueNode.Span.Start, valueNode.Span.Length);
            var quote = DetermineQuoteCharacter(original);
            var replacement = string.Concat(quote, newKey, quote);

            if (occupiedSpans.Add((valueNode.Span.Start, valueNode.Span.Length)))
            {
                edits.Add(new XamlTextEdit(valueNode.Span.Start, valueNode.Span.Length, replacement));
            }

            if (payload.CascadeTargets is { Count: > 0 })
            {
                var processed = new HashSet<string>(StringComparer.Ordinal);
                var descriptorId = descriptor.Id.ToString();

                foreach (var targetId in payload.CascadeTargets)
                {
                    if (string.IsNullOrWhiteSpace(targetId) ||
                        string.Equals(targetId, descriptorId, StringComparison.Ordinal) ||
                        !processed.Add(targetId))
                    {
                        continue;
                    }

                    if (!TryProcessCascadeTarget(
                            document,
                            index,
                            targetId,
                            oldKey,
                            newKey,
                            operation.Id,
                            occupiedSpans,
                            edits,
                            out failure))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryBuildRenameElementEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for RenameElement operation.");
                return false;
            }

            var newName = payload.NewValue;
            if (string.IsNullOrWhiteSpace(newName))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "RenameElement requires a non-empty newValue.");
                return false;
            }

            if (string.Equals(descriptor.QualifiedName, newName, StringComparison.Ordinal))
            {
                return true;
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeNodeHash(document, descriptor),
                operation.Id,
                "Element span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (!XamlGuardUtilities.TryLocateNode(document, descriptor, out var node, out _)
                || node is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target element not found in syntax tree.");
                return false;
            }

            switch (node)
            {
                case XmlElementSyntax element:
                {
                    var startSpan = element.StartTag.NameNode.Span;
                    edits.Add(new XamlTextEdit(startSpan.Start, startSpan.Length, newName));

                    if (element.EndTag is { NameNode: { } endName })
                    {
                        var endSpan = endName.Span;
                        edits.Add(new XamlTextEdit(endSpan.Start, endSpan.Length, newName));
                    }

                    return true;
                }

                case XmlEmptyElementSyntax emptyElement:
                {
                    var span = emptyElement.NameNode.Span;
                    edits.Add(new XamlTextEdit(span.Start, span.Length, newName));
                    return true;
                }

                default:
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "RenameElement supports element nodes only.");
                    return false;
            }
        }

        private static bool TryBuildReorderNodeEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            var payload = operation.Payload;
            if (payload?.NewIndex is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "ReorderNode requires a newIndex payload value.");
                return false;
            }

            var newIndex = payload.NewIndex.Value;
            if (newIndex < 0)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "ReorderNode newIndex must be non-negative.");
                return false;
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeNodeHash(document, descriptor),
                operation.Id,
                "Node span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (!ValidateParentGuard(document, index, descriptor, operation, out failure))
            {
                return false;
            }

            if (!XamlGuardUtilities.TryLocateNode(document, descriptor, out var node, out var parentNode)
                || node is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target node not found in syntax tree.");
                return false;
            }

            var parentElement = GetOwningElement(parentNode);
            if (parentElement is null)
            {
                var parentType = parentNode?.GetType().FullName ?? "<null parent>";
                failure = ChangeDispatchResult.MutationFailure(operation.Id, $"ReorderNode requires an element parent (found {parentType}).");
                return false;
            }

            var children = parentElement.Elements.ToList();
            var childrenCount = children.Count;
            if (childrenCount == 0)
            {
                return true;
            }

            var pathSegments = descriptor.Path;
            var pathCount = pathSegments.Count;
            var currentIndex = pathCount > 0 ? pathSegments[pathCount - 1] : children.FindIndex(c => c.AsNode.Span == node.Span);
            if (currentIndex < 0 || currentIndex >= childrenCount)
            {
                currentIndex = children.FindIndex(child => child.AsNode.Span == node.Span);
            }

            if (currentIndex < 0)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to determine current child index for reorder.");
                return false;
            }

            if (currentIndex == newIndex)
            {
                return true;
            }

            var boundedIndex = Math.Min(newIndex, childrenCount - 1);

            if (boundedIndex < 0)
            {
                boundedIndex = 0;
            }

            var removalEdit = BuildNodeRemovalEdit(document.Text, node.Span);
            var fragment = document.Text.Substring(removalEdit.Start, removalEdit.Length);
            edits.Add(removalEdit);

            int insertionIndex;

            if (boundedIndex < currentIndex)
            {
                var targetNode = children[boundedIndex].AsNode;
                insertionIndex = targetNode.Span.Start;
            }
            else
            {
                if (boundedIndex >= childrenCount - 1)
                {
                    insertionIndex = DetermineChildInsertionPosition(parentElement, childrenCount);
                }
                else
                {
                    var anchorNode = children[boundedIndex].AsNode;
                    insertionIndex = MovePastTrailingWhitespace(document.Text, anchorNode.Span.End);
                }
            }

            edits.Add(new XamlTextEdit(insertionIndex, 0, fragment));
            return true;
        }

        private static bool TryBuildSetNamespaceEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for SetNamespace operation.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(payload.Name))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "SetNamespace requires a namespace name.");
                return false;
            }

            var attributeName = payload.Name!;

            if (!attributeName.StartsWith("xmlns", StringComparison.Ordinal))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Namespace attributes must begin with 'xmlns'.");
                return false;
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeAttributeHash(document, descriptor, attributeName),
                operation.Id,
                "Namespace span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (!XamlGuardUtilities.TryLocateAttribute(document, descriptor, attributeName, out var attribute, out var ownerNode))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate namespace owning element.");
                return false;
            }

            var newValue = payload.NewValue;
            if (string.IsNullOrWhiteSpace(newValue))
            {
                if (attribute is null)
                {
                    return true;
                }

                edits.Add(BuildAttributeRemovalEdit(document.Text, attribute.Span));
                return true;
            }

            if (attribute is not null && attribute.ValueNode is { } valueNode)
            {
                var text = document.Text;
                var valueSpan = valueNode.Span;
                var original = text.Substring(valueSpan.Start, valueSpan.Length);
                var quote = DetermineQuoteCharacter(original);
                var replacement = string.Concat(quote, newValue, quote);
                edits.Add(new XamlTextEdit(valueSpan.Start, valueSpan.Length, replacement));
                return true;
            }

            if (ownerNode is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to determine namespace insertion point.");
                return false;
            }

            var insertionIndex = DetermineAttributeInsertionIndex(ownerNode);
            if (insertionIndex < 0)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Failed to determine namespace insertion location.");
                return false;
            }

            var attributeText = BuildAttributeInsertionText(document.Text, insertionIndex, attributeName, newValue);
            edits.Add(new XamlTextEdit(insertionIndex, 0, attributeText));
            return true;
        }

        private static bool TryBuildSetContentTextEdits(
            XamlAstDocument document,
            IXamlAstIndex index,
            ChangeOperation operation,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!TryGetDescriptor(index, operation, out var descriptor, out failure))
            {
                return false;
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for SetContentText operation.");
                return false;
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeNodeHash(document, descriptor),
                operation.Id,
                "Node span hash mismatch.",
                out failure))
            {
                return false;
            }

            if (!XamlGuardUtilities.TryLocateNode(document, descriptor, out var node, out _)
                || node is not XmlElementSyntax element)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "SetContentText requires a non-empty element.");
                return false;
            }

            var newText = payload.NewValue ?? string.Empty;
            var escapedText = EscapeContentText(newText);

            if (element.Elements.Any())
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "SetContentText cannot be applied to elements with child elements.");
                return false;
            }

            var textNodes = new List<XmlTextSyntax>();
            foreach (var contentNode in element.Content)
            {
                if (contentNode is XmlTextSyntax textNode)
                {
                    textNodes.Add(textNode);
                }
            }

            if (!string.IsNullOrEmpty(escapedText))
            {
                if (textNodes.Count > 0)
                {
                    var primary = textNodes[0];
                    if (!string.Equals(document.Text.Substring(primary.Span.Start, primary.Span.Length), escapedText, StringComparison.Ordinal))
                    {
                        edits.Add(new XamlTextEdit(primary.Span.Start, primary.Span.Length, escapedText));
                    }

                    for (var i = 1; i < textNodes.Count; i++)
                    {
                        edits.Add(BuildNodeRemovalEdit(document.Text, textNodes[i].Span));
                    }
                }
                else
                {
                    var insertionIndex = element.StartTag.GreaterThanToken.Span.End;
                    edits.Add(new XamlTextEdit(insertionIndex, 0, escapedText));
                }
            }
            else
            {
                foreach (var textNode in textNodes)
                {
                    edits.Add(BuildNodeRemovalEdit(document.Text, textNode.Span));
                }
            }

            return true;
        }

        private static bool TryGetDescriptor(
            IXamlAstIndex index,
            ChangeOperation operation,
            out XamlAstNodeDescriptor descriptor,
            out ChangeDispatchResult failure)
        {
            failure = default;
            descriptor = default!;

            var descriptorId = operation.Target?.DescriptorId;
            if (string.IsNullOrWhiteSpace(descriptorId))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Change operation missing target descriptor identifier.");
                return false;
            }

            if (!index.TryGetDescriptor(new XamlAstNodeId(descriptorId!), out descriptor))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target node not found in XAML document.");
                return false;
            }

            return true;
        }

        private static bool ValidateSpanHash(
            string? expectedHash,
            Func<string> currentHashFactory,
            string? operationId,
            string failureMessage,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                return true;
            }

            var currentHash = currentHashFactory();
            if (!string.Equals(expectedHash, currentHash, StringComparison.Ordinal))
            {
                failure = ChangeDispatchResult.GuardFailure(operationId, failureMessage);
                return false;
            }

            return true;
        }

        private static bool ValidateParentGuard(
            XamlAstDocument document,
            IXamlAstIndex index,
            XamlAstNodeDescriptor descriptor,
            ChangeOperation operation,
            out ChangeDispatchResult failure)
        {
            failure = default;
            var expectedParentHash = operation.Guard?.ParentSpanHash;

            if (string.IsNullOrWhiteSpace(expectedParentHash))
            {
                return true;
            }

            if (!TryGetParentDescriptor(index, descriptor, out var parent))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate parent node for guard validation.");
                return false;
            }

            var currentHash = XamlGuardUtilities.ComputeNodeHash(document, parent);
            if (!string.Equals(expectedParentHash, currentHash, StringComparison.Ordinal))
            {
                failure = ChangeDispatchResult.GuardFailure(operation.Id, "Parent span hash mismatch.");
                return false;
            }

            return true;
        }

        private static bool TryGetParentDescriptor(
            IXamlAstIndex index,
            XamlAstNodeDescriptor descriptor,
            out XamlAstNodeDescriptor parent)
        {
            parent = default!;

            var path = descriptor.Path;
            if (path.Count <= 1)
            {
                return false;
            }

            var expectedDepth = path.Count - 1;

            foreach (var candidate in index.Nodes)
            {
                if (candidate.Path.Count != expectedDepth)
                {
                    continue;
                }

                if (!PathMatches(candidate.Path, path, expectedDepth))
                {
                    continue;
                }

                if (candidate.Span.Start <= descriptor.Span.Start &&
                    candidate.Span.End >= descriptor.Span.End)
                {
                    parent = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool PathMatches(IReadOnlyList<int> candidate, IReadOnlyList<int> target, int length)
        {
            for (var index = 0; index < length; index++)
            {
                if (candidate[index] != target[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static XamlTextEdit BuildAttributeRemovalEdit(string text, TextSpan span)
        {
            var start = span.Start;
            var end = span.End;

            // Include preceding whitespace on the same line.
            var removalStart = start;
            while (removalStart > 0)
            {
                var ch = text[removalStart - 1];
                if (ch == ' ' || ch == '\t')
                {
                    removalStart--;
                    continue;
                }

                if (ch == '\n')
                {
                    removalStart--;
                    break;
                }

                if (ch == '\r')
                {
                    removalStart--;
                    if (removalStart > 0 && text[removalStart - 1] == '\n')
                    {
                        removalStart--;
                    }

                    break;
                }

                break;
            }

            // Collapse trailing single spaces to avoid duplication.
            var removalEnd = end;
            if (removalEnd < text.Length && (text[removalEnd] == ' ' || text[removalEnd] == '\t'))
            {
                removalEnd++;
            }

            return new XamlTextEdit(removalStart, removalEnd - removalStart, string.Empty);
        }

        private static XamlTextEdit BuildNodeRemovalEdit(string text, TextSpan span)
        {
            var removalStart = span.Start;
            var removalEnd = span.End;

            while (removalStart > 0)
            {
                var ch = text[removalStart - 1];
                if (ch == ' ' || ch == '\t')
                {
                    removalStart--;
                    continue;
                }

                if (ch == '\n')
                {
                    removalStart--;
                    break;
                }

                if (ch == '\r')
                {
                    removalStart--;
                    if (removalStart > 0 && text[removalStart - 1] == '\n')
                    {
                        removalStart--;
                    }

                    break;
                }

                break;
            }

            var index = removalEnd;
            var hasLineBreak = false;
            while (index < text.Length)
            {
                var ch = text[index];
                if (ch == ' ' || ch == '\t')
                {
                    index++;
                    continue;
                }

                if (ch == '\r')
                {
                    hasLineBreak = true;
                    index++;
                    if (index < text.Length && text[index] == '\n')
                    {
                        index++;
                    }
                    break;
                }

                if (ch == '\n')
                {
                    hasLineBreak = true;
                    index++;
                    break;
                }

                break;
            }

            if (hasLineBreak)
            {
                removalEnd = index;
            }

            return new XamlTextEdit(removalStart, removalEnd - removalStart, string.Empty);
        }

        private static string? DetermineResourceKeyAttributeName(XamlAstNodeDescriptor descriptor)
        {
            foreach (var attribute in descriptor.Attributes)
            {
                if (string.Equals(attribute.LocalName, "Key", StringComparison.Ordinal))
                {
                    return attribute.FullName;
                }
            }

            return null;
        }

        private static string BuildAttributeInsertionText(string text, int insertionIndex, string name, string value)
        {
            var builder = new StringBuilder();
            if (RequiresLeadingSpace(text, insertionIndex))
            {
                builder.Append(' ');
            }

            builder.Append(name);
            builder.Append("=\"");
            builder.Append(value);
            builder.Append('"');
            return builder.ToString();
        }

        private static string BuildElementInsertionText(ChangePayload payload)
        {
            var serialized = payload.Serialized ?? string.Empty;
            if (string.IsNullOrEmpty(payload.SurroundingWhitespace))
            {
                return serialized;
            }

            var whitespace = payload.SurroundingWhitespace!;
            if (serialized.Length == 0)
            {
                return whitespace;
            }

            return string.Concat(whitespace, serialized);
        }

        private static string BuildElementReplacementText(ChangePayload payload)
        {
            var serialized = payload.Serialized ?? string.Empty;
            if (string.IsNullOrEmpty(payload.SurroundingWhitespace))
            {
                return serialized;
            }

            return string.Concat(payload.SurroundingWhitespace, serialized);
        }

        private static HashSet<(int Start, int Length)> CreateOccupiedSpanSet(List<XamlTextEdit> edits)
        {
            var set = new HashSet<(int, int)>();
            for (var index = 0; index < edits.Count; index++)
            {
                var edit = edits[index];
                set.Add((edit.Start, edit.Length));
            }

            return set;
        }

        private static bool TryProcessCascadeTarget(
            XamlAstDocument document,
            IXamlAstIndex index,
            string cascadeDescriptorId,
            string oldKey,
            string newKey,
            string? operationId,
            HashSet<(int Start, int Length)> occupiedSpans,
            List<XamlTextEdit> edits,
            out ChangeDispatchResult failure)
        {
            failure = default;

            if (!index.TryGetDescriptor(new XamlAstNodeId(cascadeDescriptorId), out var descriptor))
            {
                failure = ChangeDispatchResult.MutationFailure(operationId, $"Cascade target '{cascadeDescriptorId}' not found in XAML document.");
                return false;
            }

            if (!XamlGuardUtilities.TryLocateNode(document, descriptor, out var node) || node is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operationId, $"Unable to locate cascade target '{cascadeDescriptorId}' in syntax tree.");
                return false;
            }

            if (!TryCollectCascadeEdits(document.Text, node, oldKey, newKey, occupiedSpans, edits))
            {
                failure = ChangeDispatchResult.GuardFailure(operationId, "Cascade target mismatch.");
                return false;
            }

            return true;
        }

        private static bool TryCollectCascadeEdits(
            string text,
            SyntaxNode node,
            string oldKey,
            string newKey,
            HashSet<(int Start, int Length)> occupiedSpans,
            List<XamlTextEdit> edits)
        {
            var updated = false;

            if (node is XmlElementSyntax element && element.StartTag is { } startTag)
            {
                updated |= TryCollectAttributeCascadeEdits(startTag.AttributesNode, text, oldKey, newKey, occupiedSpans, edits);
            }
            else if (node is XmlEmptyElementSyntax emptyElement)
            {
                updated |= TryCollectAttributeCascadeEdits(emptyElement.AttributesNode, text, oldKey, newKey, occupiedSpans, edits);
            }

            return updated;
        }

        private static bool TryCollectAttributeCascadeEdits(
            SyntaxList<XmlAttributeSyntax> attributes,
            string text,
            string oldKey,
            string newKey,
            HashSet<(int Start, int Length)> occupiedSpans,
            List<XamlTextEdit> edits)
        {
            var updated = false;

            for (var index = 0; index < attributes.Count; index++)
            {
                if (attributes[index] is not XmlAttributeSyntax attribute ||
                    attribute.ValueNode is not { } valueNode)
                {
                    continue;
                }

                var span = valueNode.Span;
                var original = text.Substring(span.Start, span.Length);
                if (!ContainsOrdinal(original, oldKey))
                {
                    continue;
                }

                var replacement = ReplaceOrdinal(original, oldKey, newKey);
                if (string.Equals(original, replacement, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!occupiedSpans.Add((span.Start, span.Length)))
                {
                    continue;
                }

                edits.Add(new XamlTextEdit(span.Start, span.Length, replacement));
                updated = true;
            }

            return updated;
        }

        private static bool ContainsOrdinal(string text, string value) =>
            !string.IsNullOrEmpty(value) &&
            text.IndexOf(value, StringComparison.Ordinal) >= 0;

        private static string ReplaceOrdinal(string text, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(oldValue))
            {
                return text;
            }

            var index = text.IndexOf(oldValue, StringComparison.Ordinal);
            if (index < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length + Math.Max(0, newValue.Length - oldValue.Length));
            var current = 0;

            while (index >= 0)
            {
                builder.Append(text, current, index - current);
                builder.Append(newValue);
                current = index + oldValue.Length;
                index = text.IndexOf(oldValue, current, StringComparison.Ordinal);
            }

            builder.Append(text, current, text.Length - current);
            return builder.ToString();
        }

        private static string ExtractAttributeValue(string text, TextSpan span)
        {
            if (span.Length <= 0)
            {
                return string.Empty;
            }

            var raw = text.Substring(span.Start, span.Length);
            return TrimQuotes(raw);
        }

        private static string TrimQuotes(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2)
            {
                return value;
            }

            var first = value[0];
            var last = value[value.Length - 1];

            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private static int DetermineChildInsertionPosition(XmlElementSyntax element, int insertionIndex)
        {
            var content = element.Content;
            if (insertionIndex < 0)
            {
                insertionIndex = 0;
            }

            if (insertionIndex < content.Count)
            {
                return content[insertionIndex].Span.Start;
            }

            if (element.EndTag is not null)
            {
                return element.EndTag.Span.Start;
            }

            return element.StartTag.GreaterThanToken.Span.End;
        }

        private static int DetermineAttributeInsertionIndex(object ownerNode) =>
            ownerNode switch
            {
                XmlElementSyntax element => element.StartTag.GreaterThanToken.Span.Start,
                XmlEmptyElementSyntax empty => empty.SlashGreaterThanToken.Span.Start,
                _ => -1
            };

        private static int MovePastTrailingWhitespace(string text, int index)
        {
            while (index < text.Length)
            {
                var ch = text[index];
                if (ch == ' ' || ch == '\t')
                {
                    index++;
                    continue;
                }

                if (ch == '\r')
                {
                    index++;
                    if (index < text.Length && text[index] == '\n')
                    {
                        index++;
                    }
                    break;
                }

                if (ch == '\n')
                {
                    index++;
                    break;
                }

                break;
            }

            return index;
        }

        private static string EscapeContentText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return SecurityElement.Escape(value) ?? string.Empty;
        }

        private static string DetermineLineEnding(string text)
        {
            var index = text.IndexOf('\n');
            if (index < 0)
            {
                return Environment.NewLine;
            }

            if (index > 0 && text[index - 1] == '\r')
            {
                return "\r\n";
            }

            return "\n";
        }

        private static XmlElementSyntax? GetOwningElement(SyntaxNode? node)
        {
            while (node is not null)
            {
                if (node is XmlElementSyntax element)
                {
                    return element;
                }

                node = node.Parent;
            }

            return null;
        }

        private static string GetIndentation(string text, int position)
        {
            var lineStart = position;

            while (lineStart > 0)
            {
                var previous = text[lineStart - 1];
                if (previous == '\n')
                {
                    break;
                }

                if (previous == '\r')
                {
                    lineStart--;
                    if (lineStart > 0 && text[lineStart - 1] == '\n')
                    {
                        lineStart--;
                    }

                    break;
                }

                lineStart--;
            }

            var indentEnd = lineStart;
            while (indentEnd < position && (text[indentEnd] == ' ' || text[indentEnd] == '\t'))
            {
                indentEnd++;
            }

            return indentEnd > lineStart ? text.Substring(lineStart, indentEnd - lineStart) : string.Empty;
        }

        private static bool RequiresLeadingSpace(string text, int insertionIndex)
        {
            if (insertionIndex == 0)
            {
                return true;
            }

            var previous = text[insertionIndex - 1];
            if (char.IsWhiteSpace(previous))
            {
                return false;
            }

            return previous != '<';
        }

        private static string BuildQualifiedName(XmlNameSyntax nameNode)
        {
            var local = nameNode.LocalName ?? string.Empty;
            var prefix = nameNode.Prefix;
            return string.IsNullOrEmpty(prefix) ? local : string.Concat(prefix, ':', local);
        }

        private static char DetermineQuoteCharacter(string originalValue)
        {
            if (string.IsNullOrEmpty(originalValue))
            {
                return '"';
            }

            var first = originalValue[0];
            return first is '"' or '\'' ? first : '"';
        }
    }
}
