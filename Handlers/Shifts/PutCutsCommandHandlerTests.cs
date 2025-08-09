using AutoMapper;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Enums;
using Klacks.Api.Exceptions;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Models.Schedules;
using Klacks.Api.Presentation.DTOs.Schedules;
using Klacks.Api.Presentation.DTOs.Associations;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Storage;

namespace UnitTest.Handlers.Shifts;

[TestFixture]
public class PutCutsCommandHandlerTests
{
    private PutCutsCommandHandler _handler;
    private IMapper _mockMapper;
    private IShiftRepository _mockRepository;
    private IUnitOfWork _mockUnitOfWork;
    private ILogger<PutCutsCommandHandler> _mockLogger;
    private IDbContextTransaction _mockTransaction;

    [SetUp]
    public void SetUp()
    {
        _mockMapper = Substitute.For<IMapper>();
        _mockRepository = Substitute.For<IShiftRepository>();
        _mockUnitOfWork = Substitute.For<IUnitOfWork>();
        _mockLogger = Substitute.For<ILogger<PutCutsCommandHandler>>();
        _mockTransaction = Substitute.For<IDbContextTransaction>();
        
        _handler = new PutCutsCommandHandler(_mockMapper, _mockRepository, _mockUnitOfWork, _mockLogger);
    }

    [TearDown]
    public void TearDown()
    {
        _mockTransaction?.Dispose();
    }

