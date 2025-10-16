using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Linq;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Threading;
using System.Windows.Input;

namespace Avalonia.Diagnostics.ViewModels
{
    public sealed class SourcePreviewViewModel : ViewModelBase
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();
        private readonly ISourceNavigator _sourceNavigator;
        private readonly HttpClient _httpClient;
        private string? _snippet;
        private bool _isLoading = true;
        private string? _errorMessage;
        private int _snippetStartLine;
        private int? _highlightedStartLine;
        private int? _highlightedEndLine;
        private int? _highlightSpanStart;
        private int? _highlightSpanLength;
        private readonly Action<XamlAstNodeDescriptor?>? _navigateToAst;
        private readonly Action<XamlAstSelection?>? _synchronizeSelection;
        private readonly DelegateCommand _openSourceCommand;
        private readonly DelegateCommand _revealInTreeCommand;
        private readonly SourcePreviewNavigationTarget _revealNavigationTarget;
        private readonly ObservableCollection<SourcePreviewNavigationTarget> _navigationTargets = new();
        private readonly DelegateCommand _flipSplitOrientationCommand;
        private SourcePreviewViewModel? _runtimeComparison;
        private bool _isSplitViewEnabled;
        private double _splitRatio;
        private SourcePreviewSplitOrientation _splitOrientation;
        private bool _hasManualSnippet;
        private bool _suppressSplitEnabledPersistence;
        private bool _suppressSplitRatioPersistence;
        private static bool s_lastSplitEnabled;
        private static double s_lastHorizontalRatio = 0.5;
        private static double s_lastVerticalRatio = 0.5;
        private static SourcePreviewSplitOrientation s_lastOrientation = SourcePreviewSplitOrientation.Horizontal;
        private MainViewModel? _mutationOwner;
        private bool _isApplyingTreeSelection;
        private readonly XamlAstWorkspace? _xamlAstWorkspace;
        private string? _normalizedDocumentPath;
        private bool _workspaceSubscribed;
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;

        public SourcePreviewViewModel(
            SourceInfo sourceInfo,
            ISourceNavigator sourceNavigator,
            XamlAstSelection? astSelection = null,
            Action<XamlAstNodeDescriptor?>? navigateToAst = null,
            Action<XamlAstSelection?>? synchronizeSelection = null,
            HttpClient? httpClient = null,
            string? initialErrorMessage = null,
            MainViewModel? mutationOwner = null,
            XamlAstWorkspace? xamlAstWorkspace = null)
        {
            SourceInfo = sourceInfo ?? throw new ArgumentNullException(nameof(sourceInfo));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            _httpClient = httpClient ?? SharedHttpClient;
            AstSelection = astSelection;
            _navigateToAst = navigateToAst;
            _synchronizeSelection = synchronizeSelection;
            _xamlAstWorkspace = xamlAstWorkspace;
            if (_xamlAstWorkspace is not null)
            {
                SubscribeToWorkspace();
            }
            if (mutationOwner is not null)
            {
                AttachToMutationOwner(mutationOwner);
            }
            Title = string.IsNullOrWhiteSpace(SourceInfo.DisplayPath)
                ? "Source Preview"
                : SourceInfo.DisplayPath;
            _splitOrientation = s_lastOrientation;
            _splitRatio = NormalizeRatio(_splitOrientation == SourcePreviewSplitOrientation.Horizontal
                ? s_lastHorizontalRatio
                : s_lastVerticalRatio);
            _isSplitViewEnabled = s_lastSplitEnabled;
            _flipSplitOrientationCommand = new DelegateCommand(() =>
            {
                SplitOrientation = SplitOrientation == SourcePreviewSplitOrientation.Horizontal
                    ? SourcePreviewSplitOrientation.Vertical
                    : SourcePreviewSplitOrientation.Horizontal;
            });
            if (!string.IsNullOrEmpty(initialErrorMessage))
            {
                ErrorMessage = initialErrorMessage;
                IsLoading = false;
            }

            _openSourceCommand = new DelegateCommand(OpenSourceAsync, () => SourceInfo.HasLocation);
            _revealInTreeCommand = new DelegateCommand(
                () =>
                {
                    NavigateToAst();
                    return Task.CompletedTask;
                },
                () => CanNavigateToAst);
            _revealNavigationTarget = new SourcePreviewNavigationTarget("Reveal in Tree", _revealInTreeCommand);
            RefreshNavigationState();
            RefreshDocumentPathFromSelection(astSelection);
        }

