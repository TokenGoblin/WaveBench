namespace WaveBench.ViewModels;

public enum CommandKind
{
    Navigate,
    EditField,
    Action,
    Library,
}

/// <summary>One command-palette entry (plan §8.11: Ctrl+K reaches every field, workspace, action and library item).</summary>
public sealed record PaletteCommand(
    CommandKind Kind, string Title, string? Subtitle = null, string? Path = null, Workspace? Target = null)
{
    /// <summary>Keywords the fuzzy match also considers, so "FI" finds "add forced induction".</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>
/// Command palette (plan §8.11). Also the discovery path for hidden
/// workspaces: "add forced induction" is reachable here even when the Boost
/// workspace is invisible, so a feature is never merely absent.
/// </summary>
public sealed class CommandPalette(ShellViewModel shell)
{
    public IReadOnlyList<PaletteCommand> AllCommands()
    {
        var commands = new List<PaletteCommand>();

        foreach (var workspace in shell.Workspaces)
        {
            if (workspace.Visible)
            {
                commands.Add(new PaletteCommand(
                    CommandKind.Navigate, $"Go to {workspace.Title}", string.Join(" · ", workspace.SubTabs),
                    Target: workspace.Workspace));
            }
        }

        // Fields, addressed by the same paths provenance and undo use.
        foreach (var path in ShellViewModel.SimpleModeFields.Order(StringComparer.Ordinal))
        {
            commands.Add(new PaletteCommand(CommandKind.EditField, path, "Edit field", Path: path));
        }

        commands.Add(new PaletteCommand(
            CommandKind.Action, "Add forced induction", "Reveals the Boost workspace", Target: Workspace.Design)
        {
            Aliases = ["turbo", "supercharger", "boost", "FI"],
        });
        commands.Add(new PaletteCommand(CommandKind.Action, "Run sweep", "Queue an rpm sweep", Target: Workspace.Run));
        commands.Add(new PaletteCommand(CommandKind.Action, "Render audio", "Auralise the current model", Target: Workspace.Sound));
        commands.Add(new PaletteCommand(CommandKind.Action, "Toggle units", "Metric ⇄ Imperial"));
        commands.Add(new PaletteCommand(CommandKind.Action, "Toggle mode", "Simple ⇄ Advanced"));
        commands.Add(new PaletteCommand(CommandKind.Library, "Fuels", "Library", Target: Workspace.Library));
        commands.Add(new PaletteCommand(CommandKind.Library, "Templates", "Library", Target: Workspace.Library));

        return commands;
    }

    /// <summary>Subsequence fuzzy match over title, subtitle, path and aliases; best matches first.</summary>
    public IReadOnlyList<PaletteCommand> Search(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return AllCommands().Take(limit).ToList();
        }

        return AllCommands()
            .Select(c => (Command: c, Score: Score(c, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Command.Title, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Command)
            .ToList();
    }

    private static int Score(PaletteCommand command, string query)
    {
        var haystacks = new List<string> { command.Title };
        if (command.Subtitle is not null)
        {
            haystacks.Add(command.Subtitle);
        }

        if (command.Path is not null)
        {
            haystacks.Add(command.Path);
        }

        haystacks.AddRange(command.Aliases);

        var best = 0;
        foreach (var hay in haystacks)
        {
            if (hay.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                best = Math.Max(best, hay.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 100 : 60);
            }
            else if (IsSubsequence(query, hay))
            {
                best = Math.Max(best, 30);
            }
        }

        return best;
    }

    private static bool IsSubsequence(string needle, string hay)
    {
        var i = 0;
        foreach (var c in hay)
        {
            if (i < needle.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(needle[i]))
            {
                i++;
            }
        }

        return i == needle.Length;
    }
}
