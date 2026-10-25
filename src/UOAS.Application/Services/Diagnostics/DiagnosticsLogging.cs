using FiddlerWebApp.Services.Users;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace FiddlerWebApp.Services.Diagnostics;

public sealed class DiagnosticLogControl(LogEventLevel minimumLevel)
{
    public LoggingLevelSwitch LevelSwitch { get; } = new(minimumLevel);
}

public static class DiagnosticsLogging
{
    public static void AddDiagnosticsLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(new DiagnosticLogStore(builder.Configuration.GetValue("Diagnostics:BufferCapacity", 2000)));
        builder.Services.AddSingleton(new DiagnosticLogControl(builder.Configuration.GetValue("Serilog:MinimumLevel:Default", LogEventLevel.Information)));
        builder.Services.AddSerilog((services, logger) =>
        {
            string directory = Path.GetFullPath(builder.Configuration["Diagnostics:LogDirectory"] ?? "App_Data/logs", builder.Environment.ContentRootPath);
            Directory.CreateDirectory(directory);
            logger.ReadFrom.Configuration(builder.Configuration)
                .MinimumLevel.ControlledBy(services.GetRequiredService<DiagnosticLogControl>().LevelSwitch)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                .WriteTo.Sink(services.GetRequiredService<DiagnosticLogStore>())
                .WriteTo.File(new JsonFormatter(renderMessage: true), Path.Combine(directory, ApplicationInfo.LogFilePattern),
                    rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true, retainedFileCountLimit: 14, shared: true);
        }, preserveStaticLogger: true);
    }
}
