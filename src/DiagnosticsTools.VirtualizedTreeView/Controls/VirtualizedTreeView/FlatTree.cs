using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Utilities;

namespace Avalonia.Diagnostics.Controls.VirtualizedTreeView;

public class FlatTree : IReadOnlyList<FlatTreeNode>,
    IList<FlatTreeNode>,
    IList,
    INotifyCollectionChanged,
    IWeakEventSubscriber<NotifyCollectionChangedEventArgs>,
    IWeakEventSubscriber<PropertyChangedEventArgs>
{
    private List<FlatTreeNode> _flatTree = new();

    // In order to keep expanded state in sync, we need to keep track of expanded nodes here, not by checking IsExpanded
    // Why: property changed can be fired even if IsExpanded is not changed
    // Why: if there is an event bound to IsExpanded changes that changes the children, then this change can be fired before IsExpanded property change
    // is fired in FlatTree
    private HashSet<ITreeNode> _expanded = new();

    public FlatTree(IEnumerable<ITreeNode> roots)
    {
        foreach (var root in roots)
        {
            InsertNode(root, 0, _flatTree.Count);
        }
    }

    /// <summary>
    /// Returns true if the given node is expanded in the flat tree
    /// (its children have been already added to the tree)
    /// </summary>
    /// <param name="node">The node to check whether is expanded</param>
    /// <returns>True if node is expanded</returns>
    private bool IsExpanded(ITreeNode node)
    {
        return _expanded.Contains(node);
    }

    private void SubscribeToNode(ITreeNode node)
    {
        WeakEvents.CollectionChanged.Subscribe(node, this);
        WeakEvents.ThreadSafePropertyChanged.Subscribe(node, this);
    }

    private void UnsubscribeFromNode(ITreeNode node)
    {
        _expanded.Remove(node);
        WeakEvents.CollectionChanged.Unsubscribe(node, this);
        WeakEvents.ThreadSafePropertyChanged.Unsubscribe(node, this);
    }

    /// <summary>
    /// Inserts an ITreeNode at the given level (indent) and index along with all expanded children.
    /// Then binds to the PropertyChanged and CollectionChanged events of the node to observer changes.
    /// </summary>
    /// <param name="node">Node to insert</param>
    /// <param name="level">Indent level for the given node</param>
    /// <param name="startIndex">Index to insert the node at</param>
    /// <returns>Number of inserted elements to the list</returns>
    private int InsertNode(ITreeNode node, int level, int startIndex)
    {
        int index = startIndex;
        SubscribeToNode(node);

        if (!node.IsVisible)
        {
            return 0;
        }

        var flatChild = new FlatTreeNode(node, level);
        _flatTree.Insert(index++, flatChild);

        if (node.IsExpanded)
        {
            _expanded.Add(node);
            index += InsertChildren(flatChild, index);
        }

        return index - startIndex;
    }

    /// <summary>
    /// Inserts children of the node and all expanded children recursively starting at the given index
    /// </summary>
    /// <param name="parent">Parent node</param>
    /// <param name="startIndex">Index to insert the children at</param>
    /// <returns>Number of added nodes</returns>
    private int InsertChildren(FlatTreeNode parent, int startIndex)
    {
        int index = startIndex;
        foreach (var child in parent.Node.Children)
        {
            index += InsertNode(child, parent.Level + 1, index);
        }

        return index - startIndex;
    }

    /// <summary>
    /// Counts all expanded children of the given node recursively
    /// </summary>
    /// <param name="parent">Parent node</param>
    /// <param name="limit">If non null, counts only first `limit` children</param>
    /// <returns>Number of expanded children</returns>
    private int CountExpandedChildren(ITreeNode parent, int? limit = null)
    {
        int count = 0;
        var end = limit ?? parent.Children.Count;
        for (var index = 0; index < end; index++)
        {
            var child = parent.Children[index];
            if (!child.IsVisible)
            {
                continue;
            }
            count++;
            if (IsExpanded(child))
                count += CountExpandedChildren(child);
        }

        return count;
    }

    private int IndexOfNode(ITreeNode node)
    {
        for (int i = 0; i < _flatTree.Count; i++)
            if (ReferenceEquals(_flatTree[i].Node, node))
                return i;
        return -1;
    }

    public void OnEvent(object? sender, WeakEvent ev, PropertyChangedEventArgs e)
    {
        NodeOnPropertyChanged(sender, e);
    }

    private void NodeOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == null)
            return;

        var node = (ITreeNode)sender;

        if (e.PropertyName != nameof(node.IsExpanded))
        {
            return;
        }

        var nodeIndex = IndexOfNode(node);
        if (nodeIndex < 0)
        {
            if (!node.IsExpanded)
            {
                _expanded.Remove(node);
            }
            return;
        }
        var flatNode = _flatTree[nodeIndex];
        if (node.IsExpanded)
        {
            if (!_expanded.Add(node))
                return;

            var insertedItemsCount = InsertChildren(flatNode, nodeIndex + 1);
            var newItems = _flatTree.GetRange(nodeIndex + 1, insertedItemsCount);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newItems, nodeIndex + 1));
        }
        else
        {
            if (!_expanded.Remove(node))
                return;

            var removedItemsCount = CountExpandedChildren(node);
            if (removedItemsCount <= 0)
            {
                return;
            }

            removedItemsCount = Math.Min(removedItemsCount, _flatTree.Count - (nodeIndex + 1));
            if (removedItemsCount <= 0)
            {
                return;
            }

            var removedItems = _flatTree.GetRange(nodeIndex + 1, removedItemsCount);
            foreach (var item in removedItems)
            {
                UnsubscribeFromNode(item.Node);
            }

            _flatTree.RemoveRange(nodeIndex + 1, removedItemsCount);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removedItems, nodeIndex + 1));
        }
    }

    public void OnEvent(object? sender, WeakEvent ev, NotifyCollectionChangedEventArgs e)
    {
        NodeChildrenChanged(sender, e);
    }

    private void NodeChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (sender == null)
            return;

        var parent = (ITreeNode)sender;
        var indexOfParent = IndexOfNode(parent);

        if (indexOfParent < 0)
            return;

        var flatParent = _flatTree[indexOfParent];

        if (!IsExpanded(parent))
            return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                HandleAdd();
                break;
            case NotifyCollectionChangedAction.Remove:
                HandleRemove();
                break;
            case NotifyCollectionChangedAction.Replace:
                HandleReplace();
                break;
            case NotifyCollectionChangedAction.Move:
                HandleMove();
                break;
            case NotifyCollectionChangedAction.Reset:
                HandleReset();
                break;
        }

        void HandleAdd()
        {
            if (e.NewItems == null || e.NewItems.Count == 0)
                return;

            var startIndex = indexOfParent + 1 + CountExpandedChildren(parent, e.NewStartingIndex);
            var insertIndex = startIndex;
            for (int i = 0; i < e.NewItems.Count; i++)
            {
                insertIndex += InsertNode(flatParent.Node.Children[e.NewStartingIndex + i], flatParent.Level + 1, insertIndex);
            }

            var addedCount = insertIndex - startIndex;
            if (addedCount <= 0)
                return;

            var newItems = _flatTree.GetRange(startIndex, addedCount);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newItems, startIndex));
        }

        void HandleRemove()
        {
            if (e.OldItems == null || e.OldItems.Count == 0)
                return;

            var startIndex = indexOfParent + 1 + CountExpandedChildren(parent, e.OldStartingIndex);
            var removeCount = CalculateRemoveCount(startIndex, e.OldItems.Count);
            if (removeCount <= 0)
                return;

            var removedItems = _flatTree.GetRange(startIndex, removeCount);
            foreach (var item in removedItems)
            {
                UnsubscribeFromNode(item.Node);
            }

            _flatTree.RemoveRange(startIndex, removeCount);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removedItems, startIndex));
        }

        void HandleReplace()
        {
            if (e.OldItems == null || e.NewItems == null)
                return;

            var startIndex = indexOfParent + 1 + CountExpandedChildren(parent, e.OldStartingIndex);
            var removeCount = CalculateRemoveCount(startIndex, e.OldItems.Count);
            var removedItems = removeCount > 0
                ? _flatTree.GetRange(startIndex, removeCount)
                : new List<FlatTreeNode>();

            if (removeCount > 0)
            {
                foreach (var item in removedItems)
                    UnsubscribeFromNode(item.Node);

                _flatTree.RemoveRange(startIndex, removeCount);
            }

            var insertIndex = startIndex;
            for (int i = 0; i < e.NewItems.Count; i++)
            {
                insertIndex += InsertNode(flatParent.Node.Children[e.NewStartingIndex + i], flatParent.Level + 1, insertIndex);
            }

            var addedCount = insertIndex - startIndex;
            IList newItems = addedCount > 0
                ? _flatTree.GetRange(startIndex, addedCount)
                : Array.Empty<FlatTreeNode>();

            if (removedItems.Count == 0 && newItems.Count == 0)
                return;

            if (removedItems.Count == 0)
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, newItems, startIndex));
            }
            else if (newItems.Count == 0)
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removedItems, startIndex));
            }
            else
            {
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, newItems, removedItems, startIndex));
            }
        }

        void HandleMove()
        {
            if (e.OldItems == null || e.OldItems.Count == 0)
                return;

            var firstMovedNode = (ITreeNode)e.OldItems[0]!;
            var removeIndex = IndexOfNode(firstMovedNode);
            if (removeIndex < 0)
                return;

            var moveCount = CalculateRemoveCount(removeIndex, e.OldItems.Count);
            if (moveCount <= 0)
                return;

            var movedItems = _flatTree.GetRange(removeIndex, moveCount);
            _flatTree.RemoveRange(removeIndex, moveCount);

            var insertIndex = indexOfParent + 1 + CountExpandedChildren(parent, e.NewStartingIndex);
            if (insertIndex < 0)
                insertIndex = 0;
            else if (insertIndex > _flatTree.Count)
                insertIndex = _flatTree.Count;

            _flatTree.InsertRange(insertIndex, movedItems);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, movedItems, insertIndex, removeIndex));
        }

        void HandleReset()
        {
            var startIndex = indexOfParent + 1;
            var removeCount = 0;
            while (startIndex + removeCount < _flatTree.Count && _flatTree[startIndex + removeCount].Level > flatParent.Level)
            {
                var node = _flatTree[startIndex + removeCount].Node;
                if (IsExpanded(node))
                    removeCount += CountExpandedChildren(node);
                removeCount++;
            }

            if (removeCount > 0)
            {
                var removedItems = _flatTree.GetRange(startIndex, removeCount);
                foreach (var item in removedItems)
                    UnsubscribeFromNode(item.Node);

                _flatTree.RemoveRange(startIndex, removeCount);
            }

            InsertChildren(flatParent, startIndex);
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        int CalculateRemoveCount(int startIndex, int itemsCount)
        {
            var count = 0;
            for (int i = 0; i < itemsCount; i++)
            {
                if (startIndex + count >= _flatTree.Count)
                    break;

                var child = _flatTree[startIndex + count].Node;
                if (IsExpanded(child))
                    count += CountExpandedChildren(child);
                count++;
            }

            return count;
        }
    }

    public IEnumerator<FlatTreeNode> GetEnumerator() => _flatTree.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(FlatTreeNode item) => throw new InvalidOperationException();

    public int Add(object? value) => throw new InvalidOperationException();

    public void Clear() => throw new InvalidOperationException();

    public bool Contains(object? value) => _flatTree.Contains(value);

    public int IndexOf(object? value) => value is FlatTreeNode node ? _flatTree.IndexOf(node) : -1;

    public void Insert(int index, object? value) => throw new InvalidOperationException();

    public void Remove(object? value) => throw new InvalidOperationException();

    public bool Contains(FlatTreeNode item) => _flatTree.Contains(item);

    public void CopyTo(FlatTreeNode[] array, int arrayIndex) => _flatTree.CopyTo(array, arrayIndex);

    public bool Remove(FlatTreeNode item) => throw new InvalidOperationException();

    public void CopyTo(Array array, int index) => _flatTree.CopyTo((FlatTreeNode[])array, index);

    public int Count => _flatTree.Count;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public bool IsReadOnly => true;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new InvalidOperationException();
    }

    public int IndexOf(FlatTreeNode item) => _flatTree.IndexOf(item);

    public void Insert(int index, FlatTreeNode item) => throw new InvalidOperationException();

    public void RemoveAt(int index) => throw new InvalidOperationException();

    public bool IsFixedSize => false;

    public FlatTreeNode this[int index]
    {
        get => _flatTree[index];
        set => throw new InvalidOperationException();
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
}
