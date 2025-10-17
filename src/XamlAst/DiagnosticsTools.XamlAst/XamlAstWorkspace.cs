using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Utilities;

namespace Avalonia.Diagnostics.Xaml
{
    /// <summary>
    /// Provides an in-memory workspace for parsing and indexing XAML documents.
    /// </summary>
    public sealed class XamlAstWorkspace : IDisposable
    {
        private readonly IXamlAstProvider _provider;
        private readonly IXamlAstInstrumentation _instrumentation;
        private readonly Dictionary<string, IndexCacheEntry> _indexCache;
        private readonly Dictionary<string, DiagnosticsCacheEntry> _diagnosticsCache;
        private readonly object _indexCacheGate = new();
        private readonly object _diagnosticsGate = new();
        private event EventHandler<XamlDocumentChangedEventArgs>? _documentChanged;
        private event EventHandler<XamlAstNodesChangedEventArgs>? _nodesChanged;
        private event EventHandler<XamlDiagnosticsChangedEventArgs>? _diagnosticsChanged;
        private bool _disposed;
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        /// <summary>
        /// Initializes a new instance of the <see cref="XamlAstWorkspace"/> class with the default provider.
        /// </summary>
        public XamlAstWorkspace(IXamlAstInstrumentation? instrumentation = null)
            : this(
                new XmlParserXamlAstProvider(instrumentation),
                instrumentation)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="XamlAstWorkspace"/> class using the supplied provider.
        /// </summary>
        public XamlAstWorkspace(IXamlAstProvider provider, IXamlAstInstrumentation? instrumentation = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _instrumentation = instrumentation ?? NullXamlAstInstrumentation.Instance;
            _indexCache = new Dictionary<string, IndexCacheEntry>(PathComparer);
            _diagnosticsCache = new Dictionary<string, DiagnosticsCacheEntry>(PathComparer);
            _provider.DocumentChanged += HandleProviderDocumentChanged;
            _provider.NodesChanged += HandleProviderNodesChanged;
        }

        public event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged
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

        public event EventHandler<XamlAstNodesChangedEventArgs>? NodesChanged
        {
            add
            {
                EnsureNotDisposed();
                _nodesChanged += value;
            }
            remove
            {
                if (_disposed)
                {
                    return;
                }

                _nodesChanged -= value;
            }
        }

        public event EventHandler<XamlDiagnosticsChangedEventArgs>? DiagnosticsChanged
        {
            add
            {
                EnsureNotDisposed();
                _diagnosticsChanged += value;
            }
            remove
            {
                if (_disposed)
                {
                    return;
                }

                _diagnosticsChanged -= value;
            }
        }

        public ValueTask<XamlAstDocument> GetDocumentAsync(string path, CancellationToken cancellationToken = default)
        {
            EnsureNotDisposed();
            return _provider.GetDocumentAsync(path, cancellationToken);
        }

        public bool TryGetDiagnostics(string path, out IReadOnlyList<XamlAstDiagnostic> diagnostics)
        {
            EnsureNotDisposed();
            diagnostics = Array.Empty<XamlAstDiagnostic>();

            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            lock (_diagnosticsGate)
            {
                if (_diagnosticsCache.TryGetValue(path, out var entry))
                {
                    diagnostics = entry.Diagnostics;
                    return true;
                }
            }

            return false;
        }

        public async ValueTask<IXamlAstIndex> GetIndexAsync(string path, CancellationToken cancellationToken = default)
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

            var stopwatch = Stopwatch.StartNew();
            var index = XamlAstIndex.Build(document);
            stopwatch.Stop();
            _instrumentation.RecordAstIndexBuild(stopwatch.Elapsed, "workspace", cacheHit: false);

            lock (_indexCacheGate)
            {
                _indexCache[document.Path] = new IndexCacheEntry(document.Version, index);
            }

            return index;
        }

        public void Invalidate(string path)
        {
            EnsureNotDisposed();
            RemoveIndexFromCache(path);
            RemoveDiagnosticsFromCache(path);
            _provider.Invalidate(path);
        }

        public void InvalidateAll()
        {
            EnsureNotDisposed();
            ClearIndexCache();
            ClearDiagnosticsCache();
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
            _provider.NodesChanged -= HandleProviderNodesChanged;
            ClearIndexCache();
            ClearDiagnosticsCache();
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
                ClearDiagnosticsCache();
            }
            else
            {
                RemoveIndexFromCache(e.Path);

                if (e.Kind == XamlDocumentChangeKind.Invalidated)
                {
                    RemoveDiagnosticsFromCache(e.Path);
                    RaiseDiagnosticsChanged(e.Path, default, Array.Empty<XamlAstDiagnostic>());
                }
                else if (e.Kind == XamlDocumentChangeKind.Removed)
                {
                    RemoveDiagnosticsFromCache(e.Path);
                }
            }

            if (e.Document is { } document)
            {
                RaiseDiagnosticsChanged(document.Path, document.Version, document.Diagnostics);
            }
            else if (e.Kind == XamlDocumentChangeKind.Removed && !string.IsNullOrWhiteSpace(e.Path))
            {
                RaiseDiagnosticsChanged(e.Path, default, Array.Empty<XamlAstDiagnostic>());
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

        private void HandleProviderNodesChanged(object? sender, XamlAstNodesChangedEventArgs e)
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
                _nodesChanged?.Invoke(this, e);
            }
            catch
            {
                // Observers should not throw.
            }
        }

        private void RemoveDiagnosticsFromCache(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            lock (_diagnosticsGate)
            {
                _diagnosticsCache.Remove(path!);
            }
        }

        private void ClearDiagnosticsCache()
        {
            lock (_diagnosticsGate)
            {
                _diagnosticsCache.Clear();
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
                _indexCache.Remove(path!);
            }
        }

        private void ClearIndexCache()
        {
            lock (_indexCacheGate)
            {
                _indexCache.Clear();
            }
        }

        private void RaiseDiagnosticsChanged(string path, XamlDocumentVersion version, IReadOnlyList<XamlAstDiagnostic> diagnostics)
        {
            diagnostics ??= Array.Empty<XamlAstDiagnostic>();

            if (string.IsNullOrWhiteSpace(path))
            {
                ClearDiagnosticsCache();
            }
            else
            {
                lock (_diagnosticsGate)
                {
                    if (diagnostics.Count == 0 && version.Equals(default))
                    {
                        _diagnosticsCache.Remove(path!);
                    }
                    else
                    {
                        _diagnosticsCache[path!] = new DiagnosticsCacheEntry(version, diagnostics);
                    }
                }
            }

            var args = new XamlDiagnosticsChangedEventArgs(path, version, diagnostics);

            try
            {
                _diagnosticsChanged?.Invoke(this, args);
            }
            catch
            {
                // Observers should not throw.
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

        private readonly struct DiagnosticsCacheEntry
        {
            public DiagnosticsCacheEntry(XamlDocumentVersion version, IReadOnlyList<XamlAstDiagnostic> diagnostics)
            {
                Version = version;
                Diagnostics = diagnostics ?? Array.Empty<XamlAstDiagnostic>();
            }

            public XamlDocumentVersion Version { get; }

            public IReadOnlyList<XamlAstDiagnostic> Diagnostics { get; }
        }
    }
}
