using Avalonia.Controls;
using Avalonia.Diagnostics.Controls.VirtualizedTreeView;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;

namespace Avalonia.Diagnostics.Views
{
    public partial class TreePageView : UserControl
    {
    private VirtualizedTreeViewItem? _hovered;
    private VirtualizedTreeView _tree;
        private System.IDisposable? _adorner;

        public TreePageView()
        {
            InitializeComponent();
            _tree = this.GetControl<VirtualizedTreeView>("tree");
        }

        protected void UpdateAdorner(object? sender, PointerEventArgs e)
        {
            if (e.Source is not StyledElement source)
            {
                return;
            }

            var item = source.FindLogicalAncestorOfType<VirtualizedTreeViewItem>();
            if (item == _hovered)
            {
                return;
            }

            _adorner?.Dispose();

            if (item is null || item.FindLogicalAncestorOfType<VirtualizedTreeView>() != _tree)
            {
                _hovered = null;
                return;
            }

            _hovered = item;

            var node = (item.DataContext as FlatTreeNode)?.Node as TreeNode;
            var visual = node?.Visual as Visual;
            var shouldVisualizeMarginPadding = (DataContext as TreePageViewModel)?.MainView.ShouldVisualizeMarginPadding;
            if (visual is null || shouldVisualizeMarginPadding is null)
            {
                return;
            }

            _adorner = Controls.ControlHighlightAdorner.Add(visual, visualizeMarginPadding: shouldVisualizeMarginPadding == true);
        }

        private void RemoveAdorner(object? sender, PointerEventArgs e)
        {
            _adorner?.Dispose();
            _adorner = null;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == DataContextProperty)
            {
                if (change.GetOldValue<object?>() is TreePageViewModel oldViewModel)
                {
                    oldViewModel.ClipboardCopyRequested -= OnClipboardCopyRequested;
                    oldViewModel.SourcePreviewRequested -= OnSourcePreviewRequested;
                }
                if (change.GetNewValue<object?>() is TreePageViewModel newViewModel)
                {
                    newViewModel.ClipboardCopyRequested += OnClipboardCopyRequested;
                    newViewModel.SourcePreviewRequested += OnSourcePreviewRequested;
                }
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnClipboardCopyRequested(object? sender, string selector)
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                var text = ToText(selector);
                var transfer = new DataTransfer();
                transfer.Add(DataTransferItem.CreateText(text));
                transfer.Add(DataTransferItem.Create(
                    DataFormat.CreateStringApplicationFormat(Constants.DataFormats.Avalonia_DevTools_Selector),
                    selector));
                _ = clipboard.SetDataAsync(transfer);
            }
        }

        private static string ToText(string text)
        {
            var sb = new System.Text.StringBuilder();
            var bufferStartIndex = -1;
            for (var ic = 0; ic < text.Length; ic++)
            {
                var c = text[ic];
                switch (c)
                {
                    case '{':
                        bufferStartIndex = sb.Length;
                        break;
                    case '}' when bufferStartIndex > -1:
                        sb.Remove(bufferStartIndex, sb.Length - bufferStartIndex);
                        bufferStartIndex = sb.Length;
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private void OnSourcePreviewRequested(object? sender, SourcePreviewViewModel e)
        {
            SourcePreviewWindow.Show(TopLevel.GetTopLevel(this), e);
        }
    }
}
