using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.Views
{
    public partial class SourcePreviewWindow : Window
    {
        private SourcePreviewViewModel? _viewModel;
        private TextBox? _snippetTextBox;

        public SourcePreviewWindow()
        {
            InitializeComponent();
            Opened += OnOpened;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _snippetTextBox = this.FindControl<TextBox>("SnippetTextBox");
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

        private async void OnOpened(object? sender, EventArgs e)
        {
            if (DataContext is SourcePreviewViewModel vm)
            {
                await vm.LoadAsync().ConfigureAwait(true);
                TryUpdateHighlight(vm);
            }
        }

        private async void OpenClick(object? sender, RoutedEventArgs e)
        {
            if (DataContext is SourcePreviewViewModel vm)
            {
                await vm.OpenSourceAsync().ConfigureAwait(true);
            }
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

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == DataContextProperty)
            {
                if (_viewModel is not null)
                {
                    _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                }

                _viewModel = change.GetNewValue<SourcePreviewViewModel?>();

                if (_viewModel is not null)
                {
                    _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                    TryUpdateHighlight(_viewModel);
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is SourcePreviewViewModel vm)
            {
                if (e.PropertyName is nameof(SourcePreviewViewModel.Snippet)
                    or nameof(SourcePreviewViewModel.HighlightedLine)
                    or nameof(SourcePreviewViewModel.SnippetStartLine))
                {
                    TryUpdateHighlight(vm);
                }
            }
        }

        private void TryUpdateHighlight(SourcePreviewViewModel vm)
        {
            if (_snippetTextBox is null)
            {
                return;
            }

            if (vm.Snippet is null || vm.HighlightedLine is null)
            {
                Dispatcher.UIThread.Post(ClearSelection, DispatcherPriority.Background);
                return;
            }

            var lineIndex = vm.HighlightedLine.Value - vm.SnippetStartLine;
            if (lineIndex < 0)
            {
                Dispatcher.UIThread.Post(ClearSelection, DispatcherPriority.Background);
                return;
            }

            if (!TryGetLineRange(vm.Snippet, lineIndex, out var start, out var length))
            {
                Dispatcher.UIThread.Post(ClearSelection, DispatcherPriority.Background);
                return;
            }

            Dispatcher.UIThread.Post(() => ApplySelection(vm, start, length), DispatcherPriority.Background);
        }

        private void ApplySelection(SourcePreviewViewModel vm, int start, int length)
        {
            if (_snippetTextBox is null || _viewModel != vm || vm.Snippet is null)
            {
                return;
            }

            var snippetLength = vm.Snippet.Length;
            var safeStart = Clamp(start, 0, snippetLength);
            var safeLength = Clamp(length, 0, snippetLength - safeStart);

            _snippetTextBox.SelectionStart = safeStart;
            _snippetTextBox.SelectionEnd = safeStart + safeLength;
            _snippetTextBox.CaretIndex = _snippetTextBox.SelectionEnd;
        }

        private void ClearSelection()
        {
            if (_snippetTextBox is null)
            {
                return;
            }

            _snippetTextBox.SelectionStart = 0;
            _snippetTextBox.SelectionEnd = 0;
            _snippetTextBox.CaretIndex = 0;
        }

    private static bool TryGetLineRange(string text, int lineIndex, out int start, out int length)
        {
            start = 0;
            length = 0;

            if (lineIndex < 0)
            {
                return false;
            }

            var currentIndex = 0;
            var currentLine = 0;

            while (currentLine < lineIndex)
            {
                var nextBreak = text.IndexOf('\n', currentIndex);
                if (nextBreak < 0)
                {
                    return false;
                }

                currentIndex = nextBreak + 1;
                currentLine++;
            }

            if (currentIndex >= text.Length)
            {
                return false;
            }

            var lineEnd = text.IndexOf('\n', currentIndex);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            start = currentIndex;
            length = lineEnd - currentIndex;
            return true;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
        }
    }
}
