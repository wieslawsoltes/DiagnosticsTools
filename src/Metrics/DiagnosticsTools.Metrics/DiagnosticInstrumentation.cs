using System;
using System.Reflection;
using System.Threading;
using Avalonia;

namespace Avalonia.Diagnostics.Metrics
{
    internal static class DiagnosticInstrumentation
    {
        private static int s_initialized;

        public static void EnsureInitialized()
        {
            if (Interlocked.CompareExchange(ref s_initialized, 1, 0) == 1)
            {
                return;
            }

            AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);

            var baseAssembly = typeof(AvaloniaObject).Assembly;
            var diagnosticType = baseAssembly.GetType("Avalonia.Diagnostics.Diagnostic");

            if (diagnosticType is null)
            {
                return;
            }

            InvokeInitializer(diagnosticType, "InitActivitySource");
            InvokeInitializer(diagnosticType, "InitMetrics");
        }

        private static void InvokeInitializer(Type diagnosticType, string methodName)
        {
            var method = diagnosticType.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            method?.Invoke(null, Array.Empty<object>());
        }
    }
}
