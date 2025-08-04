using AutoMapper;
using Klacks.Api.BasicScriptInterpreter;
using Klacks.Api.Commands;
using Klacks.Api.Datas;
using Klacks.Api.Handlers.Groups;
using Klacks.Api.Interfaces;
using Klacks.Api.Interfaces.Domains;
using NSubstitute;
using Klacks.Api.Models.Associations;
using Klacks.Api.Models.Staffs;
using Klacks.Api.Repositories;
using Klacks.Api.Resources.Associations;
using Klacks.Api.Resources.Filter;
using Klacks.Api.Resources.Settings;
using Klacks.Api.Resources.Filter;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics; // F�r InMemoryEventId
using Microsoft.Extensions.Logging;
using UnitTest.FakeData;
using UnitTest.Helper;

namespace UnitTest.Repository;

[TestFixture]
internal class GoupTests
{
    private IHttpContextAccessor _httpContextAccessor = null!;
    private TruncatedClient _truncatedClient = null!;
    private DataBaseContext dbContext = null!;
    private ILogger<PostCommandHandler> _logger = null!;
    private ILogger<UnitOfWork> _unitOfWorkLogger = null!;
    private ILogger<Group> _groupLogger = null!;
    private IMapper _mapper = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groupClient = null!;
    private IGroupVisibilityService _groupVisibility = null!; // HINZUGEF�GT

    [Test]
    public async Task PostGroup_Ok()
    {
        //Arrange
        var options = new DbContextOptionsBuilder<DataBaseContext>()
          .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
          .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)) // Ignoriere Transaktionswarnung
          .Options;

        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();
        DataSeed(_truncatedClient);

        // Use real domain services for proper filtering behavior in integration tests
        var clientFilterService = new Klacks.Api.Services.Clients.ClientFilterService();
        var membershipFilterService = new Klacks.Api.Services.Clients.ClientMembershipFilterService(dbContext);
        var searchService = new Klacks.Api.Services.Clients.ClientSearchService();
        var sortingService = new Klacks.Api.Services.Clients.ClientSortingService();
        
        var clientRepository = new ClientRepository(dbContext, new MacroEngine(), _groupClient, _groupVisibility,
            clientFilterService, membershipFilterService, searchService, sortingService);
        var groupRepository = new GroupRepository(dbContext, _groupVisibility, _groupLogger);
        var unitOfWork = new UnitOfWork(dbContext, _unitOfWorkLogger);
        var group = await CreateGroupAsync(1, clientRepository);
        var command = new PostCommand<GroupResource>(group);
        var handler = new PostCommandHandler(_mapper, groupRepository, unitOfWork, _logger);

        //Act
        var result = await handler.Handle(command, default);

        //Assert
        result.Should().NotBeNull();
        result!.GroupItems.Count.Should().Be(7);
    }

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _logger = Substitute.For<ILogger<PostCommandHandler>>();
        _unitOfWorkLogger = Substitute.For<ILogger<UnitOfWork>>();
        _groupLogger = Substitute.For<ILogger<Group>>();
        _truncatedClient = FakeData.Clients.TruncatedClient();
        _mapper = TestHelper.GetFullMapperConfiguration().CreateMapper();

        _groupClient = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _groupClient.GetAllClientIdsFromGroupAndSubgroups(Arg.Any<Guid>())
                   .Returns(Task.FromResult(new List<Guid>()));
        _groupClient.GetAllClientIdsFromGroupsAndSubgroupsFromList(Arg.Any<List<Guid>>())
                   .Returns(Task.FromResult(new List<Guid>()));

        _groupVisibility = Substitute.For<IGroupVisibilityService>();
        _groupVisibility.IsAdmin().Returns(Task.FromResult(true)); // F�r Tests als Admin setzen
        _groupVisibility.ReadVisibleRootIdList().Returns(Task.FromResult(new List<Guid>()));
    }

    [TearDown]
    public void TearDown()
    {
        dbContext.Database.EnsureDeleted();
        dbContext.Dispose();
    }

    private async Task<GroupResource> CreateGroupAsync(int index, ClientRepository clientRepository)
    {
        var idNumberList = new List<int>()
                                       { 15205,
                                         15215,
                                         15216,
                                         15217,
                                         15220,
                                         15229,
                                         15403 };
        var filter = Clients.Filter();
        filter.Male = true;
        filter.Female = false;
        filter.LegalEntity = false;
        var handler = new Klacks.Api.Handlers.Clients.GetTruncatedListQueryHandler(clientRepository, _mapper);

        var group = new GroupResource();
        group.Name = $"FakeName{index}";
        group.ValidFrom = DateTime.Now.AddMonths(index * -1);
        group.Description = "Lorem ipsum dolor sit amet, consetetur sadipscing elitr, sed diam nonumy eirmod tempor invidunt ut labore et dolore magna aliquyam erat, sed diam voluptua. At vero eos et accusam et justo duo dolores et ea rebum. Stet clita kasd gubergren, no sea takimata sanctus est Lorem ipsum dolor sit amet. Lorem ipsum dolor sit amet, consetetur sadipscing elitr, sed diam nonumy eirmod tempor invidunt ut labore et dolore magna aliquyam erat, sed diam voluptua. At vero eos et accusam et justo duo dolores et ea rebum. Stet clita kasd gubergren, no sea takimata sanctus est Lorem ipsum dolor sit amet.";
        group.GroupItems = new List<GroupItemResource>();

        foreach (var id in idNumberList)
        {
            filter.SearchString = id.ToString();
            var command = new Klacks.Api.Queries.Clients.GetTruncatedListQuery(filter);
            var result = await handler.Handle(command, default);
            if (result != null && result.Clients != null && result.Clients.Any())
            {
                var item = new GroupItemResource() { ClientId = result.Clients.First().Id };
                group.GroupItems.Add(item);
            }
        }

        return group;
    }

    private void DataSeed(TruncatedClient truncated)
    {
        var clients = new List<Client>();
        var addresses = new List<Address>();
        var memberships = new List<Membership>();
        var communications = new List<Communication>();
        var annotations = new List<Annotation>();

        foreach (var item in truncated.Clients!)
        {
            if (item.Addresses.Any())
            {
                foreach (var address in item.Addresses)
                {
                    addresses.Add(address);
                }
            }

            if (item.Communications.Any())
            {
                foreach (var communication in item.Communications)
                {
                    communications.Add(communication);
                }
            }

            if (item.Annotations.Any())
            {
                foreach (var annotation in item.Annotations)
                {
                    annotations.Add(annotation);
                }
            }

            if (item.Membership != null)
            {
                memberships.Add(item.Membership);
            }

            item.Addresses.Clear();
            item.Annotations.Clear();
            item.Communications.Clear();
            clients.Add(item);
        }

        dbContext.Client.AddRange(clients);
        dbContext.Address.AddRange(addresses);
        dbContext.Membership.AddRange(memberships);
        dbContext.Communication.AddRange(communications);
        dbContext.Annotation.AddRange(annotations);

        dbContext.SaveChanges();
    }
}