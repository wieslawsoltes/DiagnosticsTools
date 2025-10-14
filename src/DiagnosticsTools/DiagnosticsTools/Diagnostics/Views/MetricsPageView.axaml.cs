using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics.ViewModels.Metrics;
using Avalonia.Markup.Xaml;

namespace Avalonia.Diagnostics.Views
{
    public partial class MetricsPageView : UserControl
    {
        private MetricsPageViewModel? _viewModel;

        public MetricsPageView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == DataContextProperty)
            {
                if (_viewModel != null)
                {
                    _viewModel.SnapshotRequested -= OnSnapshotRequested;
                }

                _viewModel = change.GetNewValue<object?>() as MetricsPageViewModel;

                if (_viewModel != null)
                {
                    _viewModel.SnapshotRequested += OnSnapshotRequested;
                }
            }
        }

        private async void OnSnapshotRequested(object? sender, MetricsSnapshotEventArgs e)
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(e.Json);
            }
        }
    }
}
