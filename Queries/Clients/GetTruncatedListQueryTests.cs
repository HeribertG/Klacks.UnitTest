using AutoMapper;
using Klacks.Api.Application.AutoMapper;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Clients;
using NSubstitute;
using MediatR;
using Microsoft.Extensions.Logging;

namespace UnitTest.Queries.Clients;

internal class GetTruncatedListQueryTests
{
    private IMapper _mapper = null!;
    private IMediator _mediator = null!;

    [Test]
    public async Task GetTruncatedListQueryHandler_Ok()
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
        
        var lastChangeMetaData = new Klacks.Api.Domain.Models.Filters.LastChangeMetaData
        {
            Author = "TestUser",
            LastChangesDate = DateTime.Now
        };
        clientRepositoryMock.LastChangeMetaData()
            .Returns(Task.FromResult(lastChangeMetaData));
        var query = new GetTruncatedListQuery(filter);
        var logger = Substitute.For<ILogger<GetTruncatedListQueryHandler>>();
        var handler = new GetTruncatedListQueryHandler(clientRepositoryMock, _mapper, logger);

        //Act
        var result = await handler.Handle(query, default);
        //Assert
        result.Should().NotBeNull();
        //Assert.Pass();
    }

    [SetUp]
    public void Setup()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddMaps(typeof(ClientMappingProfile).Assembly);
        });

        _mapper = config.CreateMapper();
        _mediator = Substitute.For<IMediator>();
    }
}
