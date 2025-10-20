using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.SourceNavigation;

namespace Avalonia.Diagnostics.Services
{
    public interface ITemplateOverrideService
    {
        Task<TemplateOverrideResult> CreateLocalOverrideAsync(SourceInfo? context, TemplatePreviewRequest request, CancellationToken cancellationToken = default);
    }

    public sealed class TemplateOverrideService : ITemplateOverrideService
    {
        public async Task<TemplateOverrideResult> CreateLocalOverrideAsync(SourceInfo? context, TemplatePreviewRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!request.IsReadOnly)
            {
                return new TemplateOverrideResult(false, null, false, "Template is already writable. No override required.");
            }

            if (string.IsNullOrEmpty(request.SnapshotText))
            {
                return new TemplateOverrideResult(false, null, false, "Template content is unavailable. Refresh the preview and try again.");
            }

            var basePath = context?.LocalPath ?? request.DocumentPath;
            string targetDirectory;

            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var directory = Path.GetDirectoryName(basePath);
                targetDirectory = string.IsNullOrEmpty(directory)
                    ? Path.Combine(Environment.CurrentDirectory, "TemplateOverrides")
                    : Path.Combine(directory, "TemplateOverrides");
            }
            else
            {
                targetDirectory = Path.Combine(Environment.CurrentDirectory, "TemplateOverrides");
            }

            Directory.CreateDirectory(targetDirectory);

            var fileName = BuildOverrideFileName(request);
            var destinationPath = Path.Combine(targetDirectory, fileName);
            var uniquePath = EnsureUniquePath(destinationPath);

            await WriteTextAsync(uniquePath, request.SnapshotText, cancellationToken).ConfigureAwait(false);

            var includeAdded = false;
            if (!string.IsNullOrWhiteSpace(basePath) && File.Exists(basePath))
            {
                includeAdded = TryInsertResourceInclude(basePath, uniquePath);
            }

            var message = includeAdded
                ? $"Created override '{uniquePath}' and added a <ResourceInclude> entry to '{basePath}'."
                : $"Created override '{uniquePath}'. Add a <ResourceInclude> pointing to this file to activate it.";

            return new TemplateOverrideResult(true, uniquePath, includeAdded, message);
        }

        private static string BuildOverrideFileName(TemplatePreviewRequest request)
        {
            var ownerName = request.OwnerDisplayName;
            if (string.IsNullOrWhiteSpace(ownerName))
            {
                ownerName = request.Binding.Owner.LocalName;
            }

            var propertyName = request.Binding.PropertyName ?? request.Binding.RawProperty;
            var candidateName = $"{ownerName}-{propertyName}-Override.axaml";

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                candidateName = candidateName.Replace(invalid, '_');
            }

            return candidateName;
        }

        private static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            var index = 1;

            string candidate;
            do
            {
                candidate = Path.Combine(directory, $"{fileName}-{index}{extension}");
                index++;
            }
            while (File.Exists(candidate));

            return candidate;
        }

        private static bool TryInsertResourceInclude(string documentPath, string overridePath)
        {
            try
            {
                var text = File.ReadAllText(documentPath);
                var includeLine = BuildResourceIncludeLine(documentPath, overridePath);
                if (text.IndexOf(includeLine, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }

                const string insertionToken = "</ResourceDictionary>";
                var index = text.LastIndexOf(insertionToken, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    return false;
                }

                var builder = new StringBuilder(text.Length + includeLine.Length + Environment.NewLine.Length);
                builder.Append(text, 0, index);
                builder.AppendLine(includeLine);
                builder.Append(text, index, text.Length - index);

                File.WriteAllText(documentPath, builder.ToString());
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildResourceIncludeLine(string documentPath, string overridePath)
        {
            try
            {
                var baseDirectory = Path.GetDirectoryName(documentPath) ?? Environment.CurrentDirectory;
                var relative = GetRelativePath(baseDirectory, overridePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');

                if (!relative.StartsWith("./", StringComparison.Ordinal) && !relative.StartsWith("../", StringComparison.Ordinal))
                {
                    relative = "./" + relative;
                }

                return $"  <ResourceInclude Source=\"{relative}\" />";
            }
            catch
            {
                return $"  <ResourceInclude Source=\"{overridePath}\" />";
            }
        }

        private static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(content).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }

        private static string GetRelativePath(string baseDirectory, string targetPath)
        {
            try
            {
                var baseUri = new Uri(AppendDirectorySeparator(baseDirectory));
                var targetUri = new Uri(targetPath);

                if (baseUri.Scheme != targetUri.Scheme)
                {
                    return targetPath;
                }

                var relativeUri = baseUri.MakeRelativeUri(targetUri);
                var relative = Uri.UnescapeDataString(relativeUri.ToString());

                if (string.Equals(targetUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative.Replace('/', Path.DirectorySeparatorChar);
                }

                return relative;
            }
            catch
            {
                return targetPath;
            }
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Path.DirectorySeparatorChar.ToString();
            }

            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
                !path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return path + Path.DirectorySeparatorChar;
            }

            return path;
        }
    }

    public readonly record struct TemplateOverrideResult(bool Success, string? FilePath, bool IncludeAdded, string Message);
}
