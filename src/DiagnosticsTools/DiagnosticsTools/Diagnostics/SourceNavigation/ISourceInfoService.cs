using System.Reflection;
using System.Threading.Tasks;
using Avalonia;

namespace Avalonia.Diagnostics.SourceNavigation
{
    public interface ISourceInfoService
    {
        ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member);

        ValueTask<SourceInfo?> GetForAvaloniaObjectAsync(AvaloniaObject avaloniaObject);

        ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic);
    }
}
