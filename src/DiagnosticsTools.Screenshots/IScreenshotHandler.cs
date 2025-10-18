using System.Threading.Tasks;
using Avalonia.Controls;

namespace Avalonia.Diagnostics;

/// <summary>
/// Contract for handling screenshots of Avalonia controls.
/// </summary>
public interface IScreenshotHandler
{
    /// <summary>
    /// Captures a screenshot for the supplied control.
    /// </summary>
    /// <param name="control">The control to capture.</param>
    Task Take(Control control);
}

