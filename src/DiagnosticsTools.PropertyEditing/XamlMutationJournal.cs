using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class XamlMutationJournal
    {
        private readonly Stack<MutationEntry> _undo = new();
        private readonly Stack<MutationEntry> _redo = new();
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public void Record(MutationEntry entry)
        {
            _undo.Push(entry);
            _redo.Clear();
        }

        public bool TryPopUndo(out MutationEntry entry)
        {
            if (_undo.Count == 0)
            {
                entry = default;
                return false;
            }

            entry = _undo.Pop();
            return true;
        }

        public bool TryPeekUndo(out MutationEntry entry)
        {
            if (_undo.Count == 0)
            {
                entry = default;
                return false;
            }

            entry = _undo.Peek();
            return true;
        }

        public void PushRedo(MutationEntry entry)
        {
            _redo.Push(entry);
        }

        public bool TryPopRedo(out MutationEntry entry)
        {
            if (_redo.Count == 0)
            {
                entry = default;
                return false;
            }

            entry = _redo.Pop();
            return true;
        }

        public bool TryPeekRedo(out MutationEntry entry)
        {
            if (_redo.Count == 0)
            {
                entry = default;
                return false;
            }

            entry = _redo.Peek();
            return true;
        }

        public void PushUndo(MutationEntry entry)
        {
            _undo.Push(entry);
        }

        public MutationEntry[] GetUndoSnapshot() => _undo.ToArray();

        public MutationEntry[] GetRedoSnapshot() => _redo.ToArray();

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        public void DiscardEntriesForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Clear();
                return;
            }

            RemoveFromStack(_undo, path);
            RemoveFromStack(_redo, path);
        }

        private static void RemoveFromStack(Stack<MutationEntry> stack, string path)
        {
            if (stack.Count == 0)
            {
                return;
            }

            var buffer = new Stack<MutationEntry>(stack.Count);

            while (stack.Count > 0)
            {
                var entry = stack.Pop();
                if (!ContainsPath(entry.Documents, path))
                {
                    buffer.Push(entry);
                }
            }

            while (buffer.Count > 0)
            {
                stack.Push(buffer.Pop());
            }
        }

        private static bool ContainsPath(IReadOnlyList<DocumentMutation> documents, string path)
        {
            if (documents is null || documents.Count == 0)
            {
                return false;
            }

            for (var index = 0; index < documents.Count; index++)
            {
                var documentPath = documents[index].Path;
                if (!string.IsNullOrWhiteSpace(documentPath) && PathComparer.Equals(documentPath, path))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public readonly record struct DocumentMutation(ChangeEnvelope Envelope, string Path, string Before, string After);

    public readonly record struct MutationEntry(IReadOnlyList<DocumentMutation> Documents, DateTimeOffset Timestamp, string? Gesture);
}
