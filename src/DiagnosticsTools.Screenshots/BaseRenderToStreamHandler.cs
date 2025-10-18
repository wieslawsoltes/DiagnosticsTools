using System.Threading.Tasks;
using Avalonia.Controls;

namespace Avalonia.Diagnostics.Screenshots;

/// <summary>
/// Provides a base implementation that renders a control to an output stream.
/// </summary>
public abstract class BaseRenderToStreamHandler : IScreenshotHandler
{
    /// <summary>
    /// Gets the stream that should receive a rendered screenshot of the control.
    /// </summary>
    /// <param name="control">The control that will be captured.</param>
    /// <returns>A stream that receives the rendered output, or <c>null</c> to cancel the capture.</returns>
    protected abstract Task<System.IO.Stream?> GetStream(Control control);

    public async Task Take(Control control)
    {
#if NET6_0_OR_GREATER
        await using var output = await GetStream(control).ConfigureAwait(false);
#else
        using var output = await GetStream(control).ConfigureAwait(false);
#endif
        if (output is null)
        {
            return;
        }

        control.RenderTo(output);
        await output.FlushAsync().ConfigureAwait(false);
    }
}

