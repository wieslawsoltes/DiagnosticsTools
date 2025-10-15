#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace DiagnosticsToolsSample;

internal static class DiagnosticsWorkspaceProvider
{
    private static readonly Lazy<Workspace?> s_workspace = new(CreateWorkspace, isThreadSafe: true);

    public static Workspace? TryCreateWorkspace() => s_workspace.Value;

    private static Workspace? CreateWorkspace()
    {
        try
        {
            var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
            var workspace = new AdhocWorkspace(host);

            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "DiagnosticsToolsSample",
                "DiagnosticsToolsSample",
                LanguageNames.CSharp);

            workspace.AddProject(projectInfo);

            foreach (var path in EnumerateXamlFiles())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var documentId = DocumentId.CreateNewId(projectId);
                var sourceText = SourceText.From(File.ReadAllText(path), Encoding.UTF8);
                var loader = TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create(), path));
                var documentInfo = DocumentInfo.Create(
                    documentId,
                    name: Path.GetFileName(path),
                    loader: loader,
                    filePath: path);

                workspace.AddDocument(documentInfo);
            }

            return workspace;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateXamlFiles()
    {
        var projectRoot = FindProjectRoot();
        if (projectRoot is null)
        {
            yield break;
        }

        foreach (var extension in new[] { "*.axaml", "*.xaml" })
        {
            foreach (var path in Directory.EnumerateFiles(projectRoot, extension, SearchOption.AllDirectories))
            {
                yield return path;
            }
        }
    }

    private static string? FindProjectRoot()
    {
        try
        {
            var directory = AppContext.BaseDirectory;
            for (var i = 0; i < 6 && directory is not null; i++)
            {
                var candidate = Path.Combine(directory, "DiagnosticsToolsSample.csproj");
                if (File.Exists(candidate))
                {
                    return directory;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
#endif
