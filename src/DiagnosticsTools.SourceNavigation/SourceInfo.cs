using System;

namespace Avalonia.Diagnostics.SourceNavigation
{
    /// <summary>
    /// Indicates where a source location was retrieved from.
    /// </summary>
    public enum SourceOrigin
    {
        Unknown = 0,
        Local,
        SourceLink,
        Generated,
    }

    /// <summary>
    /// Represents the resolved location of a symbol or diagnostics object.
    /// </summary>
    public sealed record SourceInfo(
        string? LocalPath,
        Uri? RemoteUri,
        int? StartLine,
        int? StartColumn,
        int? EndLine,
        int? EndColumn,
        SourceOrigin Origin)
    {
        /// <summary>
        /// Gets a display-friendly path combining local and remote locations.
        /// </summary>
        public string DisplayPath => LocalPath ?? RemoteUri?.ToString() ?? string.Empty;

        /// <summary>
        /// Gets a value indicating whether the source info includes a concrete location.
        /// </summary>
        public bool HasLocation => StartLine.HasValue && StartLine.Value > 0;
    }
}
