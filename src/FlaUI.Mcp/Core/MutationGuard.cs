namespace FlaUI.Mcp.Core;

/// <summary>Never starts a mutation after cancellation or a failed identity check.</summary>
public static class MutationGuard
{
    public static readonly AsyncLocal<Action?> BeforeMutation = new();

    public static void CheckPermission() => BeforeMutation.Value?.Invoke();

    public static void Execute(Action validateIdentity, Action mutation)
    {
        OperationContext.Check();
        validateIdentity();
        OperationContext.Check();
        CheckPermission();
        mutation();
    }
}
