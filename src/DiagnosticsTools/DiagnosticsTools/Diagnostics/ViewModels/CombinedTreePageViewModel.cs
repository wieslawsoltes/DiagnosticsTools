using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Diagnostics.Runtime;

namespace Avalonia.Diagnostics.ViewModels
{
    public class CombinedTreePageViewModel : TreePageViewModel
    {
        public CombinedTreePageViewModel(
            MainViewModel mainView,
            TreeNode[] nodes,
            ISet<string> pinnedProperties,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator,
            XamlAstWorkspace xamlAstWorkspace,
            RuntimeMutationCoordinator runtimeCoordinator)
            : base(mainView, nodes, pinnedProperties, sourceInfoService, sourceNavigator, xamlAstWorkspace, runtimeCoordinator)
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
            ISourceNavigator sourceNavigator,
            XamlAstWorkspace xamlAstWorkspace,
            RuntimeMutationCoordinator runtimeCoordinator)
        {
            var nodes = CombinedTreeNode.Create(root);
            if (nodes.Length == 0)
            {
                return new CombinedTreePageViewModel(mainView, Array.Empty<TreeNode>(), pinnedProperties, sourceInfoService, sourceNavigator, xamlAstWorkspace, runtimeCoordinator);
            }

            return new CombinedTreePageViewModel(mainView, Array.ConvertAll(nodes, x => (TreeNode)x), pinnedProperties, sourceInfoService, sourceNavigator, xamlAstWorkspace, runtimeCoordinator);
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
