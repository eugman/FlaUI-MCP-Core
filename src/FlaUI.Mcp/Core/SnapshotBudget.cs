using System.Text;

namespace FlaUI.Mcp.Core;

/// <summary>Per-observation client-side limits; cannot interrupt a native provider call.</summary>
internal sealed class SnapshotBudget
{
    private readonly int _maxNodes;
    private readonly int _maxCharacters;
    private int _nodes;
    public bool Partial { get; set; }
    public bool Exhausted { get; private set; }

    public SnapshotBudget(int maxNodes, int maxCharacters)
    {
        if (maxNodes is < 1 or > 100000 || maxCharacters is < 256 or > 1000000)
        {
            throw new ArgumentException("maxNodes must be 1..100000 and maxCharacters 256..1000000");
        }

        _maxNodes = maxNodes;
        _maxCharacters = maxCharacters;
    }

    public bool Visit()
    {
        if (Exhausted)
        {
            return false;
        }

        if (_nodes++ < _maxNodes)
        {
            return true;
        }

        Partial = Exhausted = true;
        return false;
    }

    public bool Append(StringBuilder text, string line)
    {
        // Reserve space for the explicit partial-observation footer.
        if (text.Length + (long)line.Length + Environment.NewLine.Length <= _maxCharacters - 160)
        {
            text.AppendLine(line);
            return true;
        }

        Partial = Exhausted = true;
        return false;
    }
}
