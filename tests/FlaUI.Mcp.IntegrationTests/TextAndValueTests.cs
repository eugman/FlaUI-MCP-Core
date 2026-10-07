using System.Diagnostics;
using FlaUI.Mcp.Core;
using FlaUI.Mcp.Tools;
using Xunit.Abstractions;

namespace FlaUI.Mcp.IntegrationTests;

/// <summary>
/// Tests for windows_get_text, windows_type, and windows_fill tools.
/// </summary>
[Collection("TestApps")]
public class TextAndValueTests
{
    private readonly TestAppFixture _fixture;
    private readonly ITestOutputHelper _output;

    public TextAndValueTests(TestAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// Navigate to a tab and poll until an expected element appears.
    /// Returns the ref of the expected element.
    /// </summary>
    private async Task<string> NavigateToTabAndFind(string windowHandle, string tabName, string elementName)
    {
        var tabRef = _fixture.FindRefByName(windowHandle, tabName);
        Assert.NotNull(tabRef);

        var clickTool = new ClickTool(_fixture.Elements);
        await _fixture.CallTool(clickTool, new { @ref = tabRef });

        // Poll for the element to appear after tab switch
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100);
            var found = _fixture.FindRefByName(windowHandle, elementName);
            if (found != null)
            {
                return found;
            }
        }

        Assert.Fail($"Element \"{elementName}\" not found after navigating to \"{tabName}\" tab.");
        return ""; // unreachable
    }

    [Fact]
    public async Task WinForms_GetText_ReadOnlyField()
    {
        var resultRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Forms", "Result");

        var tool = new GetTextTool(_fixture.Elements);
        var text = await _fixture.CallTool(tool, new { @ref = resultRef });
        _output.WriteLine($"Result text: '{text}'");
        Assert.Equal("Computed value here", text);
    }

    [Fact]
    public async Task WinForms_TypeAndGetText()
    {
        var nameRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Forms", "Name");

        // Fill the name field
        var fillTool = new FillTool(_fixture.Elements);
        var fillResult = await _fixture.CallTool(fillTool, new { @ref = nameRef, value = "Test User" });
        _output.WriteLine($"Fill result: {fillResult}");

        // Read it back
        var textTool = new GetTextTool(_fixture.Elements);
        var text = await _fixture.CallTool(textTool, new { @ref = nameRef });
        _output.WriteLine($"Read back: '{text}'");
        Assert.Equal("Test User", text);

        // Clear it
        await _fixture.CallTool(fillTool, new { @ref = nameRef, value = "" });
    }

    [Fact]
    public async Task Wpf_GetText_ReadOnlyField()
    {
        var resultRef = await NavigateToTabAndFind(_fixture.WpfHandle, "Forms", "Result");

        var tool = new GetTextTool(_fixture.Elements);
        var text = await _fixture.CallTool(tool, new { @ref = resultRef });
        _output.WriteLine($"Result text: '{text}'");
        Assert.Equal("Computed value here", text);
    }

    // Typed keystrokes are queued input; the control may not have processed them all when the tool returns.
    private async Task<string> ReadWhenSettled(string elementRef, string expected)
    {
        var read = new GetTextTool(_fixture.Elements);
        var text = "";
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 3000; await Task.Delay(100))
        {
            if ((text = await _fixture.CallTool(read, new { @ref = elementRef })) == expected)
            {
                break;
            }
        }

        return text;
    }

    [Fact]
    public async Task WinForms_TypeSendsLineBreaksAsEnter()
    {
        var fill = new FillTool(_fixture.Elements);
        var type = new TypeTool(_fixture.Elements, sessions: _fixture.Session);
        var read = new GetTextTool(_fixture.Elements);

        // Each FindRefByName takes a fresh snapshot, which retires earlier refs; look up one box at a time.
        var notesRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Forms", "Notes");
        try
        {
            await _fixture.CallTool(fill, new { @ref = notesRef, value = "" });
            _output.WriteLine(await _fixture.CallTool(type, new { @ref = notesRef, text = "first\nsecond" }));
            Assert.Equal("first\r\nsecond", await ReadWhenSettled(notesRef, "first\r\nsecond"));
        }
        finally
        {
            await _fixture.CallTool(fill, new { @ref = notesRef, value = "" });
        }

        // A single-line box ignores the Enter; no Ctrl+Enter or stray characters appear.
        var nameRef = _fixture.FindRefByName(_fixture.WinFormsHandle, "Name");
        Assert.NotNull(nameRef);
        try
        {
            await _fixture.CallTool(fill, new { @ref = nameRef, value = "" });
            _output.WriteLine(await _fixture.CallTool(type, new { @ref = nameRef, text = "one\r\ntwo" }));
            Assert.Equal("onetwo", await ReadWhenSettled(nameRef, "onetwo"));
        }
        finally
        {
            await _fixture.CallTool(fill, new { @ref = nameRef, value = "" });
        }
    }

    [Fact]
    public async Task WinForms_PasteInsertsMultilineTextVerbatim()
    {
        // Overwrites the desktop clipboard by design.
        var notesRef = await NavigateToTabAndFind(_fixture.WinFormsHandle, "Forms", "Notes");
        var fill = new FillTool(_fixture.Elements);
        const string code = "foreach (var m in Selected.Measures)\r\n    m.FormatString = \"#,0.00\";";
        try
        {
            await _fixture.CallTool(fill, new { @ref = notesRef, value = "" });
            var paste = new PasteTool(
                _fixture.Elements,
                new PendingInvokeTracker(),
                ProcessPolicy.AllowAll,
                _fixture.Session
            );
            _output.WriteLine(await _fixture.CallTool(paste, new { @ref = notesRef, text = code }));
            Assert.Equal(code, await _fixture.CallTool(new GetTextTool(_fixture.Elements), new { @ref = notesRef }));
        }
        finally
        {
            await _fixture.CallTool(fill, new { @ref = notesRef, value = "" });
        }
    }
}
