using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.Services;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.ViewModels
{
    public class ValueFrameViewModel : ViewModelBase
    {
        private readonly IValueFrameDiagnostic _valueFrame;
        private readonly StyledElement _styledElement;
        private bool _isActive;
        private bool _isVisible;
        private ISourceInfoService _sourceInfoService;
        private ISourceNavigator _sourceNavigator;
        private SourceInfo? _sourceInfo;
        private Task<SourceInfo?>? _sourceInfoTask;
        private bool _sourceInfoLoadAttempted;
        private readonly DelegateCommand _previewSourceCommand;
        private readonly DelegateCommand _navigateToSourceCommand;
        private SourcePreviewViewModel? _inlinePreview;
        private readonly MainViewModel? _mainViewModel;
        private readonly ITemplateSourceResolver? _templateSourceResolver;
        private readonly ITemplateOverrideService? _templateOverrideService;
        private readonly HashSet<string> _templatePropertyNames;
        private readonly Dictionary<string, List<AvaloniaProperty>> _templatePropertyLookup;
        private readonly Dictionary<AvaloniaProperty, TemplatePreviewRequest> _templatePreviewMap;
        private readonly HashSet<AvaloniaProperty> _templateProperties;
        private Task<TemplatePreviewRequest?>? _templatePreviewTask;
        private TemplatePreviewRequest? _displayTemplatePreview;
        private bool _hasTemplatePreview;
        private readonly DelegateCommand _forkTemplateCommand;
        private string? _templateScopeDescription;
        private string? _templateOverrideStatusMessage;

        public ValueFrameViewModel(
            StyledElement styledElement,
            IValueFrameDiagnostic valueFrame,
            IClipboard? clipboard,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator,
            MainViewModel? mainViewModel = null,
            ITemplateSourceResolver? templateSourceResolver = null,
            ITemplateOverrideService? templateOverrideService = null)
        {
            _valueFrame = valueFrame;
            _styledElement = styledElement;
            IsVisible = true;
            _sourceInfoService = sourceInfoService ?? throw new ArgumentNullException(nameof(sourceInfoService));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            _mainViewModel = mainViewModel;
            _templateSourceResolver = templateSourceResolver;
            _templateOverrideService = templateOverrideService;
            _templatePropertyNames = new HashSet<string>(StringComparer.Ordinal);
            _templatePropertyLookup = new Dictionary<string, List<AvaloniaProperty>>(StringComparer.Ordinal);
            _templatePreviewMap = new Dictionary<AvaloniaProperty, TemplatePreviewRequest>();
            _templateProperties = new HashSet<AvaloniaProperty>();
            _templatePreviewTask = null;
            _displayTemplatePreview = null;
            _hasTemplatePreview = false;
            _forkTemplateCommand = new DelegateCommand(async () => await ForkTemplateAsync().ConfigureAwait(false), () => CanForkTemplate);
            _templateScopeDescription = null;
            _templateOverrideStatusMessage = null;

            var source = SourceToString(_valueFrame.Source);
            Description = (_valueFrame.Type, source) switch
            {
                (IValueFrameDiagnostic.FrameType.Local, _) => "Local Values " + source,
                (IValueFrameDiagnostic.FrameType.Template, _) => "Template " + source,
                (IValueFrameDiagnostic.FrameType.Theme, _) => "Theme " + source,
                (_, {Length:>0}) => source,
                _ => _valueFrame.Priority.ToString()
            };

            Setters = new List<SetterViewModel>();

            foreach (var (setterProperty, setterValue) in valueFrame.Values)
            {
                var resourceInfo = GetResourceInfo(setterValue);

                SetterViewModel setterVm;

                if (resourceInfo.HasValue)
                {
                    var resourceKey = resourceInfo.Value.resourceKey;
                    var resourceValue = styledElement.FindResource(resourceKey);

                    setterVm = new ResourceSetterViewModel(setterProperty, resourceKey, resourceValue,
                        resourceInfo.Value.isDynamic, clipboard);
                }
                else
                {
                    var isBinding = IsBinding(setterValue);

                    if (isBinding)
                    {
                        setterVm = new BindingSetterViewModel(setterProperty, setterValue, clipboard);
                    }
                    else
                    {
                        setterVm = new SetterViewModel(setterProperty, setterValue, clipboard);
                    }
                }

                RegisterTemplateProperty(setterProperty);
                Setters.Add(setterVm);
            }

            Update();

            _previewSourceCommand = new DelegateCommand(PreviewSourceAsync, () => CanPreviewSource);
            _navigateToSourceCommand = new DelegateCommand(NavigateToSourceAsync, () => CanNavigateToSource);
        }

        public bool IsActive
        {
            get => _isActive;
            set => RaiseAndSetIfChanged(ref _isActive, value);
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => RaiseAndSetIfChanged(ref _isVisible, value);
        }

        public string? Description { get; }

        public List<SetterViewModel> Setters { get; }

        public void Update()
        {
            IsActive = _valueFrame.IsActive;
        }

        public SourceInfo? SourceInfo
        {
            get => _sourceInfo;
            private set
            {
                if (Equals(_sourceInfo, value))
                {
                    return;
                }

                _sourceInfo = value;
                _templatePreviewTask = null;
                _templatePreviewMap.Clear();
                _displayTemplatePreview = null;
                UpdateInlinePreview(value);
                TemplateScopeDescription = null;
                TemplateOverrideStatusMessage = null;
                RaisePropertyChanged(nameof(SourceInfo));
                RaisePropertyChanged(nameof(SourceSummary));
                RaisePropertyChanged(nameof(HasSource));
                NotifySourceCommandAvailabilityChanged();
                _forkTemplateCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(CanForkTemplate));
            }
        }

        public string? SourceSummary => SourceInfo?.DisplayPath;

        public bool HasSource => SourceInfo is not null;

        public bool ShowSourceCommands => _hasTemplatePreview || SourceInfo is not null || !_sourceInfoLoadAttempted;

        public bool CanNavigateToSource => _hasTemplatePreview || SourceInfo is not null;

        public bool CanPreviewSource => ShowSourceCommands;

        public ICommand PreviewSourceCommand => _previewSourceCommand;

        public ICommand NavigateToSourceCommand => _navigateToSourceCommand;

        public string? TemplateScopeDescription
        {
            get => _templateScopeDescription;
            private set
            {
                if (RaiseAndSetIfChanged(ref _templateScopeDescription, value))
                {
                    RaisePropertyChanged(nameof(HasTemplateScope));
                }
            }
        }

        public bool HasTemplateScope => !string.IsNullOrWhiteSpace(TemplateScopeDescription);

        public string? TemplateOverrideStatusMessage
        {
            get => _templateOverrideStatusMessage;
            private set
            {
                if (RaiseAndSetIfChanged(ref _templateOverrideStatusMessage, value))
                {
                    RaisePropertyChanged(nameof(HasTemplateOverrideStatusMessage));
                }
            }
        }

        public bool HasTemplateOverrideStatusMessage => !string.IsNullOrWhiteSpace(TemplateOverrideStatusMessage);

        public bool ShowForkTemplateCommand => _templateOverrideService is not null;

        public bool CanForkTemplate => ShowForkTemplateCommand && _displayTemplatePreview is { IsReadOnly: true, SnapshotText: { Length: > 0 } };

        public ICommand ForkTemplateCommand => _forkTemplateCommand;

        internal event EventHandler<SourcePreviewViewModel>? SourcePreviewRequested;

        public SourcePreviewViewModel? InlinePreview
        {
            get => _inlinePreview;
            private set
            {
                if (ReferenceEquals(_inlinePreview, value))
                {
                    return;
                }

                _inlinePreview?.DetachFromMutationOwner();
                _inlinePreview?.DetachFromWorkspace();
                RaiseAndSetIfChanged(ref _inlinePreview, value);
            }
        }

        public async void NavigateToSource()
        {
            await NavigateToSourceAsync().ConfigureAwait(false);
        }

        private async Task NavigateToSourceAsync()
        {
            try
            {
                var info = await LoadSourceInfoAsync().ConfigureAwait(false);
                if (info is not null)
                {
                    await _sourceNavigator.NavigateAsync(info).ConfigureAwait(false);
                    return;
                }

                var templateRequest = await EnsureTemplatePreviewAsync().ConfigureAwait(false);
                if (templateRequest is not null)
                {
                    var navigationInfo = BuildTemplateNavigationInfo(templateRequest);
                    if (navigationInfo is not null)
                    {
                        await _sourceNavigator.NavigateAsync(navigationInfo).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // Navigation failures are non-fatal.
            }
        }

        public async void PreviewSource()
        {
            await PreviewSourceAsync().ConfigureAwait(false);
        }

        private async Task PreviewSourceAsync()
        {
            try
            {
                var templateRequest = await EnsureTemplatePreviewAsync().ConfigureAwait(false);
                if (templateRequest is not null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var preview = SourcePreviewViewModel.CreateTemplatePreview(
                            templateRequest,
                            _sourceNavigator,
                            mutationOwner: _mainViewModel,
                            workspace: _mainViewModel?.XamlAstWorkspace);
                        SourcePreviewRequested?.Invoke(this, preview);
                    });
                    return;
                }

                var info = await LoadSourceInfoAsync().ConfigureAwait(false);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SourcePreviewViewModel preview = info is not null
                        ? new SourcePreviewViewModel(info, _sourceNavigator, mutationOwner: _mainViewModel, xamlAstWorkspace: _mainViewModel?.XamlAstWorkspace)
                        : SourcePreviewViewModel.CreateUnavailable(Description, _sourceNavigator, mutationOwner: _mainViewModel);
                    SourcePreviewRequested?.Invoke(this, preview);
                });
            }
            catch
            {
                // Preview failures are non-fatal.
            }
        }

        private void UpdateInlinePreview(SourceInfo? info)
        {
            if (info is null)
            {
                InlinePreview = null;
                _templatePreviewTask = null;
                _templatePreviewMap.Clear();
                _displayTemplatePreview = null;
                if (_hasTemplatePreview)
                {
                    _hasTemplatePreview = false;
                    NotifySourceCommandAvailabilityChanged();
                }
                return;
            }

            _ = UpdateInlinePreviewAsync(info);
        }

        private async Task UpdateInlinePreviewAsync(SourceInfo info)
        {
            if (_templateSourceResolver is null || _templatePropertyNames.Count == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplySourceInfoPreview(info));
                return;
            }

            var request = await LoadTemplatePreviewRequestAsync(info).ConfigureAwait(false);
            if (request is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyTemplatePreview(request));
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplySourceInfoPreview(info));
        }

        private void ApplyTemplatePreview(TemplatePreviewRequest request)
        {
            if (request is null)
            {
                return;
            }

            _displayTemplatePreview = request;

            var preview = SourcePreviewViewModel.CreateTemplatePreview(
                request,
                _sourceNavigator,
                mutationOwner: _mainViewModel,
                workspace: _mainViewModel?.XamlAstWorkspace);

            InlinePreview = preview;
            TemplateScopeDescription = BuildTemplateScopeDescription(request);
            TemplateOverrideStatusMessage = null;

            var availabilityChanged = !_hasTemplatePreview;
            _hasTemplatePreview = true;
            if (availabilityChanged)
            {
                NotifySourceCommandAvailabilityChanged();
            }
            else
            {
                UpdateCommandStates();
            }

            _forkTemplateCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(CanForkTemplate));
        }

        private void ApplySourceInfoPreview(SourceInfo info)
        {
            if (InlinePreview is { } existing && Equals(existing.SourceInfo, info) && !_hasTemplatePreview)
            {
                return;
            }

            var preview = new SourcePreviewViewModel(info, _sourceNavigator, mutationOwner: _mainViewModel, xamlAstWorkspace: _mainViewModel?.XamlAstWorkspace);
            InlinePreview = preview;
            _displayTemplatePreview = null;
            TemplateScopeDescription = null;
            TemplateOverrideStatusMessage = null;

            var availabilityChanged = _hasTemplatePreview;
            _hasTemplatePreview = false;
            if (availabilityChanged)
            {
                NotifySourceCommandAvailabilityChanged();
            }
            else
            {
                UpdateCommandStates();
            }

            _ = preview.LoadAsync();
            _forkTemplateCommand.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(CanForkTemplate));
        }

        private void RegisterTemplateProperty(AvaloniaProperty property)
        {
            if (_valueFrame.Type is not IValueFrameDiagnostic.FrameType.Template and not IValueFrameDiagnostic.FrameType.Theme)
            {
                return;
            }

            _templatePropertyNames.Add(property.Name);

            void AddMapping(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                if (!_templatePropertyLookup.TryGetValue(key, out var list))
                {
                    list = new List<AvaloniaProperty>();
                    _templatePropertyLookup[key] = list;
                }

                if (!list.Contains(property))
                {
                    list.Add(property);
                }
            }

            AddMapping(property.Name);
            var ownerType = property.OwnerType?.Name;
            if (!string.IsNullOrEmpty(ownerType))
            {
                AddMapping(ownerType + "." + property.Name);
            }

            _templateProperties.Add(property);
        }

        private Task<TemplatePreviewRequest?> LoadTemplatePreviewRequestAsync(SourceInfo? info = null)
        {
            if (_templatePreviewTask is { } existing)
            {
                return existing;
            }

            var task = BuildTemplatePreviewRequestAsync(info);
            _templatePreviewTask = task;
            return task;
        }

        private async Task<TemplatePreviewRequest?> BuildTemplatePreviewRequestAsync(SourceInfo? info)
        {
            _templatePreviewMap.Clear();

            if (_templateSourceResolver is null || _templatePropertyNames.Count == 0)
            {
                return null;
            }

            var resolvedInfo = info ?? SourceInfo ?? await LoadSourceInfoAsync().ConfigureAwait(false);
            if (resolvedInfo is null)
            {
                return null;
            }

            var resolution = await ResolveTemplatePreviewMapAsync(resolvedInfo).ConfigureAwait(false);
            if (resolution is null)
            {
                return null;
            }

            foreach (var pair in resolution.Map)
            {
                _templatePreviewMap[pair.Key] = pair.Value;
            }

            return resolution.Display ?? resolution.Map.Values.FirstOrDefault();
        }

        private async Task<TemplatePreviewRequest?> EnsureTemplatePreviewAsync()
        {
            if (_templateSourceResolver is null || _templatePropertyNames.Count == 0)
            {
                return null;
            }

            var request = await LoadTemplatePreviewRequestAsync().ConfigureAwait(false);
            if (request is null)
            {
                return null;
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplyTemplatePreview(request));
            return request;
        }

        private async Task<TemplatePreviewResolution?> ResolveTemplatePreviewMapAsync(SourceInfo info)
        {
            if (_mainViewModel?.XamlAstWorkspace is null || _templateSourceResolver is null)
            {
                return null;
            }

            var documentPath = info.LocalPath;
            if (string.IsNullOrWhiteSpace(documentPath))
            {
                return null;
            }

            IXamlAstIndex index;
            XamlAstDocument document;

            try
            {
                document = await _mainViewModel.XamlAstWorkspace.GetDocumentAsync(documentPath).ConfigureAwait(false);
                index = await _mainViewModel.XamlAstWorkspace.GetIndexAsync(document.Path).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }

            var ownerDescriptor = FindStyleDescriptor(index, info);
            if (ownerDescriptor is null)
            {
                return null;
            }

            var bindings = FindTemplateBindings(index, ownerDescriptor);
            if (bindings.Count == 0)
            {
                return null;
            }

            var results = new Dictionary<AvaloniaProperty, TemplatePreviewRequest>();
            TemplatePreviewRequest? displayCandidate = null;
            var seenBindings = new HashSet<XamlTemplateBindingDescriptor>();

            foreach (var binding in bindings)
            {
                if (!seenBindings.Add(binding))
                {
                    continue;
                }

                foreach (var property in ResolvePropertiesForBinding(binding))
                {
                    if (results.ContainsKey(property))
                    {
                        continue;
                    }

                    TemplatePreviewRequest? request;
                    try
                    {
                        request = await _templateSourceResolver.ResolveAsync(document.Path, binding).ConfigureAwait(false);
                    }
                    catch
                    {
                        request = null;
                    }

                    if (request is null)
                    {
                        continue;
                    }

                    results[property] = request;

                    if (!request.IsReadOnly && request.Document is not null)
                    {
                        displayCandidate ??= request;
                    }
                    else if (displayCandidate is null)
                    {
                        displayCandidate = request;
                    }
                }
            }

            if (results.Count == 0)
            {
                return null;
            }

            return new TemplatePreviewResolution(results, displayCandidate);
        }

        private static XamlAstNodeDescriptor? FindStyleDescriptor(IXamlAstIndex index, SourceInfo info)
        {
            var startLine = info.StartLine ?? 0;
            if (startLine <= 0)
            {
                return null;
            }

            var endLine = info.EndLine ?? startLine;
            XamlAstNodeDescriptor? best = null;

            foreach (var style in index.Styles)
            {
                var descriptor = style.Node;
                if (descriptor is null)
                {
                    continue;
                }

                if (ContainsLine(descriptor.LineSpan, startLine, endLine))
                {
                    if (best is null || SpanLength(descriptor.LineSpan) < SpanLength(best.LineSpan))
                    {
                        best = descriptor;
                    }
                }
            }

            if (best is not null)
            {
                return best;
            }

            foreach (var node in index.Nodes)
            {
                if (ContainsLine(node.LineSpan, startLine, endLine))
                {
                    if (best is null || SpanLength(node.LineSpan) < SpanLength(best.LineSpan))
                    {
                        best = node;
                    }
                }
            }

            return best;
        }

        private static bool ContainsLine(LinePositionSpan span, int startLine, int endLine)
        {
            var spanStart = Math.Max(span.Start.Line, 1);
            var spanEnd = Math.Max(span.End.Line, spanStart);
            return startLine >= spanStart && startLine <= spanEnd && endLine <= spanEnd;
        }

        private static int SpanLength(LinePositionSpan span)
        {
            var spanStart = Math.Max(span.Start.Line, 1);
            var spanEnd = Math.Max(span.End.Line, spanStart);
            return spanEnd - spanStart;
        }

        private static IReadOnlyList<XamlTemplateBindingDescriptor> FindTemplateBindings(
            IXamlAstIndex index,
            XamlAstNodeDescriptor ownerDescriptor)
        {
            var list = new List<XamlTemplateBindingDescriptor>();

            foreach (var binding in index.TemplateBindings)
            {
                if (IsBindingWithinStyle(binding, ownerDescriptor))
                {
                    list.Add(binding);
                }
            }

            return list;
        }

        private IEnumerable<AvaloniaProperty> ResolvePropertiesForBinding(XamlTemplateBindingDescriptor binding)
        {
            var delivered = new HashSet<AvaloniaProperty>();

            foreach (var key in EnumerateBindingKeys(binding))
            {
                if (_templatePropertyLookup.TryGetValue(key, out var properties))
                {
                    foreach (var property in properties)
                    {
                        if (delivered.Add(property))
                        {
                            yield return property;
                        }
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateBindingKeys(XamlTemplateBindingDescriptor binding)
        {
            if (!string.IsNullOrEmpty(binding.PropertyName))
            {
                yield return binding.PropertyName;
            }

            if (!string.IsNullOrEmpty(binding.RawProperty))
            {
                var raw = binding.RawProperty;
                yield return raw;

                var lastDot = raw.LastIndexOf('.');
                if (lastDot >= 0 && lastDot + 1 < raw.Length)
                {
                    yield return raw.Substring(lastDot + 1);
                }
            }
        }

        private sealed class TemplatePreviewResolution
        {
            public TemplatePreviewResolution(
                Dictionary<AvaloniaProperty, TemplatePreviewRequest> map,
                TemplatePreviewRequest? display)
            {
                Map = map;
                Display = display;
            }

            public Dictionary<AvaloniaProperty, TemplatePreviewRequest> Map { get; }

            public TemplatePreviewRequest? Display { get; }
        }

        internal bool TryCreateTemplateMutationContext(
            AvaloniaProperty property,
            out PropertyChangeContext? context,
            out string? errorMessage)
        {
            context = null;
            errorMessage = null;

            if (_templateSourceResolver is null || _templateProperties.Count == 0)
            {
                return false;
            }

            if (!_templateProperties.Contains(property))
            {
                return false;
            }

            if (!_templatePreviewMap.TryGetValue(property, out var request))
            {
                try
                {
                    LoadTemplatePreviewRequestAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // ignored - failure will surface below if request remains unavailable.
                }

                _templatePreviewMap.TryGetValue(property, out request);
            }

            if (request is null)
            {
                errorMessage = "Template source could not be resolved for editing.";
                return true;
            }

            if (request.IsReadOnly)
            {
                errorMessage = request.ReadOnlyMessage ?? "Template source is read-only.";
                return true;
            }

            var document = request.Document;
            if (document is null)
            {
                errorMessage = request.ErrorMessage ?? "Template source document is unavailable.";
                return true;
            }

            var descriptor = request.Binding.PropertyElement ??
                             request.Binding.Owner ??
                             request.Binding.InlineTemplate ??
                             request.SourceDescriptor;

            if (descriptor is null)
            {
                errorMessage = "Template descriptor is unavailable for mutation.";
                return true;
            }

            context = new PropertyChangeContext(
                _styledElement,
                property,
                document,
                descriptor,
                _valueFrame.Type.ToString(),
                _valueFrame.Priority.ToString());
            context.ScopeDescription = BuildTemplateScopeDescription(request);

            return true;
        }

        private static bool IsBindingWithinStyle(XamlTemplateBindingDescriptor binding, XamlAstNodeDescriptor ownerDescriptor)
        {
            return IsDescendantOf(binding.Owner, ownerDescriptor) ||
                   IsDescendantOf(binding.PropertyElement, ownerDescriptor) ||
                   IsDescendantOf(binding.InlineTemplate, ownerDescriptor) ||
                   IsDescendantOf(binding.ResourceDescriptor, ownerDescriptor);
        }

        private static bool IsDescendantOf(XamlAstNodeDescriptor? candidate, XamlAstNodeDescriptor ancestor)
        {
            if (candidate is null)
            {
                return false;
            }

            var ancestorPath = ancestor.Path;
            var candidatePath = candidate.Path;
            if (candidatePath.Count < ancestorPath.Count)
            {
                return false;
            }

            for (var index = 0; index < ancestorPath.Count; index++)
            {
                if (candidatePath[index] != ancestorPath[index])
                {
                    return false;
                }
            }

            return true;
        }

        private static SourceInfo? BuildTemplateNavigationInfo(TemplatePreviewRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.DocumentPath) || request.SourceUri is not null)
            {
                var origin = string.IsNullOrWhiteSpace(request.DocumentPath) ? SourceOrigin.SourceLink : SourceOrigin.Local;
                return new SourceInfo(
                    request.DocumentPath,
                    request.SourceUri,
                    request.LineSpan?.Start.Line,
                    request.LineSpan?.Start.Column,
                    request.LineSpan?.End.Line,
                    request.LineSpan?.End.Column,
                    origin);
            }

            return null;
        }

        private string? BuildTemplateScopeDescription(TemplatePreviewRequest request)
        {
            var ownerName = request.OwnerDisplayName;
            if (string.IsNullOrWhiteSpace(ownerName))
            {
                ownerName = _styledElement.GetType().Name;
            }

            var source = request.DocumentPath ?? request.SourceUri?.ToString();
            if (string.IsNullOrWhiteSpace(source))
            {
                source = request.ProviderDisplayName ?? "external resource";
            }

            return $"Editing {ownerName} template from {source}";
        }

        private async Task ForkTemplateAsync()
        {
            if (!CanForkTemplate || _templateOverrideService is null || _displayTemplatePreview is null)
            {
                return;
            }

            TemplateOverrideStatusMessage = null;

            var context = SourceInfo ?? await LoadSourceInfoAsync().ConfigureAwait(false);

            try
            {
                var result = await _templateOverrideService.CreateLocalOverrideAsync(context, _displayTemplatePreview).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => TemplateOverrideStatusMessage = result.Message);
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => TemplateOverrideStatusMessage = ex.Message);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _forkTemplateCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(CanForkTemplate));
                UpdateCommandStates();
            });
        }

        internal void DetachMutationObserver()
        {
            _inlinePreview?.DetachFromMutationOwner();
            _inlinePreview?.DetachFromWorkspace();
        }

        internal void UpdateSourceNavigation(ISourceInfoService sourceInfoService, ISourceNavigator sourceNavigator)
        {
            if (sourceInfoService is null)
            {
                throw new ArgumentNullException(nameof(sourceInfoService));
            }

            if (sourceNavigator is null)
            {
                throw new ArgumentNullException(nameof(sourceNavigator));
            }

            var serviceChanged = !ReferenceEquals(_sourceInfoService, sourceInfoService);
            _sourceInfoService = sourceInfoService;
            _sourceNavigator = sourceNavigator;

            if (serviceChanged)
            {
                _sourceInfoTask = null;
                _sourceInfoLoadAttempted = false;
                SourceInfo = null;
            }
        }
        
        private static (object resourceKey, bool isDynamic)? GetResourceInfo(object? value)
        {
            if (value is StaticResourceExtension staticResource
                && staticResource.ResourceKey != null)
            {
                return (staticResource.ResourceKey, false);
            }
            else if (value is DynamicResourceExtension dynamicResource
                     && dynamicResource.ResourceKey != null)
            {
                return (dynamicResource.ResourceKey, true);
            }

            return null;
        }

        private static bool IsBinding(object? value)
        {
            switch (value)
            {
                case Binding:
                case CompiledBindingExtension:
                case TemplateBinding:
                    return true;
            }

            return false;
        }

        private string? SourceToString(object? source)
        {
            if (source is Style style)
            {
                StyleBase? currentStyle = style;
                var selectors = new Stack<string>();

                while (currentStyle is not null)
                {
                    if (currentStyle is Style { Selector: { } selector })
                    {
                        selectors.Push(selector.ToString());
                    }
                    if (currentStyle is ControlTheme theme)
                    {
                        selectors.Push("Theme " + theme.TargetType?.Name);
                    }

                    currentStyle = currentStyle.Parent as StyleBase;
                }

                return string.Concat(selectors).Replace("^", "");
            }
            else if (source is ControlTheme controlTheme)
            {
                return controlTheme.TargetType?.Name;
            }
            else if (source is StyledElement styledElement)
            {
                return styledElement.StyleKey?.Name;
            }

            return null;
        }

        private Task<SourceInfo?> LoadSourceInfoAsync()
        {
            var existing = _sourceInfoTask;
            if (existing is not null)
            {
                return existing;
            }

            MarkSourceInfoLoadAttempted();

            async Task<SourceInfo?> ResolveAsync()
            {
                try
                {
                    var info = await _sourceInfoService.GetForValueFrameAsync(_valueFrame).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => SourceInfo = info);
                    return info;
                }
                catch
                {
                    // Ignore resolution failures.
                    return null;
                }
            }

            var task = ResolveAsync();
            _sourceInfoTask = task;
            return task;
        }

        private void MarkSourceInfoLoadAttempted()
        {
            if (_sourceInfoLoadAttempted)
            {
                return;
            }

            _sourceInfoLoadAttempted = true;
            NotifySourceCommandAvailabilityChanged();
        }

        private void UpdateCommandStates()
        {
            _previewSourceCommand.RaiseCanExecuteChanged();
            _navigateToSourceCommand.RaiseCanExecuteChanged();
            _forkTemplateCommand.RaiseCanExecuteChanged();
        }

        private void NotifySourceCommandAvailabilityChanged()
        {
            RaisePropertyChanged(nameof(ShowSourceCommands));
            RaisePropertyChanged(nameof(CanNavigateToSource));
            RaisePropertyChanged(nameof(CanPreviewSource));
            UpdateCommandStates();
        }
    }
}
