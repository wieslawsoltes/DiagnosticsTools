using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics.Controls;
using Avalonia.Diagnostics;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.Services;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Threading.Tasks;
using System.Text;
using System.Net.Http;
using System.Security.Cryptography;

namespace Avalonia.Diagnostics.ViewModels
{
    public class TreePageViewModel : ViewModelBase, IDisposable
    {
        private TreeNode? _selectedNode;
        private ControlDetailsViewModel? _details;
        private TreeNode[] _nodes;
        private readonly TreeNode[] _rootNodes;
        private readonly Dictionary<XamlAstNodeId, TreeNode> _nodesByXamlId = new();
        private readonly HashSet<TreeNode> _trackedNodes = new();
        private readonly XamlAstWorkspace _xamlAstWorkspace;
        private readonly RuntimeMutationCoordinator _runtimeCoordinator;
        private readonly ITemplateSourceResolver? _templateSourceResolver;
        private readonly ITemplateOverrideService? _templateOverrideService;
        private TreeNode? _scopedRoot;
        private string? _scopedNodeKey;
        private bool _isScoped;
        private readonly ISet<string> _pinnedProperties;
        private TreeSearchField _selectedTreeSearchField = TreeSearchField.TypeName;
        private readonly ConcurrentDictionary<TreeNode, Task<SourceInfo?>> _sourceInfoCache = new();
        private ISourceInfoService _sourceInfoService;
        private ISourceNavigator _sourceNavigator;
        private SourceInfo? _selectedNodeSourceInfo;
        private XamlAstSelection? _selectedNodeXaml;
        private readonly SelectionCoordinator _selectionCoordinator;
        private readonly SelectionOverlayService _selectionOverlay;
        private readonly LayoutHandleService _layoutHandles;
        private readonly string _selectionOwnerId;
        private readonly string _previewOwnerId;
        private readonly List<WeakReference<SourcePreviewViewModel>> _previewObservers = new();
        private readonly object _previewObserversGate = new();
        private bool _suppressPreviewNavigation;
        private int _selectedNodeRevision;
        private int _previewNavigationRevision;
        private readonly DelegateCommand _previewSourceCommand;
        private readonly DelegateCommand _navigateToSourceCommand;
        private readonly DelegateCommand _deleteNodeCommand;
        private const string TreeInspectorName = "TreeView";
        private const string TreeDeleteGesture = "DeleteNode";
        private const string DefaultEncoding = "utf-8";
        private const string DocumentModeWritable = "Writable";
        private const string TreeContextProperty = "TreeView.Node";
        private const string TreeContextFrame = "Tree";
        private const string TreeContextValueSource = "Tree";
        private int _xamlSelectionRevision;
        private PropertyInspectorChangeEmitter? _changeEmitter;
        private readonly ObservableCollection<TreeNode> _multiSelection = new();
        private readonly DelegateCommand _addToMultiSelectionCommand;
        private readonly DelegateCommand _removeFromMultiSelectionCommand;
        private readonly DelegateCommand _clearMultiSelectionCommand;
        private readonly DelegateCommand _editTemplateCommand;
        private readonly ConcurrentDictionary<string, DocumentNodeIndex> _documentNodeIndices = new(PathComparer);
        private readonly ConcurrentDictionary<string, Task<string?>> _remoteDocumentCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _remoteDocumentPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _cachedDocumentRemoteIndex = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient RemoteDocumentClient = new();
        private readonly Dictionary<XamlAstNodeId, string> _descriptorDocuments = new();
        private int _totalNodeCount;
        private int _expandedNodeCount;
        internal enum SubtreePasteMode
        {
            Child,
            Sibling
        }

        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public TreePageViewModel(
            MainViewModel mainView,
            TreeNode[] nodes,
            ISet<string> pinnedProperties,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator,
            XamlAstWorkspace xamlAstWorkspace,
            RuntimeMutationCoordinator runtimeCoordinator,
            ITemplateSourceResolver? templateSourceResolver,
            ITemplateOverrideService? templateOverrideService,
            SelectionCoordinator selectionCoordinator,
            string selectionOwnerId)
        {
            MainView = mainView;
            _rootNodes = nodes;
            _nodes = nodes;
            _pinnedProperties = pinnedProperties;
            _sourceInfoService = sourceInfoService ?? throw new ArgumentNullException(nameof(sourceInfoService));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            _xamlAstWorkspace = xamlAstWorkspace ?? throw new ArgumentNullException(nameof(xamlAstWorkspace));
            _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
            _templateSourceResolver = templateSourceResolver;
            _templateOverrideService = templateOverrideService;
            _selectionCoordinator = selectionCoordinator ?? throw new ArgumentNullException(nameof(selectionCoordinator));
            _selectionOverlay = new SelectionOverlayService(mainView);
            _layoutHandles = new LayoutHandleService(runtimeCoordinator, () => _changeEmitter);
            _selectionOwnerId = string.IsNullOrWhiteSpace(selectionOwnerId) ? throw new ArgumentException("Selection owner id must not be null or whitespace.", nameof(selectionOwnerId)) : selectionOwnerId;
            _previewOwnerId = selectionOwnerId + ".Preview";
            _changeEmitter = null;
            _xamlAstWorkspace.DocumentChanged += OnXamlDocumentChanged;
            _xamlAstWorkspace.NodesChanged += OnXamlNodesChanged;
            PropertiesFilter = new FilterViewModel();
            PropertiesFilter.RefreshFilter += (s, e) => Details?.PropertiesView?.Refresh();

            SettersFilter = new FilterViewModel();
            SettersFilter.RefreshFilter += (s, e) => Details?.UpdateStyleFilters();

            TreeFilter = new FilterViewModel();
            TreeFilter.RefreshFilter += (s, e) => ApplyTreeFilter();

            _previewSourceCommand = new DelegateCommand(() => PreviewSourceInternalAsync(forceSplitView: false), () => CanPreviewSource);
            _navigateToSourceCommand = new DelegateCommand(NavigateToSourceAsync, () => CanNavigateToSource);
            _deleteNodeCommand = new DelegateCommand(DeleteNodeAsync, () => CanDeleteNode);
            _addToMultiSelectionCommand = new DelegateCommand(AddSelectedNodeToMultiSelection, () => CanAddToMultiSelection);
            _removeFromMultiSelectionCommand = new DelegateCommand(RemoveSelectedNodeFromMultiSelection, () => CanRemoveFromMultiSelection);
            _clearMultiSelectionCommand = new DelegateCommand(ClearMultiSelection, () => HasMultiSelection);
            _editTemplateCommand = new DelegateCommand(EditTemplateAsync, () => CanEditTemplate);
            RebuildNodeSubscriptions();
            UpdateTreeStats();
        }

    public event EventHandler<string>? ClipboardCopyRequested;
    public event EventHandler<SourcePreviewViewModel>? SourcePreviewRequested;

        public MainViewModel MainView { get; }

        public FilterViewModel PropertiesFilter { get; }

        public FilterViewModel SettersFilter { get; }

        public FilterViewModel TreeFilter { get; }

        public TreeNode[] Nodes
        {
            get => _nodes;
            protected set
            {
                if (RaiseAndSetIfChanged(ref _nodes, value))
                {
                    RebuildNodeSubscriptions();
                    UpdateTreeStats();
                }
            }
        }

        public int TotalNodeCount
        {
            get => _totalNodeCount;
            private set => RaiseAndSetIfChanged(ref _totalNodeCount, value);
        }

        public int ExpandedNodeCount
        {
            get => _expandedNodeCount;
            private set => RaiseAndSetIfChanged(ref _expandedNodeCount, value);
        }

        public IReadOnlyList<TreeNode> MultiSelection => _multiSelection;

        public bool HasMultiSelection => _multiSelection.Count > 0;

        public bool CanAddToMultiSelection => SelectedNode is not null && !_multiSelection.Contains(SelectedNode);

        public bool CanRemoveFromMultiSelection => SelectedNode is not null && _multiSelection.Contains(SelectedNode);

        public bool CanEditTemplate => SelectedNode is CombinedTreeNode templateNode && templateNode.Role == CombinedTreeNode.CombinedNodeRole.Template;

        public bool CanCopySubtree => SelectedNodeXaml?.Node is not null;

        public bool CanPasteSubtree => SelectedNodeXaml?.Node is not null;

        public ICommand AddToMultiSelectionCommand => _addToMultiSelectionCommand;

        public ICommand RemoveFromMultiSelectionCommand => _removeFromMultiSelectionCommand;

        public ICommand ClearMultiSelectionCommand => _clearMultiSelectionCommand;

        public ICommand EditTemplateCommand => _editTemplateCommand;

        public TreeSearchField SelectedTreeSearchField
        {
            get => _selectedTreeSearchField;
            set
            {
                if (RaiseAndSetIfChanged(ref _selectedTreeSearchField, value))
                {
                    ApplyTreeFilter();
                }
            }
        }

        private TreeNode? ScopedNode
        {
            get => _scopedRoot;
            set => RaiseAndSetIfChanged(ref _scopedRoot, value);
        }

        public string? ScopedNodeKey
        {
            get => _scopedNodeKey;
            private set => RaiseAndSetIfChanged(ref _scopedNodeKey, value);
        }

