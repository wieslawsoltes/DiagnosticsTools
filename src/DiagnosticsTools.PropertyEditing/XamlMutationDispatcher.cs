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
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    public sealed class XamlMutationDispatcher : IChangeDispatcher
    {
        private readonly XamlAstWorkspace _workspace;
        private readonly Workspace? _roslynWorkspace;
        private readonly XamlMutationJournal _journal;
        public const string MutablePipelinePreviewUnsupportedMessage = "Preview generation is not supported with the mutable pipeline.";
        private static readonly StringComparison PathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private sealed record class PreparedMutation(
            ChangeEnvelope Envelope,
            string Path,
            XamlAstDocument Document,
            MutableXamlDocument MutatedDocument,
            string UpdatedText,
            bool Mutated);

        public XamlAstWorkspace Workspace => _workspace;

        public XamlMutationDispatcher(XamlAstWorkspace workspace, Workspace? roslynWorkspace = null)
        {
            _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _roslynWorkspace = roslynWorkspace;
            _journal = new XamlMutationJournal();
        }

        public event EventHandler<MutationCompletedEventArgs>? MutationCompleted;

        public bool CanUndo => _journal.CanUndo;

        public bool CanRedo => _journal.CanRedo;

        public bool TryPeekUndo(out MutationEntry entry) => _journal.TryPeekUndo(out entry);

        public bool TryPeekRedo(out MutationEntry entry) => _journal.TryPeekRedo(out entry);

        public IReadOnlyList<MutationEntry> GetUndoHistory() => _journal.GetUndoSnapshot();

        public IReadOnlyList<MutationEntry> GetRedoHistory() => _journal.GetRedoSnapshot();

        public event EventHandler? HistoryChanged;

        public async ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            var (prepareResult, prepared) = await PrepareMutationAsync(envelope, cancellationToken).ConfigureAwait(false);
            if (prepared is null)
            {
                OnMutationCompleted(envelope, prepareResult);
                return prepareResult;
            }

            if (!prepared.Mutated)
            {
                var success = ChangeDispatchResult.Success();
                OnMutationCompleted(envelope, success);
                return success;
            }

            var (commitResult, documentMutation) = await CommitPreparedMutationAsync(prepared, cancellationToken).ConfigureAwait(false);
            if (commitResult.Status == ChangeDispatchStatus.Success && documentMutation is not null)
            {
                var gesture = documentMutation.Value.Envelope?.Source?.Gesture;
                _journal.Record(new MutationEntry(new[] { documentMutation.Value }, DateTimeOffset.UtcNow, gesture));
                OnHistoryChanged();
            }

            OnMutationCompleted(envelope, commitResult);
            return commitResult;
        }

        public async ValueTask<ChangeDispatchResult> DispatchAsync(ChangeBatch batch, CancellationToken cancellationToken = default)
        {
            if (batch is null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (batch.Documents is null || batch.Documents.Count == 0)
            {
                return ChangeDispatchResult.Success();
            }

            var preparedMutations = new List<PreparedMutation>(batch.Documents.Count);

            foreach (var envelope in batch.Documents)
            {
                var (prepareResult, prepared) = await PrepareMutationAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (prepared is null)
                {
                    OnMutationCompleted(envelope, prepareResult);
                    return prepareResult;
                }

                preparedMutations.Add(prepared);
            }

            var committedMutations = new List<DocumentMutation>();

            foreach (var prepared in preparedMutations)
            {
                if (!prepared.Mutated)
                {
                    var success = ChangeDispatchResult.Success();
                    OnMutationCompleted(prepared.Envelope, success);
                    continue;
                }

                var (commitResult, documentMutation) = await CommitPreparedMutationAsync(prepared, cancellationToken).ConfigureAwait(false);
                if (commitResult.Status != ChangeDispatchStatus.Success)
                {
                    await RollbackCommittedMutationsAsync(committedMutations, cancellationToken).ConfigureAwait(false);
                    OnMutationCompleted(prepared.Envelope, commitResult);
                    return commitResult;
                }

                if (documentMutation is not null)
                {
                    committedMutations.Add(documentMutation.Value);
                }

                OnMutationCompleted(prepared.Envelope, commitResult);
            }

            if (committedMutations.Count > 0)
            {
                var gesture = committedMutations[0].Envelope?.Source?.Gesture;
                _journal.Record(new MutationEntry(committedMutations.ToArray(), DateTimeOffset.UtcNow, gesture));
                OnHistoryChanged();
            }

            return ChangeDispatchResult.Success();
        }

        public async ValueTask<ChangeDispatchResult> UndoAsync(CancellationToken cancellationToken = default)
        {
            if (!_journal.TryPopUndo(out var entry))
            {
                return ChangeDispatchResult.MutationFailure(null, "No mutations to undo.");
            }

            var documents = entry.Documents;
            var result = ChangeDispatchResult.Success();

            if (documents is not null)
            {
                foreach (var document in documents)
                {
                    var restoreResult = await RestoreSnapshotAsync(document.Path, document.Before, cancellationToken).ConfigureAwait(false);
                    if (restoreResult.Status != ChangeDispatchStatus.Success)
                    {
                        result = restoreResult;
                        break;
                    }
                }
            }

            if (result.Status == ChangeDispatchStatus.Success)
            {
                _journal.PushRedo(entry);
                OnHistoryChanged();
            }
            else
            {
                _journal.PushUndo(entry);
                OnHistoryChanged();
            }

            if (documents is not null)
            {
                foreach (var document in documents)
                {
                    OnMutationCompleted(document.Envelope, result);
                }
            }

            return result;
        }

        public async ValueTask<ChangeDispatchResult> RedoAsync(CancellationToken cancellationToken = default)
        {
            if (!_journal.TryPopRedo(out var entry))
            {
                return ChangeDispatchResult.MutationFailure(null, "No mutations to redo.");
            }

            var documents = entry.Documents;
            var result = ChangeDispatchResult.Success();

            if (documents is not null)
            {
                foreach (var document in documents)
                {
                    var restoreResult = await RestoreSnapshotAsync(document.Path, document.After, cancellationToken).ConfigureAwait(false);
                    if (restoreResult.Status != ChangeDispatchStatus.Success)
                    {
                        result = restoreResult;
                        break;
                    }
                }
            }

            if (result.Status == ChangeDispatchStatus.Success)
            {
                _journal.PushUndo(entry);
                OnHistoryChanged();
            }
            else
            {
                _journal.PushRedo(entry);
                OnHistoryChanged();
            }

            if (documents is not null)
            {
                foreach (var document in documents)
                {
                    OnMutationCompleted(document.Envelope, result);
                }
            }

            return result;
        }

        public async ValueTask<MutationPreviewResult> PreviewAsync(ChangeEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (envelope is null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (string.IsNullOrWhiteSpace(envelope.Document.Path))
            {
                return MutationPreviewResult.Failure(ChangeDispatchStatus.MutationFailure, "Document path is missing.", string.Empty, envelope.Changes);
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
                return MutationPreviewResult.Failure(ChangeDispatchStatus.MutationFailure, $"Failed to load XAML document: {ex.Message}", string.Empty, envelope.Changes);
            }

            var currentVersion = document.Version.ToString();
            if (!string.Equals(currentVersion, envelope.Document.Version, StringComparison.Ordinal))
            {
                return MutationPreviewResult.Failure(ChangeDispatchStatus.GuardFailure, "Document version mismatch.", document.Text, envelope.Changes);
            }

                return MutationPreviewResult.Failure(ChangeDispatchStatus.MutationFailure, MutablePipelinePreviewUnsupportedMessage, document.Text, envelope.Changes);
        }

        private async Task<ChangeDispatchResult> PersistAsync(string path, string content, XamlAstDocument? document, CancellationToken cancellationToken)
        {
            var encodingInfo = await ResolveEncodingInfoAsync(path, document, cancellationToken).ConfigureAwait(false);

            var (handledByWorkspace, workspaceResult) = await TryPersistWithWorkspaceAsync(path, content, cancellationToken).ConfigureAwait(false);
            if (handledByWorkspace)
            {
                if (workspaceResult.Status != ChangeDispatchStatus.Success)
                {
                    return workspaceResult;
                }

                return await PersistToFileSystemAsync(path, content, encodingInfo, cancellationToken).ConfigureAwait(false);
            }

            return await PersistToFileSystemAsync(path, content, encodingInfo, cancellationToken).ConfigureAwait(false);
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

        private async Task<ChangeDispatchResult> PersistToFileSystemAsync(string path, string content, DocumentEncodingInfo encodingInfo, CancellationToken cancellationToken)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Exists && fileInfo.IsReadOnly)
                {
                    return ChangeDispatchResult.MutationFailure(null, "Document is read-only.");
                }

                await WriteDocumentAsync(path, content, encodingInfo.Encoding, cancellationToken).ConfigureAwait(false);
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

        private static string ApplyEdits(string text, IReadOnlyList<XamlTextEdit> edits) =>
            ApplyEdits(text, edits, null, null);

        private static string ApplyEdits(
            string text,
            IReadOnlyList<XamlTextEdit> edits,
            IList<MutationPreviewHighlight>? originalHighlights,
            IList<MutationPreviewHighlight>? previewHighlights)
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

                if (edit.Length > 0)
                {
                    originalHighlights?.Add(new MutationPreviewHighlight(edit.Start, edit.Length));
                }

                var insertionOffset = builder.Length;

                if (!string.IsNullOrEmpty(edit.Replacement))
                {
                    previewHighlights?.Add(new MutationPreviewHighlight(insertionOffset, edit.Replacement.Length));
                }

                builder.Append(edit.Replacement);
                current = edit.Start + edit.Length;
            }

            builder.Append(text, current, text.Length - current);
            return builder.ToString();
        }

        private async ValueTask<(ChangeDispatchResult Result, PreparedMutation? Mutation)> PrepareMutationAsync(
            ChangeEnvelope envelope,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(envelope.Document.Path))
            {
                return (ChangeDispatchResult.MutationFailure(null, "Document path is missing."), null);
            }

            var path = envelope.Document.Path;
            XamlAstDocument document;
            IXamlAstIndex index;

            try
            {
                document = await _workspace.GetDocumentAsync(path, cancellationToken).ConfigureAwait(false);
                index = await _workspace.GetIndexAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (ChangeDispatchResult.MutationFailure(null, $"Failed to load XAML document: {ex.Message}"), null);
            }

            var currentVersion = document.Version.ToString();
            if (!string.Equals(currentVersion, envelope.Document.Version, StringComparison.Ordinal))
            {
                return (ChangeDispatchResult.GuardFailure(null, "Document version mismatch."), null);
            }

            MutableXamlDocument mutableDocument;
            try
            {
                mutableDocument = await _workspace.GetMutableDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (ChangeDispatchResult.MutationFailure(null, "Failed to load mutable XAML document."), null);
            }

            var applicationResult = MutableXamlMutationApplier.TryApply(document, index, mutableDocument, envelope.Changes);
            if (applicationResult.Status == MutableMutationStatus.Failed)
            {
                return (applicationResult.Failure ?? ChangeDispatchResult.MutationFailure(null, "Mutable pipeline failed to apply change."), null);
            }

            if (applicationResult.Status == MutableMutationStatus.Unsupported)
            {
                return (ChangeDispatchResult.MutationFailure(null, "Mutable pipeline does not support this change type."), null);
            }

            var mutatedDocument = applicationResult.Document ?? mutableDocument;
            var updatedText = applicationResult.Mutated
                ? MutableXamlSerializer.Serialize(mutatedDocument)
                : document.Text;

            var prepared = new PreparedMutation(envelope, path, document, mutatedDocument, updatedText, applicationResult.Mutated);
            return (ChangeDispatchResult.Success(), prepared);
        }

        private async ValueTask<(ChangeDispatchResult Result, DocumentMutation? Mutation)> CommitPreparedMutationAsync(
            PreparedMutation prepared,
            CancellationToken cancellationToken)
        {
            if (!prepared.Mutated)
            {
                return (ChangeDispatchResult.Success(), null);
            }

            if (_roslynWorkspace is not null)
            {
                var (handled, workspaceResult) = await TryPersistWithWorkspaceAsync(prepared.Path, prepared.UpdatedText, cancellationToken).ConfigureAwait(false);
                if (handled && workspaceResult.Status != ChangeDispatchStatus.Success)
                {
                    return (workspaceResult, null);
                }
            }

            XamlAstDocument committedDocument;
            try
            {
                committedDocument = await _workspace.CommitMutableDocumentAsync(prepared.Path, prepared.MutatedDocument, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (ChangeDispatchResult.MutationFailure(null, $"Failed to persist mutable XAML document: {ex.Message}"), null);
            }

            var mutation = new DocumentMutation(prepared.Envelope, prepared.Path, prepared.Document.Text, committedDocument.Text);
            return (ChangeDispatchResult.Success(), mutation);
        }

        private async Task RollbackCommittedMutationsAsync(
            IReadOnlyList<DocumentMutation> mutations,
            CancellationToken cancellationToken)
        {
            if (mutations is null || mutations.Count == 0)
            {
                return;
            }

            for (var index = mutations.Count - 1; index >= 0; index--)
            {
                var mutation = mutations[index];
                await RestoreSnapshotAsync(mutation.Path, mutation.Before, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<DocumentEncodingInfo> ResolveEncodingInfoAsync(string path, XamlAstDocument? document, CancellationToken cancellationToken)
        {
            if (document is not null)
            {
                return new DocumentEncodingInfo(document.Encoding, document.IsEncodingFallback);
            }

            try
            {
                var current = await _workspace.GetDocumentAsync(path, cancellationToken).ConfigureAwait(false);
                return new DocumentEncodingInfo(current.Encoding, current.IsEncodingFallback);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new DocumentEncodingInfo(new UTF8Encoding(false), isFallback: true);
            }
        }

        private static async Task WriteDocumentAsync(string path, string content, Encoding encoding, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);

            using var writer = new StreamWriter(stream, encoding ?? new UTF8Encoding(false));

            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(content).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }

        private async Task<ChangeDispatchResult> RestoreSnapshotAsync(string path, string snapshot, CancellationToken cancellationToken)
        {
            XamlAstDocument currentDocument;

            try
            {
                currentDocument = await _workspace.GetDocumentAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ChangeDispatchResult.MutationFailure(null, $"Failed to load XAML document: {ex.Message}");
            }

            MutableXamlDocument mutableSnapshot;

            try
            {
                var syntax = Parser.ParseText(snapshot);
                var snapshotDocument = new XamlAstDocument(
                    path,
                    snapshot,
                    syntax,
                    currentDocument.Version,
                    currentDocument.Diagnostics,
                    currentDocument.Encoding,
                    currentDocument.HasByteOrderMark,
                    currentDocument.IsEncodingFallback);

                mutableSnapshot = MutableXamlDocument.FromDocument(snapshotDocument);
            }
            catch (Exception ex)
            {
                return ChangeDispatchResult.MutationFailure(null, $"Failed to parse XAML snapshot: {ex.Message}");
            }

            try
            {
                await _workspace.CommitMutableDocumentAsync(path, mutableSnapshot, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ChangeDispatchResult.MutationFailure(null, $"Failed to persist snapshot: {ex.Message}");
            }

            return ChangeDispatchResult.Success();
        }

        private void OnMutationCompleted(ChangeEnvelope envelope, ChangeDispatchResult result)
        {
            if (MutationCompleted is null)
            {
                return;
            }

            try
            {
                var provenance = MutationProvenanceHelper.FromEnvelope(envelope);
                MutationCompleted.Invoke(this, new MutationCompletedEventArgs(envelope, result, provenance));
            }
            catch
            {
                // Diagnostics notifications should not throw.
            }
        }

        public void HandleExternalDocumentChanged(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _journal.Clear();
            }
            else
            {
                _journal.DiscardEntriesForPath(path!);
            }
            OnHistoryChanged();
        }

        private void OnHistoryChanged()
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        private readonly struct DocumentEncodingInfo
        {
            public DocumentEncodingInfo(Encoding encoding, bool isFallback)
            {
                Encoding = encoding ?? new UTF8Encoding(false);
                IsFallback = isFallback;
            }

            public Encoding Encoding { get; }

            public bool IsFallback { get; }
        }
    }
}
