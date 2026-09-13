using System.Globalization;
using WaveBench.Model;

namespace WaveBench.Optimize;

/// <summary>
/// One thing the optimiser is allowed to change (plan §9.1): a numeric model
/// field, its bounds, and — where the real world only sells certain sizes —
/// the discrete set it may take.
///
/// <b>Bounds are not a formality.</b> Plan §9.7: <i>"The optimiser will find
/// unphysical designs that exploit model weaknesses — a 3 m runner, a 2 mm
/// pipe, free scavenging. Constrain aggressively and always show the geometry,
/// not just the number."</i> A variable with careless bounds is the most
/// reliable way to get a confident answer nobody can build.
/// </summary>
/// <param name="Path">Model path, e.g. <c>IntakeRunner.LengthMm</c>.</param>
/// <param name="Minimum">Lower bound, in the unit the document stores.</param>
/// <param name="Maximum">Upper bound, same unit.</param>
public sealed record OptimisationVariable(string Path, double Minimum, double Maximum)
{
    /// <summary>Human label; falls back to the path.</summary>
    public string Label { get; init; } = "";

    /// <summary>
    /// A grid the value must land on, in model units — 5 mm on a length a
    /// fabricator cuts by tape measure, 1° on a cam centreline a sprocket can
    /// index to. Null means continuous.
    /// </summary>
    public double? Step { get; init; }

    /// <summary>
    /// The only values that exist, in model units — the tube sizes actually
    /// stocked, the turbine housings actually cast. Takes precedence over
    /// <see cref="Step"/>. Null means any value in range.
    ///
    /// This is what stops an optimiser returning a 41.7 mm primary and calling
    /// it an answer.
    /// </summary>
    public IReadOnlyList<double>? Choices { get; init; }

    public string Name => string.IsNullOrWhiteSpace(Label) ? Path : Label;

    public double Span => Maximum - Minimum;

    public bool IsDiscrete => Choices is { Count: > 0 } || Step is > 0;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            throw new InvalidDataException("A variable must name a model path.");
        }

        if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum) || Maximum <= Minimum)
        {
            throw new InvalidDataException(
                $"{Name}: bounds [{Minimum}, {Maximum}] are not an interval the optimiser can search.");
        }

        if (Choices is { Count: > 0 } choices)
        {
            if (choices.Any(c => c < Minimum || c > Maximum))
            {
                throw new InvalidDataException(
                    $"{Name}: every choice must lie within [{Minimum}, {Maximum}] — a choice outside the bounds "
                    + "is a value the search can reach but the constraints were never checked against.");
            }

            if (choices.Distinct().Count() != choices.Count)
            {
                throw new InvalidDataException($"{Name}: the choice list repeats a value.");
            }
        }

        if (Step is { } step && step <= 0)
        {
            throw new InvalidDataException($"{Name}: a discrete step must be positive.");
        }
    }

    /// <summary>
    /// Map a search coordinate in [0, 1] to a model value, snapped to the grid
    /// or the choice list.
    ///
    /// <b>Every optimiser here works in the unit cube</b>, which is what lets
    /// CMA-ES's single step size, NSGA-II's crowding distance and the Gaussian
    /// process's length scales mean the same thing across a 200 mm length and
    /// a 0.4 pressure ratio. Mixing raw units would make every one of them
    /// silently dominated by whichever variable has the biggest numbers.
    /// </summary>
    public double Denormalise(double u)
    {
        var clamped = Math.Clamp(u, 0.0, 1.0);

        if (Choices is { Count: > 0 } choices)
        {
            // Equal-width bins, so a uniform sample over the cube is a uniform
            // sample over the choices.
            var index = Math.Clamp((int)(clamped * choices.Count), 0, choices.Count - 1);
            return choices[index];
        }

        var value = Minimum + (clamped * Span);

        if (Step is { } step && step > 0)
        {
            value = Minimum + (Math.Round((value - Minimum) / step) * step);
            value = Math.Clamp(value, Minimum, Maximum);
        }

        return value;
    }

    /// <summary>The inverse, for seeding a search from an existing design.</summary>
    public double Normalise(double value)
    {
        if (Choices is { Count: > 0 } choices)
        {
            var nearest = 0;
            for (var i = 1; i < choices.Count; i++)
            {
                if (Math.Abs(choices[i] - value) < Math.Abs(choices[nearest] - value))
                {
                    nearest = i;
                }
            }

            // The centre of the bin, so a round trip through Denormalise
            // returns the same choice.
            return (nearest + 0.5) / choices.Count;
        }

        return Span > 0 ? Math.Clamp((value - Minimum) / Span, 0.0, 1.0) : 0.0;
    }
}

/// <summary>
/// One candidate design: a point in the unit cube, and what it means in model
/// units.
///
/// Held normalised rather than in model units because that is the form every
/// optimiser reasons in, and converting at the boundary — here — means a
/// snapped discrete value cannot drift between the search's idea of a design
/// and the document's.
/// </summary>
public sealed class DesignPoint
{
    private readonly double[] _coordinates;