        public TreeNode? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (RaiseAndSetIfChanged(ref _selectedNode, value))
                {
                    unchecked
                    {
                        _selectedNodeRevision++;
                    }
                    Details = value != null ?
                        new ControlDetailsViewModel(this, value.Visual, _pinnedProperties, _sourceInfoService, _sourceNavigator, _runtimeCoordinator, _templateSourceResolver, _templateOverrideService) :
                        null;
                    _details?.AttachChangeEmitter(_changeEmitter);
                    Details?.UpdatePropertiesView(MainView.ShowImplementedInterfaces);
                    Details?.UpdateStyleFilters();
                    Details?.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);
                    RaisePropertyChanged(nameof(CanScopeToSubTree));
                    RaisePropertyChanged(nameof(CanNavigateToSource));
                    RaisePropertyChanged(nameof(CanPreviewSource));
                    RaisePropertyChanged(nameof(CanAddToMultiSelection));
                    RaisePropertyChanged(nameof(CanRemoveFromMultiSelection));
                    RaisePropertyChanged(nameof(CanEditTemplate));
                    RaisePropertyChanged(nameof(CanCopySubtree));
                    RaisePropertyChanged(nameof(CanPasteSubtree));
                    UpdateCommandStates();
                    SelectedNodeSourceInfo = null;
                    SelectedNodeXaml = null;
                    _ = UpdateSelectedNodeSourceInfoAsync(value);
                    _layoutHandles.UpdateSelection(value, null);
                }
            }
        }

        public SourceInfo? SelectedNodeSourceInfo
        {
            get => _selectedNodeSourceInfo;
            private set
            {
                if (RaiseAndSetIfChanged(ref _selectedNodeSourceInfo, value))
                {
                    RaisePropertyChanged(nameof(SelectedNodeSourceSummary));
                    RaisePropertyChanged(nameof(HasSelectedNodeSource));
                    RaisePropertyChanged(nameof(CanNavigateToSource));
                    RaisePropertyChanged(nameof(CanPreviewSource));
                    RaisePropertyChanged(nameof(CanCopySubtree));
                    RaisePropertyChanged(nameof(CanPasteSubtree));
                    UpdateCommandStates();
                }
            }
        }

        public string? SelectedNodeSourceSummary => SelectedNodeSourceInfo?.DisplayPath;

        public bool HasSelectedNodeSource => SelectedNodeSourceInfo is not null;

        public bool CanNavigateToSource => HasSelectedNodeSource;

        public bool CanPreviewSource => HasSelectedNodeSource || SelectedNodeXaml?.Document is not null;

        public bool CanDeleteNode
        {
            get
            {
                if (_changeEmitter?.MutationDispatcher is null)
                {
                    return false;
                }

                if (SelectedNode is null)
                {
                    return false;
                }

                if (SelectedNodeXaml is not { Node: { } } selection)
                {
                    return false;
                }

                if (FindParentDescriptor(selection) is null)
                {
                    return false;
                }

                return !string.IsNullOrWhiteSpace(selection.Document?.Path);
            }
        }

        public XamlAstSelection? SelectedNodeXaml
        {
            get => _selectedNodeXaml;
            private set
            {
                if (RaiseAndSetIfChanged(ref _selectedNodeXaml, value))
                {
                    NotifyPreviewSelectionChanged(value);
                    RaisePropertyChanged(nameof(CanDeleteNode));
                    RaisePropertyChanged(nameof(CanCopySubtree));
                    RaisePropertyChanged(nameof(CanPasteSubtree));
                    RaisePropertyChanged(nameof(CanPreviewSource));
                    UpdateCommandStates();
                    _layoutHandles.UpdateSelection(SelectedNode, value);
                }
            }
        }

        public ICommand PreviewSourceCommand => _previewSourceCommand;

        public ICommand NavigateToSourceCommand => _navigateToSourceCommand;

        public ICommand DeleteNodeCommand => _deleteNodeCommand;

        public ControlDetailsViewModel? Details
        {
            get => _details;
            private set
            {
                var oldValue = _details;

                if (RaiseAndSetIfChanged(ref _details, value))
                {
                    if (oldValue is not null)
                    {
                        oldValue.SourcePreviewRequested -= OnDetailsSourcePreviewRequested;
                        oldValue.Dispose();
                    }

                    if (value is not null)
                    {
                        value.SourcePreviewRequested += OnDetailsSourcePreviewRequested;
                    }
                }
            }
        }

        internal void AttachChangeEmitter(PropertyInspectorChangeEmitter? changeEmitter)
        {
            _changeEmitter = changeEmitter;
            _details?.AttachChangeEmitter(changeEmitter);
            RaisePropertyChanged(nameof(CanDeleteNode));
            UpdateCommandStates();
        }

        internal void NotifyMutationCompleted(MutationCompletedEventArgs args)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => NotifyMutationCompleted(args), DispatcherPriority.Background);
                return;
            }

            if (_details is null)
            {
                return;
            }

            if (args.Result.Status == ChangeDispatchStatus.Success)
            {
                _details.HandleMutationSuccess(args);
            }
            else
            {
                _details.HandleMutationFailure(args);
            }

            _ = UpdateSelectedNodeSourceInfoAsync(SelectedNode);
        }

        internal void HandleExternalDocumentChanged(ExternalDocumentChangedEventArgs args)
        {
            if (args is null)
            {
                return;
            }

            if (SelectedNode is null)
            {
                return;
            }

            var currentPath = ResolveSelectedDocumentPath();
            if (!string.IsNullOrWhiteSpace(currentPath) &&
                args.Path is { } path &&
                !PathsEqual(currentPath!, path))
            {
                return;
            }

            _ = UpdateSelectedNodeSourceInfoAsync(SelectedNode);
            Details?.HandleExternalDocumentChanged(args);
        }

        public void Dispose()
        {
            ClearMultiSelection();
            _selectionOverlay.Dispose();
            _layoutHandles.Dispose();
            ClearNodeSubscriptions();

            foreach (var node in Nodes)
            {
                node.Dispose();
            }

            _xamlAstWorkspace.DocumentChanged -= OnXamlDocumentChanged;
            _xamlAstWorkspace.NodesChanged -= OnXamlNodesChanged;

            if (_details is not null)
            {
                _details.SourcePreviewRequested -= OnDetailsSourcePreviewRequested;
                _details.Dispose();
            }

            lock (_previewObserversGate)
            {
                foreach (var reference in _previewObservers)
                {
                    if (reference.TryGetTarget(out var target) && target is not null)
                    {
                        target.DetachFromWorkspace();
                        target.DetachFromMutationOwner();
                    }
                }

                _previewObservers.Clear();
            }

            _documentNodeIndices.Clear();
            _remoteDocumentPaths.Clear();
            _cachedDocumentRemoteIndex.Clear();
            _remoteDocumentCache.Clear();
        }

        public async void NavigateToSource()
        {
            await NavigateToSourceAsync().ConfigureAwait(false);
        }

        private async Task NavigateToSourceAsync()
        {
            var node = SelectedNode;
            if (node is null)
            {
                return;
            }

            try
            {
                var info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);
                if (info is null)
                {
                    return;
                }

                await _sourceNavigator.NavigateAsync(info).ConfigureAwait(false);
            }
            catch
            {
                // Navigation is best-effort.
            }
        }

        public async void PreviewSource()
        {
            await PreviewSourceInternalAsync(forceSplitView: false).ConfigureAwait(false);
        }

        private async Task PreviewSourceInternalAsync(bool forceSplitView)
        {
            var node = SelectedNode;
            if (node is null)
            {
                return;
            }

            try
            {
                var info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var context = node.Visual?.GetType().Name ?? node.Type;
                    var preview = info is not null
                        ? new SourcePreviewViewModel(info, _sourceNavigator, SelectedNodeXaml, NavigateToAstNode, SynchronizeSelectionFromPreview, mutationOwner: MainView, xamlAstWorkspace: _xamlAstWorkspace, selectionCoordinator: _selectionCoordinator, selectionOwnerId: _previewOwnerId)
                        : SourcePreviewViewModel.CreateUnavailable(context, _sourceNavigator, mutationOwner: MainView, selectionCoordinator: _selectionCoordinator, selectionOwnerId: _previewOwnerId);
                    if (info is not null)
                    {
                        RegisterPreview(preview);
                    }
                    var runtimePreview = BuildRuntimeSnapshot(node);
                    if (runtimePreview is not null)
                    {
                        preview.RuntimeComparison = runtimePreview;
                    }

                    if (forceSplitView && preview.RuntimeComparison is not null)
                    {
                        preview.IsSplitViewEnabled = true;
                    }
                    SourcePreviewRequested?.Invoke(this, preview);
                });
            }
            catch
            {
                // Preview is best-effort.
            }
        }

        private async Task EditTemplateAsync()
        {
            if (!CanEditTemplate)
            {
                return;
            }

            await PreviewSourceInternalAsync(forceSplitView: true).ConfigureAwait(false);
        }

        private async Task DeleteNodeAsync()
        {
            if (!CanDeleteNode)
            {
                return;
            }

            var selection = SelectedNodeXaml;
            var runtimeNode = SelectedNode;

            if (selection is null || selection.Node is null || runtimeNode is null)
            {
                return;
            }

            if (_changeEmitter?.MutationDispatcher is not { } dispatcher)
            {
                return;
            }

            RemoveFromMultiSelection(runtimeNode);

            var parentDescriptor = FindParentDescriptor(selection);
            if (parentDescriptor is null)
            {
                return;
            }

            var envelope = BuildRemoveNodeEnvelope(selection, parentDescriptor, runtimeNode);
            var result = await dispatcher.DispatchAsync(envelope, CancellationToken.None).ConfigureAwait(false);
            if (result.Status != ChangeDispatchStatus.Success)
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_runtimeCoordinator.TryApplyElementRemoval(runtimeNode))
                {
                    SelectedNode = runtimeNode.Parent;
                }
            }, DispatcherPriority.Background);
        }

        private SourcePreviewViewModel? BuildRuntimeSnapshot(TreeNode node)
        {
            var details = Details;
            if (details is null)
            {
                return null;
            }

            var snippet = CreateRuntimeSnapshotSnippet(node, details);
            if (string.IsNullOrEmpty(snippet))
            {
                return null;
            }

            var runtimeInfo = new SourceInfo("Runtime Snapshot", null, null, null, null, null, SourceOrigin.Generated);
            var runtimePreview = new SourcePreviewViewModel(runtimeInfo, _sourceNavigator, mutationOwner: MainView);
            runtimePreview.SetManualSnippet(snippet!);
            return runtimePreview;
        }

        internal void RegisterPreview(SourcePreviewViewModel preview)
        {
            if (preview is null)
            {
                return;
            }

            lock (_previewObserversGate)
            {
                CleanupPreviewObservers_NoLock();
                _previewObservers.Add(new WeakReference<SourcePreviewViewModel>(preview));
            }

            preview.UpdateSelectionFromTree(SelectedNodeXaml);
        }

        private void NotifyPreviewSelectionChanged(XamlAstSelection? selection)
        {
            _selectionOverlay.OnCoordinatorSelection(SelectedNode);

            IDisposable? publishToken = null;
            try
            {
                if (!_selectionCoordinator.TryBeginPublish(_selectionOwnerId, selection, out publishToken, out var changed))
                {
                    return;
                }

                if (!changed)
                {
                    return;
                }

            SourcePreviewViewModel[]? snapshot = null;

            lock (_previewObserversGate)
            {
                if (_previewObservers.Count == 0)
                {
                    return;
                }

                CleanupPreviewObservers_NoLock();

                if (_previewObservers.Count == 0)
                {
                    return;
                }

                var list = new List<SourcePreviewViewModel>(_previewObservers.Count);
                foreach (var reference in _previewObservers)
                {
                    if (reference.TryGetTarget(out var target) && target is not null)
                    {
                        list.Add(target);
                    }
                }

                if (list.Count == 0)
                {
                    return;
                }

                snapshot = list.ToArray();
            }

            if (snapshot is null)
            {
                return;
            }

            foreach (var preview in snapshot)
            {
                preview.UpdateSelectionFromTree(selection);
            }
            }
            finally
            {
                publishToken?.Dispose();
            }
        }

        private void CleanupPreviewObservers_NoLock(SourcePreviewViewModel? instance = null)
        {
            for (var index = _previewObservers.Count - 1; index >= 0; index--)
            {
                if (!_previewObservers[index].TryGetTarget(out var target) ||
                    target is null ||
                    (instance is not null && ReferenceEquals(target, instance)))
                {
                    _previewObservers.RemoveAt(index);
                }
            }
        }

        private ChangeEnvelope BuildRemoveNodeEnvelope(XamlAstSelection selection, XamlAstNodeDescriptor parentDescriptor, TreeNode runtimeNode)
        {
            if (selection.Document is null)
            {
                throw new InvalidOperationException("XAML document is unavailable.");
            }

            if (selection.Node is null)
            {
                throw new InvalidOperationException("XAML descriptor is unavailable.");
            }

            var document = selection.Document;
            var descriptor = selection.Node;
            var elementId = BuildRuntimeElementId(runtimeNode.Visual);
            var spanHash = XamlGuardUtilities.ComputeNodeHash(document, descriptor);
            var parentHash = XamlGuardUtilities.ComputeNodeHash(document, parentDescriptor);

            var operation = new ChangeOperation
            {
                Id = "op-1",
                Type = ChangeOperationTypes.RemoveNode,
                Target = new ChangeTarget
                {
                    DescriptorId = descriptor.Id.ToString(),
                    NodeType = "Element"
                },
                Guard = new ChangeOperationGuard
                {
                    SpanHash = spanHash,
                    ParentSpanHash = parentHash
                }
            };

            return new ChangeEnvelope
            {
                BatchId = Guid.NewGuid(),
                InitiatedAt = DateTimeOffset.UtcNow,
                Source = new ChangeSourceInfo
                {
                    Inspector = TreeInspectorName,
                    Gesture = TreeDeleteGesture
                },
                Document = new ChangeDocumentInfo
                {
                    Path = document.Path,
                    Encoding = DefaultEncoding,
                    Version = document.Version.ToString(),
                    Mode = DocumentModeWritable
                },
                Context = new ChangeContextInfo
                {
                    ElementId = elementId,
                    AstNodeId = descriptor.Id.ToString(),
                    Property = TreeContextProperty,
                    Frame = TreeContextFrame,
                    ValueSource = TreeContextValueSource
                },
                Guards = new ChangeGuardsInfo
                {
                    DocumentVersion = document.Version.ToString(),
                    RuntimeFingerprint = elementId
                },
                Changes = new[] { operation }
            };
        }

        internal async Task<(IReadOnlyList<PropertyChangeContext> Contexts, IReadOnlyList<object?> PreviousValues, IReadOnlyList<TreeNode> Nodes)> BuildAdditionalMutationContextsAsync(AvaloniaProperty property)
        {
            if (property is null)
            {
                throw new ArgumentNullException(nameof(property));
            }

            if (_multiSelection.Count == 0)
            {
                return (Array.Empty<PropertyChangeContext>(), Array.Empty<object?>(), Array.Empty<TreeNode>());
            }

            var contexts = new List<PropertyChangeContext>(_multiSelection.Count);
            var previousValues = new List<object?>(_multiSelection.Count);
            var nodes = new List<TreeNode>(_multiSelection.Count);

            var primaryDocumentPath = SelectedNodeXaml?.Document?.Path;

            foreach (var candidate in _multiSelection)
            {
                if (SelectedNode is not null && ReferenceEquals(candidate, SelectedNode))
                {
                    continue;
                }

                var sourceInfo = await EnsureSourceInfoAsync(candidate).ConfigureAwait(false);
                var selection = await BuildXamlSelectionAsync(candidate, sourceInfo).ConfigureAwait(false);

                if (selection?.Document is null || selection.Node is null)
                {
                    continue;
                }

                if (primaryDocumentPath is not null && !PathsEqual(primaryDocumentPath, selection.Document.Path))
                {
                    continue;
                }

                var context = new PropertyChangeContext(
                    candidate.Visual,
                    property,
                    selection.Document,
                    selection.Node,
                    frame: "LocalValue",
                    valueSource: "LocalValue");

                contexts.Add(context);
                previousValues.Add(candidate.Visual.GetValue(property));
                nodes.Add(candidate);
            }

            if (contexts.Count == 0)
            {
                return (Array.Empty<PropertyChangeContext>(), Array.Empty<object?>(), Array.Empty<TreeNode>());
            }

            return (contexts, previousValues, nodes);
        }

        internal async Task<string?> TryGetSelectedSubtreeXamlAsync()
        {
            var node = SelectedNode;
            if (node is null)
            {
                return null;
            }

            var selection = SelectedNodeXaml;
            if (selection?.Document is null || selection.Node is null)
            {
                var info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);
                selection = await BuildXamlSelectionAsync(node, info).ConfigureAwait(false);
                if (selection is null)
                {
                    return null;
                }

                SelectedNodeXaml = selection;
            }

            if (selection.Document is null || selection.Node is null)
            {
                return null;
            }

            return selection.Document.Text.Substring(selection.Node.Span.Start, selection.Node.Span.Length);
        }

        internal async Task<bool> PasteSubtreeAsync(string? serialized, SubtreePasteMode mode)
        {
            if (string.IsNullOrWhiteSpace(serialized))
            {
                return false;
            }

            var node = SelectedNode;
            if (node is null)
            {
                return false;
            }

            var selection = SelectedNodeXaml;
            if (selection?.Document is null || selection.Node is null)
            {
                var info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);
                selection = await BuildXamlSelectionAsync(node, info).ConfigureAwait(false);
                if (selection is null)
                {
                    return false;
                }

                SelectedNodeXaml = selection;
            }

            if (selection.Document is null || selection.Node is null)
            {
                return false;
            }

            var document = selection.Document;
            var descriptor = selection.Node;
            var documentPath = document.Path;
            if (string.IsNullOrWhiteSpace(documentPath))
            {
                return false;
            }

            var index = await _xamlAstWorkspace.GetIndexAsync(documentPath).ConfigureAwait(false);
            var insertionTarget = mode == SubtreePasteMode.Child ? descriptor : FindParentDescriptor(selection);
            if (insertionTarget is null)
            {
                return false;
            }

            var operationTarget = insertionTarget;
            var insertionIndex = mode == SubtreePasteMode.Child
                ? CountChildElements(index, descriptor)
                : DetermineSiblingInsertionIndex(descriptor);

            if (insertionIndex < 0)
            {
                return false;
            }

            var guardTarget = mode == SubtreePasteMode.Child ? descriptor : insertionTarget;
            var spanHash = XamlGuardUtilities.ComputeNodeHash(document, guardTarget);

            var payload = new ChangePayload
            {
                Serialized = serialized,
                InsertionIndex = insertionIndex,
                SurroundingWhitespace = DetermineLineEnding(document.Text)
            };

            var operation = new ChangeOperation
            {
                Id = "op-1",
                Type = ChangeOperationTypes.UpsertElement,
                Target = new ChangeTarget
                {
                    DescriptorId = operationTarget.Id.ToString(),
                    Path = operationTarget.LocalName,
                    NodeType = "Element"
                },
                Payload = payload,
                Guard = new ChangeOperationGuard
                {
                    SpanHash = spanHash,
                    ParentSpanHash = mode == SubtreePasteMode.Sibling ? XamlGuardUtilities.ComputeNodeHash(document, insertionTarget) : null
                }
            };

            var elementId = BuildRuntimeElementId(node.Visual);

            var envelope = new ChangeEnvelope
            {
                BatchId = Guid.NewGuid(),
                InitiatedAt = DateTimeOffset.UtcNow,
                Source = new ChangeSourceInfo
                {
                    Inspector = TreeInspectorName,
                    Gesture = mode == SubtreePasteMode.Child ? "PasteSubtreeChild" : "PasteSubtreeSibling"
                },
                Document = new ChangeDocumentInfo
                {
                    Path = document.Path,
                    Encoding = DefaultEncoding,
                    Version = document.Version.ToString(),
                    Mode = DocumentModeWritable
                },
                Context = new ChangeContextInfo
                {
                    ElementId = elementId,
                    AstNodeId = descriptor.Id.ToString(),
                    Property = TreeContextProperty,
                    Frame = TreeContextFrame,
                    ValueSource = TreeContextValueSource
                },
                Guards = new ChangeGuardsInfo
                {
                    DocumentVersion = document.Version.ToString(),
                    RuntimeFingerprint = elementId
                },
                Changes = new[] { operation }
            };

            if (_changeEmitter?.MutationDispatcher is not { } dispatcher)
            {
                return false;
            }

            var result = await dispatcher.DispatchAsync(envelope).ConfigureAwait(false);
            return result.Status == ChangeDispatchStatus.Success;
        }

        private static string BuildRuntimeElementId(AvaloniaObject? element)
        {
            if (element is null)
            {
                return string.Empty;
            }

            return $"runtime://avalonia-object/{RuntimeHelpers.GetHashCode(element)}";
        }

        private static int CountChildElements(IXamlAstIndex index, XamlAstNodeDescriptor parent)
        {
            var parentPath = parent.Path;
            var expectedDepth = parentPath.Count + 1;
            var count = 0;

            foreach (var node in index.Nodes)
            {
                if (node.Path.Count == expectedDepth && PathStartsWith(node.Path, parentPath))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool PathStartsWith(IReadOnlyList<int> path, IReadOnlyList<int> prefix)
        {
            if (path.Count < prefix.Count)
            {
                return false;
            }

            for (var index = 0; index < prefix.Count; index++)
            {
                if (path[index] != prefix[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static int DetermineSiblingInsertionIndex(XamlAstNodeDescriptor descriptor)
        {
            var path = descriptor.Path;
            if (path.Count == 0)
            {
                return 0;
            }

            var currentIndex = path[path.Count - 1];
            return currentIndex + 1;
        }

        private static string DetermineLineEnding(string text)
        {
            var index = text.IndexOf('\n');
            if (index < 0)
            {
                return Environment.NewLine;
            }

            if (index > 0 && text[index - 1] == '\r')
            {
                return "\r\n";
            }

            return "\n";
        }

        private static string? CreateRuntimeSnapshotSnippet(TreeNode node, ControlDetailsViewModel details)
        {
            if (details.AppliedFrames.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.Append("Element: ");
            builder.Append(node.Type);
            if (!string.IsNullOrWhiteSpace(node.ElementName))
            {
                builder.Append(" \"");
                builder.Append(node.ElementName);
                builder.Append('"');
            }
            builder.AppendLine();

            if (node.Visual is { } visual)
            {
                builder.AppendLine($"Runtime type: {visual.GetType().FullName}");

                if (visual is Control control && control.DataContext is { } dataContext)
                {
                    builder.AppendLine($"DataContext: {FormatSimpleValue(dataContext)}");
                }
            }

            builder.AppendLine();

            var hasContent = false;

            foreach (var frame in details.AppliedFrames)
            {
                var relevantSetters = frame.Setters.Where(s => s.IsVisible || s.IsActive).ToList();
                if (!frame.IsActive && relevantSetters.Count == 0)
                {
                    continue;
                }

                hasContent = true;

                var description = string.IsNullOrWhiteSpace(frame.Description) ? "Values" : frame.Description!;
                builder.Append("Frame: ");
                builder.Append(description);
                builder.Append(frame.IsActive ? " (active)" : " (inactive)");
                if (!frame.IsVisible)
                {
                    builder.Append(" [filtered]");
                }
                builder.AppendLine();

                if (relevantSetters.Count == 0)
                {
                    builder.AppendLine("  (no visible setters)");
                }
                else
                {
                    foreach (var setter in relevantSetters)
                    {
                        var indicator = setter.IsActive ? '*' : '-';
                        builder.Append("  ");
                        builder.Append(indicator);
                        builder.Append(' ');
                        builder.Append(setter.Name);
                        builder.Append(" = ");
                        builder.Append(FormatSetterValue(setter));
                        builder.AppendLine();
                    }
                }

                builder.AppendLine();
            }

            if (!hasContent)
            {
                return null;
            }

            return builder.ToString().TrimEnd();
        }

        private static string FormatSetterValue(SetterViewModel setter)
        {
            switch (setter)
            {
                case BindingSetterViewModel bindingSetter:
                    return $"{bindingSetter.ValueTypeTooltip}: {bindingSetter.Path}";
                case ResourceSetterViewModel resourceSetter:
                    return $"{resourceSetter.ValueTypeTooltip}: {FormatSimpleValue(resourceSetter.Key)} -> {FormatSimpleValue(resourceSetter.Value)}";
                default:
                    return FormatSimpleValue(setter.Value);
            }
        }

        private static string FormatSimpleValue(object? value)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string s:
                    return $"\"{s}\"";
                case double d:
                    return d.ToString("G", CultureInfo.InvariantCulture);
                case float f:
                    return f.ToString("G", CultureInfo.InvariantCulture);
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value?.ToString() ?? string.Empty;
            }
        }

        public void UpdateSourceNavigation(ISourceInfoService sourceInfoService, ISourceNavigator sourceNavigator)
        {
            if (sourceInfoService is null)
            {
                throw new ArgumentNullException(nameof(sourceInfoService));
            }

            if (sourceNavigator is null)
            {
                throw new ArgumentNullException(nameof(sourceNavigator));
            }

            if (!ReferenceEquals(_sourceInfoService, sourceInfoService))
            {
                _sourceInfoCache.Clear();
                _documentNodeIndices.Clear();
                _remoteDocumentPaths.Clear();
                _cachedDocumentRemoteIndex.Clear();
                _remoteDocumentCache.Clear();
                _sourceInfoService = sourceInfoService;
            }

            _sourceNavigator = sourceNavigator;

            Details?.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);

            if (SelectedNode is not null)
            {
                _ = UpdateSelectedNodeSourceInfoAsync(SelectedNode);
            }

            UpdateCommandStates();
        }

        public TreeNode? FindNode(Control control)
        {
            foreach (var node in EnumerateAllNodes())
            {
                if (node.Visual is Control visual && ReferenceEquals(visual, control))
                {
                    return ResolveSelectionTarget(node);
                }
            }

            return null;
        }

        private async Task<SourceInfo?> EnsureSourceInfoAsync(TreeNode node)
        {
            var task = _sourceInfoCache.GetOrAdd(node, ResolveNodeSourceInfoAsync);
            var info = await task.ConfigureAwait(false);
            if (info is not null)
            {
                await IndexNodeSourceInfoAsync(node, info).ConfigureAwait(false);
            }
            else
            {
                await TryEnsureDescriptorWithoutSourceInfoAsync(node).ConfigureAwait(false);

                info = TryCreateSyntheticSourceInfo(node);
                if (info is not null)
                {
                    _sourceInfoCache[node] = Task.FromResult<SourceInfo?>(info);
                    await IndexNodeSourceInfoAsync(node, info).ConfigureAwait(false);
                }
                else
                {
                    RemoveNodeFromSourceIndex(node);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() => node.UpdateSourceInfo(info));
            return info;
        }

        private async Task<SourceInfo?> ResolveNodeSourceInfoAsync(TreeNode node)
        {
            try
            {
                SourceInfo? info = null;

                if (node is CombinedTreeTemplateGroupNode templateGroup)
                {
                    info = await TryResolveControlThemeAsync(templateGroup.Owner.Visual).ConfigureAwait(false);
                    if (info is not null)
                    {
                        return info;
                    }
                }

                info = await _sourceInfoService.GetForAvaloniaObjectAsync(node.Visual).ConfigureAwait(false);

                if (info is null &&
                    node is CombinedTreeNode { Role: CombinedTreeNode.CombinedNodeRole.Template, TemplateOwner: { } templateOwner })
                {
                    info = await TryResolveControlThemeAsync(templateOwner).ConfigureAwait(false);
                }

                if (info is null)
                {
                    info = await _sourceInfoService.GetForMemberAsync(node.Visual.GetType()).ConfigureAwait(false);
                }

                return info;
            }
            catch
            {
                return null;
            }
        }

        private async Task<SourceInfo?> TryResolveControlThemeAsync(AvaloniaObject? owner)
        {
            if (owner is not StyledElement styledElement)
            {
                return null;
            }

            ControlTheme? theme = null;

            try
            {
                theme = styledElement.GetEffectiveTheme();
            }
            catch
            {
                // Ignored – failing to resolve theme should not prevent other lookup paths.
            }

            if (theme is null)
            {
                return null;
            }

            try
            {
                var info = await _sourceInfoService.GetForAvaloniaObjectAsync(theme).ConfigureAwait(false);
                if (info is not null)
                {
                    return info;
                }

                return await _sourceInfoService.GetForValueFrameAsync(theme).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        private async Task UpdateSelectedNodeSourceInfoAsync(TreeNode? node)
        {
            var revision = Interlocked.Increment(ref _xamlSelectionRevision);
            SourceInfo? info = null;
            XamlAstSelection? xamlSelection = null;

            if (node is not null)
            {
                info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);
                xamlSelection = await BuildXamlSelectionAsync(node, info).ConfigureAwait(false);
            }

            var targetNode = node;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (revision == _xamlSelectionRevision)
                {
                    SelectedNodeSourceInfo = info;
                    SelectedNodeXaml = xamlSelection;
                    if (targetNode is not null)
                    {
                        RegisterXamlDescriptor(targetNode, xamlSelection?.Node, xamlSelection?.Document?.Path);
                    }
                }
            });
        }

        public virtual void SelectControl(Control control)
        {
            TreeNode? node = null;
            Control? candidate = control;
            var visited = new HashSet<Control>();

            while (candidate is not null && visited.Add(candidate))
            {
                node = FindNode(candidate);

                if (node is not null)
                {
                    break;
                }

                var templatedParent = candidate.TemplatedParent as Control;
                if (templatedParent is not null && !visited.Contains(templatedParent))
                {
                    candidate = templatedParent;
                    continue;
                }

                candidate = candidate.GetVisualParent<Control>();
            }

            if (node is null)
            {
                return;
            }

            var targetNode = node;
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsNodeInScope(targetNode))
                {
                    ShowFullTree();
                }

                if (!targetNode.IsVisible && !string.IsNullOrWhiteSpace(TreeFilter.FilterString))
                {
                    TreeFilter.FilterString = string.Empty;
                }

                ExpandNode(targetNode.Parent);
                SelectedNode = targetNode;
            }, DispatcherPriority.Background);
        }

        protected virtual TreeNode? ResolveSelectionTarget(TreeNode node) => node;

        public void CopySelector()
        {
            var currentVisual = SelectedNode?.Visual as Visual;
            if (currentVisual is not null)
            {
                var selector = GetVisualSelector(currentVisual);

                ClipboardCopyRequested?.Invoke(this, selector);
            }
        }

        public void CopySelectorFromTemplateParent()
        {
            var parts = new List<string>();

            var currentVisual = SelectedNode?.Visual as Visual;
            while (currentVisual is not null)
            {
                parts.Add(GetVisualSelector(currentVisual));

                currentVisual = currentVisual.TemplatedParent as Visual;
            }

            if (parts.Any())
            {
                parts.Reverse();
                var selector = string.Join(" /template/ ", parts);

                ClipboardCopyRequested?.Invoke(this, selector);
            }
        }

        private void AddSelectedNodeToMultiSelection()
        {
            if (SelectedNode is null)
            {
                return;
            }

            AddToMultiSelection(SelectedNode);
        }

        private void RemoveSelectedNodeFromMultiSelection()
        {
            if (SelectedNode is null)
            {
                return;
            }

            RemoveFromMultiSelection(SelectedNode);
        }

        public void ClearMultiSelection()
        {
            if (_multiSelection.Count == 0)
            {
                return;
            }

            foreach (var node in _multiSelection)
            {
                node.IsMultiSelected = false;
            }

            _multiSelection.Clear();
            RaisePropertyChanged(nameof(HasMultiSelection));
            RaisePropertyChanged(nameof(CanAddToMultiSelection));
            RaisePropertyChanged(nameof(CanRemoveFromMultiSelection));
            UpdateCommandStates();
        }

        private void AddToMultiSelection(TreeNode node)
        {
            if (_multiSelection.Contains(node))
            {
                return;
            }

            _multiSelection.Add(node);
            node.IsMultiSelected = true;
            RaisePropertyChanged(nameof(HasMultiSelection));
            RaisePropertyChanged(nameof(CanAddToMultiSelection));
            RaisePropertyChanged(nameof(CanRemoveFromMultiSelection));
            UpdateCommandStates();
        }

        private void RemoveFromMultiSelection(TreeNode node)
        {
            if (_multiSelection.Remove(node))
            {
                node.IsMultiSelected = false;
                RaisePropertyChanged(nameof(HasMultiSelection));
                RaisePropertyChanged(nameof(CanAddToMultiSelection));
                RaisePropertyChanged(nameof(CanRemoveFromMultiSelection));
                UpdateCommandStates();
            }
        }

        public void ExpandRecursively()
        {
            if (SelectedNode is { } selectedNode)
            {
                ExpandNode(selectedNode);

                var stack = new Stack<TreeNode>();
                stack.Push(selectedNode);

                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    item.IsExpanded = true;
                    foreach (var child in item.Children)
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        public void CollapseChildren()
        {
            if (SelectedNode is { } selectedNode)
            {
                var stack = new Stack<TreeNode>();
                stack.Push(selectedNode);

                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    item.IsExpanded = false;
                    foreach (var child in item.Children)
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        public void CaptureNodeScreenshot()
        {
            MainView.Shot(null);
        }

        public void BringIntoView()
        {
            (SelectedNode?.Visual as Control)?.BringIntoView();
        }


        public void Focus()
        {
            (SelectedNode?.Visual as Control)?.Focus();
        }

        public bool CanScopeToSubTree => !IsScoped && SelectedNode != null;

        public bool CanShowFullTree => IsScoped;

        public bool IsScoped
        {
            get => _isScoped;
            private set
            {
                if (RaiseAndSetIfChanged(ref _isScoped, value))
                {
                    RaisePropertyChanged(nameof(CanScopeToSubTree));
                    RaisePropertyChanged(nameof(CanShowFullTree));
                }
            }
        }

        public void ScopeToSubTree()
        {
            if (!CanScopeToSubTree)
            {
                return;
            }

            var selected = SelectedNode!;
            Nodes = new[] { selected };
            ScopedNode = selected;
            ScopedNodeKey = BuildNodeKey(selected);
            IsScoped = true;
            ApplyTreeFilter();
        }

        public void ShowFullTree()
        {
            if (!CanShowFullTree)
            {
                return;
            }

            Nodes = _rootNodes;
            ScopedNode = null;
            ScopedNodeKey = null;
            IsScoped = false;
            ApplyTreeFilter();
        }

        public void ExpandAll()
        {
            foreach (var node in Nodes)
            {
                SetExpandedRecursive(node, true);
            }
        }

        public void CollapseAll()
        {
            foreach (var node in Nodes)
            {
                SetExpandedRecursive(node, false);
            }
        }

        private static string GetVisualSelector(Visual visual)
        {
            var name = string.IsNullOrEmpty(visual.Name) ? "" : $"#{visual.Name}";
            var classes = string.Concat(visual.Classes
                .Where(c => !c.StartsWith(":"))
                .Select(c => '.' + c));
            var pseudo = string.Concat(visual.Classes.Where(c => c[0] == ':').Select(c => c));
            var type = StyledElement.GetStyleKey(visual);
            return $$"""{{{type.Assembly.FullName}}}{{type.Namespace}}|{{type.Name}}{{name}}{{classes}}{{pseudo}}""";
        }

        private void ExpandNode(TreeNode? node)
        {
            if (node != null)
            {
                node.IsExpanded = true;
                ExpandNode(node.Parent);
            }
        }

        private static void SetExpandedRecursive(TreeNode node, bool isExpanded)
        {
            var stack = new Stack<TreeNode>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                current.IsExpanded = isExpanded;

                foreach (var child in current.Children)
                {
                    stack.Push(child);
                }
            }
        }

        private void OnDetailsSourcePreviewRequested(object? sender, SourcePreviewViewModel e)
        {
            SourcePreviewRequested?.Invoke(this, e);
        }

        private void SynchronizeSelectionFromPreview(XamlAstSelection? selection)
        {
            if (selection?.Node is null)
            {
                return;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => SynchronizeSelectionFromPreview(selection), DispatcherPriority.Background);
                return;
            }

            if (_suppressPreviewNavigation)
            {
                return;
            }

            IDisposable? publishToken = null;
            bool publishChanged = true;
            if (!_selectionCoordinator.TryBeginPublish(_previewOwnerId, selection, out publishToken, out publishChanged))
            {
                return;
            }

            if (!publishChanged)
            {
                publishToken?.Dispose();
                return;
            }

            unchecked
            {
                _previewNavigationRevision++;
            }

            var navigationRevision = _previewNavigationRevision;
            var selectedNodeRevision = _selectedNodeRevision;

            _ = SynchronizeSelectionFromPreviewAsync(selection, navigationRevision, selectedNodeRevision, publishToken);
        }

        private async Task SynchronizeSelectionFromPreviewAsync(
            XamlAstSelection selection,
            int navigationRevision,
            int selectedNodeRevision,
            IDisposable? publishToken)
        {
            TreeNode? target;
            lock (_nodesByXamlId)
            {
                _nodesByXamlId.TryGetValue(selection.Node!.Id, out target);
            }

            if (target is null)
            {
                target = await FindNodeBySelectionAsync(selection).ConfigureAwait(false);
            }

            if (target is null)
            {
                publishToken?.Dispose();
                return;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => ApplySelectionFromPreviewWithToken(target, selection, navigationRevision, selectedNodeRevision, publishToken),
                    DispatcherPriority.Background);
                return;
            }

            ApplySelectionFromPreviewWithToken(target, selection, navigationRevision, selectedNodeRevision, publishToken);
        }

        private void ApplySelectionFromPreviewWithToken(
            TreeNode target,
            XamlAstSelection selection,
            int navigationRevision,
            int selectedNodeRevision,
            IDisposable? publishToken)
        {
            try
            {
                if (!ShouldApplyPreviewSelection(navigationRevision, selectedNodeRevision, target, selection))
                {
                    return;
                }

                _suppressPreviewNavigation = true;
                try
                {
                    RegisterXamlDescriptor(target, selection.Node, selection.Document?.Path);

                    if (!IsNodeInScope(target))
                    {
                        ShowFullTree();
                    }

                    if (!target.IsVisible && !string.IsNullOrWhiteSpace(TreeFilter.FilterString))
                    {
                        TreeFilter.FilterString = string.Empty;
                    }

                    ExpandNode(target.Parent);

                    if (!ReferenceEquals(target, SelectedNode))
                    {
                        SelectedNode = target;
                    }
                    else
                    {
                        _ = UpdateSelectedNodeSourceInfoAsync(target);
                    }

                    SelectedNodeXaml = selection;

                    Dispatcher.UIThread.Post(BringIntoView, DispatcherPriority.Background);
                }
                finally
                {
                    _suppressPreviewNavigation = false;
                }
            }
            finally
            {
                publishToken?.Dispose();
            }
        }

        private bool ShouldApplyPreviewSelection(
            int navigationRevision,
            int selectedNodeRevision,
            TreeNode target,
            XamlAstSelection selection)
        {
            // Avoid applying stale preview navigation that was superseded or when the user
            // changed the tree selection to a different node while the lookup was running.
            if (navigationRevision != _previewNavigationRevision)
            {
                return false;
            }

            if (selectedNodeRevision == _selectedNodeRevision)
            {
                return true;
            }

            if (ReferenceEquals(SelectedNode, target))
            {
                return true;
            }

            var currentDescriptor = SelectedNodeXaml?.Node;
            var selectionDescriptor = selection.Node;

            if (currentDescriptor is not null &&
                selectionDescriptor is not null &&
                currentDescriptor.Id == selectionDescriptor.Id)
            {
                return true;
            }

            return false;
        }

        private async Task<TreeNode?> FindNodeBySelectionAsync(XamlAstSelection selection)
        {
            var descriptor = selection.Node;
            if (descriptor is null)
            {
                return null;
            }

            lock (_nodesByXamlId)
            {
                if (_nodesByXamlId.TryGetValue(descriptor.Id, out var mapped))
                {
                    return mapped;
                }
            }

            var descriptorStart = Math.Max(descriptor.LineSpan.Start.Line, 1);
            var descriptorEnd = Math.Max(descriptor.LineSpan.End.Line, descriptorStart);
            var documentKey = GetDocumentKeyForSelection(selection);

            if (documentKey is not null &&
                descriptorStart > 0 &&
                _documentNodeIndices.TryGetValue(documentKey, out var documentIndex) &&
                documentIndex.TryGetBestMatch(descriptorStart, descriptorEnd, out var indexedMatch))
            {
                return indexedMatch;
            }

            return await FindNodeBySelectionFallbackAsync(selection, descriptor, descriptorStart, descriptorEnd).ConfigureAwait(false);
        }

        private Task<TreeNode?> FindNodeBySelectionFallbackAsync(
            XamlAstSelection selection,
            XamlAstNodeDescriptor descriptor,
            int descriptorStart,
            int descriptorEnd)
        {
            var selectionPath = selection.Document?.Path;
            var selectionNormalized = selectionPath is not null ? NormalizeLocalPath(selectionPath) : null;
            string? selectionRemoteKey = null;

            if (!string.IsNullOrWhiteSpace(selectionNormalized) &&
                _cachedDocumentRemoteIndex.TryGetValue(selectionNormalized!, out var selectionRemote))
            {
                selectionRemoteKey = selectionRemote;
            }
            else if (!string.IsNullOrWhiteSpace(selectionPath) &&
                     Uri.TryCreate(selectionPath, UriKind.Absolute, out var selectionUri))
            {
                selectionRemoteKey = selectionUri.AbsoluteUri;
            }

            var references = new List<DocumentReference>();

            foreach (var node in EnumerateAllNodes())
            {
                if (TryBuildDocumentReference(node, out var reference))
                {
                    references.Add(reference);
                }
            }

            if (selection.Node is { } selectionNode)
            {
                var exact = references.FirstOrDefault(r => r.DescriptorId.HasValue && r.DescriptorId.Value.Equals(selectionNode.Id));
                if (exact.Node is not null)
                {
                    return Task.FromResult<TreeNode?>(exact.Node);
                }
            }

            var matchingReferences = references
                .Where(r => PathMatches(r, selectionNormalized, selectionRemoteKey, selectionPath))
                .ToList();

            if (matchingReferences.Count == 0)
            {
                return Task.FromResult<TreeNode?>(null);
            }

            DocumentReference? best = null;
            var bestPenalty = int.MaxValue;

            foreach (var reference in matchingReferences)
            {
                if (reference.DescriptorId.HasValue && reference.DescriptorId.Value.Equals(descriptor.Id))
                {
                    return Task.FromResult<TreeNode?>(reference.Node);
                }

                var penalty = ComputeMatchPenalty(reference, descriptorStart, descriptorEnd);
                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    best = reference;
                }
            }

            TreeNode? fallbackNode = best?.Node ?? matchingReferences[0].Node;
            return Task.FromResult<TreeNode?>(fallbackNode);
        }

        private IEnumerable<TreeNode> EnumerateAllNodes()
        {
            var stack = new Stack<TreeNode>();

            for (var i = _rootNodes.Length - 1; i >= 0; i--)
            {
                stack.Push(_rootNodes[i]);
            }

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return node;

                var children = node.Children;
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }

        private async Task IndexNodeSourceInfoAsync(TreeNode node, SourceInfo info)
        {
            var key = GetDocumentKey(info);
            if (string.IsNullOrWhiteSpace(key))
            {
                RemoveNodeFromSourceIndex(node);
                return;
            }

            await TryRegisterDescriptorAsync(node, info).ConfigureAwait(false);

            int startLine;
            int endLine;

            if (info.StartLine is int infoStart && infoStart > 0)
            {
                startLine = infoStart;
                endLine = info.EndLine ?? infoStart;
            }
            else if (node.XamlDescriptor is { } descriptor)
            {
                startLine = Math.Max(descriptor.LineSpan.Start.Line, 1);
                endLine = Math.Max(descriptor.LineSpan.End.Line, startLine);
            }
            else
            {
                RemoveNodeFromSourceIndex(node);
                return;
            }

            var index = _documentNodeIndices.GetOrAdd(key, _ => new DocumentNodeIndex());
            index.AddOrUpdate(node, startLine, endLine);

            if (info.RemoteUri is { } remote && string.IsNullOrWhiteSpace(info.LocalPath))
            {
                var path = await ResolveDocumentPathAsync(info).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var normalized = NormalizeLocalPath(path!);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        _remoteDocumentPaths[remote.AbsoluteUri] = normalized;
                        _cachedDocumentRemoteIndex[normalized] = remote.AbsoluteUri;
                    }
                }
            }
        }

        private void RemoveNodeFromSourceIndex(TreeNode node)
        {
            string? descriptorPath = null;

            if (node.XamlDescriptor is { } descriptor)
            {
                lock (_nodesByXamlId)
                {
                    if (_descriptorDocuments.TryGetValue(descriptor.Id, out var path))
                    {
                        descriptorPath = path;
                        _descriptorDocuments.Remove(descriptor.Id);
                    }
                }
            }

            foreach (var index in _documentNodeIndices.Values)
            {
                index.Remove(node);
            }

            if (!string.IsNullOrWhiteSpace(descriptorPath))
            {
                _documentNodeIndices.TryRemove(descriptorPath!, out _);
                _remoteDocumentCache.TryRemove(descriptorPath!, out _);

                if (_cachedDocumentRemoteIndex.TryRemove(descriptorPath!, out var remoteKey) &&
                    !string.IsNullOrWhiteSpace(remoteKey))
                {
                    _remoteDocumentPaths.TryRemove(remoteKey!, out _);
                    _remoteDocumentCache.TryRemove(remoteKey!, out _);
                }

                foreach (var pair in _remoteDocumentPaths.ToArray())
                {
                    if (PathsEqual(pair.Value, descriptorPath!))
                    {
                        _remoteDocumentPaths.TryRemove(pair.Key, out _);
                        _remoteDocumentCache.TryRemove(pair.Key, out _);
                        _cachedDocumentRemoteIndex.TryRemove(pair.Value, out _);
                    }
                }
            }
        }

        private async Task<string?> ResolveDocumentPathAsync(SourceInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.LocalPath))
            {
                return NormalizeLocalPath(info.LocalPath!);
            }

            if (info.RemoteUri is null)
            {
                return null;
            }

            var remote = info.RemoteUri;

            if (remote.IsFile)
            {
                var localPath = remote.LocalPath;
                if (File.Exists(localPath))
                {
                    var normalized = NormalizeLocalPath(localPath);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        _remoteDocumentPaths[remote.AbsoluteUri] = normalized;
                        _cachedDocumentRemoteIndex[normalized] = remote.AbsoluteUri;
                    }

                    return normalized;
                }

                return null;
            }

            if (_remoteDocumentPaths.TryGetValue(remote.AbsoluteUri, out var cachedPath) &&
                !string.IsNullOrWhiteSpace(cachedPath) &&
                File.Exists(cachedPath))
            {
                return cachedPath;
            }

            try
            {
                var cacheKey = BuildRemoteCacheKey(remote);
                var task = _remoteDocumentCache.GetOrAdd(cacheKey, _ => DownloadRemoteDocumentAsync(remote, cacheKey));
                var path = await task.ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(path))
                {
                    _remoteDocumentCache.TryRemove(cacheKey, out _);
                    return null;
                }

                var normalized = NormalizeLocalPath(path!);
                if (!string.IsNullOrEmpty(normalized))
                {
                    _remoteDocumentPaths[remote.AbsoluteUri] = normalized;
                    _cachedDocumentRemoteIndex[normalized] = remote.AbsoluteUri;
                    return normalized;
                }

                return path;
            }
            catch
            {
                _remoteDocumentCache.TryRemove(BuildRemoteCacheKey(remote), out _);
                return null;
            }
        }

        private static string? NormalizeLocalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private string? GetDocumentKey(SourceInfo info)
        {
            if (!string.IsNullOrWhiteSpace(info.LocalPath))
            {
                return NormalizeLocalPath(info.LocalPath!);
            }

            return info.RemoteUri?.AbsoluteUri;
        }

        private string? GetDocumentKeyForSelection(XamlAstSelection selection)
        {
            var path = selection.Document?.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = NormalizeLocalPath(path!);
            if (!string.IsNullOrEmpty(normalized) &&
                _cachedDocumentRemoteIndex.TryGetValue(normalized, out var remoteKey))
            {
                return remoteKey;
            }

            return normalized;
        }

        private bool TryMatchDocument(SourceInfo info, string documentPath, string? remoteKey)
        {
            if (!string.IsNullOrWhiteSpace(info.LocalPath) && PathsEqual(info.LocalPath!, documentPath))
            {
                return true;
            }

            if (remoteKey is not null && info.RemoteUri is { } remote &&
                string.Equals(remote.AbsoluteUri, remoteKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (info.RemoteUri is { } remoteUri &&
                _remoteDocumentPaths.TryGetValue(remoteUri.AbsoluteUri, out var cached) &&
                !string.IsNullOrWhiteSpace(cached) &&
                PathsEqual(cached, documentPath))
            {
                return true;
            }

            return false;
        }

        private async Task TryEnsureDescriptorWithoutSourceInfoAsync(TreeNode node)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var normalized = NormalizeLocalPath(path!) ?? path!;
                if (seen.Add(normalized))
                {
                    candidates.Add(normalized);
                }
            }

            foreach (var ancestor in EnumerateAncestorsInclusive(node))
            {
                var ancestorInfo = ancestor.SourceInfo;
                if (ancestorInfo is not null)
                {
                    AddCandidate(ancestorInfo.LocalPath);
                    AddCandidate(ancestorInfo.RemoteUri?.AbsoluteUri);
                }

                if (ancestor.XamlDescriptor is { } ancestorDescriptor &&
                    TryGetDescriptorDocumentPath(ancestorDescriptor.Id, out var ancestorDescriptorPath))
                {
                    AddCandidate(ancestorDescriptorPath);
                }
            }

            AddCandidate(ResolveDocumentPathFromAncestors(node));

            if (ReferenceEquals(node, SelectedNode))
            {
                AddCandidate(SelectedNodeXaml?.Document?.Path);
            }

            foreach (var key in _documentNodeIndices.Keys)
            {
                AddCandidate(key);
            }

            foreach (var value in _remoteDocumentPaths.Values)
            {
                AddCandidate(value);
            }

            foreach (var key in _cachedDocumentRemoteIndex.Keys)
            {
                AddCandidate(key);
            }

            lock (_nodesByXamlId)
            {
                foreach (var path in _descriptorDocuments.Values)
                {
                    AddCandidate(path);
                }
            }

        var previewPaths = new List<string>();
        lock (_previewObserversGate)
        {
            foreach (var reference in _previewObservers)
            {
                if (reference.TryGetTarget(out var preview) &&
                    preview?.AstSelection?.Document?.Path is { } previewPath)
                {
                    previewPaths.Add(previewPath);
                }
            }
        }

        foreach (var previewPath in previewPaths)
        {
            AddCandidate(previewPath);
        }

            foreach (var candidate in candidates)
            {
                try
                {
                    var index = await _xamlAstWorkspace.GetIndexAsync(candidate).ConfigureAwait(false);
                    var descriptor = await ResolveDescriptorAsync(candidate, node, null, index).ConfigureAwait(false);
                    if (descriptor is null)
                    {
                        continue;
                    }

                    lock (_nodesByXamlId)
                    {
                        if (_nodesByXamlId.TryGetValue(descriptor.Id, out var existing) &&
                            !ReferenceEquals(existing, node))
                        {
                            continue;
                        }
                    }

                    var document = await _xamlAstWorkspace.GetDocumentAsync(candidate).ConfigureAwait(false);
                    var descriptors = index.Nodes as IReadOnlyList<XamlAstNodeDescriptor> ?? index.Nodes.ToList();
                    var selection = new XamlAstSelection(document, descriptor, descriptors);

                    var startLine = Math.Max(descriptor.LineSpan.Start.Line, 1);
                    var endLine = Math.Max(descriptor.LineSpan.End.Line, startLine);
                    var documentIndex = _documentNodeIndices.GetOrAdd(candidate, _ => new DocumentNodeIndex());
                    documentIndex.AddOrUpdate(node, startLine, endLine);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        RegisterXamlDescriptor(node, descriptor, selection.Document?.Path);

                        if (ReferenceEquals(SelectedNode, node))
                        {
                            SelectedNodeXaml = selection;
                        }
                    }, DispatcherPriority.Background);

                    return;
                }
                catch
                {
                    // Try next candidate.
                }
            }

            RemoveNodeFromSourceIndex(node);
        }

        private SourceInfo? TryCreateSyntheticSourceInfo(TreeNode node)
        {
            if (node.XamlDescriptor is not { } descriptor)
            {
                return null;
            }

            if (!TryGetDescriptorDocumentPath(descriptor.Id, out var path) || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string? localPath = null;
            Uri? remoteUri = null;
            SourceOrigin origin = SourceOrigin.Unknown;

            if (File.Exists(path))
            {
                localPath = path;
                origin = SourceOrigin.Local;
            }
            else if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                remoteUri = uri;
                origin = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    ? SourceOrigin.SourceLink
                    : SourceOrigin.Unknown;
            }

            if (localPath is null && remoteUri is null)
            {
                return null;
            }

            var startLine = Math.Max(descriptor.LineSpan.Start.Line, 1);
            var startColumn = Math.Max(descriptor.LineSpan.Start.Column, 1);
            var endLine = Math.Max(descriptor.LineSpan.End.Line, startLine);
            var endColumn = Math.Max(descriptor.LineSpan.End.Column, startColumn);

            return new SourceInfo(
                LocalPath: localPath,
                RemoteUri: remoteUri,
                StartLine: startLine,
                StartColumn: startColumn,
                EndLine: endLine,
                EndColumn: endColumn,
                Origin: origin);
        }

        private static IEnumerable<TreeNode> EnumerateAncestorsInclusive(TreeNode node)
        {
            var current = node;
            while (current is not null)
            {
                yield return current;
                current = current.Parent;
            }
        }

        private string? ResolveDocumentPathFromAncestors(TreeNode node)
        {
            if (node.XamlDescriptor is { } selfDescriptor &&
                TryGetDescriptorDocumentPath(selfDescriptor.Id, out var selfPath) &&
                !string.IsNullOrWhiteSpace(selfPath))
            {
                return selfPath;
            }

            var current = node;

            while (current is not null)
            {
                var info = current.SourceInfo;
                if (info is not null)
                {
                    if (!string.IsNullOrWhiteSpace(info.LocalPath))
                    {
                        return NormalizeLocalPath(info.LocalPath!);
                    }

                    if (info.RemoteUri is { } remote &&
                        _remoteDocumentPaths.TryGetValue(remote.AbsoluteUri, out var cached) &&
                        !string.IsNullOrWhiteSpace(cached))
                    {
                        return cached;
                    }
                }

                if (current.XamlDescriptor is { } descriptor &&
                    TryGetDescriptorDocumentPath(descriptor.Id, out var descriptorPath) &&
                    !string.IsNullOrWhiteSpace(descriptorPath))
                {
                    return descriptorPath;
                }

                current = current.Parent;
            }

            return null;
        }

        private static string BuildRemoteCacheKey(Uri uri)
        {
            return uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.ToString();
        }

        private async Task<string?> DownloadRemoteDocumentAsync(Uri uri, string cacheKey)
        {
            try
            {
                var directory = GetRemoteCacheDirectory();
                Directory.CreateDirectory(directory);

                var extension = Path.GetExtension(uri.AbsolutePath);
                if (string.IsNullOrEmpty(extension))
                {
                    extension = ".axaml";
                }

                var fileName = BuildRemoteCacheFileName(uri) + extension;
                var filePath = Path.Combine(directory, fileName);

                var content = await RemoteDocumentClient.GetStringAsync(uri).ConfigureAwait(false);
#if NETSTANDARD2_0
                await WriteAllTextAsyncCompat(filePath, content, Encoding.UTF8).ConfigureAwait(false);
#else
                await File.WriteAllTextAsync(filePath, content, Encoding.UTF8).ConfigureAwait(false);
#endif

                _xamlAstWorkspace.Invalidate(filePath);
                _remoteDocumentCache.TryRemove(cacheKey, out _);

                return filePath;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildRemoteCacheFileName(Uri uri)
        {
#if NETSTANDARD2_0
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
            return BytesToHex(hashBytes);
#else
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri));
            return Convert.ToHexString(hash);
#endif
        }

        private static string GetRemoteCacheDirectory()
        {
            return Path.Combine(Path.GetTempPath(), "DiagnosticsTools", "RemoteSources");
        }

