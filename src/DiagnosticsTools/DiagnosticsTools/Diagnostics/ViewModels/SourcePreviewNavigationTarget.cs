using System;
using System.Windows.Input;

namespace Avalonia.Diagnostics.ViewModels
{
    public sealed class SourcePreviewNavigationTarget
    {
        public SourcePreviewNavigationTarget(string displayName, ICommand command, object? commandParameter = null, string? description = null)
        {
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            Command = command ?? throw new ArgumentNullException(nameof(command));
            CommandParameter = commandParameter;
            Description = description;
        }

        public string DisplayName { get; }

        public string? Description { get; }

        public ICommand Command { get; }

        public object? CommandParameter { get; }
    }
}
