// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class GetEmailGroupTreeQueryHandlerTests
{
    private const string ClientEmailAddress = "client@example.com";
    private const int ClientEmailCount = 3;
    private const int ClientUnreadCount = 1;

    private IGroupHierarchyService _groupHierarchyService = null!;
    private IEmailQueryRepository _emailQueryRepository = null!;
    private GetEmailGroupTreeQueryHandler _handler = null!;
    private Guid _clientId;

    [SetUp]
    public void Setup()
    {
        _groupHierarchyService = Substitute.For<IGroupHierarchyService>();
        _emailQueryRepository = Substitute.For<IEmailQueryRepository>();
        _clientId = Guid.NewGuid();

        _groupHierarchyService.GetTreeAsync().Returns(new List<Group>());

        _emailQueryRepository
            .GetDistinctAssignedEmailAddressesAsync(EmailConstants.ClientAssignedFolder, Arg.Any<CancellationToken>())
            .Returns(new List<string> { ClientEmailAddress });

        _emailQueryRepository
            .GetClientsWithEmailCommunicationsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ClientEmailInfo>
            {
                new()
                {
                    ClientId = _clientId,
                    EmailAddress = ClientEmailAddress,
                    ClientDisplayName = "Test Client",
                    GroupIds = [],
                },
            });

        _emailQueryRepository
            .CountEmailsByAddressAsync(EmailConstants.ClientAssignedFolder, ClientEmailAddress, Arg.Any<CancellationToken>())
            .Returns(ClientEmailCount);

        _emailQueryRepository
            .CountUnreadEmailsByAddressAsync(EmailConstants.ClientAssignedFolder, ClientEmailAddress, Arg.Any<CancellationToken>())
            .Returns(ClientUnreadCount);

        var logger = Substitute.For<ILogger<GetEmailGroupTreeQueryHandler>>();
        _handler = new GetEmailGroupTreeQueryHandler(_groupHierarchyService, _emailQueryRepository, logger);
    }

    [TestCase("de", EmailGroupTreeLabels.UnassignedDe)]
    [TestCase("en", EmailGroupTreeLabels.UnassignedEn)]
    [TestCase("fr", EmailGroupTreeLabels.UnassignedFr)]
    [TestCase("it", EmailGroupTreeLabels.UnassignedIt)]
    [TestCase("de-CH", EmailGroupTreeLabels.UnassignedDe)]
    [TestCase("IT", EmailGroupTreeLabels.UnassignedIt)]
    [TestCase(null, EmailGroupTreeLabels.UnassignedEn)]
    [TestCase("", EmailGroupTreeLabels.UnassignedEn)]
    [TestCase("xx", EmailGroupTreeLabels.UnassignedEn)]
    public async Task Handle_UnassignedClient_UsesLocalizedContainerLabel(string? language, string expectedLabel)
    {
        var result = await _handler.Handle(new GetEmailGroupTreeQuery(language), CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe(expectedLabel);
    }

    [Test]
    public async Task Handle_UnassignedClient_BuildsContainerWithClientChildAndCounts()
    {
        var result = await _handler.Handle(new GetEmailGroupTreeQuery(), CancellationToken.None);

        result.Count.ShouldBe(1);
        var container = result[0];
        container.Id.ShouldBe(Guid.Empty);
        container.Name.ShouldBe(EmailGroupTreeLabels.UnassignedEn);
        container.EmailCount.ShouldBe(ClientEmailCount);
        container.Children.Count.ShouldBe(1);
        container.Children[0].Id.ShouldBe(_clientId);
        container.Children[0].EmailCount.ShouldBe(ClientEmailCount);
        container.Children[0].UnreadCount.ShouldBe(ClientUnreadCount);
    }
}