#if NETSTANDARD2_0
        private static Task WriteAllTextAsyncCompat(string path, string contents, Encoding encoding)
        {
            return Task.Run(() => File.WriteAllText(path, contents, encoding));
        }

        private static string BytesToHex(byte[] data)
        {
            var builder = new StringBuilder(data.Length * 2);
            foreach (var value in data)
            {
                builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
#endif

        private async Task TryRegisterDescriptorAsync(TreeNode node, SourceInfo info)
        {
            var path = await ResolveDocumentPathAsync(info).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var descriptor = await ResolveDescriptorAsync(path!, node, info).ConfigureAwait(false);
                if (descriptor is null)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() => RegisterXamlDescriptor(node, descriptor, path), DispatcherPriority.Background);
                _documentNodeIndices.TryRemove(GetDocumentKey(info) ?? string.Empty, out _);
            }
            catch
            {
                // Descriptor resolution is best-effort.
            }
        }

        private async Task RefreshDocumentDescriptorsAsync(string path)
        {
            var normalizedPath = NormalizeLocalPath(path) ?? path;

            List<TreeNode> nodesToRefresh;
            lock (_nodesByXamlId)
            {
                var set = new HashSet<TreeNode>();
                foreach (var kvp in _descriptorDocuments)
                {
                    if (kvp.Value is null || !PathsEqual(kvp.Value, normalizedPath))
                    {
                        continue;
                    }

                    if (_nodesByXamlId.TryGetValue(kvp.Key, out var node) && node is not null)
                    {
                        set.Add(node);
                    }
                }

                nodesToRefresh = set.Count > 0 ? set.ToList() : new List<TreeNode>();
            }

            if (nodesToRefresh.Count == 0)
            {
                return;
            }

            XamlAstDocument document;
            IXamlAstIndex index;
            MutableXamlDocument mutable;
            try
            {
                document = await _xamlAstWorkspace.GetDocumentAsync(path).ConfigureAwait(false);
                index = await _xamlAstWorkspace.GetIndexAsync(path).ConfigureAwait(false);
                mutable = await _xamlAstWorkspace.GetMutableDocumentAsync(path).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            var descriptors = index.Nodes as IReadOnlyList<XamlAstNodeDescriptor> ?? index.Nodes.ToList();

            var refreshTasks = new List<Task>(nodesToRefresh.Count);
            foreach (var node in nodesToRefresh)
            {
                refreshTasks.Add(UpdateDescriptorForNodeAsync(node, path, document, descriptors, index, mutable));
            }

            await Task.WhenAll(refreshTasks).ConfigureAwait(false);
        }

        private async Task UpdateDescriptorForNodeAsync(
            TreeNode node,
            string path,
            XamlAstDocument document,
            IReadOnlyList<XamlAstNodeDescriptor> descriptors,
            IXamlAstIndex index,
            MutableXamlDocument mutable)
        {
            XamlAstNodeDescriptor? descriptor = null;
            try
            {
                descriptor = await ResolveDescriptorAsync(path, node, node.SourceInfo, index, mutable).ConfigureAwait(false);
            }
            catch
            {
                // Resolution best-effort.
            }

            var selection = descriptor is not null ? new XamlAstSelection(document, descriptor, descriptors) : null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RegisterXamlDescriptor(node, descriptor, path);

                if (ReferenceEquals(node, SelectedNode) && selection is not null)
                {
                    SelectedNodeXaml = selection;
                }
            }, DispatcherPriority.Background);
        }

        private void RegisterXamlDescriptor(TreeNode node, XamlAstNodeDescriptor? descriptor, string? documentPath = null)
        {
            lock (_nodesByXamlId)
            {
                if (node.XamlDescriptor is { } previous)
                {
                    if (_nodesByXamlId.TryGetValue(previous.Id, out var mapped) &&
                        ReferenceEquals(mapped, node))
                    {
                        _nodesByXamlId.Remove(previous.Id);
                    }

                    if (descriptor is null || !descriptor.Id.Equals(previous.Id))
                    {
                        _descriptorDocuments.Remove(previous.Id);
                    }
                }

                node.UpdateXamlDescriptor(descriptor);

                if (descriptor is not null)
                {
                    _nodesByXamlId[descriptor.Id] = node;
                    if (!string.IsNullOrWhiteSpace(documentPath))
                    {
                        _descriptorDocuments[descriptor.Id] = documentPath!;
                    }
                }
            }
        }

        private bool TryGetDescriptorDocumentPath(XamlAstNodeId id, out string? path)
        {
            lock (_nodesByXamlId)
            {
                return _descriptorDocuments.TryGetValue(id, out path);
            }
        }

        private async Task<XamlAstSelection?> BuildXamlSelectionAsync(TreeNode node, SourceInfo? info)
        {
            if (info is null)
            {
                return null;
            }

            var path = await ResolveDocumentPathAsync(info).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var document = await _xamlAstWorkspace.GetDocumentAsync(path!).ConfigureAwait(false);
                var index = await _xamlAstWorkspace.GetIndexAsync(path!).ConfigureAwait(false);
                var descriptor = await ResolveDescriptorAsync(path!, node, info, index).ConfigureAwait(false);
                if (descriptor is null)
                {
                    var documentPath = document.Path ?? info?.LocalPath ?? "<unknown>";
                    LogDescriptorResolutionFailure(node, documentPath);
                }
                var descriptors = index.Nodes as IReadOnlyList<XamlAstNodeDescriptor> ?? index.Nodes.ToList();
                return new XamlAstSelection(document, descriptor, descriptors);
            }
            catch
            {
                return null;
            }
        }

        private static int GetLineStart(XamlAstNodeDescriptor descriptor)
        {
            return Math.Max(descriptor.LineSpan.Start.Line, 1);
        }

        private static int GetLineEnd(XamlAstNodeDescriptor descriptor)
        {
            var start = GetLineStart(descriptor);
            var end = descriptor.LineSpan.End.Line;
            if (end < start)
            {
                end = start;
            }

            return Math.Max(end, start);
        }

        private async Task<XamlAstNodeDescriptor?> ResolveDescriptorAsync(
            string path,
            TreeNode node,
            SourceInfo? info,
            IXamlAstIndex? index = null,
            MutableXamlDocument? mutableDocument = null)
        {
            var runtimeDescriptor = await TryResolveDescriptorByRuntimeAsync(path, node, mutableDocument).ConfigureAwait(false);
            if (runtimeDescriptor is not null)
            {
                return runtimeDescriptor;
            }

            var effectiveIndex = index ?? await _xamlAstWorkspace.GetIndexAsync(path).ConfigureAwait(false);
            return ResolveDescriptor(effectiveIndex, node, info);
        }

        private async Task<XamlAstNodeDescriptor?> TryResolveDescriptorByRuntimeAsync(string path, TreeNode node, MutableXamlDocument? mutableDocument = null)
        {
            var runtimeId = BuildRuntimeElementId(node.Visual);
            if (string.IsNullOrWhiteSpace(runtimeId))
            {
                return null;
            }

            try
            {
                var mutable = mutableDocument ?? await _xamlAstWorkspace.GetMutableDocumentAsync(path).ConfigureAwait(false);
                if (mutable.TryGetDescriptor(runtimeId, out var descriptor) && DescriptorMatchesNode(node, descriptor))
                {
                    return descriptor;
                }

                var elementName = node.ElementName ?? (node.Visual as INamed)?.Name;
                if (!string.IsNullOrWhiteSpace(elementName) &&
                    mutable.TryGetDescriptor(elementName!, out descriptor) &&
                    DescriptorMatchesNode(node, descriptor))
                {
                    return descriptor;
                }
            }
            catch
            {
                // Best effort; fallback to heuristics.
            }

            return null;
        }

        private XamlAstNodeDescriptor? ResolveDescriptor(IXamlAstIndex index, TreeNode node, SourceInfo? info)
        {
            var nameCandidates = GetCandidatesByName(index, node);
            var descriptor = TryResolveWithStructuralHints(nameCandidates, node, info);
            if (descriptor is not null && DescriptorMatchesNode(node, descriptor))
            {
                return descriptor;
            }

            if (descriptor is null || !DescriptorMatchesNode(node, descriptor))
            {
                var matchingByName = nameCandidates.FirstOrDefault(c => DescriptorMatchesNode(node, c));
                if (matchingByName is not null)
                {
                    return matchingByName;
                }
            }

            var lineCandidates = GetCandidatesByLine(index, info);
            descriptor = TryResolveWithStructuralHints(lineCandidates, node, info);
            if (descriptor is not null && DescriptorMatchesNode(node, descriptor))
            {
                return descriptor;
            }

            if (descriptor is null || !DescriptorMatchesNode(node, descriptor))
            {
                var matchingByLine = lineCandidates.FirstOrDefault(c => DescriptorMatchesNode(node, c));
                if (matchingByLine is not null)
                {
                    return matchingByLine;
                }
            }

            var allCandidates = index.Nodes.ToList();
            return TryResolveWithStructuralHints(allCandidates, node, info);
        }

        private static bool DescriptorMatchesNode(TreeNode node, XamlAstNodeDescriptor descriptor)
        {
            var expectedName = node.Visual?.GetType().Name ?? node.Type;
            if (string.IsNullOrWhiteSpace(expectedName))
            {
                return true;
            }

            if (string.Equals(descriptor.LocalName, expectedName, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(descriptor.QualifiedName))
            {
                var segments = descriptor.QualifiedName.Split(':');
                var simpleName = segments.Length > 0 ? segments[segments.Length - 1] : descriptor.QualifiedName;
                if (string.Equals(simpleName, expectedName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static IReadOnlyList<XamlAstNodeDescriptor> GetCandidatesByName(IXamlAstIndex index, TreeNode node)
        {
            var elementName = node.ElementName ?? (node.Visual as INamed)?.Name;
            if (!string.IsNullOrWhiteSpace(elementName))
            {
                var matches = index.FindByName(elementName!);
                if (matches.Count > 0)
                {
                    return matches.ToList();
                }
            }

            return Array.Empty<XamlAstNodeDescriptor>();
        }

        private static IReadOnlyList<XamlAstNodeDescriptor> GetCandidatesByLine(IXamlAstIndex index, SourceInfo? info)
        {
            if (info?.StartLine is not int startLine || startLine <= 0)
            {
                return Array.Empty<XamlAstNodeDescriptor>();
            }

            return index.Nodes
                .Where(d => startLine >= GetLineStart(d) && startLine <= GetLineEnd(d))
                .OrderBy(d => GetLineEnd(d) - GetLineStart(d))
                .ThenBy(d => d.Path.Count)
                .ToList();
        }

        private XamlAstNodeDescriptor? TryResolveWithStructuralHints(
            IReadOnlyList<XamlAstNodeDescriptor> candidates,
            TreeNode node,
            SourceInfo? info)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var filtered = ApplyTemplateRoleFilter(node, candidates);
            filtered = ApplyParentPathFilter(node, filtered, out var parentDescriptor);
            filtered = ApplyDepthFilter(node, filtered);
            filtered = ApplyOrdinalFilter(node, filtered, parentDescriptor);

            if (filtered.Count == 1)
            {
                return filtered[0];
            }

            return SelectBestCandidate(filtered, node, info);
        }

        private static IReadOnlyList<XamlAstNodeDescriptor> ApplyTemplateRoleFilter(TreeNode node, IReadOnlyList<XamlAstNodeDescriptor> candidates)
        {
            if (candidates.Count == 0)
            {
                return candidates;
            }

            var isTemplateNode = IsTemplateNode(node);
            var filtered = candidates.Where(d => d.IsTemplate == isTemplateNode).ToList();
            return filtered.Count > 0 ? filtered : candidates;
        }

        private IReadOnlyList<XamlAstNodeDescriptor> ApplyParentPathFilter(
            TreeNode node,
            IReadOnlyList<XamlAstNodeDescriptor> candidates,
            out XamlAstNodeDescriptor? parentDescriptor)
        {
            parentDescriptor = GetClosestDescriptorAncestor(node);
            if (candidates.Count == 0 || parentDescriptor is null)
            {
                return candidates;
            }

            var parentPath = parentDescriptor.Path;
            var filtered = new List<XamlAstNodeDescriptor>();

            foreach (var candidate in candidates)
            {
                if (candidate.Path.Count == parentPath.Count + 1 && HasPathPrefix(candidate.Path, parentPath))
                {
                    filtered.Add(candidate);
                }
            }

            return filtered.Count > 0 ? filtered : candidates;
        }

        private IReadOnlyList<XamlAstNodeDescriptor> ApplyDepthFilter(TreeNode node, IReadOnlyList<XamlAstNodeDescriptor> candidates)
        {
            if (candidates.Count == 0)
            {
                return candidates;
            }

            var depth = GetStructuralDepth(node);
            if (depth <= 0)
            {
                return candidates;
            }

            var filtered = candidates.Where(d => d.Path.Count == depth).ToList();
            return filtered.Count > 0 ? filtered : candidates;
        }

        private IReadOnlyList<XamlAstNodeDescriptor> ApplyOrdinalFilter(
            TreeNode node,
            IReadOnlyList<XamlAstNodeDescriptor> candidates,
            XamlAstNodeDescriptor? parentDescriptor)
        {
            if (candidates.Count <= 1 || parentDescriptor is null)
            {
                return candidates;
            }

            if (GetStructuralIndex(node) is not int ordinal)
            {
                return candidates;
            }

            var parentPath = parentDescriptor.Path;
            var filtered = candidates
                .Where(d => d.Path.Count == parentPath.Count + 1 && d.Path[d.Path.Count - 1] == ordinal)
                .ToList();

            return filtered.Count > 0 ? filtered : candidates;
        }

        private static bool HasPathPrefix(IReadOnlyList<int> path, IReadOnlyList<int> prefix)
        {
            if (prefix.Count > path.Count)
            {
                return false;
            }

            for (var i = 0; i < prefix.Count; i++)
            {
                if (path[i] != prefix[i])
                {
                    return false;
                }
            }

            return true;
        }

        private XamlAstNodeDescriptor? SelectBestCandidate(
            IReadOnlyList<XamlAstNodeDescriptor> candidates,
            TreeNode node,
            SourceInfo? info)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var typeName = node.Visual?.GetType().Name ?? node.Type;
            var startLine = info?.StartLine;

            return candidates
                .OrderBy(d => string.Equals(d.LocalName, typeName, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(d => startLine is int line ? Math.Abs(line - GetLineStart(d)) : 0)
                .ThenBy(d => d.Path.Count)
                .ThenBy(d => d.Span.Length)
                .First();
        }

        private static bool IsTemplateNode(TreeNode node) =>
            node switch
            {
                CombinedTreeNode { Role: CombinedTreeNode.CombinedNodeRole.Template } => true,
                _ => node.IsInTemplate
            };

        private static XamlAstNodeDescriptor? GetClosestDescriptorAncestor(TreeNode node)
        {
            var current = node.Parent;
            while (current is not null)
            {
                if (!MapsToXamlElement(current))
                {
                    current = current.Parent;
                    continue;
                }

                if (current.XamlDescriptor is not null)
                {
                    return current.XamlDescriptor;
                }

                current = current.Parent;
            }

            return null;
        }

        private static int GetStructuralDepth(TreeNode node)
        {
            var depth = 0;
            var current = node;

            while (current is not null)
            {
                if (MapsToXamlElement(current))
                {
                    depth++;
                }

                current = GetStructuralParent(current);
            }

            return depth;
        }

        private static int? GetStructuralIndex(TreeNode node)
        {
            var parent = GetStructuralParent(node);
            if (parent is null)
            {
                return null;
            }

            var ordinal = 0;
            var children = parent.Children;

            for (var i = 0; i < children.Count; i++)
            {
                var sibling = children[i];
                if (!MapsToXamlElement(sibling))
                {
                    continue;
                }

                if (ReferenceEquals(sibling, node))
                {
                    return ordinal;
                }

                ordinal++;
            }

            return null;
        }

        private static TreeNode? GetStructuralParent(TreeNode node)
        {
            var parent = node.Parent;
            while (parent is not null && !MapsToXamlElement(parent))
            {
                parent = parent.Parent;
            }

            return parent;
        }

        private static bool MapsToXamlElement(TreeNode node) => node is not CombinedTreeTemplateGroupNode;

        private void LogDescriptorResolutionFailure(TreeNode node, string path)
        {
            if (Logger.TryGet(LogEventLevel.Warning, nameof(TreePageViewModel)) is not { } logger)
            {
                return;
            }

            var typeName = node.Visual?.GetType().Name ?? node.Type;
            var name = node.ElementName ?? (node.Visual as INamed)?.Name ?? string.Empty;
            var role = node switch
            {
                CombinedTreeNode combined => combined.Role.ToString(),
                _ when node.IsInTemplate => "Template",
                _ => "Visual"
            };

            logger.Log(
                this,
                "Unable to resolve XAML descriptor for {Type} (name: {Name}, role: {Role}) in document {Path}.",
                typeName,
                string.IsNullOrEmpty(name) ? "<unnamed>" : name,
                role,
                string.IsNullOrWhiteSpace(path) ? "<unknown>" : path);
        }

        private static XamlAstNodeDescriptor? FindParentDescriptor(XamlAstSelection selection)
        {
            if (selection.Node is null)
            {
                return null;
            }

            var nodes = selection.DocumentNodes;
            if (nodes is null)
            {
                return null;
            }

            var node = selection.Node;
            var path = node.Path;
            if (path.Count <= 1)
            {
                return null;
            }

            var parentDepth = path.Count - 1;

            foreach (var candidate in nodes)
            {
                if (candidate.Path.Count != parentDepth)
                {
                    continue;
                }

                var matches = true;
                for (var index = 0; index < parentDepth; index++)
                {
                    if (candidate.Path[index] != path[index])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                if (candidate.Span.Start <= node.Span.Start && candidate.Span.End >= node.Span.End)
                {
                    return candidate;
                }
            }

            return null;
        }

        private void OnXamlNodesChanged(object? sender, XamlAstNodesChangedEventArgs e)
        {
            if (e is null || string.IsNullOrEmpty(e.Path))
            {
                return;
            }

            InvalidateDocumentIndices(e.Path);
            _ = RefreshDocumentDescriptorsAsync(e.Path);

            var currentPath = ResolveSelectedDocumentPath();
            if (string.IsNullOrWhiteSpace(currentPath) || !PathsEqual(currentPath!, e.Path))
            {
                return;
            }

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => _ = UpdateSelectedNodeSourceInfoAsync(SelectedNode), DispatcherPriority.Background);
            }
            else
            {
                _ = UpdateSelectedNodeSourceInfoAsync(SelectedNode);
            }
        }

        private string? ResolveSelectedDocumentPath()
        {
            if (SelectedNodeSourceInfo?.LocalPath is { } localPath && !string.IsNullOrWhiteSpace(localPath))
            {
                return NormalizeLocalPath(localPath!);
            }

            if (SelectedNodeSourceInfo?.RemoteUri is { } remote &&
                _remoteDocumentPaths.TryGetValue(remote.AbsoluteUri, out var cachedPath) &&
                !string.IsNullOrWhiteSpace(cachedPath))
            {
                return cachedPath;
            }

            return SelectedNodeXaml?.Document?.Path;
        }

        private void OnXamlDocumentChanged(object? sender, XamlDocumentChangedEventArgs e)
        {
            if (SelectedNode is null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(e.Path))
            {
                InvalidateDocumentIndices(e.Path!);
                _ = RefreshDocumentDescriptorsAsync(e.Path!);
            }

            var currentPath = ResolveSelectedDocumentPath();
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                return;
            }

            if (string.IsNullOrEmpty(e.Path) || PathsEqual(currentPath!, e.Path))
            {
                _ = UpdateSelectedNodeSourceInfoAsync(SelectedNode);
            }
        }

        private void InvalidateDocumentIndices(string path)
        {
            var normalized = NormalizeLocalPath(path) ?? path;
            _documentNodeIndices.TryRemove(normalized, out _);

            if (_cachedDocumentRemoteIndex.TryRemove(normalized, out var remoteKey))
            {
                _remoteDocumentPaths.TryRemove(remoteKey, out _);
                _documentNodeIndices.TryRemove(remoteKey, out _);
                _remoteDocumentCache.TryRemove(remoteKey, out _);
            }

            if (_remoteDocumentPaths.TryGetValue(path, out var local) && !string.IsNullOrWhiteSpace(local))
            {
                _documentNodeIndices.TryRemove(local, out _);
                _cachedDocumentRemoteIndex.TryRemove(local, out _);
                _remoteDocumentPaths.TryRemove(path, out _);
                _remoteDocumentCache.TryRemove(path, out _);
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                left = Path.GetFullPath(left);
                right = Path.GetFullPath(right);
            }
            catch
            {
                // If normalization fails, fall back to raw comparison.
            }

            return PathComparer.Equals(left, right);
        }

        private void NavigateToAstNode(XamlAstNodeDescriptor? descriptor)
        {
            NavigateToAstNodeCore(descriptor, asynchronous: true);
        }

        private void NavigateToAstNode(XamlAstNodeDescriptor? descriptor, bool immediate)
        {
            NavigateToAstNodeCore(descriptor, asynchronous: !immediate);
        }

        private void NavigateToAstNodeCore(XamlAstNodeDescriptor? descriptor, bool asynchronous)
        {
            if (descriptor is null)
            {
                return;
            }

            void Navigate()
            {
                TreeNode? target;
                lock (_nodesByXamlId)
                {
                    _nodesByXamlId.TryGetValue(descriptor.Id, out target);
                }

                if (target is null)
                {
                    return;
                }

                if (!IsNodeInScope(target))
                {
                    ShowFullTree();
                }

                if (!target.IsVisible && !string.IsNullOrWhiteSpace(TreeFilter.FilterString))
                {
                    TreeFilter.FilterString = string.Empty;
                }

                ExpandNode(target.Parent);

                if (!ReferenceEquals(target, SelectedNode))
                {
                    SelectedNode = target;
                }

                Dispatcher.UIThread.Post(BringIntoView, DispatcherPriority.Background);
            }

            if (asynchronous || !Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(Navigate, DispatcherPriority.Background);
            }
            else
            {
                Navigate();
            }
        }

        private bool IsNodeInScope(TreeNode node)
        {
            if (!IsScoped || ScopedNode is null)
            {
                return true;
            }

            var current = node;
            while (current is not null)
            {
                if (ReferenceEquals(current, ScopedNode))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private void UpdateCommandStates()
        {
            _previewSourceCommand.RaiseCanExecuteChanged();
            _navigateToSourceCommand.RaiseCanExecuteChanged();
            _deleteNodeCommand.RaiseCanExecuteChanged();
            _addToMultiSelectionCommand.RaiseCanExecuteChanged();
            _removeFromMultiSelectionCommand.RaiseCanExecuteChanged();
            _clearMultiSelectionCommand.RaiseCanExecuteChanged();
            _editTemplateCommand.RaiseCanExecuteChanged();
        }

        internal void RestoreScope(TreeNode? scoped)
        {
            if (scoped is null)
            {
                ShowFullTree();
                return;
            }

            ScopedNode = scoped;
            Nodes = new[] { scoped };
            ScopedNodeKey = BuildNodeKey(scoped);
            IsScoped = true;
            ApplyTreeFilter();
        }

        internal void RestoreScopeFromKey(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                ShowFullTree();
                return;
            }

            var node = FindNodeByKey(key!);
            if (node is null)
            {
                ShowFullTree();
                return;
            }

            ScopedNode = node;
            Nodes = new[] { node };
            ScopedNodeKey = key;
            IsScoped = true;
            ApplyTreeFilter();
        }

        private string BuildNodeKey(TreeNode node)
        {
            var segments = new Stack<int>();
            var current = node;

            while (current.Parent is { } parent)
            {
                var childIndex = GetChildIndex(parent, current);
                if (childIndex < 0)
                {
                    return string.Empty;
                }

                segments.Push(childIndex);
                current = parent;
            }

            var rootIndex = Array.IndexOf(_rootNodes, current);
            if (rootIndex < 0)
            {
                return string.Empty;
            }

            segments.Push(rootIndex);
            return string.Join(".", segments.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        }

        private static int GetChildIndex(TreeNode parent, TreeNode node)
        {
            for (var i = 0; i < parent.Children.Count; i++)
            {
                if (ReferenceEquals(parent.Children[i], node))
                {
                    return i;
                }
            }

            return -1;
        }

        private TreeNode? FindNodeByKey(string key)
        {
            var parts = key.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            TreeNode? current = null;

            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var index))
                {
                    return null;
                }

                if (i == 0)
                {
                    if (index < 0 || index >= _rootNodes.Length)
                    {
                        return null;
                    }

                    current = _rootNodes[index];
                }
                else
                {
                    if (current is null)
                    {
                        return null;
                    }

                    var children = current.Children;
                    if (index < 0 || index >= children.Count)
                    {
                        return null;
                    }

                    current = children[index];
                }
            }

            return current;
        }

        protected virtual bool MatchesSelectedControl(TreeNode node, Control control) =>
            ReferenceEquals(node.Visual, control);

        private TreeNode? FindNode(TreeNode node, Control control)
        {
            if (MatchesSelectedControl(node, control))
            {
                return ResolveSelectionTarget(node);
            }
            else
            {
                foreach (var child in node.Children)
                {
                    var result = FindNode(child, control);

                    if (result != null)
                    {
                        return result;
                    }
                }
            }

            return null;
        }

        internal void UpdatePropertiesView()
        {
            Details?.UpdatePropertiesView(MainView?.ShowImplementedInterfaces ?? true);
        }

        protected virtual bool CanNodeMatch(TreeNode node) => true;

        protected void ApplyTreeFilter()
        {
            ClearSearchHighlights();

            var hasFilter = !string.IsNullOrWhiteSpace(TreeFilter.FilterString);

            if (ScopedNode is { } scoped)
            {
                if (hasFilter)
                {
                    FilterNode(scoped, ancestorMatch: false);
                }
                else
                {
                    ResetVisibility(scoped);
                }
            }
            else
            {
                foreach (var root in _rootNodes)
                {
                    if (hasFilter)
                    {
                        FilterNode(root, ancestorMatch: false);
                    }
                    else
                    {
                        ResetVisibility(root);
                    }
                }
            }

            if (hasFilter && SelectedNode is { } selected && !selected.IsVisible)
            {
                SelectedNode = null;
            }

            RefreshNodesSource();

            bool FilterNode(TreeNode node, bool ancestorMatch)
            {
                var matches = hasFilter && CanNodeMatch(node) && NodeMatchesFilter(node);
                node.IsSearchMatch = matches;
                var hasMatchInChildren = false;

                foreach (var child in node.Children)
                {
                    var childHasMatch = FilterNode(child, ancestorMatch || matches);

                    if (hasFilter && !childHasMatch)
                    {
                        child.IsExpanded = false;
                    }

                    if (childHasMatch)
                    {
                        hasMatchInChildren = true;
                    }
                }

                var hasResult = matches || hasMatchInChildren;
                node.IsVisible = hasFilter ? (hasResult || ancestorMatch && !CanNodeMatch(node)) : true;

                if (hasFilter && node.HasChildren)
                {
                    node.IsExpanded = hasResult;
                }

                return hasResult;
            }

            void ResetVisibility(TreeNode node)
            {
                node.IsSearchMatch = false;
                node.IsVisible = true;
                foreach (var child in node.Children)
                {
                    ResetVisibility(child);
                }
            }
        }

        private void ClearSearchHighlights()
        {
            foreach (var root in _rootNodes)
            {
                ClearSearchMatchRecursive(root);
            }
        }

        private static void ClearSearchMatchRecursive(TreeNode node)
        {
            node.IsSearchMatch = false;

            foreach (var child in node.Children)
            {
                ClearSearchMatchRecursive(child);
            }
        }

        private void RefreshNodesSource()
        {
            if (IsScoped && ScopedNode is { } scoped)
            {
                Nodes = new[] { scoped };
                return;
            }

            if (_rootNodes.Length == 0)
            {
                Nodes = Array.Empty<TreeNode>();
                return;
            }

            var copy = new TreeNode[_rootNodes.Length];
            Array.Copy(_rootNodes, copy, _rootNodes.Length);
            Nodes = copy;
        }

        private void RebuildNodeSubscriptions()
        {
            ClearNodeSubscriptions();

            if (_nodes is null)
            {
                return;
            }

            foreach (var root in _nodes)
            {
                SubscribeNodeRecursive(root);
            }
        }

        private void ClearNodeSubscriptions()
        {
            if (_trackedNodes.Count == 0)
            {
                return;
            }

            foreach (var node in _trackedNodes.ToArray())
            {
                node.PropertyChanged -= OnNodePropertyChanged;
                node.CollectionChanged -= OnNodeCollectionChanged;
            }

            _trackedNodes.Clear();
        }

        private void SubscribeNodeRecursive(TreeNode node)
        {
            if (!_trackedNodes.Add(node))
            {
                return;
            }

            node.PropertyChanged += OnNodePropertyChanged;
            node.CollectionChanged += OnNodeCollectionChanged;

            foreach (var child in node.Children)
            {
                SubscribeNodeRecursive(child);
            }
        }

        private void UnsubscribeNodeRecursive(TreeNode node)
        {
            if (!_trackedNodes.Remove(node))
            {
                return;
            }

            node.PropertyChanged -= OnNodePropertyChanged;
            node.CollectionChanged -= OnNodeCollectionChanged;

            foreach (var child in node.Children)
            {
                UnsubscribeNodeRecursive(child);
            }
        }

        private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not TreeNode)
            {
                return;
            }

            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(TreeNode.IsExpanded) ||
                e.PropertyName == nameof(TreeNode.IsVisible))
            {
                UpdateTreeStats();
            }
        }

        private void OnNodeCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                RebuildNodeSubscriptions();
                UpdateTreeStats();
                return;
            }

            if (e.NewItems is not null)
            {
                foreach (var node in e.NewItems.OfType<TreeNode>())
                {
                    SubscribeNodeRecursive(node);
                }
            }

            if (e.OldItems is not null)
            {
                foreach (var node in e.OldItems.OfType<TreeNode>())
                {
                    UnsubscribeNodeRecursive(node);
                }
            }

            UpdateTreeStats();
        }

        private void UpdateTreeStats()
        {
            var total = 0;
            var expanded = 0;

            if (_nodes is not null)
            {
                foreach (var root in _nodes)
                {
                    CountNode(root, ref total, ref expanded);
                }
            }

            TotalNodeCount = total;
            ExpandedNodeCount = expanded;
        }

        private static void CountNode(TreeNode node, ref int total, ref int expanded)
        {
            if (!node.IsVisible)
            {
                return;
            }

            total++;

            if (node.IsExpanded)
            {
                expanded++;
            }

            foreach (var child in node.Children)
            {
                CountNode(child, ref total, ref expanded);
            }
        }

        private bool NodeMatchesFilter(TreeNode node)
        {
            var searchText = SelectedTreeSearchField switch
            {
                TreeSearchField.TypeName => BuildTypeSearchText(node),
                TreeSearchField.Classes => BuildClassesSearchText(node),
                TreeSearchField.Name => BuildNameSearchText(node),
                _ => node.SearchText,
            };

            if (string.IsNullOrWhiteSpace(searchText))
            {
                return false;
            }

            return TreeFilter.Filter(searchText);
        }

        private static string BuildTypeSearchText(TreeNode node)
        {
            var visualType = node.Visual?.GetType();

            return string.Join(" ", new[]
                {
                    node.Type,
                    visualType?.Name,
                    visualType?.FullName,
                }.Where(x => !string.IsNullOrWhiteSpace(x))
                 .Select(x => x!.Trim()));
        }

        private static string BuildClassesSearchText(TreeNode node)
        {
            if (node.Visual is StyledElement styled && styled.Classes.Count > 0)
            {
                var tokens = styled.Classes.SelectMany(cls =>
                {
                    if (string.IsNullOrWhiteSpace(cls))
                    {
                        return Array.Empty<string>();
                    }

                    if (cls.StartsWith(":", StringComparison.Ordinal))
                    {
                        return new[] { cls };
                    }

                    return new[] { cls, "." + cls };
                });

                var text = string.Join(" ", tokens.Where(x => !string.IsNullOrWhiteSpace(x))
                                                   .Select(x => x.Trim()))
                                   .Trim();

                return text;
            }

            return string.Empty;
        }

        private static string BuildNameSearchText(TreeNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.ElementName))
            {
                return node.ElementName!;
            }

            if (node.Visual is StyledElement styled && !string.IsNullOrWhiteSpace(styled.Name))
            {
                return styled.Name!;
            }

            return string.Empty;
        }

        private sealed class DocumentNodeIndex
        {
            private readonly object _gate = new();
            private readonly List<NodeLookupEntry> _entries = new();

            public void AddOrUpdate(TreeNode node, int startLine, int endLine)
            {
                lock (_gate)
                {
                    var normalizedEnd = Math.Max(endLine, startLine);
                    var replacement = new NodeLookupEntry(node, startLine, normalizedEnd);
                    var existingIndex = _entries.FindIndex(e => ReferenceEquals(e.Node, node));
                    if (existingIndex >= 0)
                    {
                        _entries[existingIndex] = replacement;
                    }
                    else
                    {
                        _entries.Add(replacement);
                    }
                }
            }

            public void Remove(TreeNode node)
            {
                lock (_gate)
                {
                    for (var i = _entries.Count - 1; i >= 0; i--)
                    {
                        if (ReferenceEquals(_entries[i].Node, node))
                        {
                            _entries.RemoveAt(i);
                        }
                    }
                }
            }

            public bool TryGetBestMatch(int descriptorStart, int descriptorEnd, out TreeNode? node)
            {
                lock (_gate)
                {
                    NodeLookupEntry? best = null;
                    var bestEntrySpan = int.MaxValue;
                    var bestStartDiff = int.MaxValue;
                    var bestDescriptorSpan = int.MaxValue;
                    var descriptorSpan = descriptorEnd - descriptorStart;
                    if (descriptorSpan < 0)
                    {
                        descriptorSpan = 0;
                    }

                    foreach (var entry in _entries)
                    {
                        if (descriptorStart < entry.StartLine || descriptorStart > entry.EndLine)
                        {
                            continue;
                        }

                        var span = descriptorEnd - descriptorStart;
                        if (span < 0)
                        {
                            span = 0;
                        }

                        var startDiff = Math.Abs(entry.StartLine - descriptorStart);
                        var entrySpan = entry.EndLine - entry.StartLine;
                        if (entrySpan < 0)
                        {
                            entrySpan = 0;
                        }

                        if (best is null ||
                            entrySpan < bestEntrySpan ||
                            (entrySpan == bestEntrySpan && (startDiff < bestStartDiff ||
                                                            (startDiff == bestStartDiff && descriptorSpan < bestDescriptorSpan))))
                        {
                            best = entry;
                            bestEntrySpan = entrySpan;
                            bestStartDiff = startDiff;
                            bestDescriptorSpan = descriptorSpan;
                        }
                    }

                    if (best is { } value && (bestStartDiff == 0 || bestEntrySpan == 0))
                    {
                        node = value.Node;
                        return true;
                    }

                    node = null;
                    return false;
                }
            }

            private readonly struct NodeLookupEntry
            {
                public NodeLookupEntry(TreeNode node, int startLine, int endLine)
                {
                    Node = node;
                    StartLine = startLine;
                    EndLine = endLine;
                }

                public TreeNode Node { get; }
                public int StartLine { get; }
                public int EndLine { get; }
            }
        }

        private readonly record struct DocumentReference(
            TreeNode Node,
            string? NormalizedPath,
            string? RemoteKey,
            int StartLine,
            int EndLine,
            XamlAstNodeId? DescriptorId);

        private bool TryBuildDocumentReference(TreeNode node, out DocumentReference reference)
        {
            string? normalizedPath = null;
            string? remoteKey = null;
            int startLine = -1;
            int endLine = -1;
            XamlAstNodeId? descriptorId = null;

            if (node.SourceInfo is { } info)
            {
                if (!string.IsNullOrWhiteSpace(info.LocalPath))
                {
                    normalizedPath = NormalizeLocalPath(info.LocalPath!);
                    if (!string.IsNullOrWhiteSpace(normalizedPath))
                    {
                        remoteKey = GetRemoteKeyForNormalizedPath(normalizedPath!);
                    }
                }
                else if (info.RemoteUri is { } remoteUri)
                {
                    remoteKey = remoteUri.AbsoluteUri;
                    if (_remoteDocumentPaths.TryGetValue(remoteUri.AbsoluteUri, out var mappedPath))
                    {
                        normalizedPath ??= mappedPath;
                    }
                }

                if (info.StartLine is int srcStart)
                {
                    startLine = srcStart;
                    endLine = info.EndLine ?? srcStart;
                }
            }

            if (node.XamlDescriptor is { } descriptor)
            {
                descriptorId = descriptor.Id;

                if (TryGetDescriptorDocumentPath(descriptor.Id, out var descriptorPath) && !string.IsNullOrWhiteSpace(descriptorPath))
                {
                    if (File.Exists(descriptorPath))
                    {
                        normalizedPath ??= NormalizeLocalPath(descriptorPath);
                        if (normalizedPath is not null)
                        {
                            remoteKey ??= GetRemoteKeyForNormalizedPath(normalizedPath);
                        }
                    }
                    else if (Uri.TryCreate(descriptorPath, UriKind.Absolute, out var descriptorUri))
                    {
                        remoteKey ??= descriptorUri.AbsoluteUri;
                        if (_remoteDocumentPaths.TryGetValue(remoteKey!, out var mappedPath))
                        {
                            normalizedPath ??= mappedPath;
                        }
                    }
                    else
                    {
                        normalizedPath ??= NormalizeLocalPath(descriptorPath);
                    }
                }

                if (startLine < 0)
                {
                    startLine = Math.Max(descriptor.LineSpan.Start.Line, 1);
                    endLine = Math.Max(descriptor.LineSpan.End.Line, startLine);
                }
            }

            if (string.IsNullOrWhiteSpace(normalizedPath) && string.IsNullOrWhiteSpace(remoteKey))
            {
                reference = default;
                return false;
            }

            if (startLine < 0)
            {
                startLine = 1;
            }

            if (endLine < startLine)
            {
                endLine = startLine;
            }

            remoteKey ??= normalizedPath is not null ? GetRemoteKeyForNormalizedPath(normalizedPath) : null;

            reference = new DocumentReference(node, normalizedPath, remoteKey, startLine, endLine, descriptorId);
            return true;
        }

        private bool PathMatches(DocumentReference reference, string? selectionNormalized, string? selectionRemoteKey, string? rawSelectionPath)
        {
            if (!string.IsNullOrWhiteSpace(selectionNormalized) && !string.IsNullOrWhiteSpace(reference.NormalizedPath))
            {
                if (PathsEqual(reference.NormalizedPath!, selectionNormalized!))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(selectionRemoteKey))
            {
                if (!string.IsNullOrWhiteSpace(reference.RemoteKey) &&
                    string.Equals(reference.RemoteKey, selectionRemoteKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(reference.NormalizedPath))
                {
                    var referenceRemote = GetRemoteKeyForNormalizedPath(reference.NormalizedPath!);
                    if (!string.IsNullOrWhiteSpace(referenceRemote) &&
                        string.Equals(referenceRemote, selectionRemoteKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(rawSelectionPath) &&
                Uri.TryCreate(rawSelectionPath, UriKind.Absolute, out var selectionUri))
            {
                var selectionUriString = selectionUri.AbsoluteUri;
                if (!string.IsNullOrWhiteSpace(reference.RemoteKey) &&
                    string.Equals(reference.RemoteKey, selectionUriString, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(reference.NormalizedPath) &&
                    Uri.TryCreate(reference.NormalizedPath, UriKind.Absolute, out var referenceUri) &&
                    string.Equals(referenceUri.AbsoluteUri, selectionUriString, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(selectionNormalized) && !string.IsNullOrWhiteSpace(reference.RemoteKey) &&
                _remoteDocumentPaths.TryGetValue(reference.RemoteKey!, out var mappedPath) &&
                PathsEqual(mappedPath, selectionNormalized!))
            {
                return true;
            }

            return false;
        }

        private string? GetRemoteKeyForNormalizedPath(string path)
        {
            if (_cachedDocumentRemoteIndex.TryGetValue(path, out var remoteKey) && !string.IsNullOrWhiteSpace(remoteKey))
            {
                return remoteKey;
            }

            foreach (var pair in _remoteDocumentPaths)
            {
                if (PathsEqual(pair.Value, path))
                {
                    return pair.Key;
                }
            }

            return null;
        }

        private static int ComputeMatchPenalty(DocumentReference reference, int descriptorStart, int descriptorEnd)
        {
            var startLine = reference.StartLine;
            var endLine = reference.EndLine;

            if (startLine <= descriptorStart && descriptorStart <= endLine)
            {
                var referenceSpan = endLine - startLine;
                var selectionSpan = Math.Max(descriptorEnd - descriptorStart, 0);
                return Math.Abs(referenceSpan - selectionSpan);
            }

            return Math.Abs(startLine - descriptorStart);
        }

        private sealed class SelectionOverlayService : IDisposable
        {
            private readonly MainViewModel _mainView;
            private readonly PropertyChangedEventHandler _propertyChangedHandler;
            private TreeNode? _currentNode;
            private IDisposable? _currentAdorner;

            public SelectionOverlayService(MainViewModel mainView)
            {
                _mainView = mainView ?? throw new ArgumentNullException(nameof(mainView));
                _propertyChangedHandler = OnMainViewPropertyChanged;
                _mainView.PropertyChanged += _propertyChangedHandler;
            }

            public void OnCoordinatorSelection(TreeNode? node)
            {
                _currentNode = node;
                ScheduleUpdate();
            }

            private void ScheduleUpdate()
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    ApplyOverlay();
                }
                else
                {
                    Dispatcher.UIThread.Post(ApplyOverlay, DispatcherPriority.Background);
                }
            }

            private void ApplyOverlay()
            {
                _currentAdorner?.Dispose();
                _currentAdorner = null;

                if (_currentNode?.Visual is not Visual visual)
                {
                    return;
                }

                if (visual.DoesBelongToDevTool())
                {
                    return;
                }

                _currentAdorner = ControlHighlightAdorner.Add(visual, _mainView.ShouldVisualizeMarginPadding);
            }

            private void OnMainViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (_currentNode is null)
                {
                    return;
                }

                if (string.IsNullOrEmpty(e.PropertyName) ||
                    e.PropertyName == nameof(MainViewModel.ShouldVisualizeMarginPadding))
                {
                    ScheduleUpdate();
                }
            }

            public void Dispose()
            {
                _mainView.PropertyChanged -= _propertyChangedHandler;

                if (Dispatcher.UIThread.CheckAccess())
                {
                    _currentAdorner?.Dispose();
                }
                else
                {
                    Dispatcher.UIThread.Post(() => _currentAdorner?.Dispose(), DispatcherPriority.Background);
                }

                _currentAdorner = null;
                _currentNode = null;
            }
        }

        private sealed class LayoutHandleService : IDisposable
        {
            private readonly RuntimeMutationCoordinator _runtimeCoordinator;
            private readonly Func<PropertyInspectorChangeEmitter?> _emitterProvider;
            private LayoutController? _controller;

            public LayoutHandleService(
                RuntimeMutationCoordinator runtimeCoordinator,
                Func<PropertyInspectorChangeEmitter?> emitterProvider)
            {
                _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
                _emitterProvider = emitterProvider ?? throw new ArgumentNullException(nameof(emitterProvider));
            }

            public void UpdateSelection(TreeNode? node, XamlAstSelection? selection)
            {
                _controller?.Dispose();
                _controller = null;

                if (node?.Visual is not Control control)
                {
                    return;
                }

                if (selection?.Document is null || selection.Node is null)
                {
                    return;
                }

                var parent = control.VisualParent;

                LayoutController? controller = parent switch
                {
                    Canvas canvas => new CanvasLayoutController(this, control, canvas, node, selection),
                    Grid grid => new GridLayoutController(this, control, grid, node, selection),
                    StackPanel stack => new StackPanelLayoutController(this, control, stack, node, selection),
                    DockPanel dock => new DockLayoutController(this, control, dock, node, selection),
                    _ => null
                };

                if (controller is null)
                {
                    return;
                }

                controller.Attach();
                _controller = controller;
            }

            public void Dispose()
            {
                _controller?.Dispose();
                _controller = null;
            }

            internal RuntimeMutationCoordinator RuntimeCoordinator => _runtimeCoordinator;

            private PropertyInspectorChangeEmitter? ChangeEmitter => _emitterProvider();

            internal async Task CommitAsync(
                TreeNode node,
                XamlAstSelection selection,
                IReadOnlyList<LayoutPropertyChange> changes,
                string gesture,
                EditorCommandDescriptor? command = null)
            {
                if (changes is null || changes.Count == 0)
                {
                    return;
                }

                if (selection.Document is null || selection.Node is null)
                {
                    return;
                }

                var emitter = ChangeEmitter;
                if (emitter is null)
                {
                    return;
                }

                var primary = changes[0];
                var primaryContext = new PropertyChangeContext(
                    node.Visual,
                    primary.Property,
                    selection.Document,
                    selection.Node,
                    frame: "LocalValue",
                    valueSource: "LocalValue");

                List<PropertyChangeContext>? additionalContexts = null;
                List<object?>? additionalPrevious = null;

                if (changes.Count > 1)
                {
                    additionalContexts = new List<PropertyChangeContext>(changes.Count - 1);
                    additionalPrevious = new List<object?>(changes.Count - 1);

                    for (var index = 1; index < changes.Count; index++)
                    {
                        var change = changes[index];
                        additionalContexts.Add(new PropertyChangeContext(
                            node.Visual,
                            change.Property,
                            selection.Document,
                            selection.Node,
                            frame: "LocalValue",
                            valueSource: "LocalValue"));
                        additionalPrevious.Add(change.OldValue);
                    }
                }

                try
                {
                    await emitter.EmitLocalValueChangeAsync(
                        primaryContext,
                        primary.NewValue,
                        primary.OldValue,
                        gesture,
                        command,
                        additionalContexts,
                        additionalPrevious).ConfigureAwait(false);
                }
                catch
                {
                    // Swallow errors – the runtime state has already been updated and
                    // the mutation dispatcher will surface detailed diagnostics elsewhere.
                }
            }

            internal readonly struct LayoutPropertyChange
            {
                public LayoutPropertyChange(AvaloniaProperty property, object? oldValue, object? newValue)
                {
                    Property = property ?? throw new ArgumentNullException(nameof(property));
                    OldValue = oldValue;
                    NewValue = newValue;
                }

                public AvaloniaProperty Property { get; }

                public object? OldValue { get; }

                public object? NewValue { get; }
            }

            private abstract class LayoutController : LayoutHandleAdorner.ILayoutHandleBehavior, IDisposable
            {
                protected LayoutHandleService Owner { get; }
                protected Control Target { get; }
                protected TreeNode Node { get; }
                protected XamlAstSelection Selection { get; }
                protected IDisposable? AdornerToken { get; private set; }
                protected SnapGuideAdorner.Handle? GuideHandle { get; private set; }

                protected LayoutController(LayoutHandleService owner, Control target, TreeNode node, XamlAstSelection selection)
                {
                    Owner = owner;
                    Target = target;
                    Node = node;
                    Selection = selection;
                }

                public virtual void Attach()
                {
                    AdornerToken = LayoutHandleAdorner.Add(Target, this);
                }

                public virtual void Dispose()
                {
                    AdornerToken?.Dispose();
                    AdornerToken = null;
                    GuideHandle?.Dispose();
                    GuideHandle = null;
                }

                public abstract IEnumerable<Rect> GetHandleRects(Rect bounds, double handleSize);

                public abstract void OnPointerPressed(LayoutHandleAdorner sender, PointerPressedEventArgs e);

                public abstract void OnPointerMoved(LayoutHandleAdorner sender, PointerEventArgs e);

                public abstract void OnPointerReleased(LayoutHandleAdorner sender, PointerReleasedEventArgs e);

                public abstract void OnPointerCaptureLost(LayoutHandleAdorner sender, PointerCaptureLostEventArgs e);

                protected static double Normalize(double value)
                    => Math.Round(value, 2, MidpointRounding.AwayFromZero);

                protected static bool AreClose(double left, double right)
                    => Math.Abs(left - right) < 0.5;

                protected void AttachGuides(Visual visual)
                {
                    GuideHandle?.Dispose();
                    GuideHandle = SnapGuideAdorner.Add(visual);
                }

                protected void UpdateGuides(IReadOnlyList<SnapGuideAdorner.GuideSegment> guides)
                {
                    if (GuideHandle is { } handle)
                    {
                        handle.Adorner.UpdateGuides(guides);
                    }
                }

                protected void ClearGuides()
                {
                    if (GuideHandle is { } handle)
                    {
                        handle.Adorner.Clear();
                    }
                }
            }

            private sealed class CanvasLayoutController : LayoutController
            {
                private readonly Canvas _canvas;
                private RuntimeMutationCoordinator.PointerGestureSession? _gesture;
                private bool _isDragging;
                private Point _startCanvasPoint;
                private double _initialLeft;
                private double _initialTop;
                private double _currentLeft;
                private double _currentTop;
                private readonly List<Rect> _siblingRects = new();
                private readonly List<SnapGuideAdorner.GuideSegment> _guideSegments = new();
                private const double SnapThreshold = 6.0;

                public CanvasLayoutController(LayoutHandleService owner, Control target, Canvas canvas, TreeNode node, XamlAstSelection selection)
                    : base(owner, target, node, selection)
                {
                    _canvas = canvas;
                }

                public override void Attach()
                {
                    base.Attach();
                    AttachGuides(_canvas);
                }

                public override IEnumerable<Rect> GetHandleRects(Rect bounds, double handleSize)
                {
                    var half = handleSize / 2;
                    yield return new Rect(bounds.TopLeft - new Point(half, half), new Size(handleSize, handleSize));
                    yield return new Rect(bounds.TopRight - new Point(half, half), new Size(handleSize, handleSize));
                    yield return new Rect(bounds.BottomLeft - new Point(half, half), new Size(handleSize, handleSize));
                    yield return new Rect(bounds.BottomRight - new Point(half, half), new Size(handleSize, handleSize));
                }

                public override void OnPointerPressed(LayoutHandleAdorner sender, PointerPressedEventArgs e)
                {
                    if (_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(sender);
                    _isDragging = true;

                    _gesture = Owner.RuntimeCoordinator.BeginPointerGesture();
                    _startCanvasPoint = e.GetPosition(_canvas);
                    _initialLeft = GetCanvasValue(Canvas.GetLeft);
                    _initialTop = GetCanvasValue(Canvas.GetTop);
                    _currentLeft = _initialLeft;
                    _currentTop = _initialTop;
                    e.Handled = true;
                }

                public override void OnPointerMoved(LayoutHandleAdorner sender, PointerEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    var current = e.GetPosition(_canvas);
                    var delta = current - _startCanvasPoint;

                    var newLeft = Normalize(_initialLeft + delta.X);
                    var newTop = Normalize(_initialTop + delta.Y);

                    var guides = ComputeSnapGuides(ref newLeft, ref newTop);

                    if (AreClose(newLeft, _currentLeft) && AreClose(newTop, _currentTop))
                    {
                        UpdateGuides(guides);
                        return;
                    }

                    _currentLeft = newLeft;
                    _currentTop = newTop;

                    Canvas.SetLeft(Target, newLeft);
                    Canvas.SetTop(Target, newTop);

                    Owner.RuntimeCoordinator.RegisterPropertyChange(Target, Canvas.LeftProperty, _initialLeft, newLeft);
                    Owner.RuntimeCoordinator.RegisterPropertyChange(Target, Canvas.TopProperty, _initialTop, newTop);

                    UpdateGuides(guides);
                    e.Handled = true;
                }

                public override async void OnPointerReleased(LayoutHandleAdorner sender, PointerReleasedEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(null);
                    _isDragging = false;

                    ClearGuides();

                    var hasLeftChange = !AreClose(_currentLeft, _initialLeft);
                    var hasTopChange = !AreClose(_currentTop, _initialTop);

                    var gesture = _gesture;
                    _gesture = null;

                    if (!hasLeftChange && !hasTopChange)
                    {
                        gesture?.Cancel();
                        Canvas.SetLeft(Target, _initialLeft);
                        Canvas.SetTop(Target, _initialTop);
                        return;
                    }

                    gesture?.Complete();

                    var changes = new List<LayoutPropertyChange>();
                    if (hasLeftChange)
                    {
                        changes.Add(new LayoutPropertyChange(Canvas.LeftProperty, _initialLeft, _currentLeft));
                        _initialLeft = _currentLeft;
                    }

                    if (hasTopChange)
                    {
                        changes.Add(new LayoutPropertyChange(Canvas.TopProperty, _initialTop, _currentTop));
                        _initialTop = _currentTop;
                    }

                    await Owner.CommitAsync(Node, Selection, changes, "CanvasDrag", EditorCommandDescriptor.Slider).ConfigureAwait(false);
                }

                public override void OnPointerCaptureLost(LayoutHandleAdorner sender, PointerCaptureLostEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    _isDragging = false;
                    _gesture?.Cancel();
                    _gesture = null;
                    Canvas.SetLeft(Target, _initialLeft);
                    Canvas.SetTop(Target, _initialTop);
                    ClearGuides();
                }

                private double GetCanvasValue(Func<Control, double> getter)
                {
                    var value = getter(Target);
                    return double.IsNaN(value) ? 0 : value;
                }

                private IReadOnlyList<SnapGuideAdorner.GuideSegment> ComputeSnapGuides(ref double newLeft, ref double newTop)
                {
                    _guideSegments.Clear();

                    var width = Target.Bounds.Width;
                    var height = Target.Bounds.Height;
                    if (width <= 0 || height <= 0)
                    {
                        return _guideSegments;
                    }

                    CollectSiblingRects(_siblingRects);
                    if (_siblingRects.Count == 0)
                    {
                        return _guideSegments;
                    }

                    var verticalCandidate = SnapCandidate.None;
                    var horizontalCandidate = SnapCandidate.None;

                    var targetLeft = newLeft;
                    var targetTop = newTop;
                    var targetRight = targetLeft + width;
                    var targetBottom = targetTop + height;
                    var targetCenterX = targetLeft + (width / 2);
                    var targetCenterY = targetTop + (height / 2);

                    foreach (var rect in _siblingRects)
                    {
                        EvaluateVertical(rect.Left, targetLeft, rect, ref verticalCandidate);
                        EvaluateVertical(rect.Left + (rect.Width / 2), targetCenterX, rect, ref verticalCandidate);
                        EvaluateVertical(rect.Right, targetRight, rect, ref verticalCandidate);

                        EvaluateHorizontal(rect.Top, targetTop, rect, ref horizontalCandidate);
                        EvaluateHorizontal(rect.Top + (rect.Height / 2), targetCenterY, rect, ref horizontalCandidate);
                        EvaluateHorizontal(rect.Bottom, targetBottom, rect, ref horizontalCandidate);
                    }

                    if (verticalCandidate.IsValid && verticalCandidate.AbsDifference <= SnapThreshold)
                    {
                        newLeft += verticalCandidate.Difference;
                    }

                    if (horizontalCandidate.IsValid && horizontalCandidate.AbsDifference <= SnapThreshold)
                    {
                        newTop += horizontalCandidate.Difference;
                    }

                    if (verticalCandidate.IsValid && verticalCandidate.AbsDifference <= SnapThreshold)
                    {
                        var start = Math.Min(verticalCandidate.ReferenceRect.Top, newTop);
                        var end = Math.Max(verticalCandidate.ReferenceRect.Bottom, newTop + height);
                        _guideSegments.Add(new SnapGuideAdorner.GuideSegment(true, verticalCandidate.Position, start, end));
                    }

                    if (horizontalCandidate.IsValid && horizontalCandidate.AbsDifference <= SnapThreshold)
                    {
                        var start = Math.Min(horizontalCandidate.ReferenceRect.Left, newLeft);
                        var end = Math.Max(horizontalCandidate.ReferenceRect.Right, newLeft + width);
                        _guideSegments.Add(new SnapGuideAdorner.GuideSegment(false, horizontalCandidate.Position, start, end));
                    }

                    return _guideSegments;

                    static void EvaluateVertical(double candidate, double targetValue, Rect reference, ref SnapCandidate current)
                    {
                        var diff = candidate - targetValue;
                        var absDiff = Math.Abs(diff);

                        if (!current.IsValid || absDiff < current.AbsDifference)
                        {
                            current = new SnapCandidate(true, candidate, diff, absDiff, reference);
                        }
                    }

                    static void EvaluateHorizontal(double candidate, double targetValue, Rect reference, ref SnapCandidate current)
                    {
                        var diff = candidate - targetValue;
                        var absDiff = Math.Abs(diff);

                        if (!current.IsValid || absDiff < current.AbsDifference)
                        {
                            current = new SnapCandidate(false, candidate, diff, absDiff, reference);
                        }
                    }
                }

                private void CollectSiblingRects(List<Rect> buffer)
                {
                    buffer.Clear();

                    if (Node.Parent is { } parentNode)
                    {
                        foreach (var child in parentNode.Children)
                        {
                            if (ReferenceEquals(child, Node))
                            {
                                continue;
                            }

                            if (child is TreeNode treeNode && treeNode.Visual is Control control)
                            {
                                var origin = control.TranslatePoint(default, _canvas);
                                if (origin is { } point)
                                {
                                    var size = control.Bounds.Size;
                                    if (size.Width > 0 && size.Height > 0)
                                    {
                                        buffer.Add(new Rect(point, size));
                                    }
                                }
                            }
                        }
                    }

                    var canvasSize = _canvas.Bounds.Size;
                    if (canvasSize.Width > 0 && canvasSize.Height > 0)
                    {
                        buffer.Add(new Rect(new Point(0, 0), canvasSize));
                    }
                }

                private readonly struct SnapCandidate
                {
                    public static SnapCandidate None => default;

                    public SnapCandidate(bool isVertical, double position, double difference, double absDifference, Rect referenceRect)
                    {
                        IsVertical = isVertical;
                        Position = position;
                        Difference = difference;
                        AbsDifference = absDifference;
                        ReferenceRect = referenceRect;
                        IsValid = true;
                    }

                    public bool IsVertical { get; }
                    public double Position { get; }
                    public double Difference { get; }
                    public double AbsDifference { get; }
                    public Rect ReferenceRect { get; }
                    public bool IsValid { get; }
                }
            }

            private sealed class GridLayoutController : LayoutController
            {
                private readonly Grid _grid;
                private RuntimeMutationCoordinator.PointerGestureSession? _gesture;
                private bool _isDragging;
                private int _initialRow;
                private int _initialColumn;
                private int _currentRow;
                private int _currentColumn;

                public GridLayoutController(LayoutHandleService owner, Control target, Grid grid, TreeNode node, XamlAstSelection selection)
                    : base(owner, target, node, selection)
                {
                    _grid = grid;
                }

                public override IEnumerable<Rect> GetHandleRects(Rect bounds, double handleSize)
                {
                    yield return CenterHandle(bounds, handleSize);
                }

                public override void OnPointerPressed(LayoutHandleAdorner sender, PointerPressedEventArgs e)
                {
                    if (_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(sender);
                    _isDragging = true;

                    _gesture = Owner.RuntimeCoordinator.BeginPointerGesture();
                    _initialRow = Grid.GetRow(Target);
                    _initialColumn = Grid.GetColumn(Target);
                    _currentRow = _initialRow;
                    _currentColumn = _initialColumn;
                    e.Handled = true;
                }

                public override void OnPointerMoved(LayoutHandleAdorner sender, PointerEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    var position = e.GetPosition(_grid);
                    var row = ResolveRow(position);
                    var column = ResolveColumn(position);

                    var changed = false;
                    if (row != _currentRow)
                    {
                        _currentRow = row;
                        Grid.SetRow(Target, row);
                        Owner.RuntimeCoordinator.RegisterPropertyChange(Target, Grid.RowProperty, _initialRow, row);
                        changed = true;
                    }

                    if (column != _currentColumn)
                    {
                        _currentColumn = column;
                        Grid.SetColumn(Target, column);
                        Owner.RuntimeCoordinator.RegisterPropertyChange(Target, Grid.ColumnProperty, _initialColumn, column);
                        changed = true;
                    }

                    if (changed)
                    {
                        e.Handled = true;
                    }
                }

                public override async void OnPointerReleased(LayoutHandleAdorner sender, PointerReleasedEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(null);
                    _isDragging = false;

                    var rowChanged = _currentRow != _initialRow;
                    var columnChanged = _currentColumn != _initialColumn;

                    var gesture = _gesture;
                    _gesture = null;

                    if (!rowChanged && !columnChanged)
                    {
                        gesture?.Cancel();
                        Grid.SetRow(Target, _initialRow);
                        Grid.SetColumn(Target, _initialColumn);
                        return;
                    }

                    gesture?.Complete();

                    var changes = new List<LayoutPropertyChange>();
                    if (rowChanged)
                    {
                        changes.Add(new LayoutPropertyChange(Grid.RowProperty, _initialRow, _currentRow));
                        _initialRow = _currentRow;
                    }

                    if (columnChanged)
                    {
                        changes.Add(new LayoutPropertyChange(Grid.ColumnProperty, _initialColumn, _currentColumn));
                        _initialColumn = _currentColumn;
                    }

                    await Owner.CommitAsync(Node, Selection, changes, "GridReposition", EditorCommandDescriptor.Slider).ConfigureAwait(false);
                }

                public override void OnPointerCaptureLost(LayoutHandleAdorner sender, PointerCaptureLostEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    _isDragging = false;
                    _gesture?.Cancel();
                    _gesture = null;
                    Grid.SetRow(Target, _initialRow);
                    Grid.SetColumn(Target, _initialColumn);
                }

                private Rect CenterHandle(Rect bounds, double handleSize)
                {
                    var center = bounds.Center;
                    var offset = new Point(handleSize / 2, handleSize / 2);
                    return new Rect(center - offset, new Size(handleSize, handleSize));
                }

                private int ResolveRow(Point position)
                {
                    var definitions = _grid.RowDefinitions;
                    if (definitions.Count == 0)
                    {
                        return 0;
                    }

                    var accumulated = 0.0;
                    for (var index = 0; index < definitions.Count; index++)
                    {
                        var height = definitions[index].ActualHeight;
                        if (height <= 0)
                        {
                            continue;
                        }

                        accumulated += height;
                        if (position.Y <= accumulated)
                        {
                            return index;
                        }
                    }

                    return definitions.Count - 1;
                }

                private int ResolveColumn(Point position)
                {
                    var definitions = _grid.ColumnDefinitions;
                    if (definitions.Count == 0)
                    {
                        return 0;
                    }

                    var accumulated = 0.0;
                    for (var index = 0; index < definitions.Count; index++)
                    {
                        var width = definitions[index].ActualWidth;
                        if (width <= 0)
                        {
                            continue;
                        }

                        accumulated += width;
                        if (position.X <= accumulated)
                        {
                            return index;
                        }
                    }

                    return definitions.Count - 1;
                }
            }

            private sealed class StackPanelLayoutController : LayoutController
            {
                private readonly StackPanel _stackPanel;
                private readonly Orientation _orientation;
                private RuntimeMutationCoordinator.PointerGestureSession? _gesture;
                private bool _isDragging;
                private Point _startPoint;
                private Thickness _initialMargin;
                private Thickness _currentMargin;

                public StackPanelLayoutController(LayoutHandleService owner, Control target, StackPanel stackPanel, TreeNode node, XamlAstSelection selection)
                    : base(owner, target, node, selection)
                {
                    _stackPanel = stackPanel ?? throw new ArgumentNullException(nameof(stackPanel));
                    _orientation = stackPanel.Orientation;
                }

                public override void Attach()
                {
                    base.Attach();
                    AttachGuides(_stackPanel);
                }

                public override IEnumerable<Rect> GetHandleRects(Rect bounds, double handleSize)
                {
                    var center = bounds.Center;
                    var offset = handleSize / 2;
                    yield return new Rect(center - new Point(offset, offset), new Size(handleSize, handleSize));
                }

                public override void OnPointerPressed(LayoutHandleAdorner sender, PointerPressedEventArgs e)
                {
                    if (_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(sender);
                    _isDragging = true;

                    _gesture = Owner.RuntimeCoordinator.BeginPointerGesture();
                    _startPoint = e.GetPosition(_stackPanel);
                    _initialMargin = Target.Margin;
                    _currentMargin = _initialMargin;
                    e.Handled = true;
                }

                public override void OnPointerMoved(LayoutHandleAdorner sender, PointerEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    var current = e.GetPosition(_stackPanel);
                    var delta = current - _startPoint;

                    var updatedMargin = _orientation == Orientation.Vertical
                        ? new Thickness(_initialMargin.Left, Normalize(_initialMargin.Top + delta.Y), _initialMargin.Right, _initialMargin.Bottom)
                        : new Thickness(Normalize(_initialMargin.Left + delta.X), _initialMargin.Top, _initialMargin.Right, _initialMargin.Bottom);

                    if (AreMarginClose(updatedMargin, _currentMargin))
                    {
                        return;
                    }

                    _currentMargin = updatedMargin;
                    Target.Margin = updatedMargin;
                    Owner.RuntimeCoordinator.RegisterPropertyChange(Target, Layoutable.MarginProperty, _initialMargin, updatedMargin);
                    UpdateGuides(Array.Empty<SnapGuideAdorner.GuideSegment>());
                    e.Handled = true;
                }

                public override async void OnPointerReleased(LayoutHandleAdorner sender, PointerReleasedEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(null);
                    _isDragging = false;
                    ClearGuides();

                    var gesture = _gesture;
                    _gesture = null;

                    if (AreMarginClose(_currentMargin, _initialMargin))
                    {
                        gesture?.Cancel();
                        Target.Margin = _initialMargin;
                        return;
                    }

                    gesture?.Complete();

                    await Owner.CommitAsync(Node, Selection, new[]
                    {
                        new LayoutPropertyChange(Layoutable.MarginProperty, _initialMargin, _currentMargin)
                    }, "StackPanelAdjust", EditorCommandDescriptor.Slider).ConfigureAwait(false);

                    _initialMargin = _currentMargin;
                }

                public override void OnPointerCaptureLost(LayoutHandleAdorner sender, PointerCaptureLostEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    _isDragging = false;
                    _gesture?.Cancel();
                    _gesture = null;
                    Target.Margin = _initialMargin;
                    ClearGuides();
                }

                private static bool AreMarginClose(Thickness left, Thickness right) =>
                    AreClose(left.Left, right.Left) &&
                    AreClose(left.Top, right.Top) &&
                    AreClose(left.Right, right.Right) &&
                    AreClose(left.Bottom, right.Bottom);
            }

            private sealed class DockLayoutController : LayoutController
            {
                private readonly DockPanel _dockPanel;
                private RuntimeMutationCoordinator.PointerGestureSession? _gesture;
                private bool _isDragging;
                private Dock _initialDock;
                private Dock _currentDock;

                public DockLayoutController(LayoutHandleService owner, Control target, DockPanel dockPanel, TreeNode node, XamlAstSelection selection)
                    : base(owner, target, node, selection)
                {
                    _dockPanel = dockPanel;
                }

                public override void Attach()
                {
                    base.Attach();
                    AttachGuides(_dockPanel);
                }

                public override IEnumerable<Rect> GetHandleRects(Rect bounds, double handleSize)
                {
                    var thickness = handleSize;
                    yield return new Rect(bounds.X + (bounds.Width - thickness) / 2, bounds.Y - thickness / 2, thickness, thickness);
                    yield return new Rect(bounds.Right - thickness / 2, bounds.Y + (bounds.Height - thickness) / 2, thickness, thickness);
                    yield return new Rect(bounds.X + (bounds.Width - thickness) / 2, bounds.Bottom - thickness / 2, thickness, thickness);
                    yield return new Rect(bounds.X - thickness / 2, bounds.Y + (bounds.Height - thickness) / 2, thickness, thickness);
                }

                public override void OnPointerPressed(LayoutHandleAdorner sender, PointerPressedEventArgs e)
                {
                    if (_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(sender);
                    _isDragging = true;

                    _gesture = Owner.RuntimeCoordinator.BeginPointerGesture();
                    _initialDock = DockPanel.GetDock(Target);
                    _currentDock = _initialDock;
                    e.Handled = true;
                }

                public override void OnPointerMoved(LayoutHandleAdorner sender, PointerEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    var dock = ResolveDock(e.GetPosition(_dockPanel));
                    if (dock == _currentDock)
                    {
                        return;
                    }

                    _currentDock = dock;
                    DockPanel.SetDock(Target, dock);
                    Owner.RuntimeCoordinator.RegisterPropertyChange(Target, DockPanel.DockProperty, _initialDock, dock);
                    e.Handled = true;
                }

                public override async void OnPointerReleased(LayoutHandleAdorner sender, PointerReleasedEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    e.Pointer.Capture(null);
                    _isDragging = false;

                    var gesture = _gesture;
                    _gesture = null;

                    if (_currentDock == _initialDock)
                    {
                        gesture?.Cancel();
                        DockPanel.SetDock(Target, _initialDock);
                        return;
                    }

                    gesture?.Complete();

                    var change = new LayoutPropertyChange(DockPanel.DockProperty, _initialDock, _currentDock);
                    _initialDock = _currentDock;

                    await Owner.CommitAsync(Node, Selection, new[] { change }, "DockAdjust", EditorCommandDescriptor.Default).ConfigureAwait(false);
                }

                public override void OnPointerCaptureLost(LayoutHandleAdorner sender, PointerCaptureLostEventArgs e)
                {
                    if (!_isDragging)
                    {
                        return;
                    }

                    _isDragging = false;
                    _gesture?.Cancel();
                    _gesture = null;
                    DockPanel.SetDock(Target, _initialDock);
                }

                private Dock ResolveDock(Point position)
                {
                    var size = _dockPanel.Bounds.Size;
                    if (size.Width <= 0 || size.Height <= 0)
                    {
                        return _initialDock;
                    }

                    var horizontalRatio = position.X / Math.Max(size.Width, 1);
                    var verticalRatio = position.Y / Math.Max(size.Height, 1);

                    const double edgeThreshold = 0.25;

                    if (horizontalRatio <= edgeThreshold)
                    {
                        return Dock.Left;
                    }

                    if (horizontalRatio >= 1 - edgeThreshold)
                    {
                        return Dock.Right;
                    }

                    if (verticalRatio <= edgeThreshold)
                    {
                        return Dock.Top;
                    }

                    if (verticalRatio >= 1 - edgeThreshold)
                    {
                        return Dock.Bottom;
                    }

                    return _initialDock;
                }
            }
        }
    }

    public sealed record XamlAstSelection(
        XamlAstDocument Document,
        XamlAstNodeDescriptor? Node,
        IReadOnlyList<XamlAstNodeDescriptor>? DocumentNodes = null);
}
