using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class ChangeEnvelope
    {
        [JsonPropertyName("schemaVersion")]
        public string SchemaVersion { get; init; } = "1.0.0";

        [JsonPropertyName("batchId")]
        public Guid BatchId { get; init; }

        [JsonPropertyName("initiatedAt")]
        public DateTimeOffset InitiatedAt { get; init; }

        [JsonPropertyName("source")]
        public ChangeSourceInfo Source { get; init; } = new();

        [JsonPropertyName("document")]
        public ChangeDocumentInfo Document { get; init; } = new();

        [JsonPropertyName("context")]
        public ChangeContextInfo Context { get; init; } = new();

        [JsonPropertyName("guards")]
        public ChangeGuardsInfo Guards { get; init; } = new();

        [JsonPropertyName("changes")]
        public IReadOnlyList<ChangeOperation> Changes { get; init; } = Array.Empty<ChangeOperation>();
    }

    internal sealed class ChangeSourceInfo
    {
        [JsonPropertyName("inspector")]
        public string Inspector { get; init; } = "PropertyEditor";

        [JsonPropertyName("gesture")]
        public string Gesture { get; init; } = "SetLocalValue";

        [JsonPropertyName("uiSessionId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UiSessionId { get; init; }

        [JsonPropertyName("command")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChangeSourceCommandInfo? Command { get; init; }
    }

    internal sealed class ChangeSourceCommandInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisplayName { get; init; }

        [JsonPropertyName("input")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Input { get; init; }
    }

    internal sealed class ChangeDocumentInfo
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("encoding")]
        public string Encoding { get; init; } = "utf-8";

        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("mode")]
        public string Mode { get; init; } = "Writable";
    }

    internal sealed class ChangeContextInfo
    {
        [JsonPropertyName("elementId")]
        public string ElementId { get; init; } = string.Empty;

        [JsonPropertyName("astNodeId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AstNodeId { get; init; }

        [JsonPropertyName("property")]
        public string Property { get; init; } = string.Empty;

        [JsonPropertyName("frame")]
        public string Frame { get; init; } = "LocalValue";

        [JsonPropertyName("valueSource")]
        public string ValueSource { get; init; } = "LocalValue";

        [JsonPropertyName("propertyPath")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PropertyPath { get; init; }
    }

    internal sealed class ChangeGuardsInfo
    {
        [JsonPropertyName("documentVersion")]
        public string DocumentVersion { get; init; } = string.Empty;

        [JsonPropertyName("runtimeFingerprint")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RuntimeFingerprint { get; init; }
    }

    internal sealed class ChangeOperation
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; init; }

        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("target")]
        public ChangeTarget Target { get; init; } = new();

        [JsonPropertyName("payload")]
        public ChangePayload Payload { get; init; } = new();

        [JsonPropertyName("guard")]
        public ChangeOperationGuard Guard { get; init; } = new();

        [JsonPropertyName("mergeBehavior")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MergeBehavior { get; init; }
    }

    internal sealed class ChangeTarget
    {
        [JsonPropertyName("descriptorId")]
        public string DescriptorId { get; init; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("nodeType")]
        public string NodeType { get; init; } = "Attribute";
    }

    internal sealed class ChangePayload
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("serialized")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Serialized { get; init; }

        [JsonPropertyName("namespace")]
        public string Namespace { get; init; } = string.Empty;

        [JsonPropertyName("namespacePrefix")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NamespacePrefix { get; init; }

        [JsonPropertyName("valueKind")]
        public string ValueKind { get; init; } = "Literal";

        [JsonPropertyName("newValue")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NewValue { get; init; }

        [JsonPropertyName("insertionIndex")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? InsertionIndex { get; init; }

        [JsonPropertyName("createContainer")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? CreateContainer { get; init; }

        [JsonPropertyName("surroundingWhitespace")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SurroundingWhitespace { get; init; }

        [JsonPropertyName("cascade")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Cascade { get; init; }

        [JsonPropertyName("binding")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BindingPayload? Binding { get; init; }

        [JsonPropertyName("resource")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ResourcePayload? Resource { get; init; }

        [JsonPropertyName("previousValueKind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PreviousValueKind { get; init; }

        [JsonPropertyName("indentationPolicy")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IndentationPolicyPayload? IndentationPolicy { get; init; }

        [JsonPropertyName("descriptorIds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? DescriptorIds { get; init; }

        [JsonPropertyName("companionDescriptorIds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? CompanionDescriptorIds { get; init; }

        [JsonPropertyName("newIndex")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? NewIndex { get; init; }

        [JsonPropertyName("oldKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OldKey { get; init; }

        [JsonPropertyName("newKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NewKey { get; init; }

        [JsonPropertyName("cascadeTargets")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? CascadeTargets { get; init; }
    }

    internal sealed class BindingPayload
    {
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Path { get; init; }

        [JsonPropertyName("mode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Mode { get; init; }

        [JsonPropertyName("updateSourceTrigger")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UpdateSourceTrigger { get; init; }

        [JsonPropertyName("converter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Converter { get; init; }

        [JsonPropertyName("converterParameter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ConverterParameter { get; init; }

        [JsonPropertyName("stringFormat")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StringFormat { get; init; }

        [JsonPropertyName("targetType")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TargetType { get; init; }
    }

    internal sealed class ResourcePayload
    {
        [JsonPropertyName("resourceKind")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ResourceKind { get; init; }

        [JsonPropertyName("key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Key { get; init; }
    }

    internal sealed class IndentationPolicyPayload
    {
        [JsonPropertyName("indentSize")]
        public int IndentSize { get; init; } = 2;

        [JsonPropertyName("useTabs")]
        public bool UseTabs { get; init; }
    }

    internal sealed class ChangeOperationGuard
    {
        [JsonPropertyName("spanHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SpanHash { get; init; }

        [JsonPropertyName("parentSpanHash")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParentSpanHash { get; init; }
    }
}
