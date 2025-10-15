using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal interface IChangeDispatcher
    {
        ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, CancellationToken cancellationToken = default);
    }
}
