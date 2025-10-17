using Avalonia;

namespace Avalonia.Diagnostics.Runtime;

/// <summary>
/// Represents a visual tree node that can be mutated by the runtime coordinator.
/// </summary>
public interface IMutableTreeNode
{
    /// <summary>
    /// Gets the parent node in the visual tree, if any.
    /// </summary>
    IMutableTreeNode? Parent { get; }

    /// <summary>
    /// Gets the associated Avalonia object for the node.
    /// </summary>
    AvaloniaObject Visual { get; }
}

