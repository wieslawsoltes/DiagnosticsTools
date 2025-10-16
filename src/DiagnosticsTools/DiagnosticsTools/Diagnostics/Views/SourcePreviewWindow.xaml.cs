using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Diagnostics.Controls;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.Views
{
    public partial class SourcePreviewWindow : Window
    {
        private SourcePreviewViewModel? _viewModel;
        private Grid? _comparisonGrid;
        private SourcePreviewEditor? _primaryEditor;
        private SourcePreviewEditor? _runtimeEditor;
        private GridSplitter? _splitter;
        private ColumnDefinition? _primaryColumn;
        private ColumnDefinition? _splitterColumn;
        private ColumnDefinition? _secondaryColumn;
        private RowDefinition? _primaryRow;
        private RowDefinition? _splitterRow;
        private RowDefinition? _secondaryRow;
        private SourcePreviewScrollCoordinator? _scrollCoordinator;
        private IDisposable? _primarySync;
        private IDisposable? _runtimeSync;
        private bool _isApplyingSplitMetrics;

        public SourcePreviewWindow()
        {
            InitializeComponent();
            Opened += OnOpened;
            Closed += OnClosed;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            _comparisonGrid = this.FindControl<Grid>("ComparisonGrid");
            _primaryEditor = this.FindControl<SourcePreviewEditor>("PrimaryEditor");
            _runtimeEditor = this.FindControl<SourcePreviewEditor>("RuntimeEditor");
            _splitter = this.FindControl<GridSplitter>("ComparisonSplitter");

            if (_comparisonGrid is not null)
            {
                if (_comparisonGrid.ColumnDefinitions.Count >= 3)
                {
                    _primaryColumn = _comparisonGrid.ColumnDefinitions[0];
                    _splitterColumn = _comparisonGrid.ColumnDefinitions[1];
                    _secondaryColumn = _comparisonGrid.ColumnDefinitions[2];
                }

                if (_comparisonGrid.RowDefinitions.Count >= 3)
                {
                    _primaryRow = _comparisonGrid.RowDefinitions[0];
                    _splitterRow = _comparisonGrid.RowDefinitions[1];
                    _secondaryRow = _comparisonGrid.RowDefinitions[2];
                }

                _comparisonGrid.LayoutUpdated += OnComparisonGridLayoutUpdated;
            }

            EnsureScrollSynchronization();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.DetachFromMutationOwner();
                _viewModel.DetachFromWorkspace();
            }

            _viewModel = DataContext as SourcePreviewViewModel;

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                RefreshSplitLayout();
            }
        }

        public static void Show(TopLevel? owner, SourcePreviewViewModel viewModel)
        {
            if (viewModel is null)
            {
                throw new ArgumentNullException(nameof(viewModel));
            }

            var window = new SourcePreviewWindow
            {
                DataContext = viewModel
            };

            void ShowAction() => ShowWindow(window, owner);

            if (Dispatcher.UIThread.CheckAccess())
            {
                ShowAction();
            }
            else
            {
                Dispatcher.UIThread.Post(ShowAction);
            }
        }

        private static void ShowWindow(SourcePreviewWindow window, TopLevel? owner)
        {
            if (owner is Window windowOwner)
            {
                window.Show(windowOwner);
            }
            else
            {
                window.Show();
            }

            window.Activate();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not SourcePreviewViewModel vm)
            {
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(SourcePreviewViewModel.IsSplitViewEnabled):
                case nameof(SourcePreviewViewModel.SplitRatio):
                case nameof(SourcePreviewViewModel.SplitOrientation):
                case nameof(SourcePreviewViewModel.RuntimeComparison):
                    RefreshSplitLayout();
                    break;
            }
        }
        
        private void RefreshSplitLayout()
        {
            if (_viewModel is null)
            {
                return;
            }

            ApplySplitLayout(_viewModel);

            if (_viewModel.IsSplitViewEnabled && _viewModel.RuntimeComparison is not null)
            {
                EnsureScrollSynchronization();

                if (_primaryEditor is not null)
                {
                    _scrollCoordinator?.RequestSynchronize(_primaryEditor);
                }
            }
        }

        private void ApplySplitLayout(SourcePreviewViewModel vm)
        {
            if (_primaryColumn is null || _splitterColumn is null || _secondaryColumn is null ||
                _primaryRow is null || _splitterRow is null || _secondaryRow is null ||
                _primaryEditor is null || _runtimeEditor is null || _splitter is null)
            {
                return;
            }

            var isEnabled = vm.IsSplitViewEnabled && vm.RuntimeComparison is not null;
            var ratio = vm.SplitRatio;
            var complementary = Math.Max(0.05, 1.0 - ratio);

            _isApplyingSplitMetrics = true;
            try
            {
                if (vm.SplitOrientation == SourcePreviewSplitOrientation.Horizontal)
                {
                    _primaryRow.Height = new GridLength(1, GridUnitType.Star);
                    _splitterRow.Height = new GridLength(0);
                    _secondaryRow.Height = new GridLength(0);

                    if (isEnabled)
                    {
                        _primaryColumn.Width = new GridLength(ratio, GridUnitType.Star);
                        _splitterColumn.Width = GridLength.Auto;
                        _secondaryColumn.Width = new GridLength(complementary, GridUnitType.Star);
                    }
                    else
                    {
                        _primaryColumn.Width = new GridLength(1, GridUnitType.Star);
                        _splitterColumn.Width = new GridLength(0);
                        _secondaryColumn.Width = new GridLength(0);
                    }

                    Grid.SetRow(_primaryEditor, 0);
                    Grid.SetRowSpan(_primaryEditor, 3);
                    Grid.SetColumn(_primaryEditor, 0);
                    Grid.SetColumnSpan(_primaryEditor, 1);

                    Grid.SetRow(_runtimeEditor, 0);
                    Grid.SetRowSpan(_runtimeEditor, 3);
                    Grid.SetColumn(_runtimeEditor, 2);
                    Grid.SetColumnSpan(_runtimeEditor, 1);

                    Grid.SetRow(_splitter, 0);
                    Grid.SetRowSpan(_splitter, 3);
                    Grid.SetColumn(_splitter, 1);
                    Grid.SetColumnSpan(_splitter, 1);

                    _splitter.ResizeDirection = GridResizeDirection.Columns;
                    _splitter.HorizontalAlignment = HorizontalAlignment.Center;
                    _splitter.VerticalAlignment = VerticalAlignment.Stretch;
                    _splitter.Width = 4;
                    _splitter.Height = double.NaN;
                }
                else
                {
                    _primaryColumn.Width = new GridLength(1, GridUnitType.Star);
                    _splitterColumn.Width = new GridLength(0);
                    _secondaryColumn.Width = new GridLength(0);

                    if (isEnabled)
                    {
                        _primaryRow.Height = new GridLength(ratio, GridUnitType.Star);
                        _splitterRow.Height = GridLength.Auto;
                        _secondaryRow.Height = new GridLength(complementary, GridUnitType.Star);
                    }
                    else
                    {
                        _primaryRow.Height = new GridLength(1, GridUnitType.Star);
                        _splitterRow.Height = new GridLength(0);
                        _secondaryRow.Height = new GridLength(0);
                    }

                    Grid.SetColumn(_primaryEditor, 0);
                    Grid.SetColumnSpan(_primaryEditor, 3);
                    Grid.SetRow(_primaryEditor, 0);
                    Grid.SetRowSpan(_primaryEditor, 1);

                    Grid.SetColumn(_runtimeEditor, 0);
                    Grid.SetColumnSpan(_runtimeEditor, 3);
                    Grid.SetRow(_runtimeEditor, 2);
                    Grid.SetRowSpan(_runtimeEditor, 1);

                    Grid.SetColumn(_splitter, 0);
                    Grid.SetColumnSpan(_splitter, 3);
                    Grid.SetRow(_splitter, 1);
                    Grid.SetRowSpan(_splitter, 1);

                    _splitter.ResizeDirection = GridResizeDirection.Rows;
                    _splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                    _splitter.VerticalAlignment = VerticalAlignment.Center;
                    _splitter.Width = double.NaN;
                    _splitter.Height = 4;
                }
            }
            finally
            {
                _isApplyingSplitMetrics = false;
            }
        }

        private void OnComparisonGridLayoutUpdated(object? sender, EventArgs e)
        {
            if (_isApplyingSplitMetrics)
            {
                return;
            }

            if (_viewModel is not { IsSplitViewEnabled: true, RuntimeComparison: not null })
            {
                return;
            }

            if (_primaryEditor is null || _runtimeEditor is null)
            {
                return;
            }

            double ratio;

            if (_viewModel.SplitOrientation == SourcePreviewSplitOrientation.Horizontal)
            {
                var primaryWidth = _primaryEditor.Bounds.Width;
                var runtimeWidth = _runtimeEditor.Bounds.Width;
                var totalWidth = primaryWidth + runtimeWidth;

                if (totalWidth <= 0.1)
                {
                    return;
                }

                ratio = primaryWidth / totalWidth;
            }
            else
            {
                var primaryHeight = _primaryEditor.Bounds.Height;
                var runtimeHeight = _runtimeEditor.Bounds.Height;
                var totalHeight = primaryHeight + runtimeHeight;

                if (totalHeight <= 0.1)
                {
                    return;
                }

                ratio = primaryHeight / totalHeight;
            }

            ratio = Math.Max(0.05, Math.Min(0.95, ratio));

            if (Math.Abs(ratio - _viewModel.SplitRatio) > 0.005)
            {
                _viewModel.SplitRatio = ratio;
            }
        }

        private void EnsureScrollSynchronization()
        {
            if (_primaryEditor is null || _runtimeEditor is null)
            {
                return;
            }

            _scrollCoordinator ??= new SourcePreviewScrollCoordinator();

            _primarySync ??= _scrollCoordinator.Attach(_primaryEditor);
            _runtimeSync ??= _scrollCoordinator.Attach(_runtimeEditor);
        }

        private async void OnOpened(object? sender, EventArgs e)
        {
            if (DataContext is SourcePreviewViewModel vm)
            {
                await vm.LoadAsync().ConfigureAwait(true);
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.DetachFromMutationOwner();
                _viewModel.DetachFromWorkspace();
                _viewModel = null;
            }

            if (_comparisonGrid is not null)
            {
                _comparisonGrid.LayoutUpdated -= OnComparisonGridLayoutUpdated;
            }

            _primarySync?.Dispose();
            _runtimeSync?.Dispose();
            _primarySync = null;
            _runtimeSync = null;
            _scrollCoordinator = null;
        }

        private async void CopySnippetClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is SourcePreviewViewModel vm && vm.HasSnippet)
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(vm.Snippet!).ConfigureAwait(true);
                }
            }
        }

        private async void CopyPathClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is SourcePreviewViewModel vm)
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(vm.SourceInfo.DisplayPath).ConfigureAwait(true);
                }
            }
        }

        private void CloseClick(object? sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
