using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CICDPipelineGenerator.Domain;

/// <summary>
/// A small YAML tree. The generators build pipelines as maps and lists and the writer decides the quoting,
/// so a value such as "on", "1.0" or "a: b" never changes meaning.
/// </summary>
public abstract class YNode
{
    public static implicit operator YNode?(string? text) => text is null ? null : new YScalar(text, Raw: false);

    public static implicit operator YNode(bool value) => new YScalar(value ? "true" : "false", Raw: true);

    public static implicit operator YNode(int value) => new YScalar(value.ToString(CultureInfo.InvariantCulture), Raw: true);
}

/// <summary>A value. Raw values (booleans and numbers) are written as they are; text is quoted when YAML would read it differently.</summary>
public sealed class YScalar(string text, bool Raw) : YNode
{
    public string Text { get; } = text;
    public bool IsRaw { get; } = Raw;
}

/// <summary>Keys keep the order they were added in; adding a null value adds nothing, so optional settings read naturally.</summary>
public sealed class YMap : YNode, IEnumerable<KeyValuePair<string, YNode>>
{
    private readonly List<KeyValuePair<string, YNode>> entries = [];

    public int Count => entries.Count;

    public YNode? this[string key] => entries.FirstOrDefault(entry => entry.Key == key).Value;

    public void Add(string key, YNode? value)
    {
        if (value is not null)
        {
            entries.Add(new(key, value));
        }
    }

    public IEnumerator<KeyValuePair<string, YNode>> GetEnumerator() => entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class YSeq : YNode, IEnumerable<YNode>
{
    private readonly List<YNode> items = [];

    public YSeq()
    {
    }

    public YSeq(IEnumerable<string> texts)
    {
        foreach (var text in texts)
        {
            Add(text);
        }
    }

    public int Count => items.Count;

    public void Add(YNode? item)
    {
        if (item is not null)
        {
            items.Add(item);
        }
    }

    public void Insert(int index, YNode item) => items.Insert(index, item);

    public IEnumerator<YNode> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static partial class YamlWriter
{
    public static string Write(YMap root, IEnumerable<string>? header = null)
    {
        var text = new StringBuilder();
        foreach (var line in header ?? [])
        {
            text.Append(line.Length == 0 ? "#" : "# " + line).Append('\n');
        }

        WriteMap(text, root, 0, dashColumn: null, topLevelGaps: true);
        return text.ToString();
    }

    private static void WriteMap(StringBuilder text, YMap map, int indent, int? dashColumn, bool topLevelGaps = false)
    {
        var first = true;
        foreach (var (key, value) in map)
        {
            if (topLevelGaps && !first && value is YMap or YSeq)
            {
                text.Append('\n');
            }

            text.Append(first && dashColumn is int dash ? new string(' ', dash) + "- " : new string(' ', indent));
            text.Append(Key(key)).Append(':');
            WriteAfterKey(text, value, indent);
            first = false;
        }
    }

    private static void WriteAfterKey(StringBuilder text, YNode value, int indent)
    {
        switch (value)
        {
            case YScalar scalar when IsMultiline(scalar):
                WriteLiteral(text, scalar.Text, indent + 2);
                break;
            case YScalar scalar:
                text.Append(' ').Append(Scalar(scalar)).Append('\n');
                break;
            case YMap { Count: 0 }:
                text.Append(" {}\n");
                break;
            case YMap map:
                text.Append('\n');
                WriteMap(text, map, indent + 2, dashColumn: null);
                break;
            case YSeq { Count: 0 }:
                text.Append(" []\n");
                break;
            case YSeq seq:
                text.Append('\n');
                WriteSeq(text, seq, indent + 2);
                break;
        }
    }

    private static void WriteSeq(StringBuilder text, YSeq seq, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var item in seq)
        {
            switch (item)
            {
                case YMap { Count: > 0 } map:
                    WriteMap(text, map, indent + 2, dashColumn: indent);
                    break;
                case YMap:
                    text.Append(pad).Append("- {}\n");
                    break;
                case YSeq { Count: > 0 } inner:
                    text.Append(pad).Append("-\n");
                    WriteSeq(text, inner, indent + 2);
                    break;
                case YSeq:
                    text.Append(pad).Append("- []\n");
                    break;
                case YScalar scalar when IsMultiline(scalar):
                    text.Append(pad).Append('-');
                    WriteLiteral(text, scalar.Text, indent + 2);
                    break;
                case YScalar scalar:
                    text.Append(pad).Append("- ").Append(Scalar(scalar)).Append('\n');
                    break;
            }
        }
    }

    private static bool IsMultiline(YScalar scalar) => !scalar.IsRaw && scalar.Text.Contains('\n');

    private static void WriteLiteral(StringBuilder text, string value, int indent)
    {
        var lines = value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

        // A first line that starts with a space needs the indentation stated, or YAML would take it from that line.
        text.Append(lines[0].StartsWith(' ') ? " |2\n" : " |\n");
        var pad = new string(' ', indent);
        foreach (var line in lines)
        {
            text.Append(line.Length == 0 ? "" : pad + line.TrimEnd()).Append('\n');
        }
    }

    private static string Key(string key) => NeedsQuotes(key, isKey: true) ? Quote(key) : key;

    private static string Scalar(YScalar scalar) => scalar.IsRaw ? scalar.Text : NeedsQuotes(scalar.Text, isKey: false) ? Quote(scalar.Text) : scalar.Text;

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    public static bool NeedsQuotes(string text, bool isKey)
    {
        if (text.Length == 0 || text != text.Trim() || text.Contains('\t'))
        {
            return true;
        }

        if ("-?:,[]{}#&*!|>'\"%@`".Contains(text[0]))
        {
            return true;
        }

        if (text.Contains(": ") || text.Contains(" #") || text.EndsWith(':'))
        {
            return true;
        }

        // Keys such as "on" are written plainly on purpose: GitHub, Azure DevOps and GitLab read them as text.
        return !isKey && LooksLikeAnotherType().IsMatch(text);
    }

    // YAML 1.1 and 1.2 read these as booleans, null or numbers instead of text.
    [GeneratedRegex(@"^(?:y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF|null|Null|NULL|~|[-+]?(?:\d[\d_]*)?\.?\d[\d_]*(?:[eE][-+]?\d+)?|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN)|0x[0-9a-fA-F_]+|0o?[0-7_]+|[-+]?\d[\d_]*(?::[0-5]?\d)+(?:\.\d*)?)$")]
    private static partial Regex LooksLikeAnotherType();
}
