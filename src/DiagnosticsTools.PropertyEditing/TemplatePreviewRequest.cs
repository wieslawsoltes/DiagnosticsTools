using System;
using System.Collections.Generic;
using Avalonia.Diagnostics.Xaml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    /// <summary>
    /// Represents a request to preview a template-bound property, including the resolved source snapshot.
    /// </summary>
    public sealed class TemplatePreviewRequest
    {
        public TemplatePreviewRequest(
            XamlTemplateBindingDescriptor binding,
            string? documentPath,
            Uri? sourceUri,
            string? snapshotText,
            LinePositionSpan? lineSpan,
            XamlDocumentVersion? version,
            XamlAstDocument? document,
            IReadOnlyList<XamlAstNodeDescriptor>? documentNodes,
            bool isReadOnly,
            string? readOnlyMessage = null,
            string? errorMessage = null,
            string? providerDisplayName = null,
            string? ownerDisplayName = null)
        {
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            DocumentPath = documentPath;
            SourceUri = sourceUri;
            SnapshotText = snapshotText;
            LineSpan = lineSpan;
            Version = version;
            Document = document;
            DocumentNodes = documentNodes;
            IsReadOnly = isReadOnly;
            ReadOnlyMessage = readOnlyMessage;
            ErrorMessage = errorMessage;
            ProviderDisplayName = providerDisplayName;
            OwnerDisplayName = ownerDisplayName;
        }

        public XamlTemplateBindingDescriptor Binding { get; }

        public string? DocumentPath { get; }

        public Uri? SourceUri { get; }

        public string? SnapshotText { get; }

        public LinePositionSpan? LineSpan { get; }

        public XamlDocumentVersion? Version { get; }

        public XamlAstDocument? Document { get; }

        public IReadOnlyList<XamlAstNodeDescriptor>? DocumentNodes { get; }

        public bool IsReadOnly { get; }

        public string? ReadOnlyMessage { get; }

        public string? ErrorMessage { get; }

        public string? ProviderDisplayName { get; }

        public string? OwnerDisplayName { get; }

        public XamlAstNodeDescriptor? SourceDescriptor =>
            Binding.InlineTemplate ??
            Binding.ResourceDescriptor ??
            Binding.PropertyElement ??
            Binding.Owner;
    }
}
