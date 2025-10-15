using System.Collections.Generic;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class XamlMutationJournal
    {
        private readonly Stack<MutationEntry> _undo = new();
        private readonly Stack<MutationEntry> _redo = new();

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

        public void PushUndo(MutationEntry entry)
        {
            _undo.Push(entry);
        }
    }

    internal readonly record struct MutationEntry(string Path, string Before, string After, ChangeEnvelope Envelope);
}

