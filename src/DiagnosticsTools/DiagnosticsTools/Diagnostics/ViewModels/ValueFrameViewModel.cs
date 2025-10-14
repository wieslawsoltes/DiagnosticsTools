using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.ViewModels
{
    public class ValueFrameViewModel : ViewModelBase
    {
        private readonly IValueFrameDiagnostic _valueFrame;
        private bool _isActive;
        private bool _isVisible;
        private ISourceInfoService _sourceInfoService;
        private ISourceNavigator _sourceNavigator;
        private SourceInfo? _sourceInfo;
        private Task? _sourceInfoTask;

        public ValueFrameViewModel(
            StyledElement styledElement,
            IValueFrameDiagnostic valueFrame,
            IClipboard? clipboard,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator)
        {
            _valueFrame = valueFrame;
            IsVisible = true;
            _sourceInfoService = sourceInfoService ?? throw new ArgumentNullException(nameof(sourceInfoService));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));

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
                Setters.Add(setterVm);
            }

            Update();
            _ = LoadSourceInfoAsync();
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
                RaisePropertyChanged(nameof(SourceInfo));
                RaisePropertyChanged(nameof(SourceSummary));
                RaisePropertyChanged(nameof(HasSource));
                RaisePropertyChanged(nameof(CanNavigateToSource));
            }
        }

        public string? SourceSummary => SourceInfo?.DisplayPath;

        public bool HasSource => SourceInfo is not null;

        public bool CanNavigateToSource => SourceInfo is not null;

        public async void NavigateToSource()
        {
            try
            {
                await LoadSourceInfoAsync().ConfigureAwait(false);
                if (SourceInfo is null)
                {
                    return;
                }

                await _sourceNavigator.NavigateAsync(SourceInfo).ConfigureAwait(false);
            }
            catch
            {
                // Navigation failures are non-fatal.
            }
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
                SourceInfo = null;
                _ = LoadSourceInfoAsync();
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

        private Task LoadSourceInfoAsync()
        {
            var existing = _sourceInfoTask;
            if (existing is not null)
            {
                return existing;
            }

            async Task ResolveAsync()
            {
                try
                {
                    var info = await _sourceInfoService.GetForValueFrameAsync(_valueFrame).ConfigureAwait(false);
                    await Dispatcher.UIThread.InvokeAsync(() => SourceInfo = info);
                }
                catch
                {
                    // Ignore resolution failures.
                }
            }

            var task = ResolveAsync();
            _sourceInfoTask = task;
            return task;
        }
    }
}
