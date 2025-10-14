using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Diagnostics.SourceNavigation;

namespace Avalonia.Diagnostics.ViewModels
{
    public class CombinedTreePageViewModel : TreePageViewModel
    {
        public CombinedTreePageViewModel(
            MainViewModel mainView,
            TreeNode[] nodes,
            ISet<string> pinnedProperties,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator)
            : base(mainView, nodes, pinnedProperties, sourceInfoService, sourceNavigator)
        {
        }

        private bool _searchLogicalNodesOnly = true;

        public bool SearchLogicalNodesOnly
        {
            get => _searchLogicalNodesOnly;
            set
            {
                if (RaiseAndSetIfChanged(ref _searchLogicalNodesOnly, value))
                {
                    ApplyTreeFilter();
                }
            }
        }

        public static CombinedTreePageViewModel FromRoot(
            MainViewModel mainView,
            AvaloniaObject root,
            ISet<string> pinnedProperties,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator)
        {
            var nodes = CombinedTreeNode.Create(root);
            if (nodes.Length == 0)
            {
                return new CombinedTreePageViewModel(mainView, Array.Empty<TreeNode>(), pinnedProperties, sourceInfoService, sourceNavigator);
            }

            return new CombinedTreePageViewModel(mainView, Array.ConvertAll(nodes, x => (TreeNode)x), pinnedProperties, sourceInfoService, sourceNavigator);
        }

        protected override bool CanNodeMatch(TreeNode node)
        {
            if (SearchLogicalNodesOnly)
            {
                if (node is CombinedTreeTemplateGroupNode)
                {
                    return false;
                }

                if (node is CombinedTreeNode combinedNode)
                {
                    return combinedNode.Role != CombinedTreeNode.CombinedNodeRole.Template;
                }
            }

            return base.CanNodeMatch(node);
        }
    }
}
