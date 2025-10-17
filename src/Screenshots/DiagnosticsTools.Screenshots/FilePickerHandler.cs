using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Storage.FileIO;
using Avalonia.Controls.ApplicationLifetimes;

namespace Avalonia.Diagnostics.Screenshots;

/// <summary>
/// Captures screenshots by prompting the user with a file picker dialog.
/// </summary>
public sealed class FilePickerHandler : BaseRenderToStreamHandler
{
    private readonly string _title;
    private readonly string? _screenshotRoot;

    /// <summary>
    /// Creates a handler that uses the default title and no preferred storage location.
    /// </summary>
    public FilePickerHandler()
        : this(null, null)
    {
    }

    /// <summary>
    /// Creates a handler with a custom dialog title and optional preferred storage folder.
    /// </summary>
    /// <param name="title">The title shown by the save file picker.</param>
    /// <param name="screenshotRoot">An optional path for the suggested start location.</param>
    public FilePickerHandler(string? title, string? screenshotRoot = default)
    {
        _title = title ?? "Save Screenshot to ...";
        _screenshotRoot = screenshotRoot;
    }

    protected override async Task<Stream?> GetStream(Control control)
    {
        var storageProvider = ResolveTopLevel(control).StorageProvider;
        IStorageFolder? defaultFolder = null;

        if (_screenshotRoot is not null)
        {
            defaultFolder = await storageProvider.TryGetFolderFromPathAsync(_screenshotRoot).ConfigureAwait(false);
        }

        if (defaultFolder is null)
        {
            defaultFolder = await storageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures).ConfigureAwait(false);
        }

        var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedStartLocation = defaultFolder,
            Title = _title,
            FileTypeChoices = new[] { FilePickerFileTypes.ImagePng },
            DefaultExtension = ".png"
        }).ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        return await result.OpenWriteAsync().ConfigureAwait(false);
    }

    private static TopLevel ResolveTopLevel(Control control)
    {
        var topLevel = TopLevel.GetTopLevel(control);
        if (topLevel is not null)
        {
            return topLevel;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var candidate = desktop.MainWindow ?? desktop.Windows.FirstOrDefault();
            if (candidate is not null)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No TopLevel is available.");
    }
}
