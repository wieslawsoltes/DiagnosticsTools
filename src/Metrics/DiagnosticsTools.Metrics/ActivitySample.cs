using System;

namespace Avalonia.Diagnostics.Metrics
{
    public sealed class ActivitySample
    {
        public ActivitySample(string name, TimeSpan duration, DateTimeOffset startTime, string? parentId, string? id)
        {
            Name = name;
            Duration = duration;
            StartTime = startTime;
            ParentId = parentId;
            Id = id;
        }

        public string Name { get; }

        public TimeSpan Duration { get; }

        public DateTimeOffset StartTime { get; }

        public string? ParentId { get; }

        public string? Id { get; }
    }
}
