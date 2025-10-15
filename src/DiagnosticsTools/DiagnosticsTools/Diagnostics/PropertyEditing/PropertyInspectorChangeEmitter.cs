using System;
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

        public ValueTask EmitLocalValueChangeAsync(
            PropertyChangeContext context,
            object? newValue,
            string gesture,
            CancellationToken cancellationToken = default)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Descriptor is null)
            {
                return new ValueTask();
            }

            var envelope = BuildSetAttributeEnvelope(context, newValue, gesture);
            return DispatchAsync(envelope, cancellationToken);
        }

        internal event EventHandler<MutationCompletedEventArgs>? ChangeCompleted;

        private ChangeEnvelope BuildSetAttributeEnvelope(PropertyChangeContext context, object? newValue, string gesture)
        {
            var descriptor = context.Descriptor;
            var document = context.Document;
            var property = context.Property;
            var target = context.Target;

            var valueKind = DetermineValueKind(newValue);
            var valueString = FormatValue(newValue, property.PropertyType);
            var attributeName = BuildAttributeName(property);
            var bindingPayload = BuildBindingPayload(newValue);
            var resourcePayload = BuildResourcePayload(newValue);

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

        private async ValueTask DispatchAsync(ChangeEnvelope envelope, CancellationToken cancellationToken)
        {
            try
            {
                var result = await _dispatcher.DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (!_dispatcherProvidesNotifications)
                {
                    OnChangeCompleted(new MutationCompletedEventArgs(envelope, result));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failure = ChangeDispatchResult.MutationFailure(null, $"Change dispatch failed: {ex.Message}");
                OnChangeCompleted(new MutationCompletedEventArgs(envelope, failure));
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
