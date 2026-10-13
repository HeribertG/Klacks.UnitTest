// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Test helper for the rights check RecipeEngineService runs on a matched recipe: a skill repository substitute
/// that reports no required permission for any skill, so a test of recipe routing is not about rights and still
/// passes the check, plus a variant that reports the given requirements.
/// </summary>

using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.TestHelpers;

public static class SkillRepositoryStubs
{
    public static IAgentSkillRepository WithoutRequirements() => WithRequirements(new Dictionary<string, string?>());

    public static IAgentSkillRepository WithRequirements(IReadOnlyDictionary<string, string?> requirements)
    {
        var repository = Substitute.For<IAgentSkillRepository>();
        repository.GetRequiredPermissionsByNameAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<string, string?>)((IReadOnlyCollection<string>)call[0])
                .Where(requirements.ContainsKey)
                .ToDictionary(name => name, name => requirements[name], StringComparer.OrdinalIgnoreCase));
        return repository;
    }
}
