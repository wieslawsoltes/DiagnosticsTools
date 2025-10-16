using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal interface IMutationTelemetrySink
    {
        void Report(MutationTelemetryEvent telemetryEvent);
    }

    internal sealed record MutationTelemetryEvent(
        string Inspector,
        string Gesture,
        string Frame,
        string ValueSource,
        IReadOnlyList<string> ChangeTypes,
        IReadOnlyList<string> ValueKinds,
        ChangeDispatchStatus Outcome,
        double DurationMilliseconds,
        int ChangeCount,
        long? DocumentLength,
        string? CommandId,
        MutationProvenance Provenance);

    internal static class MutationTelemetry
    {
        private static readonly object s_gate = new();
        private static IMutationTelemetrySink[] s_sinks = Array.Empty<IMutationTelemetrySink>();

        public static void RegisterSink(IMutationTelemetrySink sink)
        {
            if (sink is null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            lock (s_gate)
            {
                var current = s_sinks;
                var updated = new IMutationTelemetrySink[current.Length + 1];
                Array.Copy(current, updated, current.Length);
                updated[current.Length] = sink;
                s_sinks = updated;
            }
        }

        public static void UnregisterSink(IMutationTelemetrySink sink)
        {
            if (sink is null)
            {
                return;
            }

            lock (s_gate)
            {
                var current = s_sinks;
                var index = Array.IndexOf(current, sink);
                if (index < 0)
                {
                    return;
                }

                if (current.Length == 1)
                {
                    s_sinks = Array.Empty<IMutationTelemetrySink>();
                    return;
                }

                var updated = new IMutationTelemetrySink[current.Length - 1];
                if (index > 0)
                {
                    Array.Copy(current, 0, updated, 0, index);
                }

                if (index < current.Length - 1)
                {
                    Array.Copy(current, index + 1, updated, index, current.Length - index - 1);
                }

                s_sinks = updated;
            }
        }

        public static void ReportMutation(ChangeEnvelope envelope, ChangeDispatchResult result, TimeSpan duration, MutationProvenance provenance)
        {
            var sinks = SnapshotSinks();
            if (sinks.Length == 0)
            {
                return;
            }

            var telemetryEvent = CreateEvent(envelope, result, duration, provenance);

            foreach (var sink in sinks)
            {
                try
                {
                    sink.Report(telemetryEvent);
                }
                catch
                {
                    // Telemetry sinks must not affect diagnostics behavior.
                }
            }
        }

        private static IMutationTelemetrySink[] SnapshotSinks() => Volatile.Read(ref s_sinks);

        private static MutationTelemetryEvent CreateEvent(ChangeEnvelope envelope, ChangeDispatchResult result, TimeSpan duration, MutationProvenance provenance)
        {
            var inspector = envelope?.Source?.Inspector ?? string.Empty;
            var gesture = envelope?.Source?.Gesture ?? string.Empty;
            var commandId = envelope?.Source?.Command?.Id;
            var frame = envelope?.Context?.Frame ?? string.Empty;
            var valueSource = envelope?.Context?.ValueSource ?? string.Empty;

            var changeTypes = new HashSet<string>(StringComparer.Ordinal);
            var valueKinds = new HashSet<string>(StringComparer.Ordinal);

            IReadOnlyList<ChangeOperation> operations = envelope?.Changes ?? Array.Empty<ChangeOperation>();
            for (var index = 0; index < operations.Count; index++)
            {
                var operation = operations[index];
                if (operation is null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(operation.Type))
                {
                    changeTypes.Add(operation.Type);
                }

                var payload = operation.Payload;
                if (payload is not null && !string.IsNullOrWhiteSpace(payload.ValueKind))
                {
                    valueKinds.Add(payload.ValueKind);
                }
            }

            var documentLength = TryGetDocumentLength(envelope?.Document?.Version);

            return new MutationTelemetryEvent(
                inspector,
                gesture,
                frame,
                valueSource,
                changeTypes.ToArray(),
                valueKinds.ToArray(),
                result.Status,
                Math.Max(0, duration.TotalMilliseconds),
                operations.Count,
                documentLength,
                commandId,
                provenance);
        }

        private static long? TryGetDocumentLength(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var lastColon = version.LastIndexOf(':');
            if (lastColon <= 0)
            {
                return null;
            }

            var secondLastColon = version.LastIndexOf(':', lastColon - 1);
            if (secondLastColon <= 0)
            {
                return null;
            }

            var lengthSlice = version.Substring(secondLastColon + 1, lastColon - secondLastColon - 1);
            if (long.TryParse(lengthSlice, out var length))
            {
                return length;
            }

            return null;
        }
    }
}
