// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Truth-table tests for RecentEntityExtractor: a curated create/update skill whose payload carries its
/// own entity id extracts (type, id, name, action); an unlisted skill, a relationship/bulk skill whose
/// payload only carries a foreign id, a failed result, a null payload, and a payload missing the id all
/// extract nothing — the extractor never guesses.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant.Skills;

[TestFixture]
public class RecentEntityExtractorTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Test]
    public void CreateShift_extracts_shift_created_with_own_name()
    {
        var result = SkillResult.SuccessResult(new
        {
            ShiftId = Id,
            SealedOrderId = Guid.NewGuid(),
            Name = "Frühdienst",
            ClientId = Guid.NewGuid(),
            ClientName = "ACME AG"
        });

        RecentEntityExtractor.TryExtract("create_shift", result, out var d).ShouldBeTrue();
        d.ShouldNotBeNull();
        d!.EntityType.ShouldBe("shift");
        d.EntityId.ShouldBe(Id);
        d.DisplayName.ShouldBe("Frühdienst");
        d.Action.ShouldBe("created");
    }

    [Test]
    public void CreateEmployee_extracts_client_from_EmployeeId_with_composite_name()
    {
        var result = SkillResult.SuccessResult(new
        {
            EmployeeId = Id,
            FirstName = "Anna",
            LastName = "Meier"
        });

        RecentEntityExtractor.TryExtract("create_employee", result, out var d).ShouldBeTrue();
        d!.EntityType.ShouldBe("client");
        d.EntityId.ShouldBe(Id);
        d.DisplayName.ShouldBe("Anna Meier");
        d.Action.ShouldBe("created");
    }

    [Test]
    public void CreateGroup_extracts_group_created()
    {
        var result = SkillResult.SuccessResult(new { GroupId = Id, Name = "Bern" });

        RecentEntityExtractor.TryExtract("create_group", result, out var d).ShouldBeTrue();
        d!.EntityType.ShouldBe("group");
        d.EntityId.ShouldBe(Id);
        d.DisplayName.ShouldBe("Bern");
        d.Action.ShouldBe("created");
    }

    [Test]
    public void UpdateMembership_extracts_membership_updated_without_name()
    {
        var result = SkillResult.SuccessResult(new
        {
            MembershipId = Id,
            ChangedFields = new[] { "validFrom" }
        });

        RecentEntityExtractor.TryExtract("update_membership", result, out var d).ShouldBeTrue();
        d!.EntityType.ShouldBe("membership");
        d.EntityId.ShouldBe(Id);
        d.DisplayName.ShouldBeNull();
        d.Action.ShouldBe("updated");
    }

    [Test]
    public void UpdateClient_extracts_client_updated()
    {
        var result = SkillResult.SuccessResult(new
        {
            ClientId = Id,
            ChangedFields = new[] { "firstName" },
            FirstName = "Max",
            LastName = "Muster"
        });

        RecentEntityExtractor.TryExtract("update_client", result, out var d).ShouldBeTrue();
        d!.EntityType.ShouldBe("client");
        d.EntityId.ShouldBe(Id);
        d.DisplayName.ShouldBe("Max Muster");
        d.Action.ShouldBe("updated");
    }

    [Test]
    public void CreateContract_extracts_from_bare_Id()
    {
        var result = SkillResult.SuccessResult(new { Id = Id, Name = "Vollzeit" });

        RecentEntityExtractor.TryExtract("create_contract", result, out var d).ShouldBeTrue();
        d!.EntityType.ShouldBe("contract");
        d.EntityId.ShouldBe(Id);
        d.DisplayName.ShouldBe("Vollzeit");
    }

    [Test]
    public void UnlistedSkill_with_foreign_ClientId_is_not_registered()
    {
        // add_break's payload carries ClientId/AbsenceId but NO BreakId — must never register a client.
        var result = SkillResult.SuccessResult(new { ClientId = Id, AbsenceId = Guid.NewGuid() });

        RecentEntityExtractor.TryExtract("add_break", result, out var d).ShouldBeFalse();
        d.ShouldBeNull();
    }

    [Test]
    public void RelationshipSkill_add_client_to_group_is_not_registered()
    {
        var result = SkillResult.SuccessResult(new { ClientId = Id, GroupId = Guid.NewGuid() });

        RecentEntityExtractor.TryExtract("add_client_to_group", result, out var d).ShouldBeFalse();
        d.ShouldBeNull();
    }

    [Test]
    public void FailedResult_is_not_registered()
    {
        var result = SkillResult.Error("boom");

        RecentEntityExtractor.TryExtract("create_shift", result, out var d).ShouldBeFalse();
        d.ShouldBeNull();
    }

    [Test]
    public void NullData_is_not_registered()
    {
        var result = SkillResult.SuccessResult(null);

        RecentEntityExtractor.TryExtract("create_shift", result, out var d).ShouldBeFalse();
        d.ShouldBeNull();
    }

    [Test]
    public void TrackedSkill_without_its_id_property_is_not_registered()
    {
        var result = SkillResult.SuccessResult(new { Name = "orphan" });

        RecentEntityExtractor.TryExtract("create_shift", result, out var d).ShouldBeFalse();
        d.ShouldBeNull();
    }

    [Test]
    public void CreateEmployee_without_names_extracts_null_display_name()
    {
        var result = SkillResult.SuccessResult(new { EmployeeId = Id });

        RecentEntityExtractor.TryExtract("create_employee", result, out var d).ShouldBeTrue();
        d!.EntityType.ShouldBe("client");
        d.DisplayName.ShouldBeNull();
    }

    [Test]
    public void EmptyGuidId_is_not_registered()
    {
        var result = SkillResult.SuccessResult(new { ShiftId = Guid.Empty, Name = "x" });

        RecentEntityExtractor.TryExtract("create_shift", result, out var d).ShouldBeFalse();
        d.ShouldBeNull();
    }
}
