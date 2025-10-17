using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.SourceNavigation
{
    /// <summary>
    /// Resolves <see cref="SourceInfo"/> for CLR members and diagnostic objects by inspecting portable PDB metadata.
    /// </summary>
    public sealed class SourceInfoResolver : ISourceInfoResolver, IDisposable
    {
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        private readonly ConcurrentDictionary<string, Task<PortablePdbResolver?>> _resolverCache = new(PathComparer);
        private readonly ConcurrentBag<PortablePdbResolver> _ownedResolvers = new();
        private readonly Func<Assembly, string?> _assemblyLocationSelector;
        private readonly Func<object?, CancellationToken, ValueTask<SourceInfo?>>? _valueFrameResolver;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SourceInfoResolver"/> class.
        /// </summary>
        /// <param name="assemblyLocationSelector">Optional selector used to determine the on-disk location of an assembly.</param>
        /// <param name="valueFrameResolver">Optional delegate used to resolve non-member diagnostics.</param>
        public SourceInfoResolver(
            Func<Assembly, string?>? assemblyLocationSelector = null,
            Func<object?, CancellationToken, ValueTask<SourceInfo?>>? valueFrameResolver = null)
        {
            _assemblyLocationSelector = assemblyLocationSelector ?? (assembly => assembly.Location);
            _valueFrameResolver = valueFrameResolver;
        }

        /// <inheritdoc />
        public async ValueTask<SourceInfo?> GetForMemberAsync(
            MemberInfo member,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (member is null)
            {
                throw new ArgumentNullException(nameof(member));
            }

            switch (member)
            {
                case MethodBase method:
                {
                    var resolver = await GetResolverAsync(method.DeclaringType?.Assembly ?? method.Module.Assembly, cancellationToken).ConfigureAwait(false);
                    if (resolver is null)
                    {
                        return null;
                    }

                    return await resolver.TryGetSourceInfoAsync(method).ConfigureAwait(false);
                }

                case Type type:
                {
                    var resolver = await GetResolverAsync(type.Assembly, cancellationToken).ConfigureAwait(false);
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
                        var info = await GetForMemberAsync(getter, cancellationToken).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    if (property.SetMethod is { } setter)
                    {
                        var info = await GetForMemberAsync(setter, cancellationToken).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    break;
                }

                case EventInfo @event:
                {
                    if (@event.AddMethod is { } adder)
                    {
                        var info = await GetForMemberAsync(adder, cancellationToken).ConfigureAwait(false);
                        if (info is not null)
                        {
                            return info;
                        }
                    }

                    if (@event.RemoveMethod is { } remover)
                    {
                        var info = await GetForMemberAsync(remover, cancellationToken).ConfigureAwait(false);
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

        /// <inheritdoc />
        public ValueTask<SourceInfo?> GetForValueFrameAsync(
            object? valueFrameDiagnostic,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (_valueFrameResolver is null)
            {
                return new ValueTask<SourceInfo?>((SourceInfo?)null);
            }

            return _valueFrameResolver(valueFrameDiagnostic, cancellationToken);
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

            _resolverCache.Clear();
        }

        private async ValueTask<PortablePdbResolver?> GetResolverAsync(Assembly? assembly, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (assembly is null)
            {
                return null;
            }

            var location = _assemblyLocationSelector(assembly);
            if (string.IsNullOrEmpty(location))
            {
                return null;
            }

            var resolverTask = _resolverCache.GetOrAdd(location!, CreateResolverAsync);
            var resolver = await resolverTask.ConfigureAwait(false);

            if (resolver is null)
            {
                _resolverCache.TryRemove(location!, out _);
            }

            return resolver;
        }

        private Task<PortablePdbResolver?> CreateResolverAsync(string assemblyPath)
        {
            return Task.Run(async () =>
            {
                if (!File.Exists(assemblyPath))
                {
                    return null;
                }

                var resolver = new PortablePdbResolver(assemblyPath);
                if (!await resolver.EnsureMetadataAsync().ConfigureAwait(false))
                {
                    resolver.Dispose();
                    return null;
                }

                _ownedResolvers.Add(resolver);
                return resolver;
            });
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SourceInfoResolver));
            }
        }
    }
}
