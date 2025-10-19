using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Reactive;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Diagnostics;

namespace Avalonia.Diagnostics.ViewModels
{
    public sealed class CombinedTreeNode : TreeNode
    {
        public CombinedTreeNode(
            AvaloniaObject avaloniaObject,
            TreeNode? parent,
            CombinedNodeRole role = CombinedNodeRole.Logical,
            string? customTypeName = null,
            string? templateName = null,
            AvaloniaObject? templateOwner = null)
            : base(avaloniaObject, parent, customTypeName)
        {
            Role = role;
            TemplateName = templateName;
            TemplateOwner = templateOwner;
            Children = RegisterChildren(new CombinedTreeNodeCollection(this));
        }

        public enum CombinedNodeRole
        {
            Logical,
            Template,
            PopupHost,
        }

        public CombinedNodeRole Role { get; }

        public string? TemplateName { get; }

        public AvaloniaObject? TemplateOwner { get; }

        public string RoleKey => Role.ToString();

        public string? RoleLabel => Role switch
        {
            CombinedNodeRole.PopupHost => "/popup/",
            _ => null,
        };

        public string? TemplateDisplay
        {
            get
            {
                if (Role == CombinedNodeRole.Template)
                {
                    var segment = !string.IsNullOrEmpty(TemplateName)
                        ? TemplateName
                        : Visual.GetType().Name;

                    if (TemplateOwner is { } owner)
                    {
                        return owner.GetType().Name is { Length: > 0 } ownerName
                            ? $"{segment} • {ownerName}"
                            : segment;
                    }

                    return segment;
                }

                if (Role == CombinedNodeRole.PopupHost && TemplateOwner is { } popupOwner)
                {
                    return popupOwner.GetType().Name;
                }

                return null;
            }
        }

        public override TreeNodeCollection Children { get; }

        public override string SearchText => string.Join(" ", new[]
        {
            base.SearchText,
            TemplateName,
            TemplateDisplay,
            RoleLabel,
        }.Where(x => !string.IsNullOrWhiteSpace(x))
         .Select(x => x!.Trim()));

        public bool IsTemplatePart => Role == CombinedNodeRole.Template;

        public static CombinedTreeNode[] Create(object root)
        {
            return root is AvaloniaObject avaloniaObject
                ? new[] { new CombinedTreeNode(avaloniaObject, null) }
                : Array.Empty<CombinedTreeNode>();
        }

        private sealed class CombinedTreeNodeCollection : TreeNodeCollection
        {
            private readonly CombinedTreeNode _owner;
            private readonly List<LogicalEntry> _logical = new();
            private readonly List<TemplateEntry> _templates = new();
            private CombinedTreeTemplateGroupNode? _templateGroup;
            private PopupEntry? _popup;
            private AvaloniaList<TreeNode>? _nodes;
            private CombinedLogicalChildrenTracker? _logicalTracker;
            private CombinedTemplateChildrenTracker? _templateTracker;
            private CombinedPopupChildrenTracker? _popupTracker;

            public CombinedTreeNodeCollection(CombinedTreeNode owner)
                : base(owner)
            {
                _owner = owner;
            }

            protected override void Initialize(AvaloniaList<TreeNode> nodes)
            {
                _nodes = nodes;

                _logicalTracker = CombinedLogicalChildrenTracker.TryCreate(
                    _owner.Visual,
                    AddLogical,
                    RemoveLogical,
                    ClearLogical);

                _templateTracker = CombinedTemplateChildrenTracker.TryCreate(
                    _owner.Visual,
                    _owner,
                    AddTemplate,
                    RemoveTemplate,
                    ClearTemplates);

                _popupTracker = CombinedPopupChildrenTracker.TryCreate(
                    _owner.Visual,
                    SetPopup,
                    ClearPopup);

                RebuildNodes();
            }

            public override void Dispose()
            {
                _logicalTracker?.Dispose();
                _templateTracker?.Dispose();
                _popupTracker?.Dispose();

                foreach (var entry in _logical)
                {
                    entry.Node.Dispose();
                }

                foreach (var entry in _templates)
                {
                    entry.Node.Dispose();
                }

                _templateGroup?.Dispose();
                _templateGroup = null;

                if (_popup is { } popup)
                {
                    popup.Node.Dispose();
                }

                base.Dispose();
            }

            private void AddLogical(int index, AvaloniaObject child)
            {
                if (_nodes is null)
                {
                    return;
                }

                index = Clamp(index, 0, _logical.Count);

                var node = new CombinedTreeNode(child, _owner, CombinedNodeRole.Logical);
                _logical.Insert(index, new LogicalEntry(child, node));
                RebuildNodes();
            }

