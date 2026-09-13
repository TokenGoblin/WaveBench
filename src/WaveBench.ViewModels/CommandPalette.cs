namespace WaveBench.ViewModels;

public enum CommandKind
{
    Navigate,
    EditField,
    Action,
    Library,

    /// <summary>A Concepts explainer (plan §8.9).</summary>
    Concept,

    /// <summary>A "Show me" sweep of one field (plan §8.9).</summary>
    ShowMe,
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

        // EVERY Design field, addressed by the same paths provenance and undo
        // use.
        //
        // It used to be the Simple-mode subset, which made the palette a
        // shortcut to the fields that were already on screen and no help at
        // all for the ones that are not — the opposite of what §8.11 asks
        // ("reaching every field") and of what a search is for. The path is
        // carried as an alias so both "bore" and "Engine.BoreMm" find it.
        foreach (var field in DesignCatalogue.Fields)
        {
            var tab = DesignWorkspace.Tabs.First(t => t.Tab == field.Tab).Title;
            commands.Add(new PaletteCommand(
                CommandKind.EditField, field.Label, $"Design → {tab}", Path: field.Path, Target: Workspace.Design)
            {
                Aliases = [field.Path],
            });
        }

        // The discovery path for the hidden Boost workspace (plan §8.3). It
        // carries the aspiration field's own path, so invoking it navigates to
        // the field that reveals the workspace rather than to a screen the
        // user then has to search.
        commands.Add(new PaletteCommand(
            CommandKind.Action, "Add forced induction", "Reveals the Boost workspace",
            Path: BoostCatalogue.AspirationPath, Target: Workspace.Design)
        {
            Aliases = ["turbo", "supercharger", "boost", "FI", "aspiration"],
        });

        // Once the model IS boosted, its own fields join the palette: §8.11
        // asks that Ctrl+K reach every field, and a field on a conditional
        // workspace is exactly the one a user cannot find by looking.
        if (shell.HasForcedInduction)
        {
            foreach (var field in BoostCatalogue.Fields)
            {
                commands.Add(new PaletteCommand(
                    CommandKind.EditField, field.Label, "Boost", Path: field.Path, Target: Workspace.Boost)
                {
                    Aliases = field.Aliases,
                });
            }
        }

        // The learn layer joins the search rather than living behind a "?"
        // nobody presses (plan §8.9, and §8.11's "global search across model,
        // results and library"). A concept is findable by what it explains as
        // well as by its name — somebody wondering about their runner length
        // types "runner", not "Helmholtz".
        foreach (var concept in ConceptLibrary.All)
        {
            commands.Add(new PaletteCommand(
                CommandKind.Concept, concept.Title, concept.Summary, Path: concept.Id)
            {
                Aliases = [.. concept.Fields.Select(f => FieldLocator.Find(f)?.Label ?? f), .. concept.Fields],
            });
        }

        // Design fields always; Boost fields only once the model is boosted.
        // The same rule the field entries above follow, and for the same
        // reason (§8.3): there is no wastegate to sweep on a naturally
        // aspirated engine, and offering one is a command that cannot work.
        IEnumerable<IEditableField> sweepable = shell.HasForcedInduction
            ? FieldLocator.All
            : DesignCatalogue.Fields;

        foreach (var field in sweepable.Where(ShowMe.Supports))
        {
            commands.Add(new PaletteCommand(
                CommandKind.ShowMe, $"Show me: {field.Label}",
                "Sweep this one field and plot what it does", Path: field.Path)
            {
                Aliases = ["sweep", "what does", field.Path],
            });
        }

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
        // Names, and prose, scored differently.
        //
        // Subsequence matching is what lets "FI" find "forced induction", and
        // it is only safe on SHORT strings. Run it over a sentence and almost
        // any query matches: the letters of "wastegate" appear in order
        // somewhere in most two-line explanations of anything, so adding the
        // Concepts summaries to the palette made a turbine explainer a hit for
        // "wastegate". Prose is searched by substring only.
        var names = new List<string> { command.Title };
        if (command.Path is not null)
        {
            names.Add(command.Path);
        }

        names.AddRange(command.Aliases);

        var best = 0;
        foreach (var hay in names)
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

        if (command.Subtitle is { } subtitle && subtitle.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            best = Math.Max(best, 45);
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
