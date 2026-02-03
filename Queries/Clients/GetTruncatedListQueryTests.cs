using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Clients;
using NSubstitute;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.Logging;
using Klacks.Api.Application.Mappers;

namespace Klacks.UnitTest.Queries.Clients;

internal class GetTruncatedListQueryTests
{
    private ClientMapper _clientMapper = null!;
    private FilterMapper _filterMapper = null!;
    private IMediator _mediator = null!;

    [Test]
    public async Task GetTruncatedListQueryHandler_Ok()
    {
        //Arrange
        var returns = FakeData.Clients.TruncatedClient();
        var filter = FakeData.Clients.Filter();

        var clientFilterRepositoryMock = Substitute.For<IClientFilterRepository>();
        var clientRepositoryMock = Substitute.For<IClientRepository>();
        var pagedResult = new Klacks.Api.Domain.Models.Results.PagedResult<Klacks.Api.Domain.Models.Staffs.Client>
        {
            Items = new List<Klacks.Api.Domain.Models.Staffs.Client>(),
            TotalCount = 0,
            PageNumber = 1,
            PageSize = 10
        };
        clientFilterRepositoryMock.GetFilteredClients(Arg.Any<Klacks.Api.Domain.Models.Filters.ClientFilter>(), Arg.Any<Klacks.Api.Domain.Models.Filters.PaginationParams>())
            .Returns(Task.FromResult(pagedResult));

        var lastChangeMetaData = new Klacks.Api.Domain.Models.Filters.LastChangeMetaData
        {
            Author = "TestUser",
            LastChangesDate = DateTime.Now
        };
        clientRepositoryMock.LastChangeMetaData()
            .Returns(Task.FromResult(lastChangeMetaData));
        var query = new GetTruncatedListQuery(filter);
        var logger = Substitute.For<ILogger<GetTruncatedListQueryHandler>>();
        var handler = new GetTruncatedListQueryHandler(clientFilterRepositoryMock, clientRepositoryMock, _clientMapper, _filterMapper, logger);

        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        //Assert.Pass();
    }

    [SetUp]
    public void Setup()
    {
        _clientMapper = new ClientMapper();
        _filterMapper = new FilterMapper();
        _mediator = Substitute.For<IMediator>();
    }
}
