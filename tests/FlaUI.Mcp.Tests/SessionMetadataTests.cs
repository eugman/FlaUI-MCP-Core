using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Mcp.Core;
using Xunit;

namespace FlaUI.Mcp.Tests;

public sealed class SessionMetadataTests
{
    private static SessionManager Sessions() => new(null, ProcessPolicy.AllowAll);

    // The metadata tests never invoke members on this provider-free sentinel.
    private static Window Window() => (Window)RuntimeHelpers.GetUninitializedObject(typeof(Window));

    [Fact]
    public void ConcurrentRegistrationPublishesConsistentDistinctHandles()
    {
        using var sessions = Sessions();
        var handles = new ConcurrentBag<string>();
        Parallel.For(
            1,
            501,
            id =>
            {
                var handle = sessions.PublishWindow(null, id, id, new(id, id, id));
                handles.Add(handle);
                Assert.Equal(id, sessions.GetWindowProcessId(handle));
                Assert.Equal((nint)id, sessions.GetWindowHwnd(handle));
            }
        );
        Assert.Equal(500, handles.Distinct().Count());
    }

    [Fact]
    public void ConcurrentObservationOfSameIdentityReusesOneHandle()
    {
        using var sessions = Sessions();
        var handles = new ConcurrentBag<string>();
        Parallel.For(0, 100, _ => handles.Add(sessions.PublishWindow(null, 42, 7, new(42, 7, 9))));
        Assert.Single(handles.Distinct());
    }

    [Fact]
    public void CancellationPreventsLateRegistrationWithoutConsumingHandle()
    {
        using var sessions = Sessions();
        var operation = new OperationContext();
        operation.Stop.Cancel();
        OperationContext.Current.Value = operation;
        try
        {
            Assert.Throws<OperationCanceledException>(() => sessions.PublishWindow(null, 42, 7, new(42, 7, 9)));
        }
        finally
        {
            OperationContext.Current.Value = null;
        }

        Assert.Equal("w1", sessions.PublishWindow(null, 42, 7, new(42, 7, 9)));
    }

    [Fact]
    public void ForgottenRegistrationCannotBeResurrectedByLateAttachment()
    {
        using var sessions = Sessions();
        var handle = sessions.PublishWindow(null, 42, 7, new(42, 7, 9));
        var result = sessions.GetWindow(
            handle,
            _ =>
            {
                // Recovery on another thread must not wait for the UIA call's metadata lock.
                Assert.True(Task.Run(() => sessions.ForgetWindow(handle)).Wait(TimeSpan.FromSeconds(3)));
                return Window();
            },
            _ => { }
        );

        Assert.Null(result);
        Assert.Equal(0, sessions.GetWindowProcessId(handle));
        Assert.Null(
            sessions.GetWindow(handle, _ => throw new Exception("Forgotten HWND must not attach again"), _ => { })
        );
    }

    [Fact]
    public void CancelledAttachmentCannotPopulateCache()
    {
        using var sessions = Sessions();
        var handle = sessions.PublishWindow(null, 42, 7, new(42, 7, 9));
        var operation = new OperationContext();
        OperationContext.Current.Value = operation;
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                sessions.GetWindow(
                    handle,
                    _ =>
                    {
                        operation.Stop.Cancel();
                        return Window();
                    },
                    _ => { }
                )
            );
        }
        finally
        {
            OperationContext.Current.Value = null;
        }

        var fresh = Window();
        Assert.Same(fresh, sessions.GetWindow(handle, _ => fresh, _ => { }));
    }

    [Fact]
    public void IdentityValidationDoesNotHoldMetadataLock()
    {
        using var sessions = Sessions();
        var window = Window();
        var handle = sessions.PublishWindow(window, 42, 7, new(42, 7, 9));
        var result = sessions.GetWindow(
            handle,
            _ => throw new Exception("Window is cached"),
            _ =>
            {
                Assert.True(Task.Run(() => sessions.ForgetWindow(handle)).Wait(TimeSpan.FromSeconds(3)));
            }
        );
        Assert.Null(result);
    }

    [Fact]
    public void DisposalPreventsLateRegistration()
    {
        using var sessions = Sessions();
        sessions.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sessions.PublishWindow(null, 42, 7, new(42, 7, 9)));
    }
}
