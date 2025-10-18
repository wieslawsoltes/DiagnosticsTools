using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.SourceNavigation
{
    /// <summary>
    /// Resolves runtime objects (members or diagnostics) to their backing source metadata.
    /// </summary>
    public interface ISourceInfoResolver
    {
        /// <summary>
        /// Attempts to locate the source information for the provided member.
        /// </summary>
        /// <param name="member">The reflection member to resolve.</param>
        /// <param name="cancellationToken">Cancellation token for the asynchronous lookup.</param>
        ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member, CancellationToken cancellationToken = default);

        /// <summary>
        /// Attempts to locate source information for a diagnostics object produced by the tooling.
        /// </summary>
        /// <param name="valueFrameDiagnostic">The diagnostics object or value frame to resolve.</param>
        /// <param name="cancellationToken">Cancellation token for the asynchronous lookup.</param>
        ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic, CancellationToken cancellationToken = default);
    }
}
