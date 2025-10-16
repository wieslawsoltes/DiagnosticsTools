using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Threading.Tasks;
using System.Text;

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
        private readonly List<WeakReference<SourcePreviewViewModel>> _previewObservers = new();
        private readonly object _previewObserversGate = new();
        private bool _suppressPreviewNavigation;
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
            RuntimeMutationCoordinator runtimeCoordinator)
        {
            MainView = mainView;
            _rootNodes = nodes;
            _nodes = nodes;
            _pinnedProperties = pinnedProperties;
            _sourceInfoService = sourceInfoService ?? throw new ArgumentNullException(nameof(sourceInfoService));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            _xamlAstWorkspace = xamlAstWorkspace ?? throw new ArgumentNullException(nameof(xamlAstWorkspace));
            _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
            _changeEmitter = null;
            _xamlAstWorkspace.DocumentChanged += OnXamlDocumentChanged;
            _xamlAstWorkspace.NodesChanged += OnXamlNodesChanged;
            PropertiesFilter = new FilterViewModel();
            PropertiesFilter.RefreshFilter += (s, e) => Details?.PropertiesView?.Refresh();

            SettersFilter = new FilterViewModel();
            SettersFilter.RefreshFilter += (s, e) => Details?.UpdateStyleFilters();

            TreeFilter = new FilterViewModel();
            TreeFilter.RefreshFilter += (s, e) => ApplyTreeFilter();

            _previewSourceCommand = new DelegateCommand(PreviewSourceAsync, () => CanPreviewSource);
            _navigateToSourceCommand = new DelegateCommand(NavigateToSourceAsync, () => CanNavigateToSource);
            _deleteNodeCommand = new DelegateCommand(DeleteNodeAsync, () => CanDeleteNode);
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
                    UpdateCommandStates();
                }
            }
        }

        public string? SelectedNodeSourceSummary => SelectedNodeSourceInfo?.DisplayPath;

        public bool HasSelectedNodeSource => SelectedNodeSourceInfo is not null;

        public bool CanNavigateToSource => HasSelectedNodeSource;

        public bool CanPreviewSource => HasSelectedNodeSource;

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
            await PreviewSourceAsync().ConfigureAwait(false);
        }

        private async Task PreviewSourceAsync()
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
                        ? new SourcePreviewViewModel(info, _sourceNavigator, SelectedNodeXaml, NavigateToAstNode, SynchronizeSelectionFromPreview, mutationOwner: MainView, xamlAstWorkspace: _xamlAstWorkspace)
                        : SourcePreviewViewModel.CreateUnavailable(context, _sourceNavigator, mutationOwner: MainView);
                    if (info is not null)
                    {
                        RegisterPreview(preview);
                    }
                    var runtimePreview = BuildRuntimeSnapshot(node);
                    if (runtimePreview is not null)
                    {
                        preview.RuntimeComparison = runtimePreview;
                    }
                    SourcePreviewRequested?.Invoke(this, preview);
                });
            }
            catch
            {
                // Preview is best-effort.
            }
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

        private void RegisterPreview(SourcePreviewViewModel preview)
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

        private static string BuildRuntimeElementId(AvaloniaObject? element)
        {
            if (element is null)
            {
                return string.Empty;
            }

            return $"runtime://avalonia-object/{RuntimeHelpers.GetHashCode(element)}";
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
            foreach (var node in Nodes)
            {
                var result = FindNode(node, control);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private async Task<SourceInfo?> EnsureSourceInfoAsync(TreeNode node)
        {
            var task = _sourceInfoCache.GetOrAdd(node, ResolveNodeSourceInfoAsync);
            var info = await task.ConfigureAwait(false);
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
                        RegisterXamlDescriptor(targetNode, xamlSelection?.Node);
                    }
                }
            });
        }

        public void SelectControl(Control control)
        {
            var node = default(TreeNode);
            Control? c = control;

            while (node == null && c != null)
            {
                node = FindNode(c);

                if (node == null)
                {
                    c = c.GetVisualParent<Control>();
                }
            }

            if (node != null)
            {
                ExpandNode(node.Parent);
                // Use dispatcher to allow the tree to expand and render before selecting
                Dispatcher.UIThread.Post(() => SelectedNode = node, DispatcherPriority.Loaded);
            }
        }

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

            _ = SynchronizeSelectionFromPreviewAsync(selection);
        }

        private async Task SynchronizeSelectionFromPreviewAsync(XamlAstSelection selection)
        {
            try
            {
                _suppressPreviewNavigation = true;

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
                    return;
                }

                if (!Dispatcher.UIThread.CheckAccess())
                {
                    await Dispatcher.UIThread.InvokeAsync(() => ApplySelectionFromPreview(target, selection), DispatcherPriority.Background);
                }
                else
                {
                    ApplySelectionFromPreview(target, selection);
                }
            }
            finally
            {
                _suppressPreviewNavigation = false;
            }
        }

        private void ApplySelectionFromPreview(TreeNode target, XamlAstSelection selection)
        {
            RegisterXamlDescriptor(target, selection.Node);

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

        private async Task<TreeNode?> FindNodeBySelectionAsync(XamlAstSelection selection)
        {
            var descriptor = selection.Node;
            if (descriptor is null)
            {
                return null;
            }

            var documentPath = selection.Document?.Path;
            TreeNode? bestMatch = null;
            var bestSpan = int.MaxValue;

            foreach (var node in EnumerateNodes())
            {
                if (node.XamlDescriptor?.Id == descriptor.Id)
                {
                    return node;
                }

                var info = node.SourceInfo;
                if (info is null)
                {
                    info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);
                }

                if (info is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(documentPath))
                {
                    var targetPath = documentPath!;

                    if (string.IsNullOrWhiteSpace(info.LocalPath))
                    {
                        continue;
                    }

                    if (!PathsEqual(info.LocalPath!, targetPath))
                    {
                        continue;
                    }
                }

                if (info.StartLine is not int infoStart)
                {
                    continue;
                }

                var infoEnd = info.EndLine ?? infoStart;
                var descriptorStart = Math.Max(descriptor.LineSpan.Start.Line, 1);
                var descriptorEnd = Math.Max(descriptor.LineSpan.End.Line, descriptorStart);

                if (descriptorStart < infoStart || descriptorStart > infoEnd)
                {
                    continue;
                }

                var spanLength = descriptorEnd - descriptorStart;
                if (spanLength < bestSpan)
                {
                    bestSpan = spanLength;
                    bestMatch = node;
                }
            }

            return bestMatch;
        }

        private IEnumerable<TreeNode> EnumerateNodes()
        {
            var stack = new Stack<TreeNode>();

            for (var i = Nodes.Length - 1; i >= 0; i--)
            {
                stack.Push(Nodes[i]);
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

        private void RegisterXamlDescriptor(TreeNode node, XamlAstNodeDescriptor? descriptor)
        {
            lock (_nodesByXamlId)
            {
                if (node.XamlDescriptor is { } existing &&
                    _nodesByXamlId.TryGetValue(existing.Id, out var mapped) &&
                    ReferenceEquals(mapped, node))
                {
                    _nodesByXamlId.Remove(existing.Id);
                }

                node.UpdateXamlDescriptor(descriptor);

                if (descriptor is not null)
                {
                    _nodesByXamlId[descriptor.Id] = node;
                }
            }
        }

        private async Task<XamlAstSelection?> BuildXamlSelectionAsync(TreeNode node, SourceInfo? info)
        {
            if (info is null)
            {
                return null;
            }

            var path = info.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                var document = await _xamlAstWorkspace.GetDocumentAsync(path!).ConfigureAwait(false);
                var index = await _xamlAstWorkspace.GetIndexAsync(path!).ConfigureAwait(false);
                var descriptor = ResolveDescriptor(index, node, info);
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
            XamlAstNodeDescriptor? descriptor = null;

            if (info?.StartLine is int startLine && startLine > 0)
            {
                descriptor = index.Nodes
                    .Where(d =>
                    {
                        var begin = GetLineStart(d);
                        var end = GetLineEnd(d);
                        return startLine >= begin && startLine <= end;
                    })
                    .OrderBy(d => GetLineEnd(d) - GetLineStart(d))
                    .ThenBy(d => d.Path.Count)
                    .FirstOrDefault();

                if (descriptor is not null)
                {
                    return descriptor;
                }
            }

            var elementName = node.ElementName ?? (node.Visual as INamed)?.Name;
            if (!string.IsNullOrWhiteSpace(elementName))
            {
                var matches = index.FindByName(elementName!);
                if (matches.Count == 1)
                {
                    return matches[0];
                }

                if (matches.Count > 1)
                {
                    var typeName = node.Visual?.GetType().Name ?? node.Type;
                    descriptor = matches.FirstOrDefault(m => string.Equals(m.LocalName, typeName, StringComparison.Ordinal));
                    if (descriptor is not null)
                    {
                        return descriptor;
                    }
                }
            }

            var fallbackType = node.Visual?.GetType().Name ?? node.Type;
            descriptor = index.Nodes.FirstOrDefault(m => string.Equals(m.LocalName, fallbackType, StringComparison.Ordinal));
            return descriptor;
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
                return localPath!;
            }

            return SelectedNodeXaml?.Document?.Path;
        }

        private void OnXamlDocumentChanged(object? sender, XamlDocumentChangedEventArgs e)
        {
            if (SelectedNode is null)
            {
                return;
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

        private TreeNode? FindNode(TreeNode node, Control control)
        {
            if (node.Visual == control)
            {
                return node;
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
    }

    public sealed record XamlAstSelection(
        XamlAstDocument Document,
        XamlAstNodeDescriptor? Node,
        IReadOnlyList<XamlAstNodeDescriptor>? DocumentNodes = null);
}
