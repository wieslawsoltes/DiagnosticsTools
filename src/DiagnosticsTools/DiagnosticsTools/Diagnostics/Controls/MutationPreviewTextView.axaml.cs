using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Avalonia.Diagnostics.Controls
{
    public partial class MutationPreviewTextView : UserControl
    {
        public static readonly StyledProperty<string?> TextProperty =
            AvaloniaProperty.Register<MutationPreviewTextView, string?>(nameof(Text));

        public static readonly StyledProperty<IReadOnlyList<MutationPreviewHighlight>> HighlightsProperty =
            AvaloniaProperty.Register<MutationPreviewTextView, IReadOnlyList<MutationPreviewHighlight>>(
                nameof(Highlights),
                Array.Empty<MutationPreviewHighlight>());

        public static readonly StyledProperty<IBrush> HighlightBrushProperty =
            AvaloniaProperty.Register<MutationPreviewTextView, IBrush>(
                nameof(HighlightBrush),
                new SolidColorBrush(Color.FromArgb(0x40, 0x32, 0xCD, 0x32)));

        private TextEditor? _editor;
        private TextDocument? _document;
        private HighlightColorizer? _colorizer;

        public MutationPreviewTextView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public string? Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public IReadOnlyList<MutationPreviewHighlight> Highlights
        {
            get => GetValue(HighlightsProperty);
            set => SetValue(HighlightsProperty, value);
        }

        public IBrush HighlightBrush
        {
            get => GetValue(HighlightBrushProperty);
            set => SetValue(HighlightBrushProperty, value);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_editor is not null && _colorizer is not null)
            {
                _editor.TextArea.TextView.LineTransformers.Remove(_colorizer);
            }

            _editor = e.NameScope.Find<TextEditor>("Editor");

            if (_editor is null)
            {
                return;
            }

            _document = new TextDocument(Text ?? string.Empty);
            _editor.Document = _document;

            _colorizer = new HighlightColorizer
            {
                Highlights = Highlights,
                HighlightBrush = HighlightBrush
            };

            _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TextProperty)
            {
                UpdateText();
            }
            else if (change.Property == HighlightsProperty)
            {
                UpdateHighlights();
            }
            else if (change.Property == HighlightBrushProperty)
            {
                UpdateHighlightBrush();
            }
        }

        private void UpdateText()
        {
            if (_document is null)
            {
                return;
            }

            var text = Text ?? string.Empty;
            _document.Text = text;
        }

        private void UpdateHighlights()
        {
            if (_colorizer is null)
            {
                return;
            }

            _colorizer.Highlights = Highlights ?? Array.Empty<MutationPreviewHighlight>();
            _editor?.TextArea.TextView.Redraw();
        }

        private void UpdateHighlightBrush()
        {
            if (_colorizer is null)
            {
                return;
            }

            _colorizer.HighlightBrush = HighlightBrush;
            _editor?.TextArea.TextView.Redraw();
        }

        private sealed class HighlightColorizer : DocumentColorizingTransformer
        {
            public IReadOnlyList<MutationPreviewHighlight> Highlights { get; set; } = Array.Empty<MutationPreviewHighlight>();

            public IBrush HighlightBrush { get; set; } = Brushes.Transparent;

            protected override void ColorizeLine(DocumentLine line)
            {
                if (Highlights.Count == 0)
                {
                    return;
                }

                var lineStart = line.Offset;
                var lineEnd = lineStart + line.Length;

                foreach (var highlight in Highlights)
                {
                    var start = highlight.Start;
                    var end = highlight.Start + highlight.Length;

                    if (highlight.Length <= 0 || end <= lineStart || start >= lineEnd)
                    {
                        continue;
                    }

                    var segmentStart = Math.Max(lineStart, start);
                    var segmentEnd = Math.Min(lineEnd, end);

                    ChangeLinePart(segmentStart, segmentEnd, element =>
                    {
                        element.TextRunProperties.SetBackgroundBrush(HighlightBrush);
                    });
                }
            }
        }
    }
}
