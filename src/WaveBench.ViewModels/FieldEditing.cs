using System.Globalization;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>
/// The description of one editable document field, independent of which
/// workspace draws it.
///
/// Design owns the engine itself and Boost owns the forced-induction hardware
/// (plan §8.4), but they are the same KIND of thing: a model path, a label, a
/// unit family, a plausibility range. Stating that once is what lets both
/// screens share <see cref="FieldEditor"/> — and therefore share the single
/// unit-conversion boundary the conventions require, rather than growing a
/// second one that rounds differently.
/// </summary>
public interface IEditableField
{
    /// <summary>Model path, e.g. <c>ForcedInduction.TargetBoostKPa</c>.</summary>
    string Path { get; }

    string Label { get; }

    FieldKind Kind { get; }

    Quantity Quantity { get; }

    /// <summary>Unit the DOCUMENT stores, always the SI-ish one.</summary>
    string ModelUnit { get; }

    /// <summary>Whether Simple mode surfaces it.</summary>
    bool Simple { get; }

    double? Minimum { get; }

    double? Maximum { get; }

    IReadOnlyList<string>? Choices { get; }

    string? Help { get; }
}

/// <summary>
/// A workspace that edits document fields. The view's field-row renderer
/// takes this rather than a concrete workspace, so Design and Boost draw
/// their fields through one code path.
/// </summary>
public interface IFieldEditingSurface
{
    /// <summary>The most recent rejection per field, for the view to show inline.</summary>
    IReadOnlyDictionary<string, string> Rejections { get; }

    /// <summary>Apply a user edit from text typed into the field, in DISPLAY units.</summary>
    EditOutcome Edit(string path, string text);
}

/// <summary>
/// Parsing, unit conversion, plausibility checking and writing-through for an
/// editable field.
///
/// <b>This is the unit boundary.</b> The document always holds the unit its
/// property name declares; nothing upstream of here and nothing downstream of
/// here converts. Every edit goes out through <see cref="ProjectSession"/>, so
/// provenance stamping, protection and undo/redo come for free rather than
/// being re-implemented per screen.
/// </summary>
public sealed class FieldEditor(ProjectSession session, UserPreferences preferences)
{
    public UnitSystem Units => preferences.Units;

    public string DisplayUnit(IEditableField field) => field.Quantity switch
    {
        Quantity.Length => Units == UnitSystem.Imperial ? "in" : "mm",
        Quantity.Pressure => Units == UnitSystem.Imperial ? "psi" : "kPa",
        Quantity.Temperature => Units == UnitSystem.Imperial ? "°F" : "°C",
        Quantity.Angle => "°",
        _ => field.ModelUnit,
    };

    /// <summary>Model units → display units.</summary>
    public double ToDisplay(IEditableField field, double modelValue) => field.Quantity switch
    {
        Quantity.Length => Units == UnitSystem.Imperial ? modelValue / 25.4 : modelValue,
        Quantity.Pressure => Units == UnitSystem.Imperial ? modelValue * 0.145037737730209 : modelValue,
        Quantity.Temperature => Units == UnitSystem.Imperial
            ? ((modelValue - 273.15) * 9.0 / 5.0) + 32.0
            : modelValue - 273.15,
        _ => modelValue,
    };

    /// <summary>Display units → model units. Exactly inverts <see cref="ToDisplay"/>.</summary>
    public double ToModel(IEditableField field, double displayValue) => field.Quantity switch
    {
        Quantity.Length => Units == UnitSystem.Imperial ? displayValue * 25.4 : displayValue,
        Quantity.Pressure => Units == UnitSystem.Imperial ? displayValue / 0.145037737730209 : displayValue,
        Quantity.Temperature => Units == UnitSystem.Imperial
            ? ((displayValue - 32.0) * 5.0 / 9.0) + 273.15
            : displayValue + 273.15,
        _ => displayValue,
    };

