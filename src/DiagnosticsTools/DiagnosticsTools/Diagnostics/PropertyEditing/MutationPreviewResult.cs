using System;
using System.Collections.Generic;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class MutationPreviewResult
    {
        private MutationPreviewResult(
            ChangeDispatchStatus status,
            string? message,
            string originalText,
            string previewText,
            IReadOnlyList<XamlTextEdit> edits,
            IReadOnlyList<MutationPreviewHighlight> originalHighlights,
            IReadOnlyList<MutationPreviewHighlight> previewHighlights,
            IReadOnlyList<ChangeOperation> operations)
        {
            Status = status;
            Message = message;
            OriginalText = originalText;
            PreviewText = previewText;
            Edits = edits;
            OriginalHighlights = originalHighlights;
            PreviewHighlights = previewHighlights;
            Operations = operations;
        }

        public ChangeDispatchStatus Status { get; }

        public string? Message { get; }

        public string OriginalText { get; }

        public string PreviewText { get; }

        public IReadOnlyList<XamlTextEdit> Edits { get; }

        public IReadOnlyList<MutationPreviewHighlight> OriginalHighlights { get; }

        public IReadOnlyList<MutationPreviewHighlight> PreviewHighlights { get; }

        public IReadOnlyList<ChangeOperation> Operations { get; }

        public static MutationPreviewResult Success(
            string originalText,
            string previewText,
            IReadOnlyList<XamlTextEdit> edits,
            IReadOnlyList<MutationPreviewHighlight> originalHighlights,
            IReadOnlyList<MutationPreviewHighlight> previewHighlights,
            IReadOnlyList<ChangeOperation> operations) =>
            new(ChangeDispatchStatus.Success, null, originalText, previewText, edits, originalHighlights, previewHighlights, operations);

        public static MutationPreviewResult Failure(
            ChangeDispatchStatus status,
            string? message,
            string originalText,
            IReadOnlyList<ChangeOperation> operations) =>
            new(status, message, originalText, originalText, Array.Empty<XamlTextEdit>(), Array.Empty<MutationPreviewHighlight>(), Array.Empty<MutationPreviewHighlight>(), operations);
    }

    public readonly record struct MutationPreviewHighlight(int Start, int Length);
}
