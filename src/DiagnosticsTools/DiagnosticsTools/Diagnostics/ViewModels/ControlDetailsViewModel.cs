using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Data;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Views;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using System.Runtime.InteropServices;
using Avalonia.Diagnostics.Xaml;

namespace Avalonia.Diagnostics.ViewModels
{
    public class ControlDetailsViewModel : ViewModelBase, IDisposable, IClassesChangedListener
    {
        private readonly AvaloniaObject _avaloniaObject;
        private readonly ISet<string> _pinnedProperties;
        private IDictionary<object, PropertyViewModel[]>? _propertyIndex;
        private PropertyViewModel? _selectedProperty;
        private DataGridCollectionView? _propertiesView;
        private bool _snapshotFrames;
        private bool _showInactiveFrames;
        private string? _framesStatus;
        private object? _selectedEntity;
        private readonly Stack<(string Name, object Entry)> _selectedEntitiesStack = new();
        private string? _selectedEntityName;
        private string? _selectedEntityType;
        private bool _showImplementedInterfaces;
        // new DataGridPathGroupDescription(nameof(AvaloniaPropertyViewModel.Group))
        private readonly static IReadOnlyList<DataGridPathGroupDescription> GroupDescriptors = new DataGridPathGroupDescription[]
        {
            new DataGridPathGroupDescription(nameof(AvaloniaPropertyViewModel.Group))
        };

        private readonly static IReadOnlyList<DataGridSortDescription> SortDescriptions = new DataGridSortDescription[]
        {
            new DataGridComparerSortDescription(PropertyComparer.Instance!, ListSortDirection.Ascending),
        };
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        private ISourceInfoService _sourceInfoService;
        private ISourceNavigator _sourceNavigator;
        private PropertyInspectorChangeEmitter? _changeEmitter;
        private XamlMutationDispatcher? _mutationDispatcher;
        private readonly DelegateCommand _undoMutationCommand;
        private readonly DelegateCommand _redoMutationCommand;
        private readonly RuntimeMutationCoordinator _runtimeCoordinator;
        private readonly DelegateCommand _reloadExternalDocumentCommand;
        private readonly DelegateCommand _dismissExternalDocumentChangeCommand;
        private string? _mutationStatusMessage;
        private bool _hasExternalDocumentChanges;
        private ExternalDocumentChangedEventArgs? _externalDocumentChange;

        public ControlDetailsViewModel(
            TreePageViewModel treePage,
            AvaloniaObject avaloniaObject,
            ISet<string> pinnedProperties,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator,
            RuntimeMutationCoordinator runtimeCoordinator)
        {
            _avaloniaObject = avaloniaObject;
            _pinnedProperties = pinnedProperties;
            TreePage = treePage;
            Layout = avaloniaObject is Visual visual
                ? new ControlLayoutViewModel(visual)
                : default;
            _sourceInfoService = sourceInfoService ?? throw new ArgumentNullException(nameof(sourceInfoService));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            _changeEmitter = null;
            _mutationDispatcher = null;
            _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
            _undoMutationCommand = new DelegateCommand(UndoMutationAsync, () => CanUndoMutation);
            _redoMutationCommand = new DelegateCommand(RedoMutationAsync, () => CanRedoMutation);
            _reloadExternalDocumentCommand = new DelegateCommand(ReloadExternalDocumentAsync, () => HasExternalDocumentChanges);
            _dismissExternalDocumentChangeCommand = new DelegateCommand(DismissExternalDocumentChange, () => HasExternalDocumentChanges);

            NavigateToProperty(_avaloniaObject, (_avaloniaObject as Control)?.Name ?? _avaloniaObject.ToString());

            AppliedFrames = new ObservableCollection<ValueFrameViewModel>();
            PseudoClasses = new ObservableCollection<PseudoClassViewModel>();

            if (avaloniaObject is StyledElement styledElement)
            {
                styledElement.Classes.AddListener(this);

                var pseudoClassAttributes = styledElement.GetType().GetCustomAttributes<PseudoClassesAttribute>(true);

                foreach (var classAttribute in pseudoClassAttributes)
                {
                    foreach (var className in classAttribute.PseudoClasses)
                    {
                        PseudoClasses.Add(new PseudoClassViewModel(className, styledElement));
                    }
                }

                var styleDiagnostics = styledElement.GetValueStoreDiagnostic();

                var clipboard = TopLevel.GetTopLevel(_avaloniaObject as Visual)?.Clipboard;

                foreach (var appliedStyle in styleDiagnostics.AppliedFrames.OrderBy(s => s.Priority))
                {
                    var frame = new ValueFrameViewModel(styledElement, appliedStyle, clipboard, _sourceInfoService, _sourceNavigator, TreePage.MainView);
                    frame.SourcePreviewRequested += OnValueFramePreviewRequested;
                    AppliedFrames.Add(frame);
                }

                UpdateStyles();
            }
        }

