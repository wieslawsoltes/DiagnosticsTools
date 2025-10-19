using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Windows.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;

namespace Avalonia.Diagnostics.Controls
{
    public partial class SourcePreviewEditor : UserControl
    {
        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
        private static readonly IReadOnlyList<SourcePreviewNavigationTarget> EmptyTargets = Array.Empty<SourcePreviewNavigationTarget>();

        public event EventHandler<SourcePreviewNavigationInspectEventArgs>? InspectNavigation;

        public event EventHandler<SourcePreviewNavigationRequestEventArgs>? NavigationRequested;

        public event EventHandler<SourcePreviewNavigationStateChangedEventArgs>? NavigationStateChanged;

        public event EventHandler<SourcePreviewScrollChangedEventArgs>? ScrollChanged;

        private readonly XamlAstFoldingBuilder _foldingBuilder = new();
        private EventHandler? _themeChangedHandler;
        private SourcePreviewViewModel? _viewModel;
        private NavigationAttributes _navigationAttributes;
        private TextEditor? _textEditor;
        private HighlightedLineColorizer? _lineColorizer;
        private HighlightedSegmentColorizer? _segmentColorizer;
        private XamlAstSelectionAdorner? _selectionAdorner;
        private FoldingManager? _foldingManager;
        private string? _currentSnippet;
        private IBrush? _highlightBrush;
        private Cursor? _previousCursor;
        private bool _isApplyingScroll;
        private SourcePreviewScrollState _lastScrollState = SourcePreviewScrollState.Empty;
        private bool _suppressSelectionSync;
        private XamlAstNodeDescriptor? _currentDescriptor;

        public SourcePreviewEditor()
        {
            InitializeComponent();
            _textEditor = this.FindControl<TextEditor>("SnippetTextEditor");
            InitializeEditor();

            _themeChangedHandler = OnThemeVariantChanged;
            ActualThemeVariantChanged += _themeChangedHandler;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = DataContext as SourcePreviewViewModel;

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                TryUpdateHighlight(_viewModel);
            }
            else
            {
                ClearPreview();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel = null;
            }

            if (_themeChangedHandler is not null)
            {
                ActualThemeVariantChanged -= _themeChangedHandler;
                _themeChangedHandler = null;
            }

            if (_textEditor is not null)
            {
                var textView = _textEditor.TextArea.TextView;
                textView.ScrollOffsetChanged -= OnTextViewScrollChanged;
                textView.VisualLinesChanged -= OnTextViewVisualLinesChanged;

                _textEditor.PointerMoved -= OnEditorPointerMoved;
                _textEditor.PointerExited -= OnEditorPointerExited;
                _textEditor.PointerPressed -= OnEditorPointerPressed;
                _textEditor.TextArea.KeyDown -= OnEditorKeyDown;
                _textEditor.TextArea.KeyUp -= OnEditorKeyUp;
                _textEditor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
                _textEditor.TextArea.SelectionChanged -= OnSelectionChanged;
            }

            if (_foldingManager is not null && _textEditor is not null)
            {
                FoldingManager.Uninstall(_foldingManager);
                _foldingManager = null;
            }

            UpdateInteractiveState(default);
        }

