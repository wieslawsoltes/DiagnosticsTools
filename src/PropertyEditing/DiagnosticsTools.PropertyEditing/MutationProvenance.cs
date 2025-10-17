using System;

namespace Avalonia.Diagnostics.PropertyEditing
{
    public enum MutationProvenance
    {
        Unknown,
        PropertyInspector,
        TreeInspector,
        HotReload,
        ExternalDocument
    }

    public static class MutationProvenanceHelper
    {
        public static MutationProvenance FromEnvelope(ChangeEnvelope envelope)
        {
            if (envelope is null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            var inspector = envelope.Source?.Inspector;
            if (string.IsNullOrWhiteSpace(inspector))
            {
                return MutationProvenance.Unknown;
            }

            return inspector switch
            {
                "PropertyEditor" => MutationProvenance.PropertyInspector,
                "TreeView" => MutationProvenance.TreeInspector,
                "HotReload" => MutationProvenance.HotReload,
                _ => MutationProvenance.Unknown
            };
        }
    }
}