    public bool CanNavigateToParentProperty => _selectedEntitiesStack.Count >= 1;

    public event EventHandler<SourcePreviewViewModel>? SourcePreviewRequested;

        public TreePageViewModel TreePage { get; }

        public DataGridCollectionView? PropertiesView
        {
            get => _propertiesView;
            private set => RaiseAndSetIfChanged(ref _propertiesView, value);
        }

        public ObservableCollection<ValueFrameViewModel> AppliedFrames { get; }

        public ObservableCollection<PseudoClassViewModel> PseudoClasses { get; }

        public object? SelectedEntity
        {
            get => _selectedEntity;
            set => RaiseAndSetIfChanged(ref _selectedEntity, value);
        }

        public string? SelectedEntityName
        {
            get => _selectedEntityName;
            set => RaiseAndSetIfChanged(ref _selectedEntityName, value);
        }

        public string? SelectedEntityType
        {
            get => _selectedEntityType;
            set => RaiseAndSetIfChanged(ref _selectedEntityType, value);
        }

        public PropertyViewModel? SelectedProperty
        {
            get => _selectedProperty;
            set => RaiseAndSetIfChanged(ref _selectedProperty, value);
        }

        public bool SnapshotFrames
        {
            get => _snapshotFrames;
            set => RaiseAndSetIfChanged(ref _snapshotFrames, value);
        }

        public bool ShowInactiveFrames
        {
            get => _showInactiveFrames;
            set => RaiseAndSetIfChanged(ref _showInactiveFrames, value);
        }

        public string? FramesStatus
        {
            get => _framesStatus;
            set => RaiseAndSetIfChanged(ref _framesStatus, value);
        }

        public ICommand UndoMutationCommand => _undoMutationCommand;

        public ICommand RedoMutationCommand => _redoMutationCommand;

        public bool CanUndoMutation => _mutationDispatcher?.CanUndo ?? false;

        public bool CanRedoMutation => _mutationDispatcher?.CanRedo ?? false;

        public bool ShowMutationCommands => _mutationDispatcher is not null;

        public string? MutationStatusMessage
        {
            get => _mutationStatusMessage;
            private set
            {
                if (RaiseAndSetIfChanged(ref _mutationStatusMessage, value))
                {
                    RaisePropertyChanged(nameof(HasMutationStatusMessage));
                }
            }
        }

        public bool HasMutationStatusMessage => !string.IsNullOrWhiteSpace(MutationStatusMessage);

