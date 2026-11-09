// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the consecutive-days retraction window: after a single check the error list retracts the
/// consecutive-days entries of the client from the checked week's Monday minus RUN_RETRACT_LOOKBACK_DAYS
/// (Klacks.Ui/src/app/domain/constants/schedule-validation-keys.constants.ts) up to its Sunday, and the backend single
/// check sends every run of exactly that window again (ScenarioConstants.BoundaryDays). If the two numbers differ, the
/// list either drops runs the backend does not send again or keeps entries of runs that ended.
///
/// Like ShiftListViewRoutesUiMirrorGuardTests this is a source scan of the TypeScript file. Klacks.Ui is located next to
/// Klacks.Api; when it is not checked out there the guard reports itself inconclusive. A Klacks.Ui that is checked out
/// but lacks the file or the constant is a hard failure.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Application.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class RunRetractLookbackUiMirrorGuardTests
{
    private const string UiProjectDirectory = "Klacks.Ui";

    private static readonly string[] ConstantsFileSegments =
    [
        "src", "app", "domain", "constants", "schedule-validation-keys.constants.ts"
    ];

    private static readonly Regex LookbackDeclaration = new(
        @"(?:export\s+)?const\s+RUN_RETRACT_LOOKBACK_DAYS\s*(?::[^=]+)?=\s*(?<value>\d+)\s*;",
        RegexOptions.Compiled);

    [Test]
    public void UiRunRetractLookback_MustMirrorTheBackendBoundaryDays()
    {
        var uiProject = RepositoryRootLocator.FindDirectory(UiProjectDirectory);
        if (uiProject == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiProjectDirectory));
            return;
        }

        var path = Path.Combine(uiProject, Path.Combine(ConstantsFileSegments));
        File.Exists(path).ShouldBeTrue(
            $"The Angular schedule validation key constants file is missing: {path}. {UiProjectDirectory} is checked out, " +
            "so the error list has no retraction window for consecutive-days entries - which is drift, not a missing checkout.");

        var match = LookbackDeclaration.Match(File.ReadAllText(path));
        match.Success.ShouldBeTrue($"RUN_RETRACT_LOOKBACK_DAYS was not found as a plain number in {path}.");

        int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture).ShouldBe(
            ScenarioConstants.BoundaryDays,
            "RUN_RETRACT_LOOKBACK_DAYS in Klacks.Ui must equal ScenarioConstants.BoundaryDays: the single check sends the " +
            "consecutive-days runs of exactly that window again, and the error list retracts exactly that window.");
    }
}
