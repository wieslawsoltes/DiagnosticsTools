using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Avalonia.Diagnostics.Views
{
    public partial class SourcePreviewWindow : Window
    {
        private SourcePreviewViewModel? _viewModel;
        private TextEditor? _snippetTextEditor;
        private HighlightedLineColorizer? _highlightColorizer;
        private string? _currentSnippet;
        private IBrush? _highlightBrush;

        public SourcePreviewWindow()
        {
            InitializeComponent();
            Opened += OnOpened;
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _snippetTextEditor = this.FindControl<TextEditor>("SnippetTextEditor");

            if (_snippetTextEditor is not null)
            {
                _highlightBrush = CreateHighlightBrush();
                _highlightColorizer = new HighlightedLineColorizer
                {
                    HighlightBrush = _highlightBrush
                };
                _snippetTextEditor.TextArea.TextView.LineTransformers.Add(_highlightColorizer);
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

                var newValue = change.GetNewValue<object?>();
                _viewModel = newValue as SourcePreviewViewModel;

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
            if (_snippetTextEditor is null)
            {
                return;
            }

            var snippet = vm.Snippet;
            var highlightedLine = vm.HighlightedLine;
            var snippetStart = vm.SnippetStartLine;

            if (snippet is null)
            {
                Dispatcher.UIThread.Post(ClearPreview, DispatcherPriority.Background);
                return;
            }

            Dispatcher.UIThread.Post(() => ApplySnippetAndHighlight(vm, snippet, highlightedLine, snippetStart), DispatcherPriority.Background);
        }

        private void ApplySnippetAndHighlight(SourcePreviewViewModel vm, string snippet, int? highlightedLine, int snippetStartLine)
        {
            if (_snippetTextEditor is null || _viewModel != vm)
            {
                return;
            }

            var document = _snippetTextEditor.Document;
            var snippetChanged = !string.Equals(_currentSnippet, snippet, StringComparison.Ordinal);
            if (document is null)
            {
                _snippetTextEditor.Document = new TextDocument(snippet);
                _currentSnippet = snippet;
            }
            else if (snippetChanged)
            {
                document.Text = snippet;
                _currentSnippet = snippet;
            }

            UpdateHighlight(highlightedLine, snippetStartLine);
        }

        private void ClearPreview()
        {
            if (_snippetTextEditor is null)
            {
                return;
            }

            _currentSnippet = null;

            if (_snippetTextEditor.Document is { } document)
            {
                document.Text = string.Empty;
            }
            else
            {
                _snippetTextEditor.Document = new TextDocument();
            }

            if (_highlightColorizer is not null)
            {
                _highlightColorizer.LineNumber = null;
                _snippetTextEditor.TextArea.TextView.InvalidateVisual();
            }
        }

        private void UpdateHighlight(int? highlightedLine, int snippetStartLine)
        {
            if (_snippetTextEditor is null || _highlightColorizer is null)
            {
                return;
            }

            var document = _snippetTextEditor.Document;
            if (highlightedLine is null || document is null || document.LineCount == 0)
            {
                if (_highlightColorizer.LineNumber is not null)
                {
                    _highlightColorizer.LineNumber = null;
                    _snippetTextEditor.TextArea.TextView.InvalidateVisual();
                }
                return;
            }

            var targetLine = highlightedLine.Value - snippetStartLine + 1;
            if (targetLine < 1 || targetLine > document.LineCount)
            {
                if (_highlightColorizer.LineNumber is not null)
                {
                    _highlightColorizer.LineNumber = null;
                    _snippetTextEditor.TextArea.TextView.InvalidateVisual();
                }
                return;
            }

            if (_highlightColorizer.LineNumber != targetLine)
            {
                _highlightColorizer.LineNumber = targetLine;
            }

            _snippetTextEditor.ScrollTo(targetLine, 0);
            _snippetTextEditor.TextArea.Caret.Position = new TextViewPosition(targetLine, 0);
            _snippetTextEditor.TextArea.TextView.InvalidateVisual();
        }

        private IBrush CreateHighlightBrush()
        {
            if (Application.Current?.TryFindResource("ThemeAccentBrush", out var resource) == true &&
                resource is ISolidColorBrush accent)
            {
                var accentColor = accent.Color;
                var highlightColor = Color.FromArgb(0x60, accentColor.R, accentColor.G, accentColor.B);
                return new SolidColorBrush(highlightColor);
            }

            return new SolidColorBrush(Color.FromArgb(0x60, 0x56, 0x9C, 0xD6));
        }

        private sealed class HighlightedLineColorizer : DocumentColorizingTransformer
        {
            public int? LineNumber { get; set; }
            public IBrush? HighlightBrush { get; init; }

            protected override void ColorizeLine(DocumentLine line)
            {
                if (HighlightBrush is null || LineNumber is null)
                {
                    return;
                }

                if (line.LineNumber != LineNumber.Value)
                {
                    return;
                }

                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetBackgroundBrush(HighlightBrush);
                });
            }
        }
    }
}