    [Test]
    public async Task Handle_ValidCutShifts_UpdatesShiftsSuccessfully()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var cutShift1 = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Updated Cut Shift 1",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Groups = new List<SimpleGroupResource>()
        };
        
        var existingShift1 = new Shift 
        { 
            Id = cutShift1.Id, 
            Name = "Old Cut Shift 1",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Lft = 2,
            Rgt = 3,
            ParentId = originalId,
            RootId = originalId
        };
        
        var command = new PutCutsCommand(new List<ShiftResource> { cutShift1 });
        
        _mockRepository.Get(cutShift1.Id).Returns(Task.FromResult(existingShift1));
        _mockMapper.Map<List<Shift>, List<ShiftResource>>(Arg.Any<List<Shift>>())
            .Returns(new List<ShiftResource> { cutShift1 });
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().HaveCount(1);
        
        // Verify nested set values are preserved
        existingShift1.Lft.Should().Be(2);
        existingShift1.Rgt.Should().Be(3);
        existingShift1.ParentId.Should().Be(originalId);
        existingShift1.RootId.Should().Be(originalId);
        existingShift1.OriginalId.Should().Be(originalId);
        existingShift1.Status.Should().Be(ShiftStatus.IsCut);
        
        await _mockRepository.Received(1).Put(existingShift1);
        await _mockRepository.Received(1).UpdateGroupItems(existingShift1.Id, Arg.Any<List<Guid>>());
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
        
        var command = new PutCutsCommand(new List<ShiftResource> { cutShift });
        
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act & Assert
        var act = async () => await _handler.Handle(command, CancellationToken.None);
        
        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("*must have status IsCut*");
        
        await _mockUnitOfWork.Received(1).RollbackTransactionAsync(_mockTransaction);
    }

    [Test]
    public async Task Handle_NonExistentShift_ThrowsInvalidRequestException()
    {
        // Arrange
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Non-existent Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = Guid.NewGuid(),
            Groups = new List<SimpleGroupResource>()
        };
        
        var command = new PutCutsCommand(new List<ShiftResource> { cutShift });
        
        _mockRepository.Get(cutShift.Id).Returns(Task.FromResult<Shift>(null));
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act & Assert
        var act = async () => await _handler.Handle(command, CancellationToken.None);
        
        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage($"*Shift with ID {cutShift.Id} not found*");
        
        await _mockUnitOfWork.Received(1).RollbackTransactionAsync(_mockTransaction);
    }


    [Test]
    public async Task Handle_PreservesNestedSetValues_AfterMapping()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Updated Cut Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Groups = new List<SimpleGroupResource>()
        };
        
        var existingShift = new Shift 
        { 
            Id = cutShift.Id, 
            Name = "Old Cut Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Lft = 5,
            Rgt = 6,
            ParentId = parentId,
            RootId = rootId
        };
        
        var command = new PutCutsCommand(new List<ShiftResource> { cutShift });
        
        _mockRepository.Get(cutShift.Id).Returns(Task.FromResult(existingShift));
        _mockMapper.Map<List<Shift>, List<ShiftResource>>(Arg.Any<List<Shift>>())
            .Returns(new List<ShiftResource> { cutShift });
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Setup mapper to modify nested set values (simulating mapping issue)
        _mockMapper.When(x => x.Map(cutShift, existingShift))
            .Do(x => 
            {
                existingShift.Name = cutShift.Name;
                existingShift.Lft = null; // Mapper tries to clear these
                existingShift.Rgt = null;
                existingShift.ParentId = null;
                existingShift.RootId = null;
                existingShift.OriginalId = null;
            });

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert - Values should be restored
        existingShift.Lft.Should().Be(5);
        existingShift.Rgt.Should().Be(6);
        existingShift.ParentId.Should().Be(parentId);
        existingShift.RootId.Should().Be(rootId);
        existingShift.OriginalId.Should().Be(originalId);
        existingShift.Status.Should().Be(ShiftStatus.IsCut);
    }

    [Test]
    public async Task Handle_ShiftsWithGroups_UpdatesGroupItems()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var group1 = new SimpleGroupResource { Id = Guid.NewGuid(), Name = "Group 1" };
        var group2 = new SimpleGroupResource { Id = Guid.NewGuid(), Name = "Group 2" };
        var group3 = new SimpleGroupResource { Id = Guid.NewGuid(), Name = "Group 3" };
        
        var cutShift = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Cut Shift with Groups",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Groups = new List<SimpleGroupResource> { group1, group2, group3 }
        };
        
        var existingShift = new Shift 
        { 
            Id = cutShift.Id, 
            Name = "Old Cut Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = originalId,
            Lft = 2,
            Rgt = 3,
            ParentId = originalId,
            RootId = originalId
        };
        
        var command = new PutCutsCommand(new List<ShiftResource> { cutShift });
        
        _mockRepository.Get(cutShift.Id).Returns(Task.FromResult(existingShift));
        _mockMapper.Map<List<Shift>, List<ShiftResource>>(Arg.Any<List<Shift>>())
            .Returns(new List<ShiftResource> { cutShift });
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act
        await _handler.Handle(command, CancellationToken.None);

        // Assert
        await _mockRepository.Received(1).UpdateGroupItems(
            cutShift.Id, 
            Arg.Is<List<Guid>>(list => 
                list.Count == 3 && 
                list.Contains(group1.Id) && 
                list.Contains(group2.Id) &&
                list.Contains(group3.Id))
        );
    }

    [Test]
    public async Task Handle_MultipleShifts_UpdatesAllInTransaction()
    {
        // Arrange
        var originalId = Guid.NewGuid();
        var shifts = new List<ShiftResource>();
        var existingShifts = new Dictionary<Guid, Shift>();
        
        for (int i = 0; i < 3; i++)
        {
            var shiftResource = new ShiftResource 
            { 
                Id = Guid.NewGuid(), 
                Name = $"Updated Cut Shift {i}",
                Status = ShiftStatus.IsCut,
                OriginalId = originalId,
                Groups = new List<SimpleGroupResource>()
            };
            shifts.Add(shiftResource);
            
            var existingShift = new Shift 
            { 
                Id = shiftResource.Id, 
                Name = $"Old Cut Shift {i}",
                Status = ShiftStatus.IsCut,
                OriginalId = originalId,
                Lft = i * 2 + 2,
                Rgt = i * 2 + 3,
                ParentId = originalId,
                RootId = originalId
            };
            existingShifts[shiftResource.Id] = existingShift;
            
            _mockRepository.Get(shiftResource.Id).Returns(Task.FromResult(existingShift));
        }
        
        var command = new PutCutsCommand(shifts);
        
        _mockMapper.Map<List<Shift>, List<ShiftResource>>(Arg.Any<List<Shift>>()).Returns(shifts);
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().HaveCount(3);
        await _mockRepository.Received(3).Put(Arg.Any<Shift>());
        await _mockRepository.Received(3).UpdateGroupItems(Arg.Any<Guid>(), Arg.Any<List<Guid>>());
        await _mockUnitOfWork.Received(1).CompleteAsync();
        await _mockUnitOfWork.Received(1).CommitTransactionAsync(_mockTransaction);
        await _mockUnitOfWork.Received(0).RollbackTransactionAsync(_mockTransaction);
        
        // Verify all nested set values preserved
        foreach (var existingShift in existingShifts.Values)
        {
            existingShift.OriginalId.Should().Be(originalId);
            existingShift.Lft.Should().NotBeNull();
            existingShift.Rgt.Should().NotBeNull();
            existingShift.ParentId.Should().Be(originalId);
            existingShift.RootId.Should().Be(originalId);
        }
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
        
        var existingShift = new Shift 
        { 
            Id = cutShift.Id, 
            Name = "Old Cut Shift",
            Status = ShiftStatus.IsCut,
            OriginalId = cutShift.OriginalId,
            Lft = 2,
            Rgt = 3
        };
        
        var command = new PutCutsCommand(new List<ShiftResource> { cutShift });
        
        _mockRepository.Get(cutShift.Id).Returns(Task.FromResult(existingShift));
        _mockUnitOfWork.BeginTransactionAsync().Returns(_mockTransaction);
        _mockRepository.Put(Arg.Any<Shift>()).Returns(Task.FromException<Shift>(new Exception("Database error")));

        // Act & Assert
        var act = async () => await _handler.Handle(command, CancellationToken.None);
        
        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("*Error occurred while updating cut shifts*");
        
        await _mockUnitOfWork.Received(1).RollbackTransactionAsync(_mockTransaction);
        await _mockUnitOfWork.Received(0).CommitTransactionAsync(_mockTransaction);
    }
}