    /// <summary>
    /// Decimal places for a field, chosen by what the quantity needs rather
    /// than by how big the number happens to be — a magnitude rule prints
    /// "86.000" next to "107.0" in the same column, which reads as though the
    /// two were measured differently.
    /// </summary>
    public int Decimals(IEditableField field) => field.Kind == FieldKind.Integer ? 0 : field.Quantity switch
    {
        Quantity.Length => Units == UnitSystem.Imperial ? 3 : 2,
        Quantity.Pressure => Units == UnitSystem.Imperial ? 2 : 1,
        Quantity.Temperature => 1,
        Quantity.Angle => 1,
        _ => 3,
    };

    public string Format(IEditableField field, object? raw)
    {
        switch (raw)
        {
            case null:
                return "—";
            case bool b:
                return b ? "true" : "false";
            case string s:
                return s;
            case int i when field.Quantity == Quantity.None:
                return i.ToString(CultureInfo.InvariantCulture);
        }

        return Format(field, Convert.ToDouble(raw, CultureInfo.InvariantCulture));
    }

    public string Format(IEditableField field, double modelValue)
    {
        var shown = ToDisplay(field, modelValue);
        var text = shown.ToString("F" + Decimals(field), CultureInfo.InvariantCulture);

        // Trim the noise: "86.00" reads as a measurement to two decimals when
        // it is just 86. Keeps a real fractional part intact.
        if (text.Contains('.'))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text.Length == 0 || text == "-" ? "0" : text;
    }

    /// <summary>The value as the UI should show it right now, with its badge.</summary>
    public FieldView View(IEditableField field) => new(
        field,
        Format(field, ModelPath.GetOrDefault(session.Document, field.Path)),
        DisplayUnit(field),
        session.Provenance[field.Path]);

    /// <summary>
    /// Parse text typed in display units, convert, check, and write through
    /// the session. Rejection carries a reason the UI shows next to the field
    /// rather than a silently discarded keystroke.
    /// </summary>
    public EditOutcome Apply(IEditableField field, string text)
    {
        object? value;
        switch (field.Kind)
        {
            case FieldKind.Text:
                if (string.IsNullOrWhiteSpace(text))
                {
                    return EditOutcome.Reject("This cannot be empty.");
                }

                value = text.Trim();
                break;

            case FieldKind.Choice:
                var choices = field.Choices ?? [];
                var typedChoice = text.Trim();

                // Exact first, then a UNIQUE substring — the fuel library is
                // resolved by containment everywhere else in the codebase, so
                // "E85" has to reach "Ethanol E85" here or the field rejects
                // the very value the templates ship with. Ambiguous
                // abbreviations are refused rather than guessed at.
                var match = choices.FirstOrDefault(c => string.Equals(c, typedChoice, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    var partial = choices
                        .Where(c => c.Contains(typedChoice, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    match = partial.Count == 1 ? partial[0] : null;
                    if (match is null && partial.Count > 1)
                    {
                        return EditOutcome.Reject($"'{typedChoice}' matches {string.Join(", ", partial)} — be more specific.");
                    }
                }

                if (match is null)
                {
                    return EditOutcome.Reject($"Must be one of: {string.Join(", ", choices)}.");
                }

                // Store the canonical name so the document is unambiguous even
                // though a substring was typed.
                value = match;
                break;

            case FieldKind.Toggle:
                if (!bool.TryParse(text.Trim(), out var flag))
                {
                    return EditOutcome.Reject("Must be true or false.");
                }

                value = flag;
                break;

            case FieldKind.Integer:
            case FieldKind.Number:
            default:
                if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var typed))
                {
                    return EditOutcome.Reject("Not a number.");
                }

                var model = ToModel(field, typed);
                if (field.Minimum is { } min && model < min)
                {
                    return EditOutcome.Reject($"Below the plausible minimum of {Format(field, min)} {DisplayUnit(field)}.");
                }

                if (field.Maximum is { } max && model > max)
                {
                    return EditOutcome.Reject($"Above the plausible maximum of {Format(field, max)} {DisplayUnit(field)}.");
                }

                value = field.Kind == FieldKind.Integer ? (int)Math.Round(model) : model;
                break;
        }

        if (!ModelPath.CanWrite(session.Document, field.Path, value, out var reason))
        {
            return EditOutcome.Reject(reason ?? "This value cannot be written.");
        }

        session.EditByUser(field.Path, value);
        return EditOutcome.Ok;
    }
}
