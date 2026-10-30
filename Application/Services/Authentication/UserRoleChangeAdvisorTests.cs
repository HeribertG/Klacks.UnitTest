// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the deterministic findings of the user role change advisor: rights gained and lost per role pair (the Planer
/// floor is never reported as gained or lost), settings access coming and going, the Admin bypass, an unchanged
/// role (also for an account without any role), the last active administrator (blocking) versus a second one,
/// a deactivated administrator, the caller cutting off their own settings access and the highest-role rule.
/// </summary>

using Klacks.Api.Application.DTOs.Authentification;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Authentication;

[TestFixture]
public class UserRoleChangeAdvisorTests
{
    private const string AccountName = "Anna Muster";
    private const string AccountId = "u1";
    private const int TwoAdministrators = 2;
    private const int OneAdministrator = 1;

    private static UserRoleChangeFacts Facts(
        string[] currentRoles,
        string targetRole,
        int administrators = TwoAdministrators,
        bool active = true,
        bool isCaller = false) =>
        new(AccountId, AccountName, currentRoles, targetRole, active, administrators, isCaller);

    private static IEnumerable<UserRoleChangeFindingCode> Codes(UserRoleChangeEvaluation evaluation) =>
        evaluation.Findings.Select(finding => finding.Code);

    [Test]
    public void AuthorisedToAdmin_GainsDeleteAndSettingsRights_AndTheAdminBypass()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Authorised], Roles.Admin));

        evaluation.CurrentRole.ShouldBe(Roles.Authorised);
        evaluation.RightsLost.ShouldBeEmpty();
        evaluation.RightsGained.ShouldContain(Permissions.CanEditSettings);
        evaluation.RightsGained.ShouldContain(Permissions.CanViewSettings);
        evaluation.RightsGained.ShouldContain(Permissions.CanDeleteClients);
        evaluation.RightsGained.ShouldNotContain(Permissions.CanEditClients);
        Codes(evaluation).ShouldContain(UserRoleChangeFindingCode.AdminBypassGained);
        Codes(evaluation).ShouldContain(UserRoleChangeFindingCode.SettingsAccessGained);
        Codes(evaluation).ShouldContain(UserRoleChangeFindingCode.RightsGained);
        Codes(evaluation).ShouldNotContain(UserRoleChangeFindingCode.RightsLost);
        evaluation.Findings.ShouldNotContain(finding => finding.IsBlocking);
    }

    [Test]
    public void AdminToAuthorised_WithAnotherAdministrator_LosesSettingsAndDeleteRights_NotBlocking()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Admin], Roles.Authorised));

        evaluation.RightsGained.ShouldBeEmpty();
        evaluation.RightsLost.ShouldContain(Permissions.CanEditSettings);
        evaluation.RightsLost.ShouldContain(Permissions.CanViewSettings);
        evaluation.RightsLost.ShouldContain(Permissions.CanDeleteShifts);
        evaluation.RightsLost.ShouldNotContain(Permissions.CanEditShifts);
        Codes(evaluation).ShouldContain(UserRoleChangeFindingCode.AdminBypassLost);
        Codes(evaluation).ShouldContain(UserRoleChangeFindingCode.SettingsAccessLost);
        Codes(evaluation).ShouldNotContain(UserRoleChangeFindingCode.LastAdministratorDemotion);
        evaluation.Findings.ShouldNotContain(finding => finding.IsBlocking);
        evaluation.Recommendation.ShouldStartWith("Setting Anna Muster from Admin to Authorised");
    }

    [Test]
    public void AuthorisedToUser_LosesOnlyWhatTheFloorDoesNotCover()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Authorised], Roles.User));

        evaluation.RightsLost.ShouldContain(Permissions.CanEditClients);
        evaluation.RightsLost.ShouldContain(Permissions.CanManageAutomation);
        evaluation.RightsLost.ShouldContain(Permissions.CanManageAccessTokens);
        Permissions.PlannerFloor.ShouldAllBe(floorRight => !evaluation.RightsLost.Contains(floorRight));
        evaluation.RightsGained.ShouldBeEmpty();
        Codes(evaluation).ShouldNotContain(UserRoleChangeFindingCode.SettingsAccessLost);
    }

    [Test]
    public void AccountWithoutRole_ToAuthorised_GainsOnlyAboveTheFloor()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([], Roles.Authorised));

        evaluation.CurrentRole.ShouldBe(Roles.User);
        evaluation.RightsLost.ShouldBeEmpty();
        evaluation.RightsGained.ShouldContain(Permissions.CanCreateClients);
        Permissions.PlannerFloor.ShouldAllBe(floorRight => !evaluation.RightsGained.Contains(floorRight));
    }

    [Test]
    public void AccountWithoutRole_ToUser_IsAnUnchangedRole()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([], Roles.User));

        Codes(evaluation).ShouldBe([UserRoleChangeFindingCode.RoleUnchanged]);
        evaluation.RightsGained.ShouldBeEmpty();
        evaluation.RightsLost.ShouldBeEmpty();
        evaluation.Recommendation.ShouldContain("already holds the role User");
    }

    [Test]
    public void AdminToAdmin_IsUnchanged_AndNeverBlocking_EvenAsTheOnlyAdministrator()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Admin], Roles.Admin, OneAdministrator));

        Codes(evaluation).ShouldBe([UserRoleChangeFindingCode.RoleUnchanged]);
        evaluation.RightsGained.ShouldBeEmpty();
        evaluation.RightsLost.ShouldBeEmpty();
        evaluation.Recommendation.ShouldContain("nothing would change");
    }

    [Test]
    public void AdminWithRedundantAuthorised_ToAdmin_ChangesNoRight()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Authorised, Roles.Admin], Roles.Admin));

        evaluation.CurrentRole.ShouldBe(Roles.Admin);
        Codes(evaluation).ShouldBe([UserRoleChangeFindingCode.RoleUnchanged]);
        evaluation.RightsGained.ShouldBeEmpty();
        evaluation.RightsLost.ShouldBeEmpty();
    }

    [TestCase(Roles.Authorised)]
    [TestCase(Roles.User)]
    public void OnlyActiveAdministrator_Demoted_IsBlocking_AndTheRecommendationSaysDoNot(string targetRole)
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Admin], targetRole, OneAdministrator));

        evaluation.Findings[0].Code.ShouldBe(UserRoleChangeFindingCode.LastAdministratorDemotion);
        evaluation.Findings[0].IsBlocking.ShouldBeTrue();
        evaluation.Recommendation.ShouldStartWith($"Do not set {AccountName} to {targetRole}");
        evaluation.Recommendation.ShouldContain("without an active administrator");
        evaluation.Recommendation.ShouldContain("Nothing has been changed");
    }

    [Test]
    public void DeactivatedAdministrator_Demoted_IsNotTheLastActiveAdministrator()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Admin], Roles.User, administrators: 0, active: false));

        Codes(evaluation).ShouldNotContain(UserRoleChangeFindingCode.LastAdministratorDemotion);
        Codes(evaluation).ShouldContain(UserRoleChangeFindingCode.AccountDeactivated);
        evaluation.Findings.ShouldNotContain(finding => finding.IsBlocking);
        evaluation.AccountActive.ShouldBeFalse();
    }

    [Test]
    public void OnlyActiveAdministrator_Promoted_IsNotBlocking()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Authorised], Roles.Admin, administrators: 0));

        evaluation.Findings.ShouldNotContain(finding => finding.IsBlocking);
    }

    [Test]
    public void OwnAccount_LosingSettingsEdit_IsFlaggedFirstAmongTheNonBlockingFindings()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Admin], Roles.Authorised, isCaller: true));

        evaluation.Findings[0].Code.ShouldBe(UserRoleChangeFindingCode.OwnAccountLosesSettingsAccess);
        evaluation.Findings[0].IsBlocking.ShouldBeFalse();
    }

    [Test]
    public void OwnAccount_WithoutSettingsAccess_IsNotFlagged()
    {
        var evaluation = UserRoleChangeAdvisor.Evaluate(Facts([Roles.Authorised], Roles.User, isCaller: true));

        Codes(evaluation).ShouldNotContain(UserRoleChangeFindingCode.OwnAccountLosesSettingsAccess);
    }

    [TestCase(new string[0], Roles.User)]
    [TestCase(new[] { Roles.User }, Roles.User)]
    [TestCase(new[] { Roles.Authorised }, Roles.Authorised)]
    [TestCase(new[] { Roles.Authorised, Roles.Admin }, Roles.Admin)]
    [TestCase(new[] { "Unknown" }, Roles.User)]
    public void EffectiveRole_IsTheHighestRoleHeld(string[] roles, string expected)
    {
        UserRoleChangeAdvisor.EffectiveRole(roles).ShouldBe(expected);
    }

    [Test]
    public void Evaluation_IsDeterministic_AndCarriesTheFactsOfTheAccountOnly()
    {
        var facts = Facts([Roles.Admin], Roles.Authorised);

        var first = UserRoleChangeAdvisor.Evaluate(facts);
        var second = UserRoleChangeAdvisor.Evaluate(facts);

        first.Recommendation.ShouldBe(second.Recommendation);
        first.RightsGained.ShouldBe(second.RightsGained);
        first.RightsLost.ShouldBe(second.RightsLost);
        first.AccountId.ShouldBe(AccountId);
        first.AccountName.ShouldBe(AccountName);
        first.ActiveAdministratorCount.ShouldBe(TwoAdministrators);
    }
}
