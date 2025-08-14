using AutoMapper;
using Klacks.Api.Application.Commands;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Handlers.CalendarSelections;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Domain.Services.CalendarSelections;
using Klacks.Api.Application.Queries;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Presentation.DTOs.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UnitTest.Helper;

namespace UnitTest.Repository;

internal class CalendarSelectionTest
{
    public IHttpContextAccessor _httpContextAccessor = null!;
    public DataBaseContext dbContext = null!;
    private ILogger<PostCommandHandler> _logger = null!;
    private ILogger<PutCommandHandler> _logger2 = null!;
    private ILogger<DeleteCommandHandler> _logger3 = null!;
    private ILogger<UnitOfWork> _unitOfWorkLogger = null!;
    private ILogger<CalendarSelection> _calendarSelectionLogger = null!;
    private ILogger<SelectedCalendar> _selectedCalendarLogger = null!;
    private ILogger<CalendarSelectionUpdateService> _updateServiceLogger = null!;
    
    private IMapper _mapper = null!;

    [Test]
    public async Task AddAndReReadCalendarSelection_Ok()
    {
        //Arrange Post
        var fakeCalendarSelection = createNew();

        var options = new DbContextOptionsBuilder<DataBaseContext>()
       .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;
        dbContext = new DataBaseContext(options, _httpContextAccessor);

        dbContext.Database.EnsureCreated();

        var unitOfWork = new UnitOfWork(dbContext, _unitOfWorkLogger);
        var updateService = new CalendarSelectionUpdateService(dbContext, _updateServiceLogger);
        var repository = new CalendarSelectionRepository(dbContext, _calendarSelectionLogger, updateService);
        var queryPost = new PostCommand<CalendarSelectionResource>(fakeCalendarSelection);
        var handlerPost = new PostCommandHandler(repository, _mapper, unitOfWork, _logger);

        //Act Post
        var resultPost = await handlerPost.Handle(queryPost, default);

        //Assert Post
        resultPost.Should().NotBeNull();
        resultPost!.SelectedCalendars.Should().NotBeNull();
        resultPost.SelectedCalendars.Should().HaveCount(fakeCalendarSelection.SelectedCalendars.Count);

        //Arrange Get
        var id = resultPost.Id;
        var queryGet = new GetQuery<CalendarSelectionResource>(id);
        var updateService2 = new CalendarSelectionUpdateService(dbContext, _updateServiceLogger);
        var repository2 = new CalendarSelectionRepository(dbContext, _calendarSelectionLogger, updateService2);
        var handlerGet = new GetQueryHandler(repository2, _mapper);

        //Act Get
        var resultGet = await handlerGet.Handle(queryGet, default);

        //Assert Get
        resultGet.Should().NotBeNull();
        resultGet!.SelectedCalendars.Should().NotBeNull();
        resultGet.SelectedCalendars.Should().HaveCount(fakeCalendarSelection.SelectedCalendars.Count);

        //Arrange Put
        var fakeCalendarSelectionUpdate = resultGet!;
        var fakeSelectedCalendar = new SelectedCalendarResource()
        {
            Id = Guid.Empty,
            Country = "USA",
            State = "NY"
        };
        fakeCalendarSelectionUpdate.SelectedCalendars.Add(fakeSelectedCalendar);
        var queryPut = new PutCommand<CalendarSelectionResource>(fakeCalendarSelectionUpdate);
        var updateService3 = new CalendarSelectionUpdateService(dbContext, _updateServiceLogger);
        var repository3 = new CalendarSelectionRepository(dbContext, _calendarSelectionLogger, updateService3);
        var handlerPut = new PutCommandHandler(repository3, _mapper, unitOfWork, _logger2);

        //Act Put
        var resultPut = await handlerPut.Handle(queryPut, default);

        //Assert Put
        resultPut.Should().NotBeNull();
        resultPut!.SelectedCalendars.Should().NotBeNull();
        resultPut.SelectedCalendars.Should().HaveCount(fakeCalendarSelectionUpdate.SelectedCalendars.Count);

        //Arrange Delete
        var queryDelete = new DeleteCommand<CalendarSelectionResource>(resultPut.Id);
        var updateService4 = new CalendarSelectionUpdateService(dbContext, _updateServiceLogger);
        var repository4 = new CalendarSelectionRepository(dbContext, _calendarSelectionLogger, updateService4);
        var handlerDelete = new DeleteCommandHandler(repository4, _mapper, unitOfWork, _logger3);

        //Act Delete
        var resultDelete = await handlerDelete.Handle(queryDelete, default);

        //Assert Delete
        resultDelete.Should().NotBeNull();

        var repositorySelectedCalendar = new SelectedCalendarRepository(dbContext, _selectedCalendarLogger);

        foreach (var item in resultDelete!.SelectedCalendars)
        {
            var res = await repositorySelectedCalendar.Get(item.Id);
            res.Should().BeNull();
        }
    }

    [SetUp]
    public void Setup()
    {
        _mapper = TestHelper.GetFullMapperConfiguration().CreateMapper();
        _logger = Substitute.For<ILogger<PostCommandHandler>>();
        _logger2 = Substitute.For<ILogger<PutCommandHandler>>();
        _logger3 = Substitute.For<ILogger<DeleteCommandHandler>>();
        _unitOfWorkLogger = Substitute.For<ILogger<UnitOfWork>>();
        _calendarSelectionLogger = Substitute.For<ILogger<CalendarSelection>>();
        _selectedCalendarLogger = Substitute.For<ILogger<SelectedCalendar>>();
        _updateServiceLogger = Substitute.For<ILogger<CalendarSelectionUpdateService>>();
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
    }

    [TearDown]
    public void TearDown()
    {
        dbContext.Database.EnsureDeleted();
        dbContext.Dispose();
    }

    private CalendarSelectionResource createNew()
    {
        var fakeCalendarSelection = FakeData.CalendarSelections.GenerateFakeCalendarSelections(1).FirstOrDefault();
        fakeCalendarSelection!.Id = Guid.Empty;

        foreach (var item in fakeCalendarSelection.SelectedCalendars)
        {
            item!.Id = Guid.Empty;
        }

        return fakeCalendarSelection;
    }
}