using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.ViewModels
{
    public class TreePageViewModel : ViewModelBase, IDisposable
    {
        private TreeNode? _selectedNode;
        private ControlDetailsViewModel? _details;
        private TreeNode[] _nodes;
        private readonly TreeNode[] _rootNodes;
        private TreeNode? _scopedRoot;
        private string? _scopedNodeKey;
        private bool _isScoped;
        private readonly ISet<string> _pinnedProperties;
        private TreeSearchField _selectedTreeSearchField = TreeSearchField.TypeName;
        private readonly ConcurrentDictionary<TreeNode, Task<SourceInfo?>> _sourceInfoCache = new();
        private ISourceInfoService _sourceInfoService;
        private ISourceNavigator _sourceNavigator;
        private SourceInfo? _selectedNodeSourceInfo;

        public TreePageViewModel(
            MainViewModel mainView,
            TreeNode[] nodes,
            ISet<string> pinnedProperties,
            ISourceInfoService sourceInfoService,
            ISourceNavigator sourceNavigator)
        {
            MainView = mainView;
            _rootNodes = nodes;
            _nodes = nodes;
            _pinnedProperties = pinnedProperties;
            _sourceInfoService = sourceInfoService ?? throw new ArgumentNullException(nameof(sourceInfoService));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            PropertiesFilter = new FilterViewModel();
            PropertiesFilter.RefreshFilter += (s, e) => Details?.PropertiesView?.Refresh();

            SettersFilter = new FilterViewModel();
            SettersFilter.RefreshFilter += (s, e) => Details?.UpdateStyleFilters();

            TreeFilter = new FilterViewModel();
            TreeFilter.RefreshFilter += (s, e) => ApplyTreeFilter();
        }

        public event EventHandler<string>? ClipboardCopyRequested;

        public MainViewModel MainView { get; }

        public FilterViewModel PropertiesFilter { get; }

        public FilterViewModel SettersFilter { get; }

        public FilterViewModel TreeFilter { get; }

        public TreeNode[] Nodes
        {
            get => _nodes;
            protected set => RaiseAndSetIfChanged(ref _nodes, value);
        }

        public TreeSearchField SelectedTreeSearchField
        {
            get => _selectedTreeSearchField;
            set
            {
                if (RaiseAndSetIfChanged(ref _selectedTreeSearchField, value))
                {
                    ApplyTreeFilter();
                }
            }
        }

        private TreeNode? ScopedNode
        {
            get => _scopedRoot;
            set => RaiseAndSetIfChanged(ref _scopedRoot, value);
        }

        public string? ScopedNodeKey
        {
            get => _scopedNodeKey;
            private set => RaiseAndSetIfChanged(ref _scopedNodeKey, value);
        }

        public TreeNode? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (RaiseAndSetIfChanged(ref _selectedNode, value))
                {
                    Details = value != null ?
                        new ControlDetailsViewModel(this, value.Visual, _pinnedProperties, _sourceInfoService, _sourceNavigator) :
                        null;
                    Details?.UpdatePropertiesView(MainView.ShowImplementedInterfaces);
                    Details?.UpdateStyleFilters();
                    Details?.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);
                    RaisePropertyChanged(nameof(CanScopeToSubTree));
                    RaisePropertyChanged(nameof(CanNavigateToSource));
                    SelectedNodeSourceInfo = null;
                    _ = UpdateSelectedNodeSourceInfoAsync(value);
                }
            }
        }

        public SourceInfo? SelectedNodeSourceInfo
        {
            get => _selectedNodeSourceInfo;
            private set
            {
                if (RaiseAndSetIfChanged(ref _selectedNodeSourceInfo, value))
                {
                    RaisePropertyChanged(nameof(SelectedNodeSourceSummary));
                    RaisePropertyChanged(nameof(HasSelectedNodeSource));
                    RaisePropertyChanged(nameof(CanNavigateToSource));
                }
            }
        }

        public string? SelectedNodeSourceSummary => SelectedNodeSourceInfo?.DisplayPath;

        public bool HasSelectedNodeSource => SelectedNodeSourceInfo is not null;

    public bool CanNavigateToSource => HasSelectedNodeSource;

        public ControlDetailsViewModel? Details
        {
            get => _details;
            private set
            {
                var oldValue = _details;

                if (RaiseAndSetIfChanged(ref _details, value))
                {
                    oldValue?.Dispose();
                }
            }
        }

        public void Dispose()
        {
            foreach (var node in Nodes)
            {
                node.Dispose();
            }

            _details?.Dispose();
        }

        public async void NavigateToSource()
        {
            var node = SelectedNode;
            if (node is null)
            {
                return;
            }

            try
            {
                var info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);
                if (info is null)
                {
                    return;
                }

                await _sourceNavigator.NavigateAsync(info).ConfigureAwait(false);
            }
            catch
            {
                // Navigation is best-effort.
            }
        }

        public void UpdateSourceNavigation(ISourceInfoService sourceInfoService, ISourceNavigator sourceNavigator)
        {
            if (sourceInfoService is null)
            {
                throw new ArgumentNullException(nameof(sourceInfoService));
            }

            if (sourceNavigator is null)
            {
                throw new ArgumentNullException(nameof(sourceNavigator));
            }

            if (!ReferenceEquals(_sourceInfoService, sourceInfoService))
            {
                _sourceInfoCache.Clear();
                _sourceInfoService = sourceInfoService;
            }

            _sourceNavigator = sourceNavigator;

            Details?.UpdateSourceNavigation(_sourceInfoService, _sourceNavigator);

            if (SelectedNode is not null)
            {
                _ = UpdateSelectedNodeSourceInfoAsync(SelectedNode);
            }
        }

        public TreeNode? FindNode(Control control)
        {
            foreach (var node in Nodes)
            {
                var result = FindNode(node, control);

                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        private async Task<SourceInfo?> EnsureSourceInfoAsync(TreeNode node)
        {
            var task = _sourceInfoCache.GetOrAdd(node, ResolveNodeSourceInfoAsync);
            var info = await task.ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => node.UpdateSourceInfo(info));
            return info;
        }

        private async Task<SourceInfo?> ResolveNodeSourceInfoAsync(TreeNode node)
        {
            try
            {
                var info = await _sourceInfoService.GetForAvaloniaObjectAsync(node.Visual).ConfigureAwait(false);
                if (info is null)
                {
                    info = await _sourceInfoService.GetForMemberAsync(node.Visual.GetType()).ConfigureAwait(false);
                }

                return info;
            }
            catch
            {
                return null;
            }
        }

        private async Task UpdateSelectedNodeSourceInfoAsync(TreeNode? node)
        {
            if (node is null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SelectedNodeSourceInfo = null);
                return;
            }

            var info = await EnsureSourceInfoAsync(node).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => SelectedNodeSourceInfo = info);
        }

        public void SelectControl(Control control)
        {
            var node = default(TreeNode);
            Control? c = control;

            while (node == null && c != null)
            {
                node = FindNode(c);

                if (node == null)
                {
                    c = c.GetVisualParent<Control>();
                }
            }

            if (node != null)
            {
                ExpandNode(node.Parent);
                // Use dispatcher to allow the tree to expand and render before selecting
                Dispatcher.UIThread.Post(() => SelectedNode = node, DispatcherPriority.Loaded);
            }
        }

        public void CopySelector()
        {
            var currentVisual = SelectedNode?.Visual as Visual;
            if (currentVisual is not null)
            {
                var selector = GetVisualSelector(currentVisual);

                ClipboardCopyRequested?.Invoke(this, selector);
            }
        }

        public void CopySelectorFromTemplateParent()
        {
            var parts = new List<string>();

            var currentVisual = SelectedNode?.Visual as Visual;
            while (currentVisual is not null)
            {
                parts.Add(GetVisualSelector(currentVisual));

                currentVisual = currentVisual.TemplatedParent as Visual;
            }

            if (parts.Any())
            {
                parts.Reverse();
                var selector = string.Join(" /template/ ", parts);

                ClipboardCopyRequested?.Invoke(this, selector);
            }
        }

        public void ExpandRecursively()
        {
            if (SelectedNode is { } selectedNode)
            {
                ExpandNode(selectedNode);

                var stack = new Stack<TreeNode>();
                stack.Push(selectedNode);

                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    item.IsExpanded = true;
                    foreach (var child in item.Children)
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        public void CollapseChildren()
        {
            if (SelectedNode is { } selectedNode)
            {
                var stack = new Stack<TreeNode>();
                stack.Push(selectedNode);

                while (stack.Count > 0)
                {
                    var item = stack.Pop();
                    item.IsExpanded = false;
                    foreach (var child in item.Children)
                    {
                        stack.Push(child);
                    }
                }
            }
        }

        public void CaptureNodeScreenshot()
        {
            MainView.Shot(null);
        }

        public void BringIntoView()
        {
            (SelectedNode?.Visual as Control)?.BringIntoView();
        }


        public void Focus()
        {
            (SelectedNode?.Visual as Control)?.Focus();
        }

        public bool CanScopeToSubTree => !IsScoped && SelectedNode != null;

        public bool CanShowFullTree => IsScoped;

        public bool IsScoped
        {
            get => _isScoped;
            private set
            {
                if (RaiseAndSetIfChanged(ref _isScoped, value))
                {
                    RaisePropertyChanged(nameof(CanScopeToSubTree));
                    RaisePropertyChanged(nameof(CanShowFullTree));
                }
            }
        }

        public void ScopeToSubTree()
        {
            if (!CanScopeToSubTree)
            {
                return;
            }

            var selected = SelectedNode!;
            Nodes = new[] { selected };
            ScopedNode = selected;
            ScopedNodeKey = BuildNodeKey(selected);
            IsScoped = true;
            ApplyTreeFilter();
        }

        public void ShowFullTree()
        {
            if (!CanShowFullTree)
            {
                return;
            }

            Nodes = _rootNodes;
            ScopedNode = null;
            ScopedNodeKey = null;
            IsScoped = false;
            ApplyTreeFilter();
        }

        public void ExpandAll()
        {
            foreach (var node in Nodes)
            {
                SetExpandedRecursive(node, true);
            }
        }

        public void CollapseAll()
        {
            foreach (var node in Nodes)
            {
                SetExpandedRecursive(node, false);
            }
        }

        private static string GetVisualSelector(Visual visual)
        {
            var name = string.IsNullOrEmpty(visual.Name) ? "" : $"#{visual.Name}";
            var classes = string.Concat(visual.Classes
                .Where(c => !c.StartsWith(":"))
                .Select(c => '.' + c));
            var pseudo = string.Concat(visual.Classes.Where(c => c[0] == ':').Select(c => c));
            var type = StyledElement.GetStyleKey(visual);
            return $$"""{{{type.Assembly.FullName}}}{{type.Namespace}}|{{type.Name}}{{name}}{{classes}}{{pseudo}}""";
        }

        private void ExpandNode(TreeNode? node)
        {
            if (node != null)
            {
                node.IsExpanded = true;
                ExpandNode(node.Parent);
            }
        }

        private static void SetExpandedRecursive(TreeNode node, bool isExpanded)
        {
            var stack = new Stack<TreeNode>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                current.IsExpanded = isExpanded;

                foreach (var child in current.Children)
                {
                    stack.Push(child);
                }
            }
        }

        internal void RestoreScope(TreeNode? scoped)
        {
            if (scoped is null)
            {
                ShowFullTree();
                return;
            }

            ScopedNode = scoped;
            Nodes = new[] { scoped };
            ScopedNodeKey = BuildNodeKey(scoped);
            IsScoped = true;
            ApplyTreeFilter();
        }

        internal void RestoreScopeFromKey(string? key)
        {
            if (string.IsNullOrEmpty(key))
            {
                ShowFullTree();
                return;
            }

            var node = FindNodeByKey(key!);
            if (node is null)
            {
                ShowFullTree();
                return;
            }

            ScopedNode = node;
            Nodes = new[] { node };
            ScopedNodeKey = key;
            IsScoped = true;
            ApplyTreeFilter();
        }

        private string BuildNodeKey(TreeNode node)
        {
            var segments = new Stack<int>();
            var current = node;

            while (current.Parent is { } parent)
            {
                var childIndex = GetChildIndex(parent, current);
                if (childIndex < 0)
                {
                    return string.Empty;
                }

                segments.Push(childIndex);
                current = parent;
            }

            var rootIndex = Array.IndexOf(_rootNodes, current);
            if (rootIndex < 0)
            {
                return string.Empty;
            }

            segments.Push(rootIndex);
            return string.Join(".", segments.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        }

        private static int GetChildIndex(TreeNode parent, TreeNode node)
        {
            for (var i = 0; i < parent.Children.Count; i++)
            {
                if (ReferenceEquals(parent.Children[i], node))
                {
                    return i;
                }
            }

            return -1;
        }

        private TreeNode? FindNodeByKey(string key)
        {
            var parts = key.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            TreeNode? current = null;

            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var index))
                {
                    return null;
                }

                if (i == 0)
                {
                    if (index < 0 || index >= _rootNodes.Length)
                    {
                        return null;
                    }

                    current = _rootNodes[index];
                }
                else
                {
                    if (current is null)
                    {
                        return null;
                    }

                    var children = current.Children;
                    if (index < 0 || index >= children.Count)
                    {
                        return null;
                    }

                    current = children[index];
                }
            }

            return current;
        }

        private TreeNode? FindNode(TreeNode node, Control control)
        {
            if (node.Visual == control)
            {
                return node;
            }
            else
            {
                foreach (var child in node.Children)
                {
                    var result = FindNode(child, control);

                    if (result != null)
                    {
                        return result;
                    }
                }
            }

            return null;
        }

        internal void UpdatePropertiesView()
        {
            Details?.UpdatePropertiesView(MainView?.ShowImplementedInterfaces ?? true);
        }

        protected virtual bool CanNodeMatch(TreeNode node) => true;

        protected void ApplyTreeFilter()
        {
            var hasFilter = !string.IsNullOrWhiteSpace(TreeFilter.FilterString);

            if (ScopedNode is { } scoped)
            {
                if (hasFilter)
                {
                    FilterNode(scoped, ancestorMatch: false);
                }
                else
                {
                    ResetVisibility(scoped);
                }
            }
            else
            {
                foreach (var root in _rootNodes)
                {
                    if (hasFilter)
                    {
                        FilterNode(root, ancestorMatch: false);
                    }
                    else
                    {
                        ResetVisibility(root);
                    }
                }
            }

            if (hasFilter && SelectedNode is { } selected && !selected.IsVisible)
            {
                SelectedNode = null;
            }

            RefreshNodesSource();

            bool FilterNode(TreeNode node, bool ancestorMatch)
            {
                var matches = hasFilter && CanNodeMatch(node) && NodeMatchesFilter(node);
                var hasMatchInChildren = false;

                foreach (var child in node.Children)
                {
                    var childHasMatch = FilterNode(child, ancestorMatch || matches);

                    if (hasFilter && !childHasMatch)
                    {
                        child.IsExpanded = false;
                    }

                    if (childHasMatch)
                    {
                        hasMatchInChildren = true;
                    }
                }

                var hasResult = matches || hasMatchInChildren;
                node.IsVisible = hasFilter ? (hasResult || ancestorMatch && !CanNodeMatch(node)) : true;

                if (hasFilter && node.HasChildren)
                {
                    node.IsExpanded = hasResult;
                }

                return hasResult;
            }

            void ResetVisibility(TreeNode node)
            {
                node.IsVisible = true;
                foreach (var child in node.Children)
                {
                    ResetVisibility(child);
                }
            }
        }

        private void RefreshNodesSource()
        {
            if (IsScoped && ScopedNode is { } scoped)
            {
                Nodes = new[] { scoped };
                return;
            }

            if (_rootNodes.Length == 0)
            {
                Nodes = Array.Empty<TreeNode>();
                return;
            }

            var copy = new TreeNode[_rootNodes.Length];
            Array.Copy(_rootNodes, copy, _rootNodes.Length);
            Nodes = copy;
        }

        private bool NodeMatchesFilter(TreeNode node)
        {
            var searchText = SelectedTreeSearchField switch
            {
                TreeSearchField.TypeName => BuildTypeSearchText(node),
                TreeSearchField.Classes => BuildClassesSearchText(node),
                TreeSearchField.Name => BuildNameSearchText(node),
                _ => node.SearchText,
            };

            if (string.IsNullOrWhiteSpace(searchText))
            {
                return false;
            }

            return TreeFilter.Filter(searchText);
        }

        private static string BuildTypeSearchText(TreeNode node)
        {
            var visualType = node.Visual?.GetType();

            return string.Join(" ", new[]
                {
                    node.Type,
                    visualType?.Name,
                    visualType?.FullName,
                }.Where(x => !string.IsNullOrWhiteSpace(x))
                 .Select(x => x!.Trim()));
        }

        private static string BuildClassesSearchText(TreeNode node)
        {
            if (node.Visual is StyledElement styled && styled.Classes.Count > 0)
            {
                var tokens = styled.Classes.SelectMany(cls =>
                {
                    if (string.IsNullOrWhiteSpace(cls))
                    {
                        return Array.Empty<string>();
                    }

                    if (cls.StartsWith(":", StringComparison.Ordinal))
                    {
                        return new[] { cls };
                    }

                    return new[] { cls, "." + cls };
                });

                var text = string.Join(" ", tokens.Where(x => !string.IsNullOrWhiteSpace(x))
                                                   .Select(x => x.Trim()))
                                   .Trim();

                return text;
            }

            return string.Empty;
        }

        private static string BuildNameSearchText(TreeNode node)
        {
            if (!string.IsNullOrWhiteSpace(node.ElementName))
            {
                return node.ElementName!;
            }

            if (node.Visual is StyledElement styled && !string.IsNullOrWhiteSpace(styled.Name))
            {
                return styled.Name!;
            }

            return string.Empty;
        }
    }
}
