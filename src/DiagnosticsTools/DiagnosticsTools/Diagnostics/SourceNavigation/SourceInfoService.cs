using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using Avalonia;
using Avalonia.Diagnostics;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Avalonia.Diagnostics.SourceNavigation
{
    internal sealed class SourceInfoService : ISourceInfoService, IDisposable
    {
        private readonly SourceInfoResolver _resolver;
        private readonly XamlSourceResolver _xamlResolver;
        private bool _disposed;

        public SourceInfoService()
        {
            _resolver = new SourceInfoResolver(valueFrameResolver: ResolveValueFrameAsync);
            _xamlResolver = new XamlSourceResolver(
                new AvaloniaLogicalTreePathBuilder(),
                new AvaloniaXamlDocumentLocator(),
                (type, ct) => _resolver.GetForMemberAsync(type, ct));
        }

        public async ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member)
        {
            ThrowIfDisposed();
            if (member is null)
            {
                throw new ArgumentNullException(nameof(member));
            }

            return await _resolver.GetForMemberAsync(member).ConfigureAwait(false);
        }

        public async ValueTask<SourceInfo?> GetForAvaloniaObjectAsync(AvaloniaObject avaloniaObject)
        {
            ThrowIfDisposed();
            if (avaloniaObject is null)
            {
                throw new ArgumentNullException(nameof(avaloniaObject));
            }

            var type = avaloniaObject.GetType();
            var info = await _resolver.GetForMemberAsync(type).ConfigureAwait(false);
            if (info is not null)
            {
                return info;
            }

            if (avaloniaObject is StyledElement { StyleKey: Type styleKey })
            {
                info = await _resolver.GetForMemberAsync(styleKey).ConfigureAwait(false);
                if (info is not null)
                {
                    return info;
                }
            }

            info = await _xamlResolver.TryResolveAsync(avaloniaObject).ConfigureAwait(false);
            if (info is not null)
            {
                return info;
            }

            return null;
        }

        public ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic)
        {
            ThrowIfDisposed();

            return _resolver.GetForValueFrameAsync(valueFrameDiagnostic);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _resolver.Dispose();
        }

        private async ValueTask<SourceInfo?> ResolveValueFrameAsync(object? valueFrameDiagnostic, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (valueFrameDiagnostic is null)
            {
                return null;
            }

            if (valueFrameDiagnostic is IValueFrameDiagnostic frame)
            {
                if (frame.Source is { } source)
                {
                    var info = await TryResolveFrameSourceAsync(source).ConfigureAwait(false);
                    if (info is not null)
                    {
                        return info;
                    }
                }

                return await TryResolveFrameSourceAsync(frame).ConfigureAwait(false);
            }

            return await TryResolveFrameSourceAsync(valueFrameDiagnostic).ConfigureAwait(false);
        }

        private async ValueTask<SourceInfo?> TryResolveFrameSourceAsync(object source)
        {
            switch (source)
            {
                case ControlTheme theme:
                {
                    var info = ResolveFluentControlTheme(theme);
                    if (info is not null)
                    {
                        return info;
                    }

                    break;
                }

                case StyleBase { Parent: ControlTheme parentTheme }:
                {
                    var info = ResolveFluentControlTheme(parentTheme);
                    if (info is not null)
                    {
                        return info;
                    }

                    break;
                }

                case AvaloniaObject avaloniaObject:
                {
                    var info = await GetForAvaloniaObjectAsync(avaloniaObject).ConfigureAwait(false);
                    if (info is not null)
                    {
                        return info;
                    }

                    break;
                }

                case MemberInfo member:
                    return await _resolver.GetForMemberAsync(member).ConfigureAwait(false);
            }

            return await _resolver.GetForMemberAsync(source.GetType()).ConfigureAwait(false);
        }

        private SourceInfo? ResolveFluentControlTheme(ControlTheme theme)
        {
            if (theme.TargetType is null)
            {
                return null;
            }

            var fileName = theme.TargetType.Name;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var remoteUri = BuildFluentThemeUri($"src/Avalonia.Themes.Fluent/Controls/{fileName}.axaml");
            if (remoteUri is null)
            {
                return null;
            }

            return new SourceInfo(
                LocalPath: null,
                RemoteUri: remoteUri,
                StartLine: null,
                StartColumn: null,
                EndLine: null,
                EndColumn: null,
                Origin: SourceOrigin.SourceLink);
        }

        private static Uri? BuildFluentThemeUri(string relativePath)
        {
            var assembly = typeof(FluentTheme).Assembly;
            var version = assembly.GetName().Version;

            string tag;
            if (version is null)
            {
                tag = "master";
            }
            else if (version.Build > 0)
            {
                tag = $"v{version.Major}.{version.Minor}.{version.Build}";
            }
            else
            {
                tag = $"v{version.Major}.{version.Minor}";
            }

            var uriString = $"https://raw.githubusercontent.com/AvaloniaUI/Avalonia/{tag}/{relativePath}";
            return Uri.TryCreate(uriString, UriKind.Absolute, out var uri) ? uri : null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SourceInfoService));
            }
        }

    }
}
