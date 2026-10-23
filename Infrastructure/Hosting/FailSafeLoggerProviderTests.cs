using Klacks.Api.Infrastructure.Hosting;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

namespace Klacks.UnitTest.Infrastructure.Hosting;

[TestFixture]
public class FailSafeLoggerProviderTests
{
    private const string Category = "Klacks.Api.Infrastructure.Persistence.DatabaseInitializer";
    private const string State = "Error during database initialization";
    private const string WriteFailureMessage = "Cannot open log for source 'Klacks.Api'. You may not have write access.";
    private const string NoticeMarker = "failed and was ignored";

    private static readonly Func<string, Exception?, string> Formatter = (state, _) => state;

    private ILogger _innerLogger = null!;
    private ILoggerProvider _innerProvider = null!;
    private StringWriter _errorOutput = null!;

    [SetUp]
    public void SetUp()
    {
        _innerLogger = Substitute.For<ILogger>();
        _innerProvider = Substitute.For<ILoggerProvider, ISupportExternalScope>();
        _innerProvider.CreateLogger(Arg.Any<string>()).Returns(_innerLogger);
        _errorOutput = new StringWriter();
    }

    [TearDown]
    public void TearDown()
    {
        _innerProvider.Dispose();
        _errorOutput.Dispose();
    }

    [Test]
    public void Log_ThroughLoggerFactory_WithoutDecorator_ThrowsAggregateException()
    {
        MakeInnerLoggerThrow();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(_innerProvider));
        var logger = factory.CreateLogger(Category);

        Should.Throw<AggregateException>(() => LogError(logger));
    }

    [Test]
    public void Log_ThroughLoggerFactory_WhenInnerLoggerThrows_DoesNotThrow()
    {
        MakeInnerLoggerThrow();
        using var factory = LoggerFactory.Create(builder =>
            builder.AddProvider(new FailSafeLoggerProvider(_innerProvider, _errorOutput)));
        var logger = factory.CreateLogger(Category);

        Should.NotThrow(() => LogError(logger));
    }

    [Test]
    public void Log_WhenInnerLoggerSucceeds_ForwardsEntry()
    {
        var provider = new FailSafeLoggerProvider(_innerProvider, _errorOutput);
        var exception = new InvalidOperationException();
        var eventId = new EventId(42);

        provider.CreateLogger(Category).Log(LogLevel.Error, eventId, State, exception, Formatter);

        _innerProvider.Received(1).CreateLogger(Category);
        _innerLogger.Received(1).Log(LogLevel.Error, eventId, State, exception, Formatter);
        _errorOutput.ToString().ShouldBeEmpty();
    }

    [Test]
    public void Log_WhenInnerLoggersFailRepeatedly_ReportsOnlyTheFirstFailure()
    {
        MakeInnerLoggerThrow();
        var provider = new FailSafeLoggerProvider(_innerProvider, _errorOutput);
        var first = provider.CreateLogger(Category);
        var second = provider.CreateLogger(nameof(FailSafeLoggerProviderTests));

        LogError(first);
        LogError(first);
        LogError(second);

        var output = _errorOutput.ToString();
        output.ShouldContain(WriteFailureMessage);
        CountOccurrences(output, NoticeMarker).ShouldBe(1);
    }

    [Test]
    public void IsEnabled_ForwardsToInnerLogger()
    {
        _innerLogger.IsEnabled(LogLevel.Warning).Returns(true);
        var logger = new FailSafeLoggerProvider(_innerProvider, _errorOutput).CreateLogger(Category);

        logger.IsEnabled(LogLevel.Warning).ShouldBeTrue();
        logger.IsEnabled(LogLevel.Information).ShouldBeFalse();
    }

    [Test]
    public void BeginScope_ReturnsScopeOfInnerLogger()
    {
        var scope = Substitute.For<IDisposable>();
        _innerLogger.BeginScope(State).Returns(scope);
        var logger = new FailSafeLoggerProvider(_innerProvider, _errorOutput).CreateLogger(Category);

        logger.BeginScope(State).ShouldBeSameAs(scope);
    }

    [Test]
    public void SetScopeProvider_ForwardsToScopeAwareInnerProvider()
    {
        var scopeProvider = Substitute.For<IExternalScopeProvider>();

        new FailSafeLoggerProvider(_innerProvider, _errorOutput).SetScopeProvider(scopeProvider);

        ((ISupportExternalScope)_innerProvider).Received(1).SetScopeProvider(scopeProvider);
    }

    [Test]
    public void Dispose_DisposesInnerProvider()
    {
        new FailSafeLoggerProvider(_innerProvider, _errorOutput).Dispose();

        _innerProvider.Received(1).Dispose();
    }

    [Test]
    public void Provider_CarriesTheEventLogAlias()
    {
        var alias = typeof(FailSafeLoggerProvider).GetCustomAttribute<ProviderAliasAttribute>();

        alias.ShouldNotBeNull();
        alias.Alias.ShouldBe("EventLog");
    }

    [Test]
    public void Log_WhenEventLogSectionRaisesTheMinimumLevel_AppliesTheConfiguredLevel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:EventLog:LogLevel:Default"] = "Error" })
            .Build();
        _innerLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddProvider(new FailSafeLoggerProvider(_innerProvider, _errorOutput));
        });
        var logger = factory.CreateLogger(Category);

        logger.Log(LogLevel.Warning, new EventId(0), State, null, Formatter);
        logger.Log(LogLevel.Error, new EventId(0), State, null, Formatter);

        _innerLogger.DidNotReceive().Log(LogLevel.Warning, Arg.Any<EventId>(), State, Arg.Any<Exception?>(), Formatter);
        _innerLogger.Received(1).Log(LogLevel.Error, Arg.Any<EventId>(), State, Arg.Any<Exception?>(), Formatter);
    }

    private void MakeInnerLoggerThrow()
    {
        _innerLogger
            .When(logger => logger.Log(
                Arg.Any<LogLevel>(),
                Arg.Any<EventId>(),
                Arg.Any<string>(),
                Arg.Any<Exception?>(),
                Arg.Any<Func<string, Exception?, string>>()))
            .Do(_ => throw new InvalidOperationException(WriteFailureMessage));
    }

    private static void LogError(ILogger logger)
    {
        logger.Log(LogLevel.Error, new EventId(0), State, new InvalidOperationException(), Formatter);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
