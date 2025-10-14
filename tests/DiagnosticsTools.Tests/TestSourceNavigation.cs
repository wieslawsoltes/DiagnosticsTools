using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Diagnostics.SourceNavigation;

namespace DiagnosticsTools.Tests
{
    internal sealed class StubSourceInfoService : ISourceInfoService
    {
        public ValueTask<SourceInfo?> GetForAvaloniaObjectAsync(AvaloniaObject avaloniaObject)
        {
            return ValueTask.FromResult<SourceInfo?>(null);
        }

        public ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member)
        {
            return ValueTask.FromResult<SourceInfo?>(null);
        }

        public ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic)
        {
            return ValueTask.FromResult<SourceInfo?>(null);
        }
    }

    internal sealed class StubSourceNavigator : ISourceNavigator
    {
        public ValueTask NavigateAsync(SourceInfo sourceInfo)
        {
            return ValueTask.CompletedTask;
        }
    }
}
