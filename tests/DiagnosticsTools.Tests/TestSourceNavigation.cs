using System;
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

    internal sealed class DelegatingSourceInfoService : ISourceInfoService
    {
        private readonly Func<AvaloniaObject, SourceInfo?> _objectResolver;
        private readonly Func<MemberInfo, SourceInfo?> _memberResolver;
        private readonly Func<object?, SourceInfo?> _valueFrameResolver;

        public DelegatingSourceInfoService(
            Func<AvaloniaObject, SourceInfo?>? objectResolver = null,
            Func<MemberInfo, SourceInfo?>? memberResolver = null,
            Func<object?, SourceInfo?>? valueFrameResolver = null)
        {
            _objectResolver = objectResolver ?? (_ => null);
            _memberResolver = memberResolver ?? (_ => null);
            _valueFrameResolver = valueFrameResolver ?? (_ => null);
        }

        public ValueTask<SourceInfo?> GetForAvaloniaObjectAsync(AvaloniaObject avaloniaObject)
        {
            return ValueTask.FromResult(_objectResolver(avaloniaObject));
        }

        public ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member)
        {
            return ValueTask.FromResult(_memberResolver(member));
        }

        public ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic)
        {
            return ValueTask.FromResult(_valueFrameResolver(valueFrameDiagnostic));
        }
    }
}
