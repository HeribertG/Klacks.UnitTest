// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Phase 1 (observe) tests: the skill usage tracker threads the session id from the execution
/// context onto the persisted usage record, which is what later enables per-session skill-sequence
/// learning. A missing session id is tolerated and never breaks tracking.
/// </summary>

using Klacks.Api.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services;

[TestFixture]
public class SkillUsageTrackerServiceTests
{
    private static SkillDescriptor Descriptor() => new(
        "add_break", "desc", SkillCategory.Crud, Array.Empty<SkillParameter>(),
        Array.Empty<string>(), Array.Empty<LLMCapability>(), null);

    private static SkillExecutionContext Context(string? sessionId) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        SessionId = sessionId,
    };

    private static (SkillUsageTrackerService Sut, Func<SkillUsageRecord?> Saved) Build()
    {
        var repo = Substitute.For<ISkillUsageRepository>();
        SkillUsageRecord? saved = null;
        repo.When(r => r.AddAsync(Arg.Any<SkillUsageRecord>(), Arg.Any<CancellationToken>()))
            .Do(ci => saved = ci.Arg<SkillUsageRecord>());
        var sut = new SkillUsageTrackerService(repo, Substitute.For<ISkillSequenceProactiveNotifier>(), NullLogger<SkillUsageTrackerService>.Instance);
        return (sut, () => saved);
    }

    [Test]
    public async Task TrackAsync_WritesSessionIdFromContext()
    {
        var (sut, saved) = Build();

        await sut.TrackAsync(Descriptor(), Context("conv-123"), new Dictionary<string, object>(),
            SkillResult.SuccessResult(null), TimeSpan.FromMilliseconds(5));

        saved().ShouldNotBeNull();
        saved()!.SessionId.ShouldBe("conv-123");
        saved()!.SkillName.ShouldBe("add_break");
        saved()!.Success.ShouldBeTrue();
    }

    [Test]
    public async Task TrackAsync_NullSessionId_IsTolerated()
    {
        var (sut, saved) = Build();

        await sut.TrackAsync(Descriptor(), Context(null), new Dictionary<string, object>(),
            SkillResult.SuccessResult(null), TimeSpan.FromMilliseconds(5));

        saved().ShouldNotBeNull();
        saved()!.SessionId.ShouldBeNull();
    }
}
