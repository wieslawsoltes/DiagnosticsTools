using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Platform;

namespace Avalonia.Diagnostics.SourceNavigation
{
    internal sealed class AvaloniaXamlDocumentLocator : IXamlDocumentLocator
    {
        private static readonly HttpClient SharedHttpClient = new();
        private readonly ConcurrentDictionary<Assembly, Task<ResourceXamlInfo>> _xamlInfoCache = new();
        private readonly ConcurrentDictionary<Uri, Task<XDocument?>> _remoteXamlCache = new();

        public async ValueTask<XamlDocumentResult?> GetDocumentAsync(
            XamlDocumentRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rootSource = request.RootSource;
            XDocument? document = null;

            if (!string.IsNullOrEmpty(rootSource.LocalPath) && File.Exists(rootSource.LocalPath))
            {
                using var stream = File.OpenRead(rootSource.LocalPath);
                document = LoadXamlDocument(stream);
            }
            else
            {
                document = await TryLoadXamlFromAssetsAsync(request.RootType, cancellationToken).ConfigureAwait(false);
            }

            if (document is null && rootSource.RemoteUri is { } remoteUri)
            {
                document = await TryLoadXamlFromRemoteAsync(remoteUri, cancellationToken).ConfigureAwait(false);
            }

            return document?.Root is null
                ? null
                : new XamlDocumentResult(document, rootSource);
        }

        private async Task<XDocument?> TryLoadXamlFromAssetsAsync(Type rootType, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assembly = rootType.Assembly;
            var info = await GetResourceXamlInfoAsync(assembly).ConfigureAwait(false);
            if (!info.TryGetResourcePath(rootType, out var resourcePath))
            {
                return null;
            }

            var assemblyName = assembly.GetName().Name;
            if (string.IsNullOrEmpty(assemblyName))
            {
                return null;
            }

            var uri = new Uri($"avares://{assemblyName}{resourcePath}");
            var assetLoader = AvaloniaLocator.Current.GetService<IAssetLoader>();
            if (assetLoader is not null && assetLoader.Exists(uri))
            {
                using var stream = assetLoader.Open(uri);
                return LoadXamlDocument(stream);
            }

            var manifestName = resourcePath.TrimStart('/').Replace('/', '.');
            using var manifestStream = assembly.GetManifestResourceStream(manifestName);
            return manifestStream is null ? null : LoadXamlDocument(manifestStream);
        }

        private Task<XDocument?> TryLoadXamlFromRemoteAsync(Uri remoteUri, CancellationToken cancellationToken)
        {
            if (!remoteUri.IsAbsoluteUri)
            {
                return Task.FromResult<XDocument?>(null);
            }

            var scheme = remoteUri.Scheme;
            if (!string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<XDocument?>(null);
            }

            return _remoteXamlCache.GetOrAdd(remoteUri, LoadRemoteXamlAsync);
        }

        private async Task<XDocument?> LoadRemoteXamlAsync(Uri remoteUri)
        {
            try
            {
                using var stream = await SharedHttpClient.GetStreamAsync(remoteUri).ConfigureAwait(false);
                return LoadXamlDocument(stream);
            }
            catch
            {
                return null;
            }
        }

        private Task<ResourceXamlInfo> GetResourceXamlInfoAsync(Assembly assembly)
        {
            return _xamlInfoCache.GetOrAdd(assembly, asm => Task.Run(() => LoadResourceXamlInfo(asm)));
        }

        private static ResourceXamlInfo LoadResourceXamlInfo(Assembly assembly)
        {
            const string resourceName = "!AvaloniaResourceXamlInfo";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return ResourceXamlInfo.Empty;
            }

            try
            {
                var serializer = new DataContractSerializer(typeof(ResourceXamlInfo));
                if (serializer.ReadObject(stream) is ResourceXamlInfo info)
                {
                    return info;
                }
            }
            catch
            {
                // Ignored – fall back to empty mapping if deserialization fails.
            }

            return ResourceXamlInfo.Empty;
        }

        private static XDocument LoadXamlDocument(Stream stream)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                CloseInput = true
            };
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }

        [DataContract]
        private sealed class ResourceXamlInfo
        {
            public static ResourceXamlInfo Empty { get; } = new();

            [DataMember]
            public Dictionary<string, string> ClassToResourcePathIndex { get; set; } = new(StringComparer.Ordinal);

            public bool TryGetResourcePath(Type type, out string path)
            {
                path = string.Empty;
                var key = type.FullName ?? type.Name;
                if (string.IsNullOrEmpty(key))
                {
                    return false;
                }

                if (ClassToResourcePathIndex.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
                {
                    path = value;
                    return true;
                }

                return false;
            }
        }
    }
}
