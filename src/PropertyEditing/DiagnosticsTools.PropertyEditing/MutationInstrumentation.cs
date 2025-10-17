using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Avalonia.Diagnostics.Metrics;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal static class MutationInstrumentation
    {
        private static readonly Meter s_meter = new("Avalonia.Diagnostics.Mutations", "1.0");
        private static readonly Histogram<double> s_mutationDuration = s_meter.CreateHistogram<double>(MetricIdentifiers.Histograms.DiagnosticsMutationDuration);
        private static readonly Counter<long> s_guardFailures = s_meter.CreateCounter<long>(MetricIdentifiers.Counters.DiagnosticsMutationGuardFailures);
        private static readonly Histogram<double> s_astReloadDuration = s_meter.CreateHistogram<double>(MetricIdentifiers.Histograms.DiagnosticsAstReloadDuration);
        private static readonly Histogram<double> s_astIndexBuildDuration = s_meter.CreateHistogram<double>(MetricIdentifiers.Histograms.DiagnosticsAstIndexBuildDuration);

        public static void RecordMutation(TimeSpan duration, ChangeDispatchStatus status)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            var tags = new TagList
            {
                { "status", status.ToString() }
            };

            s_mutationDuration.Record(duration.TotalMilliseconds, tags);

            if (status == ChangeDispatchStatus.GuardFailure)
            {
                s_guardFailures.Add(1);
            }
        }

        public static void RecordAstReload(TimeSpan duration, string scope, bool cacheHit)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            var tags = new TagList
            {
                { "scope", scope },
                { "cacheHit", cacheHit }
            };

            s_astReloadDuration.Record(duration.TotalMilliseconds, tags);
        }

        public static void RecordAstIndexBuild(TimeSpan duration, string scope, bool cacheHit)
        {
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            var tags = new TagList
            {
                { "scope", scope },
                { "cacheHit", cacheHit }
            };

            s_astIndexBuildDuration.Record(duration.TotalMilliseconds, tags);
        }
    }
}