        public bool HasExternalDocumentChanges
        {
            get => _hasExternalDocumentChanges;
            private set
            {
                if (RaiseAndSetIfChanged(ref _hasExternalDocumentChanges, value))
                {
                    _reloadExternalDocumentCommand.RaiseCanExecuteChanged();
                    _dismissExternalDocumentChangeCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand ReloadExternalDocumentCommand => _reloadExternalDocumentCommand;

        public ICommand DismissExternalDocumentChangeCommand => _dismissExternalDocumentChangeCommand;

        public ControlLayoutViewModel? Layout { get; }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (string.Equals(e.PropertyName, nameof(SnapshotFrames), StringComparison.Ordinal) &&
                !SnapshotFrames)
            {
                UpdateStyles();
            }
        }

        public void UpdateStyleFilters()
        {
            foreach (var style in AppliedFrames)
            {
                var hasVisibleSetter = false;

                foreach (var setter in style.Setters)
                {
                    setter.IsVisible = TreePage.SettersFilter.Filter(setter.Name);

                    hasVisibleSetter |= setter.IsVisible;
                }

                style.IsVisible = hasVisibleSetter;
            }
        }

        internal void AttachChangeEmitter(PropertyInspectorChangeEmitter? changeEmitter)
        {
            if (ReferenceEquals(_changeEmitter, changeEmitter))
            {
                return;
            }

            if (_changeEmitter is not null)
            {
                _changeEmitter.ChangeCompleted -= OnChangeEmitterCompleted;
                _changeEmitter.ExternalDocumentChanged -= OnExternalDocumentChanged;
            }

            _changeEmitter = changeEmitter;
            _mutationDispatcher = _changeEmitter?.MutationDispatcher;

            if (_changeEmitter is not null)
            {
                _changeEmitter.ChangeCompleted += OnChangeEmitterCompleted;
                _changeEmitter.ExternalDocumentChanged += OnExternalDocumentChanged;
            }
            else
            {
                MutationStatusMessage = null;
                ClearExternalDocumentChange();
            }

            UpdateMutationCommandStates();
            RaisePropertyChanged(nameof(ShowMutationCommands));
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

            foreach (var frame in AppliedFrames)
            {
                frame.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);
            }
        }

        public void Dispose()
        {
            if (_changeEmitter is not null)
            {
                _changeEmitter.ChangeCompleted -= OnChangeEmitterCompleted;
                _changeEmitter.ExternalDocumentChanged -= OnExternalDocumentChanged;
            }

            if (_avaloniaObject is INotifyPropertyChanged inpc)
            {
                inpc.PropertyChanged -= ControlPropertyChanged;
            }

            if (_avaloniaObject is AvaloniaObject ao)
            {
                ao.PropertyChanged -= ControlPropertyChanged;
            }

            if (_avaloniaObject is StyledElement se)
            {
                se.Classes.RemoveListener(this);
            }

            foreach (var frame in AppliedFrames)
            {
                frame.SourcePreviewRequested -= OnValueFramePreviewRequested;
                frame.DetachMutationObserver();
            }
        }

        private void OnChangeEmitterCompleted(object? sender, MutationCompletedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ApplyMutationCompletion(e), DispatcherPriority.Background);
            }
            else
            {
                ApplyMutationCompletion(e);
            }
        }

