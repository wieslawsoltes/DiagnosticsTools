using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.Xaml;
using Microsoft.Language.Xml;
using Xunit;

namespace DiagnosticsTools.Tests;

public class PropertyInspectorChangeEmitterTests
{
    [AvaloniaFact]
    public async Task EmitLocalValueChange_Produces_SetAttribute_Envelope()
    {
        var xaml = """
<UserControl xmlns=\"https://github.com/avaloniaui\">
  <CheckBox x:Name=\"CheckOne\" />
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UnixEpoch, normalized.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", normalized, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "CheckBox");

        var target = new DummyAvaloniaObject();
        var context = new PropertyChangeContext(
            target,
            ToggleButton.IsCheckedProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var dispatcher = new RecordingDispatcher();
        var fixedTime = new DateTimeOffset(2024, 03, 10, 12, 34, 56, TimeSpan.Zero);
        var emitter = new PropertyInspectorChangeEmitter(dispatcher, () => fixedTime, () => Guid.Parse("00000000-0000-0000-0000-000000000123"));

        await emitter.EmitLocalValueChangeAsync(context, true, false, "ToggleCheckBox");

        var envelope = dispatcher.LastEnvelope;
        Assert.NotNull(envelope);
        var nonNullEnvelope = envelope!;
        Assert.Equal("1.0.0", nonNullEnvelope.SchemaVersion);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000123"), nonNullEnvelope.BatchId);
        Assert.Equal(fixedTime, nonNullEnvelope.InitiatedAt);
        Assert.Equal("PropertyEditor", nonNullEnvelope.Source.Inspector);
        Assert.Equal("ToggleCheckBox", nonNullEnvelope.Source.Gesture);
        Assert.Equal(document.Path, nonNullEnvelope.Document.Path);
        Assert.Equal(version.ToString(), nonNullEnvelope.Document.Version);
        Assert.Equal("LocalValue", nonNullEnvelope.Context.Frame);
        Assert.Equal("LocalValue", nonNullEnvelope.Context.ValueSource);
        Assert.Equal("ToggleButton.IsChecked", nonNullEnvelope.Context.Property);
        Assert.Equal("ToggleButton.IsChecked", nonNullEnvelope.Context.PropertyPath);
        Assert.NotNull(nonNullEnvelope.Context.ElementId);
        Assert.Contains("runtime://", nonNullEnvelope.Context.ElementId);
        Assert.Equal(version.ToString(), nonNullEnvelope.Guards.DocumentVersion);

        var operation = Assert.Single(nonNullEnvelope.Changes);
        Assert.Equal(ChangeOperationTypes.SetAttribute, operation.Type);
        Assert.Equal("Literal", operation.Payload.ValueKind);
        Assert.Equal("True", operation.Payload.NewValue);
        Assert.Equal("IsChecked", operation.Payload.Name);
        Assert.NotNull(operation.Guard.SpanHash);
        Assert.StartsWith("h64:", operation.Guard.SpanHash, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task EmitLocalValueChangeAsync_Reverts_To_Original_Removes_Attribute()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui">
  <CheckBox x:Name="CheckOne" />
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UnixEpoch, normalized.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", normalized, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "CheckBox");

        var target = new DummyAvaloniaObject();
        var context = new PropertyChangeContext(
            target,
            ToggleButton.IsCheckedProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var dispatcher = new RecordingDispatcher();
        var fixedTime = new DateTimeOffset(2024, 03, 10, 12, 34, 56, TimeSpan.Zero);
        var emitter = new PropertyInspectorChangeEmitter(dispatcher, () => fixedTime, () => Guid.Parse("00000000-0000-0000-0000-000000000456"));

        await emitter.EmitLocalValueChangeAsync(context, true, false, "ToggleCheckBox");
        await emitter.EmitLocalValueChangeAsync(context, false, true, "ToggleCheckBox");

        var envelope = dispatcher.LastEnvelope;
        Assert.NotNull(envelope);
        var operation = Assert.Single(envelope!.Changes);
        Assert.Equal(ChangeOperationTypes.SetAttribute, operation.Type);
        Assert.Equal("Unset", operation.Payload.ValueKind);
        Assert.Null(operation.Payload.NewValue);
    }

    private sealed class RecordingDispatcher : IChangeDispatcher
    {
        public ChangeEnvelope? LastEnvelope { get; private set; }

        public ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, System.Threading.CancellationToken cancellationToken = default)
        {
            LastEnvelope = envelope;
            return ValueTask.FromResult(ChangeDispatchResult.Success());
        }
    }

    private sealed class DummyAvaloniaObject : AvaloniaObject
    {
    }
}
