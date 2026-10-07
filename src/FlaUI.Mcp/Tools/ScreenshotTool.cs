using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Mcp.Core;

namespace FlaUI.Mcp.Tools;

/// <summary>
/// Take a screenshot
/// </summary>
public class ScreenshotTool : ToolBase
{
    private readonly SessionManager _sessionManager;
    private readonly ElementRegistry _elementRegistry;
    private readonly PendingInvokeTracker _invokeTracker;
    private readonly ProcessPolicy _processPolicy;

    public ScreenshotTool(
        SessionManager sessionManager,
        ElementRegistry elementRegistry,
        PendingInvokeTracker? invokeTracker = null,
        ProcessPolicy? processPolicy = null
    )
    {
        _sessionManager = sessionManager;
        _elementRegistry = elementRegistry;
        _invokeTracker = invokeTracker ?? new PendingInvokeTracker();
        _processPolicy = processPolicy ?? ProcessPolicy.AllowAll;
    }

    public override string Name => "windows_screenshot";

    public override string Description =>
        "Capture a window, element or the screen as PNG. Normal capture uses UIA bounds and may omit the title bar; strictNative captures the whole native window. With savePath and includeImage=false only the path is returned; view the saved image before claiming a visual result.";

    public override object InputSchema =>
        new
        {
            type = "object",
            properties = new
            {
                frame = new
                {
                    type = "object",
                    description = "Optional exact source-size assertion and pixel crop; no rescaling.",
                    properties = new
                    {
                        sourceWidth = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 8192,
                        },
                        sourceHeight = new
                        {
                            type = "integer",
                            minimum = 1,
                            maximum = 8192,
                        },
                        x = new { type = "integer", minimum = 0 },
                        y = new { type = "integer", minimum = 0 },
                        width = new { type = "integer", minimum = 1 },
                        height = new { type = "integer", minimum = 1 },
                    },
                    required = new[] { "x", "y", "width", "height" },
                },
                handle = new
                {
                    type = "string",
                    description = "Window handle. If omitted, captures the foreground window.",
                },
                @ref = new { type = "string", description = "Element ref to capture." },
                fullScreen = new
                {
                    type = "boolean",
                    description = "Capture the entire screen (default: false). Disabled while an app allowlist is active.",
                },
                background = new
                {
                    type = "boolean",
                    description = "Native whole-window capture. A handle falls back to screen pixels; a Window ref does not. Not with fullScreen.",
                },
                strictNative = new
                {
                    type = "boolean",
                    description = "Native whole-window capture with no screen-pixel fallback. Needs a handle or Window ref.",
                },
                savePath = new
                {
                    type = "string",
                    description = "Absolute local .png file path to save the screenshot. UNC and device paths are rejected.",
                },
                overwrite = new
                {
                    type = "boolean",
                    description = "Allow savePath to replace an existing file (default: false)",
                },
                includeImage = new
                {
                    type = "boolean",
                    description = "Return image payload (default true). Set false with savePath for compact artifact-only output.",
                },
                includeMetadata = new
                {
                    type = "boolean",
                    description = "Append capture method, bounds, crop, size and DPI.",
                },
                locate = new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Element refs whose rectangles to measure in the returned image, reported as "
                        + "{ref, x, y, width, height, clipped} in image pixels. Use this instead of converting "
                        + "windows_find bounds yourself: those are screen coordinates from an earlier observation, they "
                        + "go stale when the layout moves, and the offset between the two is not constant across DPI "
                        + "states. Implies includeMetadata. Refused when the capture cannot be trusted to map 1:1.",
                },
                allowOccludedPixels = new
                {
                    type = "boolean",
                    description = "Capture screen pixels even when another application's window covers part of the "
                        + "target (default false). Only for deliberately capturing a partly covered window: the image "
                        + "will contain whatever is on top. strictNative is usually the better answer, since it renders "
                        + "the window's own surface and is unaffected by what is in front of it.",
                },
            },
        };

    public override Task<McpToolResult> ExecuteAsync(JsonElement? arguments)
    {
        var handle = GetStringArgument(arguments, "handle");
        var refId = GetStringArgument(arguments, "ref");
        if (refId != null)
        {
            try
            {
                _elementRegistry.ResolveRef(refId, handle);
            }
            catch (Exception ex)
                when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Task.FromResult(ErrorResult(ex.Message));
            }
        }

        var fullScreen = GetBoolArgument(arguments, "fullScreen", false);
        var background = GetBoolArgument(arguments, "background", false);
        var strictNative = GetBoolArgument(arguments, "strictNative", false);
        background |= strictNative;
        var savePath = GetStringArgument(arguments, "savePath");
        var overwrite = GetBoolArgument(arguments, "overwrite", false);
        var includeImage = GetBoolArgument(arguments, "includeImage", true);
        var frame =
            arguments is { } a && a.TryGetProperty("frame", out var f) && f.ValueKind != JsonValueKind.Null
                ? f.Deserialize<CaptureFrame>()
                : null;
        frame?.Validate();
        if (!includeImage && string.IsNullOrWhiteSpace(savePath))
        {
            return Task.FromResult(ErrorResult("includeImage=false requires savePath"));
        }

        // Everything about locate that can be known before a pixel is read is checked here, so a
        // malformed call does not perform a full capture and PNG encode only to throw it away.
        var locateRefs = GetArgument<string[]>(arguments, "locate") ?? [];
        if (locateRefs.Length > 0)
        {
            if (fullScreen)
            {
                return Task.FromResult(
                    ErrorResult(
                        "locate needs a known capture origin, which fullScreen does not record. "
                            + "Capture the window by handle or ref instead. No screenshot was taken."
                    )
                );
            }

            foreach (var reference in locateRefs.Distinct())
            {
                if (!_elementRegistry.HasElement(reference))
                {
                    return Task.FromResult(
                        ErrorResult($"locate ref {reference} is not a known element ref. No screenshot was taken.")
                    );
                }

                var refProcess = _elementRegistry.GetProcessIdForRef(reference);
                if (!_processPolicy.IsProcessAllowed(refProcess))
                {
                    return Task.FromResult(ErrorResult(_processPolicy.DescribeDenied($"locate ref {reference}")));
                }
                // BoundingRectangle is a UIA read, which is exactly what hangs while a provider is
                // blocked — the state the screen-pixel fallback exists to survive.
                if (_invokeTracker.TryGetPending(refProcess, out var pendingRef))
                {
                    return Task.FromResult(
                        ErrorResult(
                            $"locate cannot measure {reference} while this app's UI Automation provider is blocked. "
                                + PendingInvokeTracker.DescribeBlocked(pendingRef)
                        )
                    );
                }
            }
        }

        if (!TryNormalizeSavePath(savePath, overwrite, out var normalizedSavePath, out var pathError))
        {
            return Task.FromResult(ErrorResult(pathError));
        }

        try
        {
            OperationContext.Check();
            CaptureImage capture;
            var method = "screen-uia-bounds";
            System.Drawing.Rectangle? sourceBounds = null;
            nint targetHwnd = 0;
            // Set only where a modal is in front of the target by construction, so the occlusion
            // guard would otherwise leave that path with no way to capture at all.
            var occlusionExempt = false;
            McpToolResult Finish(byte[] bytes)
            {
                var windowDpi = DpiUtility.WindowDpi(targetHwnd);
                var monitorDpi = DpiUtility.MonitorDpi(targetHwnd);
                var dpiWarning = DpiUtility.DescribeMismatch(windowDpi, monitorDpi);
                // Screen-pixel capture reads the glass, so anything layered over the target is in
                // the image. Native capture renders the window's own surface and is exempt; so is a
                // capture with no known target window, which is reported as unchecked rather than clean.
                Win32Desktop.OcclusionCheck? occlusion = null;
                var guardTarget = targetHwnd;
                // An element's NativeWindowHandle is 0 for most WPF controls, which left every
                // element capture on the screen-pixel path with no guard at all. The window under
                // the captured region is the target in that case.
                if (guardTarget == 0 && sourceBounds is { } region && ReadsScreenPixels(method))
                {
                    guardTarget = Win32Desktop.WindowAt(
                        new System.Drawing.Point(region.Left + region.Width / 2, region.Top + region.Height / 2)
                    );
                }

                if (guardTarget != 0 && sourceBounds is { } captured && ReadsScreenPixels(method))
                {
                    occlusion = Win32Desktop.FindObstructions(
                        Win32Desktop.ObserveZOrder(),
                        guardTarget,
                        Win32Desktop.GetProcessId(guardTarget),
                        captured
                    );
                }

                var verdict = Decide(
                    method,
                    occlusionExempt,
                    occlusion,
                    dpiWarning,
                    GetBoolArgument(arguments, "allowOccludedPixels"),
                    UncheckedReason(method, guardTarget, sourceBounds)
                );
                if (verdict.Refusal is { } refusal)
                {
                    return ErrorResult(refusal);
                }

                List<LocatedElement>? located = null;
                if (locateRefs.Length > 0)
                {
                    using var probe = new MemoryStream(bytes);
                    using var measured = System.Drawing.Image.FromStream(probe);
                    // A located rectangle only means anything if the image is a faithful 1:1 render
                    // of the region sourceBounds names.
                    if (sourceBounds is not { } origin)
                    {
                        return ErrorResult("locate needs a known capture origin, which this capture did not record.");
                    }

                    if (dpiWarning != null)
                    {
                        return ErrorResult(
                            "locate is unavailable while window and monitor DPI disagree: the "
                                + $"coordinates would be computed against a mis-scaled render. {dpiWarning}"
                        );
                    }

                    if (measured.Width != origin.Width || measured.Height != origin.Height)
                    {
                        return ErrorResult(
                            $"locate is unavailable: the capture is {measured.Width}x{measured.Height} "
                                + $"but its source region is {origin.Width}x{origin.Height}, so image pixels do not map "
                                + "1:1 to screen coordinates."
                        );
                    }

                    located = [];
                    foreach (var reference in locateRefs)
                    {
                        var refHwnd = _sessionManager.GetWindowHwnd(_elementRegistry.WindowForRef(reference));
                        // Both zero would compare equal and wave through an unrelated ref.
                        if (refHwnd == 0 || targetHwnd == 0 || refHwnd != targetHwnd)
                        {
                            return ErrorResult(
                                $"locate ref {reference} belongs to a different window than the "
                                    + "capture, so its coordinates would not be in this image."
                            );
                        }

                        if (
                            _elementRegistry.GetElement(reference) is not { } element
                            || !element.Properties.BoundingRectangle.TryGetValue(out var elementBounds)
                        )
                        {
                            return ErrorResult($"locate ref {reference} did not report bounds.");
                        }

                        located.Add(
                            LocateInCapture(reference, elementBounds, origin, measured.Width, measured.Height, frame)
                        );
                    }
                }

                var result = BuildScreenshotResult(bytes, normalizedSavePath, overwrite, includeImage, frame);
                if (result.IsError == true)
                {
                    return result;
                }
                // Warnings do not depend on includeMetadata: a warning the caller has to opt into is
                // not a warning, and the default call is the one that produced a mis-scaled capture.
                foreach (var warning in verdict.Warnings)
                {
                    result.Content.Add(new McpContent { Text = warning });
                }
                // locate output lives in the metadata block, and no caller wants the rectangles but
                // not the metadata, so asking for locate turns it on rather than being refused.
                if (!GetBoolArgument(arguments, "includeMetadata") && located == null)
                {
                    return result;
                }

                using var stream = new MemoryStream(bytes);
                using var bitmap = System.Drawing.Image.FromStream(stream);
                var metadata = new
                {
                    method,
                    sourceBounds = sourceBounds is { } b
                        ? new
                        {
                            x = b.X,
                            y = b.Y,
                            width = b.Width,
                            height = b.Height,
                        }
                        : null,
                    sourceWidth = bitmap.Width,
                    sourceHeight = bitmap.Height,
                    width = frame?.Width ?? bitmap.Width,
                    height = frame?.Height ?? bitmap.Height,
                    frame,
                    windowDpi,
                    monitorDpi,
                    // Flags, not prose: screenFallback:false read as reassurance and stopped nothing.
                    dpiMismatch = dpiWarning != null,
                    // Tri-state. Null means the capture could not be checked, which is not the same
                    // as checked and clean, and must not read as reassurance either.
                    occluded = occlusion is null ? (bool?)null : !occlusion.IsClean,
                    occlusionWarning = DescribeOcclusion(occlusion),
                    located,
                    screenFallback = method == "screen-fallback",
                };
                result.Content.Add(
                    new McpContent { Text = JsonSerializer.Serialize(metadata, McpProtocol.JsonOptions) }
                );
                return result;
            }

            if (background && (fullScreen || (string.IsNullOrEmpty(refId) && string.IsNullOrEmpty(handle))))
            {
                return Task.FromResult(
                    ErrorResult(
                        "background capture requires a window handle or Window ref and cannot be combined with fullScreen"
                    )
                );
            }

            if (fullScreen)
            {
                // A full-screen capture would include windows of apps outside
                // the allowlist, so it is disabled while one is active.
                if (_processPolicy.IsRestricted)
                {
                    return Task.FromResult(
                        ErrorResult(
                            "fullScreen capture is disabled while the app allowlist "
                                + $"({ProcessPolicy.EnvironmentVariable}) is active. Capture an allowed window by handle instead."
                        )
                    );
                }

                capture = Capture.Screen();
                method = "screen";
            }
            else if (!string.IsNullOrEmpty(refId))
            {
                if (!_processPolicy.IsProcessAllowed(_elementRegistry.GetProcessIdForRef(refId)))
                {
                    return Task.FromResult(ErrorResult(_processPolicy.DescribeDenied("Screenshot target")));
                }

                var element = _elementRegistry.GetElement(refId);
                if (element == null)
                {
                    return Task.FromResult(ErrorResult($"Element not found: {refId}"));
                }

                // Element capture needs the UIA bounding rectangle, which hangs while
                // the app's provider is blocked; suggest window-handle capture instead
                // (its Win32 fallback works while blocked, and fullScreen may be
                // unavailable when an app allowlist is active).
                if (_invokeTracker.TryGetPending(_elementRegistry.GetProcessIdForRef(refId), out var pendingRef))
                {
                    return Task.FromResult(
                        ErrorResult(
                            PendingInvokeTracker.DescribeBlocked(pendingRef)
                                + " For screenshots, use a window handle instead of a ref."
                        ) with
                        {
                            Outcome = BlockedResult(pendingRef).Outcome,
                        }
                    );
                }

                if (background)
                {
                    if (element.ControlType != FlaUI.Core.Definitions.ControlType.Window)
                    {
                        return Task.FromResult(ErrorResult("background ref capture requires a Window element"));
                    }

                    if (!NativeWindowCapture.TryCaptureWindow(element.AsWindow(), out var image, out var reason))
                    {
                        return Task.FromResult(ErrorResult($"Native Window ref capture failed: {reason}"));
                    }

                    targetHwnd = element.Properties.NativeWindowHandle.ValueOrDefault;
                    sourceBounds = Win32Desktop.GetWindowBounds(targetHwnd);
                    method = "native-window";
                    return Task.FromResult(Finish(image));
                }

                sourceBounds = element.BoundingRectangle;
                targetHwnd = _sessionManager.GetWindowHwnd(_elementRegistry.WindowForRef(refId) ?? "");
                capture = Capture.Element(element);
            }
            else if (!string.IsNullOrEmpty(handle))
            {
                targetHwnd = _sessionManager.GetWindowHwnd(handle);
                if (!_processPolicy.IsProcessAllowed(_sessionManager.GetWindowProcessId(handle)))
                {
                    return Task.FromResult(ErrorResult(_processPolicy.DescribeDenied("Screenshot target")));
                }
                // While the app's UIA provider is blocked (pending pattern call, e.g. an
                // open modal dialog), fall back to a pure Win32 capture of the window
                // bounds so screenshots keep working.
                if (_invokeTracker.TryGetPending(_sessionManager.GetWindowProcessId(handle), out _))
                {
                    if (strictNative)
                    {
                        return Task.FromResult(
                            ErrorResult(
                                "Native capture is unavailable while this provider is blocked. Screen-pixel fallback is disabled; no screenshot was taken."
                            )
                        );
                    }

                    var hwnd = _sessionManager.GetWindowHwnd(handle);
                    var bounds = hwnd != 0 ? Win32Desktop.GetWindowBounds(hwnd) : null;
                    if (bounds == null)
                    {
                        return Task.FromResult(
                            ErrorResult(
                                "This app's UI Automation provider is blocked and its window bounds are unknown. "
                                    + "Use windows_list_windows to find the window (or the open dialog) and capture it "
                                    + "by that handle instead."
                            )
                        );
                    }

                    capture = Capture.Rectangle(bounds.Value);
                    sourceBounds = bounds;
                    method = "screen-fallback";
                    // The provider is blocked because a modal is up, so the modal is in front of the
                    // target by construction and strictNative was already refused above. Refusing for
                    // occlusion here would leave this path with no way to capture anything.
                    occlusionExempt = true;
                }
                else
                {
                    // A provider can stop answering after a dialog opens even with no tracked
                    // call; the cached HWND paths below never touch UI Automation.
                    Window? window = null;
                    try
                    {
                        window = _sessionManager.GetWindow(handle);
                        if (window == null)
                        {
                            return Task.FromResult(ErrorResult($"Window not found: {handle}"));
                        }
                    }
                    catch (TimeoutException) { }

                    if (background)
                    {
                        if (NativeWindowCapture.TryCaptureHwnd(targetHwnd, out var backgroundImage, out var reason))
                        {
                            sourceBounds = Win32Desktop.GetWindowBounds(targetHwnd);
                            method = "native-window";
                            return Task.FromResult(Finish(backgroundImage));
                        }

                        if (strictNative)
                        {
                            return Task.FromResult(
                                ErrorResult(
                                    $"Native capture failed: {reason}. Screen-pixel fallback is disabled; no screenshot was taken."
                                )
                            );
                        }
                    }

                    CaptureImage? elementCapture = null;
                    try
                    {
                        if (window != null)
                        {
                            sourceBounds = window.BoundingRectangle;
                            elementCapture = Capture.Element(window);
                            if (background)
                            {
                                method = "screen-fallback";
                            }
                        }
                    }
                    catch (TimeoutException) { }

                    if (elementCapture == null)
                    {
                        var bounds = targetHwnd != 0 ? Win32Desktop.GetWindowBounds(targetHwnd) : null;
                        if (bounds == null)
                        {
                            return Task.FromResult(
                                ErrorResult(
                                    "UI Automation timed out for this window and its bounds are unknown. Use windows_list_windows for a current handle."
                                )
                            );
                        }

                        elementCapture = Capture.Rectangle(bounds.Value);
                        sourceBounds = bounds;
                        method = "screen-fallback";
                    }

                    capture = elementCapture;
                }
            }
            else
            {
                // Resolve the foreground window through Win32 and authorize its process before
                // reading its provider; a focused-element walk can hang on unrelated apps.
                var foreground = Win32Desktop.GetForegroundWindow();
                if (foreground == 0)
                {
                    return Task.FromResult(ErrorResult("No foreground window found"));
                }

                Window? foregroundWindow;
                try
                {
                    foregroundWindow = CaptureForeground(
                        Win32Desktop.GetProcessId(foreground),
                        _processPolicy,
                        () => _sessionManager.Automation.FromHandle(foreground)?.AsWindow()
                    );
                }
                catch (UnauthorizedAccessException ex)
                {
                    return Task.FromResult(ErrorResult(ex.Message));
                }

                if (foregroundWindow == null)
                {
                    return Task.FromResult(ErrorResult("Could not read the foreground window"));
                }

                capture = Capture.Element(foregroundWindow);
                sourceBounds = foregroundWindow.BoundingRectangle;
                targetHwnd = foreground;
            }

            byte[] imageData;
            using (capture)
            {
                using var stream = new MemoryStream();
                capture.Bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                imageData = stream.ToArray();
            }

            return Task.FromResult(Finish(imageData));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ErrorResult($"Failed to capture screenshot: {ex.Message}"));
        }
    }

    /// <summary>An element's rectangle as measured in the image that was returned.</summary>
    /// <param name="Ref">The element ref this describes.</param>
    /// <param name="X">Left edge in image pixels; negative when the element starts outside the crop.</param>
    /// <param name="Y">Top edge in image pixels.</param>
    /// <param name="Width">Width in image pixels.</param>
    /// <param name="Height">Height in image pixels.</param>
    /// <param name="Clipped">Whether any part falls outside the returned image.</param>
    internal sealed record LocatedElement(string Ref, int X, int Y, int Width, int Height, bool Clipped);

    /// <summary>
    /// Convert a screen-space element rectangle into the coordinate space of the image actually
    /// returned, so annotation coordinates come from the same observation as the pixels.
    /// Deliberately never clamps: an element outside the crop is reported as outside rather than
    /// moved to the edge, because a plausible-looking wrong rectangle is what put annotation boxes
    /// on the wrong control. The title-bar offset this removes is not a constant — it
    /// was 39px in one DPI state and 55px in another on the same window (30).
    /// </summary>
    /// <param name="reference">The element ref being described.</param>
    /// <param name="element">The element's screen rectangle.</param>
    /// <param name="sourceBounds">Screen rectangle the capture covers; its origin is image pixel (0,0).</param>
    /// <param name="imageWidth">Width of the captured bitmap, before any crop.</param>
    /// <param name="imageHeight">Height of the captured bitmap, before any crop.</param>
    /// <param name="frame">The crop applied to the capture, if any.</param>
    /// <returns>The element's rectangle in the returned image.</returns>
    internal static LocatedElement LocateInCapture(
        string reference,
        System.Drawing.Rectangle element,
        System.Drawing.Rectangle sourceBounds,
        int imageWidth,
        int imageHeight,
        CaptureFrame? frame
    )
    {
        // Screen -> capture, then capture -> returned image when a crop was applied.
        var x = element.X - sourceBounds.X - (frame?.X ?? 0);
        var y = element.Y - sourceBounds.Y - (frame?.Y ?? 0);
        var visible = new System.Drawing.Rectangle(0, 0, frame?.Width ?? imageWidth, frame?.Height ?? imageHeight);
        var located = new System.Drawing.Rectangle(x, y, element.Width, element.Height);
        return new(reference, x, y, element.Width, element.Height, !visible.Contains(located));
    }

    /// <summary>Whether a capture method reads pixels off the screen rather than the window's own surface.</summary>
    /// <param name="method">The capture method that produced the image.</param>
    /// <returns>True when anything layered over the target would be in the image.</returns>
    internal static bool ReadsScreenPixels(string method) => method is "screen-uia-bounds" or "screen-fallback";

    /// <summary>
    /// Say why a screen-pixel capture could not be checked for obstructions, so "unchecked" is
    /// never reported as "clean". Native capture renders the window's own surface and needs no
    /// check, so it has no reason to state.
    /// </summary>
    /// <param name="method">How the pixels were obtained.</param>
    /// <param name="targetHwnd">The window the guard would bound the z-order by, or 0.</param>
    /// <param name="bounds">The screen rectangle read, or null when none was recorded.</param>
    /// <returns>A warning, or null when the capture was checkable or needed no check.</returns>
    internal static string? UncheckedReason(string method, nint targetHwnd, System.Drawing.Rectangle? bounds)
    {
        if (!ReadsScreenPixels(method))
        {
            return null;
        }

        if (targetHwnd == 0)
        {
            return "This capture read screen pixels but no target window could be identified, so it "
                + "was not checked for windows covering it. Anything on top is in the image.";
        }

        if (bounds is null)
        {
            return "This capture read screen pixels but recorded no source rectangle, so it was not "
                + "checked for windows covering it. Anything on top is in the image.";
        }

        return null;
    }

    /// <summary>
    /// What to do about a capture, and what to tell the caller regardless.
    /// </summary>
    /// <param name="Refusal">Why no image may be returned, or null to proceed.</param>
    /// <param name="Warnings">Statements that must reach the caller even on success.</param>
    internal sealed record CaptureVerdict(string? Refusal, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Decide whether a capture can be trusted. Extracted from the capture path so the choices that
    /// matter are testable without a desktop: that a DPI mismatch warns and never refuses, that
    /// occlusion refuses unless the caller opted in or the path has no alternative, and that an
    /// unchecked capture is never reported as clean.
    /// </summary>
    /// <param name="method">The capture method that produced the image.</param>
    /// <param name="occlusionExempt">The path has a modal in front of the target by construction.</param>
    /// <param name="occlusion">The occlusion observation, or null when none was possible.</param>
    /// <param name="dpiWarning">A window/monitor DPI disagreement, or null.</param>
    /// <param name="allowOccludedPixels">The caller deliberately wants a partly covered window.</param>
    /// <returns>The refusal, if any, and every warning to surface.</returns>
    internal static CaptureVerdict Decide(
        string method,
        bool occlusionExempt,
        Win32Desktop.OcclusionCheck? occlusion,
        string? dpiWarning,
        bool allowOccludedPixels,
        string? uncheckedReason = null
    )
    {
        var warnings = new List<string>();
        // Unchecked is not clean. Skipping the guard used to produce a reply indistinguishable
        // from a verified one, which is the failure the guard exists to remove.
        if (uncheckedReason != null)
        {
            warnings.Add(uncheckedReason);
        }
        // DPI mismatch never refuses: a screen-pixel capture in that state returns literally what is
        // on the glass, which for photography is the correct result, and it is permanent for
        // DPI-unaware apps where no remedy exists.
        if (dpiWarning != null)
        {
            warnings.Add(dpiWarning);
        }

        if (occlusion is null || occlusion.IsClean)
        {
            return new(null, warnings);
        }

        var finding = DescribeOcclusion(occlusion)!;
        // The blocked-provider fallback exists precisely because a modal is up; refusing there would
        // leave it no way to capture, and strictNative is already unavailable on that path.
        if (occlusionExempt || allowOccludedPixels || !ReadsScreenPixels(method))
        {
            warnings.Add(finding);
            return new(null, warnings);
        }

        return new(
            finding
                + " No screenshot was returned. Use strictNative:true to capture the window's "
                + "own surface instead (unaffected by what is on top), move the covering window, or pass "
                + "allowOccludedPixels:true to capture the screen anyway.",
            warnings
        );
    }

    /// <summary>
    /// State what was covering a capture, with no claim about what was returned: the same finding is
    /// attached both to a refusal and to an image the caller asked for anyway.
    /// </summary>
    /// <param name="occlusion">The observation to describe, or null.</param>
    /// <returns>The finding, or null when the capture was clean or unchecked.</returns>
    internal static string? DescribeOcclusion(Win32Desktop.OcclusionCheck? occlusion)
    {
        if (occlusion is null || occlusion.IsClean)
        {
            return null;
        }

        if (!occlusion.TargetFound)
        {
            return "The capture target was not found in the desktop window order, so it could not be "
                + "shown to be unobstructed.";
        }

        var named = occlusion.Obstructions.Select(w =>
            $"\"{(string.IsNullOrEmpty(w.Title) ? "untitled" : w.Title)}\" (HWND {w.Hwnd}, PID {w.ProcessId})"
        );
        return "Another application's window covers part of the capture area, so these pixels are not "
            + $"the target's: {string.Join("; ", named)}.";
    }

    internal static T CaptureForeground<T>(int processId, ProcessPolicy policy, Func<T> capture)
    {
        if (!policy.IsProcessAllowed(processId))
        {
            var name = ProcessPolicy.TryGetProcessName(processId) ?? "unknown";
            throw new UnauthorizedAccessException(policy.DescribeDenied($"The foreground window's process '{name}'"));
        }

        return capture();
    }

    internal static bool TryNormalizeSavePath(
        string? savePath,
        bool overwrite,
        out string? normalizedPath,
        out string error
    )
    {
        normalizedPath = null;
        error = "";

        if (string.IsNullOrWhiteSpace(savePath))
        {
            return true;
        }

        if (!Path.IsPathFullyQualified(savePath))
        {
            error = $"savePath must be an absolute local path: {savePath}";
            return false;
        }

        if (savePath.StartsWith(@"\\") || savePath.StartsWith(@"\\?\") || savePath.StartsWith(@"\\.\"))
        {
            error = "savePath must be a local drive path; UNC and device paths are not allowed";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(savePath);
        }
        catch (Exception ex)
        {
            error = $"savePath is invalid: {ex.Message}";
            return false;
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            error = "savePath must end with .png";
            return false;
        }

        if (File.Exists(fullPath) && !overwrite)
        {
            error = $"savePath already exists; pass overwrite=true to replace it: {fullPath}";
            return false;
        }

        normalizedPath = fullPath;
        return true;
    }

    internal static McpToolResult BuildScreenshotResult(
        byte[] imageData,
        string? savePath,
        bool overwrite,
        bool includeImage,
        CaptureFrame? frame = null
    )
    {
        OperationContext.Check();
        if (frame != null)
        {
            imageData = frame.Apply(imageData);
        }

        OperationContext.Check();
        if (string.IsNullOrEmpty(savePath))
        {
            return ImageResult(imageData, "image/png");
        }

        try
        {
            var directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = Path.Combine(
                directory ?? Directory.GetCurrentDirectory(),
                $"{Path.GetFileName(savePath)}.{Guid.NewGuid():N}.tmp"
            );
            try
            {
                OperationContext.Check();
                File.WriteAllBytes(tempPath, imageData);
                OperationContext.Check();
                File.Move(tempPath, savePath, overwrite);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex)
        {
            return ErrorResult($"Failed to save screenshot to {savePath}: {ex.Message}");
        }

        if (!includeImage)
        {
            return frame == null
                ? TextResult(JsonSerializer.Serialize(new { path = savePath, bytes = imageData.Length }))
                : TextResult(
                    JsonSerializer.Serialize(
                        new
                        {
                            path = savePath,
                            bytes = imageData.Length,
                            frame,
                        }
                    )
                );
        }

        return new McpToolResult
        {
            Content = new List<McpContent>
            {
                new() { Type = "text", Text = $"Screenshot saved to {savePath}" },
                new()
                {
                    Type = "image",
                    Data = Convert.ToBase64String(imageData),
                    MimeType = "image/png",
                },
            },
        };
    }
}
