using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(DiagnosticsTools.Tests.TestAppBuilder))]

namespace DiagnosticsTools.Tests
{
    internal static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<TestApp>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true
                });
        }
    }

    internal sealed class TestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }
    }
}
