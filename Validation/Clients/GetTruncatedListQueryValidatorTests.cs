using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Application.Validation.Clients;

namespace Klacks.UnitTest.Validation.Clients
{
    [TestFixture]
    internal class GetTruncatedListQueryValidatorTests
    {
        [Test]
        public async Task GetTruncatedListQueryHandler_Pagination_Ok()
        {
            //Arrange
            var returns = FakeData.Clients.TruncatedClient();
            var filter = FakeData.Clients.Filter();

            var clientRepositoryMock = Substitute.For<IClientRepository>();
            var query = new GetTruncatedListQuery(filter);
            var validator = new GetTruncatedListQueryValidator(clientRepositoryMock);

            //Act
            var result = await validator.ValidateAsync(query);

            //Assert
            Assert.That(result.IsValid, Is.True);
        }

        [SetUp]
        public void Setup()
        {
        }
    }
}
