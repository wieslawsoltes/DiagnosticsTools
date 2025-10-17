using System;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Avalonia.Diagnostics.SourceNavigation
{
    public readonly record struct XamlDocumentRequest(Type RootType, SourceInfo RootSource);

    public sealed record XamlDocumentResult(XDocument Document, SourceInfo Source);

    public interface IXamlDocumentLocator
    {
        ValueTask<XamlDocumentResult?> GetDocumentAsync(
            XamlDocumentRequest request,
            CancellationToken cancellationToken = default);
    }
}