    public DesignPoint(DesignSpace space, IReadOnlyList<double> coordinates)
    {
        Space = space ?? throw new ArgumentNullException(nameof(space));
        ArgumentNullException.ThrowIfNull(coordinates);

        if (coordinates.Count != space.Variables.Count)
        {
            throw new ArgumentException(
                $"This space has {space.Variables.Count} variables; got {coordinates.Count} coordinates.",
                nameof(coordinates));
        }

        _coordinates = coordinates.Select(c => Math.Clamp(c, 0.0, 1.0)).ToArray();
    }

    public DesignSpace Space { get; }

    /// <summary>The search coordinates, each in [0, 1].</summary>
    public IReadOnlyList<double> Coordinates => _coordinates;

    /// <summary>The values a document would hold, in model units, snapped.</summary>
    public IReadOnlyList<double> Values =>
        _coordinates.Select((u, i) => Space.Variables[i].Denormalise(u)).ToList();

    public double ValueOf(string path)
    {
        var index = Space.IndexOf(path);
        return index < 0
            ? throw new ArgumentException($"'{path}' is not a variable of this space.", nameof(path))
            : Space.Variables[index].Denormalise(_coordinates[index]);
    }

    /// <summary>
    /// A stable identity for the SNAPPED design, which is what the cache is
    /// keyed on.
    ///
    /// Two coordinates a hair apart in the cube can denormalise to the same
    /// tube size, and re-solving that design would be paying twice for one
    /// answer. Hashing the model values rather than the coordinates is what
    /// makes a discrete space cheap — plan §9.5's "repeated designs are free".
    /// </summary>
    public string Key()
    {
        var parts = Space.Variables.Select((v, i) =>
            $"{v.Path}={v.Denormalise(_coordinates[i]).ToString("R", CultureInfo.InvariantCulture)}");
        return string.Join("|", parts);
    }

    /// <summary>Write this design into a document, in place.</summary>
    public void ApplyTo(EngineModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        for (var i = 0; i < _coordinates.Length; i++)
        {
            var variable = Space.Variables[i];
            ModelPath.Set(document, variable.Path, variable.Denormalise(_coordinates[i]), createMissing: true);
        }
    }

    /// <summary>A deep copy of the baseline with this design written into it.</summary>
    public EngineModelDocument Materialise(EngineModelDocument baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        // Through the file format, because that is the only deep copy the
        // document type guarantees — and because a design that cannot survive
        // a save/load round trip is not a design anyone can keep.
        var copy = EngineModelDocument.Load(baseline.Save());
        ApplyTo(copy);
        return copy;
    }

    public override string ToString() => Key();
}

/// <summary>
/// The variables an optimisation may change, and nothing else (plan §9.1).
///
/// Everything not in here is held at the baseline, which is what makes a
/// result attributable: a design that differs from the baseline in a way the
/// space does not describe is a bug, not a discovery.
/// </summary>
public sealed class DesignSpace
{
    private readonly Dictionary<string, int> _index;

    public DesignSpace(IReadOnlyList<OptimisationVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        if (variables.Count == 0)
        {
            throw new ArgumentException("An optimisation needs at least one variable.", nameof(variables));
        }

        foreach (var variable in variables)
        {
            variable.Validate();
        }

        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < variables.Count; i++)
        {
            if (!_index.TryAdd(variables[i].Path, i))
            {
                throw new InvalidDataException(
                    $"'{variables[i].Path}' appears twice. One field cannot be optimised from two places — the "
                    + "two would disagree and the last write would win silently.");
            }
        }

        Variables = variables;
    }

    public IReadOnlyList<OptimisationVariable> Variables { get; }

    public int Dimension => Variables.Count;

    public int IndexOf(string path) => _index.TryGetValue(path, out var i) ? i : -1;

    /// <summary>The centre of the cube — where a search starts when nothing better is known.</summary>
    public DesignPoint Centre() => new(this, Enumerable.Repeat(0.5, Dimension).ToList());

    /// <summary>The design a document already holds, as a starting point.</summary>
    public DesignPoint From(EngineModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var coordinates = Variables.Select(v =>
        {
            var raw = ModelPath.GetOrDefault(document, v.Path);
            var value = raw is null ? v.Minimum + (0.5 * v.Span) : Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return v.Normalise(value);
        }).ToList();

        return new DesignPoint(this, coordinates);
    }

    public DesignPoint Point(params double[] coordinates) => new(this, coordinates);

    /// <summary>
    /// Check every variable actually addresses a writable numeric field on the
    /// document, before a search spends hours discovering it does not.
    /// </summary>
    public void ValidateAgainst(EngineModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var variable in Variables)
        {
            if (!ModelPath.Exists(document, variable.Path))
            {
                throw new InvalidDataException(
                    $"'{variable.Path}' is not a field of this model, so the optimiser cannot set it.");
            }

            var probe = variable.Denormalise(0.5);
            if (!ModelPath.CanWrite(document, variable.Path, probe, out var reason))
            {
                throw new InvalidDataException($"'{variable.Path}' cannot be written: {reason}");
            }
        }
    }
}
