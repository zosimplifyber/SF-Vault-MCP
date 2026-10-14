using System.Globalization;

namespace SFVaultBridge.Core;

/// <summary>Validation and clean-up rules for the Settings window, kept free of UI so they can be tested.</summary>
public static class SettingsRules
{
    /// <summary>Parses a percentage (0 to 100, either decimal mark) into the stored fraction. Error text is shown to the user.</summary>
    public static bool TryParsePercent(string? text, out double fraction, out string error)
    {
        fraction = 0; error = "";
        var t = (text ?? "").Trim().TrimEnd('%').Trim();
        if (t.Length == 0) { error = "Enter the sales tax rate as a percentage, for example 7."; return false; }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var p)
            && !double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out p))
        { error = "The sales tax rate must be a number, for example 7 or 8.25."; return false; }
        if (!double.IsFinite(p) || p < 0 || p > 100) { error = "The sales tax rate must be between 0 and 100 percent."; return false; }
        fraction = Math.Round(p / 100.0, 8);
        return true;
    }

    /// <summary>The stored fraction as text for the box (0.0825 gives "8.25").</summary>
    public static string PercentText(double fraction) =>
        Math.Round(fraction * 100.0, 6).ToString("0.######", CultureInfo.CurrentCulture);

    /// <summary>Trimmed, blanks dropped, case-insensitive duplicates dropped (first spelling and order kept).</summary>
    public static List<string> CleanList(IEnumerable<string?> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in items)
        {
            var t = (raw ?? "").Trim();
            if (t.Length > 0 && seen.Add(t)) result.Add(t);
        }
        return result;
    }

    /// <summary>Null when the list (after cleaning) has at least one value; otherwise the message for the named list.</summary>
    public static string? ListError(string listName, IEnumerable<string?> items) =>
        CleanList(items).Count == 0 ? $"{listName} needs at least one value." : null;

    /// <summary>Adds a value to a list: false with a reason when it is blank or already there (any case).</summary>
    public static bool TryAdd(IList<string> list, string? value, out string error)
    {
        error = "";
        var t = (value ?? "").Trim();
        if (t.Length == 0) { error = "Type a value first."; return false; }
        if (list.Any(x => string.Equals(x.Trim(), t, StringComparison.OrdinalIgnoreCase))) { error = $"\"{t}\" is already in the list."; return false; }
        list.Add(t);
        return true;
    }

    /// <summary>Moves the item at index by delta (-1 up, +1 down); returns the new index, or the old one when it cannot move.</summary>
    public static int Move<T>(IList<T> list, int index, int delta)
    {
        var to = index + delta;
        if (index < 0 || index >= list.Count || to < 0 || to >= list.Count) return index;
        (list[index], list[to]) = (list[to], list[index]);
        return to;
    }

    /// <summary>Override key names: trimmed, known team keys only, no duplicates, in team key order.</summary>
    public static List<string> CleanOverrides(IEnumerable<string?>? names)
    {
        var set = new HashSet<string>((names ?? Array.Empty<string?>()).Select(n => (n ?? "").Trim()), StringComparer.Ordinal);
        return SettingsMerge.TeamKeys.Where(set.Contains).ToList();
    }
}

/// <summary>Where the Claude Desktop extension (.mcpb) can be found on this PC.</summary>
public static class ClaudeExtension
{
    public const string FileFilter = "*.mcpb";

    /// <summary>
    /// The newest .mcpb in the install folders (top level of each, in order), else in downloadsDir or one folder below it.
    /// Null when there is none. File is the .mcpb path; Folder is where it sits.
    /// </summary>
    public static (string Folder, string File)? Find(IEnumerable<string> installDirs, string downloadsDir)
    {
        foreach (var dir in installDirs)
            if (Newest(dir, SearchOption.TopDirectoryOnly) is { } hit) return (Path.GetDirectoryName(hit)!, hit);
        if (Newest(downloadsDir, SearchOption.AllDirectories, 1) is { } found) return (Path.GetDirectoryName(found)!, found);
        return null;
    }

    private static string? Newest(string dir, SearchOption option, int depth = 0)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            var folders = new List<string> { dir };
            if (option == SearchOption.AllDirectories && depth > 0) folders.AddRange(Directory.GetDirectories(dir));
            return folders.SelectMany(f => Directory.GetFiles(f, FileFilter))
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The text "Copy setup info" puts on the clipboard. Names the token file; never contains the token.</summary>
    public static string SetupInfo(string endpoint, string tokenFile) =>
        "SF Vault Bridge MCP endpoint: " + endpoint + Environment.NewLine +
        "Token file: " + tokenFile + Environment.NewLine +
        "The Claude extension reads the token file itself. For another MCP client, send the token as X-Bridge-Token or Authorization: Bearer.";
}