        private void InitializeEditor()
        {
            if (_textEditor is null)
            {
                return;
            }

            _segmentColorizer = new HighlightedSegmentColorizer();
            _lineColorizer = new HighlightedLineColorizer();
            _selectionAdorner = new XamlAstSelectionAdorner();

            _textEditor.TextArea.TextView.LineTransformers.Add(_segmentColorizer);
            _textEditor.TextArea.TextView.LineTransformers.Add(_lineColorizer);
            _textEditor.TextArea.TextView.BackgroundRenderers.Add(_selectionAdorner);

            _foldingManager = FoldingManager.Install(_textEditor.TextArea);

            var highlighting = HighlightingManager.Instance.GetDefinitionByExtension(".xaml") ??
                               HighlightingManager.Instance.GetDefinition("XML");
            if (highlighting is not null)
            {
                _textEditor.SyntaxHighlighting = highlighting;
            }

            ApplyTheme();

            _textEditor.PointerMoved += OnEditorPointerMoved;
            _textEditor.PointerExited += OnEditorPointerExited;
            _textEditor.PointerPressed += OnEditorPointerPressed;
            _textEditor.TextArea.KeyDown += OnEditorKeyDown;
            _textEditor.TextArea.KeyUp += OnEditorKeyUp;
            _textEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
            _textEditor.TextArea.SelectionChanged += OnSelectionChanged;

            var textView = _textEditor.TextArea.TextView;
            textView.ScrollOffsetChanged += OnTextViewScrollChanged;
            textView.VisualLinesChanged += OnTextViewVisualLinesChanged;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not SourcePreviewViewModel vm)
            {
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(SourcePreviewViewModel.Snippet):
                case nameof(SourcePreviewViewModel.SnippetStartLine):
                case nameof(SourcePreviewViewModel.HighlightedStartLine):
                case nameof(SourcePreviewViewModel.HighlightedEndLine):
                case nameof(SourcePreviewViewModel.HighlightSpanStart):
                case nameof(SourcePreviewViewModel.HighlightSpanLength):
                    TryUpdateHighlight(vm);
                    break;
            }
        }

        private void TryUpdateHighlight(SourcePreviewViewModel vm)
        {
            if (_textEditor is null)
            {
                return;
            }

            var snippet = vm.Snippet;
            var highlightedStartLine = vm.HighlightedStartLine;
            var highlightedEndLine = vm.HighlightedEndLine;
            var highlightSpanStart = vm.HighlightSpanStart;
            var highlightSpanLength = vm.HighlightSpanLength;
            var snippetStart = vm.SnippetStartLine;
            var document = vm.AstSelection?.Document;

            if (snippet is null)
            {
                Dispatcher.UIThread.Post(ClearPreview, DispatcherPriority.Background);
                return;
            }

            Dispatcher.UIThread.Post(
                () => ApplySnippet(vm, snippet, snippetStart, highlightedStartLine, highlightedEndLine, highlightSpanStart, highlightSpanLength, document),
                DispatcherPriority.Background);
        }

        private void ApplySnippet(
            SourcePreviewViewModel vm,
            string snippet,
            int snippetStartLine,
            int? highlightedStartLine,
            int? highlightedEndLine,
            int? highlightSpanStart,
            int? highlightSpanLength,
            XamlAstDocument? document)
        {
            if (_textEditor is null || !ReferenceEquals(vm, _viewModel))
            {
                return;
            }

            var textDocument = _textEditor.Document;
            var changed = !string.Equals(_currentSnippet, snippet, StringComparison.Ordinal);
            if (textDocument is null)
            {
                _textEditor.Document = new TextDocument(snippet);
                _currentSnippet = snippet;
                _textEditor.TextArea.ClearSelection();
                _textEditor.TextArea.Caret.Offset = 0;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_textEditor is null)
                    {
                        return;
                    }

                    _textEditor.TextArea.ClearSelection();
                    _textEditor.TextArea.Caret.Offset = 0;
                }, DispatcherPriority.Background);
            }
            else if (changed)
            {
                textDocument.Text = snippet;
                _currentSnippet = snippet;
                _textEditor.TextArea.ClearSelection();
                _textEditor.TextArea.Caret.Offset = 0;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_textEditor is null)
                    {
                        return;
                    }

                    _textEditor.TextArea.ClearSelection();
                    _textEditor.TextArea.Caret.Offset = 0;
                }, DispatcherPriority.Background);
            }

            UpdateFoldings(document, changed || document is null);
            UpdateHighlight(snippetStartLine, highlightedStartLine, highlightedEndLine, highlightSpanStart, highlightSpanLength);
        }

        private void ClearPreview()
        {
            if (_textEditor is null)
            {
                return;
            }

            _currentSnippet = null;
            _currentDescriptor = null;
            _lastScrollState = SourcePreviewScrollState.Empty;

            if (_textEditor.Document is { } document)
            {
                document.Text = string.Empty;
            }
            else
            {
                _textEditor.Document = new TextDocument();
            }

            if (_lineColorizer is not null)
            {
                _lineColorizer.StartLine = null;
                _lineColorizer.EndLine = null;
            }

            if (_segmentColorizer is not null)
            {
                _segmentColorizer.SegmentStart = null;
                _segmentColorizer.SegmentLength = null;
            }

            if (_selectionAdorner is not null)
            {
                _selectionAdorner.SegmentStart = null;
                _selectionAdorner.SegmentLength = null;
                _selectionAdorner.ShowUnderline = false;
            }

            UpdateFoldings(null, true);

            _textEditor.TextArea.TextView.InvalidateVisual();
            UpdateInteractiveState(default);
        }

        private void UpdateFoldings(XamlAstDocument? document, bool force)
        {
            if (_textEditor is null)
            {
                return;
            }

            _foldingManager ??= FoldingManager.Install(_textEditor.TextArea);

            if (_foldingManager is null)
            {
                return;
            }

            if (document is null)
            {
                if (force)
                {
                    _foldingManager.UpdateFoldings(Array.Empty<NewFolding>(), -1);
                }

                return;
            }

            var foldings = _foldingBuilder.BuildFoldings(document);
            _foldingManager.UpdateFoldings(foldings, -1);
        }

        private void UpdateHighlight(
            int snippetStartLine,
            int? highlightedStartLine,
            int? highlightedEndLine,
            int? highlightSpanStart,
            int? highlightSpanLength)
        {
            if (_textEditor is null || _lineColorizer is null || _segmentColorizer is null)
            {
                return;
            }

            _suppressSelectionSync = true;
            try
            {
                _currentDescriptor = _viewModel?.AstSelection?.Node;

                var document = _textEditor.Document;
                var invalidate = false;

                // Avoid AvaloniaEdit retaining the previous full-document selection when new content is loaded.
                _textEditor.TextArea.ClearSelection();

                if (document is null || document.LineCount == 0 || highlightedStartLine is null || highlightedEndLine is null)
                {
                    if (_lineColorizer.StartLine is not null || _lineColorizer.EndLine is not null)
                    {
                        _lineColorizer.StartLine = null;
                        _lineColorizer.EndLine = null;
                        invalidate = true;
                    }
                }
                else
                {
                    var startLine = highlightedStartLine.Value - snippetStartLine + 1;
                    var endLine = highlightedEndLine.Value - snippetStartLine + 1;

                    if (startLine < 1 || startLine > document.LineCount)
                    {
                        if (_lineColorizer.StartLine is not null || _lineColorizer.EndLine is not null)
                        {
                            _lineColorizer.StartLine = null;
                            _lineColorizer.EndLine = null;
                            invalidate = true;
                        }
                    }
                    else
                    {
                        endLine = Math.Max(startLine, Math.Min(endLine, document.LineCount));
                        if (_lineColorizer.StartLine != startLine || _lineColorizer.EndLine != endLine)
                        {
                            _lineColorizer.StartLine = startLine;
                            _lineColorizer.EndLine = endLine;
                            invalidate = true;
                        }

                        var caretLine = startLine;
                        var caretColumn = 1;
                        var caretOffset = 0;

                        if (TryGetSpanAnchor(document, highlightSpanStart, out var spanOffset, out var spanLocation))
                        {
                            caretOffset = spanOffset;
                            caretLine = spanLocation.Line;
                            caretColumn = Math.Max(1, spanLocation.Column);
                        }
                        else if (document.LineCount >= startLine)
                        {
                            var line = document.GetLineByNumber(startLine);
                            var lineText = document.GetText(line.Offset, line.Length);
                            var firstContent = lineText.TakeWhile(char.IsWhiteSpace).Count() + 1;
                            caretColumn = Math.Min(firstContent, lineText.Length + 1);
                            caretOffset = document.GetOffset(new TextLocation(caretLine, caretColumn));
                        }
                        else
                        {
                            var fallbackOffset = highlightSpanStart.HasValue
                                ? Math.Max(0, Math.Min(highlightSpanStart.Value, document.TextLength))
                                : 0;
                            caretOffset = fallbackOffset;
                        }

                        _textEditor.ScrollTo(caretLine, caretColumn);
                        _textEditor.TextArea.Caret.Offset = Math.Min(caretOffset, document.TextLength);
                        _textEditor.TextArea.ClearSelection();
                    }
                }

                var hasSpan = false;
                var startOffset = 0;
                var spanLength = 0;

                if (document is not null && highlightSpanStart is not null && highlightSpanLength is not null && highlightSpanLength.Value > 0)
                {
                    startOffset = Math.Max(0, highlightSpanStart.Value);
                    var maxLength = Math.Max(0, document.TextLength - startOffset);
                    spanLength = Math.Min(highlightSpanLength.Value, maxLength);
                    hasSpan = spanLength > 0;
                }

                if (!hasSpan)
                {
                    if (_segmentColorizer.SegmentStart is not null || _segmentColorizer.SegmentLength is not null)
                    {
                        _segmentColorizer.SegmentStart = null;
                        _segmentColorizer.SegmentLength = null;
                        invalidate = true;
                    }
                }
                else if (_segmentColorizer.SegmentStart != startOffset || _segmentColorizer.SegmentLength != spanLength)
                {
                    _segmentColorizer.SegmentStart = startOffset;
                    _segmentColorizer.SegmentLength = spanLength;
                    invalidate = true;
                }

                if (_selectionAdorner is not null)
                {
                    if (!hasSpan)
                    {
                        if (_selectionAdorner.SegmentStart is not null || _selectionAdorner.SegmentLength is not null)
                        {
                            _selectionAdorner.SegmentStart = null;
                            _selectionAdorner.SegmentLength = null;
                            invalidate = true;
                        }
                    }
                    else if (_selectionAdorner.SegmentStart != startOffset || _selectionAdorner.SegmentLength != spanLength)
                    {
                        _selectionAdorner.SegmentStart = startOffset;
                        _selectionAdorner.SegmentLength = spanLength;
                        invalidate = true;
                    }
                }

                if (invalidate)
                {
                    _textEditor.TextArea.TextView.InvalidateVisual();
                }
                else
                {
                    _textEditor.TextArea.TextView.InvalidateVisual();
                }
            }
            finally
            {
                _suppressSelectionSync = false;
            }
        }

        private static bool TryGetSpanAnchor(TextDocument? document, int? highlightSpanStart, out int offset, out TextLocation location)
        {
            offset = 0;
            location = default;
            if (document is null || highlightSpanStart is null)
            {
                return false;
            }

            var startOffset = Math.Max(0, highlightSpanStart.Value);
            if (document.TextLength == 0)
            {
                return false;
            }

            int locationOffset;
            if (startOffset >= document.TextLength)
            {
                locationOffset = Math.Max(0, document.TextLength - 1);
            }
            else
            {
                locationOffset = startOffset;
            }

            location = document.GetLocation(locationOffset);
            offset = Math.Min(startOffset, document.TextLength);

            return true;
        }

        private void OnCaretPositionChanged(object? sender, EventArgs e)
        {
            SynchronizeSelectionFromEditor();
        }

        private void OnSelectionChanged(object? sender, EventArgs e)
        {
            SynchronizeSelectionFromEditor();
        }

        private void SynchronizeSelectionFromEditor()
        {
            if (_suppressSelectionSync)
            {
                return;
            }

            var viewModel = _viewModel;
            var textEditor = _textEditor;

            if (viewModel is null || textEditor is null)
            {
                return;
            }

            var selection = viewModel.AstSelection;
            if (selection is null || selection.Document is null)
            {
                return;
            }

            var nodes = selection.DocumentNodes;
            if (nodes is null || nodes.Count == 0)
            {
                return;
            }

            var document = textEditor.Document;
            if (document is null || document.TextLength == 0)
            {
                return;
            }

            var textArea = textEditor.TextArea;
            var offset = textArea.Caret.Offset;

            if (!textArea.Selection.IsEmpty && textArea.Selection.SurroundingSegment is { } segment)
            {
                offset = Math.Min(Math.Max(segment.Offset, 0), document.TextLength);
            }

            if (offset >= document.TextLength && document.TextLength > 0)
            {
                offset = document.TextLength - 1;
            }

            if (offset < 0)
            {
                offset = 0;
            }

            var descriptor = FindDescriptorAtOffset(selection, offset);

            if (descriptor is null && offset > 0)
            {
                descriptor = FindDescriptorAtOffset(selection, offset - 1);
            }

            if (_currentDescriptor?.Id == descriptor?.Id)
            {
                return;
            }

            _currentDescriptor = descriptor;
            viewModel.NotifyEditorSelectionChanged(descriptor);
        }

        private static XamlAstNodeDescriptor? FindDescriptorAtOffset(XamlAstSelection selection, int offset)
        {
            var nodes = selection.DocumentNodes;
            if (nodes is null || nodes.Count == 0)
            {
                return null;
            }

            XamlAstNodeDescriptor? best = null;

            foreach (var node in nodes)
            {
                var span = node.Span;
                if (offset < span.Start || offset >= span.End)
                {
                    continue;
                }

                if (span.Length <= 0)
                {
                    continue;
                }

                if (best is null || span.Length < best.Span.Length)
                {
                    best = node;
                }
            }

            return best;
        }

        private void OnThemeVariantChanged(object? sender, EventArgs e)
        {
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            if (_textEditor is null)
            {
                return;
            }

            _highlightBrush = CreateHighlightBrush();

            if (_segmentColorizer is not null)
            {
                _segmentColorizer.HighlightBrush = _highlightBrush;
            }

            if (_lineColorizer is not null)
            {
                _lineColorizer.HighlightBrush = _highlightBrush;
            }

            if (_selectionAdorner is not null)
            {
                _selectionAdorner.BorderBrush = CreateAdornerBrush();
            }
        }

        private IBrush CreateHighlightBrush()
        {
            if (Avalonia.Application.Current?.TryFindResource("ThemeAccentBrush", out var resource) == true &&
                resource is ISolidColorBrush accent)
            {
                var accentColor = accent.Color;
                var highlightColor = Color.FromArgb(0x60, accentColor.R, accentColor.G, accentColor.B);
                return new SolidColorBrush(highlightColor);
            }

            return new SolidColorBrush(Color.FromArgb(0x60, 0x56, 0x9C, 0xD6));
        }

        private IBrush CreateAdornerBrush()
        {
            if (Avalonia.Application.Current?.TryFindResource("ThemeAccentBrush", out var resource) == true &&
                resource is ISolidColorBrush accent)
            {
                var accentColor = accent.Color;
                var stroke = Color.FromArgb(0xA0, accentColor.R, accentColor.G, accentColor.B);
                return new SolidColorBrush(stroke);
            }

            return new SolidColorBrush(Color.FromArgb(0xA0, 0x56, 0x9C, 0xD6));
        }

        private NavigationAttributes ComputeNavigationAttributes(KeyModifiers modifiers)
        {
            var viewModel = _viewModel;
            if (viewModel is null)
            {
                return default;
            }

            var hasCtrl = modifiers.HasFlag(KeyModifiers.Control);
            var defaultTargets = viewModel.NavigationTargets.Count == 0
                ? EmptyTargets
                : viewModel.NavigationTargets.ToArray();

            var options = new SourcePreviewNavigationOptions(defaultTargets)
            {
                IsClickable = hasCtrl && (viewModel.OpenSourceCommand?.CanExecute(null) == true || defaultTargets.Count > 0),
                ShowUnderline = hasCtrl,
                Cursor = hasCtrl ? HandCursor : null,
                Context = viewModel.AstSelection?.Node
            };

            if (InspectNavigation is not null)
            {
                var inspectArgs = new SourcePreviewNavigationInspectEventArgs(viewModel, modifiers, options);
                InspectNavigation.Invoke(this, inspectArgs);
            }

            var snapshot = options.GetSnapshot();
            return new NavigationAttributes(options.IsClickable, options.ShowUnderline, options.Cursor, options.Context, snapshot.Count == 0 ? EmptyTargets : snapshot);
        }

        private bool TryHandleKeyNavigation(Key key, KeyModifiers modifiers)
        {
            var viewModel = _viewModel;
            if (viewModel is null)
            {
                return false;
            }

            switch (key)
            {
                case Key.F12:
                {
                    var attributes = ComputeNavigationAttributes(modifiers);
                    return InvokeNavigation(SourcePreviewNavigationTrigger.KeyboardF12, modifiers, attributes, allowFallback: true);
                }
                case Key.Enter when modifiers.HasFlag(KeyModifiers.Control):
                {
                    var attributes = ComputeNavigationAttributes(modifiers);
                    return InvokeNavigation(SourcePreviewNavigationTrigger.KeyboardControlEnter, modifiers, attributes, allowFallback: true);
                }
                default:
                    return false;
            }
        }

        private bool InvokeNavigation(SourcePreviewNavigationTrigger trigger, KeyModifiers modifiers, NavigationAttributes attributes, bool allowFallback)
        {
            var viewModel = _viewModel;
            if (viewModel is null)
            {
                return false;
            }

            var targets = attributes.Targets;
            var args = new SourcePreviewNavigationRequestEventArgs(viewModel, trigger, modifiers, attributes.Context, targets);
            NavigationRequested?.Invoke(this, args);

            if (args.Handled)
            {
                return true;
            }

            if (args.SelectedTarget is { } selectedTarget)
            {
                return ExecuteNavigationTarget(selectedTarget);
            }

            if (targets.Count == 1 && ExecuteNavigationTarget(targets[0]))
            {
                return true;
            }

            if (allowFallback && viewModel.OpenSourceCommand?.CanExecute(null) == true)
            {
                viewModel.OpenSourceCommand.Execute(null);
                return true;
            }

            if (attributes.IsClickable && viewModel.OpenSourceCommand?.CanExecute(null) == true)
            {
                viewModel.OpenSourceCommand.Execute(null);
                return true;
            }

            return false;
        }

        private static bool ExecuteNavigationTarget(SourcePreviewNavigationTarget target)
        {
            if (target.Command is null)
            {
                return false;
            }

            if (!target.Command.CanExecute(target.CommandParameter))
            {
                return false;
            }

            target.Command.Execute(target.CommandParameter);
            return true;
        }

        private void OnEditorPointerMoved(object? sender, PointerEventArgs e)
        {
            var newFacets = ComputeNavigationAttributes(e.KeyModifiers);
            UpdateInteractiveState(newFacets);
        }

        private void OnEditorPointerExited(object? sender, PointerEventArgs e)
        {
            UpdateInteractiveState(default);
        }

        private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_navigationAttributes.IsClickable || _navigationAttributes.Targets.Count > 0)
            {
                var point = e.GetCurrentPoint(_textEditor);
                if (point.Properties.IsLeftButtonPressed && InvokeNavigation(SourcePreviewNavigationTrigger.PointerPrimary, e.KeyModifiers, _navigationAttributes, allowFallback: true))
                {
                    e.Handled = true;
                }
            }
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (TryHandleKeyNavigation(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
            }
            else
            {
                UpdateInteractiveState(ComputeNavigationAttributes(e.KeyModifiers));
            }
        }

        private void OnEditorKeyUp(object? sender, KeyEventArgs e)
        {
            UpdateInteractiveState(ComputeNavigationAttributes(e.KeyModifiers));
        }

        private void UpdateInteractiveState(NavigationAttributes attributes)
        {
            if (_navigationAttributes.Equals(attributes))
            {
                return;
            }

            _navigationAttributes = attributes;

            if (_textEditor is not null)
            {
                if (_navigationAttributes.Cursor is not null)
                {
                    _previousCursor ??= _textEditor.Cursor;
                    _textEditor.Cursor = _navigationAttributes.Cursor;
                }
                else if (_previousCursor is not null)
                {
                    _textEditor.Cursor = _previousCursor;
                    _previousCursor = null;
                }
            }

            if (_selectionAdorner is not null)
            {
                if (_selectionAdorner.ShowUnderline != _navigationAttributes.ShowUnderline)
                {
                    _selectionAdorner.ShowUnderline = _navigationAttributes.ShowUnderline;
                    _textEditor?.TextArea.TextView.InvalidateVisual();
                }
            }

            NavigationStateChanged?.Invoke(this, new SourcePreviewNavigationStateChangedEventArgs(
                new SourcePreviewNavigationState(
                    _navigationAttributes.IsClickable,
                    _navigationAttributes.ShowUnderline,
                    _navigationAttributes.Cursor,
                    _navigationAttributes.Targets,
                    _navigationAttributes.Context)));
        }

        public void ApplyScrollState(SourcePreviewScrollState state)
        {
            if (_textEditor is null || !state.IsValid)
            {
                return;
            }

            if (state.Equals(_lastScrollState))
            {
                return;
            }

            _isApplyingScroll = true;
            try
            {
                if (state.FirstVisibleLine is int firstLine && firstLine > 0)
                {
                    _textEditor.ScrollTo(firstLine, 0);
                }
                else
                {
                    _textEditor.ScrollToVerticalOffset(state.VerticalOffset);
                }

                if (!double.IsNaN(state.HorizontalOffset))
                {
                    _textEditor.ScrollToHorizontalOffset(state.HorizontalOffset);
                }
            }
            finally
            {
                _isApplyingScroll = false;
                _lastScrollState = CaptureScrollState();
            }
        }

        public SourcePreviewScrollState GetCurrentScrollState()
        {
            var state = CaptureScrollState();

            if (state.IsValid)
            {
                _lastScrollState = state;
            }

            return state;
        }

        private void OnTextViewScrollChanged(object? sender, EventArgs e)
        {
            EmitScrollState();
        }

        private void OnTextViewVisualLinesChanged(object? sender, EventArgs e)
        {
            EmitScrollState();
        }

        private void EmitScrollState()
        {
            if (_textEditor is null || _isApplyingScroll)
            {
                return;
            }

            var state = CaptureScrollState();
            if (!state.IsValid || state.Equals(_lastScrollState))
            {
                return;
            }

            _lastScrollState = state;
            ScrollChanged?.Invoke(this, new SourcePreviewScrollChangedEventArgs(state));
        }

        private SourcePreviewScrollState CaptureScrollState()
        {
            if (_textEditor is null)
            {
                return SourcePreviewScrollState.Empty;
            }

            var textView = _textEditor.TextArea.TextView;
            var document = _textEditor.Document;
            textView.EnsureVisualLines();

            int? firstLine = null;
            int? firstColumn = null;

            var visualLines = textView.VisualLines;
            if (visualLines is { Count: > 0 })
            {
                var visualLine = visualLines[0];
                firstLine = visualLine.FirstDocumentLine.LineNumber;

                if (document is not null)
                {
                    var startOffset = visualLine.FirstDocumentLine.Offset;
                    var location = document.GetLocation(startOffset);
                    firstColumn = location.Column;
                }
            }

            var offset = textView.ScrollOffset;
            return new SourcePreviewScrollState(offset.X, offset.Y, firstLine, firstColumn);
        }

        private sealed class HighlightedSegmentColorizer : DocumentColorizingTransformer
        {
            public int? SegmentStart { get; set; }
            public int? SegmentLength { get; set; }
            public IBrush? HighlightBrush { get; set; }

            protected override void ColorizeLine(DocumentLine line)
            {
                if (HighlightBrush is null || SegmentStart is null || SegmentLength is null || SegmentLength <= 0)
                {
                    return;
                }

                var start = SegmentStart.Value;
                var end = start + SegmentLength.Value;
                var lineStart = line.Offset;
                var lineEnd = line.EndOffset;

                var segmentStart = Math.Max(start, lineStart);
                var segmentEnd = Math.Min(end, lineEnd);

                if (segmentStart >= segmentEnd)
                {
                    return;
                }

                ChangeLinePart(segmentStart, segmentEnd, element =>
                {
                    element.TextRunProperties.SetBackgroundBrush(HighlightBrush);
                });
            }
        }

        private sealed class HighlightedLineColorizer : DocumentColorizingTransformer
        {
            public int? StartLine { get; set; }
            public int? EndLine { get; set; }
            public IBrush? HighlightBrush { get; set; }

            protected override void ColorizeLine(DocumentLine line)
            {
                if (HighlightBrush is null || StartLine is null || EndLine is null)
                {
                    return;
                }

                if (line.LineNumber < StartLine.Value || line.LineNumber > EndLine.Value)
                {
                    return;
                }

                ChangeLinePart(line.Offset, line.EndOffset, element =>
                {
                    element.TextRunProperties.SetBackgroundBrush(HighlightBrush);
                });
            }
        }

        private sealed class XamlAstSelectionAdorner : IBackgroundRenderer
        {
            private Pen? _pen;

            public int? SegmentStart { get; set; }
            public int? SegmentLength { get; set; }
            public bool ShowUnderline { get; set; }

            private IBrush? _borderBrush;
            public IBrush? BorderBrush
            {
                get => _borderBrush;
                set
                {
                    _borderBrush = value;
                    _pen = _borderBrush is null ? null : new Pen(_borderBrush, 1.0);
                }
            }

            public KnownLayer Layer => KnownLayer.Selection;

            public void Draw(TextView textView, DrawingContext drawingContext)
            {
                if (_pen is null || SegmentStart is null || SegmentLength is null || SegmentLength <= 0)
                {
                    return;
                }

                var document = textView.Document;
                if (document is null)
                {
                    return;
                }

                var startOffset = SegmentStart.Value;
                var endOffset = Math.Min(document.TextLength, startOffset + SegmentLength.Value);
                if (endOffset <= startOffset)
                {
                    return;
                }

                textView.EnsureVisualLines();

                var line = document.GetLineByOffset(startOffset);
                while (line is not null && line.Offset < endOffset)
                {
                    var lineStart = Math.Max(startOffset, line.Offset);
                    var lineEnd = Math.Min(endOffset, line.EndOffset);

                    if (lineEnd > lineStart)
                {
                    var startLocation = document.GetLocation(lineStart);
                    var endLocation = document.GetLocation(Math.Max(lineEnd - 1, lineStart));

                    var topPoint = textView.GetVisualPosition(new TextViewPosition(startLocation.Line, startLocation.Column), VisualYPosition.LineTop);
                    var bottomPoint = textView.GetVisualPosition(new TextViewPosition(endLocation.Line, endLocation.Column + 1), VisualYPosition.LineBottom);

                    if (ShowUnderline)
                    {
                        var underlineY = bottomPoint.Y;
                        var underlineLeft = topPoint.X;
                        var underlineRight = bottomPoint.X;
                        drawingContext.DrawLine(_pen, new Point(underlineLeft, underlineY), new Point(underlineRight, underlineY));
                    }
                    else
                    {
                        var rect = new Rect(topPoint, bottomPoint);
                        var inflated = new Rect(rect.X, rect.Y - 1, rect.Width, rect.Height + 2);
                        drawingContext.DrawRectangle(null, _pen, inflated);
                    }
                }

                if (line.EndOffset >= endOffset)
                {
                    break;
                    }

                    line = line.NextLine;
                }
            }
        }

        private readonly struct NavigationAttributes : IEquatable<NavigationAttributes>
        {
            private readonly IReadOnlyList<SourcePreviewNavigationTarget>? _targets;

            public NavigationAttributes(bool isClickable, bool showUnderline, Cursor? cursor, object? context, IReadOnlyList<SourcePreviewNavigationTarget>? targets)
            {
                IsClickable = isClickable;
                ShowUnderline = showUnderline;
                Cursor = cursor;
                Context = context;
                _targets = targets;
            }

            public bool IsClickable { get; }

            public bool ShowUnderline { get; }

            public Cursor? Cursor { get; }

            public object? Context { get; }

            public IReadOnlyList<SourcePreviewNavigationTarget> Targets => _targets ?? Array.Empty<SourcePreviewNavigationTarget>();

            public bool Equals(NavigationAttributes other) =>
                IsClickable == other.IsClickable &&
                ShowUnderline == other.ShowUnderline &&
                Equals(Cursor, other.Cursor) &&
                Equals(Context, other.Context) &&
                TargetsEqual(_targets, other._targets);

            public override bool Equals(object? obj) => obj is NavigationAttributes other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = (hash * 31) + IsClickable.GetHashCode();
                    hash = (hash * 31) + ShowUnderline.GetHashCode();
                    hash = (hash * 31) + (Cursor?.GetHashCode() ?? 0);
                    hash = (hash * 31) + (Context?.GetHashCode() ?? 0);
                    hash = (hash * 31) + GetTargetsHashCode(_targets);
                    return hash;
                }
            }

            private static bool TargetsEqual(IReadOnlyList<SourcePreviewNavigationTarget>? left, IReadOnlyList<SourcePreviewNavigationTarget>? right)
            {
                if (ReferenceEquals(left, right))
                {
                    return true;
                }

                if (left is null || right is null || left.Count != right.Count)
                {
                    return false;
                }

                for (var index = 0; index < left.Count; index++)
                {
                    if (!ReferenceEquals(left[index], right[index]))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static int GetTargetsHashCode(IReadOnlyList<SourcePreviewNavigationTarget>? targets)
            {
                if (targets is null || targets.Count == 0)
                {
                    return 0;
                }

                unchecked
                {
                    var hash = 17;
                    for (var index = 0; index < targets.Count; index++)
                    {
                        hash = (hash * 31) + (targets[index]?.GetHashCode() ?? 0);
                    }

                    return hash;
                }
            }
        }
    }

