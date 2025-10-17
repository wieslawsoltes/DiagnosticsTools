using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Metadata;
using Avalonia.Threading;
using Avalonia.Reactive;
using Avalonia.Rendering;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Diagnostics.Metrics;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.ViewModels.Metrics;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Diagnostics.Runtime;
using Microsoft.CodeAnalysis;
using Avalonia.Diagnostics;
using Avalonia.VisualTree;

namespace Avalonia.Diagnostics.ViewModels
{
    public class MainViewModel : ViewModelBase, IDisposable
    {
        private readonly AvaloniaObject _root;
        private readonly TreePageViewModel _logicalTree;
        private readonly TreePageViewModel _visualTree;
        private readonly CombinedTreePageViewModel _combinedTree;
        private readonly EventsPageViewModel _events;
        private readonly HotKeyPageViewModel _hotKeys;
        private readonly MetricsListenerService _metricsListener;
        private readonly MetricsPageViewModel _metrics;
        private readonly IDisposable _pointerOverSubscription;
        private readonly XamlMutationDispatcher _mutationDispatcher;
        private readonly DelegateCommand _undoMutationCommand;
        private readonly DelegateCommand _redoMutationCommand;
        private readonly List<WeakReference<SourcePreviewViewModel>> _sourcePreviewObservers = new();
        private readonly object _sourcePreviewGate = new();
        private ViewModelBase? _content;
        private int _selectedTab;
        private string? _focusedControl;
        private IInputElement? _pointerOverElement;
        private bool _shouldVisualizeMarginPadding = true;
        private bool _freezePopups;
        private string? _pointerOverElementName;
        private IInputRoot? _pointerOverRoot;
        private IScreenshotHandler? _screenshotHandler;
        private bool _showPropertyType;
        private bool _showImplementedInterfaces;
        private readonly HashSet<string> _pinnedProperties = new();
        private IBrush? _FocusHighlighter;
        private IDisposable? _currentFocusHighlightAdorner = default;
        private string? _combinedTreeScopeKey;
        private string? _logicalTreeScopeKey;
        private string? _visualTreeScopeKey;
        private PropertyChangedEventHandler? _combinedTreeScopeHandler;
        private PropertyChangedEventHandler? _logicalTreeScopeHandler;
        private PropertyChangedEventHandler? _visualTreeScopeHandler;
        private ISourceInfoService _sourceInfoService;
        private ISourceNavigator _sourceNavigator;
        private readonly XamlAstWorkspace _xamlAstWorkspace;
        private readonly PropertyInspectorChangeEmitter _propertyChangeEmitter;
        private readonly Workspace? _roslynWorkspace;
        private readonly EventHandler<WorkspaceChangeEventArgs>? _workspaceChangedHandler;
        private readonly RuntimeMutationCoordinator _runtimeCoordinator;

        internal XamlAstWorkspace XamlAstWorkspace => _xamlAstWorkspace;

