using System.Diagnostics.CodeAnalysis;

namespace Avalonia.Diagnostics.PropertyEditing
{
    public readonly struct ChangeDispatchResult
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

        public static ChangeDispatchResult Success() => Success(message: null);

        public static ChangeDispatchResult Success(string? message) => new(ChangeDispatchStatus.Success, null, message);

        public static ChangeDispatchResult GuardFailure(string? operationId, string? message = null) =>
            new(ChangeDispatchStatus.GuardFailure, operationId, message);

        public static ChangeDispatchResult MutationFailure(string? operationId, string? message = null) =>
            new(ChangeDispatchStatus.MutationFailure, operationId, message);
    }

    public enum ChangeDispatchStatus
    {
        Success,
        GuardFailure,
        MutationFailure
    }
}
