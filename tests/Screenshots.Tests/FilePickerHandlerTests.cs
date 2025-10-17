using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Diagnostics.Screenshots;
using Xunit;

namespace Screenshots.Tests;

public class FilePickerHandlerTests
{
    [Fact]
    public async Task Take_WithoutTopLevel_Throws()
    {
        var handler = new FilePickerHandler();
        var control = new Control();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Take(control));
    }

    [Fact]
    public async Task Take_RequestsStreamWhenAvailable()
    {
        var handler = new TestStreamHandler();
        var control = new Control();

        await handler.Take(control);

        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class TestStreamHandler : BaseRenderToStreamHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<Stream?> GetStream(Control control)
        {
            RequestCount++;
            return Task.FromResult<Stream?>(new MemoryStream());
        }
    }
}