        public SourceInfo SourceInfo { get; }

        public XamlAstSelection? AstSelection { get; private set; }

        public string Title { get; }

        public string? Snippet
        {
            get => _snippet;
            private set
            {
                if (RaiseAndSetIfChanged(ref _snippet, value))
                {
                    RaisePropertyChanged(nameof(HasSnippet));
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => RaiseAndSetIfChanged(ref _isLoading, value);
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set => RaiseAndSetIfChanged(ref _errorMessage, value);
        }

        public int SnippetStartLine
        {
            get => _snippetStartLine;
            private set => RaiseAndSetIfChanged(ref _snippetStartLine, value);
        }

        public int? HighlightedStartLine
        {
            get => _highlightedStartLine;
            private set => RaiseAndSetIfChanged(ref _highlightedStartLine, value);
        }

        public int? HighlightedEndLine
        {
            get => _highlightedEndLine;
            private set => RaiseAndSetIfChanged(ref _highlightedEndLine, value);
        }

        public int? HighlightSpanStart
        {
            get => _highlightSpanStart;
            private set => RaiseAndSetIfChanged(ref _highlightSpanStart, value);
        }

        public int? HighlightSpanLength
        {
            get => _highlightSpanLength;
            private set => RaiseAndSetIfChanged(ref _highlightSpanLength, value);
        }

        public bool HasSnippet => !string.IsNullOrEmpty(Snippet);

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public bool CanNavigateToAst => _navigateToAst is not null && AstSelection?.Node is not null;

        public ICommand OpenSourceCommand => _openSourceCommand;

        public ICommand RevealInTreeCommand => _revealInTreeCommand;

        public ICommand FlipSplitOrientationCommand => _flipSplitOrientationCommand;

        public SourcePreviewViewModel? RuntimeComparison
        {
            get => _runtimeComparison;
            set
            {
                if (RaiseAndSetIfChanged(ref _runtimeComparison, value))
                {
                    if (value is null)
                    {
                        SetSplitViewEnabled(false, persist: false);
                    }
                    else if (!IsSplitViewEnabled && s_lastSplitEnabled)
                    {
                        SetSplitViewEnabled(true, persist: false);
                    }
                }
            }
        }

        public bool IsSplitViewEnabled
        {
            get => _isSplitViewEnabled;
            set
            {
                if (!SetSplitViewEnabledCore(value))
                {
                    return;
                }

                if (!_suppressSplitEnabledPersistence)
                {
                    s_lastSplitEnabled = value;
                }
            }
        }

        public double SplitRatio
        {
            get => _splitRatio;
            set
            {
                var normalized = NormalizeRatio(value);
                if (!SetSplitRatioCore(normalized))
                {
                    return;
                }

                if (!_suppressSplitRatioPersistence)
                {
                    if (_splitOrientation == SourcePreviewSplitOrientation.Horizontal)
                    {
                        s_lastHorizontalRatio = normalized;
                    }
                    else
                    {
                        s_lastVerticalRatio = normalized;
                    }
                }
            }
        }

        public SourcePreviewSplitOrientation SplitOrientation
        {
            get => _splitOrientation;
            set
            {
                if (!SetSplitOrientationCore(value))
                {
                    return;
                }

                s_lastOrientation = value;
                var storedRatio = value == SourcePreviewSplitOrientation.Horizontal
                    ? s_lastHorizontalRatio
                    : s_lastVerticalRatio;
                SetSplitRatio(storedRatio, persist: false);
            }
        }

        public IList<SourcePreviewNavigationTarget> NavigationTargets => _navigationTargets;

        public string LocationSummary
        {
            get
            {
                if (!SourceInfo.HasLocation)
                {
                    return "Location unknown";
                }

                var builder = new StringBuilder();
                builder.Append("Line ");
                builder.Append(SourceInfo.StartLine);
                if (SourceInfo.StartColumn.HasValue)
                {
                    builder.Append(":");
                    builder.Append(SourceInfo.StartColumn);
                }

                if (!string.IsNullOrEmpty(SourceInfo.LocalPath))
                {
                    builder.Append(" • ");
                    builder.Append(SourceInfo.LocalPath);
                }
                else if (SourceInfo.RemoteUri is not null)
                {
                    builder.Append(" • ");
                    builder.Append(SourceInfo.RemoteUri);
                }

                return builder.ToString();
            }
        }

        public async Task LoadAsync()
        {
            if (_hasManualSnippet)
            {
                return;
            }

            if (!IsLoading && (Snippet is not null || ErrorMessage is not null))
            {
                return;
            }

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var content = AstSelection?.Document?.Text ?? await FetchContentAsync().ConfigureAwait(false);
                if (content is null)
                {
                    ErrorMessage = "Source content is unavailable." +
                                   (SourceInfo.RemoteUri is not null ? " Check SourceLink connectivity." : string.Empty);
                    return;
                }

                PopulateSnippet(content);
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task OpenSourceAsync()
        {
            await _sourceNavigator.NavigateAsync(SourceInfo).ConfigureAwait(false);
        }

        public void NavigateToAst()
        {
            if (!CanNavigateToAst)
            {
                return;
            }

            _navigateToAst?.Invoke(AstSelection?.Node);
        }

        public static SourcePreviewViewModel CreateUnavailable(string? context, ISourceNavigator sourceNavigator, HttpClient? httpClient = null, MainViewModel? mutationOwner = null)
        {
            var messageContext = string.IsNullOrWhiteSpace(context) ? "the requested item" : context;
            var message = $"Source information for {messageContext} is unavailable.";
            var placeholderInfo = new SourceInfo(null, null, null, null, null, null, SourceOrigin.Unknown);
            return new SourcePreviewViewModel(placeholderInfo, sourceNavigator, astSelection: null, navigateToAst: null, httpClient: httpClient, initialErrorMessage: message, mutationOwner: mutationOwner);
        }

        internal void HandleMutationCompleted(MutationCompletedEventArgs args)
        {
            if (args.Result.Status != ChangeDispatchStatus.Success)
            {
                var message = !string.IsNullOrWhiteSpace(args.Result.Message)
                    ? args.Result.Message
                    : args.Result.Status == ChangeDispatchStatus.GuardFailure
                        ? "The XAML document was modified outside DevTools. Refresh the inspector and retry."
                        : "The XAML update failed. Check the diagnostics output for details.";
                SetErrorMessage(message);
                return;
            }

            void Reload() => _ = ReloadAfterMutationAsync();

            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(Reload, DispatcherPriority.Background);
            }
            else
            {
                Reload();
            }
        }

        internal void AttachToMutationOwner(MainViewModel owner)
        {
            if (owner is null)
            {
                return;
            }

            if (ReferenceEquals(_mutationOwner, owner))
            {
                return;
            }

            _mutationOwner?.UnregisterSourcePreview(this);
            _mutationOwner = owner;
            _mutationOwner.RegisterSourcePreview(this);
        }

        internal void DetachFromMutationOwner()
        {
            if (_mutationOwner is null)
            {
                return;
            }

            _mutationOwner.UnregisterSourcePreview(this);
            _mutationOwner = null;
        }

        internal void DetachFromWorkspace()
        {
            if (!_workspaceSubscribed || _xamlAstWorkspace is null)
            {
                return;
            }

            _xamlAstWorkspace.NodesChanged -= HandleWorkspaceNodesChanged;
            _xamlAstWorkspace.DiagnosticsChanged -= HandleWorkspaceDiagnosticsChanged;
            _workspaceSubscribed = false;
        }

        internal void UpdateSelectionFromTree(XamlAstSelection? selection)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => UpdateSelectionFromTree(selection), DispatcherPriority.Background);
                return;
            }

