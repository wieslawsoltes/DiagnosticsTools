using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Diagnostics;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Avalonia.Diagnostics.SourceNavigation
{
    internal sealed class SourceInfoService : ISourceInfoService, IDisposable
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private readonly ConcurrentDictionary<string, Task<PortablePdbResolver?>> _resolverCache = new(PathComparer);
        private readonly ConcurrentBag<PortablePdbResolver> _ownedResolvers = new();
    private readonly ConcurrentDictionary<Type, Task<XamlDocument?>> _xamlDocumentCache = new();
    private readonly ConcurrentDictionary<Assembly, Task<ResourceXamlInfo>> _xamlInfoCache = new();
        private bool _disposed;

        public async ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member)
        {
            ThrowIfDisposed();
            if (member is null)
            {
                throw new ArgumentNullException(nameof(member));
            }

            return await ResolveMemberAsync(member).ConfigureAwait(false);
        }

        public async ValueTask<SourceInfo?> GetForAvaloniaObjectAsync(AvaloniaObject avaloniaObject)
        {
            ThrowIfDisposed();
            if (avaloniaObject is null)
            {
                throw new ArgumentNullException(nameof(avaloniaObject));
            }

            var type = avaloniaObject.GetType();
            var info = await ResolveMemberAsync(type).ConfigureAwait(false);
            if (info is not null)
            {
                return info;
            }

            if (avaloniaObject is StyledElement { StyleKey: Type styleKey })
            {
                info = await ResolveMemberAsync(styleKey).ConfigureAwait(false);
                if (info is not null)
                {
                    return info;
                }
            }

            info = await TryResolveFromXamlAsync(avaloniaObject).ConfigureAwait(false);
            if (info is not null)
            {
                return info;
            }

            return null;
        }

        public async ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic)
        {
            ThrowIfDisposed();

            if (valueFrameDiagnostic is null)
            {
                return null;
            }

            if (valueFrameDiagnostic is IValueFrameDiagnostic frame)
            {
                if (frame.Source is { } source)
                {
                    var info = await TryResolveFrameSourceAsync(source).ConfigureAwait(false);
                    if (info is not null)
                    {
                        return info;
                    }
                }

                return await TryResolveFrameSourceAsync(frame).ConfigureAwait(false);
            }

            return await TryResolveFrameSourceAsync(valueFrameDiagnostic).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var resolver in _ownedResolvers)
            {
                resolver.Dispose();
            }
        }

        private async ValueTask<SourceInfo?> TryResolveFrameSourceAsync(object source)
        {
            switch (source)
            {
                case ControlTheme theme:
                {
                    var info = ResolveFluentControlTheme(theme);
                    if (info is not null)
                    {
                        return info;
                    }

                    break;
                }

                case StyleBase { Parent: ControlTheme parentTheme }:
                {
                    var info = ResolveFluentControlTheme(parentTheme);
                    if (info is not null)
                    {
                        return info;
                    }

                    break;
                }

                case AvaloniaObject avaloniaObject:
                {
                    var info = await GetForAvaloniaObjectAsync(avaloniaObject).ConfigureAwait(false);
                    if (info is not null)
                    {
                        return info;
                    }

                    break;
                }

                case MemberInfo member:
                    return await ResolveMemberAsync(member).ConfigureAwait(false);
            }

            return await ResolveMemberAsync(source.GetType()).ConfigureAwait(false);
        }

        private async ValueTask<SourceInfo?> ResolveMemberAsync(MemberInfo member)
        {
            switch (member)
            {
                case MethodBase method:
                {
                    var resolver = await GetResolverAsync(method.DeclaringType?.Assembly ?? method.Module.Assembly).ConfigureAwait(false);
                    if (resolver is null)
                    {
                        return null;
                    }

                    return await resolver.TryGetSourceInfoAsync(method).ConfigureAwait(false);
                }

                case Type type:
                {
                    var resolver = await GetResolverAsync(type.Assembly).ConfigureAwait(false);
                    if (resolver is null)
                    {
                        return null;
                    }

                    return await resolver.TryGetSourceInfoAsync(type).ConfigureAwait(false);
                }

                case PropertyInfo property:
                {
                    if (property.GetMethod is { } getter)
                    {
                        var info = await ResolveMemberAsync(getter).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    if (property.SetMethod is { } setter)
                    {
                        var info = await ResolveMemberAsync(setter).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    break;
                }

                case EventInfo evt:
                {
                    if (evt.AddMethod is { } adder)
                    {
                        var info = await ResolveMemberAsync(adder).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    if (evt.RemoveMethod is { } remover)
                    {
                        var info = await ResolveMemberAsync(remover).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    break;
                }
            }

            return null;
        }

        private SourceInfo? ResolveFluentControlTheme(ControlTheme theme)
        {
            if (theme.TargetType is null)
            {
                return null;
            }

            var fileName = theme.TargetType.Name;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var remoteUri = BuildFluentThemeUri($"src/Avalonia.Themes.Fluent/Controls/{fileName}.axaml");
            if (remoteUri is null)
            {
                return null;
            }

            return new SourceInfo(
                LocalPath: null,
                RemoteUri: remoteUri,
                StartLine: null,
                StartColumn: null,
                EndLine: null,
                EndColumn: null,
                Origin: SourceOrigin.SourceLink);
        }

        private static Uri? BuildFluentThemeUri(string relativePath)
        {
            var assembly = typeof(FluentTheme).Assembly;
            var version = assembly.GetName().Version;

            string tag;
            if (version is null)
            {
                tag = "master";
            }
            else if (version.Build > 0)
            {
                tag = $"v{version.Major}.{version.Minor}.{version.Build}";
            }
            else
            {
                tag = $"v{version.Major}.{version.Minor}";
            }

            var uriString = $"https://raw.githubusercontent.com/AvaloniaUI/Avalonia/{tag}/{relativePath}";
            return Uri.TryCreate(uriString, UriKind.Absolute, out var uri) ? uri : null;
        }

        private async ValueTask<PortablePdbResolver?> GetResolverAsync(Assembly? assembly)
        {
            if (assembly is null)
            {
                return null;
            }

            var location = assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                return null;
            }

            var task = _resolverCache.GetOrAdd(location, path => CreateResolverAsync(path));
            return await task.ConfigureAwait(false);
        }

        private async Task<PortablePdbResolver?> CreateResolverAsync(string assemblyPath)
        {
            var resolver = new PortablePdbResolver(assemblyPath);
            if (await resolver.EnsureMetadataAsync().ConfigureAwait(false))
            {
                _ownedResolvers.Add(resolver);
                return resolver;
            }

            resolver.Dispose();
            return null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SourceInfoService));
            }
        }

        private async ValueTask<SourceInfo?> TryResolveFromXamlAsync(AvaloniaObject avaloniaObject)
        {
            if (!TryBuildLogicalPath(avaloniaObject, out var root, out var path))
            {
                return null;
            }

            var rootType = root.GetType();
            var rootInfo = await ResolveMemberAsync(rootType).ConfigureAwait(false);
            if (rootInfo is null)
            {
                return null;
            }

            var document = await GetXamlDocumentAsync(rootType, rootInfo).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            var node = document.FindNode(path);
            if (node is null)
            {
                return null;
            }

            return document.CreateSourceInfo(node);
        }

        private async Task<XamlDocument?> GetXamlDocumentAsync(Type rootType, SourceInfo rootInfo)
        {
            return await _xamlDocumentCache.GetOrAdd(rootType, _ => BuildXamlDocumentAsync(rootType, rootInfo)).ConfigureAwait(false);
        }

        private async Task<XamlDocument?> BuildXamlDocumentAsync(Type rootType, SourceInfo rootInfo)
        {
            XDocument? document = null;

            if (!string.IsNullOrEmpty(rootInfo.LocalPath) && File.Exists(rootInfo.LocalPath))
            {
                using var fileStream = File.OpenRead(rootInfo.LocalPath);
                document = LoadXamlDocument(fileStream);
            }
            else
            {
                document = await TryLoadXamlFromAssetsAsync(rootType).ConfigureAwait(false);
            }

            if (document?.Root is null)
            {
                return null;
            }

            var rootNode = BuildXamlNode(document.Root);
            return new XamlDocument(rootInfo, rootNode);
        }

        private async Task<XDocument?> TryLoadXamlFromAssetsAsync(Type rootType)
        {
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

        private static XamlNode BuildXamlNode(XElement element)
        {
            var children = new List<XamlNode>();

            foreach (var child in element.Elements())
            {
                if (IsPropertyElement(child.Name))
                {
                    if (ShouldSkipPropertyElement(child.Name))
                    {
                        continue;
                    }

                    foreach (var propertyChild in child.Elements())
                    {
                        children.Add(BuildXamlNode(propertyChild));
                    }
                }
                else
                {
                    children.Add(BuildXamlNode(child));
                }
            }

            return new XamlNode(element, children);
        }

        private static bool IsPropertyElement(XName name)
        {
            return name.LocalName.IndexOf('.') >= 0;
        }

        private static bool ShouldSkipPropertyElement(XName name)
        {
            var localName = name.LocalName;
            var separatorIndex = localName.IndexOf('.');
            if (separatorIndex < 0)
            {
                return false;
            }

            var propertyName = localName.Substring(separatorIndex + 1);
            return propertyName.Equals("Resources", StringComparison.Ordinal) ||
                   propertyName.Equals("Styles", StringComparison.Ordinal);
        }

        private static bool TryBuildLogicalPath(AvaloniaObject target, out StyledElement root, out List<int> path)
        {
            path = new List<int>();
            if (target is not ILogical logical)
            {
                root = null!;
                return false;
            }

            var current = logical;
            while (true)
            {
                if (current.LogicalParent is not { } parent)
                {
                    if (current is StyledElement styledRoot)
                    {
                        root = styledRoot;
                        path.Reverse();
                        return true;
                    }

                    root = null!;
                    return false;
                }

                var index = IndexOfLogicalChild(parent, current);
                if (index < 0)
                {
                    root = null!;
                    return false;
                }

                path.Add(index);
                current = parent;
            }
        }

        private static int IndexOfLogicalChild(ILogical parent, ILogical child)
        {
            var children = parent.LogicalChildren;
            var count = children.Count;
            for (var i = 0; i < count; ++i)
            {
                if (ReferenceEquals(children[i], child))
                {
                    return i;
                }
            }

            return -1;
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

        private sealed class XamlDocument
        {
            private readonly string? _localPath;
            private readonly Uri? _remoteUri;
            private readonly SourceOrigin _origin;

            public XamlDocument(SourceInfo baseInfo, XamlNode root)
            {
                _localPath = baseInfo.LocalPath;
                _remoteUri = baseInfo.RemoteUri;
                _origin = baseInfo.Origin;
                Root = root;
            }

            public XamlNode Root { get; }

            public XamlNode? FindNode(IReadOnlyList<int> path)
            {
                var current = Root;
                foreach (var index in path)
                {
                    if (index < 0 || index >= current.Children.Count)
                    {
                        return null;
                    }

                    current = current.Children[index];
                }

                return current;
            }

            public SourceInfo? CreateSourceInfo(XamlNode node)
            {
                if (node.Element is not IXmlLineInfo lineInfo || !lineInfo.HasLineInfo())
                {
                    return null;
                }

                return new SourceInfo(
                    LocalPath: _localPath,
                    RemoteUri: _remoteUri,
                    StartLine: lineInfo.LineNumber,
                    StartColumn: lineInfo.LinePosition,
                    EndLine: null,
                    EndColumn: null,
                    Origin: DetermineOrigin());
            }

            private SourceOrigin DetermineOrigin()
            {
                if (!string.IsNullOrEmpty(_localPath))
                {
                    return SourceOrigin.Local;
                }

                if (_remoteUri is not null)
                {
                    return SourceOrigin.SourceLink;
                }

                return _origin;
            }
        }

        private sealed class XamlNode
        {
            public XamlNode(XElement element, IReadOnlyList<XamlNode> children)
            {
                Element = element;
                Children = children;
            }

            public XElement Element { get; }

            public IReadOnlyList<XamlNode> Children { get; }
        }
    }
}
