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
}
