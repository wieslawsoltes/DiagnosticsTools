using System;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal readonly record struct EditorCommandDescriptor(string Id, string DisplayName, string? InputHint = null)
    {
        internal static EditorCommandDescriptor Default { get; } = new("setLocalValue", "Set Value");
        internal static EditorCommandDescriptor Toggle { get; } = new("toggle", "Toggle", "boolean");
        internal static EditorCommandDescriptor Slider { get; } = new("slider", "Adjust Slider", "range");
        internal static EditorCommandDescriptor ColorPicker { get; } = new("colorPicker", "Color Picker", "color");
        internal static EditorCommandDescriptor BindingEditor { get; } = new("bindingEditor", "Binding Editor", "binding");

        internal ChangeSourceCommandInfo? ToCommandInfo()
        {
            var normalizedId = string.IsNullOrWhiteSpace(Id) ? null : Id;
            if (normalizedId is null)
            {
                return null;
            }

            return new ChangeSourceCommandInfo
            {
                Id = normalizedId,
                DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName,
                Input = string.IsNullOrWhiteSpace(InputHint) ? null : InputHint
            };
        }

        internal static EditorCommandDescriptor Normalize(EditorCommandDescriptor? descriptor)
        {
            if (descriptor is null)
            {
                return Default;
            }

            return string.IsNullOrWhiteSpace(descriptor.Value.Id) ? Default : descriptor.Value;
        }

        internal static EditorCommandDescriptor Normalize(EditorCommandDescriptor descriptor)
        {
            return string.IsNullOrWhiteSpace(descriptor.Id) ? Default : descriptor;
        }
    }
}
