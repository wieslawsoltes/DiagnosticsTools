using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Data;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Microsoft.Language.Xml;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal sealed class PropertyInspectorChangeEmitter
    {
        private const string InspectorName = "PropertyEditor";
        private const string DefaultFrame = "LocalValue";
        private const string DefaultValueSource = "LocalValue";
        private const string DefaultEncoding = "utf-8";
        private const string DefaultDocumentMode = "Writable";
        private const string AttributeNodeType = "Attribute";
        private const string DefaultValueKind = "Literal";

        private readonly IChangeDispatcher _dispatcher;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<Guid> _idProvider;
        private readonly JsonSerializerOptions _serializerOptions;
        private readonly bool _dispatcherProvidesNotifications;
        private readonly Dictionary<MutationOriginKey, PropertyMutationOrigin> _mutationOrigins = new();
        private readonly HashSet<string> _pendingMutationInvalidations = new(PathComparer);
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        private static readonly StringComparison PathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        internal XamlMutationDispatcher? MutationDispatcher => _dispatcher as XamlMutationDispatcher;

        public PropertyInspectorChangeEmitter(
            IChangeDispatcher dispatcher,
            Func<DateTimeOffset>? clock = null,
            Func<Guid>? idProvider = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _idProvider = idProvider ?? Guid.NewGuid;
            if (dispatcher is XamlMutationDispatcher xamlDispatcher)
            {
                _dispatcherProvidesNotifications = true;
                xamlDispatcher.MutationCompleted += HandleDispatcherMutationCompleted;
                SubscribeToWorkspace(xamlDispatcher.Workspace);
            }
            _serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        public async ValueTask<ChangeDispatchResult> EmitLocalValueChangeAsync(
            PropertyChangeContext context,
            object? newValue,
            object? previousValue,
            string gesture,
            CancellationToken cancellationToken = default)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Descriptor is null)
            {
                return ChangeDispatchResult.MutationFailure(null, "Missing XAML selection.");
            }

            var origin = GetOrCreateMutationOrigin(context, previousValue, out var originKey);
            var attributeName = BuildAttributeName(context.Property);
            var shouldUnset = ShouldUnsetAttribute(origin, newValue);
            var valueKind = shouldUnset ? "Unset" : DetermineValueKind(newValue);
            var bindingPayload = shouldUnset ? null : BuildBindingPayload(newValue);
            var resourcePayload = shouldUnset ? null : BuildResourcePayload(newValue);
            var valueText = shouldUnset ? null : DetermineAttributeValueText(context.Property, newValue, origin);

            var envelope = BuildSetAttributeEnvelope(
                context,
                attributeName,
                valueKind,
                valueText,
                bindingPayload,
                resourcePayload,
                shouldUnset,
                gesture);

            var documentPath = context.Document.Path;
            if (!string.IsNullOrWhiteSpace(documentPath))
            {
                _pendingMutationInvalidations.Add(documentPath);
            }

            try
            {
                var result = await DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChangeDispatchStatus.Success)
                {
                    UpdateMutationOriginBaseline(originKey, origin, shouldUnset, valueText, newValue);
                }

                return result;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(documentPath))
                {
                    _pendingMutationInvalidations.Remove(documentPath);
                }
            }
        }

        internal event EventHandler<MutationCompletedEventArgs>? ChangeCompleted;

        private void SubscribeToWorkspace(XamlAstWorkspace workspace)
        {
            if (workspace is null)
            {
                return;
            }

            workspace.DocumentChanged += HandleWorkspaceDocumentChanged;
        }

        private void HandleWorkspaceDocumentChanged(object? sender, XamlDocumentChangedEventArgs e)
        {
            if (e is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(e.Path) && _pendingMutationInvalidations.Remove(e.Path))
            {
                return;
            }

            InvalidateMutationOrigins(e.Path);
        }

        private void InvalidateMutationOrigins(string? path)
        {
            if (_mutationOrigins.Count == 0)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                _mutationOrigins.Clear();
                return;
            }

            var keysToRemove = new List<MutationOriginKey>();

            foreach (var key in _mutationOrigins.Keys)
            {
                if (string.Equals(key.DocumentPath, path, PathComparison))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _mutationOrigins.Remove(key);
            }
        }

        private ChangeEnvelope BuildSetAttributeEnvelope(
            PropertyChangeContext context,
            string attributeName,
            string valueKind,
            string? valueText,
            BindingPayload? bindingPayload,
            ResourcePayload? resourcePayload,
            bool shouldUnset,
            string gesture)
        {
            var descriptor = context.Descriptor;
            var document = context.Document;
            var property = context.Property;
            var target = context.Target;

            var elementPath = BuildTargetPath(descriptor, attributeName);
            var spanHash = XamlGuardUtilities.ComputeAttributeHash(document, descriptor, attributeName);
            var elementId = BuildElementId(target);
            var propertyQualifiedName = $"{property.OwnerType.Name}.{property.Name}";

            var targetInfo = new ChangeTarget
            {
                DescriptorId = descriptor.Id.ToString(),
                Path = elementPath,
                NodeType = AttributeNodeType
            };

            var payload = new ChangePayload
            {
                Name = attributeName,
                Namespace = string.Empty,
                ValueKind = valueKind,
                NewValue = shouldUnset ? null : valueText,
                Binding = bindingPayload,
                Resource = resourcePayload
            };

            var operation = new ChangeOperation
            {
                Id = "op-1",
                Type = ChangeOperationTypes.SetAttribute,
                Target = targetInfo,
                Payload = payload,
                Guard = new ChangeOperationGuard
                {
                    SpanHash = spanHash
                }
            };

            var envelope = new ChangeEnvelope
            {
                BatchId = _idProvider(),
                InitiatedAt = _clock(),
                Source = new ChangeSourceInfo
                {
                    Inspector = InspectorName,
                    Gesture = gesture
                },
                Document = new ChangeDocumentInfo
                {
                    Path = document.Path,
                    Encoding = DefaultEncoding,
                    Version = document.Version.ToString(),
                    Mode = DefaultDocumentMode
                },
                Context = new ChangeContextInfo
                {
                    ElementId = elementId,
                    AstNodeId = descriptor.Id.ToString(),
                    Property = propertyQualifiedName,
                    PropertyPath = propertyQualifiedName,
                    Frame = context.Frame,
                    ValueSource = context.ValueSource
                },
                Guards = new ChangeGuardsInfo
                {
                    DocumentVersion = document.Version.ToString(),
                    RuntimeFingerprint = elementId
                },
                Changes = new[] { operation }
            };

            return envelope;
        }

        private PropertyMutationOrigin GetOrCreateMutationOrigin(
            PropertyChangeContext context,
            object? previousValue,
            out MutationOriginKey key)
        {
            key = new MutationOriginKey(
                context.Document.Path ?? string.Empty,
                context.Descriptor.Id.ToString(),
                context.Property);

            if (_mutationOrigins.TryGetValue(key, out var origin))
            {
                var updatedCurrent = origin.Current.WithRuntimeValue(previousValue);
                origin = origin with { Current = updatedCurrent };
                _mutationOrigins[key] = origin;
                return origin;
            }

            var attributeName = BuildAttributeName(context.Property);
            var snapshot = CaptureAttributeSnapshot(context.Document, context.Descriptor, attributeName);

            var initial = new MutationOriginSnapshot(snapshot.Exists, snapshot.Value, previousValue);
            var newOrigin = new PropertyMutationOrigin(initial, initial);
            _mutationOrigins[key] = newOrigin;
            return newOrigin;
        }

        private static string BuildAttributeName(AvaloniaProperty property)
        {
            if (property is null)
            {
                throw new ArgumentNullException(nameof(property));
            }

            if (property.IsAttached && property.OwnerType is { } owner)
            {
                return $"{owner.Name}.{property.Name}";
            }

            return property.Name;
        }

        private static string BuildElementId(AvaloniaObject target)
        {
            return $"runtime://avalonia-object/{RuntimeHelpers.GetHashCode(target)}";
        }

        private static string BuildTargetPath(XamlAstNodeDescriptor descriptor, string attributeName)
        {
            var name = descriptor.LocalName;
            var index = descriptor.Path.Count > 0 ? descriptor.Path[descriptor.Path.Count - 1] : 0;
            return $"{name}[{index}].@{attributeName}";
        }

        private static AttributeSnapshot CaptureAttributeSnapshot(
            XamlAstDocument document,
            XamlAstNodeDescriptor descriptor,
            string attributeName)
        {
            if (XamlGuardUtilities.TryLocateAttribute(document, descriptor, attributeName, out var attribute, out _)
                && attribute is not null)
            {
                if (attribute.ValueNode is { } valueNode)
                {
                    return new AttributeSnapshot(true, ExtractAttributeValue(document.Text, valueNode.Span));
                }

                return new AttributeSnapshot(true, null);
            }

            return new AttributeSnapshot(false, null);
        }

        private static string DetermineValueKind(object? value)
        {
            return value switch
            {
                null => "Unset",
                BindingBase => "Binding",
                MarkupExtension => "MarkupExtension",
                _ => DefaultValueKind
            };
        }

        private static bool ShouldUnsetAttribute(PropertyMutationOrigin origin, object? newValue)
        {
            if (newValue is null || IsUnsetValue(newValue))
            {
                return true;
            }

            if (!origin.Initial.AttributeExists && AreEquivalent(newValue, origin.Initial.RuntimeValue))
            {
                return true;
            }

            return false;
        }

        private static BindingPayload? BuildBindingPayload(object? value)
        {
            if (value is not Binding binding)
            {
                return null;
            }

            return new BindingPayload
            {
                Path = binding.Path,
                Mode = binding.Mode.ToString(),
                UpdateSourceTrigger = binding.UpdateSourceTrigger.ToString(),
                Converter = binding.Converter?.GetType().FullName,
                ConverterParameter = binding.ConverterParameter?.ToString(),
                StringFormat = binding.StringFormat,
                TargetType = null
            };
        }

        private static ResourcePayload? BuildResourcePayload(object? value)
        {
            if (value is StaticResourceExtension staticResource && staticResource.ResourceKey is { } staticKey)
            {
                return new ResourcePayload
                {
                    ResourceKind = "Static",
                    Key = staticKey.ToString()
                };
            }

            if (value is DynamicResourceExtension dynamicResource && dynamicResource.ResourceKey is { } dynamicKey)
            {
                return new ResourcePayload
                {
                    ResourceKind = "Dynamic",
                    Key = dynamicKey.ToString()
                };
            }

            return null;
        }

        private static string? FormatValue(object? value, Type targetType)
        {
            if (value is null)
            {
                return null;
            }

            if (IsUnsetValue(value))
            {
                return null;
            }

            if (value is string s)
            {
                return s;
            }

            if (targetType == typeof(object))
            {
                return value.ToString();
            }

            var converter = TypeDescriptor.GetConverter(targetType);
            if (converter is not null && converter.CanConvertTo(typeof(string)))
            {
                try
                {
                    return converter.ConvertToInvariantString(value);
                }
                catch
                {
                    // Fall back to ToString below.
                }
            }

            if (value is IFormattable formattable)
            {
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }

        private static string? DetermineAttributeValueText(
            AvaloniaProperty property,
            object? newValue,
            PropertyMutationOrigin origin)
        {
            if (newValue is null || IsUnsetValue(newValue))
            {
                return null;
            }

            if (origin.Initial.AttributeExists &&
                AreEquivalent(newValue, origin.Initial.RuntimeValue) &&
                !string.IsNullOrEmpty(origin.Initial.AttributeValue))
            {
                return origin.Initial.AttributeValue;
            }

            return FormatValue(newValue, property.PropertyType);
        }

        private void UpdateMutationOriginBaseline(
            MutationOriginKey key,
            PropertyMutationOrigin origin,
            bool shouldUnset,
            string? attributeValue,
            object? runtimeValue)
        {
            var updatedSnapshot = new MutationOriginSnapshot(!shouldUnset, attributeValue, runtimeValue);
            _mutationOrigins[key] = origin with { Current = updatedSnapshot };
        }

        private static string? ExtractAttributeValue(string text, TextSpan span)
        {
            if (span.Length <= 0)
            {
                return string.Empty;
            }

            var raw = text.Substring(span.Start, span.Length);
            return TrimQuotes(raw);
        }

        private static string TrimQuotes(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2)
            {
                return value;
            }

            var first = value[0];
            var last = value[value.Length - 1];

            if ((first == '"' && last == '"') || (first == '\'' && last == '\''))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private static bool IsUnsetValue(object? value) =>
            ReferenceEquals(value, AvaloniaProperty.UnsetValue);

        private static bool AreEquivalent(object? first, object? second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            var firstIsUnset = IsUnsetValue(first);
            var secondIsUnset = IsUnsetValue(second);
            if (firstIsUnset || secondIsUnset)
            {
                return firstIsUnset && secondIsUnset;
            }

            if (first is null || second is null)
            {
                return false;
            }

            if (first is Binding firstBinding && second is Binding secondBinding)
            {
                return AreBindingsEquivalent(firstBinding, secondBinding);
            }

            if (first is StaticResourceExtension firstStatic && second is StaticResourceExtension secondStatic)
            {
                return Equals(firstStatic.ResourceKey, secondStatic.ResourceKey);
            }

            if (first is DynamicResourceExtension firstDynamic && second is DynamicResourceExtension secondDynamic)
            {
                return Equals(firstDynamic.ResourceKey, secondDynamic.ResourceKey);
            }

            if (first is MarkupExtension firstMarkup && second is MarkupExtension secondMarkup)
            {
                return string.Equals(firstMarkup.ToString(), secondMarkup.ToString(), StringComparison.Ordinal);
            }

            return Equals(first, second);
        }

        private static bool AreBindingsEquivalent(Binding first, Binding second)
        {
            return string.Equals(first.Path, second.Path, StringComparison.Ordinal) &&
                   first.Mode == second.Mode &&
                   first.UpdateSourceTrigger == second.UpdateSourceTrigger &&
                   string.Equals(first.StringFormat, second.StringFormat, StringComparison.Ordinal) &&
                   Equals(first.Converter, second.Converter) &&
                   Equals(first.ConverterParameter, second.ConverterParameter) &&
                   string.Equals(first.ElementName, second.ElementName, StringComparison.Ordinal) &&
                   Equals(first.Source, second.Source) &&
                   Equals(first.RelativeSource, second.RelativeSource) &&
                   Equals(first.TargetNullValue, second.TargetNullValue) &&
                   Equals(first.FallbackValue, second.FallbackValue);
        }

        private async ValueTask<ChangeDispatchResult> DispatchAsync(ChangeEnvelope envelope, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _dispatcher.DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (!_dispatcherProvidesNotifications)
                {
                    OnChangeCompleted(new MutationCompletedEventArgs(envelope, result));
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failure = ChangeDispatchResult.MutationFailure(null, $"Change dispatch failed: {ex.Message}");
                OnChangeCompleted(new MutationCompletedEventArgs(envelope, failure));
                return failure;
            }
        }

        public string Serialize(ChangeEnvelope envelope)
        {
            return JsonSerializer.Serialize(envelope, _serializerOptions);
        }

        private void HandleDispatcherMutationCompleted(object? sender, MutationCompletedEventArgs e) =>
            OnChangeCompleted(e);

        private void OnChangeCompleted(MutationCompletedEventArgs args)
        {
            if (ChangeCompleted is null)
            {
                return;
            }

            try
            {
                ChangeCompleted.Invoke(this, args);
            }
            catch
            {
                // Diagnostics notifications should not throw.
            }
        }

        private readonly record struct PropertyMutationOrigin(
            MutationOriginSnapshot Initial,
            MutationOriginSnapshot Current);

        private readonly record struct MutationOriginSnapshot(bool AttributeExists, string? AttributeValue, object? RuntimeValue)
        {
            public MutationOriginSnapshot WithRuntimeValue(object? runtimeValue) =>
                this with { RuntimeValue = runtimeValue };
        }

        private readonly struct AttributeSnapshot
        {
            public AttributeSnapshot(bool exists, string? value)
            {
                Exists = exists;
                Value = value;
            }

            public bool Exists { get; }

            public string? Value { get; }
        }

        private readonly struct MutationOriginKey : IEquatable<MutationOriginKey>
        {
            public MutationOriginKey(string documentPath, string descriptorId, AvaloniaProperty property)
            {
                DocumentPath = documentPath ?? string.Empty;
                DescriptorId = descriptorId ?? string.Empty;
                Property = property ?? throw new ArgumentNullException(nameof(property));
            }

            public string DocumentPath { get; }

            public string DescriptorId { get; }

            public AvaloniaProperty Property { get; }

            public bool Equals(MutationOriginKey other)
            {
                return string.Equals(DocumentPath, other.DocumentPath, StringComparison.Ordinal) &&
                       string.Equals(DescriptorId, other.DescriptorId, StringComparison.Ordinal) &&
                       ReferenceEquals(Property, other.Property);
            }

            public override bool Equals(object? obj) =>
                obj is MutationOriginKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = 17;
                    hash = (hash * 23) + StringComparer.Ordinal.GetHashCode(DocumentPath);
                    hash = (hash * 23) + StringComparer.Ordinal.GetHashCode(DescriptorId);
                    hash = (hash * 23) + Property.GetHashCode();
                    return hash;
                }
            }
        }
    }

    internal static class ChangeOperationTypes
    {
        public const string SetAttribute = "SetAttribute";
        public const string UpsertElement = "UpsertElement";
        public const string RemoveNode = "RemoveNode";
        public const string ReorderNode = "ReorderNode";
        public const string RenameResource = "RenameResource";
        public const string RenameElement = "RenameElement";
        public const string SetNamespace = "SetNamespace";
        public const string SetContentText = "SetContentText";
    }
}
