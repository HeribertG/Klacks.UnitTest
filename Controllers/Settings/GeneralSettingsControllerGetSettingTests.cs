using System.Threading.Tasks;
using Klacks.Api.Application.Queries.Settings.Settings;
using Klacks.Api.Infrastructure.Email;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Controllers.Settings
{
    [TestFixture]
    public class GeneralSettingsControllerGetSettingTests
    {
        private GeneralSettingsController _controller = null!;
        private IMediator _mockMediator = null!;

        [SetUp]
        public void SetUp()
        {
            _mockMediator = Substitute.For<IMediator>();
            var mockEmailTestService = Substitute.For<IEmailTestService>();
            var mockLogger = Substitute.For<ILogger<GeneralSettingsController>>();

            _controller = new GeneralSettingsController(_mockMediator, mockLogger, mockEmailTestService)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
        }

        [Test]
        public async Task GetSetting_WithUnconfiguredKey_ShouldReturnOkNotNotFound()
        {
            _mockMediator.Send(Arg.Any<GetQuery>()).Returns(Task.FromResult<Klacks.Api.Domain.Models.Settings.Settings?>(null));

            var result = await _controller.GetSetting("ENABLED_EXPORT_FORMATS");

            result.Result.ShouldNotBeOfType<NotFoundResult>();
            result.Result.ShouldBeOfType<OkObjectResult>();
        }

        [Test]
        public async Task GetSetting_WithExistingKey_ShouldReturnOkWithValue()
        {
            var existing = new Klacks.Api.Domain.Models.Settings.Settings { Type = "ENABLED_EXPORT_FORMATS", Value = "datev-order" };
            _mockMediator.Send(Arg.Any<GetQuery>()).Returns(Task.FromResult<Klacks.Api.Domain.Models.Settings.Settings?>(existing));

            var result = await _controller.GetSetting("ENABLED_EXPORT_FORMATS");

            var okResult = result.Result as OkObjectResult;
            okResult!.Value.ShouldBe(existing);
        }
    }
}
