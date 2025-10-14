using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Diagnostics;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Avalonia.Diagnostics.SourceNavigation
{
    internal sealed class SourceInfoService : ISourceInfoService, IDisposable
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
        private readonly ConcurrentDictionary<string, Task<PortablePdbResolver?>> _resolverCache = new(PathComparer);
        private readonly ConcurrentBag<PortablePdbResolver> _ownedResolvers = new();
        private bool _disposed;

        public async ValueTask<SourceInfo?> GetForMemberAsync(MemberInfo member)
        {
            ThrowIfDisposed();
            if (member is null)
            {
                throw new ArgumentNullException(nameof(member));
            }

            return await ResolveMemberAsync(member).ConfigureAwait(false);
        }

        public async ValueTask<SourceInfo?> GetForAvaloniaObjectAsync(AvaloniaObject avaloniaObject)
        {
            ThrowIfDisposed();
            if (avaloniaObject is null)
            {
                throw new ArgumentNullException(nameof(avaloniaObject));
            }

            var type = avaloniaObject.GetType();
            var info = await ResolveMemberAsync(type).ConfigureAwait(false);
            if (info is not null)
            {
                return info;
            }

            if (avaloniaObject is StyledElement { StyleKey: Type styleKey })
            {
                info = await ResolveMemberAsync(styleKey).ConfigureAwait(false);
                if (info is not null)
                {
                    return info;
                }
            }

            return null;
        }

        public async ValueTask<SourceInfo?> GetForValueFrameAsync(object? valueFrameDiagnostic)
        {
            ThrowIfDisposed();

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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var resolver in _ownedResolvers)
            {
                resolver.Dispose();
            }
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
                    return await ResolveMemberAsync(member).ConfigureAwait(false);
            }

            return await ResolveMemberAsync(source.GetType()).ConfigureAwait(false);
        }

        private async ValueTask<SourceInfo?> ResolveMemberAsync(MemberInfo member)
        {
            switch (member)
            {
                case MethodBase method:
                {
                    var resolver = await GetResolverAsync(method.DeclaringType?.Assembly ?? method.Module.Assembly).ConfigureAwait(false);
                    if (resolver is null)
                    {
                        return null;
                    }

                    return await resolver.TryGetSourceInfoAsync(method).ConfigureAwait(false);
                }

                case Type type:
                {
                    var resolver = await GetResolverAsync(type.Assembly).ConfigureAwait(false);
                    if (resolver is null)
                    {
                        return null;
                    }

                    return await resolver.TryGetSourceInfoAsync(type).ConfigureAwait(false);
                }

                case PropertyInfo property:
                {
                    if (property.GetMethod is { } getter)
                    {
                        var info = await ResolveMemberAsync(getter).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    if (property.SetMethod is { } setter)
                    {
                        var info = await ResolveMemberAsync(setter).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    break;
                }

                case EventInfo evt:
                {
                    if (evt.AddMethod is { } adder)
                    {
                        var info = await ResolveMemberAsync(adder).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    if (evt.RemoveMethod is { } remover)
                    {
                        var info = await ResolveMemberAsync(remover).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    break;
                }
            }

            return null;
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

        private async ValueTask<PortablePdbResolver?> GetResolverAsync(Assembly? assembly)
        {
            if (assembly is null)
            {
                return null;
            }

            var location = assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                return null;
            }

            var task = _resolverCache.GetOrAdd(location, path => CreateResolverAsync(path));
            return await task.ConfigureAwait(false);
        }

        private async Task<PortablePdbResolver?> CreateResolverAsync(string assemblyPath)
        {
            var resolver = new PortablePdbResolver(assemblyPath);
            if (await resolver.EnsureMetadataAsync().ConfigureAwait(false))
            {
                _ownedResolvers.Add(resolver);
                return resolver;
            }

            resolver.Dispose();
            return null;
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
