using AutoMapper;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Filter;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using NSubstitute;
using UnitTest.Mocks;

namespace UnitTest.GroupTrees;

[TestFixture]
public class GroupTreeTests
{
    private DataBaseContext _dbContext = null!;
    private MockGroupRepository _groupRepository = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;
    private IMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<MockUnitOfWork> _unitOfWorkLogger = null!;
    private IMediator _mediator = null!;

    // Handler
    private PostCommandHandler _postHandler = null!;
    private PutCommandHandler _putHandler = null!;
    private DeleteCommandHandler _deleteHandler = null!;
    private GetQueryHandler _getHandler = null!;
    private GetPathToNodeQueryHandler _getPathHandler = null!;
    private MoveGroupNodeCommandHandler _moveHandler = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = Substitute.For<HttpContext>();
        var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "TestUser") }));
        httpContext.User.Returns(claimsPrincipal);
        _httpContextAccessor.HttpContext.Returns(httpContext);

        _dbContext = new DataBaseContext(options, _httpContextAccessor);
        _dbContext.Database.EnsureCreated();

        _unitOfWorkLogger = Substitute.For<ILogger<MockUnitOfWork>>();
        var deleteHandlerLogger = Substitute.For<ILogger<DeleteCommandHandler>>();
        var postHandlerLogger = Substitute.For<ILogger<PostCommandHandler>>();
        var putHandlerLogger = Substitute.For<ILogger<PutCommandHandler>>();
        var moveHandlerLogger = Substitute.For<ILogger<MoveGroupNodeCommandHandler>>();

        _groupRepository = new MockGroupRepository(_dbContext);
        _unitOfWork = new MockUnitOfWork(_dbContext, _unitOfWorkLogger);

        var mapperConfig = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Group, GroupResource>().ReverseMap();
            cfg.CreateMap<GroupResource, Group>();
            cfg.CreateMap<GroupItemResource, GroupItem>().ReverseMap();

            cfg.CreateMap<TruncatedGroup, TruncatedGroupResource>();
        });
        _mapper = mapperConfig.CreateMapper();

        _mediator = Substitute.For<IMediator>();

        _postHandler = new PostCommandHandler(
            _groupRepository,
            _mapper,
            _unitOfWork,
            postHandlerLogger);

        _putHandler = new PutCommandHandler(
            _groupRepository,
            _mapper,
            _unitOfWork,
            putHandlerLogger);

        _deleteHandler = new DeleteCommandHandler(
            _groupRepository,
            _mapper,
            _unitOfWork,
            deleteHandlerLogger);

        _getHandler = new GetQueryHandler(_groupRepository, _mapper);

        _getPathHandler = new GetPathToNodeQueryHandler(_groupRepository, _mapper);

        _moveHandler = new MoveGroupNodeCommandHandler(
            _groupRepository,
            _mapper,
            _unitOfWork,
            moveHandlerLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.EnsureDeleted();
        _dbContext.Dispose();
    }

    [Test]
    public async Task CreateRootNode_ShouldCreateGroup()
    {
        // Arrange
        var groupResource = new GroupResource
        {
            Name = "Root Group",
            Description = "Root Group Description",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItemResource>()
        };

        var command = new PostCommand<GroupResource>(groupResource);

        // Act
        var result = await _postHandler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Root Group"));
        Assert.That(result.Description, Is.EqualTo("Root Group Description"));

        var groupInDb = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == result.Id);
        Assert.That(groupInDb, Is.Not.Null);
        Assert.That(groupInDb!.Name, Is.EqualTo("Root Group"));
        Assert.That(groupInDb.Description, Is.EqualTo("Root Group Description"));
        Assert.That(groupInDb.Lft < groupInDb.Rgt);
    }

    [Test]
    public async Task CreateChildNode_ShouldCreateGroupWithParentRelationship()
    {
        // Arrange 
        var rootGroup = new Group
        {
            Name = "Root Group",
            Description = "Root Group Description",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 1,
            Rgt = 2,
            Parent = null,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(rootGroup);
        await _dbContext.SaveChangesAsync();


        rootGroup.Root = rootGroup.Id;
        _dbContext.Group.Update(rootGroup);
        await _dbContext.SaveChangesAsync();

        var childGroupResource = new GroupResource
        {
            Name = "Child Group",
            Description = "Child Group Description",
            ValidFrom = DateTime.Now,
            Parent = rootGroup.Id,
            GroupItems = new List<GroupItemResource>()
        };

        var command = new PostCommand<GroupResource>(childGroupResource);

        // Act
        var result = await _postHandler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Child Group"));
        Assert.That(result.Description, Is.EqualTo("Child Group Description"));
        Assert.That(result.Parent, Is.EqualTo(rootGroup.Id));

        var childInDb = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == result.Id);
        Assert.That(childInDb, Is.Not.Null);
        Assert.That(childInDb!.Parent, Is.EqualTo(rootGroup.Id));
        Assert.That(childInDb.Root, Is.EqualTo(rootGroup.Id));

        var rootInDb = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == rootGroup.Id);
        Assert.That(rootInDb!.Rgt > 2);
    }

    [Test]
    public async Task UpdateGroup_ShouldUpdateGroupProperties()
    {
        // Arrange 
        var group = new Group
        {
            Name = "Original Name",
            Description = "Original Description",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 1,
            Rgt = 2,
            Parent = null,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(group);
        await _dbContext.SaveChangesAsync();

        group.Root = group.Id;
        _dbContext.Group.Update(group);
        await _dbContext.SaveChangesAsync();

        var updateResource = new GroupResource
        {
            Id = group.Id,
            Name = "Updated Name",
            Description = "Updated Description",
            ValidFrom = group.ValidFrom,
            ValidUntil = DateTime.Now.AddYears(1),
            GroupItems = new List<GroupItemResource>()
        };

        var command = new PutCommand<GroupResource>(updateResource);

        // Act
        var result = await _putHandler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Updated Name"));
        Assert.That(result.Description, Is.EqualTo("Updated Description"));
        Assert.That(result.ValidUntil, Is.Not.Null);

        var updatedInDb = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == group.Id);
        Assert.That(updatedInDb, Is.Not.Null);
        Assert.That(updatedInDb!.Name, Is.EqualTo("Updated Name"));
        Assert.That(updatedInDb.Description, Is.EqualTo("Updated Description"));
        Assert.That(updatedInDb.ValidUntil, Is.Not.Null);
    }

    [Test]
    public async Task MoveNode_ShouldUpdateParentRelationship()
    {
        // Arrange 
        var rootGroup = new Group
        {
            Name = "Root Group",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 1,
            Rgt = 6,
            Parent = null,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(rootGroup);
        await _dbContext.SaveChangesAsync();

        rootGroup.Root = rootGroup.Id;
        _dbContext.Group.Update(rootGroup);
        await _dbContext.SaveChangesAsync();

        var child1Group = new Group
        {
            Name = "Child 1",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 2,
            Rgt = 3,
            Parent = rootGroup.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser"
        };

        var child2Group = new Group
        {
            Name = "Child 2",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 4,
            Rgt = 5,
            Parent = rootGroup.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.AddRange(child1Group, child2Group);
        await _dbContext.SaveChangesAsync();

        // Act 
        var command = new MoveGroupNodeCommand(child1Group.Id, child2Group.Id);
        var result = await _moveHandler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);

        var child1InDb = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == child1Group.Id);
        Assert.That(child1InDb, Is.Not.Null);
        Assert.That(child1InDb!.Parent, Is.EqualTo(child2Group.Id));
    }

    [Test]
    public async Task DeleteNode_ShouldMarkNodeAsDeleted()
    {
        // Arrange - Erstelle Root und Child-Knoten
        var rootGroup = new Group
        {
            Name = "Root Group",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 1,
            Rgt = 4,
            Parent = null,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(rootGroup);
        await _dbContext.SaveChangesAsync();

        rootGroup.Root = rootGroup.Id;
        _dbContext.Group.Update(rootGroup);
        await _dbContext.SaveChangesAsync();

        var childGroup = new Group
        {
            Name = "Child Group",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 2,
            Rgt = 3,
            Parent = rootGroup.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser",
            IsDeleted = false
        };

        _dbContext.Group.Add(childGroup);
        await _dbContext.SaveChangesAsync();

        // Act
        var command = new DeleteCommand<GroupResource>(childGroup.Id);
        var result = await _deleteHandler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);

        // Überprüfe, dass der Knoten als gelöscht markiert ist
        var childInDb = await _dbContext.Group
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == childGroup.Id);

        Assert.That(childInDb, Is.Not.Null);
        Assert.That(childInDb!.IsDeleted, Is.True);
        Assert.That(childInDb.DeletedTime, Is.Not.Null);
    }

    [Test]
    public async Task GetGroup_ShouldReturnCorrectGroup()
    {
        // Arrange - Erstelle einen Knoten
        var group = new Group
        {
            Name = "Test Group",
            Description = "Test Description",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 1,
            Rgt = 2,
            Parent = null,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(group);
        await _dbContext.SaveChangesAsync();

        group.Root = group.Id;
        _dbContext.Group.Update(group);
        await _dbContext.SaveChangesAsync();

        var query = new GetQuery<GroupResource>(group.Id);

        // Act
        var result = await _getHandler.Handle(query, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Id, Is.EqualTo(group.Id));
        Assert.That(result.Name, Is.EqualTo("Test Group"));
        Assert.That(result.Description, Is.EqualTo("Test Description"));
    }

    [Test]
    public async Task GetPath_ShouldReturnCorrectPath()
    {
        // Arrange - Erstelle eine Hierarchie
        var rootGroup = new Group
        {
            Name = "Root",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 1,
            Rgt = 6,
            Parent = null,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(rootGroup);
        await _dbContext.SaveChangesAsync();

        rootGroup.Root = rootGroup.Id;
        _dbContext.Group.Update(rootGroup);
        await _dbContext.SaveChangesAsync();

        var childGroup = new Group
        {
            Name = "Child",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 2,
            Rgt = 5,
            Parent = rootGroup.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(childGroup);
        await _dbContext.SaveChangesAsync();

        var grandChildGroup = new Group
        {
            Name = "GrandChild",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 3,
            Rgt = 4,
            Parent = childGroup.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(grandChildGroup);
        await _dbContext.SaveChangesAsync();

        var query = new GetPathToNodeQuery(grandChildGroup.Id);

        // Act
        var result = await _getPathHandler.Handle(query, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(3)); // Root, Child, GrandChild
        Assert.That(result[0].Name, Is.EqualTo("Root"));
        Assert.That(result[1].Name, Is.EqualTo("Child"));
        Assert.That(result[2].Name, Is.EqualTo("GrandChild"));

        // Überprüfe die Tiefe
        Assert.That(result[0].Depth, Is.EqualTo(0));
        Assert.That(result[1].Depth, Is.EqualTo(1));
        Assert.That(result[2].Depth, Is.EqualTo(2));
    }

    [Test]
    public async Task CreateTreeHierarchy_ShouldBuildCorrectStructure()
    {
        // Root-Knoten erstellen
        var rootResource = new GroupResource
        {
            Name = "Root",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItemResource>()
        };

        var rootCmd = new PostCommand<GroupResource>(rootResource);
        var root = await _postHandler.Handle(rootCmd, CancellationToken.None);

        // Kind 1 erstellen
        var child1Resource = new GroupResource
        {
            Name = "Child1",
            ValidFrom = DateTime.Now,
            Parent = root.Id,
            GroupItems = new List<GroupItemResource>()
        };

        var child1Cmd = new PostCommand<GroupResource>(child1Resource);
        var child1 = await _postHandler.Handle(child1Cmd, CancellationToken.None);

        // Kind 2 erstellen
        var child2Resource = new GroupResource
        {
            Name = "Child2",
            ValidFrom = DateTime.Now,
            Parent = root.Id,
            GroupItems = new List<GroupItemResource>()
        };

        var child2Cmd = new PostCommand<GroupResource>(child2Resource);
        var child2 = await _postHandler.Handle(child2Cmd, CancellationToken.None);

        // Enkelkind erstellen
        var grandChild1Resource = new GroupResource
        {
            Name = "GrandChild1",
            ValidFrom = DateTime.Now,
            Parent = child1.Id,
            GroupItems = new List<GroupItemResource>()
        };

        var grandChild1Cmd = new PostCommand<GroupResource>(grandChild1Resource);
        var grandChild1 = await _postHandler.Handle(grandChild1Cmd, CancellationToken.None);

        // Datenbank-Objekte abrufen, um interne Werte zu prüfen
        var rootNode = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == root.Id);
        var child1Node = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == child1.Id);
        var child2Node = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == child2.Id);
        var grandChild1Node = await _dbContext.Group.FirstOrDefaultAsync(g => g.Id == grandChild1.Id);

        // Überprüfe Parent-Beziehungen
        Assert.That(child1Node!.Parent, Is.EqualTo(rootNode!.Id));
        Assert.That(child2Node!.Parent, Is.EqualTo(rootNode.Id));
        Assert.That(grandChild1Node!.Parent, Is.EqualTo(child1Node.Id));

        // Überprüfe Root-Werte
        Assert.That(rootNode.Root, Is.EqualTo(rootNode.Id));
        Assert.That(child1Node.Root, Is.EqualTo(rootNode.Id));
        Assert.That(child2Node.Root, Is.EqualTo(rootNode.Id));
        Assert.That(grandChild1Node.Root, Is.EqualTo(rootNode.Id));

        // Überprüfe nur die Beziehungen der Lft/Rgt-Werte, nicht die exakten Werte
        Assert.That(rootNode.Lft < rootNode.Rgt);
        Assert.That(child1Node.Lft > rootNode.Lft);
        Assert.That(child1Node.Rgt < rootNode.Rgt);
        Assert.That(child2Node.Lft > rootNode.Lft);
        Assert.That(child2Node.Rgt < rootNode.Rgt);
        Assert.That(grandChild1Node.Lft > child1Node.Lft);
        Assert.That(grandChild1Node.Rgt < child1Node.Rgt);
    }

    [Test]
    public async Task GetNodeDepth_ShouldReturnCorrectDepth()
    {
        // Arrange - Erstelle eine Hierarchie
        var rootGroup = new Group
        {
            Name = "Root",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 1,
            Rgt = 8,
            Parent = null,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(rootGroup);
        await _dbContext.SaveChangesAsync();

        rootGroup.Root = rootGroup.Id;
        _dbContext.Group.Update(rootGroup);
        await _dbContext.SaveChangesAsync();

        var child1 = new Group
        {
            Name = "Child1",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 2,
            Rgt = 5,
            Parent = rootGroup.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(child1);
        await _dbContext.SaveChangesAsync();

        var grandChild = new Group
        {
            Name = "GrandChild",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 3,
            Rgt = 4,
            Parent = child1.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(grandChild);
        await _dbContext.SaveChangesAsync();

        var child2 = new Group
        {
            Name = "Child2",
            ValidFrom = DateTime.Now,
            GroupItems = new List<GroupItem>(),
            Lft = 6,
            Rgt = 7,
            Parent = rootGroup.Id,
            Root = rootGroup.Id,
            CurrentUserCreated = "TestUser"
        };

        _dbContext.Group.Add(child2);
        await _dbContext.SaveChangesAsync();

        // Act
        var rootDepth = await _groupRepository.GetNodeDepth(rootGroup.Id);
        var child1Depth = await _groupRepository.GetNodeDepth(child1.Id);
        var grandChildDepth = await _groupRepository.GetNodeDepth(grandChild.Id);

        // Assert
        Assert.That(rootDepth, Is.EqualTo(0));
        Assert.That(child1Depth, Is.EqualTo(1));
        Assert.That(grandChildDepth, Is.EqualTo(2));
    }
}