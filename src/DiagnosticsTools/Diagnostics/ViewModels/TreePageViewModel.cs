using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Diagnostics.Xaml;
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
        private readonly XamlAstWorkspace _xamlAstWorkspace;
        private readonly RuntimeMutationCoordinator _runtimeCoordinator;
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
            _selectionCoordinator = selectionCoordinator ?? throw new ArgumentNullException(nameof(selectionCoordinator));
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
            protected set => RaiseAndSetIfChanged(ref _nodes, value);
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
                        new ControlDetailsViewModel(this, value.Visual, _pinnedProperties, _sourceInfoService, _sourceNavigator, _runtimeCoordinator) :
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
                    var descriptor = ResolveDescriptor(index, node, null);
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
                var index = await _xamlAstWorkspace.GetIndexAsync(path!).ConfigureAwait(false);
                var descriptor = ResolveDescriptor(index, node, info);
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
                var descriptor = ResolveDescriptor(index, node, info);
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
                node.IsVisible = true;
                foreach (var child in node.Children)
                {
                    ResetVisibility(child);
                }
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
    }

    public sealed record XamlAstSelection(
        XamlAstDocument Document,
        XamlAstNodeDescriptor? Node,
        IReadOnlyList<XamlAstNodeDescriptor>? DocumentNodes = null);
}
