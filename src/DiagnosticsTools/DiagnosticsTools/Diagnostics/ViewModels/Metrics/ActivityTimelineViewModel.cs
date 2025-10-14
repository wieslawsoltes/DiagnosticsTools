using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Diagnostics.Metrics;
using Avalonia.Threading;

namespace Avalonia.Diagnostics.ViewModels.Metrics
{
    internal class ActivityTimelineViewModel : ViewModelBase
    {
        private readonly object _sync = new();
        private readonly ObservableCollection<ActivityGroupViewModel> _groups = new();
        private string? _filter;
        private TimeSpan _minimumDuration = TimeSpan.Zero;
        private bool _isPaused;

        public ActivityTimelineViewModel()
        {
            Groups = new ReadOnlyObservableCollection<ActivityGroupViewModel>(_groups);
        }

        public ReadOnlyObservableCollection<ActivityGroupViewModel> Groups { get; }

        public string? Filter
        {
            get => _filter;
            set => RaiseAndSetIfChanged(ref _filter, value);
        }

        public TimeSpan MinimumDuration
        {
            get => _minimumDuration;
            set => RaiseAndSetIfChanged(ref _minimumDuration, value);
        }

        public double MinimumDurationMilliseconds
        {
            get => MinimumDuration.TotalMilliseconds;
            set
            {
                MinimumDuration = TimeSpan.FromMilliseconds(value < 0 ? 0 : value);
                RaisePropertyChanged();
            }
        }

        public bool IsPaused
        {
            get => _isPaused;
            set => RaiseAndSetIfChanged(ref _isPaused, value);
        }

        public void Clear()
        {
            _groups.Clear();
        }

    public void Update(IReadOnlyDictionary<string, IReadOnlyCollection<ActivitySample>> snapshots)
        {
            if (IsPaused)
            {
                return;
            }

            lock (_sync)
            {
                _groups.Clear();

                foreach (var pair in snapshots.OrderBy(x => x.Key))
                {
                    var items = pair.Value
                        .Where(MatchesFilter)
                        .Select(sample => new ActivityItemViewModel(sample))
                        .ToList();

                    if (items.Count == 0)
                    {
                        continue;
                    }

                    _groups.Add(new ActivityGroupViewModel(pair.Key, items));
                }
            }
        }

        private bool MatchesFilter(ActivitySample sample)
        {
            if (sample.Duration < MinimumDuration)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(Filter))
            {
                return true;
            }

            return sample.Name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal sealed class ActivityGroupViewModel
        {
            public ActivityGroupViewModel(string name, IReadOnlyList<ActivityItemViewModel> items)
            {
                Name = name;
                Items = items;
            }

            public string Name { get; }

            public IReadOnlyList<ActivityItemViewModel> Items { get; }
        }

        internal sealed class ActivityItemViewModel
        {
            public ActivityItemViewModel(ActivitySample sample)
            {
                Name = sample.Name;
                Duration = sample.Duration;
                StartTime = sample.StartTime;
                ParentId = sample.ParentId;
                Id = sample.Id;
            }

            public string Name { get; }

            public TimeSpan Duration { get; }

            public DateTimeOffset StartTime { get; }

            public string? ParentId { get; }

            public string? Id { get; }
        }
    }
}
