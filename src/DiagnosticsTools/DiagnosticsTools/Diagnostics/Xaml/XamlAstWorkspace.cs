using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.Xaml
{
    public sealed class XamlAstWorkspace : IDisposable
    {
        private readonly IXamlAstProvider _provider;
        private readonly Dictionary<string, IndexCacheEntry> _indexCache;
        private readonly object _indexCacheGate = new();
        private event EventHandler<XamlDocumentChangedEventArgs>? _documentChanged;
        private bool _disposed;
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public XamlAstWorkspace()
            : this(new XmlParserXamlAstProvider())
        {
        }

        internal XamlAstWorkspace(IXamlAstProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _indexCache = new Dictionary<string, IndexCacheEntry>(PathComparer);
            _provider.DocumentChanged += HandleProviderDocumentChanged;
        }

        internal event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged
        {
            add
            {
                EnsureNotDisposed();
                _documentChanged += value;
            }
            remove
            {
                if (_disposed)
                {
                    return;
                }

                _documentChanged -= value;
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

            lock (_indexCacheGate)
            {
                if (_indexCache.TryGetValue(document.Path, out var cached) &&
                    cached.Version.Equals(document.Version))
                {
                    return cached.Index;
                }
            }

            var index = XamlAstIndex.Build(document);

            lock (_indexCacheGate)
            {
                _indexCache[document.Path] = new IndexCacheEntry(document.Version, index);
            }

            return index;
        }

        internal void Invalidate(string path)
        {
            EnsureNotDisposed();
            RemoveIndexFromCache(path);
            _provider.Invalidate(path);
        }

        internal void InvalidateAll()
        {
            EnsureNotDisposed();
            ClearIndexCache();
            _provider.InvalidateAll();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _provider.DocumentChanged -= HandleProviderDocumentChanged;
            ClearIndexCache();
            _provider.Dispose();
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(XamlAstWorkspace));
            }
        }

        private void HandleProviderDocumentChanged(object? sender, XamlDocumentChangedEventArgs e)
        {
            if (e is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(e.Path))
            {
                ClearIndexCache();
            }
            else
            {
                RemoveIndexFromCache(e.Path);
            }

            try
            {
                _documentChanged?.Invoke(this, e);
            }
            catch
            {
                // Observers should not throw.
            }
        }

        private void RemoveIndexFromCache(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            lock (_indexCacheGate)
            {
                _indexCache.Remove(path);
            }
        }

        private void ClearIndexCache()
        {
            lock (_indexCacheGate)
            {
                _indexCache.Clear();
            }
        }

        private readonly struct IndexCacheEntry
        {
            public IndexCacheEntry(XamlDocumentVersion version, IXamlAstIndex index)
            {
                Version = version;
                Index = index;
            }

            public XamlDocumentVersion Version { get; }

            public IXamlAstIndex Index { get; }
        }
    }
}
