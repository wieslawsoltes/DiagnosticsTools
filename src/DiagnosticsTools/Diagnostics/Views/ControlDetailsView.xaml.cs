using System;
using Avalonia.Controls;
using Avalonia.Diagnostics.ViewModels;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

namespace Avalonia.Diagnostics.Views
{
    public partial class ControlDetailsView : UserControl
    {
    private DataGrid _dataGrid;
    private ControlDetailsViewModel? _viewModel;

        public ControlDetailsView()
        {
            InitializeComponent();

            _dataGrid = this.GetControl<DataGrid>("DataGrid");
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_viewModel is not null)
            {
                _viewModel.SourcePreviewRequested -= OnSourcePreviewRequested;
            }

            _viewModel = DataContext as ControlDetailsViewModel;

            if (_viewModel is not null)
            {
                _viewModel.SourcePreviewRequested += OnSourcePreviewRequested;
            }
        }

        private void PropertiesGrid_OnDoubleTapped(object sender, TappedEventArgs e)
        {
            if (sender is DataGrid grid && grid.DataContext is ControlDetailsViewModel controlDetails)
            {
                controlDetails.NavigateToSelectedProperty();
            }
            
        }

        public void PropertyNamePressed(object sender, PointerPressedEventArgs e)
        {
            var mainVm = (ControlDetailsViewModel?) DataContext;

            if (mainVm is null)
            {
                return;
            }
            
            if (sender is Control control && control.DataContext is SetterViewModel setterVm)
            {
                mainVm.SelectProperty(setterVm.Property);

                if (mainVm.SelectedProperty is not null)
                {
                    _dataGrid.ScrollIntoView(mainVm.SelectedProperty, null);   
                }
            }
        }

        private void OnSourcePreviewRequested(object? sender, SourcePreviewViewModel e)
        {
            SourcePreviewWindow.Show(TopLevel.GetTopLevel(this), e);
        }
    }
}
