using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.SourceNavigation
{
    internal sealed class PortablePdbResolver : IDisposable
    {
        private static readonly Guid SourceLinkGuid = new("cc110556-a091-4d38-9f06-3330a3c5c2c3");

        private readonly string _assemblyLocation;
        private FileStream? _pdbStream;
        private MetadataReaderProvider? _readerProvider;
        private SourceLinkMap? _sourceLink;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public PortablePdbResolver(string assemblyLocation)
        {
            _assemblyLocation = assemblyLocation;
        }

        public async ValueTask<SourceInfo?> TryGetSourceInfoAsync(MethodBase method)
        {
            ThrowIfDisposed();

            if (method is null)
            {
                throw new ArgumentNullException(nameof(method));
            }

            var reader = await GetReaderAsync().ConfigureAwait(false);
            if (reader is null)
            {
                return null;
            }

            MethodDefinitionHandle methodHandle;
            try
            {
                methodHandle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }

            var debug = reader.GetMethodDebugInformation(methodHandle);
            var sequencePoints = debug.GetSequencePoints();

            SequencePoint? firstPoint = null;
            DocumentHandle documentHandle = debug.Document;

            foreach (var point in sequencePoints)
            {
                if (point.IsHidden)
                {
                    continue;
                }

                firstPoint = point;
                if (!point.Document.IsNil)
                {
                    documentHandle = point.Document;
                }

                break;
            }

            if (documentHandle.IsNil)
            {
                return null;
            }

            var document = reader.GetDocument(documentHandle);
            var path = reader.GetString(document.Name);
            string? fallback = null;

            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    fallback = Path.GetFullPath(path);
                }
                catch (Exception) when (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    fallback = path;
                }
                catch (IOException)
                {
                    fallback = path;
                }
            }

            var remoteUri = _sourceLink?.TryResolve(path);
            var origin = SourceOrigin.Unknown;
            string? localPath = null;

            if (!string.IsNullOrEmpty(fallback) && File.Exists(fallback))
            {
                localPath = fallback;
                origin = SourceOrigin.Local;
            }
            else if (remoteUri is not null)
            {
                origin = SourceOrigin.SourceLink;
                localPath = fallback;
            }

            if (origin == SourceOrigin.Unknown)
            {
                return null;
            }

            var startLine = firstPoint?.StartLine;
            var startColumn = firstPoint?.StartColumn;
            var endLine = firstPoint?.EndLine;
            var endColumn = firstPoint?.EndColumn;

            return new SourceInfo(localPath, remoteUri, startLine, startColumn, endLine, endColumn, origin);
        }

        public async ValueTask<SourceInfo?> TryGetSourceInfoAsync(Type type, string methodName)
        {
            ThrowIfDisposed();

            var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.Ordinal));

            return method is null ? null : await TryGetSourceInfoAsync(method).ConfigureAwait(false);
        }

        public async ValueTask<SourceInfo?> TryGetSourceInfoAsync(Type type)
        {
            ThrowIfDisposed();

            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            static IEnumerable<MethodBase> EnumerateMethods(Type t)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                foreach (var method in t.GetMethods(flags))
                {
                    yield return method;
                }

                foreach (var ctor in t.GetConstructors(flags))
                {
                    yield return ctor;
                }
            }

            SourceInfo? fallback = null;

            foreach (var member in EnumerateMethods(type))
            {
                var info = await TryGetSourceInfoAsync(member).ConfigureAwait(false);
                if (info is null)
                {
                    continue;
                }

                if (IsXamlDocument(info))
                {
                    return info;
                }

                fallback ??= info;
            }

            return fallback;
        }

        private static bool IsXamlDocument(SourceInfo info)
        {
            var candidate = info.LocalPath ?? info.RemoteUri?.AbsoluteUri;
            return candidate is not null && candidate.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
        }

        private async ValueTask<MetadataReader?> GetReaderAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_readerProvider is not null)
                {
                    return _readerProvider.GetMetadataReader();
                }

                var pdbPath = Path.ChangeExtension(_assemblyLocation, ".pdb");
                if (string.IsNullOrEmpty(pdbPath) || !File.Exists(pdbPath))
                {
                    return null;
                }

                _pdbStream = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _readerProvider = MetadataReaderProvider.FromPortablePdbStream(_pdbStream, MetadataStreamOptions.LeaveOpen);
                var reader = _readerProvider.GetMetadataReader();
                _sourceLink = TryReadSourceLink(reader);
                return reader;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static SourceLinkMap? TryReadSourceLink(MetadataReader reader)
        {
            foreach (var handle in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
            {
                var info = reader.GetCustomDebugInformation(handle);
                var kind = reader.GetGuid(info.Kind);
                if (!kind.Equals(SourceLinkGuid))
                {
                    continue;
                }

                var blob = reader.GetBlobBytes(info.Value);
                return SourceLinkMap.TryLoad(blob);
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _readerProvider?.Dispose();
            _pdbStream?.Dispose();
            _gate.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PortablePdbResolver));
            }
        }
    }
}
