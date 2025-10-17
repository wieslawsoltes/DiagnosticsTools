using System;

namespace Avalonia.Diagnostics.SourceNavigation
{
    public enum SourceOrigin
    {
        Unknown = 0,
        Local,
        SourceLink,
        Generated,
    }

    public sealed record SourceInfo(
        string? LocalPath,
        Uri? RemoteUri,
        int? StartLine,
        int? StartColumn,
        int? EndLine,
        int? EndColumn,
        SourceOrigin Origin)
    {
        public string DisplayPath => LocalPath ?? RemoteUri?.ToString() ?? string.Empty;

        public bool HasLocation => StartLine.HasValue && StartLine.Value > 0;
    }
}