            private static int Clamp(int value, int min, int max)
            {
                if (value < min)
                {
                    return min;
                }

                if (value > max)
                {
                    return max;
                }

                return value;
            }

            private void RemoveLogical(AvaloniaObject child)
            {
                var idx = _logical.FindIndex(x => ReferenceEquals(x.Control, child));
                if (idx < 0)
                {
                    return;
                }

                var entry = _logical[idx];
                _logical.RemoveAt(idx);
                entry.Node.Dispose();
                RebuildNodes();
            }

            private void ClearLogical()
            {
                foreach (var entry in _logical)
                {
                    entry.Node.Dispose();
                }

                _logical.Clear();
                RebuildNodes();
            }

            private void AddTemplate(int visualIndex, Visual visual, string? customTypeName, string? templateName, AvaloniaObject? templateOwner)
            {
                if (_nodes is null)
                {
                    return;
                }

                foreach (var entry in _templates)
                {
                    if (entry.VisualIndex >= visualIndex)
                    {
                        entry.VisualIndex++;
                    }
                }

                var insertIndex = 0;
                while (insertIndex < _templates.Count && _templates[insertIndex].VisualIndex < visualIndex)
                {
                    insertIndex++;
                }

                var group = EnsureTemplateGroup();
                var node = new CombinedTreeNode(
                    visual,
                    group,
                    CombinedNodeRole.Template,
                    customTypeName,
                    templateName,
                    templateOwner);

                _templates.Insert(insertIndex, new TemplateEntry(visual, node, visualIndex));
                RebuildNodes();
            }

            private void RemoveTemplate(Visual visual)
            {
                var index = _templates.FindIndex(x => ReferenceEquals(x.Visual, visual));
                if (index < 0)
                {
                    return;
                }

                var entry = _templates[index];
                _templates.RemoveAt(index);

                foreach (var item in _templates)
                {
                    if (item.VisualIndex > entry.VisualIndex)
                    {
                        item.VisualIndex--;
                    }
                }

                entry.Node.Dispose();
                RebuildNodes();
            }

            private void ClearTemplates()
            {
                foreach (var entry in _templates)
                {
                    entry.Node.Dispose();
                }

                _templates.Clear();
                _templateGroup?.UpdateChildren(Array.Empty<TreeNode>());
                RebuildNodes();
            }

            private void SetPopup(Control root, string? customName)
            {
                if (_popup is { } existing)
                {
                    if (ReferenceEquals(existing.Root, root))
                    {
                        return;
                    }

                    existing.Node.Dispose();
                }

                _popup = new PopupEntry(root, new CombinedTreeNode(root, _owner, CombinedNodeRole.PopupHost, customName, null, _owner.Visual));
                RebuildNodes();
            }

            private void ClearPopup()
            {
                if (_popup is not { } popup)
                {
                    return;
                }

                popup.Node.Dispose();
                _popup = null;
                RebuildNodes();
            }

            private void RebuildNodes()
            {
                if (_nodes is null)
                {
                    return;
                }

                _nodes.Clear();

                foreach (var entry in _logical)
                {
                    _nodes.Add(entry.Node);
                }

                if (_templates.Count > 0)
                {
                    var group = EnsureTemplateGroup();
                    group.UpdateChildren(GetTemplateNodesSnapshot());
                    _nodes.Add(group);
                }
                else if (_templateGroup is { } existingGroup)
                {
                    existingGroup.UpdateChildren(Array.Empty<TreeNode>());
                }

                if (_popup is { } popup)
                {
                    _nodes.Add(popup.Node);
                }
            }

            private CombinedTreeTemplateGroupNode EnsureTemplateGroup()
            {
                return _templateGroup ??= new CombinedTreeTemplateGroupNode(_owner)
                {
                    IsExpanded = true,
                };
            }

            private IReadOnlyList<TreeNode> GetTemplateNodesSnapshot()
            {
                if (_templates.Count == 0)
                {
                    return Array.Empty<TreeNode>();
                }

                var result = new TreeNode[_templates.Count];
                for (var i = 0; i < _templates.Count; i++)
                {
                    result[i] = _templates[i].Node;
                }

                return result;
            }

            private readonly struct LogicalEntry
            {
                public LogicalEntry(AvaloniaObject control, CombinedTreeNode node)
                {
                    Control = control;
                    Node = node;
                }

                public AvaloniaObject Control { get; }

                public CombinedTreeNode Node { get; }
            }

