using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Avalonia.Reactive;
using Avalonia.Diagnostics;

namespace Avalonia.Diagnostics.Views
{
    public partial class MainWindow : Window, IStyleHost
    {
        private readonly IDisposable? _inputSubscription;
        private readonly HashSet<Popup> _frozenPopupStates;
        private AvaloniaObject? _root;
        private PixelPoint _lastPointerPosition;
        private HotKeyConfiguration? _hotKeys;
    private DevToolsOptions _options = new();
    private ISourceInfoService? _sourceInfoService;
    private ISourceNavigator? _sourceNavigator;
    private bool _ownsSourceInfoService;

        public MainWindow()
        {
            InitializeComponent();

            // Apply the FluentTheme.Window theme; this must be done after the XAML is parsed as
            // the theme is included in the MainWindow's XAML.
            if (Theme is null && this.FindResource(typeof(Window)) is ControlTheme windowTheme)
                Theme = windowTheme;

            _inputSubscription = InputManager.Instance?.Process
                .Subscribe(x =>
                {
                    if (x is RawPointerEventArgs pointerEventArgs)
                    {
                        if (pointerEventArgs.Root is Visual visualRoot)
                        {
                            var topLevel = TopLevel.GetTopLevel(visualRoot);
                            if (topLevel is not null && DevTools.IsDevToolsWindow(topLevel))
                            {
                                _lastPointerPosition = default;
                                return;
                            }

                            _lastPointerPosition = visualRoot.PointToScreen(pointerEventArgs.Position);
                        }
                    }
                    else if (x is RawKeyEventArgs keyEventArgs && keyEventArgs.Type == RawKeyEventType.KeyDown)
                    {
                        RawKeyDown(keyEventArgs);
                    }
                });
            
            _frozenPopupStates = new HashSet<Popup>();

            EventHandler? lh = default;
            lh = (s, e) =>
            {
                this.Opened -= lh;
                if ((DataContext as MainViewModel)?.StartupScreenIndex is { } index)
                {
                    var screens = this.Screens;
                    if (index > -1 && index < screens.ScreenCount)
                    {
                        var screen = screens.All[index];
                        this.Position = screen.Bounds.TopLeft;
                        this.WindowState = WindowState.Maximized;
                    }
                }
            };
            this.Opened += lh;
        }

        public AvaloniaObject? Root
        {
            get => _root;
            set
            {
                if (_root != value)
                {
                    if (_root is ICloseable oldClosable)
                    {
                        oldClosable.Closed -= RootClosed;
                    }

                    _root = value;

                    if (_root is  ICloseable newClosable)
                    {
                        newClosable.Closed += RootClosed;
                        var services = EnsureSourceNavigationServices();
                        var viewModel = new MainViewModel(_root, services.Service, services.Navigator, _options.RoslynWorkspace);
                        DataContext = viewModel;
                        viewModel.SetOptions(_options);
                    }
                    else
                    {
                        DataContext = null;
                    }
                }
            }
        }

        IStyleHost? IStyleHost.StylingParent => null;

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _inputSubscription?.Dispose();

            foreach (var state in _frozenPopupStates)
            {
                state.Closing -= PopupOnClosing;
            }

            _frozenPopupStates.Clear();

            if (_root is ICloseable cloneable)
            {
                cloneable.Closed -= RootClosed;
                _root = null;
            }

            ((MainViewModel?)DataContext)?.Dispose();
            DisposeOwnedSourceService();
            _sourceInfoService = null;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private Control? GetHoveredControl(TopLevel topLevel)
        {
            var point = topLevel.PointToClient(_lastPointerPosition);

            return (Control?)topLevel.GetVisualsAt(point, x =>
                {
                    if (x is AdornerLayer || !x.IsVisible)
                    {
                        return false;
                    }

                    return !(x is IInputElement ie) || ie.IsHitTestVisible;
                })
                .FirstOrDefault();
        }

        private static List<PopupRoot> GetPopupRoots(TopLevel root)
        {
            var popupRoots = new List<PopupRoot>();

            void ProcessProperty<T>(Control control, AvaloniaProperty<T> property)
            {
                if (control.GetValue(property) is IPopupHostProvider popupProvider
                    && popupProvider.PopupHost is PopupRoot popupRoot)
                {
                    popupRoots.Add(popupRoot);
                }
            }

            foreach (var control in root.GetVisualDescendants().OfType<Control>())
            {
                if (control is Popup p && p.Host is PopupRoot popupRoot)
                {
                    popupRoots.Add(popupRoot);
                }

                ProcessProperty(control, ContextFlyoutProperty);
                ProcessProperty(control, ContextMenuProperty);
                ProcessProperty(control, FlyoutBase.AttachedFlyoutProperty);
                ProcessProperty(control, ToolTipDiagnostics.ToolTipProperty);
                ProcessProperty(control, Button.FlyoutProperty);
            }

            return popupRoots;
        }

