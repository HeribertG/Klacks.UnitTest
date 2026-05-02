using Shouldly;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Results;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Domain.DTOs.Filter;
using Klacks.Api.Application.DTOs.Staffs;
using NUnit.Framework;

namespace Klacks.UnitTest.Application.Mappers;

[TestFixture]
public class ClientMapperTests
{
    private ClientMapper _mapper = null!;

    [SetUp]
    public void Setup()
    {
        _mapper = new ClientMapper();
    }

    [Test]
    public void ToListItemResource_ValidClient_MapsBasicProperties()
    {
        // Arrange
        var client = new Client
        {
            Id = Guid.NewGuid(),
            FirstName = "John",
            Name = "Doe",
            Company = "Test Company",
            IdNumber = 12345,
            IsDeleted = false
        };

        // Act
        var result = _mapper.ToListItemResource(client);

        // Assert
        result.ShouldNotBeNull();
        result.FirstName.ShouldBe("John");
        result.Name.ShouldBe("Doe");
        result.Company.ShouldBe("Test Company");
        result.IdNumber.ShouldBe(12345);
        result.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public void ToListItemResources_MultipleClients_MapsAll()
    {
        // Arrange
        var clients = new List<Client>
        {
            new Client { Id = Guid.NewGuid(), FirstName = "John", Name = "Doe" },
            new Client { Id = Guid.NewGuid(), FirstName = "Jane", Name = "Smith" },
            new Client { Id = Guid.NewGuid(), FirstName = "Bob", Name = "Wilson" }
        };

        // Act
        var result = _mapper.ToListItemResources(clients);

        // Assert
        result.Count().ShouldBe(3);
        result[0].FirstName.ShouldBe("John");
        result[1].FirstName.ShouldBe("Jane");
        result[2].FirstName.ShouldBe("Bob");
    }

    [Test]
    public void ToResource_ValidClient_MapsAllProperties()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var client = new Client
        {
            Id = clientId,
            FirstName = "John",
            Name = "Doe",
            Company = "Test Company",
            Gender = GenderEnum.Male,
            IdNumber = 12345,
            LegalEntity = false
        };

        // Act
        var result = _mapper.ToResource(client);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(clientId);
        result.FirstName.ShouldBe("John");
        result.Name.ShouldBe("Doe");
        result.Company.ShouldBe("Test Company");
        result.Gender.ShouldBe(GenderEnum.Male);
        result.IdNumber.ShouldBe(12345);
        result.LegalEntity.ShouldBeFalse();
    }

    [Test]
    public void ToEntity_ValidResource_MapsToClient()
    {
        // Arrange
        var resource = new ClientResource
        {
            Id = Guid.NewGuid(),
            FirstName = "John",
            Name = "Doe",
            Company = "Test Company",
            Gender = GenderEnum.Male,
            IdNumber = 12345,
            LegalEntity = false
        };

        // Act
        var result = _mapper.ToEntity(resource);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(resource.Id);
        result.FirstName.ShouldBe("John");
        result.Name.ShouldBe("Doe");
        result.Company.ShouldBe("Test Company");
        result.Gender.ShouldBe(GenderEnum.Male);
        result.IdNumber.ShouldBe(12345);
        result.LegalEntity.ShouldBeFalse();
    }

    [Test]
    public void ToTruncatedClient_ValidPagedResult_MapsCorrectly()
    {
        // Arrange
        var clients = new List<Client>
        {
            new Client { Id = Guid.NewGuid(), FirstName = "John", Name = "Doe" },
            new Client { Id = Guid.NewGuid(), FirstName = "Jane", Name = "Smith" }
        };

        var pagedResult = new PagedResult<Client>
        {
            Items = clients,
            TotalCount = 50,
            PageNumber = 1,
            PageSize = 10
        };

        // Act
        var result = _mapper.ToTruncatedClient(pagedResult);

        // Assert
        result.ShouldNotBeNull();
        result.Clients.Count().ShouldBe(2);
        result.MaxItems.ShouldBe(50);
        result.MaxPages.ShouldBe(5);
        result.CurrentPage.ShouldBe(1);
    }

