using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Builds a real PersistentPendingConfirmationStore/PersistentPendingCompanyRuleDraftStore backed by a
/// fresh EF InMemory DataBaseContext, for tests of OTHER components (skills, handlers, the autonomy gate)
/// that need a working pending-store double. Each call gets its own isolated in-memory database, mirroring
/// the store's own fresh-scope-per-operation design (a new scope resolves a new DbContext instance).
/// </summary>
internal static class PendingStoreTestFactory
{
    public static IPendingConfirmationStore CreateConfirmationStore()
    {
        var scopeFactory = CreateScopeFactory<IPendingConfirmationRepository>(
            context => new PendingConfirmationRepository(context));
        return new PersistentPendingConfirmationStore(scopeFactory);
    }

    public static IPendingCompanyRuleDraftStore CreateCompanyRuleDraftStore()
    {
        var scopeFactory = CreateScopeFactory<IPendingCompanyRuleDraftRepository>(
            context => new PendingCompanyRuleDraftRepository(context));
        return new PersistentPendingCompanyRuleDraftStore(scopeFactory);
    }

    private static IServiceScopeFactory CreateScopeFactory<TRepository>(Func<DataBaseContext, TRepository> createRepository)
        where TRepository : class
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var httpAccessor = Substitute.For<IHttpContextAccessor>();

        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(TRepository))
            .Returns(_ => createRepository(new DataBaseContext(options, httpAccessor)));
        scope.ServiceProvider.Returns(provider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        return scopeFactory;
    }
}
