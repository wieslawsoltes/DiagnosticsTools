namespace Avalonia.Diagnostics.PropertyEditing
{
    /// <summary>
    /// Represents a mutation to the XAML text buffer.
    /// </summary>
    public readonly record struct XamlTextEdit(int Start, int Length, string Replacement);
}
