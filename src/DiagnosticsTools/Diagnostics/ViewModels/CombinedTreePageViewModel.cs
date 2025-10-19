using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics.Runtime;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

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
            RuntimeMutationCoordinator runtimeCoordinator,
            SelectionCoordinator selectionCoordinator,
            string selectionOwnerId)
            : base(mainView, nodes, pinnedProperties, sourceInfoService, sourceNavigator, xamlAstWorkspace, runtimeCoordinator, selectionCoordinator, selectionOwnerId)
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
            RuntimeMutationCoordinator runtimeCoordinator,
            SelectionCoordinator selectionCoordinator,
            string selectionOwnerId)
        {
            var nodes = CombinedTreeNode.Create(root);
            if (nodes.Length == 0)
            {
                return new CombinedTreePageViewModel(mainView, Array.Empty<TreeNode>(), pinnedProperties, sourceInfoService, sourceNavigator, xamlAstWorkspace, runtimeCoordinator, selectionCoordinator, selectionOwnerId);
            }

            return new CombinedTreePageViewModel(mainView, Array.ConvertAll(nodes, x => (TreeNode)x), pinnedProperties, sourceInfoService, sourceNavigator, xamlAstWorkspace, runtimeCoordinator, selectionCoordinator, selectionOwnerId);
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

        protected override TreeNode? ResolveSelectionTarget(TreeNode node) =>
            SearchLogicalNodesOnly
                ? ResolveSelectionTargetForLogicalSearch(node)
                : node;

        private TreeNode? ResolveSelectionTargetForLogicalSearch(TreeNode node)
        {
            if (node is CombinedTreeNode { Role: CombinedTreeNode.CombinedNodeRole.Template } templateNode)
            {
                return templateNode;
            }

            return GetLogicalAncestor(node) ?? node;
        }

        private static TreeNode? GetLogicalAncestor(TreeNode? node)
        {
            var current = node;

            while (current is not null)
            {
                if (current is CombinedTreeNode combined)
                {
                    if (combined.Role == CombinedTreeNode.CombinedNodeRole.Logical &&
                        !IsTemplateLogicalNode(combined))
                    {
                        return combined;
                    }
                }

                current = current.Parent;
            }

            return null;
        }

        private static bool IsTemplateLogicalNode(CombinedTreeNode node)
        {
            if (node.Role != CombinedTreeNode.CombinedNodeRole.Logical)
            {
                return false;
            }

            if (node.Visual is StyledElement { TemplatedParent: { } })
            {
                return true;
            }

            if (node.Visual is Control { TemplatedParent: { } })
            {
                return true;
            }

            return false;
        }

        public override void SelectControl(Control control)
        {
            Dispatcher.UIThread.InvokeAsync(() => base.SelectControl(control), DispatcherPriority.Loaded);
        }
    }
}
