using System;
using System.Collections.Generic;
using Avalonia;

namespace Avalonia.Diagnostics.ViewModels
{
    public class CombinedTreePageViewModel : TreePageViewModel
    {
        public CombinedTreePageViewModel(
            MainViewModel mainView,
            TreeNode[] nodes,
            ISet<string> pinnedProperties)
            : base(mainView, nodes, pinnedProperties)
        {
        }

        public static CombinedTreePageViewModel FromRoot(
            MainViewModel mainView,
            AvaloniaObject root,
            ISet<string> pinnedProperties)
        {
            var nodes = CombinedTreeNode.Create(root);
            if (nodes.Length == 0)
            {
                return new CombinedTreePageViewModel(mainView, Array.Empty<TreeNode>(), pinnedProperties);
            }

            return new CombinedTreePageViewModel(mainView, Array.ConvertAll(nodes, x => (TreeNode)x), pinnedProperties);
        }
    }
}
