using System;
using System.Collections.Generic;

namespace Avalonia.Diagnostics.PropertyEditing
{
    public sealed class ChangeBatch
    {
        public Guid BatchId { get; init; }

        public DateTimeOffset InitiatedAt { get; init; }

        public ChangeSourceInfo Source { get; init; } = new();

        public IReadOnlyList<ChangeEnvelope> Documents { get; init; } = Array.Empty<ChangeEnvelope>();
    }
}
