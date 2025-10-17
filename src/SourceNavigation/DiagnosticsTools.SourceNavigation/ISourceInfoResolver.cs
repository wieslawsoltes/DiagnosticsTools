using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.SourceNavigation
{
    public interface ISourceInfoResolver
    {
        ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member, CancellationToken cancellationToken = default);

        ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic, CancellationToken cancellationToken = default);
    }
}