        public MainViewModel(AvaloniaObject root, ISourceInfoService sourceInfoService, ISourceNavigator sourceNavigator, Workspace? roslynWorkspace = null)
        {
            _root = root;
            _sourceInfoService = sourceInfoService ?? throw new ArgumentNullException(nameof(sourceInfoService));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            _roslynWorkspace = roslynWorkspace;
            _xamlAstWorkspace = new XamlAstWorkspace();
            _runtimeCoordinator = new RuntimeMutationCoordinator();
            _mutationDispatcher = new XamlMutationDispatcher(_xamlAstWorkspace, _roslynWorkspace);
            _propertyChangeEmitter = new PropertyInspectorChangeEmitter(_mutationDispatcher);
            _propertyChangeEmitter.ChangeCompleted += OnMutationCompleted;
            _propertyChangeEmitter.ExternalDocumentChanged += OnExternalDocumentChanged;
            _undoMutationCommand = new DelegateCommand(UndoMutationAsync, () => CanUndoMutation);
            _redoMutationCommand = new DelegateCommand(RedoMutationAsync, () => CanRedoMutation);
            if (_roslynWorkspace is not null)
            {
                _workspaceChangedHandler = OnRoslynWorkspaceChanged;
                _roslynWorkspace.WorkspaceChanged += _workspaceChangedHandler;
            }
            else
            {
                _workspaceChangedHandler = null;
            }
            _logicalTree = new TreePageViewModel(this, LogicalTreeNode.Create(root), _pinnedProperties, _sourceInfoService, _sourceNavigator, _xamlAstWorkspace, _runtimeCoordinator);
            _logicalTree.AttachChangeEmitter(_propertyChangeEmitter);
            _visualTree = new TreePageViewModel(this, VisualTreeNode.Create(root), _pinnedProperties, _sourceInfoService, _sourceNavigator, _xamlAstWorkspace, _runtimeCoordinator);
            _visualTree.AttachChangeEmitter(_propertyChangeEmitter);
            _combinedTree = CombinedTreePageViewModel.FromRoot(this, root, _pinnedProperties, _sourceInfoService, _sourceNavigator, _xamlAstWorkspace, _runtimeCoordinator);
            _combinedTree.AttachChangeEmitter(_propertyChangeEmitter);
            AttachScopePersistence(
                _combinedTree,
                () => _combinedTreeScopeKey,
                value => _combinedTreeScopeKey = value,
                ref _combinedTreeScopeHandler);
            AttachScopePersistence(
                _logicalTree,
                () => _logicalTreeScopeKey,
                value => _logicalTreeScopeKey = value,
                ref _logicalTreeScopeHandler);
            AttachScopePersistence(
                _visualTree,
                () => _visualTreeScopeKey,
                value => _visualTreeScopeKey = value,
                ref _visualTreeScopeHandler);
            _events = new EventsPageViewModel(this);
            _hotKeys = new HotKeyPageViewModel();
            _metricsListener = new MetricsListenerService();
            _metrics = new MetricsPageViewModel(_metricsListener);

            UpdateFocusedControl();

            if (KeyboardDevice.Instance is not null)
                KeyboardDevice.Instance.PropertyChanged += KeyboardPropertyChanged;
            SelectedTab = 0;
            if (root is TopLevel topLevel)
            {
                _pointerOverRoot = topLevel;
                _pointerOverSubscription = topLevel.GetObservable(TopLevel.PointerOverElementProperty)
                    .Subscribe(x => PointerOverElement = x);

            }
            else
            {
                _pointerOverSubscription = InputManager.Instance!.PreProcess
                    .Subscribe(e =>
                        {
                            if (e is Input.Raw.RawPointerEventArgs pointerEventArgs)
                            {
                                if (pointerEventArgs.Root is Visual visualRoot)
                                {
                                    var topLevel = TopLevel.GetTopLevel(visualRoot);
                                    if (topLevel is not null && DevTools.IsDevToolsWindow(topLevel))
                                    {
                                        PointerOverRoot = null;
                                        PointerOverElement = null;
                                        return;
                                    }
                                }

                                PointerOverRoot = pointerEventArgs.Root;
                                PointerOverElement = pointerEventArgs.Root.InputHitTest(pointerEventArgs.Position);
                            }
                        });
            }

            UpdateMutationCommandStates();
        }

        public ICommand UndoMutationCommand => _undoMutationCommand;

        public ICommand RedoMutationCommand => _redoMutationCommand;

        public bool CanUndoMutation => _mutationDispatcher.CanUndo;

        public bool CanRedoMutation => _mutationDispatcher.CanRedo;

        public bool FreezePopups
        {
            get => _freezePopups;
            set => RaiseAndSetIfChanged(ref _freezePopups, value);
        }

        public bool ShouldVisualizeMarginPadding
        {
            get => _shouldVisualizeMarginPadding;
            set => RaiseAndSetIfChanged(ref _shouldVisualizeMarginPadding, value);
        }

        public void ToggleVisualizeMarginPadding()
            => ShouldVisualizeMarginPadding = !ShouldVisualizeMarginPadding;

        private IRenderer? TryGetRenderer()
            => _root switch
            {
                TopLevel topLevel => topLevel.Renderer,
                Controls.Application app => app.RendererRoot,
                _ => null
            };

        private bool GetDebugOverlay(RendererDebugOverlays overlay)
            => ((TryGetRenderer()?.Diagnostics.DebugOverlays ?? RendererDebugOverlays.None) & overlay) != 0;

        private void SetDebugOverlay(RendererDebugOverlays overlay, bool enable,
            [CallerMemberName] string? propertyName = null)
        {
            if (TryGetRenderer() is not { } renderer)
            {
                return;
            }

            var oldValue = renderer.Diagnostics.DebugOverlays;
            var newValue = enable ? oldValue | overlay : oldValue & ~overlay;

            if (oldValue == newValue)
            {
                return;
            }

            renderer.Diagnostics.DebugOverlays = newValue;
            RaisePropertyChanged(propertyName);
        }