    [Test]
    public void ToTruncatedResource_ValidTruncatedClient_MapsCorrectly()
    {
        // Arrange
        var truncatedClient = new TruncatedClient
        {
            Clients = new List<Client>
            {
                new Client { Id = Guid.NewGuid(), FirstName = "John", Name = "Doe" },
                new Client { Id = Guid.NewGuid(), FirstName = "Jane", Name = "Smith" }
            },
            Editor = "admin",
            LastChange = DateTime.UtcNow,
            MaxItems = 100,
            MaxPages = 10,
            CurrentPage = 1,
            FirstItemOnPage = 1
        };

        // Act
        var result = _mapper.ToTruncatedResource(truncatedClient);

        // Assert
        result.ShouldNotBeNull();
        result.Clients.Count().ShouldBe(2);
        result.Editor.ShouldBe("admin");
        result.MaxItems.ShouldBe(100);
        result.MaxPages.ShouldBe(10);
    }

    [Test]
    public void ToImageResource_ValidClientImage_MapsWithBase64Conversion()
    {
        // Arrange
        var imageData = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var clientImage = new ClientImage
        {
            Id = Guid.NewGuid(),
            ImageData = imageData,
            ContentType = "image/png"
        };

        // Act
        var result = _mapper.ToImageResource(clientImage);

        // Assert
        result.ShouldNotBeNull();
        result.ImageData.ShouldBe(Convert.ToBase64String(imageData));
        result.ContentType.ShouldBe("image/png");
    }

    [Test]
    public void ToImageResource_NullImageData_ReturnsEmptyString()
    {
        // Arrange
        var clientImage = new ClientImage
        {
            Id = Guid.NewGuid(),
            ImageData = null!,
            ContentType = "image/png"
        };

        // Act
        var result = _mapper.ToImageResource(clientImage);

        // Assert
        result.ImageData.ShouldBeEmpty();
    }

    [Test]
    public void ToImageEntity_ValidResource_MapsWithBase64Decoding()
    {
        // Arrange
        var imageData = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var resource = new ClientImageResource
        {
            ImageData = Convert.ToBase64String(imageData),
            ContentType = "image/png"
        };

        // Act
        var result = _mapper.ToImageEntity(resource);

        // Assert
        result.ShouldNotBeNull();
        result.ImageData.ShouldBeEquivalentTo(imageData);
        result.ContentType.ShouldBe("image/png");
    }

    [Test]
    public void FromSummary_ValidSummary_MapsToClientResource()
    {
        // Arrange
        var summary = new ClientSummary
        {
            FirstName = "John",
            LastName = "Doe",
            Company = "Test Company",
            Gender = GenderEnum.Male,
            IdNumber = "12345",
            DateOfBirth = new DateOnly(1990, 5, 15),
            IsActive = true
        };

        // Act
        var result = _mapper.FromSummary(summary);

        // Assert
        result.ShouldNotBeNull();
        result.FirstName.ShouldBe("John");
        result.Name.ShouldBe("Doe");
        result.Company.ShouldBe("Test Company");
        result.Gender.ShouldBe(GenderEnum.Male);
        result.IdNumber.ShouldBe(12345);
        result.IsDeleted.ShouldBeFalse();
    }

    [Test]
    public void FromSummary_InvalidIdNumber_ReturnsZero()
    {
        // Arrange
        var summary = new ClientSummary
        {
            FirstName = "John",
            LastName = "Doe",
            IdNumber = "not-a-number"
        };

        // Act
        var result = _mapper.FromSummary(summary);

        // Assert
        result.IdNumber.ShouldBe(0);
    }

    [Test]
    public void ToGroupItemResource_NullGroup_ReturnsEmptyStrings()
    {
        // Arrange
        var groupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Group = null
        };

        // Act
        var result = _mapper.ToGroupItemResource(groupItem);

        // Assert
        result.GroupName.ShouldBeEmpty();
        result.Description.ShouldBeEmpty();
    }

    [Test]
    public void ToGroupItemResource_ValidGroupItem_MapsWithGroupInfo()
    {
        // Arrange
        var group = new Group { Name = "Test Group", Description = "Test Description" };
        var groupItem = new GroupItem
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Group = group
        };

        // Act
        var result = _mapper.ToGroupItemResource(groupItem);

        // Assert
        result.ShouldNotBeNull();
        result.GroupId.ShouldBe(groupItem.GroupId);
        result.GroupName.ShouldBe("Test Group");
        result.Description.ShouldBe("Test Description");
    }
}
