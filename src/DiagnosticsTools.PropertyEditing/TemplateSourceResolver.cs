using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    public interface ITemplateSourceResolver
    {
        ValueTask<TemplatePreviewRequest?> ResolveAsync(
            string documentPath,
            XamlTemplateBindingDescriptor binding,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Resolves template-bound property metadata to previewable XAML snapshots.
    /// </summary>
    public sealed class TemplateSourceResolver : ITemplateSourceResolver
    {
        private const string DefaultReadOnlyMessage = "Template source is read-only. Create a local override in your project to edit.";
        private static readonly StringComparer PathComparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        private static readonly StringComparer KeyComparer = StringComparer.Ordinal;

        private readonly XamlAstWorkspace _workspace;
        private readonly object _cacheGate;
        private readonly Dictionary<TemplateCacheKey, TemplateCacheEntry> _cache;
        private readonly Dictionary<string, HashSet<TemplateCacheKey>> _dependencyIndex;

        public TemplateSourceResolver(XamlAstWorkspace workspace)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _workspace.DocumentChanged += HandleWorkspaceDocumentChanged;
            _cacheGate = new object();
            _cache = new Dictionary<TemplateCacheKey, TemplateCacheEntry>();
            _dependencyIndex = new Dictionary<string, HashSet<TemplateCacheKey>>(PathComparer);
        }

        public async ValueTask<TemplatePreviewRequest?> ResolveAsync(
            string documentPath,
            XamlTemplateBindingDescriptor binding,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(documentPath))
            {
                throw new ArgumentException("Document path must not be null or whitespace.", nameof(documentPath));
            }

            if (binding is null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            var document = await _workspace.GetDocumentAsync(documentPath, cancellationToken).ConfigureAwait(false);
            var normalizedDocumentPath = string.IsNullOrWhiteSpace(document.Path) ? null : TemplateResourceReader.NormalizePath(document.Path);
            TemplateCacheKey? cacheKey = null;
            Dictionary<string, XamlDocumentVersion>? dependencies = null;

            if (normalizedDocumentPath is not null)
            {
                cacheKey = TemplateCacheKey.Create(normalizedDocumentPath, binding);
                if (TryGetCached(cacheKey.Value, document.Version, out var cached))
                {
                    return cached;
                }

                dependencies = CreateDependencyMap(normalizedDocumentPath, document.Version);
            }

            var index = await _workspace.GetIndexAsync(document.Path, cancellationToken).ConfigureAwait(false);
            TemplatePreviewRequest? result;

            switch (binding.SourceKind)
            {
                case XamlTemplateSourceKind.Inline:
                    if (binding.InlineTemplate is null)
                    {
                        result = null;
                        break;
                    }

                    result = CreateRequestFromDescriptor(
                        binding,
                        document,
                        index,
                        binding.InlineTemplate,
                        IsDocumentReadOnly(document.Path));
                    break;

                case XamlTemplateSourceKind.StaticResource:
                case XamlTemplateSourceKind.DynamicResource:
                case XamlTemplateSourceKind.ThemeResource:
                    result = await ResolveResourceReferenceAsync(document, index, binding, dependencies, cancellationToken).ConfigureAwait(false);
                    break;

                case XamlTemplateSourceKind.Uri:
                case XamlTemplateSourceKind.CompiledResource:
                {
                    var assetPreview = await TryCreateAssetPreviewAsync(
                        binding,
                        document.Path,
                        binding.SourceValue,
                        readOnlyMessage: null,
                        dependencies,
                        cancellationToken).ConfigureAwait(false);
                    if (assetPreview is not null)
                    {
                        result = assetPreview;
                        break;
                    }

                    result = CreateExternalReadOnlyRequest(binding, binding.SourceValue, document.Path);
                    break;
                }

                default:
                    result = null;
                    break;
            }

            if (cacheKey.HasValue && dependencies is not null && dependencies.Count > 0)
            {
                StoreCacheEntry(cacheKey.Value, result, dependencies);
            }

            return result;
        }

        private async ValueTask<TemplatePreviewRequest?> ResolveResourceReferenceAsync(
            XamlAstDocument document,
            IXamlAstIndex index,
            XamlTemplateBindingDescriptor binding,
            Dictionary<string, XamlDocumentVersion>? dependencies,
            CancellationToken cancellationToken,
            ISet<string>? visited = null,
            string? normalizedKey = null)
        {
            normalizedKey ??= NormalizeResourceKey(binding.SourceValue);
            if (string.IsNullOrEmpty(normalizedKey))
            {
                return CreateReadOnlyRequest(
                    binding,
                    document.Path,
                    null,
                    DefaultReadOnlyMessage,
                    $"Unable to resolve template resource for '{binding.RawProperty}'.");
            }

            var resourceDescriptor = FindResourceDescriptor(index, normalizedKey!);
            if (resourceDescriptor is not null)
            {
                var isReadOnly = IsDocumentReadOnly(document.Path);
                var readOnlyMessage = isReadOnly ? FormatReadOnlyMessage(null, document.Path) : null;
                return CreateRequestFromDescriptor(binding, document, index, resourceDescriptor, isReadOnly, readOnlyMessage);
            }

            visited ??= new HashSet<string>(PathComparer);
            if (!string.IsNullOrEmpty(document.Path))
            {
                visited.Add(TemplateResourceReader.NormalizePath(document.Path));
            }

            TemplatePreviewRequest? externalFallback = null;

            foreach (var include in GetResourceIncludes(index))
            {
                var includeSource = GetAttributeValue(include, "Source");
                if (string.IsNullOrWhiteSpace(includeSource))
                {
                    continue;
                }

                var resolvedPath = ResolveIncludePath(document.Path, includeSource);
                if (string.IsNullOrEmpty(resolvedPath))
                {
                    resolvedPath = TemplateResourceReader.TryMapAvaresToLocalPath(document.Path, includeSource);
                }

                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    var normalized = TemplateResourceReader.NormalizePath(resolvedPath);
                    if (!visited.Contains(normalized) && File.Exists(normalized))
                    {
                        var includeDocument = await _workspace.GetDocumentAsync(normalized, cancellationToken).ConfigureAwait(false);
                        if (dependencies is not null)
                        {
                            AddDependency(dependencies, includeDocument.Path, includeDocument.Version);
                        }
                        var includeIndex = await _workspace.GetIndexAsync(includeDocument.Path, cancellationToken).ConfigureAwait(false);
                        var resolved = await ResolveResourceReferenceAsync(includeDocument, includeIndex, binding, dependencies, cancellationToken, visited, normalizedKey).ConfigureAwait(false);
                        if (resolved is not null)
                        {
                            return resolved;
                        }
                    }
                }
                else
                {
                    var assetPreview = await TryCreateAssetPreviewAsync(
                        binding,
                        document.Path,
                        includeSource!,
                        FormatReadOnlyMessage(includeSource!, null),
                        dependencies,
                        cancellationToken).ConfigureAwait(false);
                    if (assetPreview is not null)
                    {
                        return assetPreview;
                    }

                    if (externalFallback is null)
                    {
                        var includeUri = TryCreateUri(includeSource);
                        externalFallback = CreateReadOnlyRequest(
                            binding,
                            document.Path,
                            includeUri,
                            FormatReadOnlyMessage(includeSource!, null),
                            $"Template resource '{normalizedKey}' is provided by external source '{includeSource}'.");
                    }
                }
            }

            if (externalFallback is not null)
            {
                return externalFallback;
            }

            var assetFallback = await TryCreateAssetPreviewAsync(
                binding,
                document.Path,
                binding.SourceValue,
                FormatReadOnlyMessage(normalizedKey!, null),
                dependencies,
                cancellationToken).ConfigureAwait(false);
            if (assetFallback is not null)
            {
                return assetFallback;
            }

            return CreateReadOnlyRequest(
                binding,
                document.Path,
                null,
                FormatReadOnlyMessage(normalizedKey, null),
                $"Unable to resolve template resource '{normalizedKey}'.");
        }

        private TemplatePreviewRequest CreateRequestFromDescriptor(
            XamlTemplateBindingDescriptor binding,
            XamlAstDocument document,
            IXamlAstIndex index,
            XamlAstNodeDescriptor descriptor,
            bool isReadOnly,
            string? readOnlyMessage = null)
        {
            var snippet = ExtractSnippet(document.Text, descriptor.Span);
            var sourceUri = TryCreateUri(document.Path);

            return new TemplatePreviewRequest(
                binding,
                document.Path,
                sourceUri,
                snippet,
                descriptor.LineSpan,
                document.Version,
                document,
                GetDocumentNodes(index),
                isReadOnly,
                isReadOnly ? readOnlyMessage ?? FormatReadOnlyMessage(null, document.Path) : null,
                providerDisplayName: "File",
                ownerDisplayName: Path.GetFileName(document.Path));
        }

        private TemplatePreviewRequest CreateExternalReadOnlyRequest(
            XamlTemplateBindingDescriptor binding,
            string? sourceValue,
            string? fallbackDocumentPath)
        {
            var uri = TryCreateUri(sourceValue) ?? TryCreateUri(fallbackDocumentPath);
            var message = FormatReadOnlyMessage(sourceValue, null);
            var error = $"Template source '{sourceValue ?? binding.RawProperty}' is external and cannot be previewed.";
            var metadata = GetProviderMetadata(sourceValue);
            return CreateReadOnlyRequest(binding, fallbackDocumentPath, uri, message, error, metadata.Provider, metadata.OwnerDisplayName);
        }

        private static TemplatePreviewRequest CreateReadOnlyRequest(
            XamlTemplateBindingDescriptor binding,
            string? documentPath,
            Uri? sourceUri,
            string? readOnlyMessage,
            string? errorMessage,
            string? providerDisplayName = null,
            string? ownerDisplayName = null)
        {
            return new TemplatePreviewRequest(
                binding,
                documentPath,
                sourceUri,
                snapshotText: null,
                lineSpan: null,
                version: null,
                document: null,
                documentNodes: null,
                isReadOnly: true,
                readOnlyMessage: string.IsNullOrWhiteSpace(readOnlyMessage) ? DefaultReadOnlyMessage : readOnlyMessage,
                errorMessage: errorMessage,
                providerDisplayName: providerDisplayName,
                ownerDisplayName: ownerDisplayName);
        }

        private static IEnumerable<XamlAstNodeDescriptor> GetResourceIncludes(IXamlAstIndex index)
        {
            foreach (var node in index.Nodes)
            {
                if (string.Equals(node.LocalName, "ResourceInclude", StringComparison.Ordinal) ||
                    string.Equals(node.LocalName, "StyleInclude", StringComparison.Ordinal))
                {
                    yield return node;
                }
            }
        }

        private static XamlAstNodeDescriptor? FindResourceDescriptor(IXamlAstIndex index, string resourceKey)
        {
            foreach (var resource in index.Resources)
            {
                if (KeyComparer.Equals(resource.Key, resourceKey))
                {
                    return resource.Node;
                }
            }

            return null;
        }

        private async Task<TemplatePreviewRequest?> TryCreateAssetPreviewAsync(
            XamlTemplateBindingDescriptor binding,
            string? baseDocumentPath,
            string? source,
            string? readOnlyMessage,
            Dictionary<string, XamlDocumentVersion>? dependencies,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            var content = await TemplateResourceReader.TryReadAsync(_workspace, baseDocumentPath, source!, cancellationToken).ConfigureAwait(false);
            if (content is null || !content.Value.Success || string.IsNullOrEmpty(content.Value.Text))
            {
                return null;
            }

            var info = content.Value;

            var message = string.IsNullOrWhiteSpace(readOnlyMessage)
                ? FormatReadOnlyMessage(source, info.DocumentPath)
                : readOnlyMessage;

            if (dependencies is not null && !string.IsNullOrEmpty(info.DocumentPath))
            {
                AddDependency(dependencies, info.DocumentPath, info.Version);
            }

            return new TemplatePreviewRequest(
                binding,
                info.DocumentPath,
                info.SourceUri,
                info.Text,
                lineSpan: null,
                version: null,
                document: null,
                documentNodes: null,
                isReadOnly: true,
                readOnlyMessage: message,
                errorMessage: null,
                providerDisplayName: info.ProviderDisplayName,
                ownerDisplayName: info.AssemblyName);
        }

        private static string? NormalizeResourceKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();

            if (trimmed.Length >= 2 && trimmed[0] == '{' && trimmed[trimmed.Length - 1] == '}')
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            }

            var commaIndex = trimmed.IndexOf(',');
            if (commaIndex >= 0)
            {
                trimmed = trimmed.Substring(0, commaIndex).Trim();
            }

            if (trimmed.StartsWith("ResourceKey=", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("ResourceKey=".Length).Trim();
            }
            else if (trimmed.StartsWith("Key=", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring("Key=".Length).Trim();
            }

            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string? ResolveIncludePath(string? baseDocumentPath, string includeValue)
        {
            if (string.IsNullOrWhiteSpace(includeValue))
            {
                return null;
            }

            if (Uri.TryCreate(includeValue, UriKind.Absolute, out var absolute))
            {
                if (absolute.IsFile)
                {
                    return TemplateResourceReader.NormalizePath(absolute.LocalPath);
                }

                return null;
            }

            if (string.IsNullOrWhiteSpace(baseDocumentPath))
            {
                return null;
            }

            var baseDirectory = Path.GetDirectoryName(baseDocumentPath);
            if (string.IsNullOrEmpty(baseDirectory))
            {
                return null;
            }

            var candidate = Path.IsPathRooted(includeValue)
                ? includeValue
                : Path.Combine(baseDirectory, includeValue.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return TemplateResourceReader.NormalizePath(candidate);
        }

        private static bool IsDocumentReadOnly(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return true;
                }

                var attributes = File.GetAttributes(path);
                return attributes.HasFlag(FileAttributes.ReadOnly);
            }
            catch
            {
                return true;
            }
        }

        private static IReadOnlyList<XamlAstNodeDescriptor> GetDocumentNodes(IXamlAstIndex index)
        {
            if (index.Nodes is IReadOnlyList<XamlAstNodeDescriptor> readOnly)
            {
                return readOnly;
            }

            return index.Nodes.ToList();
        }

        private static string ExtractSnippet(string text, TextSpan span)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var start = span.Start;
            if (start < 0)
            {
                start = 0;
            }
            else if (start > text.Length)
            {
                start = text.Length;
            }
            var length = span.Length;
            if (length < 0)
            {
                length = 0;
            }

            if (start + length > text.Length)
            {
                length = text.Length - start;
            }

            return length <= 0 ? string.Empty : text.Substring(start, length);
        }

        internal static Uri? TryCreateUri(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
            {
                return absolute;
            }

            if (Uri.TryCreate(value, UriKind.Relative, out var relative))
            {
                return relative;
            }

            return null;
        }

        private static string? GetAttributeValue(XamlAstNodeDescriptor descriptor, string attributeName)
        {
            foreach (var attribute in descriptor.Attributes)
            {
                if (string.Equals(attribute.LocalName, attributeName, StringComparison.Ordinal))
                {
                    var value = attribute.Value?.Trim();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }

            return null;
        }

        private static string FormatReadOnlyMessage(string? identifier, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                return $"Template source '{path}' is read-only. Create a local override to edit.";
            }

            if (!string.IsNullOrWhiteSpace(identifier))
            {
                return $"Template resource '{identifier}' is external and read-only.";
            }

            return DefaultReadOnlyMessage;
        }

        private static (string Provider, string? OwnerDisplayName) GetProviderMetadata(string? sourceValue)
        {
            if (string.IsNullOrWhiteSpace(sourceValue))
            {
                return ("External Resource", null);
            }

            if (Uri.TryCreate(sourceValue, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    return ("File", Path.GetFileName(uri.LocalPath));
                }

                if (string.Equals(uri.Scheme, "avares", StringComparison.OrdinalIgnoreCase))
                {
                    return ("Embedded Resource", uri.Authority);
                }

                if (string.Equals(uri.Scheme, "resm", StringComparison.OrdinalIgnoreCase))
                {
                    var assembly = TemplateResourceReader.ExtractResmAssemblyName(uri) ?? uri.Authority;
                    return ("Embedded Resource", assembly);
                }

                if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    return ($"{uri.Scheme.ToUpperInvariant()} Resource", uri.Host);
                }

                return ($"{uri.Scheme.ToUpperInvariant()} Resource", uri.Authority);
            }

            return ("External Resource", null);
        }

        private static Dictionary<string, XamlDocumentVersion> CreateDependencyMap(string documentPath, XamlDocumentVersion version)
        {
            var map = new Dictionary<string, XamlDocumentVersion>(PathComparer)
            {
                [documentPath] = version
            };
            return map;
        }

        private static void AddDependency(Dictionary<string, XamlDocumentVersion>? dependencies, string? path, XamlDocumentVersion? version)
        {
            if (dependencies is null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var normalized = TemplateResourceReader.NormalizePath(path);
            dependencies[normalized] = version ?? default;
        }

        private bool TryGetCached(TemplateCacheKey key, XamlDocumentVersion rootVersion, out TemplatePreviewRequest? request)
        {
            lock (_cacheGate)
            {
                if (_cache.TryGetValue(key, out var entry))
                {
                    if (!entry.IsValidFor(key.DocumentPath, rootVersion))
                    {
                        RemoveCacheEntry_NoLock(key);
                    }
                    else
                    {
                        request = entry.Request;
                        return true;
                    }
                }
            }

            request = null;
            return false;
        }

        private void StoreCacheEntry(TemplateCacheKey key, TemplatePreviewRequest? request, Dictionary<string, XamlDocumentVersion> dependencies)
        {
            if (dependencies.Count == 0)
            {
                return;
            }

            var dependencyArray = dependencies
                .Select(kvp => new TemplateCacheDependency(kvp.Key, kvp.Value))
                .ToArray();

            lock (_cacheGate)
            {
                RemoveCacheEntry_NoLock(key);
                var entry = new TemplateCacheEntry(request, dependencyArray);
                _cache[key] = entry;

                foreach (var dependency in dependencyArray)
                {
                    if (!_dependencyIndex.TryGetValue(dependency.Path, out var keys))
                    {
                        keys = new HashSet<TemplateCacheKey>();
                        _dependencyIndex[dependency.Path] = keys;
                    }

                    keys.Add(key);
                }
            }
        }

        private void RemoveCacheEntry_NoLock(TemplateCacheKey key)
        {
            if (!_cache.TryGetValue(key, out var entry))
            {
                return;
            }

            _cache.Remove(key);

            foreach (var dependency in entry.Dependencies)
            {
                if (_dependencyIndex.TryGetValue(dependency.Path, out var keys))
                {
                    keys.Remove(key);
                    if (keys.Count == 0)
                    {
                        _dependencyIndex.Remove(dependency.Path);
                    }
                }
            }
        }

        private void ClearCache_NoLock()
        {
            _cache.Clear();
            _dependencyIndex.Clear();
        }

        private void HandleWorkspaceDocumentChanged(object? sender, XamlDocumentChangedEventArgs e)
        {
            if (e is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(e.Path))
            {
                lock (_cacheGate)
                {
                    ClearCache_NoLock();
                }
                return;
            }

            var normalizedPath = TemplateResourceReader.NormalizePath(e.Path);

            lock (_cacheGate)
            {
                if (!_dependencyIndex.TryGetValue(normalizedPath, out var keys) || keys.Count == 0)
                {
                    return;
                }

                foreach (var cacheKey in keys.ToArray())
                {
                    RemoveCacheEntry_NoLock(cacheKey);
                }
            }
        }

        private sealed class TemplateCacheEntry
        {
            public TemplateCacheEntry(TemplatePreviewRequest? request, TemplateCacheDependency[] dependencies)
            {
                Request = request;
                Dependencies = dependencies ?? Array.Empty<TemplateCacheDependency>();
            }

            public TemplatePreviewRequest? Request { get; }

            public TemplateCacheDependency[] Dependencies { get; }

            public bool IsValidFor(string rootPath, XamlDocumentVersion rootVersion)
            {
                foreach (var dependency in Dependencies)
                {
                    if (PathComparer.Equals(dependency.Path, rootPath))
                    {
                        return dependency.Version.Equals(rootVersion);
                    }
                }

                return false;
            }
        }

        private readonly record struct TemplateCacheDependency(string Path, XamlDocumentVersion Version);

        private readonly record struct TemplateCacheKey(
            string DocumentPath,
            string OwnerId,
            string Property,
            XamlTemplateSourceKind SourceKind,
            string? SourceValue)
        {
            public static TemplateCacheKey Create(string documentPath, XamlTemplateBindingDescriptor binding)
            {
                if (binding is null)
                {
                    throw new ArgumentNullException(nameof(binding));
                }

                var ownerId = binding.Owner.Id.Value;
                if (string.IsNullOrEmpty(ownerId))
                {
                    ownerId = binding.Owner.Path is { Count: > 0 } path
                        ? string.Join("/", path)
                        : binding.Owner.LineSpan.ToString();
                }

                return new TemplateCacheKey(
                    documentPath,
                    ownerId ?? string.Empty,
                    binding.RawProperty,
                    binding.SourceKind,
                    binding.SourceValue);
            }
        }
    }
}
