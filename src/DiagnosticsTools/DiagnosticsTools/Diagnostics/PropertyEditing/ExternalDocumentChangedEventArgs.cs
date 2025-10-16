using System;
using Avalonia.Diagnostics.Xaml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class ExternalDocumentChangedEventArgs : EventArgs
    {
        public ExternalDocumentChangedEventArgs(
            string? path,
            XamlDocumentChangeKind changeKind,
            MutationProvenance provenance)
        {
            Path = path;
            ChangeKind = changeKind;
            Provenance = provenance;
        }

        public string? Path { get; }

        public XamlDocumentChangeKind ChangeKind { get; }

        public MutationProvenance Provenance { get; }
    }
}
