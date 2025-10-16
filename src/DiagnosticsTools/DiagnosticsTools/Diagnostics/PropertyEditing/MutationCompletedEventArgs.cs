using System;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class MutationCompletedEventArgs : EventArgs
    {
        public MutationCompletedEventArgs(
            ChangeEnvelope envelope,
            ChangeDispatchResult result,
            MutationProvenance provenance = MutationProvenance.Unknown)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Result = result;
            Provenance = provenance;
        }

        public ChangeEnvelope Envelope { get; }

        public ChangeDispatchResult Result { get; }

        public MutationProvenance Provenance { get; }
    }
}
