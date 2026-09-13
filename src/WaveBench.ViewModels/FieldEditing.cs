using System.Globalization;
using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>
/// What values this field usually takes, and what it means to be outside that
/// (plan §8.9: <i>"'Why' on every field — one sentence, a typical range, and
/// the citation where a correlation is involved"</i>).
///
/// <b>Not the same as the plausibility bounds.</b> Those are the limits past
/// which a value is refused: bore accepts 20–200 mm because somebody really
/// might be modelling a model-aircraft engine or a ship's diesel. The TYPICAL
/// range is where almost every real answer lies, and its job is to tell a
/// beginner that 78 mm is ordinary and 140 mm means they have mistyped
/// something — which the hard bounds cannot, because both are legal.
/// </summary>
/// <param name="Minimum">Lower end of the usual range, in model units.</param>
/// <param name="Maximum">Upper end.</param>
/// <param name="Note">
/// What sits at each end, where that is worth saying — "road cars at the
/// bottom, race engines at the top".
/// </param>
public sealed record TypicalRange(double Minimum, double Maximum, string? Note = null)
{
    /// <summary><paramref name="value"/> is in MODEL units, as the bounds are.</summary>
    public bool Contains(double value) => value >= Minimum && value <= Maximum;

    /// <summary>Which end a value falls outside, or null when it is inside.</summary>
    public double? NearestEnd(double value) =>
        !double.IsFinite(value) || Contains(value) ? null
        : value < Minimum ? Minimum
        : Maximum;
}

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

    /// <summary>
    /// One sentence saying WHY this field exists and what moving it does —
    /// required on every field by the Phase 24 gate, and enforced by a test.
    /// </summary>
    string? Help { get; }

    /// <summary>
    /// Where the answer usually lands, for numeric fields. Null on toggles and
    /// choices, which have no range to be outside of.
    /// </summary>
    TypicalRange? Typical { get; }
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

    /// <summary>
    /// The model being edited. On the interface because a field row offers
    /// "Show me" (plan §8.9), and a sweep of one field needs the document the
    /// other fields are held at.
    /// </summary>
    EngineModelDocument Document { get; }

    /// <summary>Units and mode, for the same reason.</summary>
    UserPreferences Preferences { get; }

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

    /// <summary>
    /// "Typically 150–600 mm — short race intakes at the bottom", converted to
    /// whatever units the user is working in.
    ///
    /// Built here rather than stored on the field because the range is in
    /// model units and the sentence is not: a stored sentence would read
    /// "typically 150–600 mm" to somebody working in inches.
    /// </summary>
    public string? DescribeTypical(IEditableField field)
    {
        if (field.Typical is not { } typical)
        {
            return null;
        }

        var unit = DisplayUnit(field);
        var suffix = string.IsNullOrWhiteSpace(unit) ? "" : " " + unit;
        var text = $"Typically {Format(field, typical.Minimum)}–{Format(field, typical.Maximum)}{suffix}";

        return string.IsNullOrWhiteSpace(typical.Note) ? text + "." : $"{text} — {typical.Note}";
    }

    /// <summary>
    /// What to say about a value outside the usual range (plan §8.10:
    /// <i>"Implausible input detection with a WARNING, never a hard
    /// block"</i>). Null when the value is ordinary.
    ///
    /// This is deliberately separate from the rejection path in
    /// <see cref="Apply"/>. A rejection refuses the keystroke; this accepts it
    /// and says the value is unusual — which is the whole distinction the plan
    /// draws, and the reason the typical range is not just a tighter bound.
    /// </summary>
    public string? Unusual(IEditableField field, double modelValue)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.Typical is not { } typical || typical.NearestEnd(modelValue) is not { } nearest)
        {
            return null;
        }

        var unit = DisplayUnit(field);
        var suffix = string.IsNullOrWhiteSpace(unit) ? "" : " " + unit;
        var side = modelValue < typical.Minimum ? "below" : "above";

        // If the value and the bound it is outside print the same, say
        // nothing. Wall roughness is typical from 0.0015 mm and displays to
        // two decimals, so a value of 0 produced "0 mm is below the usual
        // 0–0.3 mm" — arithmetically correct and self-evidently absurd to
        // read. A warning the user cannot see in the number is not a warning.
        if (Format(field, modelValue) == Format(field, nearest))
        {
            return null;
        }

        return $"{Format(field, modelValue)}{suffix} is {side} the usual "
               + $"{Format(field, typical.Minimum)}–{Format(field, typical.Maximum)}{suffix} for "
               + $"{field.Label.ToLowerInvariant()} (nearest usual value {Format(field, nearest)}{suffix})."
               + (string.IsNullOrWhiteSpace(typical.Note) ? "" : $" {typical.Note}.");
    }

    /// <summary>The same check against whatever the document currently holds.</summary>
    public string? Unusual(IEditableField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.Typical is null || ModelPath.GetOrDefault(session.Document, field.Path) is not { } raw)
        {
            return null;
        }

        return Unusual(field, Convert.ToDouble(raw, CultureInfo.InvariantCulture));
    }

    /// <summary>The value as the UI should show it right now, with its badge.</summary>
    public FieldView View(IEditableField field) => new(
        field,
        Format(field, ModelPath.GetOrDefault(session.Document, field.Path)),
        DisplayUnit(field),
        session.Provenance[field.Path])
    {
        Typical = DescribeTypical(field),
        Unusual = Unusual(field),
    };

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
