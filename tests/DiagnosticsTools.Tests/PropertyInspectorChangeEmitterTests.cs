using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Microsoft.Language.Xml;
using Xunit;
using System.Linq;
using Xunit.Sdk;
using System.Reflection;
using System.Collections.Generic;

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
        Assert.NotNull(nonNullEnvelope.Source.Command);
        Assert.Equal(EditorCommandDescriptor.Toggle.Id, nonNullEnvelope.Source.Command!.Id);
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
        Assert.NotNull(envelope!.Source.Command);
        Assert.Equal(EditorCommandDescriptor.Toggle.Id, envelope.Source.Command!.Id);
        var operation = Assert.Single(envelope.Changes);
        Assert.Equal(ChangeOperationTypes.SetAttribute, operation.Type);
        Assert.Equal("Unset", operation.Payload.ValueKind);
        Assert.Null(operation.Payload.NewValue);
    }

    [AvaloniaFact]
    public async Task EmitLocalValueChangeAsync_Recognizes_Binding_Revert()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui">
  <TextBlock x:Name="Label" />
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UnixEpoch, normalized.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", normalized, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "TextBlock");

        var target = new DummyAvaloniaObject();
        var context = new PropertyChangeContext(
            target,
            TextBlock.TextProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var dispatcher = new RecordingDispatcher();
        var emitter = new PropertyInspectorChangeEmitter(dispatcher);

        var previousBinding = new Binding("Title");
        var newBinding = new Binding("Title");

        await emitter.EmitLocalValueChangeAsync(context, newBinding, previousBinding, "SetBinding");

        var envelope = dispatcher.LastEnvelope;
        Assert.NotNull(envelope);
        var operation = Assert.Single(envelope!.Changes);
        Assert.Equal(ChangeOperationTypes.SetAttribute, operation.Type);
        Assert.Equal("Unset", operation.Payload.ValueKind);
        Assert.Null(operation.Payload.NewValue);
        Assert.Null(operation.Payload.Binding);
    }

    [AvaloniaFact]
    public async Task EmitLocalValueChangeAsync_Recognizes_StaticResource_Revert()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui">
  <Border x:Name="Frame" />
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UnixEpoch, normalized.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", normalized, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "Border");

        var target = new DummyAvaloniaObject();
        var context = new PropertyChangeContext(
            target,
            Border.BackgroundProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var dispatcher = new RecordingDispatcher();
        var emitter = new PropertyInspectorChangeEmitter(dispatcher);

        var previousResource = new StaticResourceExtension("AccentBrush");
        var newResource = new StaticResourceExtension("AccentBrush");

        await emitter.EmitLocalValueChangeAsync(context, newResource, previousResource, "SetStaticResource");

        var envelope = dispatcher.LastEnvelope;
        Assert.NotNull(envelope);
        var operation = Assert.Single(envelope!.Changes);
        Assert.Equal(ChangeOperationTypes.SetAttribute, operation.Type);
        Assert.Equal("Unset", operation.Payload.ValueKind);
        Assert.Null(operation.Payload.NewValue);
        Assert.Null(operation.Payload.Resource);
    }

    [AvaloniaFact]
    public async Task EmitLocalValueChangeAsync_Restores_Whitespace_When_Reverting()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui">
  <Border x:Name="Frame" Margin="4,  8,16, 32" />
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UnixEpoch, normalized.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", normalized, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "Border");

        var target = new DummyAvaloniaObject();
        var context = new PropertyChangeContext(
            target,
            Border.MarginProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var dispatcher = new RecordingDispatcher();
        var emitter = new PropertyInspectorChangeEmitter(dispatcher);

        var originalThickness = new Thickness(4, 8, 16, 32);
        var updatedThickness = new Thickness(10, 20, 30, 40);

        await emitter.EmitLocalValueChangeAsync(context, updatedThickness, originalThickness, "SetMargin");
        await emitter.EmitLocalValueChangeAsync(context, originalThickness, updatedThickness, "SetMargin");

        var envelope = dispatcher.LastEnvelope;
        Assert.NotNull(envelope);
        var operation = Assert.Single(envelope!.Changes);
        Assert.Equal(ChangeOperationTypes.SetAttribute, operation.Type);
        Assert.Equal("Literal", operation.Payload.ValueKind);
        Assert.Equal("4,  8,16, 32", operation.Payload.NewValue);
    }

    [AvaloniaFact]
    public async Task EmitLocalValueChangeAsync_Emits_Telemetry()
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
        var emitter = new PropertyInspectorChangeEmitter(dispatcher);
        var telemetrySink = new RecordingTelemetrySink();
        MutationTelemetry.RegisterSink(telemetrySink);

        try
        {
            await emitter.EmitLocalValueChangeAsync(context, true, false, "ToggleCheckBox");

            var events = telemetrySink.Events;
            var telemetry = Assert.Single(events);
            Assert.Equal("PropertyEditor", telemetry.Inspector);
            Assert.Equal("ToggleCheckBox", telemetry.Gesture);
            Assert.Equal(ChangeDispatchStatus.Success, telemetry.Outcome);
            Assert.Equal(1, telemetry.ChangeCount);
            Assert.Contains("SetAttribute", telemetry.ChangeTypes);
            Assert.Contains("Literal", telemetry.ValueKinds);
        }
        finally
        {
            MutationTelemetry.UnregisterSink(telemetrySink);
        }
    }

    [AvaloniaFact]
    public async Task EmitLocalValueChangeAsync_Supports_MultiSelection()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui">
  <StackPanel>
    <CheckBox x:Name="First" />
    <CheckBox x:Name="Second" />
  </StackPanel>
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UnixEpoch, normalized.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", normalized, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptors = index.Nodes.Where(n => n.LocalName == "CheckBox").ToArray();
        Assert.Equal(2, descriptors.Length);

        var targetOne = new DummyAvaloniaObject();
        var targetTwo = new DummyAvaloniaObject();

        var contextOne = new PropertyChangeContext(
            targetOne,
            ToggleButton.IsCheckedProperty,
            document,
            descriptors[0],
            frame: "LocalValue",
            valueSource: "LocalValue");

        var contextTwo = new PropertyChangeContext(
            targetTwo,
            ToggleButton.IsCheckedProperty,
            document,
            descriptors[1],
            frame: "LocalValue",
            valueSource: "LocalValue");

        var dispatcher = new RecordingDispatcher();
        var fixedTime = new DateTimeOffset(2024, 03, 10, 12, 34, 56, TimeSpan.Zero);
        var emitter = new PropertyInspectorChangeEmitter(dispatcher, () => fixedTime, () => Guid.Parse("00000000-0000-0000-0000-000000000789"));

        await emitter.EmitLocalValueChangeAsync(
            contextOne,
            true,
            false,
            "ToggleCheckBox",
            command: null,
            additionalContexts: new[] { contextTwo },
            additionalPreviousValues: new object?[] { false });

        var envelope = dispatcher.LastEnvelope;
        Assert.NotNull(envelope);
        Assert.Equal(2, envelope!.Changes.Count);

        var firstOperation = envelope.Changes[0];
        var secondOperation = envelope.Changes[1];

        Assert.Equal(ChangeOperationTypes.SetAttribute, firstOperation.Type);
        Assert.Equal(ChangeOperationTypes.SetAttribute, secondOperation.Type);
        Assert.Equal(descriptors[0].Id.ToString(), firstOperation.Target.DescriptorId);
        Assert.Equal(descriptors[1].Id.ToString(), secondOperation.Target.DescriptorId);
        Assert.Equal("True", firstOperation.Payload.NewValue);
        Assert.Equal("True", secondOperation.Payload.NewValue);
    }

    [AvaloniaFact]
    public async Task EmitLocalValueChangeAsync_Adds_Namespace_When_Value_Uses_Prefix()
    {
        var xaml = """
<UserControl xmlns="https://github.com/avaloniaui">
  <ContentControl x:Name="Host" />
</UserControl>
""";
        var normalized = xaml.Replace("\r\n", "\n");
        var syntax = Parser.ParseText(normalized);
        var diagnostics = XamlDiagnosticMapper.CollectDiagnostics(syntax);
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        var version = new XamlDocumentVersion(DateTimeOffset.UnixEpoch, normalized.Length, checksum);
        var document = new XamlAstDocument("/tmp/MainWindow.axaml", normalized, syntax, version, diagnostics);
        var index = XamlAstIndex.Build(document);
        var descriptor = Assert.Single(index.Nodes, n => n.LocalName == "ContentControl");

        var target = new ContentControl();
        var context = new PropertyChangeContext(
            target,
            ContentControl.ContentProperty,
            document,
            descriptor,
            frame: "LocalValue",
            valueSource: "LocalValue");

        var dispatcher = new RecordingDispatcher();
        var emitter = new PropertyInspectorChangeEmitter(dispatcher, () => DateTimeOffset.UtcNow, Guid.NewGuid);

        Assert.Equal("Avalonia.Controls", ContentControl.ContentProperty.OwnerType.Namespace);

        var extractMethod = typeof(PropertyInspectorChangeEmitter).GetMethod("ExtractPrefixesFromValue", BindingFlags.NonPublic | BindingFlags.Static);
        var prefixes = ((IEnumerable<string>)extractMethod!.Invoke(null, new object?[] { "{local:DemoContent}" })!).ToList();
        Assert.Contains("local", prefixes);

        await emitter.EmitLocalValueChangeAsync(context, "{local:DemoContent}", null, "SetLocalValue");

        var envelope = dispatcher.LastEnvelope;
        Assert.NotNull(envelope);
        var operations = envelope!.Changes;
        Assert.NotEmpty(operations);
        var attributeOperation = operations.Last();
        Assert.Equal(ChangeOperationTypes.SetAttribute, attributeOperation.Type);
        Assert.Equal("{local:DemoContent}", attributeOperation.Payload.NewValue);
        Assert.Equal("local", attributeOperation.Payload.NamespacePrefix);
        Assert.Contains("clr-namespace", attributeOperation.Payload.Namespace, StringComparison.Ordinal);
        var namespaceOperation = operations.FirstOrDefault(op => op.Type == ChangeOperationTypes.SetNamespace);
        Assert.NotNull(namespaceOperation);
        Assert.Equal("xmlns:local", namespaceOperation!.Payload.Name);
    }

    private sealed class RecordingDispatcher : IChangeDispatcher
    {
        public ChangeEnvelope? LastEnvelope { get; private set; }

        public ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, System.Threading.CancellationToken cancellationToken = default)
        {
            LastEnvelope = envelope;
            return ValueTask.FromResult(ChangeDispatchResult.Success());
        }

        public ValueTask<ChangeDispatchResult> DispatchAsync(ChangeBatch batch, System.Threading.CancellationToken cancellationToken = default)
        {
            if (batch.Documents is { Count: > 0 })
            {
                LastEnvelope = batch.Documents[batch.Documents.Count - 1];
            }

            return ValueTask.FromResult(ChangeDispatchResult.Success());
        }
    }

    private sealed class RecordingTelemetrySink : IMutationTelemetrySink
    {
        private readonly List<MutationTelemetryEvent> _events = new();

        public IReadOnlyList<MutationTelemetryEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Report(MutationTelemetryEvent telemetryEvent)
        {
            if (telemetryEvent is null)
            {
                return;
            }

            lock (_events)
            {
                _events.Add(telemetryEvent);
            }
        }
    }

    private sealed class DummyAvaloniaObject : AvaloniaObject
    {
    }
}
