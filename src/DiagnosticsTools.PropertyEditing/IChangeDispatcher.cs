using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.PropertyEditing
{
    public interface IChangeDispatcher
    {
        ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, CancellationToken cancellationToken = default);
    }
}