            if (ReferenceEquals(AstSelection, selection) || (AstSelection is not null && selection is not null && AstSelection.Equals(selection)))
            {
                return;
            }

            if (selection is null)
            {
                if (AstSelection is null)
                {
                    return;
                }

                _isApplyingTreeSelection = true;
                try
                {
                    AstSelection = null;
                    ApplyHighlightForCurrentSelection();
                    RefreshNavigationState();
                }
            finally
            {
                _isApplyingTreeSelection = false;
            }

            RefreshDocumentPathFromSelection(null);
            return;
        }

            var currentDocument = AstSelection?.Document;
            var newDocument = selection.Document;
            var documentMatches = currentDocument is not null &&
                                  newDocument is not null &&
                                  DocumentPathsEqual(currentDocument.Path, newDocument.Path);

            _isApplyingTreeSelection = true;
            try
            {
                AstSelection = selection;
                ApplyHighlightForCurrentSelection();

                if (!HasSnippet || !documentMatches)
                {
                    _hasManualSnippet = false;
                    Snippet = null;
                    ErrorMessage = null;
                    _ = LoadAsync();
                }

                RefreshNavigationState();
            }
            finally
            {
                _isApplyingTreeSelection = false;
            }

            RefreshDocumentPathFromSelection(selection);
        }

        internal void NotifyEditorSelectionChanged(XamlAstNodeDescriptor? descriptor)
        {
            if (_isApplyingTreeSelection)
            {
                return;
            }

            var currentSelection = AstSelection;
            if (currentSelection is null || currentSelection.Document is null)
            {
                return;
            }

            if (currentSelection.Node?.Id == descriptor?.Id)
            {
                return;
            }

            AstSelection = new XamlAstSelection(
                currentSelection.Document,
                descriptor,
                currentSelection.DocumentNodes);

            ApplyHighlightForCurrentSelection();
            RefreshNavigationState();

            _synchronizeSelection?.Invoke(AstSelection);
        }

        private void SubscribeToWorkspace()
        {
            if (_xamlAstWorkspace is null || _workspaceSubscribed)
            {
                return;
            }

            _xamlAstWorkspace.NodesChanged += HandleWorkspaceNodesChanged;
            _xamlAstWorkspace.DiagnosticsChanged += HandleWorkspaceDiagnosticsChanged;
            _workspaceSubscribed = true;
        }

        private void HandleWorkspaceNodesChanged(object? sender, XamlAstNodesChangedEventArgs e)
        {
            if (e is null || !IsEventForCurrentDocument(e.Path))
            {
                return;
            }

            async Task RefreshAsync()
            {
                var snapshot = await BuildSelectionSnapshotAsync(e).ConfigureAwait(false);
                if (snapshot is null)
                {
                    return;
                }

                UpdateSelectionFromTree(snapshot);
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                _ = RefreshAsync();
            }
            else
            {
                Dispatcher.UIThread.Post(() => _ = RefreshAsync(), DispatcherPriority.Background);
            }
        }

        private void HandleWorkspaceDiagnosticsChanged(object? sender, XamlDiagnosticsChangedEventArgs e)
        {
            if (e is null || !IsEventForCurrentDocument(e.Path))
            {
                return;
            }

            void Apply() => ApplyDiagnostics(e.Diagnostics);

            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
            }
        }

        private async Task<XamlAstSelection?> BuildSelectionSnapshotAsync(XamlAstNodesChangedEventArgs args)
        {
            if (_xamlAstWorkspace is null || string.IsNullOrWhiteSpace(_normalizedDocumentPath))
            {
                return null;
            }

            try
            {
                var document = await _xamlAstWorkspace.GetDocumentAsync(_normalizedDocumentPath!).ConfigureAwait(false);
                var index = await _xamlAstWorkspace.GetIndexAsync(_normalizedDocumentPath!).ConfigureAwait(false);
                var descriptors = index.Nodes as IReadOnlyList<XamlAstNodeDescriptor> ?? index.Nodes.ToList();

                XamlAstNodeDescriptor? node = null;
                var currentNode = AstSelection?.Node;

                if (currentNode is not null)
                {
                    var replacement = ResolveReplacementDescriptor(currentNode, args);
                    if (replacement is null && index.TryGetDescriptor(currentNode.Id, out var descriptor))
                    {
                        replacement = descriptor;
                    }

                    node = replacement;
                }

                return new XamlAstSelection(document, node, descriptors);
            }
            catch
            {
                return null;
            }
        }

        private XamlAstNodeDescriptor? ResolveReplacementDescriptor(XamlAstNodeDescriptor currentNode, XamlAstNodesChangedEventArgs args)
        {
            foreach (var change in args.Changes)
            {
                if (change.Kind == XamlAstNodeChangeKind.Removed &&
                    change.OldNode is not null &&
                    change.OldNode.Id.Equals(currentNode.Id))
                {
                    return null;
                }

                if (change.NewNode is not null &&
                    change.NewNode.Id.Equals(currentNode.Id))
                {
                    return change.NewNode;
                }

                if (change.OldNode is not null &&
                    change.OldNode.Id.Equals(currentNode.Id) &&
                    change.NewNode is not null)
                {
                    return change.NewNode;
                }
            }

            return currentNode;
        }

        private void ApplyDiagnostics(IReadOnlyList<XamlAstDiagnostic> diagnostics)
        {
            if (diagnostics is null || diagnostics.Count == 0)
            {
                if (!string.IsNullOrEmpty(ErrorMessage))
                {
                    ErrorMessage = null;
                    if (!_hasManualSnippet && !IsLoading)
                    {
                        _ = LoadAsync();
                    }
                }

                return;
            }

            var primary = diagnostics.FirstOrDefault(d => d.Severity == XamlDiagnosticSeverity.Error)
                          ?? diagnostics.FirstOrDefault(d => d.Severity == XamlDiagnosticSeverity.Warning)
                          ?? diagnostics[0];

            if (primary is null || string.IsNullOrWhiteSpace(primary.Message) ||
                string.Equals(ErrorMessage, primary.Message, StringComparison.Ordinal))
            {
                return;
            }

            SetErrorMessage(primary.Message);
        }

        private bool IsEventForCurrentDocument(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(_normalizedDocumentPath))
            {
                return false;
            }

            var normalized = NormalizePath(path);
            if (normalized is null)
            {
                return false;
            }

            return PathComparer.Equals(_normalizedDocumentPath!, normalized);
        }

        private void RefreshDocumentPathFromSelection(XamlAstSelection? selection)
        {
            var candidate = selection?.Document?.Path ?? SourceInfo.LocalPath;
            _normalizedDocumentPath = NormalizePath(candidate);
        }

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
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

        private async Task ReloadAfterMutationAsync()
        {
            _hasManualSnippet = false;
            AstSelection = null;
            Snippet = null;
            ErrorMessage = null;
            await LoadAsync().ConfigureAwait(false);
        }

        private void SetErrorMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            ErrorMessage = message;
            Snippet = null;
            IsLoading = false;
        }

        private async Task<string?> FetchContentAsync()
        {
            if (!string.IsNullOrEmpty(SourceInfo.LocalPath) && File.Exists(SourceInfo.LocalPath))
            {
                using (var stream = new FileStream(SourceInfo.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    return await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }

            if (SourceInfo.RemoteUri is not null)
            {
                try
                {
                    return await _httpClient.GetStringAsync(SourceInfo.RemoteUri).ConfigureAwait(false);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private void PopulateSnippet(string content)
        {
            SnippetStartLine = 1;
            Snippet = content;

            ApplyHighlightForCurrentSelection();
            RefreshNavigationState();
        }

        public void SetManualSnippet(string snippet, int snippetStartLine = 1)
        {
            _hasManualSnippet = true;
            ErrorMessage = null;
            SnippetStartLine = snippetStartLine;
            Snippet = snippet;
            ClearHighlight();
            IsLoading = false;
            RefreshNavigationState();
        }

        private void ApplyHighlightForCurrentSelection()
        {
            if (AstSelection?.Node is { } descriptor)
            {
                ApplyDescriptorHighlight(descriptor);
                return;
            }

            ApplySourceInfoHighlight();
        }

        private void ApplyDescriptorHighlight(XamlAstNodeDescriptor descriptor)
        {
            var span = descriptor.Span;
            if (span.Length > 0)
            {
                HighlightSpanStart = span.Start;
                HighlightSpanLength = span.Length;
            }
            else
            {
                HighlightSpanStart = null;
                HighlightSpanLength = null;
            }

            var startLine = Math.Max(descriptor.LineSpan.Start.Line, 1);
            var endLine = Math.Max(descriptor.LineSpan.End.Line, startLine);
            HighlightedStartLine = startLine;
            HighlightedEndLine = endLine;
        }

        private void ApplySourceInfoHighlight()
        {
            if (SourceInfo.HasLocation && SourceInfo.StartLine is int startLine)
            {
                var endLine = SourceInfo.EndLine ?? startLine;
                startLine = Math.Max(startLine, 1);
                endLine = Math.Max(endLine, startLine);
                HighlightedStartLine = startLine;
                HighlightedEndLine = endLine;
            }
            else
            {
                HighlightedStartLine = null;
                HighlightedEndLine = null;
            }

            HighlightSpanStart = null;
            HighlightSpanLength = null;
        }

        private void ClearHighlight()
        {
            HighlightedStartLine = null;
            HighlightedEndLine = null;
            HighlightSpanStart = null;
            HighlightSpanLength = null;
        }

        private void RefreshNavigationState()
        {
            RaisePropertyChanged(nameof(CanNavigateToAst));
            UpdateNavigationTargets();
            UpdateCommandStates();
        }

        private static bool DocumentPathsEqual(string? left, string? right)
        {
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return string.Equals(left, right, comparison);
            }

            try
            {
                left = Path.GetFullPath(left);
                right = Path.GetFullPath(right);
            }
            catch
            {
                // Ignore normalization errors and fall back to raw comparison.
            }

            return string.Equals(left, right, comparison);
        }

        public void AddNavigationTarget(SourcePreviewNavigationTarget target)
        {
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            _navigationTargets.Add(target);
        }

        public bool RemoveNavigationTarget(SourcePreviewNavigationTarget target)
        {
            if (target is null)
            {
                return false;
            }

            return _navigationTargets.Remove(target);
        }

        private void UpdateCommandStates()
        {
            _openSourceCommand.RaiseCanExecuteChanged();
            _revealInTreeCommand.RaiseCanExecuteChanged();
        }

        private void UpdateNavigationTargets()
        {
            if (CanNavigateToAst)
            {
                if (!_navigationTargets.Contains(_revealNavigationTarget))
                {
                    _navigationTargets.Add(_revealNavigationTarget);
                }
            }
            else
            {
                _navigationTargets.Remove(_revealNavigationTarget);
            }
        }

        private void SetSplitViewEnabled(bool value, bool persist)
        {
            var original = _suppressSplitEnabledPersistence;
            _suppressSplitEnabledPersistence = !persist;
            try
            {
                IsSplitViewEnabled = value;
            }
            finally
            {
                _suppressSplitEnabledPersistence = original;
            }
        }

        private bool SetSplitViewEnabledCore(bool value)
        {
            if (_isSplitViewEnabled == value)
            {
                return false;
            }

            RaiseAndSetIfChanged(ref _isSplitViewEnabled, value);
            return true;
        }

        private void SetSplitRatio(double ratio, bool persist)
        {
            var original = _suppressSplitRatioPersistence;
            _suppressSplitRatioPersistence = !persist;
            try
            {
                SplitRatio = ratio;
            }
            finally
            {
                _suppressSplitRatioPersistence = original;
            }
        }

        private bool SetSplitRatioCore(double value)
        {
            if (AreClose(_splitRatio, value))
            {
                return false;
            }

            RaiseAndSetIfChanged(ref _splitRatio, value);
            return true;
        }

        private bool SetSplitOrientationCore(SourcePreviewSplitOrientation value)
        {
            if (_splitOrientation == value)
            {
                return false;
            }

            RaiseAndSetIfChanged(ref _splitOrientation, value);
            return true;
        }

        private static double NormalizeRatio(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.5;
            }

            const double minimum = 0.05;
            const double maximum = 0.95;

            if (value < minimum)
            {
                return minimum;
            }

            if (value > maximum)
            {
                return maximum;
            }

            return value;
        }

        private static bool AreClose(double left, double right) =>
            Math.Abs(left - right) < 0.0001;
    }

    public enum SourcePreviewSplitOrientation
    {
        Horizontal,
        Vertical
    }
}
