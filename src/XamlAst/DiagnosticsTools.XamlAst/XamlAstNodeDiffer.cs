using System;
using System.Collections.Generic;

namespace Avalonia.Diagnostics.Xaml
{
    public static class XamlAstNodeDiffer
    {
        public static IReadOnlyList<XamlAstNodeChange> Diff(IXamlAstIndex? oldIndex, IXamlAstIndex newIndex)
        {
            if (newIndex is null)
            {
                throw new ArgumentNullException(nameof(newIndex));
            }

            var changes = new List<XamlAstNodeChange>();

            if (oldIndex is null)
            {
                foreach (var node in newIndex.Nodes)
                {
                    changes.Add(new XamlAstNodeChange(XamlAstNodeChangeKind.Added, null, node));
                }

                return changes.Count == 0 ? Array.Empty<XamlAstNodeChange>() : changes;
            }

            var oldNodesById = new Dictionary<XamlAstNodeId, XamlAstNodeDescriptor>();
            foreach (var node in oldIndex.Nodes)
            {
                oldNodesById[node.Id] = node;
            }

            foreach (var node in newIndex.Nodes)
            {
                if (!oldNodesById.TryGetValue(node.Id, out var oldNode))
                {
                    changes.Add(new XamlAstNodeChange(XamlAstNodeChangeKind.Added, null, node));
                    continue;
                }

                if (!node.StructuralEquals(oldNode))
                {
                    changes.Add(new XamlAstNodeChange(XamlAstNodeChangeKind.Updated, oldNode, node));
                }

                oldNodesById.Remove(node.Id);
            }

            if (oldNodesById.Count > 0)
            {
                foreach (var removed in oldNodesById.Values)
                {
                    changes.Add(new XamlAstNodeChange(XamlAstNodeChangeKind.Removed, removed, null));
                }
            }

            return changes.Count == 0 ? Array.Empty<XamlAstNodeChange>() : changes;
        }
    }
}