            private sealed class TemplateEntry
            {
                public TemplateEntry(Visual visual, CombinedTreeNode node, int visualIndex)
                {
                    Visual = visual;
                    Node = node;
                    VisualIndex = visualIndex;
                }

                public Visual Visual { get; }

                public CombinedTreeNode Node { get; }

                public int VisualIndex { get; set; }
            }

            private readonly struct PopupEntry
            {
                public PopupEntry(Control root, CombinedTreeNode node)
                {
                    Root = root;
                    Node = node;
                }

                public Control Root { get; }

                public CombinedTreeNode Node { get; }
            }
        }

        private sealed class CombinedLogicalChildrenTracker : IDisposable
        {
            private readonly IDisposable? _subscription;
            private readonly Controls.TopLevelGroup? _group;
            private readonly Action<int, AvaloniaObject> _add;
            private readonly Action<AvaloniaObject> _remove;
            private readonly Action _reset;

            private CombinedLogicalChildrenTracker(
                IDisposable? subscription,
                Controls.TopLevelGroup? group,
                Action<int, AvaloniaObject> add,
                Action<AvaloniaObject> remove,
                Action reset)
            {
                _subscription = subscription;
                _group = group;
                _add = add;
                _remove = remove;
                _reset = reset;
            }

            public static CombinedLogicalChildrenTracker? TryCreate(
                AvaloniaObject owner,
                Action<int, AvaloniaObject> add,
                Action<AvaloniaObject> remove,
                Action reset)
            {
                if (owner is ILogical logical)
                {
                    var subscription = logical.LogicalChildren.ForEachItem(
                        (index, child) => add(index, (AvaloniaObject)child!),
                        (index, child) => remove((AvaloniaObject)child!),
                        () => reset());

                    return new CombinedLogicalChildrenTracker(subscription, null, add, remove, reset);
                }

                if (owner is Controls.TopLevelGroup group)
                {
                    var tracker = new CombinedLogicalChildrenTracker(null, group, add, remove, reset);
                    tracker.InitializeGroup();
                    return tracker;
                }

                return null;
            }

            private void InitializeGroup()
            {
                if (_group is null)
                {
                    return;
                }

                var items = _group.Items;
                var index = 0;
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (DevTools.IsDevToolsWindow(item))
                    {
                        continue;
                    }

                    _add(index++, item);
                }

                _group.Added += OnGroupAdded;
                _group.Removed += OnGroupRemoved;
            }

            private void OnGroupAdded(object? sender, TopLevel topLevel)
            {
                if (DevTools.IsDevToolsWindow(topLevel))
                {
                    return;
                }

                _add(int.MaxValue, topLevel);
            }

            private void OnGroupRemoved(object? sender, TopLevel topLevel)
            {
                if (DevTools.IsDevToolsWindow(topLevel))
                {
                    return;
                }

                _remove(topLevel);
            }

            public void Dispose()
            {
                _subscription?.Dispose();

                if (_group is not null)
                {
                    _group.Added -= OnGroupAdded;
                    _group.Removed -= OnGroupRemoved;
                }
            }
        }

        private sealed class CombinedTemplateChildrenTracker : IDisposable
        {
            private readonly CombinedTreeNode _owner;
            private readonly Action<int, Visual, string?, string?, AvaloniaObject?> _add;
            private readonly Action<Visual> _remove;
            private readonly Action _reset;
            private IDisposable? _subscription;

            private CombinedTemplateChildrenTracker(
                CombinedTreeNode owner,
                Action<int, Visual, string?, string?, AvaloniaObject?> add,
                Action<Visual> remove,
                Action reset)
            {
                _owner = owner;
                _add = add;
                _remove = remove;
                _reset = reset;
            }

            public static CombinedTemplateChildrenTracker? TryCreate(
                AvaloniaObject owner,
                CombinedTreeNode ownerNode,
                Action<int, Visual, string?, string?, AvaloniaObject?> add,
                Action<Visual> remove,
                Action reset)
            {
                if (owner is not Visual visual)
                {
                    return null;
                }

                var tracker = new CombinedTemplateChildrenTracker(ownerNode, add, remove, reset);
                tracker.Initialize(visual);
                return tracker;
            }

            private void Initialize(Visual visual)
            {
                _subscription = visual.VisualChildren.ForEachItem(
                    (index, child) => OnVisualAdded(index, (Visual)child!),
                    (index, child) => OnVisualRemoved((Visual)child!),
                    () => _reset());
            }

