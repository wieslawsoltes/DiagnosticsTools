using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Utilities;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.Xaml
{
    internal sealed class XmlParserXamlAstProvider : IXamlAstProvider
    {
        private static readonly StringComparer PathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private static readonly string[] WatchedExtensions = { ".xaml", ".axaml", ".paml" };

        private readonly ConcurrentDictionary<string, CachedDocument> _cache = new(PathComparer);
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
        private readonly CancellationTokenSource _disposeCancellation = new();
        private bool _disposed;

        public event EventHandler<XamlDocumentChangedEventArgs>? DocumentChanged;
        public event EventHandler<XamlAstNodesChangedEventArgs>? NodesChanged;

        public async ValueTask<XamlAstDocument> GetDocumentAsync(string path, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path must not be null or whitespace.", nameof(path));
            }

            EnsureNotDisposed();

            var normalizedPath = NormalizePath(path);
            EnsureWatcherFor(normalizedPath);

            var fileInfo = new FileInfo(normalizedPath);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException($"The specified XAML file was not found: {normalizedPath}", normalizedPath);
            }

            var timestamp = fileInfo.LastWriteTimeUtc;
            var length = fileInfo.Length;
            var cached = _cache.GetOrAdd(normalizedPath, _ => new CachedDocument());
            var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);

            try
            {
                var result = await cached.GetOrCreateAsync(
                    normalizedPath,
                    timestamp,
                    length,
                    (ct) => LoadDocumentAsync(normalizedPath, timestamp, length, ct),
                    linkedToken.Token).ConfigureAwait(false);

                if (result.IsNew)
                {
                    var changes = XamlAstNodeDiffer.Diff(result.PreviousIndex, result.Index);
                    if (changes.Count > 0)
                    {
                        OnNodesChanged(new XamlAstNodesChangedEventArgs(normalizedPath, result.Document.Version, changes));
                    }

                    OnDocumentChanged(new XamlDocumentChangedEventArgs(normalizedPath, XamlDocumentChangeKind.Updated, result.Document));
                }

                return result.Document;
            }
            finally
            {
                linkedToken.Dispose();
            }
        }

        public void Invalidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalizedPath = NormalizePath(path);
            if (_cache.TryGetValue(normalizedPath, out var cached))
            {
                cached.Invalidate();
                OnDocumentChanged(new XamlDocumentChangedEventArgs(normalizedPath, XamlDocumentChangeKind.Invalidated));
            }
        }

        public void InvalidateAll()
        {
            foreach (var entry in _cache)
            {
                entry.Value.Invalidate();
            }

            if (_cache.Count > 0)
            {
                OnDocumentChanged(new XamlDocumentChangedEventArgs(string.Empty, XamlDocumentChangeKind.Invalidated));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCancellation.Cancel();

            foreach (var watcherPair in _watchers)
            {
                try
                {
                    watcherPair.Value.EnableRaisingEvents = false;
                    watcherPair.Value.Changed -= HandleWatcherChanged;
                    watcherPair.Value.Created -= HandleWatcherChanged;
                    watcherPair.Value.Deleted -= HandleWatcherChanged;
                    watcherPair.Value.Renamed -= HandleWatcherRenamed;
                    watcherPair.Value.Dispose();
                }
                catch
                {
                    // Swallow cleanup exceptions – watcher might already be disposed.
                }
            }

            _watchers.Clear();

            foreach (var entry in _cache)
            {
                entry.Value.Dispose();
            }

            _cache.Clear();
            _disposeCancellation.Dispose();
        }

        private async Task<XamlAstDocument> LoadDocumentAsync(string path, DateTimeOffset timestampHintUtc, long lengthHint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var content = await reader.ReadToEndAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            stream.Position = 0;
            var checksum = ComputeSha256(stream);

            var syntax = Parser.ParseText(content);
            var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);

            var currentTimestamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : timestampHintUtc;
            var currentLength = stream.Length > 0 ? stream.Length : lengthHint;
            var version = new XamlDocumentVersion(currentTimestamp, currentLength, checksum);

            return new XamlAstDocument(path, content, syntax, version, diagnostics);
        }

        private void EnsureWatcherFor(string normalizedPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(normalizedPath);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                _watchers.GetOrAdd(directory, CreateWatcher);
            }
            catch (Exception)
            {
                // Ignore watcher failures; provider still works without file system notifications.
            }
        }

        private FileSystemWatcher CreateWatcher(string directory)
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                Filter = "*.*",
                EnableRaisingEvents = true
            };

            watcher.Changed += HandleWatcherChanged;
            watcher.Created += HandleWatcherChanged;
            watcher.Deleted += HandleWatcherChanged;
            watcher.Renamed += HandleWatcherRenamed;

            return watcher;
        }

        private void HandleWatcherChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsXamlFile(e.FullPath))
            {
                return;
            }

            var normalizedPath = NormalizePath(e.FullPath);

            if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                if (_cache.TryRemove(normalizedPath, out var removed))
                {
                    if (removed.TryGetSnapshot(out var document, out var index) && index is not null)
                    {
                        var removalChanges = BuildRemovalChanges(index);
                        if (removalChanges.Count > 0)
                        {
                            OnNodesChanged(new XamlAstNodesChangedEventArgs(
                                normalizedPath,
                                document?.Version ?? default,
                                removalChanges));
                        }
                    }

                    removed.Dispose();
                }

                OnDocumentChanged(new XamlDocumentChangedEventArgs(normalizedPath, XamlDocumentChangeKind.Removed));
                return;
            }

            if (_cache.TryGetValue(normalizedPath, out var cached))
            {
                cached.Invalidate();
            }

            OnDocumentChanged(new XamlDocumentChangedEventArgs(normalizedPath, XamlDocumentChangeKind.Invalidated));
        }

        private void HandleWatcherRenamed(object sender, RenamedEventArgs e)
        {
            if (IsXamlFile(e.OldFullPath))
            {
                var oldPath = NormalizePath(e.OldFullPath);
                if (_cache.TryRemove(oldPath, out var removed))
                {
                    if (removed.TryGetSnapshot(out var document, out var index) && index is not null)
                    {
                        var removalChanges = BuildRemovalChanges(index);
                        if (removalChanges.Count > 0)
                        {
                            OnNodesChanged(new XamlAstNodesChangedEventArgs(
                                oldPath,
                                document?.Version ?? default,
                                removalChanges));
                        }
                    }

                    removed.Dispose();
                }

                OnDocumentChanged(new XamlDocumentChangedEventArgs(oldPath, XamlDocumentChangeKind.Removed));
            }

            if (IsXamlFile(e.FullPath))
            {
                var newPath = NormalizePath(e.FullPath);
                OnDocumentChanged(new XamlDocumentChangedEventArgs(newPath, XamlDocumentChangeKind.Invalidated));
            }
        }

        private void OnDocumentChanged(XamlDocumentChangedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                DocumentChanged?.Invoke(this, args);
            }
            catch
            {
                // Diagnostics observers should not crash the provider.
            }
        }

        private void OnNodesChanged(XamlAstNodesChangedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                NodesChanged?.Invoke(this, args);
            }
            catch
            {
                // Diagnostics observers should not crash the provider.
            }
        }

        private static IReadOnlyList<XamlAstNodeChange> BuildRemovalChanges(IXamlAstIndex index)
        {
            if (index is null)
            {
                throw new ArgumentNullException(nameof(index));
            }

            var changes = new List<XamlAstNodeChange>();
            foreach (var node in index.Nodes)
            {
                changes.Add(new XamlAstNodeChange(XamlAstNodeChangeKind.Removed, node, null));
            }

            return changes.Count == 0 ? Array.Empty<XamlAstNodeChange>() : changes;
        }

        private static string ComputeSha256(Stream stream)
        {
            stream.Position = 0;
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            stream.Position = 0;
            return ToHex(hash);
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path);
        }

        private static bool IsXamlFile(string fullPath)
        {
            var extension = Path.GetExtension(fullPath);
            if (extension is null)
            {
                return false;
            }

            foreach (var candidate in WatchedExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(XmlParserXamlAstProvider));
            }
        }

        private sealed class CachedDocument : IDisposable
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private XamlAstDocument? _document;
            private IXamlAstIndex? _index;

            public async ValueTask<CachedDocumentResult> GetOrCreateAsync(
                string path,
                DateTimeOffset timestampUtc,
                long length,
                Func<CancellationToken, Task<XamlAstDocument>> factory,
                CancellationToken cancellationToken)
            {
                if (_document is { } cached && Matches(cached, timestampUtc, length))
                {
                    if (_index is null)
                    {
                        var buildStart = Stopwatch.GetTimestamp();
                        _index = XamlAstIndex.Build(cached);
                        MutationInstrumentation.RecordAstIndexBuild(StopwatchHelper.GetElapsedTime(buildStart), "provider", cacheHit: true);
                    }

                    return new CachedDocumentResult(cached, _index!, null, false);
                }

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_document is { } existing && Matches(existing, timestampUtc, length))
                    {
                        if (_index is null)
                        {
                            var buildStart = Stopwatch.GetTimestamp();
                            _index = XamlAstIndex.Build(existing);
                            MutationInstrumentation.RecordAstIndexBuild(StopwatchHelper.GetElapsedTime(buildStart), "provider", cacheHit: true);
                        }

                        return new CachedDocumentResult(existing, _index!, null, false);
                    }

                    var previousIndex = _index;
                    var loadStart = Stopwatch.GetTimestamp();
                    var document = await factory(cancellationToken).ConfigureAwait(false);
                    MutationInstrumentation.RecordAstReload(StopwatchHelper.GetElapsedTime(loadStart), "provider", cacheHit: false);

                    var indexStart = Stopwatch.GetTimestamp();
                    var index = XamlAstIndex.Build(document);
                    MutationInstrumentation.RecordAstIndexBuild(StopwatchHelper.GetElapsedTime(indexStart), "provider", cacheHit: false);
                    _document = document;
                    _index = index;
                    return new CachedDocumentResult(document, index, previousIndex, true);
                }
                finally
                {
                    _gate.Release();
                }
            }

            public bool TryGetSnapshot(out XamlAstDocument? document, out IXamlAstIndex? index)
            {
                document = _document;
                if (document is null)
                {
                    index = null;
                    return false;
                }

                if (_index is null)
                {
                    var buildStart = Stopwatch.GetTimestamp();
                    _index = XamlAstIndex.Build(document);
                    MutationInstrumentation.RecordAstIndexBuild(StopwatchHelper.GetElapsedTime(buildStart), "provider", cacheHit: true);
                }

                index = _index;
                return index is not null;
            }

            public void Invalidate()
            {
                _document = null;
                _index = null;
            }

            public void Dispose()
            {
                _gate.Dispose();
                _document = null;
                _index = null;
            }

            private static bool Matches(XamlAstDocument document, DateTimeOffset timestampUtc, long length)
            {
                var version = document.Version;
                return version.TimestampUtc == timestampUtc && version.Length == length;
            }
        }

        private readonly struct CachedDocumentResult
        {
            public CachedDocumentResult(
                XamlAstDocument document,
                IXamlAstIndex index,
                IXamlAstIndex? previousIndex,
                bool isNew)
            {
                Document = document;
                Index = index;
                PreviousIndex = previousIndex;
                IsNew = isNew;
            }

            public XamlAstDocument Document { get; }

            public IXamlAstIndex Index { get; }

            public IXamlAstIndex? PreviousIndex { get; }

            public bool IsNew { get; }
        }
    }
}
