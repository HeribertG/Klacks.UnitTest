using Klacks.Api.Application.Commands.Shifts;
using Klacks.Api.Application.Handlers.Shifts;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Schedules;

namespace UnitTest.Handlers.Shifts;

[TestFixture]
public class PutCutsCommandHandlerTests
{
    private PutCutsCommandHandler _handler;
    private IShiftApplicationService _mockShiftApplicationService;

    [SetUp]
    public void SetUp()
    {
        _mockShiftApplicationService = Substitute.For<IShiftApplicationService>();
        _handler = new PutCutsCommandHandler(_mockShiftApplicationService);
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
                Name = "Updated Cut Shift 1",
                Status = ShiftStatus.IsCut,
                Groups = new List<SimpleGroupResource>()
            }
        };

        var command = new PutCutsCommand(cutShifts);
        var expectedResult = new List<ShiftResource> { cutShifts.First() };

        _mockShiftApplicationService.UpdateShiftCutsAsync(Arg.Any<List<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(expectedResult));
        await _mockShiftApplicationService.Received(1).UpdateShiftCutsAsync(
            Arg.Is<List<ShiftResource>>(x => x.Count == 1), 
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_EmptyList_CallsApplicationService()
    {
        // Arrange
        var command = new PutCutsCommand(new List<ShiftResource>());
        var expectedResult = new List<ShiftResource>();

        _mockShiftApplicationService.UpdateShiftCutsAsync(Arg.Any<List<ShiftResource>>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.That(result, Is.EqualTo(expectedResult));
        await _mockShiftApplicationService.Received(1).UpdateShiftCutsAsync(
            Arg.Is<List<ShiftResource>>(x => x.Count == 0), 
            Arg.Any<CancellationToken>());
    }
}