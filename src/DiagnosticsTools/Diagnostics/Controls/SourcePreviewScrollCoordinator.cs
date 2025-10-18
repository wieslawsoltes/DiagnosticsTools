using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalonia.Diagnostics.Controls
{
    public sealed class SourcePreviewScrollCoordinator
    {
        private readonly List<Subscription> _subscriptions = new();
        private bool _isBroadcasting;

        public IDisposable Attach(SourcePreviewEditor editor)
        {
            if (editor is null)
            {
                throw new ArgumentNullException(nameof(editor));
            }

            var subscription = new Subscription(this, editor);
            _subscriptions.Add(subscription);
            return subscription;
        }

        public void RequestSynchronize(SourcePreviewEditor editor)
        {
            if (editor is null)
            {
                throw new ArgumentNullException(nameof(editor));
            }

            var state = editor.GetCurrentScrollState();
            if (!state.IsValid)
            {
                return;
            }

            Broadcast(editor, state);
        }

        private void Broadcast(SourcePreviewEditor source, SourcePreviewScrollState state)
        {
            if (!state.IsValid)
            {
                return;
            }

            if (_isBroadcasting)
            {
                return;
            }

            _isBroadcasting = true;

            try
            {
                foreach (var subscription in _subscriptions.ToArray())
                {
                    if (!subscription.TryGetEditor(out var editor) || editor is null)
                    {
                        subscription.Dispose();
                        continue;
                    }

                    if (ReferenceEquals(editor, source))
                    {
                        continue;
                    }

                    editor.ApplyScrollState(state);
                }
            }
            finally
            {
                _isBroadcasting = false;
            }
        }

        private void Remove(Subscription subscription)
        {
            _subscriptions.Remove(subscription);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly SourcePreviewScrollCoordinator _owner;
            private readonly WeakReference<SourcePreviewEditor> _editor;
            private readonly EventHandler<SourcePreviewScrollChangedEventArgs> _handler;
            private bool _isDisposed;

            public Subscription(SourcePreviewScrollCoordinator owner, SourcePreviewEditor editor)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _editor = new WeakReference<SourcePreviewEditor>(editor ?? throw new ArgumentNullException(nameof(editor)));
                _handler = OnScrollChanged;
                editor.ScrollChanged += _handler;
            }

            public bool TryGetEditor(out SourcePreviewEditor? editor) => _editor.TryGetTarget(out editor);

            private void OnScrollChanged(object? sender, SourcePreviewScrollChangedEventArgs e)
            {
                if (!_editor.TryGetTarget(out var editor))
                {
                    Dispose();
                    return;
                }

                _owner.Broadcast(editor, e.State);
            }

            public void Dispose()
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;

                if (_editor.TryGetTarget(out var editor))
                {
                    editor.ScrollChanged -= _handler;
                }

                _owner.Remove(this);
            }
        }
    }
}
