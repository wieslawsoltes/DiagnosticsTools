using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Avalonia.Diagnostics.SourceNavigation
{
    /// <summary>
    /// Provides an abstraction for navigating to resolved source locations.
    /// </summary>
    public interface ISourceNavigator
    {
        /// <summary>
        /// Launches the appropriate viewer for the provided source information.
        /// </summary>
        ValueTask NavigateAsync(SourceInfo sourceInfo);
    }

    /// <summary>
    /// Launches local editors or browser instances for a given <see cref="SourceInfo"/>.
    /// </summary>
    public sealed class DefaultSourceNavigator : ISourceNavigator
    {
        /// <inheritdoc />
        public ValueTask NavigateAsync(SourceInfo sourceInfo)
        {
            if (sourceInfo is null)
            {
                throw new ArgumentNullException(nameof(sourceInfo));
            }

            if (!string.IsNullOrWhiteSpace(sourceInfo.LocalPath) && File.Exists(sourceInfo.LocalPath))
            {
                LaunchLocal(sourceInfo.LocalPath!);
                return default;
            }

            if (sourceInfo.RemoteUri is { } remote)
            {
                LaunchUri(remote);
            }

            return default;
        }

        private static void LaunchLocal(string path)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                    });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = false,
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{path}\"",
                        UseShellExecute = false,
                    });
                }
            }
            catch (Exception)
            {
                // Navigation failures are non-fatal; ignore.
            }
        }

        private static void LaunchUri(Uri uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri.ToString(),
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // Navigation failures are non-fatal; ignore.
            }
        }
    }
}
