using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.IntegrationTests;

[Collection("TestApps")]
public class GuardedInputTests(TestAppFixture fixture)
{
    [Fact]
    public void ContainerClickCanToggleChildDespitePassingNativeWindowHitCheck()
    {
        var query = new ElementQuery(fixture.Session, fixture.Elements, new PendingInvokeTracker());
        query
            .Resolve(fixture.WinFormsHandle, new(AutomationId: "ButtonsTab", ControlType: "TabItem"))
            .Patterns.SelectionItem.Pattern.Select();
        var panel = query.Resolve(fixture.WinFormsHandle, new(AutomationId: "ContainerHitPanel"));
        var checkbox = query.Resolve(fixture.WinFormsHandle, new(AutomationId: "ContainerHitCheckBox"));
        var toggle = checkbox.Patterns.Toggle.Pattern;
        var before = toggle.ToggleState.Value;
        using var input = new GuardedInput(fixture.Session.GetInputTarget(fixture.WinFormsHandle));
        Assert.True(checkbox.BoundingRectangle.Contains(panel.GetClickablePoint()));
        Assert.Equal(
            fixture.Session.GetWindowHwnd(fixture.WinFormsHandle),
            Win32Desktop.WindowAt(panel.GetClickablePoint())
        );
        try
        {
            input.Click(panel);
            Assert.True(
                SpinWait.SpinUntil(() => toggle.ToggleState.Value != before, TimeSpan.FromSeconds(2)),
                "The child checkbox should receive the container-center click."
            );
        }
        finally
        {
            if (toggle.ToggleState.Value != before)
            {
                toggle.Toggle();
            }
        }
    }

    [Fact]
    public void ExactControlFocusIsRequiredBeforeKeyboardInput()
    {
        var query = new ElementQuery(fixture.Session, fixture.Elements, new PendingInvokeTracker());
        query
            .Resolve(fixture.WinFormsHandle, new(AutomationId: "FormsTab", ControlType: "TabItem"))
            .Patterns.SelectionItem.Pattern.Select();
        var edit = query.Resolve(fixture.WinFormsHandle, new(AutomationId: "NameTextBox"));
        using var input = new GuardedInput(
            fixture.Session.GetInputTarget(fixture.WinFormsHandle),
            edit,
            verifyFocus: true
        );
        Assert.True(edit.Properties.HasKeyboardFocus.Value);
    }

    [Fact]
    public void FocusStealStopsExistingInputLease()
    {
        var target = fixture.Session.GetInputTarget(fixture.WpfHandle);
        using var input = new GuardedInput(target);
        fixture.Session.FocusWindow(fixture.WinFormsHandle);
        var sent = false;
        Assert.Throws<InvalidOperationException>(() => input.Send(() => sent = true));
        Assert.False(sent);
    }

    [Fact]
    public void SecondPhysicalInputLeaseTimesOutWithoutInput()
    {
        var target = fixture.Session.GetInputTarget(fixture.WpfHandle);
        using var first = new GuardedInput(target);
        Assert.Throws<TimeoutException>(() => new GuardedInput(target));
    }
}
