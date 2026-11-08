// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Services.Schedules;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Owner rule 2026-11-07: minimums (rest hours, rest days) take 0 as "no requirement", caps (daily hours,
/// weekly hours, consecutive days) are never 0 - a legacy stored 0 still counts as empty and falls back.
/// </summary>
[TestFixture]
public class SchedulingPolicyResolverZeroValueTests
{
    private static readonly DateOnly Date = new(2026, 11, 9);

    private static SchedulingPolicyResolver ResolverFor(EffectiveContractData data)
    {
        var provider = Substitute.For<IClientContractDataProvider>();
        provider.GetEffectiveContractDataAsync(Arg.Any<Guid>(), Arg.Any<DateOnly>(), Arg.Any<int?>()).Returns(data);
        return new SchedulingPolicyResolver(provider);
    }

    [Test]
    public async Task GetForClientAsync_MinPauseHoursZero_MeansNoRestRequirement()
    {
        var resolver = ResolverFor(new EffectiveContractData { MinPauseHours = 0m, MinRestDays = 2m, MaxDailySpanHours = 0m });

        var policy = await resolver.GetForClientAsync(Guid.NewGuid(), Date);

        policy.MinRestHours.ShouldBe(TimeSpan.Zero);
        policy.DailyWorkFrame.ShouldBe(TimeSpan.FromHours(24));
    }

    [Test]
    public async Task GetForClientAsync_MinRestDaysZero_MeansNoRestDayRequirement()
    {
        var resolver = ResolverFor(new EffectiveContractData { MinPauseHours = 11m, MinRestDays = 0m });

        var policy = await resolver.GetForClientAsync(Guid.NewGuid(), Date);

        policy.MinRestDays.ShouldBe(0m);
    }

    [Test]
    public async Task GetForClientAsync_LegacyStoredZeroCaps_FallBackToTheDefaultsInsteadOfBlockingEverything()
    {
        var resolver = ResolverFor(new EffectiveContractData
        {
            MinPauseHours = 11m,
            MinRestDays = 2m,
            MaxDailyHours = 0m,
            MaxWeeklyHours = 0m,
            MaxConsecutiveDays = 0,
        });

        var policy = await resolver.GetForClientAsync(Guid.NewGuid(), Date);

        policy.MaxDailyHours.ShouldBe(TimeSpan.FromHours(SchedulingPolicyDefaults.MaxDailyHours));
        policy.MaxWeeklyHours.ShouldBe(TimeSpan.FromHours(SchedulingPolicyDefaults.MaxWeeklyHours));
        policy.MaxConsecutiveDays.ShouldBe(SchedulingPolicyDefaults.MaxConsecutiveDays);
    }

    [Test]
    public async Task GetForClientAsync_SetCapsAndMinimums_AreTakenAsTheyAre()
    {
        var resolver = ResolverFor(new EffectiveContractData
        {
            MinPauseHours = 9m,
            MinRestDays = 1.5m,
            MaxDailyHours = 1m,
            MaxWeeklyHours = 40m,
            MaxConsecutiveDays = 1,
        });

        var policy = await resolver.GetForClientAsync(Guid.NewGuid(), Date);

        policy.MinRestHours.ShouldBe(TimeSpan.FromHours(9));
        policy.MinRestDays.ShouldBe(1.5m);
        policy.MaxDailyHours.ShouldBe(TimeSpan.FromHours(1));
        policy.MaxWeeklyHours.ShouldBe(TimeSpan.FromHours(40));
        policy.MaxConsecutiveDays.ShouldBe(1);
    }
}
