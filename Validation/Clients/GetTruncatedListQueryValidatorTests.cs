using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Application.Validation.Clients;

namespace UnitTest.Validation.Clients
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
            var pagedResult = new Klacks.Api.Domain.Models.Results.PagedResult<Klacks.Api.Domain.Models.Staffs.Client>
            {
                Items = new List<Klacks.Api.Domain.Models.Staffs.Client>(),
                TotalCount = 0,
                PageNumber = 1,
                PageSize = 10
            };
            clientRepositoryMock.GetFilteredClients(Arg.Any<Klacks.Api.Domain.Models.Filters.ClientFilter>(), Arg.Any<Klacks.Api.Domain.Models.Filters.PaginationParams>())
                .Returns(Task.FromResult(pagedResult));
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
