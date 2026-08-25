using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace WaveBench.Cli;

/// <summary>
/// Headless entry point. Run/sweep/validate commands land in Phase 7; Phase 0
/// establishes the logging pipeline (console + rolling file, plan §7.3).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine("logs", "wavebench-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
            var logger = loggerFactory.CreateLogger("WaveBench");

            var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            logger.LogInformation("WaveBench CLI {Version} — headless solver arrives in Phase 7.", version);
            return 0;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
