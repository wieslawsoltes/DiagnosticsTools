using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Diagnostics.PropertyEditing;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Avalonia.Diagnostics.ViewModels
{
    internal sealed class MutationPreviewDialogViewModel : ViewModelBase
    {
        private readonly bool _allowRawEditing;

        public MutationPreviewDialogViewModel(MutationPreviewResult preview, bool allowRawEditing)
        {
            Preview = preview ?? throw new ArgumentNullException(nameof(preview));
            _allowRawEditing = allowRawEditing || preview.Status != ChangeDispatchStatus.Success;

            ApplyCommand = new DelegateCommand(OnApply, () => CanApply);
            CancelCommand = new DelegateCommand(OnCancel);
            EditRawCommand = new DelegateCommand(OnEditRaw, () => CanEditRaw);

            Changes = Preview.Operations
                .Select(operation => new MutationPreviewChangeViewModel(operation))
                .ToList()
                .AsReadOnly();

            OriginalHighlightBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x44, 0xD4, 0x3C, 0x4A));
            PreviewHighlightBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x44, 0x2E, 0x8B, 0x57));
        }

        public MutationPreviewResult Preview { get; }

        public string StatusText => Preview.Status switch
        {
            ChangeDispatchStatus.Success => "Preview ready. Review the changes before applying.",
            ChangeDispatchStatus.GuardFailure => "Guard check failed. The document has changed since it was loaded.",
            ChangeDispatchStatus.MutationFailure => "Preview unavailable due to an error.",
            _ => "Preview status unknown."
        };

        public string? Message => Preview.Message;

        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

        public string OriginalText => Preview.OriginalText;

        public string PreviewText => Preview.PreviewText;

        public IReadOnlyList<MutationPreviewHighlight> OriginalHighlights => Preview.OriginalHighlights;

        public IReadOnlyList<MutationPreviewHighlight> PreviewHighlights => Preview.PreviewHighlights;

        public IBrush OriginalHighlightBrush { get; }

        public IBrush PreviewHighlightBrush { get; }

        public IReadOnlyList<MutationPreviewChangeViewModel> Changes { get; }

        public bool HasChanges => Changes.Count > 0;

        public bool HasMultipleChanges => Changes.Count > 1;

        public string ChangesTitle => HasMultipleChanges ? "Changes" : "Change";

        public bool CanApply => Preview.Status == ChangeDispatchStatus.Success;

        public bool CanEditRaw => _allowRawEditing;

        public ICommand ApplyCommand { get; }

        public ICommand CancelCommand { get; }

        public ICommand EditRawCommand { get; }

        public event EventHandler<MutationPreviewDecision>? CloseRequested;

        private void OnApply() => CloseRequested?.Invoke(this, MutationPreviewDecision.Apply);

        private void OnCancel() => CloseRequested?.Invoke(this, MutationPreviewDecision.Cancel);

        private void OnEditRaw() => CloseRequested?.Invoke(this, MutationPreviewDecision.EditRaw);
    }

    internal sealed class MutationPreviewChangeViewModel
    {
        public MutationPreviewChangeViewModel(ChangeOperation operation)
        {
            Operation = operation ?? throw new ArgumentNullException(nameof(operation));
            Header = BuildHeader(operation);
            Summary = BuildSummary(operation);
            HasSummary = !string.IsNullOrWhiteSpace(Summary) &&
                         !string.Equals(Summary, operation.Type, StringComparison.Ordinal) &&
                         !string.Equals(Summary, Header, StringComparison.Ordinal);
        }

        public ChangeOperation Operation { get; }

        public string Header { get; }

        public string Summary { get; }

        public bool HasSummary { get; }

        private static string BuildHeader(ChangeOperation operation)
        {
            var targetPath = operation.Target.Path;
            return string.IsNullOrWhiteSpace(targetPath) ? operation.Type : $"{operation.Type} @ {targetPath}";
        }

        private static string BuildSummary(ChangeOperation operation)
        {
            var payload = operation.Payload;
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(payload.Name))
            {
                parts.Add(payload.Name);
            }

            if (!string.IsNullOrWhiteSpace(payload.NewValue))
            {
                parts.Add($"= {payload.NewValue}");
            }
            else if (payload.ValueKind == "Unset")
            {
                parts.Add("Unset");
            }
            else if (payload.Binding is not null)
            {
                parts.Add("Binding");
            }
            else if (payload.Resource is not null)
            {
                parts.Add("Resource");
            }
            else if (!string.IsNullOrWhiteSpace(payload.ValueKind) && payload.ValueKind != "Literal")
            {
                parts.Add(payload.ValueKind);
            }

            return parts.Count == 0 ? string.Empty : string.Join(" ", parts);
        }
    }
}
