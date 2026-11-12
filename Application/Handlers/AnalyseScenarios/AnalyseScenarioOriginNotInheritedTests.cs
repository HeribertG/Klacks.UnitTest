// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// AnalyseScenario.Origin AbsenceCover marks the cover of an absence and nothing else: a wizard or harmonizer apply built on
/// a cover scenario writes a scenario of origin Other, and in the whole Api source only CoverAbsenceCommandHandler assigns
/// AbsenceCover and no code copies the Origin of one scenario onto another (clone, variant or mapper) in a file that deals
/// with scenarios. The not overridable stale refusal of a scenario built on a cover comes from the root of its source chain
/// (ScenarioStalenessDetector), never from an inherited Origin; the origin is not sent to the Ui.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Application.Services.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.AnalyseScenarios;

[TestFixture]
public class AnalyseScenarioOriginNotInheritedTests
{
    private const string CoverHandlerFileName = "CoverAbsenceCommandHandler.cs";
    private const string ScenarioTypeName = "AnalyseScenario";

    private static readonly Regex OriginCopy = new(@"\bOrigin\s*=\s*\w+\.Origin\b", RegexOptions.CultureInvariant);

    private static readonly Regex AbsenceCoverAssignment =
        new(@"(?<![=!<>])=\s*AnalyseScenarioOrigin\.AbsenceCover\b", RegexOptions.CultureInvariant);

    [Test]
    public void AppliedScenarioBuiltOnACover_HasOriginOther()
    {
        var scenario = AppliedScenarioBuilder.Create(
            "Wizard on cover", Guid.NewGuid(), new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), Guid.NewGuid(), Guid.NewGuid(),
            sourceToken: Guid.NewGuid());

        scenario.Origin.ShouldBe(AnalyseScenarioOrigin.Other);
    }

    [Test]
    public void OnlyTheCoverHandlerAssignsAbsenceCover_AndNoCodeCopiesTheOrigin()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var files = Directory.EnumerateFiles(apiRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        files.ShouldNotBeEmpty();

        var assigning = files
            .Where(f => AbsenceCoverAssignment.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        var copying = files
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains(ScenarioTypeName, StringComparison.Ordinal) && OriginCopy.IsMatch(text);
            })
            .Select(Path.GetFileName)
            .ToList();

        assigning.ShouldContain(CoverHandlerFileName);
        assigning.Where(name => name != CoverHandlerFileName).ShouldBeEmpty();
        copying.ShouldBeEmpty();
    }
}
