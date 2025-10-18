using System;
using Avalonia.Controls;

namespace Avalonia.Diagnostics.Controls.VirtualizedTreeView;

public sealed class VirtualizedTreeListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return new VirtualizedTreeViewItem();
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        return NeedsContainer<VirtualizedTreeViewItem>(item, out recycleKey);
    }
}
