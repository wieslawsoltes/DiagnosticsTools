using System;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.Xaml
{
    internal interface IXamlAstProvider : IDisposable
    {
        event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged;

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

    internal enum XamlDocumentChangeKind
    {
        Updated,
        Invalidated,
        Removed,
        Error
    }
}
