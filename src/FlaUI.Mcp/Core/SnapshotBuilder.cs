using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUI.Mcp.Core;

/// <summary>
/// Builds agent-friendly accessibility snapshots from UI Automation trees
/// </summary>
public class SnapshotBuilder
{
    private readonly ElementRegistry _elementRegistry;
    private readonly int _maxDepth;

    public SnapshotBuilder(ElementRegistry elementRegistry, int maxDepth = 10)
    {
        _elementRegistry = elementRegistry;
        _maxDepth = maxDepth;
    }

    public string BuildSnapshot(
        string windowHandle,
        AutomationElement root,
        int maxNodes = 3000,
        int maxCharacters = 120000
    )
    {
        var budget = new SnapshotBudget(maxNodes, maxCharacters);
        // Clear previous elements for this window
        var generation = _elementRegistry.BeginSnapshot(windowHandle);

        // Remember the owning process so tools can later detect a blocked
        // UIA provider for this window's refs without touching UIA.
        try
        {
            var processId = Read(() => root.Properties.ProcessId.ValueOrDefault);
            if (processId != 0)
            {
                _elementRegistry.SetWindowProcessId(windowHandle, processId);
                _elementRegistry.SetWindowIdentity(
                    windowHandle,
                    processId,
                    Read(() => root.Properties.NativeWindowHandle.ValueOrDefault),
                    generation
                );
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Process id is best-effort; snapshot still works without it
        }

        var sb = new StringBuilder();
        BuildElementSnapshot(sb, windowHandle, root, 0, generation, budget);
        _elementRegistry.CheckGeneration(windowHandle, generation);
        if (budget.Partial)
        {
            sb.AppendLine(
                "[partial snapshot: traversal/output limit or unreadable children; use scoped windows_find. Missing controls are not proven absent.]"
            );
        }

        return sb.ToString();
    }

    private void BuildElementSnapshot(
        StringBuilder sb,
        string windowHandle,
        AutomationElement element,
        int depth,
        long generation,
        SnapshotBudget budget
    )
    {
        _elementRegistry.CheckGeneration(windowHandle, generation);
        if (depth > _maxDepth)
        {
            budget.Partial = true;
            return;
        }

        if (!budget.Visit())
        {
            return;
        }

        // Skip elements with no meaningful content
        var name = GetElementName(element);
        var role = GetElementRole(element);

        // Skip some noise elements, but keep elements with names or important roles
        if (ShouldSkipElement(element, name, role))
        {
            // A decorative node may still contain accessible controls.
            try
            {
                foreach (var child in Read(element.FindAllChildren))
                {
                    if (budget.Exhausted)
                    {
                        break;
                    }

                    BuildElementSnapshot(sb, windowHandle, child, depth + 1, generation, budget);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                budget.Partial = true;
            }

            return;
        }

        // Register element and get ref
        var refId = _elementRegistry.Register(windowHandle, element, generation);

        // Build the line
        var indent = new string(' ', depth * 2);
        var line = BuildElementLine(element, refId, name, role);
        if (!budget.Append(sb, $"{indent}- {line}"))
        {
            return;
        }

        // Process children
        try
        {
            var children = Read(element.FindAllChildren);
            foreach (var child in children)
            {
                if (budget.Exhausted)
                {
                    break;
                }

                BuildElementSnapshot(sb, windowHandle, child, depth + 1, generation, budget);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            budget.Partial = true;
        }
    }

    private string BuildElementLine(AutomationElement element, string refId, string? name, string role)
    {
        var parts = new List<string>();

        // Role first
        parts.Add(role);

        // Name in quotes if present
        if (!string.IsNullOrEmpty(name))
        {
            parts.Add($"\"{EscapeName(name)}\"");
        }

        // Ref
        parts.Add($"[ref={refId}]");

        // Human-facing roles are lossy (Pane and Group both render as group).
        // Always expose the exact selector type alongside the role.
        try
        {
            parts.Add("[controlType=" + Read(() => element.Properties.ControlType.ValueOrDefault) + "]");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { }

        // State indicators
        var states = GetStateIndicators(element);
        if (states.Count > 0)
        {
            parts.AddRange(states.Select(s => $"[{s}]"));
        }

        return string.Join(" ", parts);
    }

    private string GetElementRole(AutomationElement element)
    {
        try
        {
            var controlType = Read(() => element.Properties.ControlType.ValueOrDefault);
            return controlType switch
            {
                ControlType.Button => "button",
                ControlType.Edit => "textbox",
                ControlType.Text => "text",
                ControlType.CheckBox => "checkbox",
                ControlType.RadioButton => "radio",
                ControlType.ComboBox => "combobox",
                ControlType.List => "list",
                ControlType.ListItem => "listitem",
                ControlType.Menu => "menu",
                ControlType.MenuItem => "menuitem",
                ControlType.MenuBar => "menubar",
                ControlType.Tree => "tree",
                ControlType.TreeItem => "treeitem",
                ControlType.Tab => "tablist",
                ControlType.TabItem => "tab",
                ControlType.Table => "table",
                ControlType.DataItem => "row",
                ControlType.Header => "header",
                ControlType.HeaderItem => "columnheader",
                ControlType.Slider => "slider",
                ControlType.Spinner => "spinbutton",
                ControlType.ProgressBar => "progressbar",
                ControlType.Hyperlink => "link",
                ControlType.Image => "image",
                ControlType.Pane => "group",
                ControlType.Group => "group",
                ControlType.Window => "window",
                ControlType.Document => "document",
                ControlType.ToolBar => "toolbar",
                ControlType.ToolTip => "tooltip",
                ControlType.ScrollBar => "scrollbar",
                ControlType.StatusBar => "status",
                ControlType.Separator => "separator",
                ControlType.Thumb => "thumb",
                ControlType.TitleBar => "titlebar",
                ControlType.DataGrid => "grid",
                ControlType.Custom => "custom",
                _ => "element",
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return "element";
        }
    }

    private string? GetElementName(AutomationElement element)
    {
        try
        {
            var name = Read(() => element.Properties.Name.ValueOrDefault);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            // Try automation ID as fallback for identification
            var automationId = Read(() => element.Properties.AutomationId.ValueOrDefault);
            if (!string.IsNullOrWhiteSpace(automationId) && automationId.Length < 50)
            {
                return $"[{automationId}]";
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private List<string> GetStateIndicators(AutomationElement element)
    {
        var states = new List<string>();

        try
        {
            if (!Read(() => element.Properties.IsEnabled.ValueOrDefault))
            {
                states.Add("disabled");
            }

            if (Read(() => element.Properties.IsOffscreen.ValueOrDefault))
            {
                states.Add("offscreen");
            }

            // Check for readonly (ValuePattern)
            if (Read(() => element.Patterns.Value.IsSupported))
            {
                var valuePattern = Read(() => element.Patterns.Value.Pattern);
                if (Read(() => valuePattern.IsReadOnly.ValueOrDefault))
                {
                    states.Add("readonly");
                }
            }

            // Check toggle state
            if (Read(() => element.Patterns.Toggle.IsSupported))
            {
                var toggleState = Read(() => element.Patterns.Toggle.Pattern.ToggleState.ValueOrDefault);
                if (toggleState == ToggleState.On)
                {
                    states.Add("checked");
                }
                else if (toggleState == ToggleState.Indeterminate)
                {
                    states.Add("indeterminate");
                }
            }

            // Check selection state
            if (Read(() => element.Patterns.SelectionItem.IsSupported))
            {
                if (Read(() => element.Patterns.SelectionItem.Pattern.IsSelected.ValueOrDefault))
                {
                    states.Add("selected");
                }
            }

            // Check expanded state
            if (Read(() => element.Patterns.ExpandCollapse.IsSupported))
            {
                var expandState = Read(() =>
                    element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.ValueOrDefault
                );
                if (expandState == ExpandCollapseState.Expanded)
                {
                    states.Add("expanded");
                }
                else if (expandState == ExpandCollapseState.Collapsed)
                {
                    states.Add("collapsed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore state query errors
        }

        return states;
    }

    private bool ShouldSkipElement(AutomationElement element, string? name, string role)
    {
        // Always include named elements
        if (!string.IsNullOrEmpty(name))
        {
            return false;
        }

        // Always include actionable element types
        if (
            role
            is "button"
                or "textbox"
                or "checkbox"
                or "radio"
                or "combobox"
                or "listitem"
                or "menuitem"
                or "tab"
                or "treeitem"
                or "link"
                or "slider"
        )
        {
            return false;
        }

        // Include structural elements that might contain others
        if (
            role
            is "window"
                or "group"
                or "list"
                or "tree"
                or "tablist"
                or "menu"
                or "menubar"
                or "toolbar"
                or "grid"
                or "table"
        )
        {
            return false;
        }

        // Skip decorative/structural elements without names
        if (role is "element" or "thumb" or "scrollbar" or "separator" or "titlebar")
        {
            return true;
        }

        return false;
    }

    internal static T Read<T>(Func<T> provider)
    {
        OperationContext.Check();
        var value = provider(); // Native calls cannot be interrupted safely.
        OperationContext.Check();
        return value;
    }

    private string EscapeName(string name)
    {
        return name.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}
