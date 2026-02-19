using Klacks.Api.Application.DTOs.Filter;
using System.Text.Json;

namespace Klacks.UnitTest.FakeData
{
    internal static class CalendarRules
    {
        internal static List<CalendarRule> CalendarRuleList()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Deserialize<List<CalendarRule>>(FakeDateSerializeString.Data.calendarRuleList, options)!;
        }

        internal static CalendarRulesFilter CalendarRulesFilter()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Deserialize<CalendarRulesFilter>(FakeDateSerializeString.Data.filterCalendarRuleList, options)!;
        }

        internal static List<Countries> CountryList()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Deserialize<List<Countries>>(FakeDateSerializeString.Data.countryList, options)!;
        }

        internal static List<State> StateList()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Deserialize<List<State>>(FakeDateSerializeString.Data.stateList, options)!;
        }
    }
}
