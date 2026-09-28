// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;

internal static class LearningModeOptions
{
    public static SkillLearningOptions Options(
        SkillLearningMode mode,
        int minGoldenCases = SkillLearningDefaults.MinGoldenCasesForAutoApply,
        int minNetGain = SkillLearningDefaults.GateMinNetGain) =>
        new(
            SkillLearningDefaults.MinOccurrences,
            SkillLearningDefaults.MinDistinctUsers,
            SkillLearningDefaults.PruneDays,
            SkillLearningDefaults.RetentionDays,
            minGoldenCases,
            mode,
            minNetGain);

    public static ISkillLearningOptionsProvider Provider(SkillLearningMode mode)
    {
        var provider = Substitute.For<ISkillLearningOptionsProvider>();
        provider.GetAsync(Arg.Any<CancellationToken>()).Returns(Options(mode));
        return provider;
    }
}
