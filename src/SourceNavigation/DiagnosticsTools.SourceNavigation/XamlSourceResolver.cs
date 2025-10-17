using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Avalonia.Diagnostics.SourceNavigation
{
    /// <summary>
    /// Provides helpers for mapping Avalonia logical tree elements back to their originating XAML documents.
    /// </summary>
    public sealed class XamlSourceResolver
    {
        private readonly ILogicalTreePathBuilder _pathBuilder;
        private readonly IXamlDocumentLocator _documentLocator;
        private readonly Func<Type, CancellationToken, ValueTask<SourceInfo?>> _rootSourceResolver;
        private readonly ConcurrentDictionary<Type, XamlDocumentCacheEntry> _documentCache = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="XamlSourceResolver"/> class.
        /// </summary>
        /// <param name="pathBuilder">Logical tree path builder implementation.</param>
        /// <param name="documentLocator">Document locator capable of retrieving XAML documents.</param>
        /// <param name="rootSourceResolver">Delegate used to resolve the XAML root type to a <see cref="SourceInfo"/>.</param>
        public XamlSourceResolver(
            ILogicalTreePathBuilder pathBuilder,
            IXamlDocumentLocator documentLocator,
            Func<Type, CancellationToken, ValueTask<SourceInfo?>> rootSourceResolver)
        {
            _pathBuilder = pathBuilder ?? throw new ArgumentNullException(nameof(pathBuilder));
            _documentLocator = documentLocator ?? throw new ArgumentNullException(nameof(documentLocator));
            _rootSourceResolver = rootSourceResolver ?? throw new ArgumentNullException(nameof(rootSourceResolver));
        }

        /// <summary>
        /// Attempts to resolve a logical tree node to its XAML source location.
        /// </summary>
        /// <param name="node">The node to resolve.</param>
        /// <param name="cancellationToken">Cancellation token for asynchronous work.</param>
        public async ValueTask<SourceInfo?> TryResolveAsync(object node, CancellationToken cancellationToken = default)
        {
            if (!_pathBuilder.TryBuildPath(node, out var root, out var path))
            {
                return null;
            }

            if (root is null)
            {
                return null;
            }

            var rootType = root as Type ?? root.GetType();

            var rootSource = await _rootSourceResolver(rootType, cancellationToken).ConfigureAwait(false);
            if (rootSource is null)
            {
                return null;
            }

            var document = await GetXamlDocumentAsync(rootType, rootSource, cancellationToken).ConfigureAwait(false);
            if (document is null)
            {
                return null;
            }

            if (!document.TryFindNode(path, out var xamlNode))
            {
                return null;
            }

            return document.CreateSourceInfo(xamlNode);
        }

        private async ValueTask<XamlDocument?> GetXamlDocumentAsync(Type rootType, SourceInfo rootSource, CancellationToken cancellationToken)
        {
            var cacheEntry = _documentCache.GetOrAdd(rootType, _ => new XamlDocumentCacheEntry());
            return await cacheEntry.GetOrCreateAsync(
                rootSource,
                (ct) => BuildXamlDocumentAsync(rootType, rootSource, ct),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<XamlDocument?> BuildXamlDocumentAsync(Type rootType, SourceInfo rootSource, CancellationToken cancellationToken)
        {
            var request = new XamlDocumentRequest(rootType, rootSource);
            var documentResult = await _documentLocator.GetDocumentAsync(request, cancellationToken).ConfigureAwait(false);
            if (documentResult is null)
            {
                return null;
            }

            if (documentResult.Document.Root is null)
            {
                return null;
            }

            var rootNode = BuildXamlNode(documentResult.Document.Root);
            return new XamlDocument(documentResult.Source, rootNode);
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

        private sealed class XamlDocumentCacheEntry
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private SourceInfo? _source;
            private XamlDocument? _document;

            public async ValueTask<XamlDocument?> GetOrCreateAsync(
                SourceInfo requestedSource,
                Func<CancellationToken, Task<XamlDocument?>> factory,
                CancellationToken cancellationToken)
            {
                if (_document is not null && _source == requestedSource)
                {
                    return _document;
                }

                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_document is not null && _source == requestedSource)
                    {
                        return _document;
                    }

                    var created = await factory(cancellationToken).ConfigureAwait(false);
                    _document = created;
                    _source = created is null ? null : requestedSource;
                    return created;
                }
                finally
                {
                    _gate.Release();
                }
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

            public bool TryFindNode(IReadOnlyList<int> path, out XamlNode node)
            {
                var current = Root;
                foreach (var index in path)
                {
                    if (index < 0 || index >= current.Children.Count)
                    {
                        node = default!;
                        return false;
                    }

                    current = current.Children[index];
                }

                node = current;
                return true;
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