        private void RawKeyDown(RawKeyEventArgs e)
        {
            if (_hotKeys is null ||
                DataContext is not MainViewModel vm ||
                vm.PointerOverRoot is not TopLevel root)
            {
                return;
            }

            if (root is PopupRoot pr && pr.ParentTopLevel != null)
            {
                root = pr.ParentTopLevel;
            }

            var modifiers = MergeModifiers(e.Key, e.Modifiers.ToKeyModifiers());

            if (IsMatched(_hotKeys.ValueFramesFreeze, e.Key, modifiers))
            {
                FreezeValueFrames(vm);
            }
            else if (IsMatched(_hotKeys.ValueFramesUnfreeze, e.Key, modifiers))
            {
                UnfreezeValueFrames(vm);
            }
            else if (IsMatched(_hotKeys.TogglePopupFreeze, e.Key, modifiers))
            {
                ToggleFreezePopups(root, vm);
            }
            else if (IsMatched(_hotKeys.ScreenshotSelectedControl, e.Key, modifiers))
            {
                ScreenshotSelectedControl(vm);
            }
            else if (IsMatched(_hotKeys.InspectHoveredControl, e.Key, modifiers))
            {
                InspectHoveredControl(root, vm);
            }
            else if (IsMatched(_hotKeys.UndoMutation, e.Key, modifiers))
            {
                if (vm.UndoMutationCommand.CanExecute(null))
                {
                    vm.UndoMutationCommand.Execute(null);
                }
            }
            else if (IsMatched(_hotKeys.RedoMutation, e.Key, modifiers))
            {
                if (vm.RedoMutationCommand.CanExecute(null))
                {
                    vm.RedoMutationCommand.Execute(null);
                }
            }

            static bool IsMatched(KeyGesture gesture, Key key, KeyModifiers modifiers)
            {
                return (gesture.Key == key || gesture.Key == Key.None) && modifiers.HasAllFlags(gesture.KeyModifiers);
            }

            // When Control, Shift, or Alt are initially pressed, they are the Key and not part of Modifiers
            // This merges so modifier keys alone can more easily trigger actions
            static KeyModifiers MergeModifiers(Key key, KeyModifiers modifiers)
            {
                return key switch
                {
                    Key.LeftCtrl or Key.RightCtrl => modifiers | KeyModifiers.Control,
                    Key.LeftShift or Key.RightShift => modifiers | KeyModifiers.Shift,
                    Key.LeftAlt or Key.RightAlt => modifiers | KeyModifiers.Alt,
                    _ => modifiers
                };
            }
        }

        private void FreezeValueFrames(MainViewModel vm)
        {
            vm.EnableSnapshotStyles(true);
        }

        private void UnfreezeValueFrames(MainViewModel vm)
        {
            vm.EnableSnapshotStyles(false);
        }

        private void ToggleFreezePopups(TopLevel root, MainViewModel vm)
        {
            vm.FreezePopups = !vm.FreezePopups;

            foreach (var popupRoot in GetPopupRoots(root))
            {
                if (popupRoot.Parent is Popup popup)
                {
                    if (vm.FreezePopups)
                    {
                        popup.Closing += PopupOnClosing;
                        _frozenPopupStates.Add(popup);
                    }
                    else
                    {
                        popup.Closing -= PopupOnClosing;
                        _frozenPopupStates.Remove(popup);
                    }
                }
            }
        }

        private void ScreenshotSelectedControl(MainViewModel vm)
        {
            vm.Shot(null);
        }

        private void InspectHoveredControl(TopLevel root, MainViewModel vm)
        {
            Control? control = null;

            foreach (var popupRoot in GetPopupRoots(root))
            {
                control = GetHoveredControl(popupRoot);

                if (control != null)
                {
                    break;
                }
            }

            control ??= GetHoveredControl(root);

            if (control != null)
            {
                vm.SelectControl(control);
            }
        }

        private void PopupOnClosing(object? sender, CancelEventArgs e)
        {
            var vm = (MainViewModel?)DataContext;
            if (vm?.FreezePopups == true)
            {
                e.Cancel = true;
            }
        }
        
        private void RootClosed(object? sender, EventArgs e) => Close();

        public void SetOptions(DevToolsOptions options)
        {
            _options = options ?? new DevToolsOptions();
            _hotKeys = _options.HotKeys;

            var services = EnsureSourceNavigationServices();
            if (DataContext is MainViewModel vm)
            {
                vm.UpdateSourceNavigation(services.Service, services.Navigator);
                vm.SetOptions(_options);
            }

            if (_options.ThemeVariant is { } themeVariant)
            {
                RequestedThemeVariant = themeVariant;
            }
        }

        internal void SelectedControl(Control? control)
        {
            if (control is { })
            {
                (DataContext as MainViewModel)?.SelectControl(control);
            }
        }

        private (ISourceInfoService Service, ISourceNavigator Navigator) EnsureSourceNavigationServices()
        {
            if (_options.SourceInfoService is { } providedService && !ReferenceEquals(providedService, _sourceInfoService))
            {
                DisposeOwnedSourceService();
                _sourceInfoService = providedService;
                _ownsSourceInfoService = false;
            }

            if (_sourceInfoService is null)
            {
                _sourceInfoService = new SourceInfoService();
                _ownsSourceInfoService = true;
            }

            if (_options.SourceNavigator is { } providedNavigator)
            {
                _sourceNavigator = providedNavigator;
            }

            _sourceNavigator ??= new DefaultSourceNavigator();

            return (_sourceInfoService, _sourceNavigator);
        }

        private void DisposeOwnedSourceService()
        {
            if (_ownsSourceInfoService && _sourceInfoService is IDisposable disposable)
            {
                disposable.Dispose();
                _ownsSourceInfoService = false;
            }
        }
    }
}
