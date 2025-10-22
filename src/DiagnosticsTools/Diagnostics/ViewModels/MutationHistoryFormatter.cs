using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Diagnostics.PropertyEditing;

namespace Avalonia.Diagnostics.ViewModels;

internal static class MutationHistoryFormatter
{
    public static string BuildSummary(string prefix, in MutationEntry entry)
    {
        var documentsSummary = BuildDocumentsSummary(entry, out var count);
        return $"{prefix} ({count} file{(count == 1 ? string.Empty : "s")}): {documentsSummary}";
    }

    public static string BuildDocumentsSummary(in MutationEntry entry)
        => BuildDocumentsSummary(entry, out _);

    private static string BuildDocumentsSummary(in MutationEntry entry, out int count)
    {
        if (entry.Documents is not { Count: > 0 })
        {
            count = 0;
            return "No documents";
        }

        var documents = entry.Documents;
        count = documents.Count;

        var documentNames = documents
            .Select(d => !string.IsNullOrWhiteSpace(d.Path)
                ? Path.GetFileName(d.Path)
                : (d.Envelope?.Document.Path is { Length: > 0 } path ? Path.GetFileName(path) : "Document"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (documentNames.Length == 0)
        {
            return "Document";
        }

        if (documentNames.Length <= 3)
        {
            return string.Join(", ", documentNames);
        }

        return string.Join(", ", documentNames.Take(3)) + ", …";
    }
}
