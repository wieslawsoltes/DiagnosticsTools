using System;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class MutationCompletedEventArgs : EventArgs
    {
        public MutationCompletedEventArgs(ChangeEnvelope envelope, ChangeDispatchResult result)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Result = result;
        }

        public ChangeEnvelope Envelope { get; }

        public ChangeDispatchResult Result { get; }
    }
}