        public bool ShowDirtyRectsOverlay
        {
            get => GetDebugOverlay(RendererDebugOverlays.DirtyRects);
            set => SetDebugOverlay(RendererDebugOverlays.DirtyRects, value);
        }

        public void ToggleDirtyRectsOverlay()
            => ShowDirtyRectsOverlay = !ShowDirtyRectsOverlay;

        public bool ShowFpsOverlay
        {
            get => GetDebugOverlay(RendererDebugOverlays.Fps);
            set => SetDebugOverlay(RendererDebugOverlays.Fps, value);
        }

        public void ToggleFpsOverlay()
            => ShowFpsOverlay = !ShowFpsOverlay;

        public bool ShowLayoutTimeGraphOverlay
        {
            get => GetDebugOverlay(RendererDebugOverlays.LayoutTimeGraph);
            set => SetDebugOverlay(RendererDebugOverlays.LayoutTimeGraph, value);
        }

        public void ToggleLayoutTimeGraphOverlay()
            => ShowLayoutTimeGraphOverlay = !ShowLayoutTimeGraphOverlay;

        public bool ShowRenderTimeGraphOverlay
        {
            get => GetDebugOverlay(RendererDebugOverlays.RenderTimeGraph);
            set => SetDebugOverlay(RendererDebugOverlays.RenderTimeGraph, value);
        }

        public void ToggleRenderTimeGraphOverlay()
            => ShowRenderTimeGraphOverlay = !ShowRenderTimeGraphOverlay;

        public ViewModelBase? Content
        {
            get { return _content; }
            private set
            {
                if (_content is TreePageViewModel oldTree &&
                    value is TreePageViewModel newTree &&
                    oldTree?.SelectedNode?.Visual is Control control)
                {
                    // HACK: We want to select the currently selected control in the new tree, but
                    // to select nested nodes in TreeView, currently the TreeView has to be able to
                    // expand the parent nodes. Because at this point the TreeView isn't visible,
                    // this will fail unless we schedule the selection to run after layout.
                    DispatcherTimer.RunOnce(
                        () =>
                        {
                            try
                            {
                                newTree.SelectControl(control);
                            }
                            catch { }
                        },
                        TimeSpan.FromMilliseconds(0));
                }

                RaiseAndSetIfChanged(ref _content, value);
            }
        }

        public int SelectedTab
        {
            get { return _selectedTab; }
            // [MemberNotNull(nameof(_content))]
            set
            {
                _selectedTab = value;

                switch (value)
                {
                    case 0:
                        Content = _combinedTree;
                        break;
                    case 1:
                        Content = _logicalTree;
                        break;
                    case 2:
                        Content = _visualTree;
                        break;
                    case 3:
                        Content = _events;
                        break;
                    case 4:
                        Content = _metrics;
                        break;
                    case 5:
                        Content = _hotKeys;
                        break;
                    default:
                        Content = _combinedTree;
                        break;
                }

                RaisePropertyChanged();
            }
        }

        public string? FocusedControl
        {
            get { return _focusedControl; }
            private set { RaiseAndSetIfChanged(ref _focusedControl, value); }
        }

        public IInputRoot? PointerOverRoot
        {
            get => _pointerOverRoot;
            private set => RaiseAndSetIfChanged(ref _pointerOverRoot, value);
        }

        public IInputElement? PointerOverElement
        {
            get { return _pointerOverElement; }
            private set
            {
                RaiseAndSetIfChanged(ref _pointerOverElement, value);
                PointerOverElementName = value?.GetType()?.Name;
            }
        }

        public string? PointerOverElementName
        {
            get => _pointerOverElementName;
            private set => RaiseAndSetIfChanged(ref _pointerOverElementName, value);
        }

        public void ShowHotKeys()
        {
            SelectedTab = 5;
        }

        public void SelectControl(Control control)
        {
            var tree = Content as TreePageViewModel;

            tree?.SelectControl(control);
        }

        public void EnableSnapshotStyles(bool enable)
        {
            if (Content is TreePageViewModel treeVm && treeVm.Details != null)
            {
                treeVm.Details.SnapshotFrames = enable;
            }
        }

