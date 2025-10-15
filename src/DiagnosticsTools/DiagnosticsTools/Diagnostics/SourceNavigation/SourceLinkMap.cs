using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Avalonia.Diagnostics.SourceNavigation
{
    internal sealed class SourceLinkMap
    {
        private readonly List<Entry> _entries;

        private SourceLinkMap(List<Entry> entries)
        {
            _entries = entries;
        }

        public static SourceLinkMap? TryLoad(ReadOnlySpan<byte> jsonPayload)
        {
            if (jsonPayload.IsEmpty)
            {
                return null;
            }

            try
            {
                var buffer = new byte[jsonPayload.Length];
                jsonPayload.CopyTo(buffer);
                using var doc = JsonDocument.Parse(buffer);
                if (!doc.RootElement.TryGetProperty("documents", out var documents))
                {
                    return null;
                }

                var entries = new List<Entry>();

                foreach (var property in documents.EnumerateObject())
                {
                    var path = Normalize(property.Name);
                    var url = property.Value.GetString();
                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    entries.Add(new Entry(path, url!));
                }

                entries.Sort(static (a, b) => b.Path.Length.CompareTo(a.Path.Length));
                return entries.Count == 0 ? null : new SourceLinkMap(entries);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public Uri? TryResolve(string documentPath)
        {
            if (string.IsNullOrEmpty(documentPath))
            {
                return null;
            }

            var normalized = Normalize(documentPath);

            foreach (var entry in _entries)
            {
                if (TryMatch(entry.Path, normalized, out var suffix))
                {
                    var resolved = ReplaceWildcard(entry.Url, suffix);
                    return Uri.TryCreate(resolved, UriKind.Absolute, out var uri) ? uri : null;
                }
            }

            return null;
        }

        private static string ReplaceWildcard(string url, string suffix)
        {
            string result;

            if (string.IsNullOrEmpty(suffix))
            {
                result = url.Replace("*", string.Empty);
#if NETSTANDARD2_0
                result = result.Replace("%2A", string.Empty);
                result = result.Replace("%2a", string.Empty);
#else
                result = result.Replace("%2A", string.Empty, StringComparison.OrdinalIgnoreCase);
#endif
                return result;
            }

            var encoded = EncodePathForUrl(suffix);
#if NETSTANDARD2_0
            result = url.Replace("*", encoded);
            result = result.Replace("%2A", encoded);
            result = result.Replace("%2a", encoded);
#else
            result = url.Replace("*", encoded, StringComparison.Ordinal);
            result = result.Replace("%2A", encoded, StringComparison.OrdinalIgnoreCase);
#endif
            return result;
        }

        private static string EncodePathForUrl(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var segments = path.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (segments[i].Length == 0)
                {
                    continue;
                }

                segments[i] = Uri.EscapeDataString(segments[i]);
            }

            return string.Join("/", segments);
        }

        private static bool TryMatch(string pattern, string candidate, out string suffix)
        {
            suffix = string.Empty;

            var wildcardIndex = pattern.IndexOf('*');
            if (wildcardIndex < 0)
            {
                if (string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }

            var prefix = pattern.Substring(0, wildcardIndex);
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suffixPattern = pattern.Substring(wildcardIndex + 1);
            if (!candidate.EndsWith(suffixPattern, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var start = prefix.Length;
            var length = candidate.Length - start - suffixPattern.Length;

            if (length < 0)
            {
                return false;
            }

            suffix = candidate.Substring(start, length);
            return true;
        }

        private static string Normalize(string path)
        {
            return path.Replace('\\', '/');
        }

        private readonly record struct Entry(string Path, string Url);
    }
}
