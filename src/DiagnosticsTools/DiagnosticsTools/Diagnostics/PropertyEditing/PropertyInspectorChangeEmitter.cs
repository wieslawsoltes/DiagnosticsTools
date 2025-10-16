using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Data;
using Avalonia.Diagnostics.Xaml;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Microsoft.Language.Xml;
using Avalonia.Utilities;

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
        private readonly Dictionary<string, DateTimeOffset> _pendingMutationInvalidations = new(PathComparer);
        private static readonly StringComparer PathComparer =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        private static readonly StringComparison PathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        private static readonly TimeSpan MutationSuppressionWindow = TimeSpan.FromSeconds(1);

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
            EditorCommandDescriptor? command = null,
            IReadOnlyList<PropertyChangeContext>? additionalContexts = null,
            IReadOnlyList<object?>? additionalPreviousValues = null,
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

            var commandDescriptor = EditorCommandDescriptor.Normalize(command);
            var plan = BuildLocalValueMutationPlan(
                BuildMutationTargets(context, previousValue, additionalContexts, additionalPreviousValues),
                newValue,
                gesture,
                commandDescriptor);
            var envelope = plan.Envelope;
            var mutationTargets = plan.Targets;

            var pendingPaths = new HashSet<string>(PathComparer);
            foreach (var target in mutationTargets)
            {
                var path = target.Context.Document.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (pendingPaths.Add(path))
                {
                    _pendingMutationInvalidations[path] = _clock().Add(MutationSuppressionWindow);
                }
            }

            var mutationApplied = false;

            try
            {
                var result = await DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
                if (result.Status == ChangeDispatchStatus.Success)
                {
                    mutationApplied = true;
                    foreach (var target in mutationTargets)
                    {
                        UpdateMutationOriginBaseline(
                            target.OriginKey,
                            target.Origin,
                            target.ShouldUnset,
                            target.AttributeValue,
                            newValue);
                    }
                }

                return result;
            }
            finally
            {
                if (!mutationApplied)
                {
                    foreach (var path in pendingPaths)
                    {
                        _pendingMutationInvalidations.Remove(path);
                    }
                }
            }
        }

        public async ValueTask<MutationPreviewResult> PreviewLocalValueChangeAsync(
            PropertyChangeContext context,
            object? newValue,
            object? previousValue,
            string gesture,
            EditorCommandDescriptor? command = null,
            IReadOnlyList<PropertyChangeContext>? additionalContexts = null,
            IReadOnlyList<object?>? additionalPreviousValues = null,
            CancellationToken cancellationToken = default)
        {
            if (context is null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.Descriptor is null)
            {
                return MutationPreviewResult.Failure(
                    ChangeDispatchStatus.MutationFailure,
                    "Missing XAML selection.",
                    context.Document?.Text ?? string.Empty,
                    Array.Empty<ChangeOperation>());
            }

            if (_dispatcher is not XamlMutationDispatcher xamlDispatcher)
            {
                return MutationPreviewResult.Failure(
                    ChangeDispatchStatus.MutationFailure,
                    "Mutation dispatcher does not support preview.",
                    context.Document?.Text ?? string.Empty,
                    Array.Empty<ChangeOperation>());
            }

            var commandDescriptor = EditorCommandDescriptor.Normalize(command);
            var plan = BuildLocalValueMutationPlan(
                BuildMutationTargets(context, previousValue, additionalContexts, additionalPreviousValues),
                newValue,
                gesture,
                commandDescriptor);
            return await xamlDispatcher.PreviewAsync(plan.Envelope, cancellationToken).ConfigureAwait(false);
        }

        internal event EventHandler<MutationCompletedEventArgs>? ChangeCompleted;

        internal event EventHandler<ExternalDocumentChangedEventArgs>? ExternalDocumentChanged;

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

            var path = e.Path;
            var now = _clock();
            var pendingHit = false;
            if (!string.IsNullOrWhiteSpace(path) && _pendingMutationInvalidations.TryGetValue(path, out var expiry))
            {
                if (expiry >= now)
                {
                    pendingHit = true;
                }
                else
                {
                    _pendingMutationInvalidations.Remove(path);
                }
            }

            if (pendingHit && path is not null)
            {
                _pendingMutationInvalidations[path] = now.Add(MutationSuppressionWindow);
                return;
            }

            InvalidateMutationOrigins(e.Path);

            if (MutationDispatcher is { } dispatcher &&
                (e.Kind == XamlDocumentChangeKind.Invalidated || e.Kind == XamlDocumentChangeKind.Removed))
            {
                dispatcher.HandleExternalDocumentChanged(e.Path);
            }

            if (ExternalDocumentChanged is not null &&
                (e.Kind == XamlDocumentChangeKind.Invalidated || e.Kind == XamlDocumentChangeKind.Removed))
            {
                var args = new ExternalDocumentChangedEventArgs(
                    e.Path,
                    e.Kind,
                    MutationProvenance.ExternalDocument);
                OnExternalDocumentChanged(args);
            }
        }

        private static IReadOnlyList<(PropertyChangeContext Context, object? PreviousValue)> BuildMutationTargets(
            PropertyChangeContext primaryContext,
            object? primaryPreviousValue,
            IReadOnlyList<PropertyChangeContext>? additionalContexts,
            IReadOnlyList<object?>? additionalPreviousValues)
        {
            if (primaryContext is null)
            {
                throw new ArgumentNullException(nameof(primaryContext));
            }

            if (additionalContexts is null || additionalContexts.Count == 0)
            {
                return new[] { (primaryContext, primaryPreviousValue) };
            }

            if (additionalPreviousValues is not null && additionalPreviousValues.Count != additionalContexts.Count)
            {
                throw new ArgumentException("additionalPreviousValues length must match additionalContexts length.", nameof(additionalPreviousValues));
            }

            var result = new (PropertyChangeContext Context, object? PreviousValue)[additionalContexts.Count + 1];
            result[0] = (primaryContext, primaryPreviousValue);
            for (var index = 0; index < additionalContexts.Count; index++)
            {
                var context = additionalContexts[index];
                if (context is null)
                {
                    throw new ArgumentNullException(nameof(additionalContexts), "Additional mutation contexts must not contain null entries.");
                }

                var previous = additionalPreviousValues is not null ? additionalPreviousValues[index] : null;
                result[index + 1] = (context, previous);
            }

            return result;
        }

        private LocalValueMutationPlan BuildLocalValueMutationPlan(
            IReadOnlyList<(PropertyChangeContext Context, object? PreviousValue)> targets,
            object? newValue,
            string gesture,
            EditorCommandDescriptor commandDescriptor)
        {
            if (targets is null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            if (targets.Count == 0)
            {
                throw new ArgumentException("At least one mutation target is required.", nameof(targets));
            }

            var primary = targets[0];
            var primaryContext = primary.Context;

            var documentPath = primaryContext.Document.Path;
            foreach (var target in targets)
            {
                if (!string.Equals(target.Context.Document.Path, documentPath, PathComparison))
                {
                    throw new InvalidOperationException("Multi-selection edits require all targets to originate from the same XAML document.");
                }

                if (!ReferenceEquals(target.Context.Property, primaryContext.Property))
                {
                    throw new InvalidOperationException("Multi-selection edits require all targets to reference the same AvaloniaProperty.");
                }
            }

            var attributeName = BuildAttributeName(primaryContext.Property);
            var valueKind = DetermineValueKind(newValue);
            var bindingPayload = BuildBindingPayload(newValue);
            var resourcePayload = BuildResourcePayload(newValue);
            var adjustedCommand = EnsureCommandDescriptor(commandDescriptor, primaryContext.Property.PropertyType, newValue, gesture);

            var operations = new List<ChangeOperation>(targets.Count);
            var mutationTargets = new List<MutationTargetPlan>(targets.Count);

            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                var context = target.Context;

                var origin = GetOrCreateMutationOrigin(context, target.PreviousValue, out var originKey);
                var shouldUnset = ShouldUnsetAttribute(origin, newValue);
                var effectiveValueKind = shouldUnset ? "Unset" : valueKind;
                var effectiveBindingPayload = shouldUnset ? null : bindingPayload;
                var effectiveResourcePayload = shouldUnset ? null : resourcePayload;
                var valueText = shouldUnset ? null : DetermineAttributeValueText(context.Property, newValue, origin);

                var operation = BuildSetAttributeOperation(
                    context,
                    attributeName,
                    effectiveValueKind,
                    valueText,
                    effectiveBindingPayload,
                    effectiveResourcePayload,
                    shouldUnset,
                    gesture,
                    adjustedCommand,
                    index);

                operations.Add(operation);
                mutationTargets.Add(new MutationTargetPlan(context, origin, originKey, shouldUnset, valueText));
            }

            var envelope = BuildSetAttributeEnvelope(primaryContext, gesture, adjustedCommand, operations, mutationTargets);
            return new LocalValueMutationPlan(envelope, mutationTargets, adjustedCommand);
        }

        private NamespaceRequirement? DetermineNamespaceRequirement(PropertyChangeContext context, string attributeName, string? valueText)
        {
            if (context is null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(attributeName))
            {
                var colonIndex = attributeName.IndexOf(':');
                if (colonIndex > 0)
                {
                    var prefix = attributeName.Substring(0, colonIndex);
                    if (!string.Equals(prefix, "xmlns", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ResolveNamespaceForPrefix(context, prefix) is { } attributeRequirement)
                        {
                            return attributeRequirement;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(valueText))
            {
                foreach (var candidatePrefix in ExtractPrefixesFromValue(valueText))
                {
                    var valueRequirement = ResolveNamespaceForPrefix(context, candidatePrefix) ??
                                           BuildNamespaceFallback(context, candidatePrefix);
                    if (valueRequirement is not null)
                    {
                        return valueRequirement;
                    }
                }
            }

            return null;
        }

        private static bool TryGetNamespaceDeclaration(PropertyChangeContext context, string prefix, out string? value)
        {
            var attributeName = string.IsNullOrEmpty(prefix) ? "xmlns" : $"xmlns:{prefix}";
            if (XamlGuardUtilities.TryLocateAttribute(context.Document, context.Descriptor, attributeName, out var attribute, out _)
                && attribute is not null &&
                attribute.ValueNode is { } valueNode)
            {
                var text = context.Document.Text;
                var raw = text.Substring(valueNode.Span.Start, valueNode.Span.Length);
                value = TrimQuotes(raw);
                return true;
            }

            value = null;
            return false;
        }

        private NamespaceRequirement? ResolveNamespaceForPrefix(PropertyChangeContext context, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return null;
            }

            if (TryGetNamespaceDeclaration(context, prefix, out var existingValue) && !string.IsNullOrEmpty(existingValue))
            {
                return new NamespaceRequirement(prefix, existingValue);
            }

            if (string.Equals(prefix, "x", StringComparison.Ordinal))
            {
                return new NamespaceRequirement(prefix, "http://schemas.microsoft.com/winfx/2006/xaml");
            }

            var ownerType = context.Property.OwnerType;
            var clrNamespace = ownerType.Namespace;
            if (string.IsNullOrEmpty(clrNamespace))
            {
                return null;
            }

            var assemblyName = ownerType.Assembly.GetName().Name;
            var builder = new StringBuilder();
            builder.Append("clr-namespace:");
            builder.Append(clrNamespace);

            if (!string.IsNullOrEmpty(assemblyName))
            {
                builder.Append(";assembly=");
                builder.Append(assemblyName);
            }

            return new NamespaceRequirement(prefix, builder.ToString());
        }

        private static NamespaceRequirement? BuildNamespaceFallback(PropertyChangeContext context, string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
            {
                return null;
            }

            var ownerType = context.Property.OwnerType;
            var clrNamespace = ownerType.Namespace;
            if (string.IsNullOrEmpty(clrNamespace))
            {
                return null;
            }

            var assemblyName = ownerType.Assembly.GetName().Name;
            var builder = new StringBuilder();
            builder.Append("clr-namespace:");
            builder.Append(clrNamespace);

            if (!string.IsNullOrEmpty(assemblyName))
            {
                builder.Append(";assembly=");
                builder.Append(assemblyName);
            }

            return new NamespaceRequirement(prefix, builder.ToString());
        }

        private static IEnumerable<string> ExtractPrefixesFromValue(string valueText)
        {
            if (string.IsNullOrEmpty(valueText))
            {
                yield break;
            }

            for (var index = 0; index < valueText.Length; index++)
            {
                if (valueText[index] != '{')
                {
                    continue;
                }

                var start = index + 1;
                if (start >= valueText.Length)
                {
                    break;
                }

                var end = start;
                while (end < valueText.Length && !char.IsWhiteSpace(valueText[end]) && valueText[end] != '}' && valueText[end] != ',')
                {
                    end++;
                }

                if (end <= start)
                {
                    continue;
                }

                var token = valueText.Substring(start, end - start);
                var colonIndex = token.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var prefix = token.Substring(0, colonIndex);
                if (!string.IsNullOrEmpty(prefix))
                {
                    yield return prefix;
                }
            }
        }


        private IReadOnlyList<ChangeOperation> CombineWithNamespaceImports(
            IReadOnlyList<ChangeOperation> operations,
            IReadOnlyList<MutationTargetPlan> mutationTargets)
        {
            if (operations.Count == 0)
            {
                return operations;
            }

            var namespaceOperations = new List<ChangeOperation>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            var count = Math.Min(operations.Count, mutationTargets.Count);
            for (var index = 0; index < count; index++)
            {
                var operation = operations[index];
                var payload = operation.Payload;

                if (payload is null || string.IsNullOrWhiteSpace(payload.NamespacePrefix) || string.IsNullOrWhiteSpace(payload.Namespace))
                {
                    continue;
                }

                var target = mutationTargets[index].Context;
                var prefix = payload.NamespacePrefix!;
                var namespaceValue = payload.Namespace;

                var key = $"{target.Descriptor.Id}:{prefix}";
                if (!seenKeys.Add(key))
                {
                    continue;
                }

                if (HasNamespaceDeclaration(target, prefix, namespaceValue))
                {
                    continue;
                }

                var nsOperation = BuildNamespaceOperation(target, prefix, namespaceValue, namespaceOperations.Count);
                namespaceOperations.Add(nsOperation);
            }

            if (namespaceOperations.Count == 0)
            {
                return operations;
            }

            var combined = new ChangeOperation[namespaceOperations.Count + operations.Count];
            namespaceOperations.CopyTo(combined, 0);
            for (var i = 0; i < operations.Count; i++)
            {
                combined[namespaceOperations.Count + i] = operations[i];
            }

            return combined;
        }

        private bool HasNamespaceDeclaration(PropertyChangeContext context, string prefix, string expectedValue)
        {
            return TryGetNamespaceDeclaration(context, prefix, out var existingValue) &&
                   string.Equals(existingValue, expectedValue, StringComparison.Ordinal);
        }

        private ChangeOperation BuildNamespaceOperation(
            PropertyChangeContext context,
            string prefix,
            string namespaceValue,
            int index)
        {
            var attributeName = string.IsNullOrEmpty(prefix) ? "xmlns" : $"xmlns:{prefix}";
            var descriptor = context.Descriptor;
            var document = context.Document;

            var targetInfo = new ChangeTarget
            {
                DescriptorId = descriptor.Id.ToString(),
                Path = BuildTargetPath(descriptor, attributeName),
                NodeType = AttributeNodeType
            };

            var spanHash = XamlGuardUtilities.ComputeAttributeHash(document, descriptor, attributeName);

            return new ChangeOperation
            {
                Id = $"ns-{index + 1}",
                Type = ChangeOperationTypes.SetNamespace,
                Target = targetInfo,
                Payload = new ChangePayload
                {
                    Name = attributeName,
                    Namespace = namespaceValue,
                    NamespacePrefix = prefix,
                    ValueKind = "Literal",
                    NewValue = namespaceValue
                },
                Guard = new ChangeOperationGuard
                {
                    SpanHash = spanHash
                }
            };
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

        private ChangeOperation BuildSetAttributeOperation(
            PropertyChangeContext context,
            string attributeName,
            string valueKind,
            string? valueText,
            BindingPayload? bindingPayload,
            ResourcePayload? resourcePayload,
            bool shouldUnset,
            string gesture,
            EditorCommandDescriptor command,
            int operationIndex)
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

            var namespaceRequirement = DetermineNamespaceRequirement(context, attributeName, valueText);

            var payload = new ChangePayload
            {
                Name = attributeName,
                Namespace = namespaceRequirement?.Value ?? string.Empty,
                NamespacePrefix = namespaceRequirement?.Prefix,
                ValueKind = valueKind,
                NewValue = shouldUnset ? null : valueText,
                Binding = bindingPayload,
                Resource = resourcePayload
            };

            var operation = new ChangeOperation
            {
                Id = $"op-{operationIndex + 1}",
                Type = ChangeOperationTypes.SetAttribute,
                Target = targetInfo,
                Payload = payload,
                Guard = new ChangeOperationGuard
                {
                    SpanHash = spanHash
                }
            };

            return operation;
        }

        private ChangeEnvelope BuildSetAttributeEnvelope(
            PropertyChangeContext primaryContext,
            string gesture,
            EditorCommandDescriptor command,
            IReadOnlyList<ChangeOperation> operations,
            IReadOnlyList<MutationTargetPlan> mutationTargets)
        {
            if (operations is null)
            {
                throw new ArgumentNullException(nameof(operations));
            }

            if (operations.Count == 0)
            {
                throw new ArgumentException("At least one change operation is required.", nameof(operations));
            }

            var effectiveOperations = CombineWithNamespaceImports(operations, mutationTargets);

            var document = primaryContext.Document;
            var property = primaryContext.Property;
            var target = primaryContext.Target;
            var descriptor = primaryContext.Descriptor;
            var elementId = BuildElementId(target);
            var propertyQualifiedName = $"{property.OwnerType.Name}.{property.Name}";

            var changes = new ChangeOperation[effectiveOperations.Count];
            for (var index = 0; index < effectiveOperations.Count; index++)
            {
                changes[index] = effectiveOperations[index];
            }

            return new ChangeEnvelope
            {
                BatchId = _idProvider(),
                InitiatedAt = _clock(),
                Source = new ChangeSourceInfo
                {
                    Inspector = InspectorName,
                    Gesture = gesture,
                    Command = command.ToCommandInfo()
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
                    Frame = primaryContext.Frame,
                    ValueSource = primaryContext.ValueSource
                },
                Guards = new ChangeGuardsInfo
                {
                    DocumentVersion = document.Version.ToString(),
                    RuntimeFingerprint = elementId
                },
                Changes = changes
            };
        }

        private static EditorCommandDescriptor EnsureCommandDescriptor(
            EditorCommandDescriptor command,
            Type propertyType,
            object? newValue,
            string gesture)
        {
            if (!string.Equals(command.Id, EditorCommandDescriptor.Default.Id, StringComparison.Ordinal))
            {
                return command;
            }

            if (propertyType == typeof(bool) ||
                propertyType == typeof(bool?) ||
                newValue is bool ||
                string.Equals(gesture, "ToggleCheckBox", StringComparison.Ordinal))
            {
                return EditorCommandDescriptor.Toggle;
            }

            if (IsNumericType(propertyType))
            {
                return EditorCommandDescriptor.Slider;
            }

            if (propertyType == typeof(Color) || propertyType == typeof(Color?) || string.Equals(gesture, "PickColor", StringComparison.Ordinal))
            {
                return EditorCommandDescriptor.ColorPicker;
            }

            if (typeof(IBinding).IsAssignableFrom(propertyType) || string.Equals(gesture, "OpenBindingEditor", StringComparison.Ordinal))
            {
                return EditorCommandDescriptor.BindingEditor;
            }

            return command;
        }

        private static bool IsNumericType(Type propertyType)
        {
            if (propertyType is null)
            {
                return false;
            }

            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                propertyType = Nullable.GetUnderlyingType(propertyType)!;
            }

            if (propertyType.IsEnum)
            {
                return false;
            }

            switch (Type.GetTypeCode(propertyType))
            {
                case TypeCode.Byte:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    return true;
                default:
                    return false;
            }
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
            var provenance = MutationProvenanceHelper.FromEnvelope(envelope);
            var startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                var result = await _dispatcher.DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
                var duration = StopwatchHelper.GetElapsedTime(startTimestamp);
                MutationInstrumentation.RecordMutation(duration, result.Status);
                MutationTelemetry.ReportMutation(envelope, result, duration, provenance);
                if (!_dispatcherProvidesNotifications)
                {
                    OnChangeCompleted(new MutationCompletedEventArgs(envelope, result, provenance));
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var duration = StopwatchHelper.GetElapsedTime(startTimestamp);
                var failure = ChangeDispatchResult.MutationFailure(null, $"Change dispatch failed: {ex.Message}");
                MutationInstrumentation.RecordMutation(duration, failure.Status);
                MutationTelemetry.ReportMutation(envelope, failure, duration, provenance);
                OnChangeCompleted(new MutationCompletedEventArgs(envelope, failure, provenance));
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

        private void OnExternalDocumentChanged(ExternalDocumentChangedEventArgs args)
        {
            try
            {
                ExternalDocumentChanged?.Invoke(this, args);
            }
            catch
            {
                // Diagnostics notifications should not throw.
            }
        }

        private sealed record class LocalValueMutationPlan(
            ChangeEnvelope Envelope,
            IReadOnlyList<MutationTargetPlan> Targets,
            EditorCommandDescriptor Command);

        private readonly record struct MutationTargetPlan(
            PropertyChangeContext Context,
            PropertyMutationOrigin Origin,
            MutationOriginKey OriginKey,
            bool ShouldUnset,
            string? AttributeValue);

        private readonly record struct NamespaceRequirement(string Prefix, string Value);

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
