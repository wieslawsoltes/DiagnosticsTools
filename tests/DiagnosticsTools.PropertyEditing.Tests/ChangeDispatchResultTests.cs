using Avalonia.Diagnostics.PropertyEditing;
using Xunit;

namespace PropertyEditing.Tests;

public class ChangeDispatchResultTests
{
    [Fact]
    public void Success_Result_HasSuccessStatus()
    {
        var result = ChangeDispatchResult.Success();

        Assert.Equal(ChangeDispatchStatus.Success, result.Status);
        Assert.Null(result.OperationId);
        Assert.Null(result.Message);
    }

    [Fact]
    public void MutationFailure_SetsOperationAndMessage()
    {
        var result = ChangeDispatchResult.MutationFailure("op-1", "failed");

        Assert.Equal(ChangeDispatchStatus.MutationFailure, result.Status);
        Assert.Equal("op-1", result.OperationId);
        Assert.Equal("failed", result.Message);
    }
}
