using System;

namespace Avalonia.Diagnostics.ViewModels.Metrics
{
    internal sealed class MetricsSnapshotEventArgs : EventArgs
    {
        public MetricsSnapshotEventArgs(string json)
        {
            Json = json;
        }

        public string Json { get; }
    }
}
