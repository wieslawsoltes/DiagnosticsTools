using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal static class MutableXamlMutationApplier
    {
        public static MutableMutationResult TryApply(
            XamlAstDocument document,
            IXamlAstIndex index,
            MutableXamlDocument mutableDocument,
            IReadOnlyList<ChangeOperation> operations)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (index is null)
            {
                throw new ArgumentNullException(nameof(index));
            }

            if (mutableDocument is null)
            {
                throw new ArgumentNullException(nameof(mutableDocument));
            }

            if (operations is null)
            {
                throw new ArgumentNullException(nameof(operations));
            }

            if (operations.Count == 0)
            {
                return MutableMutationResult.Applied(mutated: false, mutableDocument);
            }

            var guardDocument = document;
            var guardIndex = index;
            var currentDocument = document;
            var currentMutable = mutableDocument;
            var mutated = false;

            foreach (var operation in operations)
            {
                var outcome = operation.Type switch
                {
                    ChangeOperationTypes.SetAttribute => TryApplySetAttribute(guardDocument, guardIndex, currentMutable, operation),
                    ChangeOperationTypes.RemoveNode => TryApplyRemoveNode(guardDocument, guardIndex, currentMutable, operation),
                    ChangeOperationTypes.UpsertElement => TryApplyUpsertElement(guardDocument, guardIndex, currentDocument, currentMutable, operation),
                    ChangeOperationTypes.SetNamespace => TryApplySetNamespace(guardDocument, guardIndex, currentMutable, operation),
                    ChangeOperationTypes.SetContentText => TryApplySetContentText(guardDocument, guardIndex, currentMutable, operation),
                    ChangeOperationTypes.ReorderNode => TryApplyReorderNode(guardDocument, guardIndex, currentMutable, operation),
                    ChangeOperationTypes.RenameElement => TryApplyRenameElement(guardDocument, guardIndex, currentMutable, operation),
                    ChangeOperationTypes.RenameResource => TryApplyRenameResource(guardDocument, guardIndex, currentMutable, operation),
                    _ => OperationOutcome.Unsupported(),
                };

                switch (outcome.Status)
                {
                    case OperationStatus.Failed:
                        return outcome.Failure.HasValue
                            ? MutableMutationResult.Failed(outcome.Failure.Value)
                            : MutableMutationResult.Failed(ChangeDispatchResult.MutationFailure(null, "Mutable pipeline operation failed."));
                    case OperationStatus.Unsupported:
                        return MutableMutationResult.Unsupported();
                }

                mutated |= outcome.Mutated;

                if (outcome.DocumentOverride is not null)
                {
                    currentDocument = outcome.DocumentOverride;
                    currentMutable = outcome.MutableOverride ?? MutableXamlDocument.FromDocument(currentDocument);
                    continue;
                }

                if (outcome.RequiresRebuild)
                {
                    var updatedText = MutableXamlSerializer.Serialize(currentMutable);
                    var parsed = Parser.ParseText(updatedText);
                    currentDocument = new XamlAstDocument(
                        currentDocument.Path,
                        updatedText,
                        parsed,
                        currentDocument.Version,
                        currentDocument.Diagnostics,
                        currentDocument.Encoding,
                        currentDocument.HasByteOrderMark,
                        currentDocument.IsEncodingFallback);

                    currentMutable = MutableXamlDocument.FromDocument(currentDocument);
                }
            }

            return MutableMutationResult.Applied(mutated, currentMutable);
        }

        private static OperationOutcome TryApplySetAttribute(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            if (!TryGetDescriptor(guardIndex, operation, out var descriptor, out var failure))
            {
                return OperationOutcome.Failed(failure);
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for SetAttribute operation.");
                return OperationOutcome.Failed(failure);
            }

            var attributeName = payload.Name;
            if (string.IsNullOrWhiteSpace(attributeName))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "SetAttribute requires an attribute name.");
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateSpanHash(
                    operation.Guard?.SpanHash,
                    () => XamlGuardUtilities.ComputeAttributeHash(guardDocument, descriptor, attributeName!),
                    operation.Id,
                    "Attribute span hash mismatch.",
                    out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (attributeName!.Contains(".") &&
                !ValidateParentGuard(guardDocument, guardIndex, descriptor, operation, out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!mutableDocument.TryGetElement(descriptor, out var element) || element is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target element could not be resolved in mutable document.");
                return OperationOutcome.Failed(failure);
            }

            SplitQualifiedName(attributeName, out var localName, out var prefix);
            var existingAttribute = element.FindAttribute(localName, prefix);

            var valueKind = payload.ValueKind ?? string.Empty;
            var shouldUnset = string.Equals(valueKind, "Unset", StringComparison.OrdinalIgnoreCase) ||
                              payload.NewValue is null;

            if (shouldUnset)
            {
                if (existingAttribute is null)
                {
                    return OperationOutcome.NoChange();
                }

                if (!element.RemoveAttribute(existingAttribute))
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Failed to remove attribute from element.");
                    return OperationOutcome.Failed(failure);
                }

                var updatedDescriptor = mutableDocument.ToDescriptor(element);
                mutableDocument.UpdateDescriptorMapping(element, updatedDescriptor);
                return OperationOutcome.Applied(true);
            }

            var newValue = payload.NewValue;
            if (string.IsNullOrEmpty(newValue))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "SetAttribute requires a non-empty value.");
                return OperationOutcome.Failed(failure);
            }

            if (existingAttribute is not null)
            {
                if (string.Equals(existingAttribute.Value, newValue, StringComparison.Ordinal))
                {
                    return OperationOutcome.NoChange();
                }

                var replacementSyntax = CreateAttributeSyntax(
                    attributeName,
                    newValue,
                    existingAttribute.LeadingTrivia,
                    existingAttribute.TrailingTrivia);

                if (!element.TryReplaceAttribute(existingAttribute, replacementSyntax))
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Failed to update attribute value.");
                    return OperationOutcome.Failed(failure);
                }

                existingAttribute.Value = newValue;
            }
            else
            {
                var attributeSyntax = CreateAttributeSyntax(
                    attributeName,
                    newValue,
                    SyntaxTriviaListHelper.CreateLeadingSpace(),
                    SyntaxFactory.TriviaList());

                var created = element.AddAttribute(attributeSyntax);
                created.Value = newValue;
            }

            var descriptorUpdateFinal = mutableDocument.ToDescriptor(element);
            mutableDocument.UpdateDescriptorMapping(element, descriptorUpdateFinal);
            return OperationOutcome.Applied(true);
        }

        private static OperationOutcome TryApplyRemoveNode(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            if (!TryGetDescriptor(guardIndex, operation, out var descriptor, out var failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeNodeHash(guardDocument, descriptor),
                operation.Id,
                "Node span hash mismatch.",
                out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateParentGuard(guardDocument, guardIndex, descriptor, operation, out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!mutableDocument.TryGetElement(descriptor, out var element) || element is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target element could not be resolved in mutable document.");
                return OperationOutcome.Failed(failure);
            }

            var parent = element.Parent;
            if (parent is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Cannot remove the root element using mutable pipeline.");
                return OperationOutcome.Failed(failure);
            }

            if (!parent.RemoveChild(element))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Failed to remove target element.");
                return OperationOutcome.Failed(failure);
            }

            mutableDocument.RemoveDescriptorMapping(element);
            return OperationOutcome.Applied(true);
        }

        private static OperationOutcome TryApplyUpsertElement(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            XamlAstDocument currentDocument,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            if (!TryGetDescriptor(guardIndex, operation, out var descriptor, out var failure))
            {
                return OperationOutcome.Failed(failure);
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for UpsertElement operation.");
                return OperationOutcome.Failed(failure);
            }

            if (string.IsNullOrWhiteSpace(payload.Serialized))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "UpsertElement requires a serialized fragment.");
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateSpanHash(
                operation.Guard?.SpanHash,
                () => XamlGuardUtilities.ComputeNodeHash(guardDocument, descriptor),
                operation.Id,
                "Element span hash mismatch.",
                out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateParentGuard(guardDocument, guardIndex, descriptor, operation, out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (payload.InsertionIndex is int insertionIndex)
            {
                if (!mutableDocument.TryGetElement(descriptor, out var parentElement) || parentElement is null)
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate parent element for insertion.");
                    return OperationOutcome.Failed(failure);
                }

                if (parentElement.Syntax is XmlEmptyElementSyntax)
                {
                    return TryApplyWithTextRewrite(guardDocument, guardIndex, mutableDocument, operation);
                }

                var nodes = ParseContentNodes(payload.Serialized!, payload.SurroundingWhitespace);
                if (nodes.Count == 0)
                {
                    return OperationOutcome.NoChange();
                }

                parentElement.InsertRawContent(insertionIndex, nodes);
                return OperationOutcome.Applied(true);
            }

            if (!mutableDocument.TryGetElement(descriptor, out var targetElement) || targetElement is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate element for replacement.");
                return OperationOutcome.Failed(failure);
            }

            var replacementNodes = ParseContentNodes(payload.Serialized!, payload.SurroundingWhitespace);
            if (replacementNodes.Count == 0)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Replacement fragment did not yield any nodes.");
                return OperationOutcome.Failed(failure);
            }

            var parent = targetElement.Parent;
            if (parent is null)
            {
                if (replacementNodes.Count != 1 || replacementNodes[0] is not IXmlElementSyntax replacementRoot)
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Root replacement requires a single element payload.");
                    return OperationOutcome.Failed(failure);
                }

                if (currentDocument.Syntax.RootSyntax is not IXmlElementSyntax originalRoot)
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Document root is not an XML element.");
                    return OperationOutcome.Failed(failure);
                }

                var updatedSyntax = currentDocument.Syntax.ReplaceNode(originalRoot.AsNode, replacementRoot.AsNode);
                var updatedText = updatedSyntax.ToFullString();
                var updatedDocument = new XamlAstDocument(
                    currentDocument.Path,
                    updatedText,
                    updatedSyntax,
                    currentDocument.Version,
                    currentDocument.Diagnostics,
                    currentDocument.Encoding,
                    currentDocument.HasByteOrderMark,
                    currentDocument.IsEncodingFallback);

                var updatedMutable = MutableXamlDocument.FromDocument(updatedDocument);
                return OperationOutcome.ReplacedDocument(updatedDocument, updatedMutable);
            }

            var childIndex = parent.IndexOfChild(targetElement);
            if (childIndex < 0)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to compute replacement index for target element.");
                return OperationOutcome.Failed(failure);
            }

            mutableDocument.RemoveDescriptorMapping(targetElement);
            parent.RemoveChild(targetElement);
            parent.InsertRawContent(childIndex, replacementNodes);
            return OperationOutcome.Applied(true);
        }

        private static OperationOutcome TryApplySetNamespace(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            if (!TryGetDescriptor(guardIndex, operation, out var descriptor, out var failure))
            {
                return OperationOutcome.Failed(failure);
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for SetNamespace operation.");
                return OperationOutcome.Failed(failure);
            }

            if (string.IsNullOrWhiteSpace(payload.Name))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "SetNamespace requires a namespace attribute name.");
                return OperationOutcome.Failed(failure);
            }

            var attributeName = payload.Name!;
            if (!attributeName.StartsWith("xmlns", StringComparison.Ordinal))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Namespace attributes must begin with 'xmlns'.");
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateSpanHash(
                    operation.Guard?.SpanHash,
                    () => XamlGuardUtilities.ComputeAttributeHash(guardDocument, descriptor, attributeName),
                    operation.Id,
                    "Namespace span hash mismatch.",
                    out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!mutableDocument.TryGetElement(descriptor, out var element) || element is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to locate element for SetNamespace operation.");
                return OperationOutcome.Failed(failure);
            }

            SplitNamespaceAttributeName(attributeName, out var localName, out var prefix);
            var mutableAttribute = element.FindAttribute(localName, prefix);
            var newValue = payload.NewValue;

            if (string.IsNullOrWhiteSpace(newValue))
            {
                if (mutableAttribute is null)
                {
                    return OperationOutcome.NoChange();
                }

                if (!element.RemoveAttribute(mutableAttribute))
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Failed to remove namespace attribute.");
                    return OperationOutcome.Failed(failure);
                }

                var updatedDescriptor = mutableDocument.ToDescriptor(element);
                mutableDocument.UpdateDescriptorMapping(element, updatedDescriptor);
                return OperationOutcome.Applied(true);
            }

            var qualifiedName = attributeName;
            var normalizedValue = NormalizeNamespaceValue(newValue ?? string.Empty);
            if (mutableAttribute is not null)
            {
                var replacementSyntax = CreateAttributeSyntax(qualifiedName, normalizedValue, mutableAttribute.LeadingTrivia, mutableAttribute.TrailingTrivia);
                if (!element.TryReplaceAttribute(mutableAttribute, replacementSyntax))
                {
                    failure = ChangeDispatchResult.MutationFailure(operation.Id, "Failed to update namespace attribute value.");
                    return OperationOutcome.Failed(failure);
                }

                mutableAttribute.Value = normalizedValue;
            }
            else
            {
                var attributeSyntax = CreateAttributeSyntax(
                    qualifiedName,
                    normalizedValue,
                    SyntaxTriviaListHelper.CreateLeadingSpace(),
                    SyntaxFactory.TriviaList());

                var createdAttribute = element.AddAttribute(attributeSyntax);
                createdAttribute.Value = normalizedValue;
            }

            var descriptorUpdate = mutableDocument.ToDescriptor(element);
            mutableDocument.UpdateDescriptorMapping(element, descriptorUpdate);
            return OperationOutcome.Applied(true);
        }

        private static string NormalizeNamespaceValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Replace("assembly:", "assembly=");
        }

        private static OperationOutcome TryApplyRenameElement(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            if (!TryGetDescriptor(guardIndex, operation, out var descriptor, out var failure))
            {
                return OperationOutcome.Failed(failure);
            }

            var payload = operation.Payload;
            if (payload is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Missing payload for RenameElement operation.");
                return OperationOutcome.Failed(failure);
            }

            var newName = payload.NewValue;
            if (string.IsNullOrWhiteSpace(newName))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "RenameElement requires a non-empty newValue.");
                return OperationOutcome.Failed(failure);
            }

            if (string.Equals(descriptor.QualifiedName, newName, StringComparison.Ordinal))
            {
                return OperationOutcome.NoChange();
            }

            if (!ValidateSpanHash(
                    operation.Guard?.SpanHash,
                    () => XamlGuardUtilities.ComputeNodeHash(guardDocument, descriptor),
                    operation.Id,
                    "Element span hash mismatch.",
                    out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!mutableDocument.TryGetElement(descriptor, out var element) || element is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Target element could not be resolved in mutable document.");
                return OperationOutcome.Failed(failure);
            }

            if (!TryCreateNameSyntax(newName, out var nameSyntax, out var localName, out var prefix))
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Invalid element name provided for RenameElement operation.");
                return OperationOutcome.Failed(failure);
            }

            try
            {
                element.UpdateName(nameSyntax, localName, prefix);
            }
            catch (Exception ex)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, ex.Message);
                return OperationOutcome.Failed(failure);
            }

            var descriptorUpdate = mutableDocument.ToDescriptor(element);
            mutableDocument.UpdateDescriptorMapping(element, descriptorUpdate);
            return OperationOutcome.Applied(false);
        }

        private static OperationOutcome TryApplySetContentText(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            if (!TryGetDescriptor(guardIndex, operation, out var descriptor, out var failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateSpanHash(
                    operation.Guard?.SpanHash,
                    () => XamlGuardUtilities.ComputeNodeHash(guardDocument, descriptor),
                    operation.Id,
                    "Node span hash mismatch.",
                    out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            return TryApplyWithTextRewrite(guardDocument, guardIndex, mutableDocument, operation);
        }

        private static OperationOutcome TryApplyReorderNode(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            if (!TryGetDescriptor(guardIndex, operation, out var descriptor, out var failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!mutableDocument.TryGetElement(descriptor, out var element) || element is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to resolve element for ReorderNode operation.");
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateSpanHash(
                    operation.Guard?.SpanHash,
                    () => XamlGuardUtilities.ComputeNodeHash(guardDocument, descriptor),
                    operation.Id,
                    "Node span hash mismatch.",
                    out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            if (!ValidateParentGuard(guardDocument, guardIndex, descriptor, operation, out failure))
            {
                return OperationOutcome.Failed(failure);
            }

            var payload = operation.Payload;
            if (payload?.NewIndex is not int requestedIndex)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "ReorderNode requires a target index.");
                return OperationOutcome.Failed(failure);
            }

            var parent = element.Parent;
            if (parent is null)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Cannot reorder the root element.");
                return OperationOutcome.Failed(failure);
            }

            var currentIndex = parent.IndexOfChild(element);
            if (currentIndex < 0)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to determine current index for target element.");
                return OperationOutcome.Failed(failure);
            }

            var boundedIndex = ClampIndex(requestedIndex, parent.Children.Count);
            if (boundedIndex == currentIndex)
            {
                return OperationOutcome.NoChange();
            }

            if (parent.Syntax is not XmlElementSyntax parentSyntax)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Parent element cannot accept reordered content.");
                return OperationOutcome.Failed(failure);
            }

            if (element.Syntax.AsNode is not XmlNodeSyntax elementSyntaxNode)
            {
                failure = ChangeDispatchResult.MutationFailure(operation.Id, "Unable to resolve syntax node for reorder operation.");
                return OperationOutcome.Failed(failure);
            }

            var adjustedIndex = boundedIndex > currentIndex ? boundedIndex - 1 : boundedIndex;
            if (adjustedIndex < 0)
            {
                adjustedIndex = 0;
            }

            parent.RemoveChild(element);
            parent.InsertRawContent(adjustedIndex, new[] { elementSyntaxNode });

            var updatedDescriptor = mutableDocument.ToDescriptor(parent);
            mutableDocument.UpdateDescriptorMapping(parent, updatedDescriptor);
            return OperationOutcome.Applied(true);
        }

        private static OperationOutcome TryApplyWithTextRewrite(
            XamlAstDocument document,
            IXamlAstIndex index,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            var currentText = MutableXamlSerializer.Serialize(mutableDocument);
            var currentSyntax = Parser.ParseText(currentText);
            var currentDocument = new XamlAstDocument(
                document.Path,
                currentText,
                currentSyntax,
                document.Version,
                document.Diagnostics,
                document.Encoding,
                document.HasByteOrderMark,
                document.IsEncodingFallback);

            var currentIndex = XamlAstIndex.Build(currentDocument);

            var edits = new List<XamlTextEdit>();
            if (!XamlMutationEditBuilder.TryBuildEdits(currentDocument, currentIndex, operation, edits, out var failure))
            {
                Console.WriteLine($"Mutable fallback failed: {failure.Status} {failure.Message}");
                return OperationOutcome.Failed(failure);
            }

            if (edits.Count == 0)
            {
                return OperationOutcome.NoChange();
            }

            var updatedText = ApplyTextEdits(currentText, edits);
            var updatedSyntax = Parser.ParseText(updatedText);
            var updatedDocument = new XamlAstDocument(
                document.Path,
                updatedText,
                updatedSyntax,
                document.Version,
                document.Diagnostics,
                document.Encoding,
                document.HasByteOrderMark,
                document.IsEncodingFallback);

            var updatedMutable = MutableXamlDocument.FromDocument(updatedDocument);
            return OperationOutcome.ReplacedDocument(updatedDocument, updatedMutable);
        }

        private static string ApplyTextEdits(string text, IReadOnlyList<XamlTextEdit> edits)
        {
            if (edits.Count == 0)
            {
                return text;
            }

            var sorted = new List<XamlTextEdit>(edits);
            sorted.Sort((left, right) => left.Start.CompareTo(right.Start));

            var builder = new StringBuilder(text.Length);
            var current = 0;

            foreach (var edit in sorted)
            {
                if (edit.Start > current)
                {
                    builder.Append(text, current, edit.Start - current);
                }

                builder.Append(edit.Replacement);
                current = edit.Start + edit.Length;
            }

            if (current < text.Length)
            {
                builder.Append(text, current, text.Length - current);
            }

            return builder.ToString();
        }

        private static OperationOutcome TryApplyRenameResource(
            XamlAstDocument guardDocument,
            IXamlAstIndex guardIndex,
            MutableXamlDocument mutableDocument,
            ChangeOperation operation)
        {
            return TryApplyWithTextRewrite(guardDocument, guardIndex, mutableDocument, operation);
        }

        private static IReadOnlyList<XmlNodeSyntax> ParseContentNodes(string serialized, string? surroundingWhitespace)
        {
            var prefix = surroundingWhitespace ?? string.Empty;
            var wrapped = $"<Wrapper>{prefix}{serialized}</Wrapper>";
            var parsed = Parser.ParseText(wrapped);

            if (parsed.RootSyntax is not XmlElementSyntax element)
            {
                return Array.Empty<XmlNodeSyntax>();
            }

            var builder = new List<XmlNodeSyntax>(element.Content.Count);
            foreach (var node in element.Content)
            {
                if (node is XmlNodeSyntax nodeSyntax)
                {
                    builder.Add(nodeSyntax);
                }
            }

            return builder;
        }

        private static bool TryCreateNameSyntax(string qualifiedName, out XmlNameSyntax nameSyntax, out string localName, out string? prefix)
        {
            nameSyntax = null!;
            localName = string.Empty;
            prefix = null;

            try
            {
                string snippet;
                var colonIndex = qualifiedName.IndexOf(':');
                if (colonIndex >= 0)
                {
                    var prefixPart = qualifiedName.Substring(0, colonIndex);
                    snippet = $"<{qualifiedName} xmlns:{prefixPart}='urn:temp'></{qualifiedName}>";
                }
                else
                {
                    snippet = $"<{qualifiedName}></{qualifiedName}>";
                }

                var parsed = Parser.ParseText(snippet);
                if (parsed.RootSyntax is XmlElementSyntax element)
                {
                    var nameNode = element.StartTag.NameNode.WithTrailingTrivia(SyntaxFactory.TriviaList());
                    localName = nameNode.LocalName ?? string.Empty;
                    prefix = nameNode.Prefix;
                    nameSyntax = nameNode;
                    return true;
                }
            }
            catch
            {
                // Ignore and fall through to failure.
            }

            return false;
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

        private static void SplitQualifiedName(string qualifiedName, out string localName, out string? prefix)
        {
            var colonIndex = qualifiedName.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < qualifiedName.Length - 1)
            {
                prefix = qualifiedName.Substring(0, colonIndex);
                localName = qualifiedName.Substring(colonIndex + 1);
            }
            else
            {
                prefix = null;
                localName = qualifiedName;
            }
        }

        private static bool TryUpdateCascadeAttributes(MutableXamlElement element, string oldKey, string newKey)
        {
            var updated = false;

            foreach (var attribute in element.Attributes)
            {
                if (!ContainsOrdinal(attribute.Value, oldKey))
                {
                    continue;
                }

                var replacement = ReplaceOrdinal(attribute.Value, oldKey, newKey);
                if (string.Equals(replacement, attribute.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                var syntax = CreateAttributeSyntax(attribute.FullName, replacement, attribute.LeadingTrivia, attribute.TrailingTrivia);
                if (!element.TryReplaceAttribute(attribute, syntax))
                {
                    continue;
                }

                attribute.Value = replacement;
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

        private static XmlAttributeSyntax CreateAttributeSyntax(
            string name,
            string value,
            SyntaxTriviaList leadingTrivia,
            SyntaxTriviaList trailingTrivia)
        {
            var escapedValue = EscapeAttributeValue(value);
            var fragment = $"<Element {name}=\"{escapedValue}\" />";
            var parsed = Parser.ParseText(fragment);

            if (parsed.RootSyntax is not XmlEmptyElementSyntax element ||
                element.AttributesNode.Count == 0 ||
                element.AttributesNode[0] is not XmlAttributeSyntax attribute)
            {
                throw new InvalidOperationException("Failed to parse attribute fragment.");
            }

            return attribute
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(trailingTrivia);
        }

        private static string EscapeAttributeValue(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private static void SplitNamespaceAttributeName(string attributeName, out string localName, out string? prefix)
        {
            var colonIndex = attributeName.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < attributeName.Length - 1)
            {
                prefix = attributeName.Substring(0, colonIndex);
                localName = attributeName.Substring(colonIndex + 1);
            }
            else
            {
                prefix = null;
                localName = attributeName;
            }
        }

        private static int ClampIndex(int requestedIndex, int totalCount)
        {
            if (totalCount <= 0)
            {
                return 0;
            }

            var upperBound = totalCount - 1;

            var clamped = Math.Max(0, Math.Min(requestedIndex, upperBound));
            return clamped;
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

            if (!ValidateSpanHash(
                    expectedParentHash,
                    () => XamlGuardUtilities.ComputeNodeHash(document, parent),
                    operation.Id,
                    "Parent span hash mismatch.",
                    out failure))
            {
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

        private static bool PathMatches(IReadOnlyList<int> candidate, IReadOnlyList<int> path, int depth)
        {
            for (var i = 0; i < depth; i++)
            {
                if (candidate[i] != path[i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal readonly record struct MutableMutationResult(
        MutableMutationStatus Status,
        bool Mutated,
        ChangeDispatchResult? Failure,
        MutableXamlDocument? Document)
    {
        public static MutableMutationResult Applied(bool mutated, MutableXamlDocument? document = null) =>
            new(MutableMutationStatus.Applied, mutated, null, document);

        public static MutableMutationResult Unsupported() =>
            new(MutableMutationStatus.Unsupported, false, null, null);

        public static MutableMutationResult Failed(ChangeDispatchResult failure) =>
            new(MutableMutationStatus.Failed, false, failure, null);
    }

    internal enum MutableMutationStatus
    {
        Applied,
        Unsupported,
        Failed
    }

    internal static class SyntaxTriviaListHelper
    {
        public static SyntaxTriviaList CreateLeadingSpace() =>
            SyntaxFactory.TriviaList(SyntaxFactory.WhitespaceTrivia(" "));
    }

    internal readonly record struct OperationOutcome(
        OperationStatus Status,
        bool Mutated,
        bool RequiresRebuild,
        ChangeDispatchResult? Failure,
        XamlAstDocument? DocumentOverride,
        MutableXamlDocument? MutableOverride)
    {
        public static OperationOutcome Applied(bool requiresRebuild) =>
            new(OperationStatus.Applied, true, requiresRebuild, null, null, null);

        public static OperationOutcome NoChange() =>
            new(OperationStatus.Applied, false, false, null, null, null);

        public static OperationOutcome Failed(ChangeDispatchResult failure) =>
            new(OperationStatus.Failed, false, false, failure, null, null);

        public static OperationOutcome Unsupported() =>
            new(OperationStatus.Unsupported, false, false, null, null, null);

        public static OperationOutcome ReplacedDocument(XamlAstDocument document, MutableXamlDocument mutable) =>
            new(OperationStatus.Applied, true, false, null, document, mutable);
    }

    internal enum OperationStatus
    {
        Applied,
        Unsupported,
        Failed
    }
}
