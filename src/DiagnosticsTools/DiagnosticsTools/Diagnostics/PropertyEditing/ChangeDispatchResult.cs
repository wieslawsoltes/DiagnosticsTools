using System.Diagnostics.CodeAnalysis;

namespace Avalonia.Diagnostics.PropertyEditing
{
    internal readonly struct ChangeDispatchResult
    {
        private ChangeDispatchResult(ChangeDispatchStatus status, string? operationId = null, string? message = null)
        {
            Status = status;
            OperationId = operationId;
            Message = message;
        }

        public ChangeDispatchStatus Status { get; }

        public string? OperationId { get; }

        public string? Message { get; }

        public static ChangeDispatchResult Success() => new(ChangeDispatchStatus.Success);

        public static ChangeDispatchResult GuardFailure(string? operationId, string? message = null) =>
            new(ChangeDispatchStatus.GuardFailure, operationId, message);

        public static ChangeDispatchResult MutationFailure(string? operationId, string? message = null) =>
            new(ChangeDispatchStatus.MutationFailure, operationId, message);
    }

    internal enum ChangeDispatchStatus
    {
        Success,
        GuardFailure,
        MutationFailure
    }
}
