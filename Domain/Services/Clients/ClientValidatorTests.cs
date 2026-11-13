using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Clients;
using NUnit.Framework;

namespace Klacks.UnitTest.Domain.Services.Clients;

[TestFixture]
public class ClientValidatorTests
{
    private ClientValidator _validator = null!;

    [SetUp]
    public void Setup()
    {
        _validator = new ClientValidator();
    }

    [Test]
    public void EnsureUniqueQualifications_RemovesDuplicateQualificationIds()
    {
        var qualificationId = Guid.NewGuid();
        var qualifications = new List<ClientQualification>
        {
            new() { Id = Guid.NewGuid(), QualificationId = qualificationId, Level = QualificationLevel.Basic },
            new() { Id = Guid.NewGuid(), QualificationId = qualificationId, Level = QualificationLevel.Expert },
            new() { Id = Guid.NewGuid(), QualificationId = Guid.NewGuid(), Level = QualificationLevel.Basic },
        };

        _validator.EnsureUniqueQualifications(qualifications);

        qualifications.Count.ShouldBe(2);
        qualifications.Count(q => q.QualificationId == qualificationId).ShouldBe(1);
    }

    [Test]
    public void RemoveEmptyCollections_DropsQualificationsWithoutQualificationId()
    {
        var client = new Client { Id = Guid.NewGuid(), FirstName = "John", Name = "Doe" };
        client.Qualifications.Add(new ClientQualification { QualificationId = Guid.Empty, Level = QualificationLevel.Basic });
        client.Qualifications.Add(new ClientQualification { QualificationId = Guid.NewGuid(), Level = QualificationLevel.Basic });

        _validator.RemoveEmptyCollections(client);

        client.Qualifications.Count.ShouldBe(1);
        client.Qualifications.All(q => q.QualificationId != Guid.Empty).ShouldBeTrue();
    }

    [Test]
    public void EnsureUniqueGroupItems_RemovesADoubledRegularMembership_AndLeavesBorrowedDaysAndScenarioRowsOfThatGroup()
    {
        var groupId = Guid.NewGuid();
        var monday = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var friday = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        var borrowedMonday = new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ValidFrom = monday, ValidUntil = monday, IsTemporary = true };
        var borrowedFriday = new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ValidFrom = friday, ValidUntil = friday, IsTemporary = true };
        var scenarioDay = new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, AnalyseToken = Guid.NewGuid(), IsTemporary = true };
        var groupItems = new List<GroupItem>
        {
            new() { Id = Guid.NewGuid(), GroupId = groupId },
            borrowedMonday,
            new() { Id = Guid.NewGuid(), GroupId = groupId },
            borrowedFriday,
            scenarioDay,
        };

        _validator.EnsureUniqueGroupItems(groupItems);

        groupItems.Count(gi => !gi.IsTemporary).ShouldBe(1);
        groupItems.ShouldContain(borrowedMonday);
        groupItems.ShouldContain(borrowedFriday);
        groupItems.ShouldContain(scenarioDay);
    }
}
