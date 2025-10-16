using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DiagnosticsTools.Tests;

public class XamlMutationDispatcherTests : IDisposable
{
    private readonly string _tempFile;

    public XamlMutationDispatcherTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"DiagnosticsTools_{Guid.NewGuid():N}.axaml");
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Sets_Attribute_Value()
    {
        var initial = """
<UserControl xmlns=\"https://github.com/avaloniaui\">
  <CheckBox x:Name=\"CheckOne\" />
</UserControl>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var envelope = await CreateEnvelopeAsync(workspace, value: true, previousValue: false);

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("IsChecked=\"True\"", updated); 
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Detects_Guard_Mismatch()
    {
        var initial = """
<UserControl xmlns=\"https://github.com/avaloniaui\">
  <CheckBox x:Name=\"CheckOne\" />
</UserControl>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var envelope = await CreateEnvelopeAsync(workspace, value: true, previousValue: false);

        // External modification breaking guard
        var mutated = initial.Replace("CheckBox", "CheckBox Content=\"Updated\"");
        await File.WriteAllTextAsync(_tempFile, mutated);
        workspace.Invalidate(_tempFile);

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.GuardFailure, result.Status);
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Removes_Attribute_When_Value_Unset()
    {
        var initial = """
<UserControl xmlns="https://github.com/avaloniaui">
  <CheckBox x:Name="CheckOne" IsChecked="True" />
</UserControl>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var envelope = await CreateEnvelopeAsync(workspace, value: null, previousValue: true);

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.DoesNotContain("IsChecked", updated, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Removes_Attribute_When_Reverting_To_Original_Value()
    {
        var initial = """
<UserControl xmlns="https://github.com/avaloniaui">
  <CheckBox x:Name="CheckOne" />
</UserControl>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var dispatcher = new XamlMutationDispatcher(workspace);
        var emitter = new PropertyInspectorChangeEmitter(dispatcher);

        var document = await workspace.GetDocumentAsync(_tempFile);
        var index = await workspace.GetIndexAsync(_tempFile);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "CheckBox");

        var context = new PropertyChangeContext(
            new DummyAvaloniaObject(),
            ToggleButton.IsCheckedProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var firstResult = await emitter.EmitLocalValueChangeAsync(context, true, false, "ToggleCheckBox");
        Assert.Equal(ChangeDispatchStatus.Success, firstResult.Status);
        var mutated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("IsChecked=\"True\"", mutated, StringComparison.Ordinal);

        document = await workspace.GetDocumentAsync(_tempFile);
        index = await workspace.GetIndexAsync(_tempFile);
        descriptor = Assert.Single(index.Nodes, n => n.LocalName == "CheckBox");
        context = new PropertyChangeContext(
            new DummyAvaloniaObject(),
            ToggleButton.IsCheckedProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var secondResult = await emitter.EmitLocalValueChangeAsync(context, false, true, "ToggleCheckBox");
        Assert.Equal(ChangeDispatchStatus.Success, secondResult.Status);
        var reverted = await File.ReadAllTextAsync(_tempFile);
        Assert.DoesNotContain("IsChecked", reverted, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Upserts_Element_Fragment()
    {
        var initial = """
<Style xmlns="https://github.com/avaloniaui">
  <Style.Setters>
  </Style.Setters>
</Style>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var serialized = "    <Setter Property=\"Foreground\" Value=\"Red\" />\n";
        var envelope = await CreateUpsertEnvelopeAsync(workspace, serialized, insertionIndex: 0, gesture: "AddSetter", surroundingWhitespace: "\n");

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("Setter Property=\"Foreground\"", updated, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Upserts_Element_Into_Empty_Container()
    {
        var initial = """
<Style xmlns="https://github.com/avaloniaui">
  <Style.Setters />
</Style>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var serialized = "    <Setter Property=\"Foreground\" Value=\"Red\" />\n";
        var envelope = await CreateUpsertEnvelopeAsync(workspace, serialized, insertionIndex: 0, gesture: "AddSetter", surroundingWhitespace: null);

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        var normalized = updated.Replace("\r\n", "\n");
        Assert.Contains("<Style.Setters>\n    <Setter Property=\"Foreground\" Value=\"Red\" />", normalized, StringComparison.Ordinal);
        Assert.Contains("  </Style.Setters>", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("<Style.Setters />", normalized, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Updates_Roslyn_Workspace_Document()
    {
        var initial = """
<UserControl xmlns="https://github.com/avaloniaui">
  <CheckBox x:Name="CheckOne" />
</UserControl>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        var workspaceSetup = CreateWorkspaceWithDocument(_tempFile, initial);
        using var workspace = workspaceSetup.Workspace;
        var documentId = workspaceSetup.DocumentId;

        using var xamlWorkspace = new XamlAstWorkspace();
        var dispatcher = new XamlMutationDispatcher(xamlWorkspace, workspace);
        var envelope = await CreateEnvelopeAsync(xamlWorkspace, value: true, previousValue: false);

        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("IsChecked=\"True\"", updated, StringComparison.Ordinal);

        var document = workspace.CurrentSolution.GetDocument(documentId);
        Assert.NotNull(document);
        var text = await document!.GetTextAsync();
        Assert.Contains("IsChecked=\"True\"", text.ToString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task DispatchAsync_Removes_Element_Node()
    {
        var initial = """
<Style xmlns="https://github.com/avaloniaui">
  <Style.Setters>
    <Setter Property="Foreground" Value="Red" />
    <Setter Property="Background" Value="Blue" />
  </Style.Setters>
</Style>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var envelope = await CreateRemoveNodeEnvelopeAsync(
            workspace,
            descriptor =>
                string.Equals(descriptor.LocalName, "Setter", StringComparison.Ordinal) &&
                descriptor.Attributes.Any(a => string.Equals(a.FullName, "Property", StringComparison.Ordinal) &&
                                               string.Equals(a.Value, "Background", StringComparison.Ordinal)),
            gesture: "RemoveSetter");

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("Foreground", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("Background\"", updated, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task UndoAsync_Reverts_Last_Mutation()
    {
        var initial = """
<UserControl xmlns="https://github.com/avaloniaui">
  <CheckBox x:Name="CheckOne" />
</UserControl>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var dispatcher = new XamlMutationDispatcher(workspace);
        var envelope = await CreateEnvelopeAsync(workspace, value: true, previousValue: false);

        var dispatchResult = await dispatcher.DispatchAsync(envelope);
        Assert.Equal(ChangeDispatchStatus.Success, dispatchResult.Status);
        Assert.True(dispatcher.CanUndo);
        Assert.False(dispatcher.CanRedo);

        var mutated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("IsChecked=\"True\"", mutated, StringComparison.Ordinal);

        var undoResult = await dispatcher.UndoAsync();
        Assert.Equal(ChangeDispatchStatus.Success, undoResult.Status);
        var reverted = await File.ReadAllTextAsync(_tempFile);
        Assert.DoesNotContain("IsChecked", reverted, StringComparison.Ordinal);
        Assert.True(dispatcher.CanRedo);
    }

    [AvaloniaFact]
    public async Task RedoAsync_Reapplies_Last_Mutation()
    {
        var initial = """
<UserControl xmlns="https://github.com/avaloniaui">
  <CheckBox x:Name="CheckOne" />
</UserControl>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var dispatcher = new XamlMutationDispatcher(workspace);
        var envelope = await CreateEnvelopeAsync(workspace, value: true, previousValue: false);

        await dispatcher.DispatchAsync(envelope);
        await dispatcher.UndoAsync();
        Assert.True(dispatcher.CanRedo);

        var redoResult = await dispatcher.RedoAsync();
        Assert.Equal(ChangeDispatchStatus.Success, redoResult.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("IsChecked=\"True\"", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_Renames_Resource_Key()
    {
        var initial = """
<ResourceDictionary xmlns="https://github.com/avaloniaui">
  <SolidColorBrush x:Key="PrimaryBrush" Color="Red" />
</ResourceDictionary>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var envelope = await CreateRenameResourceEnvelopeAsync(workspace, oldKey: "PrimaryBrush", newKey: "AccentBrush", gesture: "RenameResource");

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("x:Key=\"AccentBrush\"", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"PrimaryBrush\"", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchAsync_Renames_Resource_Key_With_CascadeTargets()
    {
        var initial = """
<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="PrimaryBrush" Color="Red" />
  <Style x:Key="HighlightBorderStyle" TargetType="Border">
    <Setter Property="Background" Value="{StaticResource PrimaryBrush}" />
  </Style>
  <Border Background="{DynamicResource PrimaryBrush}" />
</ResourceDictionary>
""";
        await File.WriteAllTextAsync(_tempFile, initial);

        using var workspace = new XamlAstWorkspace();
        var index = await workspace.GetIndexAsync(_tempFile);

        var cascadeTargetIds = new List<string>();

        var setterDescriptor = index.Nodes.Single(
            n => string.Equals(n.LocalName, "Setter", StringComparison.Ordinal) &&
                 n.Attributes.Any(a => string.Equals(a.FullName, "Value", StringComparison.Ordinal) &&
                                       a.Value.Contains("PrimaryBrush", StringComparison.Ordinal)));
        cascadeTargetIds.Add(setterDescriptor.Id.ToString());

        var borderDescriptor = index.Nodes.Single(
            n => string.Equals(n.LocalName, "Border", StringComparison.Ordinal) &&
                 n.Attributes.Any(a => string.Equals(a.FullName, "Background", StringComparison.Ordinal)));
        cascadeTargetIds.Add(borderDescriptor.Id.ToString());

        var envelope = await CreateRenameResourceEnvelopeAsync(
            workspace,
            oldKey: "PrimaryBrush",
            newKey: "AccentBrush",
            gesture: "RenameResourceCascade",
            cascadeTargetIds);

        var dispatcher = new XamlMutationDispatcher(workspace);
        var result = await dispatcher.DispatchAsync(envelope);

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        var updated = await File.ReadAllTextAsync(_tempFile);
        Assert.Contains("x:Key=\"AccentBrush\"", updated, StringComparison.Ordinal);
        Assert.Contains("{StaticResource AccentBrush}", updated, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource AccentBrush}", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("PrimaryBrush\"", updated, StringComparison.Ordinal);
    }

    private static (AdhocWorkspace Workspace, DocumentId DocumentId) CreateWorkspaceWithDocument(string path, string content)
    {
        var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
        var workspace = new AdhocWorkspace(host);
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "DiagnosticsToolsTests", "DiagnosticsToolsTests", LanguageNames.CSharp);
        workspace.AddProject(projectInfo);
        var documentId = DocumentId.CreateNewId(projectId);
        var textLoader = TextLoader.From(TextAndVersion.Create(SourceText.From(content, Encoding.UTF8), VersionStamp.Create()));
        var documentInfo = DocumentInfo.Create(
            documentId,
            Path.GetFileName(path),
            loader: textLoader,
            filePath: path);
        workspace.AddDocument(documentInfo);
        return (workspace, documentId);
    }

    private async Task<ChangeEnvelope> CreateEnvelopeAsync(XamlAstWorkspace workspace, bool? value, bool? previousValue)
    {
        var document = await workspace.GetDocumentAsync(_tempFile);
        var index = await workspace.GetIndexAsync(_tempFile);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "CheckBox");

        var target = new DummyAvaloniaObject();
        var context = new PropertyChangeContext(
            target,
            ToggleButton.IsCheckedProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var recorder = new CapturingDispatcher();
        var emitter = new PropertyInspectorChangeEmitter(recorder, () => DateTimeOffset.UtcNow, Guid.NewGuid);
        await emitter.EmitLocalValueChangeAsync(context, value, previousValue, "ToggleCheckBox");

        return recorder.LastEnvelope ?? throw new InvalidOperationException("Envelope capture failed.");
    }

    private async Task<ChangeEnvelope> CreateUpsertEnvelopeAsync(
        XamlAstWorkspace workspace,
        string serialized,
        int insertionIndex,
        string gesture,
        string? surroundingWhitespace = "\n")
    {
        var document = await workspace.GetDocumentAsync(_tempFile);
        var index = await workspace.GetIndexAsync(_tempFile);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName.EndsWith("Setters", StringComparison.Ordinal));
        var spanHash = XamlGuardUtilities.ComputeNodeHash(document, descriptor);

        var operation = new ChangeOperation
        {
            Id = "op-1",
            Type = ChangeOperationTypes.UpsertElement,
            Target = new ChangeTarget
            {
                DescriptorId = descriptor.Id.ToString(),
                Path = descriptor.LocalName,
                NodeType = "Element"
            },
            Payload = new ChangePayload
            {
                Serialized = serialized,
                InsertionIndex = insertionIndex,
                SurroundingWhitespace = surroundingWhitespace
            },
            Guard = new ChangeOperationGuard
            {
                SpanHash = spanHash
            }
        };

        return CreateEnvelope(document, descriptor, operation, gesture);
    }

    private async Task<ChangeEnvelope> CreateRemoveNodeEnvelopeAsync(
        XamlAstWorkspace workspace,
        Func<XamlAstNodeDescriptor, bool> predicate,
        string gesture)
    {
        var document = await workspace.GetDocumentAsync(_tempFile);
        var index = await workspace.GetIndexAsync(_tempFile);
        var descriptor = index.Nodes.Single(predicate);
        var spanHash = XamlGuardUtilities.ComputeNodeHash(document, descriptor);

        var operation = new ChangeOperation
        {
            Id = "op-1",
            Type = ChangeOperationTypes.RemoveNode,
            Target = new ChangeTarget
            {
                DescriptorId = descriptor.Id.ToString(),
                Path = descriptor.LocalName,
                NodeType = "Element"
            },
            Payload = new ChangePayload(),
            Guard = new ChangeOperationGuard
            {
                SpanHash = spanHash
            }
        };

        return CreateEnvelope(document, descriptor, operation, gesture);
    }

    private async Task<ChangeEnvelope> CreateRenameResourceEnvelopeAsync(
        XamlAstWorkspace workspace,
        string oldKey,
        string newKey,
        string gesture,
        IReadOnlyList<string>? cascadeTargetIds = null)
    {
        var document = await workspace.GetDocumentAsync(_tempFile);
        var index = await workspace.GetIndexAsync(_tempFile);
        var descriptor = index.Nodes.Single(n => string.Equals(n.ResourceKey, oldKey, StringComparison.Ordinal));
        var keyAttribute = descriptor.Attributes.Single(a => string.Equals(a.LocalName, "Key", StringComparison.Ordinal));
        var spanHash = XamlGuardUtilities.ComputeAttributeHash(document, descriptor, keyAttribute.FullName);

        var operation = new ChangeOperation
        {
            Id = "op-1",
            Type = ChangeOperationTypes.RenameResource,
            Target = new ChangeTarget
            {
                DescriptorId = descriptor.Id.ToString(),
                Path = $"{descriptor.LocalName}[@x:Key='{oldKey}']",
                NodeType = "Resource"
            },
            Payload = new ChangePayload
            {
                OldKey = oldKey,
                NewKey = newKey,
                CascadeTargets = cascadeTargetIds
            },
            Guard = new ChangeOperationGuard
            {
                SpanHash = spanHash
            }
        };

        return CreateEnvelope(document, descriptor, operation, gesture);
    }

    private static ChangeEnvelope CreateEnvelope(
        XamlAstDocument document,
        XamlAstNodeDescriptor descriptor,
        ChangeOperation operation,
        string gesture)
    {
        return new ChangeEnvelope
        {
            BatchId = Guid.NewGuid(),
            InitiatedAt = DateTimeOffset.UtcNow,
            Source = new ChangeSourceInfo
            {
                Inspector = "PropertyEditor",
                Gesture = gesture
            },
            Document = new ChangeDocumentInfo
            {
                Path = document.Path,
                Encoding = "utf-8",
                Version = document.Version.ToString(),
                Mode = "Writable"
            },
            Context = new ChangeContextInfo
            {
                ElementId = descriptor.Id.ToString(),
                AstNodeId = descriptor.Id.ToString(),
                Property = descriptor.LocalName,
                PropertyPath = descriptor.LocalName,
                Frame = "LocalValue",
                ValueSource = "LocalValue"
            },
            Guards = new ChangeGuardsInfo
            {
                DocumentVersion = document.Version.ToString(),
                RuntimeFingerprint = descriptor.Id.ToString()
            },
            Changes = new[] { operation }
        };
    }

    private sealed class CapturingDispatcher : IChangeDispatcher
    {
        public ChangeEnvelope? LastEnvelope { get; private set; }

        public ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, System.Threading.CancellationToken cancellationToken = default)
        {
            LastEnvelope = envelope;
            return ValueTask.FromResult(ChangeDispatchResult.Success());
        }
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempFile))
            {
                File.Delete(_tempFile);
            }
        }
        catch
        {
            // ignore cleanup failures.
        }
    }

    private sealed class DummyAvaloniaObject : AvaloniaObject
    {
    }
}
