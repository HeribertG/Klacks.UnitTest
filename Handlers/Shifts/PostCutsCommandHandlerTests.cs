using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Schedules;

namespace UnitTest.Handlers.Shifts;

[TestFixture]
public class PostCutsCommandHandlerTests
{
    private PostCutsCommandHandler _handler;
    private IShiftApplicationService _mockShiftApplicationService;

    [SetUp]
    public void SetUp()
    {
        _mockShiftApplicationService = Substitute.For<IShiftApplicationService>();
        _handler = new PostCutsCommandHandler(_mockShiftApplicationService);
    }

    [Test]
    public async Task Handle_ValidRequest_CallsApplicationService()
    {
        // Arrange
        var cutShifts = new List<ShiftResource>
        {
            new ShiftResource 
            { 
                Id = Guid.NewGuid(), 
                Name = "Cut Shift 1",
                Status = ShiftStatus.IsCut,
                Groups = new List<SimpleGroupResource>()
            }
        };

        var command = new PostCutsCommand(cutShifts);
        var expectedResult = new List<ShiftResource> { cutShifts.First() };

        _mockShiftApplicationService.CreateShiftCutsAsync(Arg.Any<List<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(expectedResult));
        await _mockShiftApplicationService.Received(1).CreateShiftCutsAsync(
            Arg.Is<List<ShiftResource>>(x => x.Count == 1), 
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_EmptyList_CallsApplicationService()
    {
        // Arrange
        var command = new PostCutsCommand(new List<ShiftResource>());
        var expectedResult = new List<ShiftResource>();

        _mockShiftApplicationService.CreateShiftCutsAsync(Arg.Any<List<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(expectedResult));
        await _mockShiftApplicationService.Received(1).CreateShiftCutsAsync(
            Arg.Is<List<ShiftResource>>(x => x.Count == 0), 
            Arg.Any<CancellationToken>());
    }
}