        public void Dispose()
        {
            _propertyChangeEmitter.ChangeCompleted -= OnMutationCompleted;
            _propertyChangeEmitter.ExternalDocumentChanged -= OnExternalDocumentChanged;
            if (_roslynWorkspace is not null && _workspaceChangedHandler is not null)
            {
                _roslynWorkspace.WorkspaceChanged -= _workspaceChangedHandler;
            }
            if (KeyboardDevice.Instance is not null)
                KeyboardDevice.Instance.PropertyChanged -= KeyboardPropertyChanged;
            _pointerOverSubscription.Dispose();
            DetachScopePersistence(_combinedTree, ref _combinedTreeScopeHandler);
            DetachScopePersistence(_logicalTree, ref _logicalTreeScopeHandler);
            DetachScopePersistence(_visualTree, ref _visualTreeScopeHandler);
            _logicalTree.Dispose();
            _visualTree.Dispose();
            _combinedTree.Dispose();
            _xamlAstWorkspace.Dispose();
            _metrics.Dispose();
            _metricsListener.Dispose();
            _currentFocusHighlightAdorner?.Dispose();
            lock (_sourcePreviewGate)
            {
                _sourcePreviewObservers.Clear();
            }
            if (TryGetRenderer() is { } renderer)
            {
                renderer.Diagnostics.DebugOverlays = RendererDebugOverlays.None;
            }
        }

        private void AttachScopePersistence(
            TreePageViewModel tree,
            Func<string?> getKey,
            Action<string?> setKey,
            ref PropertyChangedEventHandler? handler)
        {
            var existingKey = getKey();
            if (!string.IsNullOrEmpty(existingKey))
            {
                tree.RestoreScopeFromKey(existingKey);
            }

            setKey(tree.ScopedNodeKey);

            handler = (_, e) =>
            {
                if (e.PropertyName == nameof(TreePageViewModel.ScopedNodeKey))
                {
                    setKey(tree.ScopedNodeKey);
                }
            };

            tree.PropertyChanged += handler;
        }

        private static void DetachScopePersistence(TreePageViewModel tree, ref PropertyChangedEventHandler? handler)
        {
            if (handler is not null)
            {
                tree.PropertyChanged -= handler;
                handler = null;
            }
        }

        private void UpdateFocusedControl()
        {
            var element = KeyboardDevice.Instance?.FocusedElement;
            FocusedControl = element?.GetType().Name;
            _currentFocusHighlightAdorner?.Dispose();
            if (FocusHighlighter is IBrush brush
                && element is InputElement input
                && !input.DoesBelongToDevTool()
                )
            {
                _currentFocusHighlightAdorner = Controls.ControlHighlightAdorner.Add(input, brush);
            }
        }

