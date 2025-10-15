using System;
using System.Collections.Generic;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.Xaml
{
    public sealed class XamlAstDocument
    {
        private readonly TextLineMap _lineMap;

        public XamlAstDocument(string path, string text, XmlDocumentSyntax syntax, XamlDocumentVersion version, IReadOnlyList<XamlAstDiagnostic> diagnostics)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Text = text ?? throw new ArgumentNullException(nameof(text));
            Syntax = syntax ?? throw new ArgumentNullException(nameof(syntax));
            Version = version;
            Diagnostics = diagnostics ?? Array.Empty<XamlAstDiagnostic>();
            _lineMap = new TextLineMap(text);
        }

        public string Path { get; }

        public string Text { get; }

        public XmlDocumentSyntax Syntax { get; }

        public XamlDocumentVersion Version { get; }

        public IReadOnlyList<XamlAstDiagnostic> Diagnostics { get; }

        public LinePositionSpan GetLinePositionSpan(TextSpan span) => _lineMap.GetLinePositionSpan(span);

        public bool TryGetLinePositionSpan(TextSpan span, out LinePositionSpan lineSpan) =>
            _lineMap.TryGetLinePositionSpan(span, out lineSpan);
    }

    public readonly record struct XamlDocumentVersion(DateTimeOffset TimestampUtc, long Length, string Checksum)
    {
        public override string ToString() => $"{TimestampUtc.UtcDateTime:O}:{Length}:{Checksum}";
    }

    public sealed class XamlAstDiagnostic
    {
        public XamlAstDiagnostic(TextSpan span, XamlDiagnosticSeverity severity, ERRID errorId, string message)
        {
            Span = span;
            Severity = severity;
            ErrorId = errorId;
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        public TextSpan Span { get; }

        public XamlDiagnosticSeverity Severity { get; }

        public ERRID ErrorId { get; }

        public string Message { get; }
    }

    public enum XamlDiagnosticSeverity
    {
        Hidden,
        Info,
        Warning,
        Error
    }

    public readonly struct LinePosition
    {
        public LinePosition(int line, int column)
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }

        public int Column { get; }
    }

    public readonly struct LinePositionSpan
    {
        public LinePositionSpan(LinePosition start, LinePosition end)
        {
            Start = start;
            End = end;
        }

        public LinePosition Start { get; }

        public LinePosition End { get; }
    }

    internal sealed class TextLineMap
    {
        private readonly int[] _lineStarts;

        public TextLineMap(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var starts = new List<int>(Math.Max(4, text.Length / 32));
            starts.Add(0);

            for (var index = 0; index < text.Length; index++)
            {
                var ch = text[index];
                switch (ch)
                {
                    case '\r':
                        if (index + 1 < text.Length && text[index + 1] == '\n')
                        {
                            index++;
                        }

                        starts.Add(index + 1);
                        break;
                    case '\n':
                        starts.Add(index + 1);
                        break;
                }
            }

            _lineStarts = starts.ToArray();
        }

        public LinePositionSpan GetLinePositionSpan(TextSpan span)
        {
            if (!TryGetLinePositionSpan(span, out var result))
            {
                throw new ArgumentOutOfRangeException(nameof(span));
            }

            return result;
        }

        public bool TryGetLinePositionSpan(TextSpan span, out LinePositionSpan spanResult)
        {
            if (span.Start < 0 || span.End < span.Start)
            {
                spanResult = default;
                return false;
            }

            var start = GetLinePosition(span.Start);
            var end = GetLinePosition(span.End);

            spanResult = new LinePositionSpan(start, end);
            return true;
        }

        private LinePosition GetLinePosition(int position)
        {
            if (_lineStarts.Length == 0)
            {
                return new LinePosition(1, 1);
            }

            var lineIndex = Array.BinarySearch(_lineStarts, position);
            if (lineIndex < 0)
            {
                lineIndex = ~lineIndex - 1;
            }

            if (lineIndex < 0)
            {
                lineIndex = 0;
            }

            lineIndex = Math.Min(lineIndex, _lineStarts.Length - 1);
            var lineStart = _lineStarts[lineIndex];
            return new LinePosition(lineIndex + 1, (position - lineStart) + 1);
        }
    }
}
