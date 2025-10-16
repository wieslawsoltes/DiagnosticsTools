using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class XamlMutationDispatcher : IChangeDispatcher
    {
        private readonly XamlAstWorkspace _workspace;
        private readonly Workspace? _roslynWorkspace;
        private readonly XamlMutationJournal _journal;
        private static readonly StringComparison PathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        internal XamlAstWorkspace Workspace => _workspace;

        internal XamlMutationDispatcher(XamlAstWorkspace workspace, Workspace? roslynWorkspace = null)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _roslynWorkspace = roslynWorkspace;
            _journal = new XamlMutationJournal();
        }

        internal event EventHandler<MutationCompletedEventArgs>? MutationCompleted;

        public bool CanUndo => _journal.CanUndo;

        public bool CanRedo => _journal.CanRedo;

        public async ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (string.IsNullOrWhiteSpace(envelope.Document.Path))
            {
                var failure = ChangeDispatchResult.MutationFailure(null, "Document path is missing.");
                OnMutationCompleted(envelope, failure);
                return failure;
            }

            var path = envelope.Document.Path;
            XamlAstDocument document;
            IXamlAstIndex index;

            try
            {
                document = await _workspace.GetDocumentAsync(path, cancellationToken).ConfigureAwait(false);
                index = await _workspace.GetIndexAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var failure = ChangeDispatchResult.MutationFailure(null, $"Failed to load XAML document: {ex.Message}");
                OnMutationCompleted(envelope, failure);
                return failure;
            }

            var currentVersion = document.Version.ToString();
            if (!string.Equals(currentVersion, envelope.Document.Version, StringComparison.Ordinal))
            {
                var versionFailure = ChangeDispatchResult.GuardFailure(null, "Document version mismatch.");
                OnMutationCompleted(envelope, versionFailure);
                return versionFailure;
            }

            var edits = new List<XamlTextEdit>();

            foreach (var change in envelope.Changes)
            {
                if (!XamlMutationEditBuilder.TryBuildEdits(document, index, change, edits, out var failure))
                {
                    OnMutationCompleted(envelope, failure);
                    return failure;
                }
            }

            if (edits.Count == 0)
            {
                var success = ChangeDispatchResult.Success();
                OnMutationCompleted(envelope, success);
                return success;
            }

            string updatedText;

            try
            {
                updatedText = ApplyEdits(document.Text, edits);
            }
            catch (Exception ex)
            {
                var failure = ChangeDispatchResult.MutationFailure(null, $"Failed to apply XAML edits: {ex.Message}");
                OnMutationCompleted(envelope, failure);
                return failure;
            }

            var persistenceResult = await PersistAsync(path, updatedText, cancellationToken).ConfigureAwait(false);
            if (persistenceResult.Status == ChangeDispatchStatus.Success)
            {
                _journal.Record(new MutationEntry(path, document.Text, updatedText, envelope));
            }

            OnMutationCompleted(envelope, persistenceResult);
            return persistenceResult;
        }

        public async ValueTask<ChangeDispatchResult> UndoAsync(CancellationToken cancellationToken = default)
        {
            if (!_journal.TryPopUndo(out var entry))
            {
                return ChangeDispatchResult.MutationFailure(null, "No mutations to undo.");
            }

            var result = await PersistAsync(entry.Path, entry.Before, cancellationToken).ConfigureAwait(false);
            if (result.Status == ChangeDispatchStatus.Success)
            {
                _journal.PushRedo(entry);
            }
            else
            {
                _journal.PushUndo(entry);
            }

            OnMutationCompleted(entry.Envelope, result);
            return result;
        }

        public async ValueTask<ChangeDispatchResult> RedoAsync(CancellationToken cancellationToken = default)
        {
            if (!_journal.TryPopRedo(out var entry))
            {
                return ChangeDispatchResult.MutationFailure(null, "No mutations to redo.");
            }

            var result = await PersistAsync(entry.Path, entry.After, cancellationToken).ConfigureAwait(false);
            if (result.Status == ChangeDispatchStatus.Success)
            {
                _journal.PushUndo(entry);
            }
            else
            {
                _journal.PushRedo(entry);
            }

            OnMutationCompleted(entry.Envelope, result);
            return result;
        }

        private async Task<ChangeDispatchResult> PersistAsync(string path, string content, CancellationToken cancellationToken)
        {
            var (handledByWorkspace, workspaceResult) = await TryPersistWithWorkspaceAsync(path, content, cancellationToken).ConfigureAwait(false);
            if (handledByWorkspace)
            {
                if (workspaceResult.Status != ChangeDispatchStatus.Success)
                {
                    return workspaceResult;
                }

                return await PersistToFileSystemAsync(path, content, cancellationToken).ConfigureAwait(false);
            }

            return await PersistToFileSystemAsync(path, content, cancellationToken).ConfigureAwait(false);
        }

        private async Task<(bool handled, ChangeDispatchResult result)> TryPersistWithWorkspaceAsync(string path, string content, CancellationToken cancellationToken)
        {
            if (_roslynWorkspace is null)
            {
                return (false, default);
            }

            var normalizedPath = NormalizePath(path);
            var solution = _roslynWorkspace.CurrentSolution;

            if (_roslynWorkspace.CanApplyChange(ApplyChangesKind.ChangeDocument))
            {
                var document = FindDocumentByPath(solution, normalizedPath);
                if (document is not null)
                {
                    return await ApplyDocumentChangeAsync(document, content, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_roslynWorkspace.CanApplyChange(ApplyChangesKind.ChangeAdditionalDocument))
            {
                var additionalDocument = FindAdditionalDocumentByPath(solution, normalizedPath);
                if (additionalDocument is not null)
                {
                    return await ApplyAdditionalDocumentChangeAsync(additionalDocument, content, cancellationToken).ConfigureAwait(false);
                }
            }

            return (false, default);
        }

        private async Task<(bool handled, ChangeDispatchResult result)> ApplyDocumentChangeAsync(Document document, string content, CancellationToken cancellationToken)
        {
            try
            {
                var currentText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var encoding = currentText.Encoding ?? new UTF8Encoding(false);
                var newText = SourceText.From(content, encoding);
                var newSolution = document.Project.Solution.WithDocumentText(document.Id, newText, PreservationMode.PreserveIdentity);

                if (!_roslynWorkspace!.TryApplyChanges(newSolution))
                {
                    return (true, ChangeDispatchResult.MutationFailure(null, "Roslyn workspace rejected the XAML update."));
                }

                return (true, ChangeDispatchResult.Success());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (true, ChangeDispatchResult.MutationFailure(null, $"Failed to persist XAML via Roslyn workspace: {ex.Message}"));
            }
        }

        private async Task<(bool handled, ChangeDispatchResult result)> ApplyAdditionalDocumentChangeAsync(TextDocument document, string content, CancellationToken cancellationToken)
        {
            try
            {
                var currentText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var encoding = currentText.Encoding ?? new UTF8Encoding(false);
                var newText = SourceText.From(content, encoding);
                var newSolution = document.Project.Solution.WithAdditionalDocumentText(document.Id, newText, PreservationMode.PreserveIdentity);

                if (!_roslynWorkspace!.TryApplyChanges(newSolution))
                {
                    return (true, ChangeDispatchResult.MutationFailure(null, "Roslyn workspace rejected the XAML update."));
                }

                return (true, ChangeDispatchResult.Success());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (true, ChangeDispatchResult.MutationFailure(null, $"Failed to persist XAML via Roslyn workspace: {ex.Message}"));
            }
        }

        private async Task<ChangeDispatchResult> PersistToFileSystemAsync(string path, string content, CancellationToken cancellationToken)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Exists && fileInfo.IsReadOnly)
                {
                    return ChangeDispatchResult.MutationFailure(null, "Document is read-only.");
                }

                await WriteDocumentAsync(path, content, cancellationToken).ConfigureAwait(false);
                _workspace.Invalidate(path);
                return ChangeDispatchResult.Success();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ChangeDispatchResult.MutationFailure(null, $"Failed to persist XAML document: {ex.Message}");
            }
        }

        private static Document? FindDocumentByPath(Solution solution, string normalizedPath)
        {
            return solution.Projects
                .SelectMany(project => project.Documents)
                .FirstOrDefault(document =>
                    !string.IsNullOrWhiteSpace(document.FilePath) &&
                    string.Equals(NormalizePath(document.FilePath!), normalizedPath, PathComparison));
        }

        private static TextDocument? FindAdditionalDocumentByPath(Solution solution, string normalizedPath)
        {
            return solution.Projects
                .SelectMany(project => project.AdditionalDocuments)
                .FirstOrDefault(document =>
                    !string.IsNullOrWhiteSpace(document.FilePath) &&
                    string.Equals(NormalizePath(document.FilePath!), normalizedPath, PathComparison));
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        private static string ApplyEdits(string text, IReadOnlyList<XamlTextEdit> edits)
        {
            var sorted = new List<XamlTextEdit>(edits);
            sorted.Sort(static (a, b) => a.Start.CompareTo(b.Start));

            var builder = new StringBuilder(text.Length + 128);
            var current = 0;

            foreach (var edit in sorted)
            {
                if (edit.Start < current)
                {
                    throw new InvalidOperationException("Overlapping text edits are not supported.");
                }

                builder.Append(text, current, edit.Start - current);
                builder.Append(edit.Replacement);
                current = edit.Start + edit.Length;
            }

            builder.Append(text, current, text.Length - current);
            return builder.ToString();
        }

        private static async Task WriteDocumentAsync(string path, string content, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(content).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        private void OnMutationCompleted(ChangeEnvelope envelope, ChangeDispatchResult result)
        {
            if (MutationCompleted is null)
            {
                return;
            }

            try
            {
                MutationCompleted.Invoke(this, new MutationCompletedEventArgs(envelope, result));
            }
            catch
            {
                // Diagnostics notifications should not throw.
            }
        }
    }
}
