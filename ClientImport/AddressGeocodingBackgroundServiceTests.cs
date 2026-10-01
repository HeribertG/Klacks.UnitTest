// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Background geocoding of imported addresses: the worker drains every queued id through a scoped
/// processor and survives a failing item; the processor fills coordinates (and an empty state) only on
/// an exact hit and leaves the address unchanged otherwise; the disabled queue accepts nothing.
/// </summary>

using Klacks.Api.Application.Services.Geocoding;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class AddressGeocodingBackgroundServiceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task Worker_ProcessesEveryQueuedAddress_EvenAfterAFailure()
    {
        var processed = new List<Guid>();
        var done = new TaskCompletionSource();
        var failing = Guid.NewGuid();
        var ok = Guid.NewGuid();

        var processor = Substitute.For<IAddressGeocodingProcessor>();
        processor.ProcessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var id = ci.Arg<Guid>();
            processed.Add(id);
            if (id == ok)
            {
                done.TrySetResult();
            }

            return id == failing ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask;
        });

        var provider = new ServiceCollection().AddScoped(_ => processor).BuildServiceProvider();
        var service = new AddressGeocodingBackgroundService(provider, NullLogger<AddressGeocodingBackgroundService>.Instance, TimeSpan.Zero);

        service.TryQueue(failing).ShouldBeTrue();
        service.TryQueue(ok).ShouldBeTrue();

        await service.StartAsync(CancellationToken.None);
        await done.Task.WaitAsync(WaitLimit);
        await service.StopAsync(CancellationToken.None);

        processed.ShouldBe([failing, ok]);
    }

    [Test]
    public void AddressGeocodingFlag_IsOnByDefault()
    {
        new Klacks.Api.Application.Configuration.BackgroundServiceOptions().AddressGeocoding.ShouldBeTrue();
    }

    [Test]
    public void DisabledQueue_AcceptsNothing()
    {
        new DisabledAddressGeocodingQueue().TryQueue(Guid.NewGuid()).ShouldBeFalse();
    }

    [Test]
    public void FullQueue_RefusesWithoutWaiting()
    {
        var service = new AddressGeocodingBackgroundService(
            new ServiceCollection().BuildServiceProvider(), NullLogger<AddressGeocodingBackgroundService>.Instance, TimeSpan.Zero);

        for (var index = 0; index < ClientImportLimits.GeocodingQueueCapacity; index++)
        {
            service.TryQueue(Guid.NewGuid()).ShouldBeTrue();
        }

        service.TryQueue(Guid.NewGuid()).ShouldBeFalse();
    }

    [Test]
    public async Task Processor_ExactHit_FillsCoordinatesAndEmptyState()
    {
        var (processor, address, geocoding, unitOfWork) = Processor(new GeocodingValidationResult
        {
            Found = true, ExactMatch = true, Latitude = 47.37, Longitude = 8.54, State = "Zürich"
        });

        await processor.ProcessAsync(address.Id, CancellationToken.None);

        address.Latitude.ShouldBe(47.37);
        address.Longitude.ShouldBe(8.54);
        address.State.ShouldBe("ZH");
        await geocoding.Received(1).ValidateExactAddressAsync("Hauptstrasse 1", "8000", "Zürich", "Schweiz");
        await unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Processor_NonExactStreetHit_LeavesTheAddressUnchanged()
    {
        var (processor, address, _, unitOfWork) = Processor(new GeocodingValidationResult
        {
            Found = true, ExactMatch = false, MatchType = "city", Latitude = 1, Longitude = 2
        });

        await processor.ProcessAsync(address.Id, CancellationToken.None);

        address.Latitude.ShouldBeNull();
        await unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Processor_ServiceUnavailable_LeavesTheAddressUnchanged()
    {
        var (processor, address, _, unitOfWork) = Processor(new GeocodingValidationResult { ServiceUnavailable = true });

        await processor.ProcessAsync(address.Id, CancellationToken.None);

        address.Latitude.ShouldBeNull();
        await unitOfWork.DidNotReceive().CompleteAsync();
    }

    private static (AddressGeocodingProcessor Processor, Address Address, IGeocodingService Geocoding, IUnitOfWork UnitOfWork) Processor(
        GeocodingValidationResult result)
    {
        var address = new Address { Id = Guid.NewGuid(), Street = "Hauptstrasse 1", Zip = "8000", City = "Zürich", Country = "CH" };
        var repository = Substitute.For<IAddressRepository>();
        repository.Get(address.Id).Returns(address);

        var geocoding = Substitute.For<IGeocodingService>();
        geocoding.ValidateExactAddressAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(result);

        var countries = Substitute.For<ICountryResolver>();
        countries.ResolveAsync("CH", Arg.Any<CancellationToken>()).Returns(ClientImportTestData.Switzerland);

        var unitOfWork = Substitute.For<IUnitOfWork>();
        var states = Substitute.For<IStateRepository>();
        states.List().Returns([new State { Abbreviation = "ZH", Name = new MultiLanguage { De = "Zürich" } }]);
        var stateResolver = new StateAbbreviationResolver(states);

        var processor = new AddressGeocodingProcessor(repository, geocoding, countries, stateResolver, unitOfWork,
            NullLogger<AddressGeocodingProcessor>.Instance);
        return (processor, address, geocoding, unitOfWork);
    }
}
