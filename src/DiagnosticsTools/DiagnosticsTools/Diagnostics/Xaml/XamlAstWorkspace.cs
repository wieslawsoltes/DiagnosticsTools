using System;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.Xaml
{
    public sealed class XamlAstWorkspace : IDisposable
    {
        private readonly IXamlAstProvider _provider;
        private bool _disposed;

        public XamlAstWorkspace()
            : this(new XmlParserXamlAstProvider())
        {
        }

        internal XamlAstWorkspace(IXamlAstProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        internal event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged
        {
            add
            {
                EnsureNotDisposed();
                _provider.DocumentChanged += value;
            }
            remove
            {
                if (_disposed)
                {
                    return;
                }

                _provider.DocumentChanged -= value;
            }
        }

        internal ValueTask<XamlAstDocument> GetDocumentAsync(string path, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            return _provider.GetDocumentAsync(path, cancellationToken);
        }

        internal async ValueTask<IXamlAstIndex> GetIndexAsync(string path, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            var document = await _provider.GetDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            return XamlAstIndex.Build(document);
        }

        internal void Invalidate(string path)
        {
            EnsureNotDisposed();
            _provider.Invalidate(path);
        }

        internal void InvalidateAll()
        {
            EnsureNotDisposed();
            _provider.InvalidateAll();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _provider.Dispose();
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(XamlAstWorkspace));
            }
        }
    }
}
