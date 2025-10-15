using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Diagnostics.SourceNavigation;

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
        private int? _highlightedLine;

        public SourcePreviewViewModel(SourceInfo sourceInfo, ISourceNavigator sourceNavigator, HttpClient? httpClient = null)
        {
            SourceInfo = sourceInfo ?? throw new ArgumentNullException(nameof(sourceInfo));
            _sourceNavigator = sourceNavigator ?? throw new ArgumentNullException(nameof(sourceNavigator));
            _httpClient = httpClient ?? SharedHttpClient;
            Title = string.IsNullOrWhiteSpace(SourceInfo.DisplayPath)
                ? "Source Preview"
                : SourceInfo.DisplayPath;
        }

        public SourceInfo SourceInfo { get; }

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

        public int? HighlightedLine
        {
            get => _highlightedLine;
            private set => RaiseAndSetIfChanged(ref _highlightedLine, value);
        }

        public bool HasSnippet => !string.IsNullOrEmpty(Snippet);

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

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
            if (!IsLoading && (Snippet is not null || ErrorMessage is not null))
            {
                return;
            }

            IsLoading = true;
            ErrorMessage = null;

            try
            {
                var content = await FetchContentAsync().ConfigureAwait(false);
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
            var normalized = content.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');
            var requestedLine = SourceInfo.StartLine ?? 1;
            var hasLocation = SourceInfo.HasLocation;

            var builder = new StringBuilder(normalized.Length + (lines.Length * 8));
            for (var index = 0; index < lines.Length; index++)
            {
                var lineNumber = index + 1;
                var text = lines[index];
                builder.AppendFormat("{0,5}: {1}\n", lineNumber, text);
            }

            SnippetStartLine = 1;
            HighlightedLine = hasLocation ? requestedLine : null;
            Snippet = builder.ToString();
        }
    }
}
