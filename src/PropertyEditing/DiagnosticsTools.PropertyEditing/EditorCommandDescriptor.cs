using System;

namespace Avalonia.Diagnostics.PropertyEditing
{
    public readonly record struct EditorCommandDescriptor(string Id, string DisplayName, string? InputHint = null)
    {
        public static EditorCommandDescriptor Default { get; } = new("setLocalValue", "Set Value");
        public static EditorCommandDescriptor Toggle { get; } = new("toggle", "Toggle", "boolean");
        public static EditorCommandDescriptor Slider { get; } = new("slider", "Adjust Slider", "range");
        public static EditorCommandDescriptor ColorPicker { get; } = new("colorPicker", "Color Picker", "color");
        public static EditorCommandDescriptor BindingEditor { get; } = new("bindingEditor", "Binding Editor", "binding");

        public ChangeSourceCommandInfo? ToCommandInfo()
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

        public static EditorCommandDescriptor Normalize(EditorCommandDescriptor? descriptor)
        {
            if (descriptor is null)
            {
                return Default;
            }

            return string.IsNullOrWhiteSpace(descriptor.Value.Id) ? Default : descriptor.Value;
        }

        public static EditorCommandDescriptor Normalize(EditorCommandDescriptor descriptor)
        {
            return string.IsNullOrWhiteSpace(descriptor.Id) ? Default : descriptor;
        }
    }
}
