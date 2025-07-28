using AutoMapper;
using Klacks.Api.Commands.Shifts;
using Klacks.Api.Enums;
using Klacks.Api.Exceptions;
using Klacks.Api.Handlers.Shifts;
using Klacks.Api.Interfaces;
using Klacks.Api.Models.Schedules;
using Klacks.Api.Resources.Schedules;
using Klacks.Api.Resources.Associations;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage;

namespace UnitTest.Handlers.Shifts;

[TestFixture]
public class PostCutsCommandHandlerTests
{
    private PostCutsCommandHandler _handler;
    private IMapper _mockMapper;
    private IShiftRepository _mockRepository;
    private IUnitOfWork _mockUnitOfWork;
    private ILogger<PostCutsCommandHandler> _mockLogger;
    private IDbContextTransaction _mockTransaction;

    [SetUp]
    public void SetUp()
    {
        _mockMapper = Substitute.For<IMapper>();
        _mockRepository = Substitute.For<IShiftRepository>();
        _mockUnitOfWork = Substitute.For<IUnitOfWork>();
        _mockLogger = Substitute.For<ILogger<PostCutsCommandHandler>>();
        _mockTransaction = Substitute.For<IDbContextTransaction>();
        
        _handler = new PostCutsCommandHandler(_mockMapper, _mockRepository, _mockUnitOfWork, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _mockTransaction?.Dispose();
    }

    [Test]
    public async Task Handle_ValidCutShifts_CreatesShiftsSuccessfully()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var cutShift1 = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Cut Shift 1",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Groups = new List<SimpleGroupResource>()
        };
        var cutShift2 = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Cut Shift 2",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Groups = new List<SimpleGroupResource>()
        };
        
        var command = new PostCutsCommand(new List<ShiftResource> { cutShift1, cutShift2 });
        
        var mappedShift1 = new Shift 
        { 
            Id = cutShift1.Id, 
            Name = cutShift1.Name,
            Status = ShiftStatus.IsCut,
            OriginalId = originalId
        };
        var mappedShift2 = new Shift 
        { 
            Id = cutShift2.Id, 
            Name = cutShift2.Name,
            Status = ShiftStatus.IsCut,
            OriginalId = originalId
        };
        
        _mockMapper.Map<ShiftResource, Shift>(cutShift1).Returns(mappedShift1);
        _mockMapper.Map<ShiftResource, Shift>(cutShift2).Returns(mappedShift2);
        _mockMapper.Map<List<Shift>, List<ShiftResource>>(Arg.Any<List<Shift>>())
            .Returns(new List<ShiftResource> { cutShift1, cutShift2 });
        
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        await _mockRepository.Received(2).Add(Arg.Any<Shift>());
        await _mockRepository.Received(2).UpdateGroupItems(Arg.Any<Guid>(), Arg.Any<List<Guid>>());
        await _mockUnitOfWork.Received(1).CompleteAsync();
        await _mockUnitOfWork.Received(1).CommitTransactionAsync(_mockTransaction);
    }

    [Test]
    public async Task Handle_ShiftWithoutIsCutStatus_ThrowsInvalidRequestException()
    {
        // Arrange
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Wrong Status Shift",
            Status = ShiftStatus.Original, // Falscher Status
            OriginalId = Guid.NewGuid(),
            Groups = new List<SimpleGroupResource>()
        };
        
        var command = new PostCutsCommand(new List<ShiftResource> { cutShift });
        
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act & Assert
        var act = async () => await _handler.Handle(command, CancellationToken.None);
        
        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("*must have status IsCut*");
        
        await _mockUnitOfWork.Received(1).RollbackTransactionAsync(_mockTransaction);
    }

    [Test]
    public async Task Handle_ShiftWithoutOriginalId_ThrowsInvalidRequestException()
    {
        // Arrange
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "No Original ID Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = null, // Fehlende OriginalId
            Groups = new List<SimpleGroupResource>()
        };
        
        var command = new PostCutsCommand(new List<ShiftResource> { cutShift });
        
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act & Assert
        var act = async () => await _handler.Handle(command, CancellationToken.None);
        
        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("*must have an OriginalId*");
        
        await _mockUnitOfWork.Received(1).RollbackTransactionAsync(_mockTransaction);
    }

    [Test]
    public async Task Handle_MappedShiftWithoutOriginalId_ThrowsInvalidRequestException()
    {
        // Arrange
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Cut Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = Guid.NewGuid(),
            Groups = new List<SimpleGroupResource>()
        };
        
        var command = new PostCutsCommand(new List<ShiftResource> { cutShift });
        
        var mappedShift = new Shift 
        { 
            Id = cutShift.Id, 
            Name = cutShift.Name,
            Status = ShiftStatus.IsCut,
            OriginalId = null // Mapping Problem
        };
        
        _mockMapper.Map<ShiftResource, Shift>(cutShift).Returns(mappedShift);
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act & Assert
        var act = async () => await _handler.Handle(command, CancellationToken.None);
        
        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("*Mapped shift must have an OriginalId*");
        
        await _mockUnitOfWork.Received(1).RollbackTransactionAsync(_mockTransaction);
    }

    [Test]
    public async Task Handle_ShiftsWithGroups_ClearsGroupsBeforeMapping()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var group1 = new SimpleGroupResource { Id = Guid.NewGuid(), Name = "Group 1" };
        var group2 = new SimpleGroupResource { Id = Guid.NewGuid(), Name = "Group 2" };
        
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Cut Shift with Groups",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Groups = new List<SimpleGroupResource> { group1, group2 }
        };
        
        var command = new PostCutsCommand(new List<ShiftResource> { cutShift });
        
        var mappedShift = new Shift 
        { 
            Id = cutShift.Id, 
            Name = cutShift.Name,
            Status = ShiftStatus.IsCut,
            OriginalId = originalId
        };
        
        _mockMapper.Map<ShiftResource, Shift>(cutShift).Returns(mappedShift);
        _mockMapper.Map<List<Shift>, List<ShiftResource>>(Arg.Any<List<Shift>>())
            .Returns(new List<ShiftResource> { cutShift });
        
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockRepository.Received(1).UpdateGroupItems(
            cutShift.Id, 
            Arg.Is<List<Guid>>(list => list.Count == 2 && list.Contains(group1.Id) && list.Contains(group2.Id))
        );
    }

    [Test]
    public async Task Handle_MultipleShifts_ProcessesAllInTransaction()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var shifts = new List<ShiftResource>();
        var mappedShifts = new List<Shift>();
        
        for (int i = 0; i < 5; i++)
        {
            var shiftResource = new ShiftResource 
            { 
                Id = Guid.NewGuid(), 
                Name = $"Cut Shift {i}",
                Status = ShiftStatus.IsCut,
                OriginalId = originalId,
                Groups = new List<SimpleGroupResource>()
            };
            shifts.Add(shiftResource);
            
            var mappedShift = new Shift 
            { 
                Id = shiftResource.Id, 
                Name = shiftResource.Name,
                Status = ShiftStatus.IsCut,
                OriginalId = originalId
            };
            mappedShifts.Add(mappedShift);
            
            _mockMapper.Map<ShiftResource, Shift>(shiftResource).Returns(mappedShift);
        }
        
        var command = new PostCutsCommand(shifts);
        
        _mockMapper.Map<List<Shift>, List<ShiftResource>>(Arg.Any<List<Shift>>()).Returns(shifts);
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().HaveCount(5);
        await _mockRepository.Received(5).Add(Arg.Any<Shift>());
        await _mockRepository.Received(5).UpdateGroupItems(Arg.Any<Guid>(), Arg.Any<List<Guid>>());
        await _mockUnitOfWork.Received(1).CompleteAsync();
        await _mockUnitOfWork.Received(1).CommitTransactionAsync(_mockTransaction);
        await _mockUnitOfWork.Received(0).RollbackTransactionAsync(_mockTransaction);
    }

    [Test]
    public async Task Handle_RepositoryThrowsException_RollsBackTransaction()
    {
        // Arrange
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Cut Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = Guid.NewGuid(),
            Groups = new List<SimpleGroupResource>()
        };
        
        var command = new PostCutsCommand(new List<ShiftResource> { cutShift });
        
        var mappedShift = new Shift 
        { 
            Id = cutShift.Id, 
            Name = cutShift.Name,
            Status = ShiftStatus.IsCut,
            OriginalId = cutShift.OriginalId
        };
        
        _mockMapper.Map<ShiftResource, Shift>(cutShift).Returns(mappedShift);
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);
        _mockRepository.Add(Arg.Any<Shift>()).Returns(Task.FromException(new Exception("Database error")));

        // Act & Assert
        var act = async () => await _handler.Handle(command, CancellationToken.None);
        
        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("*Error occurred while creating cut shifts*");
        
        await _mockUnitOfWork.Received(1).RollbackTransactionAsync(_mockTransaction);
        await _mockUnitOfWork.Received(0).CommitTransactionAsync(_mockTransaction);
    }
}