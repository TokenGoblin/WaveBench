using FluentAssertions;
using Xunit;

namespace WaveBench.Core.Tests;

/// <summary>
/// Plan Part 0 rule 5: WaveBench.Core never references a UI assembly.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Core_references_no_ui_assembly()
    {
        var forbidden = new[] { "WaveBench.App", "Microsoft.WindowsAppSDK", "Microsoft.WinUI", "PresentationFramework" };
        var referenced = typeof(WaveBench.Core.AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().NotContain(name => forbidden.Any(f =>
            string.Equals(name, f, StringComparison.OrdinalIgnoreCase)));
    }
}
