using System;
using Avalonia.Diagnostics.PropertyEditing;
using Xunit;

namespace PropertyEditing.Tests;

public class MutationProvenanceTests
{
    [Fact]
    public void FromEnvelope_IdentifiesTreeInspector()
    {
        var envelope = new ChangeEnvelope
        {
            Source = new ChangeSourceInfo { Inspector = "TreeView" }
        };

        var provenance = MutationProvenanceHelper.FromEnvelope(envelope);

        Assert.Equal(MutationProvenance.TreeInspector, provenance);
    }

    [Fact]
    public void FromEnvelope_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => MutationProvenanceHelper.FromEnvelope(null!));
    }
}
