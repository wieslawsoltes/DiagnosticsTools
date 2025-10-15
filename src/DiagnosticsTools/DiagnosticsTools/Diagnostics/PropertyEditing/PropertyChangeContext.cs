using System;
using Avalonia;
using Avalonia.Diagnostics.Xaml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class PropertyChangeContext
    {
        public PropertyChangeContext(
            AvaloniaObject target,
            AvaloniaProperty property,
            XamlAstDocument document,
            XamlAstNodeDescriptor descriptor,
            string frame,
            string valueSource)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Property = property ?? throw new ArgumentNullException(nameof(property));
            Document = document ?? throw new ArgumentNullException(nameof(document));
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            ValueSource = valueSource ?? throw new ArgumentNullException(nameof(valueSource));
        }

        public AvaloniaObject Target { get; }

        public AvaloniaProperty Property { get; }

        public XamlAstDocument Document { get; }

        public XamlAstNodeDescriptor Descriptor { get; }

        public string Frame { get; }

        public string ValueSource { get; }
    }
}
