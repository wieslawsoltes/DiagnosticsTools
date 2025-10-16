using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
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
            }
            _serializerOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
        }

        public ValueTask<ChangeDispatchResult> EmitLocalValueChangeAsync(
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
                return new ValueTask<ChangeDispatchResult>(ChangeDispatchResult.MutationFailure(null, "Missing XAML selection."));
            }

            var origin = GetOrCreateMutationOrigin(context, previousValue);
            var envelope = BuildSetAttributeEnvelope(context, newValue, origin, gesture);
            return DispatchAsync(envelope, cancellationToken);
        }

        internal event EventHandler<MutationCompletedEventArgs>? ChangeCompleted;

        private ChangeEnvelope BuildSetAttributeEnvelope(
            PropertyChangeContext context,
            object? newValue,
            PropertyMutationOrigin origin,
            string gesture)
        {
            var descriptor = context.Descriptor;
            var document = context.Document;
            var property = context.Property;
            var target = context.Target;

            var attributeName = BuildAttributeName(property);

            var shouldUnset = ShouldUnsetAttribute(origin, newValue);
            var valueKind = shouldUnset ? "Unset" : DetermineValueKind(newValue);
            string? valueString = null;
            BindingPayload? bindingPayload = null;
            ResourcePayload? resourcePayload = null;

            if (!string.Equals(valueKind, "Unset", StringComparison.Ordinal))
            {
                valueString = FormatValue(newValue, property.PropertyType);
                bindingPayload = BuildBindingPayload(newValue);
                resourcePayload = BuildResourcePayload(newValue);
            }

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
                NewValue = valueKind is "Unset" ? null : valueString,
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

        private PropertyMutationOrigin GetOrCreateMutationOrigin(PropertyChangeContext context, object? previousValue)
        {
            var key = new MutationOriginKey(
                context.Document.Path ?? string.Empty,
                context.Descriptor.Id.ToString(),
                context.Property);

            if (_mutationOrigins.TryGetValue(key, out var origin))
            {
                return origin;
            }

            var attributeName = BuildAttributeName(context.Property);
            var snapshot = CaptureAttributeSnapshot(context.Document, context.Descriptor, attributeName);

            origin = new PropertyMutationOrigin(snapshot.Exists, snapshot.Value, previousValue);
            _mutationOrigins[key] = origin;
            return origin;
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

        private static (bool Exists, string? Value) CaptureAttributeSnapshot(
            XamlAstDocument document,
            XamlAstNodeDescriptor descriptor,
            string attributeName)
        {
            if (XamlGuardUtilities.TryLocateAttribute(document, descriptor, attributeName, out var attribute, out _)
                && attribute is not null)
            {
                if (attribute.ValueNode is { } valueNode)
                {
                    return (true, ExtractAttributeValue(document.Text, valueNode.Span));
                }

                return (true, null);
            }

            return (false, null);
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

            if (!origin.AttributeExisted)
            {
                return ValuesEqual(newValue, origin.RuntimeValue);
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

        private static bool ValuesEqual(object? first, object? second)
        {
            if (ReferenceEquals(first, second))
            {
                return true;
            }

            if (first is null || second is null)
            {
                return false;
            }

            return Equals(first, second);
        }

        private static bool IsUnsetValue(object? value) =>
            ReferenceEquals(value, AvaloniaProperty.UnsetValue);

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

        private void HandleDispatcherMutationCompleted(object? sender, MutationCompletedEventArgs e)
        {
            OnChangeCompleted(e);
        }

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

        private readonly record struct PropertyMutationOrigin(bool AttributeExisted, string? AttributeValue, object? RuntimeValue);

        private readonly struct MutationOriginKey : IEquatable<MutationOriginKey>
        {
            public MutationOriginKey(string documentPath, string descriptorId, AvaloniaProperty property)
            {
                DocumentPath = documentPath ?? string.Empty;
                DescriptorId = descriptorId ?? string.Empty;
                Property = property ?? throw new ArgumentNullException(nameof(property));
            }

            private string DocumentPath { get; }
            private string DescriptorId { get; }
            private AvaloniaProperty Property { get; }

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
                var hash = new HashCode();
                hash.Add(DocumentPath, StringComparer.Ordinal);
                hash.Add(DescriptorId, StringComparer.Ordinal);
                hash.Add(Property);
                return hash.ToHashCode();
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
    }
}
