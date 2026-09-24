// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guards for the inbound clarification dialog. (1) No clarification component takes
/// ILLMService: foreign inbound text must never run through the chat pipeline (recipe engine,
/// auto-memory; see inbound-messenger-review-findings §2). (2) The messenger reply channel adapter takes
/// plugin-level services only. (3) No messenger observer takes a clarification service in its
/// constructor: MessagingService receives the observers through its constructor and sits inside the
/// ILLMService graph, so such an edge would close a DI cycle and stop the host from booting whenever the
/// messaging plugin is active (reviewer memory messaging-observer-di-leaf). Direct constructor
/// parameters only. The container tests (ClarificationCoordinatorContainerTests,
/// ClarificationExpirySweepContainerTests, MessengerReplyChannelContainerTests; ValidateOnBuild) cover
/// transitive DI cycles and resolvability of the registrations, NOT the transitive reachability of
/// ILLMService and NOT the real host boot.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.Api.Infrastructure.Plugins;
using Klacks.Api.Infrastructure.Repositories.Inbound;
using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class InboundClarificationDependencyGuardTests
{
    private const int MinimumExpectedObservers = 2;

    private static readonly Type[] ClarificationComponents =
    [
        typeof(ClarificationCoordinator),
        typeof(ClarificationQuestionComposer),
        typeof(EmailReplySender),
        typeof(MessengerReplySender),
        typeof(MessagingPluginClientReplyChannel),
        typeof(ClarificationExpirySweep),
        typeof(InboundClarificationRepository),
        typeof(InboundShiftContextReader),
        typeof(CloseClarificationSkill)
    ];

    private static readonly Type[] ClarificationServiceTypes =
    [
        typeof(IClarificationCoordinator),
        typeof(IInboundReplySender),
        typeof(IEnumerable<IInboundReplySender>),
        typeof(IClientMessengerReplyChannel),
        typeof(IClarificationQuestionComposer),
        typeof(IInboundClarificationRepository),
        typeof(IInboundIntentAnalysisService)
    ];

    private static IEnumerable<Type> Components() => ClarificationComponents;

    [TestCaseSource(nameof(Components))]
    public void ClarificationComponent_DoesNotTakeILLMService(Type component)
    {
        var parameterTypes = component.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        parameterTypes.ShouldNotContain(typeof(ILLMService));
    }

    [Test]
    public void MessengerReplyChannel_TakesPluginLevelServicesOnly()
    {
        var parameterTypes = typeof(MessagingPluginClientReplyChannel).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        Assert.That(parameterTypes, Is.EquivalentTo(new[]
        {
            typeof(IPluginStateChecker),
            typeof(IMessengerContactRepository),
            typeof(IMessagingProviderRepository),
            typeof(IMessagingProviderAdapterFactory),
            typeof(IMessagingService),
            typeof(ILogger<MessagingPluginClientReplyChannel>)
        }));
    }

    [Test]
    public void NoMessengerObserver_TakesAClarificationService()
    {
        var observerTypes = typeof(ClarificationCoordinator).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && (typeof(IInboundClientMessengerObserver).IsAssignableFrom(t)
                            || typeof(IInboundMessengerObserver).IsAssignableFrom(t)))
            .ToList();

        observerTypes.Count.ShouldBeGreaterThanOrEqualTo(MinimumExpectedObservers);
        observerTypes.ShouldContain(typeof(MessengerIntentObserver));

        var offenders = observerTypes
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => ClarificationServiceTypes.Contains(p.ParameterType))
                .Select(p => $"{t.Name}({p.ParameterType.Name})"))
            .ToList();

        offenders.ShouldBeEmpty(string.Join(", ", offenders));
    }
}