        private void OnExternalDocumentChanged(object? sender, ExternalDocumentChangedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => HandleExternalDocumentChanged(e), DispatcherPriority.Background);
            }
            else
            {
                HandleExternalDocumentChanged(e);
            }
        }

        private void ApplyMutationCompletion(MutationCompletedEventArgs args)
        {
            if (args.Result.Status == ChangeDispatchStatus.Success)
            {
                MutationStatusMessage = null;
            }
            else
            {
                HandleMutationFailure(args);
            }

            UpdateMutationCommandStates();
        }

        internal void HandleMutationSuccess(MutationCompletedEventArgs args)
        {
            MutationStatusMessage = null;
            ClearExternalDocumentChange();

            var selectedProperty = SelectedProperty is AvaloniaPropertyViewModel propertyVm ? propertyVm.Property : null;

            NavigateToProperty(_avaloniaObject, (_avaloniaObject as Control)?.Name ?? _avaloniaObject.ToString());

            if (selectedProperty is not null)
            {
                SelectProperty(selectedProperty);
            }

            RefreshValueFrames();
            UpdateStyleFilters();

            if (args.Provenance == MutationProvenance.PropertyInspector)
            {
                RefreshInspectorPreviousValues();
            }
        }

        internal void HandleMutationFailure(MutationCompletedEventArgs args)
        {
            MutationStatusMessage = BuildMutationFailureMessage(args);
        }

        internal void HandleExternalDocumentChanged(ExternalDocumentChangedEventArgs args)
        {
            if (args is null)
            {
                return;
            }

            if (!IsMatchingDocument(args.Path))
            {
                return;
            }

            _externalDocumentChange = args;
            HasExternalDocumentChanges = true;
            MutationStatusMessage = BuildExternalDocumentChangeMessage(args);
            RefreshInspectorPreviousValues();
            UpdateMutationCommandStates();
        }

        private static string BuildMutationFailureMessage(MutationCompletedEventArgs args)
        {
            var baseMessage = string.IsNullOrWhiteSpace(args.Result.Message)
                ? args.Result.Status switch
                {
                    ChangeDispatchStatus.GuardFailure => "The underlying XAML changed. Refresh the inspector to pick up the latest document and retry.",
                    ChangeDispatchStatus.MutationFailure => "Failed to persist the XAML change. See output for more details.",
                    _ => "XAML mutation failed."
                }
                : args.Result.Message!;

            if (!string.IsNullOrWhiteSpace(args.Result.OperationId))
            {
                baseMessage = $"{baseMessage} (operation {args.Result.OperationId})";
            }

            return baseMessage;
        }

        private string BuildExternalDocumentChangeMessage(ExternalDocumentChangedEventArgs args)
        {
            var baseMessage = "The XAML document changed outside the inspector.";

            if (!string.IsNullOrWhiteSpace(args.Path))
            {
                baseMessage = $"{baseMessage} ({Path.GetFileName(args.Path)}).";
            }
            else
            {
                baseMessage = $"{baseMessage}.";
            }

            return $"{baseMessage} Reload to sync the inspector or dismiss to continue with existing values.";
        }

        private Task ReloadExternalDocumentAsync()
        {
            if (!HasExternalDocumentChanges)
            {
                return Task.CompletedTask;
            }

            var selectedKey = SelectedProperty?.Key;

            NavigateToProperty(_avaloniaObject, (_avaloniaObject as Control)?.Name ?? _avaloniaObject.ToString());

            if (selectedKey is not null &&
                _propertyIndex is not null &&
                _propertyIndex.TryGetValue(selectedKey, out var properties) &&
                properties.Length > 0)
            {
                SelectedProperty = properties[0];
            }

            RefreshValueFrames();
            UpdateStyleFilters();
            RefreshInspectorPreviousValues();

            MutationStatusMessage = null;
            ClearExternalDocumentChange();
            UpdateMutationCommandStates();

            return Task.CompletedTask;
        }

        private void DismissExternalDocumentChange()
        {
            if (_externalDocumentChange is not null)
            {
                MutationStatusMessage = null;
            }

            ClearExternalDocumentChange();
            UpdateMutationCommandStates();
        }

        private void ClearExternalDocumentChange()
        {
            _externalDocumentChange = null;
            HasExternalDocumentChanges = false;
        }

        private void RefreshInspectorPreviousValues()
        {
            if (_propertyIndex is null)
            {
                return;
            }

            foreach (var bucket in _propertyIndex.Values)
            {
                foreach (var property in bucket)
                {
                    if (property is AvaloniaPropertyViewModel avaloniaProperty)
                    {
                        avaloniaProperty.RefreshPreviousValueBaseline();
                    }
                }
            }
        }

        private bool IsMatchingDocument(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            var selectedPath = ResolveSelectedDocumentPath();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return false;
            }

            var normalizedSelected = NormalizePath(selectedPath!);
            var normalizedPath = NormalizePath(path!);

            return PathComparer.Equals(normalizedSelected, normalizedPath);
        }

        private string? ResolveSelectedDocumentPath()
        {
            if (TreePage.SelectedNodeSourceInfo?.LocalPath is { } local && !string.IsNullOrWhiteSpace(local))
            {
                return local;
            }

            return TreePage.SelectedNodeXaml?.Document?.Path;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
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

        private async Task UndoMutationAsync()
        {
            var dispatcher = _mutationDispatcher;
            if (dispatcher is null || !dispatcher.CanUndo)
            {
                return;
            }

            var result = await dispatcher.UndoAsync().ConfigureAwait(false);
            if (result.Status == ChangeDispatchStatus.Success)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => _runtimeCoordinator.ApplyUndo(),
                    DispatcherPriority.Background);
            }
        }

        private async Task RedoMutationAsync()
        {
            var dispatcher = _mutationDispatcher;
            if (dispatcher is null || !dispatcher.CanRedo)
            {
                return;
            }

            var result = await dispatcher.RedoAsync().ConfigureAwait(false);
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
            RaisePropertyChanged(nameof(ShowMutationCommands));
        }

        private IEnumerable<PropertyViewModel> GetAvaloniaProperties(object o)
        {
            if (o is not AvaloniaObject ao)
            {
                return Enumerable.Empty<PropertyViewModel>();
            }

            var registry = AvaloniaPropertyRegistry.Instance;
            var seen = new HashSet<AvaloniaProperty>();
            var list = new List<PropertyViewModel>();

            foreach (var property in registry.GetRegistered(ao))
            {
                if (seen.Add(property))
                {
                    list.Add(CreateAvaloniaPropertyViewModel(ao, property));
                }
            }

            foreach (var attached in registry.GetRegisteredAttached(ao.GetType()))
            {
                if (seen.Add(attached))
                {
                    list.Add(CreateAvaloniaPropertyViewModel(ao, attached));
                }
            }

            return list;
        }

        private PropertyViewModel CreateAvaloniaPropertyViewModel(AvaloniaObject target, AvaloniaProperty property)
        {
            return new AvaloniaPropertyViewModel(
                target,
                property,
                OnAvaloniaPropertyEditedAsync);
        }

        private static IEnumerable<PropertyViewModel> GetClrProperties(object o, bool showImplementedInterfaces)
        {
            foreach (var p in GetClrProperties(o, o.GetType()))
            {
                yield return p;
            }

            if (showImplementedInterfaces)
            {
                foreach (var i in o.GetType().GetInterfaces())
                {
                    foreach (var p in GetClrProperties(o, i))
                    {
                        yield return p;
                    }
                }
            }
        }

        private static IEnumerable<PropertyViewModel> GetClrProperties(object o, Type t)
        {
            return t.GetProperties()
                .Where(x => x.GetIndexParameters().Length == 0)
                .Select(x => new ClrPropertyViewModel(o, x));
        }

        private async ValueTask OnAvaloniaPropertyEditedAsync(AvaloniaPropertyViewModel viewModel, object? newValue)
        {
            var changeEmitter = _changeEmitter;
            if (changeEmitter is null)
            {
                return;
            }

            var context = CreatePropertyChangeContext(viewModel.Property);
            if (context is null)
            {
                return;
            }

            var gesture = DetermineGesture(viewModel.Property, newValue);
            var command = DetermineEditorCommand(viewModel, newValue);
            var previous = viewModel.PreviousValue;

            MutationStatusMessage = null;

            var preview = await changeEmitter.PreviewLocalValueChangeAsync(context, newValue, previous, gesture, command).ConfigureAwait(false);
            var decision = await ShowMutationPreviewAsync(context, preview).ConfigureAwait(false);
            if (decision != MutationPreviewDecision.Apply)
            {
                viewModel.ActiveEditorCommand = EditorCommandDescriptor.Default;
                await RevertPropertyAsync(context, viewModel, previous).ConfigureAwait(false);
                if (decision == MutationPreviewDecision.EditRaw || preview.Status != ChangeDispatchStatus.Success)
                {
                    MutationStatusMessage = preview.Message ?? "Preview unavailable. Use Source Preview to edit the document manually.";
                }

                return;
            }

            ChangeDispatchResult result;
            try
            {
                result = await changeEmitter.EmitLocalValueChangeAsync(context, newValue, previous, gesture, command).ConfigureAwait(false);
            }
            finally
            {
                viewModel.ActiveEditorCommand = EditorCommandDescriptor.Default;
            }

            var mutationTarget = context.Target;
            var mutationProperty = context.Property;

            if (result.Status == ChangeDispatchStatus.Success)
            {
                if (_runtimeCoordinator is not null && mutationTarget is AvaloniaObject target && mutationProperty is AvaloniaProperty property && !Equals(previous, newValue))
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        _runtimeCoordinator.RegisterPropertyChange(target, property, previous, newValue),
                        DispatcherPriority.Background);
                }

                return;
            }

            await RevertPropertyAsync(context, viewModel, previous).ConfigureAwait(false);
        }

        private PropertyChangeContext? CreatePropertyChangeContext(AvaloniaProperty property)
        {
            var selection = TreePage.SelectedNodeXaml;
            if (selection is null || selection.Node is null)
            {
                return null;
            }

            return new PropertyChangeContext(
                _avaloniaObject,
                property,
                selection.Document,
                selection.Node,
                frame: "LocalValue",
                valueSource: "LocalValue");
        }

        private static string DetermineGesture(AvaloniaProperty property, object? newValue)
        {
            if (property.PropertyType == typeof(bool) ||
                property.PropertyType == typeof(bool?) ||
                newValue is bool)
            {
                return "ToggleCheckBox";
            }

            return "SetLocalValue";
        }

        private static EditorCommandDescriptor DetermineEditorCommand(PropertyViewModel viewModel, object? newValue)
        {
            var command = EditorCommandDescriptor.Normalize(viewModel.ActiveEditorCommand);
            if (!string.Equals(command.Id, EditorCommandDescriptor.Default.Id, StringComparison.Ordinal))
            {
                return command;
            }

            var propertyType = viewModel.PropertyType;
            if (propertyType == typeof(bool) || propertyType == typeof(bool?) || newValue is bool)
            {
                return EditorCommandDescriptor.Toggle;
            }

            if (IsNumericType(propertyType))
            {
                return EditorCommandDescriptor.Slider;
            }

            if (propertyType == typeof(Avalonia.Media.Color) || propertyType == typeof(Avalonia.Media.Color?))
            {
                return EditorCommandDescriptor.ColorPicker;
            }

            if (typeof(IBinding).IsAssignableFrom(propertyType))
            {
                return EditorCommandDescriptor.BindingEditor;
            }

            return EditorCommandDescriptor.Default;
        }

        private static bool IsNumericType(Type type)
        {
            if (type is null)
            {
                return false;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                type = Nullable.GetUnderlyingType(type)!;
            }

            if (type.IsEnum)
            {
                return false;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyPropertyValue(AvaloniaObject target, AvaloniaProperty property, object? value)
        {
            if (ReferenceEquals(value, AvaloniaProperty.UnsetValue))
            {
                target.ClearValue(property);
            }
            else
            {
                target.SetValue(property, value);
            }
        }

        private async Task<MutationPreviewDecision> ShowMutationPreviewAsync(PropertyChangeContext context, MutationPreviewResult preview)
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime ||
                lifetime.MainWindow is not Window owner)
            {
                return preview.Status == ChangeDispatchStatus.Success ? MutationPreviewDecision.Apply : MutationPreviewDecision.Cancel;
            }

            var window = new MutationPreviewWindow();

            if (Dispatcher.UIThread.CheckAccess())
            {
                var decision = await window.ShowDialogAsync(owner, preview, allowRawEditing: true).ConfigureAwait(false);
                if (decision == MutationPreviewDecision.EditRaw)
                {
                    await OpenSourcePreviewAsync(context).ConfigureAwait(false);
                }

                return decision;
            }

            var tcs = new TaskCompletionSource<MutationPreviewDecision>();
            Dispatcher.UIThread.Post(async () =>
            {
                var result = await window.ShowDialogAsync(owner, preview, allowRawEditing: true).ConfigureAwait(false);
                if (result == MutationPreviewDecision.EditRaw)
                {
                    await OpenSourcePreviewAsync(context).ConfigureAwait(false);
                }
                tcs.TrySetResult(result);
            });
            return await tcs.Task.ConfigureAwait(false);
        }

        private async Task RevertPropertyAsync(PropertyChangeContext context, AvaloniaPropertyViewModel viewModel, object? previous)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyPropertyValue(context.Target, context.Property, previous);
                viewModel.Update();
            }, DispatcherPriority.Background);
        }

        private async Task OpenSourcePreviewAsync(PropertyChangeContext context)
        {
            var document = context.Document;
            var descriptor = context.Descriptor;

            if (string.IsNullOrWhiteSpace(document.Path))
            {
                return;
            }

            var sourceInfo = new SourceInfo(
                document.Path,
                null,
                descriptor.LineSpan.Start.Line + 1,
                descriptor.LineSpan.Start.Column + 1,
                descriptor.LineSpan.End.Line + 1,
                descriptor.LineSpan.End.Column + 1,
                SourceOrigin.Local);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var selection = new XamlAstSelection(document, descriptor, new[] { descriptor });
                var preview = new SourcePreviewViewModel(
                    sourceInfo,
                    _sourceNavigator,
                    selection,
                    mutationOwner: TreePage.MainView,
                    xamlAstWorkspace: _mutationDispatcher?.Workspace);

                TreePage.RegisterPreview(preview);
                SourcePreviewRequested?.Invoke(this, preview);
            }, DispatcherPriority.Background);
        }

        private void OnValueFramePreviewRequested(object? sender, SourcePreviewViewModel e)
        {
            SourcePreviewRequested?.Invoke(this, e);
        }

        private void ControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (_propertyIndex is { } && _propertyIndex.TryGetValue(e.Property, out var properties))
            {
                foreach (var property in properties)
                {
                    property.Update();
                }
            }

            Layout?.ControlPropertyChanged(sender, e);
        }

        private void ControlPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != null
                && _propertyIndex is { }
                && _propertyIndex.TryGetValue(e.PropertyName, out var properties))
            {
                foreach (var property in properties)
                {
                    property.Update();
                }
            }

            if (!SnapshotFrames)
            {
                Dispatcher.UIThread.Post(UpdateStyles);
            }
        }

        void IClassesChangedListener.Changed()
        {
            if (!SnapshotFrames)
            {
                Dispatcher.UIThread.Post(UpdateStyles);
            }
        }

        private void UpdateStyles()
        {
            int activeCount = 0;

            foreach (var style in AppliedFrames)
            {
                style.Update();

                if (style.IsActive)
                {
                    activeCount++;
                }
            }

            var propertyBuckets = new Dictionary<AvaloniaProperty, List<SetterViewModel>>();

            foreach (var style in AppliedFrames.Reverse())
            {
                if (!style.IsActive)
                {
                    continue;
                }

                foreach (var setter in style.Setters)
                {
                    if (propertyBuckets.TryGetValue(setter.Property, out var setters))
                    {
                        foreach (var otherSetter in setters)
                        {
                            otherSetter.IsActive = false;
                        }

                        setter.IsActive = true;

                        setters.Add(setter);
                    }
                    else
                    {
                        setter.IsActive = true;

                        setters = new List<SetterViewModel> { setter };

                        propertyBuckets.Add(setter.Property, setters);
                    }
                }
            }

            foreach (var pseudoClass in PseudoClasses)
            {
                pseudoClass.Update();
            }

            FramesStatus = $"Value Frames ({activeCount}/{AppliedFrames.Count} active)";
        }

        private void RefreshValueFrames()
        {
            if (_avaloniaObject is not StyledElement styledElement)
            {
                return;
            }

            foreach (var frame in AppliedFrames)
            {
                frame.SourcePreviewRequested -= OnValueFramePreviewRequested;
                frame.DetachMutationObserver();
            }

            AppliedFrames.Clear();

            var clipboard = TopLevel.GetTopLevel(_avaloniaObject as Visual)?.Clipboard;
            var diagnostics = styledElement.GetValueStoreDiagnostic();

            foreach (var appliedStyle in diagnostics.AppliedFrames.OrderBy(s => s.Priority))
            {
                var frame = new ValueFrameViewModel(styledElement, appliedStyle, clipboard, _sourceInfoService, _sourceNavigator, TreePage.MainView);
                frame.SourcePreviewRequested += OnValueFramePreviewRequested;
                AppliedFrames.Add(frame);
            }

            UpdateStyles();
        }

        private bool FilterProperty(object arg)
        {
            return !(arg is PropertyViewModel property) || TreePage.PropertiesFilter.Filter(property.Name);
        }

        private class PropertyComparer : IComparer<PropertyViewModel>, IComparer
        {
            public static PropertyComparer Instance { get; } = new PropertyComparer();

            public int Compare(PropertyViewModel? x, PropertyViewModel? y)
            {
                if (x is null && y is null)
                    return 0;

                if (x is null && y is not null)
                    return -1;

                if (x is not null && y is null)
                    return 1;

                var groupX = GroupIndex(x!.Group);
                var groupY = GroupIndex(y!.Group);

                if (groupX != groupY)
                {
                    return groupX - groupY;
                }
                else
                {
                    return string.CompareOrdinal(x.Name, y.Name);
                }
            }

            private static int GroupIndex(string? group)
            {
                switch (group)
                {
                    case "Pinned":
                        return -1;
                    case "Properties":
                        return 0;
                    case "Attached Properties":
                        return 1;
                    case "CLR Properties":
                        return 2;
                    default:
                        return 3;
                }
            }

            public int Compare(object? x, object? y) =>
                Compare(x as PropertyViewModel, y as PropertyViewModel);
        }

        private static IEnumerable<PropertyInfo> GetAllPublicProperties(Type type)
        {
            return type
                .GetProperties()
                .Concat(type.GetInterfaces().SelectMany(i => i.GetProperties()));
        }

        public void NavigateToSelectedProperty()
        {
            var selectedProperty = SelectedProperty;
            var selectedEntity = SelectedEntity;
            var selectedEntityName = SelectedEntityName;
            if (selectedEntity == null
                || selectedProperty == null
                || selectedProperty.PropertyType == typeof(string)
                || selectedProperty.PropertyType.IsValueType)
                return;

            object? property = null;

            switch (selectedProperty)
            {
                case AvaloniaPropertyViewModel avaloniaProperty:

                    property = (_selectedEntity as Control)?.GetValue(avaloniaProperty.Property);

                    break;

                case ClrPropertyViewModel clrProperty:
                    {
                        property = GetAllPublicProperties(selectedEntity.GetType())
                            .FirstOrDefault(pi => clrProperty.Property == pi)?
                            .GetValue(selectedEntity);

                        break;
                    }
            }

            if (property == null)
                return;

            _selectedEntitiesStack.Push((Name: selectedEntityName!, Entry: selectedEntity));

            var propertyName = selectedProperty.Name;

            //Strip out interface names
            if (propertyName.LastIndexOf('.') is var p && p != -1)
            {
                propertyName = propertyName.Substring(p + 1);
            }

            NavigateToProperty(property, selectedEntityName + "." + propertyName);

            RaisePropertyChanged(nameof(CanNavigateToParentProperty));
        }

        public void NavigateToParentProperty()
        {
            if (_selectedEntitiesStack.Count > 0)
            {
                var property = _selectedEntitiesStack.Pop();
                NavigateToProperty(property.Entry, property.Name);

                RaisePropertyChanged(nameof(CanNavigateToParentProperty));
            }
        }

        protected void NavigateToProperty(object o, string? entityName)
        {
            var oldSelectedEntity = SelectedEntity;

            switch (oldSelectedEntity)
            {
                case AvaloniaObject ao1:
                    ao1.PropertyChanged -= ControlPropertyChanged;
                    break;

                case INotifyPropertyChanged inpc1:
                    inpc1.PropertyChanged -= ControlPropertyChanged;
                    break;
            }

            SelectedEntity = o;
            SelectedEntityName = entityName;
            SelectedEntityType = o.ToString();

            var properties = GetAvaloniaProperties(o)
                .Concat(GetClrProperties(o, _showImplementedInterfaces))
                .Do(p =>
                    {
                        p.IsPinned = _pinnedProperties.Contains(p.FullName);
                    })
                .ToArray();

            _propertyIndex = properties
                .GroupBy(x => x.Key)
                .ToDictionary(x => x.Key, x => x.ToArray());

            var view = new DataGridCollectionView(properties);
            view.GroupDescriptions.AddRange(GroupDescriptors);
            view.SortDescriptions.AddRange(SortDescriptions);
            view.Filter = FilterProperty;
            PropertiesView = view;

            switch (o)
            {
                case AvaloniaObject ao2:
                    ao2.PropertyChanged += ControlPropertyChanged;
                    break;

                case INotifyPropertyChanged inpc2:
                    inpc2.PropertyChanged += ControlPropertyChanged;
                    break;
            }
        }

        internal void SelectProperty(AvaloniaProperty property)
        {
            SelectedProperty = null;

            if (SelectedEntity != _avaloniaObject)
            {
                NavigateToProperty(
                    _avaloniaObject,
                    (_avaloniaObject as Control)?.Name ?? _avaloniaObject.ToString());
            }

            if (PropertiesView is null)
            {
                return;
            }

            foreach (object o in PropertiesView)
            {
                if (o is AvaloniaPropertyViewModel propertyVm && propertyVm.Property == property)
                {
                    SelectedProperty = propertyVm;

                    break;
                }
            }
        }

        internal void UpdatePropertiesView(bool showImplementedInterfaces)
        {
            _showImplementedInterfaces = showImplementedInterfaces;
            SelectedProperty = null;
            NavigateToProperty(_avaloniaObject, (_avaloniaObject as Control)?.Name ?? _avaloniaObject.ToString());
        }

        public void TogglePinnedProperty(object parameter)
        {
            if (parameter is PropertyViewModel model)
            {
                var fullname = model.FullName;
                if (_pinnedProperties.Contains(fullname))
                {
                    _pinnedProperties.Remove(fullname);
                    model.IsPinned = false;
                }
                else
                {
                    _pinnedProperties.Add(fullname);
                    model.IsPinned = true;
                }
                PropertiesView?.Refresh();
            }
        }
    }
}
