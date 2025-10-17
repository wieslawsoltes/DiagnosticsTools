using System;
using Avalonia.Diagnostics.Xaml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class XamlAstInstrumentationAdapter : IXamlAstInstrumentation
    {
        public static IXamlAstInstrumentation Instance { get; } = new XamlAstInstrumentationAdapter();

        private XamlAstInstrumentationAdapter()
        {
        }

        public void RecordAstReload(TimeSpan duration, string scope, bool cacheHit) =>
            MutationInstrumentation.RecordAstReload(duration, scope, cacheHit);

        public void RecordAstIndexBuild(TimeSpan duration, string scope, bool cacheHit) =>
            MutationInstrumentation.RecordAstIndexBuild(duration, scope, cacheHit);
    }
}
