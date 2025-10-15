using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Diagnostics.SourceNavigation;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Xunit;

namespace DiagnosticsTools.Tests
{
    public sealed class SourceInfoServiceTests
    {
        [AvaloniaFact]
        public async Task GetForAvaloniaObjectAsync_returns_line_info_for_visual_tree_element()
        {
            using var service = new SourceInfoService();
            var window = new DiagnosticsToolsSample.MainWindow();

            var textBlock = window.GetLogicalDescendants()
                .OfType<TextBlock>()
                .First(tb => string.Equals(tb.Text, "Default Button", StringComparison.Ordinal));

            var info = await service.GetForAvaloniaObjectAsync(textBlock);

            Assert.NotNull(info);
            Assert.True(info!.StartLine >= 11 && info.StartLine <= 20);
            var hasLocalPath = info.LocalPath?.EndsWith("MainWindow.axaml", StringComparison.OrdinalIgnoreCase) == true;
            var hasRemotePath = info.RemoteUri?.AbsoluteUri.EndsWith("MainWindow.axaml", StringComparison.OrdinalIgnoreCase) == true;
            Assert.True(hasLocalPath || hasRemotePath);
        }
    }
}
