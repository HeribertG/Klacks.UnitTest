using AutoMapper;
using Klacks.Api.Application.AutoMapper;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Clients;
using NSubstitute;
using MediatR;

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
        clientRepositoryMock.Truncated(filter).Returns(Task.FromResult(FakeData.Clients.TruncatedClient()));
        var query = new GetTruncatedListQuery(filter);
        var handler = new GetTruncatedListQueryHandler(clientRepositoryMock, _mapper);

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
            cfg.AddProfile<MappingProfile>();
        });

        _mapper = config.CreateMapper();
        _mediator = Substitute.For<IMediator>();
    }
}
