using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    private readonly Stack<PointerGestureSession> _gestureStack = new();

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

        if (_gestureStack.Count > 0)
        {
            _gestureStack.Peek().RegisterChange(target, property, oldValue, newValue);
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
    /// Begins a pointer gesture session that coalesces subsequent property changes into a single undo unit.
    /// </summary>
    /// <returns>A disposable session that must be completed or cancelled.</returns>
    public PointerGestureSession BeginPointerGesture()
    {
        Dispatcher.UIThread.VerifyAccess();

        var session = new PointerGestureSession(this);
        _gestureStack.Push(session);
        return session;
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

    private void CompletePointerGesture(PointerGestureSession session)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (!TryPopGesture(session))
        {
            return;
        }

        var snapshot = session.CloseForCommit();
        if (snapshot.Count == 0)
        {
            return;
        }

        var mutation = new PointerGestureMutation(snapshot);
        if (!mutation.HasChanges)
        {
            return;
        }

        _undo.Push(mutation);
        _redo.Clear();
    }

    private void CancelPointerGesture(PointerGestureSession session)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (!TryPopGesture(session))
        {
            return;
        }

        session.CloseWithoutCommit();
    }

    private void DisposePointerGesture(PointerGestureSession session)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (session.IsActive)
        {
            CancelPointerGesture(session);
        }
    }

    private bool TryPopGesture(PointerGestureSession session)
    {
        if (_gestureStack.Count == 0)
        {
            return false;
        }

        if (!ReferenceEquals(_gestureStack.Peek(), session))
        {
            throw new InvalidOperationException("Pointer gestures must be closed in the order they were opened.");
        }

        _gestureStack.Pop();
        return true;
    }

    private interface IRuntimeMutation
    {
        void ApplyUndo();
        void ApplyRedo();
    }

    /// <summary>
    /// Represents a pointer gesture session that groups multiple property changes into a single mutation.
    /// </summary>
    public sealed class PointerGestureSession : IDisposable
    {
        private readonly RuntimeMutationCoordinator _owner;
        private readonly Dictionary<PropertyKey, GestureEntry> _entries = new();
        private PointerGestureState _state;

        internal PointerGestureSession(RuntimeMutationCoordinator owner)
        {
            _owner = owner;
            _state = PointerGestureState.Active;
        }

        internal bool IsActive => _state == PointerGestureState.Active;

        internal IReadOnlyCollection<GestureEntry> CloseForCommit()
        {
            if (_state != PointerGestureState.Active)
            {
                return Array.Empty<GestureEntry>();
            }

            _state = PointerGestureState.Completed;

            if (_entries.Count == 0)
            {
                return Array.Empty<GestureEntry>();
            }

            var list = new List<GestureEntry>(_entries.Count);
            foreach (var entry in _entries.Values)
            {
                if (entry.IsMeaningful)
                {
                    list.Add(entry);
                }
            }

            _entries.Clear();
            return list;
        }

        internal void CloseWithoutCommit()
        {
            if (_state != PointerGestureState.Active)
            {
                return;
            }

            _entries.Clear();
            _state = PointerGestureState.Cancelled;
        }

        internal void RegisterChange(
            AvaloniaObject target,
            AvaloniaProperty property,
            object? oldValue,
            object? newValue)
        {
            if (_state != PointerGestureState.Active)
            {
                throw new InvalidOperationException("Pointer gesture session is no longer active.");
            }

            var key = new PropertyKey(target, property);
            if (_entries.TryGetValue(key, out var existing))
            {
                existing.UpdateNewValue(newValue);
                return;
            }

            if (Equals(oldValue, newValue))
            {
                return;
            }

            _entries.Add(key, new GestureEntry(target, property, oldValue, newValue));
        }

        /// <summary>
        /// Commits the pointer gesture, coalescing recorded property changes into a single undo unit.
        /// </summary>
        public void Complete()
        {
            if (_state != PointerGestureState.Active)
            {
                return;
            }

            _owner.CompletePointerGesture(this);
        }

        /// <summary>
        /// Cancels the pointer gesture, discarding any recorded property changes.
        /// </summary>
        public void Cancel()
        {
            if (_state != PointerGestureState.Active)
            {
                return;
            }

            _owner.CancelPointerGesture(this);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_state == PointerGestureState.Active)
            {
                _owner.DisposePointerGesture(this);
            }
        }

        private readonly struct PropertyKey : IEquatable<PropertyKey>
        {
            public PropertyKey(AvaloniaObject target, AvaloniaProperty property)
            {
                Target = target;
                Property = property;
            }

            public AvaloniaObject Target { get; }
            public AvaloniaProperty Property { get; }

            public bool Equals(PropertyKey other) =>
                ReferenceEquals(Target, other.Target) && ReferenceEquals(Property, other.Property);

            public override bool Equals(object? obj) => obj is PropertyKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = (hash * 31) + RuntimeHelpers.GetHashCode(Target);
                    hash = (hash * 31) + Property.GetHashCode();
                    return hash;
                }
            }
        }

        internal sealed class GestureEntry
        {
            public GestureEntry(AvaloniaObject target, AvaloniaProperty property, object? originalValue, object? currentValue)
            {
                Target = target;
                Property = property;
                OriginalValue = originalValue;
                CurrentValue = currentValue;
            }

            public AvaloniaObject Target { get; }
            public AvaloniaProperty Property { get; }
            public object? OriginalValue { get; }
            public object? CurrentValue { get; private set; }

            public bool IsMeaningful => !Equals(OriginalValue, CurrentValue);

            public void UpdateNewValue(object? value) => CurrentValue = value;
        }

        private enum PointerGestureState
        {
            Active,
            Completed,
            Cancelled
        }
    }

    private sealed class PointerGestureMutation : IRuntimeMutation
    {
        private readonly GestureStep[] _steps;

        public PointerGestureMutation(IEnumerable<PointerGestureSession.GestureEntry> entries)
        {
            if (entries is null)
            {
                _steps = Array.Empty<GestureStep>();
                return;
            }

            var list = new List<GestureStep>();
            foreach (var entry in entries)
            {
                if (!entry.IsMeaningful)
                {
                    continue;
                }

                list.Add(new GestureStep(entry.Target, entry.Property, entry.OriginalValue, entry.CurrentValue));
            }

            _steps = list.ToArray();
        }

        public bool HasChanges => _steps.Length > 0;

        public void ApplyUndo()
        {
            for (var i = 0; i < _steps.Length; i++)
            {
                var step = _steps[i];
                if (step.Target.TryGetTarget(out var target))
                {
                    SetValue(target, step.Property, step.OldValue);
                }
            }
        }

        public void ApplyRedo()
        {
            for (var i = 0; i < _steps.Length; i++)
            {
                var step = _steps[i];
                if (step.Target.TryGetTarget(out var target))
                {
                    SetValue(target, step.Property, step.NewValue);
                }
            }
        }

        private readonly struct GestureStep
        {
            public GestureStep(AvaloniaObject target, AvaloniaProperty property, object? oldValue, object? newValue)
            {
                Target = new WeakReference<AvaloniaObject>(target);
                Property = property;
                OldValue = oldValue;
                NewValue = newValue;
            }

            public WeakReference<AvaloniaObject> Target { get; }
            public AvaloniaProperty Property { get; }
            public object? OldValue { get; }
            public object? NewValue { get; }
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
