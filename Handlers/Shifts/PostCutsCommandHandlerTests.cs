using AutoMapper;
using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Schedules;
using Microsoft.Extensions.Logging;

namespace UnitTest.Handlers.Shifts;

[TestFixture]
public class PostCutsCommandHandlerTests
{
    private PostCutsCommandHandler _handler;
    private IShiftRepository _mockShiftRepository;
    private IMapper _mockMapper;
    private IUnitOfWork _mockUnitOfWork;
    private ILogger<PostCutsCommandHandler> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockShiftRepository = Substitute.For<IShiftRepository>();
        _mockMapper = Substitute.For<IMapper>();
        _mockUnitOfWork = Substitute.For<IUnitOfWork>();
        _mockLogger = Substitute.For<ILogger<PostCutsCommandHandler>>();
        _handler = new PostCutsCommandHandler(_mockShiftRepository, _mockMapper, _mockUnitOfWork, _mockLogger);
    }

    [Test]
    public async Task Handle_ValidRequest_CallsRepositoryAndMapper()
    {
        // Arrange
        var shiftResource = new ShiftResource 
        { 
            Id = Guid.NewGuid(), 
            Name = "Cut Shift 1",
            Status = ShiftStatus.SplitShift,
            Groups = new List<SimpleGroupResource>()
        };
        var cutShifts = new List<ShiftResource> { shiftResource };
        var command = new PostCutsCommand(cutShifts);
        
        var shiftEntity = new Shift { Id = shiftResource.Id, Name = shiftResource.Name };
        
        _mockMapper.Map<Shift>(shiftResource).Returns(shiftEntity);
        _mockShiftRepository.Add(shiftEntity).Returns(Task.CompletedTask);
        _mockMapper.Map<ShiftResource>(shiftEntity).Returns(shiftResource);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result.First(), Is.EqualTo(shiftResource));

        _mockMapper.Received(1).Map<Shift>(shiftResource);
        await _mockShiftRepository.Received(1).Add(shiftEntity);
        await _mockUnitOfWork.Received(1).CompleteAsync();
        _mockMapper.Received(1).Map<ShiftResource>(shiftEntity);
    }

    [Test]
    public async Task Handle_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var command = new PostCutsCommand(new List<ShiftResource>());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count, Is.EqualTo(0));
        
        _mockMapper.DidNotReceive().Map<Shift>(Arg.Any<ShiftResource>());
        await _mockShiftRepository.DidNotReceive().Add(Arg.Any<Shift>());
        _mockMapper.DidNotReceive().Map<ShiftResource>(Arg.Any<Shift>());
    }
}