public sealed class SourcePreviewNavigationOptions
{
    private readonly List<SourcePreviewNavigationTarget> _targets;

    internal SourcePreviewNavigationOptions(IEnumerable<SourcePreviewNavigationTarget> targets)
    {
        _targets = targets?.ToList() ?? new List<SourcePreviewNavigationTarget>();
    }

    public bool IsClickable { get; set; }

    public bool ShowUnderline { get; set; }

    public Cursor? Cursor { get; set; }

    public object? Context { get; set; }

    public IList<SourcePreviewNavigationTarget> Targets => _targets;

    internal IReadOnlyList<SourcePreviewNavigationTarget> GetSnapshot() => _targets.Count == 0 ? Array.Empty<SourcePreviewNavigationTarget>() : _targets.ToArray();
}

public sealed class SourcePreviewNavigationInspectEventArgs : EventArgs
{
    public SourcePreviewNavigationInspectEventArgs(SourcePreviewViewModel viewModel, KeyModifiers keyModifiers, SourcePreviewNavigationOptions options)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        KeyModifiers = keyModifiers;
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public SourcePreviewViewModel ViewModel { get; }

    public KeyModifiers KeyModifiers { get; }

    public SourcePreviewNavigationOptions Options { get; }
}

public sealed class SourcePreviewNavigationRequestEventArgs : EventArgs
{
    public SourcePreviewNavigationRequestEventArgs(SourcePreviewViewModel viewModel, SourcePreviewNavigationTrigger trigger, KeyModifiers keyModifiers, object? context, IReadOnlyList<SourcePreviewNavigationTarget> targets)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Trigger = trigger;
        KeyModifiers = keyModifiers;
        Context = context;
        Targets = targets ?? Array.Empty<SourcePreviewNavigationTarget>();
    }

    public SourcePreviewViewModel ViewModel { get; }

    public SourcePreviewNavigationTrigger Trigger { get; }

    public KeyModifiers KeyModifiers { get; }

    public object? Context { get; }

    public IReadOnlyList<SourcePreviewNavigationTarget> Targets { get; }

    public bool Handled { get; set; }

    public SourcePreviewNavigationTarget? SelectedTarget { get; set; }
}

