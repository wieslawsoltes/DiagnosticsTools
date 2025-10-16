using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.Xaml
{
    internal interface IXamlAstProvider : IDisposable
    {
        event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged;
        event EventHandler<XamlAstNodesChangedEventArgs>? NodesChanged;

        ValueTask<XamlAstDocument> GetDocumentAsync(string path, CancellationToken cancellationToken = default);

        void Invalidate(string path);

        void InvalidateAll();
    }

    internal sealed class XamlDocumentChangedEventArgs : EventArgs
    {
        public XamlDocumentChangedEventArgs(string path, XamlDocumentChangeKind kind, XamlAstDocument? document = null, Exception? error = null)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Kind = kind;
            Document = document;
            Error = error;
        }

        public string Path { get; }

        public XamlDocumentChangeKind Kind { get; }

        public XamlAstDocument? Document { get; }

        public Exception? Error { get; }
    }

    internal sealed class XamlDiagnosticsChangedEventArgs : EventArgs
    {
        public XamlDiagnosticsChangedEventArgs(string path, XamlDocumentVersion version, IReadOnlyList<XamlAstDiagnostic> diagnostics)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Version = version;
            Diagnostics = diagnostics ?? Array.Empty<XamlAstDiagnostic>();
        }

        public string Path { get; }

        public XamlDocumentVersion Version { get; }

        public IReadOnlyList<XamlAstDiagnostic> Diagnostics { get; }
    }

    internal sealed class XamlAstNodesChangedEventArgs : EventArgs
    {
        public XamlAstNodesChangedEventArgs(string path, XamlDocumentVersion version, IReadOnlyList<XamlAstNodeChange> changes)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Version = version;
            Changes = changes ?? throw new ArgumentNullException(nameof(changes));
        }

        public string Path { get; }

        public XamlDocumentVersion Version { get; }

        public IReadOnlyList<XamlAstNodeChange> Changes { get; }
    }

    internal sealed class XamlAstNodeChange
    {
        public XamlAstNodeChange(XamlAstNodeChangeKind kind, XamlAstNodeDescriptor? oldNode, XamlAstNodeDescriptor? newNode)
        {
            Kind = kind;
            OldNode = oldNode;
            NewNode = newNode;
        }

        public XamlAstNodeChangeKind Kind { get; }

        public XamlAstNodeDescriptor? OldNode { get; }

        public XamlAstNodeDescriptor? NewNode { get; }
    }

    internal enum XamlAstNodeChangeKind
    {
        Added,
        Removed,
        Updated
    }

    internal enum XamlDocumentChangeKind
    {
        Updated,
        Invalidated,
        Removed,
        Error
    }
}
