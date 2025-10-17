using System;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Avalonia.Diagnostics.SourceNavigation
{
    /// <summary>
    /// Parameters describing a document lookup request.
    /// </summary>
    public readonly record struct XamlDocumentRequest(Type RootType, SourceInfo RootSource);

    /// <summary>
    /// Represents a XAML document retrieved from an <see cref="IXamlDocumentLocator"/>.
    /// </summary>
    public sealed record XamlDocumentResult(XDocument Document, SourceInfo Source);

    /// <summary>
    /// Retrieves XAML documents for a given type/source combination.
    /// </summary>
    public interface IXamlDocumentLocator
    {
        /// <summary>
        /// Attempts to load the document associated with the specified request.
        /// </summary>
        ValueTask<XamlDocumentResult?> GetDocumentAsync(
            XamlDocumentRequest request,
            CancellationToken cancellationToken = default);
    }
}