public sealed class SourcePreviewNavigationStateChangedEventArgs : EventArgs
{
    public SourcePreviewNavigationStateChangedEventArgs(SourcePreviewNavigationState state)
    {
        State = state;
    }

    public SourcePreviewNavigationState State { get; }
}

public readonly record struct SourcePreviewNavigationState(
    bool IsClickable,
    bool ShowUnderline,
    Cursor? Cursor,
    IReadOnlyList<SourcePreviewNavigationTarget> Targets,
    object? Context);

public sealed class SourcePreviewScrollChangedEventArgs : EventArgs
{
    public SourcePreviewScrollChangedEventArgs(SourcePreviewScrollState state)
    {
        State = state;
    }

    public SourcePreviewScrollState State { get; }
}

public readonly record struct SourcePreviewScrollState(
    double HorizontalOffset,
    double VerticalOffset,
    int? FirstVisibleLine,
    int? FirstVisibleColumn)
{
    public static SourcePreviewScrollState Empty { get; } = new(double.NaN, double.NaN, null, null);

    public bool IsValid => !double.IsNaN(HorizontalOffset) && !double.IsNaN(VerticalOffset);
}

public enum SourcePreviewNavigationTrigger
{
    PointerPrimary,
    KeyboardF12,
    KeyboardControlEnter
}

}
