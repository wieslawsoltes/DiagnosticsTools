using System;

namespace Avalonia.Diagnostics.Xaml
{
    public interface IXamlAstInstrumentation
    {
        void RecordAstReload(TimeSpan duration, string scope, bool cacheHit);

        void RecordAstIndexBuild(TimeSpan duration, string scope, bool cacheHit);
    }

    /// <summary>
    /// Default instrumentation implementation that ignores all events.
    /// </summary>
    public sealed class NullXamlAstInstrumentation : IXamlAstInstrumentation
    {
        public static NullXamlAstInstrumentation Instance { get; } = new();

        public void RecordAstReload(TimeSpan duration, string scope, bool cacheHit)
        {
        }

        public void RecordAstIndexBuild(TimeSpan duration, string scope, bool cacheHit)
        {
        }
    }
}
