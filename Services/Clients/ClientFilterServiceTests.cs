using Klacks.Api.Domain.Services.Clients;

namespace UnitTest.Services.Clients
{
    [TestFixture]
    public class ClientFilterServiceTests
    {
        private ClientFilterService _service;
        private List<Client> _testClients;

        [SetUp]
        public void SetUp()
        {
            _service = new ClientFilterService();
            _testClients = CreateTestClients();
        }

        private List<Client> CreateTestClients()
        {
            return new List<Client>
            {
                new Client { Id = Guid.NewGuid(), Gender = GenderEnum.Male, LegalEntity = false, Name = "Male Client" },
                new Client { Id = Guid.NewGuid(), Gender = GenderEnum.Female, LegalEntity = false, Name = "Female Client" },
                new Client { Id = Guid.NewGuid(), Gender = GenderEnum.Intersexuality, LegalEntity = false, Name = "Intersex Client" },
                new Client { Id = Guid.NewGuid(), Gender = GenderEnum.Male, LegalEntity = true, Name = "Legal Entity" },
                new Client { Id = Guid.NewGuid(), Gender = GenderEnum.Female, LegalEntity = true, Name = "Another Legal Entity" }
            };
        }
        
        [Test]
        public void CreateGenderList_WithAllOptionsTrue_ReturnsAllGenderEnums()
        {
            // Arrange
            bool? male = true;
            bool? female = true;
            bool? legalEntity = true;
            bool? intersexuality = true;

            // Act
            var result = _service.CreateGenderList(male, female, legalEntity, intersexuality);

            // Assert
            result.Should().HaveCount(3); // Only 3 genders, LegalEntity is not a gender
            result.Should().Contain((int)GenderEnum.Male);
            result.Should().Contain((int)GenderEnum.Female);
            result.Should().Contain((int)GenderEnum.Intersexuality);
            // LegalEntity should not be in the gender list
        }

        [Test]
        public void CreateGenderList_WithOnlyIntersexualityTrue_ReturnsOnlyIntersexuality()
        {
            // Arrange
            bool? male = false;
            bool? female = false;
            bool? legalEntity = false;
            bool? intersexuality = true;

            // Act
            var result = _service.CreateGenderList(male, female, legalEntity, intersexuality);

            // Assert
            result.Should().HaveCount(1);
            result.Should().Contain((int)GenderEnum.Intersexuality);
        }

        [Test]
        public void CreateGenderList_WithAllOptionsFalse_ReturnsEmptyArray()
        {
            // Arrange
            bool? male = false;
            bool? female = false;
            bool? legalEntity = false;
            bool? intersexuality = false;

            // Act
            var result = _service.CreateGenderList(male, female, legalEntity, intersexuality);

            // Assert
            result.Should().BeEmpty();
        }

        [Test]
        public void ApplyEntityTypeFilter_AllTypesTrue_ReturnsAllClients()
        {
            // Arrange
            var query = _testClients.AsQueryable();

            // Act
            var result = _service.ApplyEntityTypeFilter(query, true, true, true);

            // Assert
            result.Should().HaveCount(5);
        }

        [Test]
        public void ApplyEntityTypeFilter_AllTypesFalse_ReturnsEmpty()
        {
            // Arrange
            var query = _testClients.AsQueryable();

            // Act
            var result = _service.ApplyEntityTypeFilter(query, false, false, false);

            // Assert
            result.Should().BeEmpty();
        }
                
        [Test]
        public void CreateGenderList_WithNullValues_ReturnsEmptyArray()
        {
            // Arrange
            bool? male = null;
            bool? female = null;
            bool? legalEntity = null;
            bool? intersexuality = null;

            // Act
            var result = _service.CreateGenderList(male, female, legalEntity, intersexuality);

            // Assert
            result.Should().BeEmpty();
        }

        [Test]
        public void CreateGenderList_MixedTrueAndFalse_ReturnsOnlyTrueValues()
        {
            // Arrange
            bool? male = true;
            bool? female = false;
            bool? legalEntity = true;
            bool? intersexuality = false;

            // Act
            var result = _service.CreateGenderList(male, female, legalEntity, intersexuality);

            // Assert
            result.Should().HaveCount(1); // Only Male, LegalEntity is not a gender
            result.Should().Contain((int)GenderEnum.Male);
            result.Should().NotContain((int)GenderEnum.Female);
            result.Should().NotContain((int)GenderEnum.Intersexuality);
        }
    }
}