            private void OnVisualAdded(int index, Visual visual)
            {
                if (visual is StyledElement styled && styled.TemplatedParent is AvaloniaObject templatedParent)
                {
                    if (!ReferenceEquals(templatedParent, _owner.Visual))
                    {
                        return;
                    }

                    var templateName = styled.Name;
                    _add(index, visual, null, templateName, templatedParent);
                }
            }

            private void OnVisualRemoved(Visual visual)
            {
                _remove(visual);
            }

            public void Dispose()
            {
                var subscription = _subscription;
                _subscription = null;
                subscription?.Dispose();
            }
        }

        private sealed class CombinedPopupChildrenTracker : IDisposable
        {
            private readonly Action<Control, string?> _set;
            private readonly Action _clear;
            private IDisposable? _subscription;

            private CombinedPopupChildrenTracker(Action<Control, string?> set, Action clear)
            {
                _set = set;
                _clear = clear;
            }

            public static CombinedPopupChildrenTracker? TryCreate(
                AvaloniaObject owner,
                Action<Control, string?> set,
                Action clear)
            {
                if (owner is not Visual visual)
                {
                    return null;
                }

                var tracker = new CombinedPopupChildrenTracker(set, clear);
                tracker.Initialize(visual);
                return tracker;
            }

            private void Initialize(Visual visual)
            {
                var popupObservable = GetHostedPopupRootObservable(visual);
                if (popupObservable is null)
                {
                    return;
                }

                _subscription = popupObservable.Subscribe(popupRoot =>
                {
                    if (popupRoot is { Root: { } root })
                    {
                        _set(root, popupRoot.Value.CustomName);
                    }
                    else
                    {
                        _clear();
                    }
                });
            }

            public void Dispose()
            {
                var subscription = _subscription;
                _subscription = null;
                subscription?.Dispose();
            }

            private static IObservable<PopupRootInfo?>? GetHostedPopupRootObservable(Visual visual)
            {
                static IObservable<PopupRootInfo?> CreatePopupHostObservable(
                    IPopupHostProvider popupHostProvider,
                    string? providerName = null)
                {
                    return Observable.Create<IPopupHost?>(observer =>
                        {
                            void Handler(IPopupHost? args) => observer.OnNext(args);
                            popupHostProvider.PopupHostChanged += Handler;
                            return Disposable.Create(() => popupHostProvider.PopupHostChanged -= Handler);
                        })
                        .StartWith(popupHostProvider.PopupHost)
                        .Select(popupHost =>
                        {
                            if (popupHost is Control control)
                            {
                                return new PopupRootInfo(
                                    control,
                                    providerName != null ? $"{providerName} ({control.GetType().Name})" : null);
                            }

                            return (PopupRootInfo?)null;
                        });
                }

                return visual switch
                {
                    Popup popup => CreatePopupHostObservable(popup),
                    Control control => Observable
                        .CombineLatest(new IObservable<object?>[]
                        {
                            control.GetObservable(Control.ContextFlyoutProperty),
                            control.GetObservable(Control.ContextMenuProperty),
                            control.GetObservable(FlyoutBase.AttachedFlyoutProperty),
                            control.GetObservable(ToolTipDiagnostics.ToolTipProperty),
                            control.GetObservable(Button.FlyoutProperty),
                        })
                        .Select(items =>
                        {
                            var contextFlyout = items[0] as IPopupHostProvider;
                            var contextMenu = items[1] as ContextMenu;
                            var attachedFlyout = items[2] as IPopupHostProvider;
                            var toolTip = items[3] as IPopupHostProvider;
                            var buttonFlyout = items[4] as IPopupHostProvider;

                            if (contextMenu is not null)
                            {
                                return Observable.Return<PopupRootInfo?>(new PopupRootInfo(contextMenu));
                            }

                            if (contextFlyout is not null)
                            {
                                return CreatePopupHostObservable(contextFlyout, "ContextFlyout");
                            }

                            if (attachedFlyout is not null)
                            {
                                return CreatePopupHostObservable(attachedFlyout, "AttachedFlyout");
                            }

                            if (toolTip is not null)
                            {
                                return CreatePopupHostObservable(toolTip, "ToolTip");
                            }

                            if (buttonFlyout is not null)
                            {
                                return CreatePopupHostObservable(buttonFlyout, "Flyout");
                            }

                            return Observable.Return<PopupRootInfo?>(null);
                        })
                        .Switch(),
                    _ => null,
                };
            }

            private readonly struct PopupRootInfo
            {
                public PopupRootInfo(Control root, string? customName = null)
                {
                    Root = root;
                    CustomName = customName;
                }

                public Control Root { get; }

                public string? CustomName { get; }
            }
        }
    }
}
