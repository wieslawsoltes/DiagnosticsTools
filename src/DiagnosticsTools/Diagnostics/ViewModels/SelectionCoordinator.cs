using System;
using System.Runtime.InteropServices;
using Avalonia.Diagnostics.Xaml;

namespace Avalonia.Diagnostics.ViewModels
{
    public sealed class SelectionCoordinator
    {
        private readonly object _gate = new();
        private SelectionSnapshot _current = SelectionSnapshot.Empty;
        private string? _publisher;
        private int _publishDepth;

        public bool TryBeginPublish(string ownerId, XamlAstSelection? selection, out IDisposable? token, out bool changed)
        {
            if (ownerId is null)
            {
                throw new ArgumentNullException(nameof(ownerId));
            }

            var snapshot = SelectionSnapshot.Create(selection);

            lock (_gate)
            {
                if (_publisher is not null && _publisher != ownerId)
                {
                    token = null;
                    changed = false;
                    return false;
                }

                changed = !_current.Equals(snapshot);
                _publisher = ownerId;
                _publishDepth++;
                _current = snapshot;
                token = new PublishToken(this, ownerId);
                return true;
            }
        }

        public bool IsCurrent(XamlAstSelection? selection)
        {
            var snapshot = SelectionSnapshot.Create(selection);

            lock (_gate)
            {
                return _current.Equals(snapshot);
            }
        }

        private void EndPublish(string ownerId)
        {
            lock (_gate)
            {
                if (_publisher == ownerId)
                {
                    _publishDepth--;
                    if (_publishDepth <= 0)
                    {
                        _publishDepth = 0;
                        _publisher = null;
                    }
                }
            }
        }

        private readonly record struct SelectionSnapshot(string? DocumentPath, XamlAstNodeId? NodeId, int? StartLine, int? EndLine)
        {
            public static SelectionSnapshot Empty { get; } = new(null, null, null, null);

            private static readonly StringComparer PathComparer =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal;

            public static SelectionSnapshot Create(XamlAstSelection? selection)
            {
                if (selection is null)
                {
                    return Empty;
                }

                var documentPath = selection.Document?.Path;

                if (selection.Node is { } node && selection.Document is { } document)
                {
                    var span = node.LineSpan;
                    return new SelectionSnapshot(
                        document.Path,
                        node.Id,
                        span.Start.Line,
                        span.End.Line);
                }

                if (!string.IsNullOrWhiteSpace(documentPath))
                {
                    return new SelectionSnapshot(documentPath, null, null, null);
                }

                return Empty;
            }

            public bool Equals(SelectionSnapshot other)
            {
                if (NodeId.HasValue && other.NodeId.HasValue)
                {
                    if (!NodeId.Value.Equals(other.NodeId.Value))
                    {
                        return false;
                    }

                    if (!PathsEqual(DocumentPath, other.DocumentPath))
                    {
                        return false;
                    }

                    if (StartLine.HasValue && other.StartLine.HasValue)
                    {
                        var leftEnd = EndLine ?? StartLine.Value;
                        var rightEnd = other.EndLine ?? other.StartLine.Value;
                        return StartLine.Value == other.StartLine.Value && leftEnd == rightEnd;
                    }

                    return !StartLine.HasValue && !other.StartLine.HasValue;
                }

                if (!PathsEqual(DocumentPath, other.DocumentPath))
                {
                    return false;
                }

                if (StartLine.HasValue && other.StartLine.HasValue)
                {
                    var leftStart = StartLine.Value;
                    var rightStart = other.StartLine.Value;
                    var leftEnd = EndLine ?? leftStart;
                    var rightEnd = other.EndLine ?? rightStart;
                    return leftStart == rightStart && leftEnd == rightEnd;
                }

                return !StartLine.HasValue && !other.StartLine.HasValue;
            }

            public override int GetHashCode()
            {
                var pathHash = GetPathHash(DocumentPath);
                var nodeHash = NodeId.HasValue ? NodeId.Value.GetHashCode() : 0;
                var spanHash = StartLine.HasValue ? CombineHash(StartLine.Value, EndLine ?? StartLine.Value) : 0;

                var hash = 17;
                hash = (hash * 31) + pathHash;
                hash = (hash * 31) + nodeHash;
                hash = (hash * 31) + spanHash;
                return hash;
            }

            private static bool PathsEqual(string? left, string? right)
            {
                var leftNull = string.IsNullOrWhiteSpace(left);
                var rightNull = string.IsNullOrWhiteSpace(right);

                if (leftNull || rightNull)
                {
                    return leftNull && rightNull;
                }

                return PathComparer.Equals(left!, right!);
            }

            private static int GetPathHash(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return 0;
                }

                return PathComparer.GetHashCode(path);
            }

            private static int CombineHash(int left, int right)
            {
                unchecked
                {
                    var hash = 17;
                    hash = (hash * 31) + left;
                    hash = (hash * 31) + right;
                    return hash;
                }
            }
        }

        private sealed class PublishToken : IDisposable
        {
            private readonly SelectionCoordinator _owner;
            private readonly string _ownerId;
            private bool _disposed;

            public PublishToken(SelectionCoordinator owner, string ownerId)
            {
                _owner = owner;
                _ownerId = ownerId;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _owner.EndPublish(_ownerId);
            }
        }
    }
}
