// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

[TestFixture]
public class SchedulingPolicyResolverDailyFrameTests
{
    private static readonly DateOnly Date = new(2026, 10, 5);

    private static SchedulingPolicyResolver ResolverFor(EffectiveContractData data)
    {
        var provider = Substitute.For<IClientContractDataProvider>();
        provider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>()).Returns(data);
        return new SchedulingPolicyResolver(provider);
    }

    [Test]
    public async Task GetForClientAsync_LegalFrameOfThePlace_IsTheDailyWorkFrame()
    {
        var resolver = ResolverFor(new EffectiveContractData { MinPauseHours = 11m, MaxDailySpanHours = 14m });

        var policy = await resolver.GetForClientAsync(Guid.NewGuid(), Date);

        policy.MaxDailySpan.ShouldBe(TimeSpan.FromHours(14));
        policy.DailyWorkFrame.ShouldBe(TimeSpan.FromHours(14));
    }

    [Test]
    public async Task GetForClientAsync_NoLegalFrame_FallsBackTo24HoursMinusMinRest()
    {
        var resolver = ResolverFor(new EffectiveContractData { MinPauseHours = 12m, MaxDailySpanHours = 0m });

        var policy = await resolver.GetForClientAsync(Guid.NewGuid(), Date);

        policy.MaxDailySpan.ShouldBeNull();
        policy.DailyWorkFrame.ShouldBe(TimeSpan.FromHours(12));
    }
}
