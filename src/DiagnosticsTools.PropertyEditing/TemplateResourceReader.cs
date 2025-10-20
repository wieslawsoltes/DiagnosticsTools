using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.Xaml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal static class TemplateResourceReader
    {
        private static readonly HttpClient ExternalHttpClient = new();
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public static async ValueTask<TemplateResourceInfo?> TryReadAsync(
            XamlAstWorkspace workspace,
            string? baseDocumentPath,
            string source,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            var mappedPath = TryMapAvaresToLocalPath(baseDocumentPath, source);
            if (!string.IsNullOrEmpty(mappedPath) && File.Exists(mappedPath))
            {
                return await ReadLocalDocumentAsync(workspace, mappedPath, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(source))
            {
                return await ReadLocalDocumentAsync(workspace, source, cancellationToken).ConfigureAwait(false);
            }

            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                if (!string.IsNullOrWhiteSpace(baseDocumentPath))
                {
                    var candidate = Path.Combine(Path.GetDirectoryName(baseDocumentPath) ?? string.Empty, source);
                    if (File.Exists(candidate))
                    {
                        return await ReadLocalDocumentAsync(workspace, candidate, cancellationToken).ConfigureAwait(false);
                    }
                }

                return null;
            }

            if (string.Equals(uri.Scheme, "avares", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyName = uri.Authority;
                var relativePath = uri.AbsolutePath.TrimStart('/');
                var text = LoadEmbeddedResourceText(assemblyName, relativePath);
                return text is null
                    ? null
                    : TemplateResourceInfo.FromEmbedded(uri, text, assemblyName);
            }

            if (string.Equals(uri.Scheme, "resm", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyName = ExtractResmAssemblyName(uri) ?? uri.Authority;
                var resourcePath = uri.AbsolutePath.TrimStart('/');
                var text = LoadEmbeddedResourceText(assemblyName, resourcePath);
                return text is null
                    ? null
                    : TemplateResourceInfo.FromEmbedded(uri, text, assemblyName);
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(uri.LocalPath))
                {
                    return await ReadLocalDocumentAsync(workspace, uri.LocalPath, cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var response = await ExternalHttpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return TemplateResourceInfo.FromExternal(uri, text);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static async ValueTask<TemplateResourceInfo> ReadLocalDocumentAsync(
            XamlAstWorkspace workspace,
            string path,
            CancellationToken cancellationToken)
        {
            var normalized = NormalizePath(path);
            try
            {
                var document = await workspace.GetDocumentAsync(normalized, cancellationToken).ConfigureAwait(false);
                return TemplateResourceInfo.FromDocument(document, normalized);
            }
            catch
            {
                using var reader = new StreamReader(normalized, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                return TemplateResourceInfo.FromFile(normalized, text);
            }
        }

        private static string? LoadEmbeddedResourceText(string? assemblyName, string? resourcePath)
        {
            if (string.IsNullOrEmpty(assemblyName) || string.IsNullOrEmpty(resourcePath))
            {
                return null;
            }

            Assembly? assembly = null;

            foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = candidate.GetName().Name;
                if (string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    assembly = candidate;
                    break;
                }
            }

            if (assembly is null)
            {
                try
                {
                    assembly = Assembly.Load(new AssemblyName(assemblyName));
                }
                catch
                {
                    return null;
                }
            }

            var manifestPath = resourcePath.Replace('/', '.');
            var names = assembly.GetManifestResourceNames();
            var match = names.FirstOrDefault(n => n.EndsWith(manifestPath, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(match);
            if (stream is null)
            {
                return null;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }

        internal static string? ExtractResmAssemblyName(Uri uri)
        {
            var query = uri.Query;
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            var trimmed = query.TrimStart('?');
            var start = 0;
            while (start < trimmed.Length)
            {
                var ampIndex = trimmed.IndexOf('&', start);
                var segment = ampIndex >= 0 ? trimmed.Substring(start, ampIndex - start) : trimmed.Substring(start);
                if (!string.IsNullOrEmpty(segment))
                {
                    var equalsIndex = segment.IndexOf('=');
                    string key;
                    string value;
                    if (equalsIndex >= 0)
                    {
                        key = segment.Substring(0, equalsIndex);
                        value = segment.Substring(equalsIndex + 1);
                    }
                    else
                    {
                        key = segment;
                        value = string.Empty;
                    }

                    if (key.Equals("assembly", StringComparison.OrdinalIgnoreCase))
                    {
                        return Uri.UnescapeDataString(value);
                    }
                }

            start = ampIndex < 0 ? trimmed.Length : ampIndex + 1;
            }

            return null;
        }

        internal static string? TryMapAvaresToLocalPath(string? baseDocumentPath, string? source)
        {
            if (string.IsNullOrWhiteSpace(baseDocumentPath) || string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, "avares", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var assemblyName = uri.Authority;
            var relativePath = uri.AbsolutePath.TrimStart('/');
            if (string.IsNullOrEmpty(assemblyName) || string.IsNullOrEmpty(relativePath))
            {
                return null;
            }

            var current = Path.GetDirectoryName(baseDocumentPath);
            while (!string.IsNullOrEmpty(current))
            {
                var folderName = Path.GetFileName(current);
                if (string.Equals(folderName, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(current, relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        return NormalizePath(candidate);
                    }

                    break;
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        public static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }

    internal readonly record struct TemplateResourceInfo(
        bool Success,
        string? Text,
        Uri? SourceUri,
        string? DocumentPath,
        XamlDocumentVersion? Version,
        bool IsReadOnly,
        string ProviderDisplayName,
        string? AssemblyName)
    {
        public static TemplateResourceInfo FromDocument(XamlAstDocument document, string path) =>
            new(true, document.Text, TemplateSourceResolver.TryCreateUri(path), document.Path, document.Version, false, "File", null);

        public static TemplateResourceInfo FromFile(string path, string text) =>
            new(true, text, TemplateSourceResolver.TryCreateUri(path), path, null, false, "File", null);

        public static TemplateResourceInfo FromEmbedded(Uri uri, string text, string? assemblyName) =>
            new(true, text, uri, null, null, true, "Embedded Resource", assemblyName);

        public static TemplateResourceInfo FromExternal(Uri uri, string text) =>
            new(true, text, uri, null, null, true, $"{uri.Scheme.ToUpperInvariant()} Resource", null);
    }
}
