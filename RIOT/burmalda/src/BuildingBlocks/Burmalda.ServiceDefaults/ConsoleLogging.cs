using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Burmalda.ServiceDefaults;

public sealed class BurmaldaConsoleOptions : ConsoleFormatterOptions
{
    public bool Colors { get; set; } = !Console.IsOutputRedirected;
    public bool IncludeExceptionDetails { get; set; }
}

public sealed class BurmaldaConsoleFormatter(IOptionsMonitor<BurmaldaConsoleOptions> options)
    : ConsoleFormatter("burmalda")
{
    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopes, TextWriter writer)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        if (string.IsNullOrEmpty(message) && entry.Exception is null) return;
        var (label, color) = entry.LogLevel switch
        {
            LogLevel.Trace => ("trace", "90"), LogLevel.Debug => ("debug", "90"),
            LogLevel.Warning => ("warn", "33"), LogLevel.Error => ("error", "31"),
            LogLevel.Critical => ("critical", "97;41"), _ => ("info", "36")
        };
        var settings = options.CurrentValue;
        writer.Write(settings.Colors ? $"\u001b[{color}m[{label}]\u001b[0m" : $"[{label}]");
        writer.Write($" [{DateTimeOffset.UtcNow:HH:mm:ss.fff} UTC] [{entry.Category.Split('.').Last()}] ");
        writer.Write(message is null ? null : string.Concat(message.Select(c => char.IsControl(c) ? ' ' : c)));
        if (entry.Exception is { } exception)
            writer.Write(settings.IncludeExceptionDetails ? $" {exception}" : $" ({exception.GetType().Name})");
        writer.WriteLine();
    }
}

public static class ConsoleLogging
{
    public static ILoggingBuilder AddBurmaldaConsole(this ILoggingBuilder logging, IConfiguration configuration)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        logging.ClearProviders();
        logging.AddConsole(x => x.FormatterName = "burmalda")
            .AddConsoleFormatter<BurmaldaConsoleFormatter, BurmaldaConsoleOptions>(x =>
            {
                x.Colors = configuration.GetValue<bool?>("Logging:Console:Colors") ?? !Console.IsOutputRedirected;
                x.IncludeExceptionDetails = configuration.GetValue<bool>("Logging:Console:IncludeExceptionDetails");
            });
        // Polling, SQL statements and transport heartbeats obscure the business journal.
        // Errors remain visible; detailed diagnostics can be enabled through configuration.
        logging.AddFilter("Microsoft.AspNetCore", configuration.GetValue<LogLevel?>("Logging:LogLevel:Microsoft.AspNetCore") ?? LogLevel.Warning);
        logging.AddFilter("Microsoft.EntityFrameworkCore", configuration.GetValue<LogLevel?>("Logging:LogLevel:Microsoft.EntityFrameworkCore") ?? LogLevel.Warning);
        logging.AddFilter("System.Net.Http", LogLevel.Warning);
        logging.AddFilter("Hangfire", LogLevel.Warning);
        logging.AddFilter("Yarp", LogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        return logging;
    }
}
