using AutoMapper;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Schedules;

namespace UnitTest.Handlers.Shifts;

[TestFixture]
public class PutCutsCommandHandlerTests
{
    private PutCutsCommandHandler _handler;
    private IShiftRepository _mockShiftRepository;
    private IMapper _mockMapper;

    [SetUp]
    public void SetUp()
    {
        _mockShiftRepository = Substitute.For<IShiftRepository>();
        _mockMapper = Substitute.For<IMapper>();
        _handler = new PutCutsCommandHandler(_mockShiftRepository, _mockMapper);
    }

    [Test]
    public async Task Handle_ValidRequest_CallsRepositoryAndMapper()
    {
        // Arrange
        var shiftResource = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Updated Cut Shift 1",
            Status = ShiftStatus.IsCut,
            Groups = new List<SimpleGroupResource>()
        };
        var cutShifts = new List<ShiftResource> { shiftResource };
        var command = new PutCutsCommand(cutShifts);
        
        var shiftEntity = new Shift { Id = shiftResource.Id, Name = shiftResource.Name };
        var updatedShiftEntity = new Shift { Id = shiftResource.Id, Name = shiftResource.Name };
        
        _mockMapper.Map<Shift>(shiftResource).Returns(shiftEntity);
        _mockShiftRepository.Put(shiftEntity).Returns(updatedShiftEntity);
        _mockMapper.Map<ShiftResource>(updatedShiftEntity).Returns(shiftResource);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result.First(), Is.EqualTo(shiftResource));
        
        _mockMapper.Received(1).Map<Shift>(shiftResource);
        await _mockShiftRepository.Received(1).Put(shiftEntity);
        _mockMapper.Received(1).Map<ShiftResource>(updatedShiftEntity);
    }

    [Test]
    public async Task Handle_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var command = new PutCutsCommand(new List<ShiftResource>());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0));
        
        _mockMapper.DidNotReceive().Map<Shift>(Arg.Any<ShiftResource>());
        await _mockShiftRepository.DidNotReceive().Put(Arg.Any<Shift>());
        _mockMapper.DidNotReceive().Map<ShiftResource>(Arg.Any<Shift>());
    }
}