        private void KeyboardPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(KeyboardDevice.Instance.FocusedElement))
            {
                UpdateFocusedControl();
            }
        }

        public void RequestTreeNavigateTo(Control control, bool isVisualTree)
        {
            var tree = isVisualTree ? _visualTree : _logicalTree;

            var node = tree.FindNode(control);

            if (node != null)
            {
                SelectedTab = isVisualTree ? 2 : 1;

                tree.SelectControl(control);
                return;
            }

            var combinedNode = _combinedTree.FindNode(control);
            if (combinedNode != null)
            {
                SelectedTab = 0;
                _combinedTree.SelectControl(control);
            }
        }

        public int? StartupScreenIndex { get; private set; } = default;

        [DependsOn(nameof(TreePageViewModel.SelectedNode))]
        [DependsOn(nameof(Content))]
        public bool CanShot(object? parameter)
        {
            return Content is TreePageViewModel tree
                && tree.SelectedNode != null
                && tree.SelectedNode.Visual is Visual visual
                && visual.VisualRoot != null;
        }

        public async void Shot(object? parameter)
        {
            if ((Content as TreePageViewModel)?.SelectedNode?.Visual is Control control
                && _screenshotHandler is { }
                )
            {
                try
                {
                    await _screenshotHandler.Take(control);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                    //TODO: Notify error
                }
            }
        }

        private void OnMutationCompleted(object? sender, MutationCompletedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ProcessMutationCompletion(e), DispatcherPriority.Background);
            }
            else
            {
                ProcessMutationCompletion(e);
            }
        }

        private void OnExternalDocumentChanged(object? sender, ExternalDocumentChangedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(
                    () => OnExternalDocumentChanged(sender, e),
                    DispatcherPriority.Background);
                return;
            }

            _runtimeCoordinator.Clear();
            UpdateMutationCommandStates();

            _logicalTree.HandleExternalDocumentChanged(e);
            _visualTree.HandleExternalDocumentChanged(e);
            _combinedTree.HandleExternalDocumentChanged(e);

            if (Content is TreePageViewModel activeTree &&
                activeTree != _logicalTree &&
                activeTree != _visualTree &&
                activeTree != _combinedTree)
            {
                activeTree.HandleExternalDocumentChanged(e);
            }
        }

        private async Task UndoMutationAsync()
        {
            if (!_mutationDispatcher.CanUndo)
            {
                return;
            }

            var result = await _mutationDispatcher.UndoAsync().ConfigureAwait(false);
            if (result.Status == ChangeDispatchStatus.Success)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => _runtimeCoordinator.ApplyUndo(),
                    DispatcherPriority.Background);
            }
        }

        private async Task RedoMutationAsync()
        {
            if (!_mutationDispatcher.CanRedo)
            {
                return;
            }

            var result = await _mutationDispatcher.RedoAsync().ConfigureAwait(false);
            if (result.Status == ChangeDispatchStatus.Success)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => _runtimeCoordinator.ApplyRedo(),
                    DispatcherPriority.Background);
            }
        }

        private void UpdateMutationCommandStates()
        {
            _undoMutationCommand.RaiseCanExecuteChanged();
            _redoMutationCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(CanUndoMutation));
            RaisePropertyChanged(nameof(CanRedoMutation));
        }

        internal void RegisterSourcePreview(SourcePreviewViewModel preview)
        {
            if (preview is null)
            {
                return;
            }

            lock (_sourcePreviewGate)
            {
                CleanupSourcePreviewObservers_NoLock();
                _sourcePreviewObservers.Add(new WeakReference<SourcePreviewViewModel>(preview));
            }
        }

        internal void UnregisterSourcePreview(SourcePreviewViewModel preview)
        {
            if (preview is null)
            {
                return;
            }

            lock (_sourcePreviewGate)
            {
                CleanupSourcePreviewObservers_NoLock(preview);
            }
        }

        private void ProcessMutationCompletion(MutationCompletedEventArgs args)
        {
            UpdateMutationCommandStates();

            switch (args.Result.Status)
            {
                case ChangeDispatchStatus.Success:
                    NotifyMutationSuccess(args);
                    break;
                case ChangeDispatchStatus.GuardFailure:
                case ChangeDispatchStatus.MutationFailure:
                    NotifyMutationFailure(args);
                    break;
            }
        }

        private void NotifyMutationSuccess(MutationCompletedEventArgs args)
        {
            _logicalTree.NotifyMutationCompleted(args);
            _visualTree.NotifyMutationCompleted(args);
            _combinedTree.NotifyMutationCompleted(args);

            if (Content is TreePageViewModel activeTree &&
                activeTree != _logicalTree &&
                activeTree != _visualTree &&
                activeTree != _combinedTree)
            {
                activeTree.NotifyMutationCompleted(args);
            }

            RefreshSourcePreviews(args);
        }

        private void NotifyMutationFailure(MutationCompletedEventArgs args)
        {
            _logicalTree.NotifyMutationCompleted(args);
            _visualTree.NotifyMutationCompleted(args);
            _combinedTree.NotifyMutationCompleted(args);

            if (Content is TreePageViewModel activeTree &&
                activeTree != _logicalTree &&
                activeTree != _visualTree &&
                activeTree != _combinedTree)
            {
                activeTree.NotifyMutationCompleted(args);
            }

            RefreshSourcePreviews(args);
        }

        private void RefreshSourcePreviews(MutationCompletedEventArgs args)
        {
            SourcePreviewViewModel?[] snapshot;

            lock (_sourcePreviewGate)
            {
                CleanupSourcePreviewObservers_NoLock();
                if (_sourcePreviewObservers.Count == 0)
                {
                    return;
                }

                snapshot = new SourcePreviewViewModel?[_sourcePreviewObservers.Count];

                var index = 0;
                foreach (var reference in _sourcePreviewObservers)
                {
                    if (reference.TryGetTarget(out var target) && target is not null)
                    {
                        snapshot[index++] = target;
                    }
                }

                if (index != snapshot.Length)
                {
                    Array.Resize(ref snapshot, index);
                }
            }

            foreach (var preview in snapshot)
            {
                preview?.HandleMutationCompleted(args);
            }
        }

        private void CleanupSourcePreviewObservers_NoLock(SourcePreviewViewModel? instance = null)
        {
            for (var i = _sourcePreviewObservers.Count - 1; i >= 0; i--)
            {
                if (!_sourcePreviewObservers[i].TryGetTarget(out var target) ||
                    (instance is not null && ReferenceEquals(target, instance)))
                {
                    _sourcePreviewObservers.RemoveAt(i);
                }
            }
        }

        private void OnRoslynWorkspaceChanged(object? sender, WorkspaceChangeEventArgs e)
        {
            string? path = null;

            try
            {
                if (e.Kind == WorkspaceChangeKind.SolutionCleared || e.Kind == WorkspaceChangeKind.SolutionRemoved)
                {
                    _xamlAstWorkspace.InvalidateAll();
                    return;
                }

                path = ResolveWorkspaceDocumentPath(e);
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                _xamlAstWorkspace.Invalidate(path!);
            }
            catch
            {
                // Workspace notifications should not break diagnostics tooling.
            }
        }

        private static string? ResolveWorkspaceDocumentPath(WorkspaceChangeEventArgs e)
        {
            if (e.DocumentId is not DocumentId documentId)
            {
                return null;
            }

            return e.Kind switch
            {
                WorkspaceChangeKind.DocumentAdded or
                WorkspaceChangeKind.DocumentChanged or
                WorkspaceChangeKind.DocumentRemoved or
                WorkspaceChangeKind.DocumentReloaded =>
                    e.NewSolution.GetDocument(documentId)?.FilePath ??
                    e.OldSolution.GetDocument(documentId)?.FilePath,

                WorkspaceChangeKind.AdditionalDocumentAdded or
                WorkspaceChangeKind.AdditionalDocumentChanged or
                WorkspaceChangeKind.AdditionalDocumentRemoved or
                WorkspaceChangeKind.AdditionalDocumentReloaded =>
                    e.NewSolution.GetAdditionalDocument(documentId)?.FilePath ??
                    e.OldSolution.GetAdditionalDocument(documentId)?.FilePath,

                WorkspaceChangeKind.AnalyzerConfigDocumentAdded or
                WorkspaceChangeKind.AnalyzerConfigDocumentChanged or
                WorkspaceChangeKind.AnalyzerConfigDocumentRemoved or
                WorkspaceChangeKind.AnalyzerConfigDocumentReloaded =>
                    e.NewSolution.GetAnalyzerConfigDocument(documentId)?.FilePath ??
                    e.OldSolution.GetAnalyzerConfigDocument(documentId)?.FilePath,

                _ => null
            };
        }

        public void SetOptions(DevToolsOptions options)
        {
            _screenshotHandler = options.ScreenshotHandler;
            StartupScreenIndex = options.StartupScreenIndex;
            ShowImplementedInterfaces = options.ShowImplementedInterfaces;
            FocusHighlighter = options.FocusHighlighterBrush;
            SelectedTab = MapLaunchViewToTab(options.LaunchView);

            _hotKeys.SetOptions(options);
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

            _sourceInfoService = sourceInfoService;
            _sourceNavigator = sourceNavigator;

            _logicalTree.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);
            _visualTree.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);
            _combinedTree.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);

            if (Content is TreePageViewModel tree)
            {
                tree.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);
            }
        }

        public bool ShowImplementedInterfaces
        {
            get => _showImplementedInterfaces;
            private set => RaiseAndSetIfChanged(ref _showImplementedInterfaces, value);
        }

        public void ToggleShowImplementedInterfaces(object parameter)
        {
            ShowImplementedInterfaces = !ShowImplementedInterfaces;
            if (Content is TreePageViewModel viewModel)
            {
                viewModel.UpdatePropertiesView();
            }
        }

        public bool ShowDetailsPropertyType
        {
            get => _showPropertyType;
            private set => RaiseAndSetIfChanged(ref _showPropertyType, value);
        }

        public void ToggleShowDetailsPropertyType(object parameter)
        {
            ShowDetailsPropertyType = !ShowDetailsPropertyType;
        }

        public IBrush? FocusHighlighter
        {
            get => _FocusHighlighter;
            private set => RaiseAndSetIfChanged(ref _FocusHighlighter, value);
        }

        public void SelectFocusHighlighter(object parameter)
        {
            FocusHighlighter = parameter as IBrush;
        }

        private static int MapLaunchViewToTab(DevToolsViewKind viewKind) => viewKind switch
        {
            DevToolsViewKind.CombinedTree => 0,
            DevToolsViewKind.LogicalTree => 1,
            DevToolsViewKind.VisualTree => 2,
            DevToolsViewKind.Events => 3,
            DevToolsViewKind.Metrics => 4,
            _ => 0,
        };
    }
}
