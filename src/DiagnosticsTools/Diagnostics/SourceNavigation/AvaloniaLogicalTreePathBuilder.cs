using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.LogicalTree;
using Avalonia.Styling;

namespace Avalonia.Diagnostics.SourceNavigation
{
    internal sealed class AvaloniaLogicalTreePathBuilder : ILogicalTreePathBuilder
    {
        public bool TryBuildPath(object candidate, out object? root, out IReadOnlyList<int> path)
        {
            root = null;
            path = Array.Empty<int>();

            if (candidate is not ILogical logical)
            {
                return false;
            }

            var indices = new List<int>();
            var current = logical;
            while (true)
            {
                if (current.LogicalParent is not { } parent)
                {
                    if (current is StyledElement styledRoot)
                    {
                        root = styledRoot;
                        indices.Reverse();
                        path = indices.ToArray();
                        return true;
                    }

                    return false;
                }

                var index = IndexOfLogicalChild(parent, current);
                if (index < 0)
                {
                    return false;
                }

                indices.Add(index);
                current = parent;
            }
        }

        private static int IndexOfLogicalChild(ILogical parent, ILogical child)
        {
            var children = parent.LogicalChildren;
            var count = children.Count;
            for (var i = 0; i < count; ++i)
            {
                if (ReferenceEquals(children[i], child))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
