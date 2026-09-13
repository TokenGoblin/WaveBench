using System.Text.RegularExpressions;
using FluentAssertions;
using WaveBench.ViewModels;
using WaveBench.ViewModels.Reporting;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Core.Tests.ViewModels;

/// <summary>
/// The Phase 25 gate's second clause: <i>a new user goes from download to a
/// converged torque curve in under 15 minutes using only the docs.</i>
///
/// The time cannot be measured here, but the failure mode can: a quick start
/// that names a command the tool does not have, an example file that is not in
/// the repository, or a documented path that quietly stopped working. Those
/// are what turn fifteen minutes into an afternoon, and they are exactly what
/// rots when nothing checks them.
/// </summary>
public class DocumentationGateTests(ITestOutputHelper output)
{
    private static DirectoryInfo Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WaveBench.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests must be able to find the repository root");
        return directory!;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(Repository().FullName, Path.Combine(parts)));

    [Fact]
    public void Gate_the_documents_the_plan_requires_all_exist()
    {
        string[] required =
        [
            "README.md",
            Path.Combine("docs", "user-guide.md"),
            Path.Combine("docs", "citations.md"),
            Path.Combine("docs", "physics.md"),
            Path.Combine("docs", "acoustics.md"),
            Path.Combine("docs", "numerics.md"),
            Path.Combine("packaging", "README.md"),
            Path.Combine("packaging", "AppxManifest.xml"),
            Path.Combine("packaging", "WaveBench.wxs"),
            Path.Combine("packaging", "Package.ps1"),
        ];

        foreach (var path in required)
        {
            var file = new FileInfo(Path.Combine(Repository().FullName, path));
            output.WriteLine($"{path,-28} {(file.Exists ? $"{file.Length:N0} bytes" : "MISSING")}");
            file.Exists.Should().BeTrue($"{path} is part of the Phase 25 deliverable");
        }
    }

    /// <summary>
    /// Every <c>wavebench …</c> line in the docs names a command the CLI
    /// actually has. A quick start that sends a new user to a command that
    /// does not exist is the fastest way to lose the fifteen minutes.
    /// </summary>
    [Fact]
    public void Gate_every_documented_cli_command_exists()
    {
        var program = Read("src", "WaveBench.Cli", "Program.cs");

        var declared = Regex.Matches(program, @"new Command\(\s*""([a-z-]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        declared.Should().NotBeEmpty("the scan must find the CLI's commands");
        output.WriteLine("declared: " + string.Join(", ", declared.Order(StringComparer.Ordinal)));

        foreach (var document in new[] { "README.md", Path.Combine("docs", "user-guide.md") })
        {
            var used = Regex.Matches(Read(document), @"wavebench\s+([a-z-]+)")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            output.WriteLine($"{document}: {string.Join(", ", used.Order(StringComparer.Ordinal))}");
            used.Should().NotBeEmpty($"{document} should show how to run it");
            used.Except(declared).Should().BeEmpty($"{document} documents a command the CLI does not have");
        }
    }

    /// <summary>
    /// Every model file the docs tell a new user to run must be in the
    /// repository, or the very first command fails.
    /// </summary>
    [Fact]
    public void Gate_every_example_file_the_docs_name_is_present()
    {
        foreach (var document in new[] { "README.md", Path.Combine("docs", "user-guide.md") })
        {
            var referenced = Regex.Matches(Read(document), @"examples/([\w.-]+\.json)")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            foreach (var name in referenced)
            {
                var file = new FileInfo(Path.Combine(Repository().FullName, "examples", name));
                output.WriteLine($"{document} → examples/{name}: {(file.Exists ? "present" : "MISSING")}");
                file.Exists.Should().BeTrue($"{document} tells the reader to run examples/{name}");
            }
        }
    }

    /// <summary>
    /// Relative links between the documents have to resolve. A user guide that
    /// links to a citation list that is not there is a dead end at exactly the
    /// moment somebody is checking whether to trust a number.
    /// </summary>
    [Fact]
    public void Every_relative_link_between_documents_resolves()
    {
        string[] documents = ["README.md", Path.Combine("docs", "user-guide.md"), Path.Combine("docs", "citations.md")];

        foreach (var document in documents)
        {
            var directory = Path.GetDirectoryName(Path.Combine(Repository().FullName, document))!;

            foreach (Match match in Regex.Matches(Read(document), @"\]\(([^)#:]+?)(?:#[^)]*)?\)"))
            {
                var target = match.Groups[1].Value;
                if (target.Length == 0 || target.StartsWith("http", StringComparison.Ordinal))
                {
                    continue;
                }

                var resolved = Path.GetFullPath(Path.Combine(directory, target));
                var exists = File.Exists(resolved) || Directory.Exists(resolved);

                if (!exists)
                {
                    output.WriteLine($"{document} → {target}  BROKEN");
                }

                exists.Should().BeTrue($"{document} links to '{target}', which does not exist");
            }
        }

        output.WriteLine("all relative links resolve");
    }

    /// <summary>
    /// The quick start has to reach a TORQUE CURVE, which is what the gate
    /// asks for — not merely run something.
    /// </summary>
    [Fact]
    public void The_quick_start_reaches_a_torque_curve()
    {
        var readme = Read("README.md");
        var guide = Read("docs", "user-guide.md");

        readme.Should().Contain("torque curve");
        readme.Should().Contain("sweep", "a torque curve comes from a sweep");
        readme.Should().Contain("mesh", "and it is not evidence until it is mesh-converged");

        guide.Should().Contain("Fifteen minutes to a torque curve");
        guide.Should().Contain("dotnet build", "a reader building from source needs the command");
    }

    /// <summary>
    /// The validation gallery must state what is NOT validated. A gallery of
    /// only the successes is advertising.
    /// </summary>
    [Fact]
    public void Gate_the_validation_gallery_states_what_is_not_validated()
    {
        var readme = Read("README.md");

        readme.Should().Contain("Validation gallery");
        readme.Should().Contain("Not validated");

        // The standing gaps, by name. Each is deferred for a stated reason and
        // must not quietly disappear from the front page.
        readme.Should().Contain("Transient spool", "the open transient case must be on the front page");
        readme.Should().Contain("ECMA-418-2", "the deferred psychoacoustic metrics must be on the front page");

        foreach (var (title, _, _) in ValidationRegister.Open)
        {
            output.WriteLine("open case: " + title);
        }

        ValidationRegister.Open.Should().NotBeEmpty();
        ValidationRegister.Verified.Should().OnlyContain(v => v.Source.Length > 0);
    }

    /// <summary>
    /// The README's own claims about the repository have to be true — the
    /// project list especially, since it is what a contributor navigates by.
    /// </summary>
    [Fact]
    public void The_readme_describes_the_solution_that_is_actually_here()
    {
        var readme = Read("README.md");
        var projects = Repository().GetDirectories("src")[0].GetDirectories()
            .Select(d => d.Name)
            .Where(n => n.StartsWith("WaveBench.", StringComparison.Ordinal))
            .ToList();

        foreach (var project in projects)
        {
            output.WriteLine($"{project}: {(readme.Contains(project, StringComparison.Ordinal) ? "listed" : "MISSING")}");
            readme.Should().Contain(project, $"{project} is in the solution and should be in the layout table");
        }

        // The framework claim. The app is WPF; saying WinUI 3 sends a
        // contributor to install a workload this repository does not use.
        readme.Should().Contain("WPF");
        readme.Should().NotContain("WinUI");
    }
}
