using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Collections;

namespace Avalonia.Diagnostics.ViewModels
{
    public sealed class CombinedTreeTemplateGroupNode : TreeNode
    {
        private readonly TemplateGroupChildren _children;

        public CombinedTreeTemplateGroupNode(CombinedTreeNode owner)
            : base(new AvaloniaObject(), owner, "/template/")
        {
            _children = new TemplateGroupChildren(this);
            Children = RegisterChildren(_children);
            IsExpanded = true;
        }

        public override TreeNodeCollection Children { get; }

        internal void UpdateChildren(IReadOnlyList<TreeNode> templates)
        {
            _children.Update(templates);
        }

        private sealed class TemplateGroupChildren : TreeNodeCollection
        {
            private IReadOnlyList<TreeNode> _children = Array.Empty<TreeNode>();
            private AvaloniaList<TreeNode>? _target;

            public TemplateGroupChildren(CombinedTreeTemplateGroupNode owner)
                : base(owner)
            {
            }

            protected override void Initialize(AvaloniaList<TreeNode> nodes)
            {
                _target = nodes;
                Refresh();
            }

            public void Update(IReadOnlyList<TreeNode> templates)
            {
                _children = templates;
                Refresh();
            }

            private void Refresh()
            {
                if (_target is null)
                {
                    return;
                }

                _target.Clear();
                foreach (var child in _children)
                {
                    _target.Add(child);
                }
            }

            public override void Dispose()
            {
                _target?.Clear();
            }
        }
    }
}
