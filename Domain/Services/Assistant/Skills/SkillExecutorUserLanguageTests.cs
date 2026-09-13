// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// End-to-end test of the user language through the real dispatch path: SkillExecutorService resolves
/// the implementation, the dispatch-time parameter gate validates, the skill calls GetParameter and the
/// reader picks the culture. Proves that "03/04/2026" reaches the skill as 4 March for an English user
/// and as 3 April for a French one without any skill passing the language itself, and that the reserved
/// key the executor injects never reaches the usage tracker or the redacted usage log. The same
/// language also reaches the dispatch-time gate, so a relative day word of a foreign language is
/// refused before the skill runs instead of being resolved to the wrong day.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class SkillExecutorUserLanguageTests
{
    private const string SkillName = "probe_date_skill";
    private const string DateParameterName = "date";
    private const string AmbiguousDate = "03/04/2026";
    private const string FrenchYesterday = "hier";

    private static readonly string[] ProcessCultures = ["en-US", "de-CH"];

    private ISkillUsageTracker _usageTracker = null!;
    private DateProbeSkill _probe = null!;
    private SkillExecutorService _executor = null!;

    [SetUp]
    public void SetUp()
    {
        _probe = new DateProbeSkill();
        _usageTracker = Substitute.For<ISkillUsageTracker>();

        var registry = Substitute.For<ISkillRegistry>();
        registry.GetSkillByName(SkillName).Returns(Descriptor());

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(DateProbeSkill)).Returns(_probe);

        _executor = new SkillExecutorService(
            registry,
            _usageTracker,
            serviceProvider,
            Substitute.For<IGenericSkillDispatcher>(),
            Substitute.For<IAutonomyGate>(),
            Substitute.For<IEntityChangeNotifier>(),
            Substitute.For<IRecentEntityRegistrar>(),
            NullLogger<SkillExecutorService>.Instance);
    }

    [TestCase("en", 2026, 3, 4)]
    [TestCase("fr", 2026, 4, 3)]
    [TestCase("de", 2026, 4, 3)]
    public async Task AmbiguousDate_IsReadInTheCallersLanguage(
        string language, int year, int month, int day)
    {
        foreach (var processCulture in ProcessCultures)
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(processCulture);
                var result = await _executor.ExecuteAsync(Invocation(), ContextWith(language));

                result.Success.ShouldBeTrue(result.Message);
                _probe.ReadDate.ShouldBe(new DateOnly(year, month, day));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }

    [Test]
    public async Task ARelativeDayWordOfAnotherLanguage_IsRefusedByTheDispatchGate()
    {
        var result = await _executor.ExecuteAsync(Invocation(FrenchYesterday), ContextWith("de"));

        result.Success.ShouldBeFalse(
            "French 'hier' is German for 'here' - accepting it for a German user books yesterday");
        result.Message.ShouldContain(DateParameterName);
        _probe.ReadDate.ShouldBeNull();
    }

    [Test]
    public async Task ARelativeDayWordOfTheUsersOwnLanguage_PassesTheDispatchGate()
    {
        var result = await _executor.ExecuteAsync(Invocation(FrenchYesterday), ContextWith("fr"));

        result.Success.ShouldBeTrue(result.Message);
    }

    [Test]
    public async Task WithoutALanguage_TheReservedKeyIsNotAddedAtAll()
    {
        await _executor.ExecuteAsync(Invocation(), ContextWith(null));

        _probe.SeenKeys.ShouldNotContain(SkillParameterKeys.UserLanguage);
    }

    [Test]
    public async Task ReservedKey_NeverReachesTheUsageTracker()
    {
        await _executor.ExecuteAsync(Invocation(), ContextWith("en"));

        await _usageTracker.Received(1).TrackAsync(
            Arg.Any<SkillDescriptor>(),
            Arg.Any<SkillExecutionContext>(),
            Arg.Is<Dictionary<string, object>>(p => !p.ContainsKey(SkillParameterKeys.UserLanguage)),
            Arg.Any<SkillResult>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Guid?>());
    }

    [Test]
    public void ReservedKey_IsDroppedFromTheRedactedUsageLog()
    {
        var redacted = SkillParameterRedactor.Redact(new Dictionary<string, object>
        {
            [DateParameterName] = AmbiguousDate,
            [SkillParameterKeys.UserLanguage] = "en"
        });

        redacted.Keys.ShouldNotContain(SkillParameterKeys.UserLanguage);
        redacted.Keys.ShouldContain(DateParameterName);
    }

    private static SkillInvocation Invocation() => Invocation(AmbiguousDate);

    private static SkillInvocation Invocation(string dateValue) => new()
    {
        SkillName = SkillName,
        Parameters = new Dictionary<string, object> { [DateParameterName] = dateValue }
    };

    private static SkillExecutionContext ContextWith(string? language) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = nameof(SkillExecutorUserLanguageTests),
        UserPermissions = Array.Empty<string>(),
        UserLanguage = language
    };

    private static SkillDescriptor Descriptor() => new(
        SkillName,
        string.Empty,
        SkillCategory.Query,
        [new SkillParameter(DateParameterName, string.Empty, SkillParameterType.Date, true)],
        Array.Empty<string>(),
        Array.Empty<LLMCapability>(),
        typeof(DateProbeSkill));

    private sealed class DateProbeSkill : BaseSkillImplementation
    {
        public DateOnly? ReadDate { get; private set; }

        public IReadOnlyCollection<string> SeenKeys { get; private set; } = Array.Empty<string>();

        public override Task<SkillResult> ExecuteAsync(
            SkillExecutionContext context,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken = default)
        {
            SeenKeys = parameters.Keys.ToList();
            ReadDate = GetParameter<DateOnly?>(parameters, DateParameterName);
            return Task.FromResult(SkillResult.SuccessResult(null));
        }
    }
}
