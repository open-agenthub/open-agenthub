using AgentHub.Api.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionFileLoggingTests
{
    [Fact]
    public void The_request_start_line_stays_off_even_when_configuration_turns_it_on()
    {
        // The line that prints a request's query string, and so a video stream ticket. An operator
        // raising every level to Debug must not bring it back.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Debug",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
            [$"Logging:LogLevel:{SessionFileLogging.RequestStartCategory}"] = "Trace",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddProvider(new NullProvider());
        });
        services.KeepStreamTicketsOutOfLogs();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();

        var requestStart = factory.CreateLogger(SessionFileLogging.RequestStartCategory);
        var routing = factory.CreateLogger("Microsoft.AspNetCore.Routing");

        Assert.False(requestStart.IsEnabled(LogLevel.Information));
        Assert.False(requestStart.IsEnabled(LogLevel.Debug));
        Assert.True(requestStart.IsEnabled(LogLevel.Warning));
        Assert.True(routing.IsEnabled(LogLevel.Debug));
    }

    // Enabled for everything, so whatever IsEnabled answers is the filter rules' decision alone.
    private sealed class NullProvider : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
        public void Dispose() { }
    }
}
