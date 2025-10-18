using System.Collections.Generic;

namespace Avalonia.Diagnostics.SourceNavigation
{
    public interface ILogicalTreePathBuilder
    {
        bool TryBuildPath(object candidate, out object? root, out IReadOnlyList<int> path);
    }
}
