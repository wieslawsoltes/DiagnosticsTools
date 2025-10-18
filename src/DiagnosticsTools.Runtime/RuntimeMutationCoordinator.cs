using System;
using System.Collections;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.Runtime;

/// <summary>
/// Tracks property mutations applied at runtime so they can be undone or redone.
/// </summary>
public sealed class RuntimeMutationCoordinator
{
    private readonly Stack<IRuntimeMutation> _undo = new();
    private readonly Stack<IRuntimeMutation> _redo = new();

    /// <summary>
    /// Gets whether there are pending mutations that can be undone.
    /// </summary>
    public bool HasPendingMutations => _undo.Count > 0;

    /// <summary>
    /// Clears all tracked mutations.
    /// </summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>
    /// Registers a property change so that it can be undone or redone later.
    /// </summary>
    public void RegisterPropertyChange(
        AvaloniaObject target,
        AvaloniaProperty property,
        object? oldValue,
        object? newValue)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (target is null || property is null)
        {
            return;
        }

        var mutation = new PropertyMutation(target, property, oldValue, newValue);
        if (!mutation.IsMeaningful)
        {
            return;
        }

        _undo.Push(mutation);
        _redo.Clear();
    }

    /// <summary>
    /// Attempts to apply an element removal mutation for the provided node.
    /// </summary>
    public bool TryApplyElementRemoval(IMutableTreeNode node)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (node is null)
        {
            return false;
        }

        if (!ElementRemovalMutation.TryCreate(node, out var mutation))
        {
            return false;
        }

        _undo.Push(mutation);
        _redo.Clear();
        mutation.ApplyRemoval();
        return true;
    }

    /// <summary>
    /// Undoes the most recently applied mutation.
    /// </summary>
    public void ApplyUndo()
    {
        if (_undo.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.VerifyAccess();

        var mutation = _undo.Pop();
        mutation.ApplyUndo();
        _redo.Push(mutation);
    }

    /// <summary>
    /// Redoes the most recently undone mutation.
    /// </summary>
    public void ApplyRedo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.VerifyAccess();

        var mutation = _redo.Pop();
        mutation.ApplyRedo();
        _undo.Push(mutation);
    }

    private interface IRuntimeMutation
    {
        void ApplyUndo();
        void ApplyRedo();
    }

    private sealed class PropertyMutation : IRuntimeMutation
    {
        private readonly WeakReference<AvaloniaObject> _target;
        private readonly AvaloniaProperty _property;
        private readonly object? _oldValue;
        private readonly object? _newValue;

        public PropertyMutation(
            AvaloniaObject target,
            AvaloniaProperty property,
            object? oldValue,
            object? newValue)
        {
            _target = new WeakReference<AvaloniaObject>(target);
            _property = property;
            _oldValue = oldValue;
            _newValue = newValue;
        }

        public bool IsMeaningful
        {
            get
            {
                if (!_target.TryGetTarget(out var target))
                {
                    return false;
                }

                var current = target.GetValue(_property);
                return !Equals(current, _oldValue) || !Equals(current, _newValue);
            }
        }

        public void ApplyUndo()
        {
            if (!_target.TryGetTarget(out var target))
            {
                return;
            }

            SetValue(target, _property, _oldValue);
        }

        public void ApplyRedo()
        {
            if (!_target.TryGetTarget(out var target))
            {
                return;
            }

            SetValue(target, _property, _newValue);
        }

        private static void SetValue(AvaloniaObject target, AvaloniaProperty property, object? value)
        {
            if (ReferenceEquals(value, AvaloniaProperty.UnsetValue))
            {
                target.ClearValue(property);
                return;
            }

            target.SetValue(property, value);
        }
    }

    private sealed class ElementRemovalMutation : IRuntimeMutation
    {
        private enum RemovalKind
        {
            Panel,
            Decorator,
            ContentControl,
            ItemsControl
        }

        private readonly WeakReference<AvaloniaObject> _parentRef;
        private readonly AvaloniaObject _element;
        private readonly RemovalKind _kind;
        private readonly int _index;
        private readonly object? _storedItem;

        private ElementRemovalMutation(
            AvaloniaObject parent,
            AvaloniaObject element,
            RemovalKind kind,
            int index,
            object? storedItem)
        {
            _parentRef = new WeakReference<AvaloniaObject>(parent);
            _element = element;
            _kind = kind;
            _index = index;
            _storedItem = storedItem;
        }

        public static bool TryCreate(IMutableTreeNode node, out ElementRemovalMutation mutation)
        {
            mutation = default!;

            var parentNode = node.Parent;
            if (parentNode?.Visual is not AvaloniaObject parent)
            {
                return false;
            }

            var element = node.Visual;

            switch (parent)
            {
                case Panel panel when element is Control control:
                    var children = panel.Children;
                    var index = children.IndexOf(control);
                    if (index < 0)
                    {
                        return false;
                    }

                    mutation = new ElementRemovalMutation(panel, control, RemovalKind.Panel, index, null);
                    return true;

                case Decorator decorator when ReferenceEquals(decorator.Child, element):
                    mutation = new ElementRemovalMutation(
                        decorator,
                        element,
                        RemovalKind.Decorator,
                        0,
                        decorator.Child);
                    return true;

                case ContentControl contentControl when ReferenceEquals(contentControl.Content, element):
                    mutation = new ElementRemovalMutation(
                        contentControl,
                        element,
                        RemovalKind.ContentControl,
                        0,
                        contentControl.Content);
                    return true;

                case ItemsControl itemsControl when element is Control container && itemsControl.ItemsSource is null:
                {
                    var generator = itemsControl.ItemContainerGenerator;
#pragma warning disable CS0618
                    var containerIndex = generator.IndexFromContainer(container);
#pragma warning restore CS0618
                    if (containerIndex < 0 && itemsControl.Items is IList list)
                    {
                        containerIndex = list.IndexOf(container);
                        if (containerIndex >= 0)
                        {
                            var stored = list[containerIndex];
                            mutation = new ElementRemovalMutation(
                                itemsControl,
                                container,
                                RemovalKind.ItemsControl,
                                containerIndex,
                                stored);
                            return true;
                        }
                    }
                    else if (containerIndex >= 0 && itemsControl.Items is IList items)
                    {
                        var stored = items[containerIndex];
                        mutation = new ElementRemovalMutation(
                            itemsControl,
                            container,
                            RemovalKind.ItemsControl,
                            containerIndex,
                            stored);
                        return true;
                    }

                    break;
                }
            }

            return false;
        }

        public void ApplyRemoval()
        {
            if (!_parentRef.TryGetTarget(out var parent))
            {
                return;
            }

            switch (_kind)
            {
                case RemovalKind.Panel when parent is Panel panel && _element is Control control:
                    if (panel.Children.Contains(control))
                    {
                        panel.Children.Remove(control);
                    }
                    break;

                case RemovalKind.Decorator when parent is Decorator decorator:
                    if (ReferenceEquals(decorator.Child, _element))
                    {
                        decorator.Child = null;
                    }
                    break;

                case RemovalKind.ContentControl when parent is ContentControl contentControl:
                    if (ReferenceEquals(contentControl.Content, _element))
                    {
                        contentControl.Content = null;
                    }
                    break;

                case RemovalKind.ItemsControl when parent is ItemsControl itemsControl
                                                  && itemsControl.Items is IList list
                                                  && _index >= 0
                                                  && _index < list.Count:
                    list.RemoveAt(_index);
                    break;
            }
        }

        public void ApplyUndo()
        {
            if (!_parentRef.TryGetTarget(out var parent))
            {
                return;
            }

            switch (_kind)
            {
                case RemovalKind.Panel when parent is Panel panel && _element is Control control:
                    var insertIndex = Clamp(_index, 0, panel.Children.Count);
                    if (!panel.Children.Contains(control))
                    {
                        panel.Children.Insert(insertIndex, control);
                    }
                    break;

                case RemovalKind.Decorator when parent is Decorator decorator:
                    decorator.Child = _storedItem as Control ?? _element as Control;
                    break;

                case RemovalKind.ContentControl when parent is ContentControl contentControl:
                    contentControl.Content = _storedItem ?? _element;
                    break;

                case RemovalKind.ItemsControl when parent is ItemsControl itemsControl
                                                  && itemsControl.Items is IList list:
                    var insert = Clamp(_index, 0, list.Count);
                    if (_storedItem is not null)
                    {
                        list.Insert(insert, _storedItem);
                    }
                    else
                    {
                        list.Insert(insert, _element);
                    }
                    break;
            }
        }

        public void ApplyRedo()
        {
            ApplyRemoval();
        }
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
}

