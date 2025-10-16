using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.ViewModels;

namespace Avalonia.Diagnostics.Views
{
    internal sealed partial class MutationPreviewWindow : Window
    {
        public MutationPreviewWindow()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public Task<MutationPreviewDecision> ShowDialogAsync(Window owner, MutationPreviewResult preview, bool allowRawEditing)
        {
            var viewModel = new MutationPreviewDialogViewModel(preview, allowRawEditing);
            void OnCloseRequested(object? _, MutationPreviewDecision decision)
            {
                viewModel.CloseRequested -= OnCloseRequested;
                Closed -= OnWindowClosed;
                Close(decision);
            }

            void OnWindowClosed(object? _, EventArgs __)
            {
                viewModel.CloseRequested -= OnCloseRequested;
                Closed -= OnWindowClosed;
            }

            viewModel.CloseRequested += OnCloseRequested;
            Closed += OnWindowClosed;
            DataContext = viewModel;
            return ShowDialog<MutationPreviewDecision>(owner);
        }
    }
}
