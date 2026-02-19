using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Application.DTOs.Clients;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Staffs;
using System.Text.Json;

namespace Klacks.UnitTest.FakeData
{
    internal static class Clients
    {
        internal static FilterResource Filter()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Deserialize<FilterResource>(FakeDateSerializeString.Data.filterSimpleList, options)!;
        }

        internal static List<Absence> GenerateAbsences(int count)
        {
            List<Absence> absences = new List<Absence>();
            Random rand = new Random();

            for (int i = 0; i < count; i++)
            {
                bool weekendValue = rand.NextDouble() > 0.5;

                var absence = new Absence
                {
                    Id = Guid.NewGuid(),
                    Color = GenerateRandomColor(rand),
                    DefaultLength = rand.Next(1, 15),
                    DefaultValue = rand.NextDouble(),
                    Description = new MultiLanguage
                    {
                        De = $"Beschreibung_{i}",
                        En = $"Description_{i}",
                        Fr = $"DescriptionFr_{i}",
                        It = $"Descrizione_{i}"
                    },
                    HideInGantt = rand.NextDouble() > 0.5,
                    Name = new MultiLanguage
                    {
                        De = $"Abwesenheit_{i}",
                        En = $"Absence_{i}",
                        Fr = $"Absence_{i}",
                        It = $"Assenza_{i}"
                    },
                    Undeletable = rand.NextDouble() > 0.5,
                    WithHoliday = rand.NextDouble() > 0.5,
                    WithSaturday = weekendValue,
                    WithSunday = weekendValue,
                };

                absences.Add(absence);
            }

            return absences;
        }

        internal static ICollection<Address> GenerateAddresses(Guid clientId)
        {
            Random rand = new Random();
            DateTime start = DateTime.Now.AddYears(-1).AddDays(rand.Next(365)); // Zufälliges Datum im letzten Jahr
            return new List<Address>
        {
          new Address
          {
            ValidFrom = start,
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Street = "SomeStreet",
            Zip = "12345",
            City = "SomeCity",
            State = "SomeState",
            Country = "SomeCountry"
          }
        };
        }

        internal static BreakFilter GenerateBreakFilter(List<Absence> absences, int year)
        {
            var breakFilter = new BreakFilter
            {
                CurrentYear = year,
                Absences = new List<AbsenceTokenFilter>(),
            };

            return breakFilter;
        }

        internal static List<BreakPlaceholder> GenerateBreakPlaceholders(List<Client> clients, List<Absence> absences, int year, int count)
        {
            List<BreakPlaceholder> breakPlaceholders = new List<BreakPlaceholder>();

            createBreakPlaceholders(clients, breakPlaceholders, absences, year - 1, count);
            createBreakPlaceholders(clients, breakPlaceholders, absences, year, count);
            createBreakPlaceholders(clients, breakPlaceholders, absences, year + 1, count);

            return breakPlaceholders;
        }

        internal static List<Client> GenerateClients(int count, int year, bool withIncludes)
        {
            var clients = new List<Client>();

            for (int i = 0; i < count; i++)
            {
                var id = Guid.NewGuid();
                var client = new Client
                {
                    Id = id,
                    FirstName = GenerateMockName(i + 10),
                    Name = GenerateMockName(1) + i.ToString(),

                    Addresses = withIncludes ? GenerateAddresses(id) : new List<Address>(),
                    Communications = withIncludes ? GenerateCommunications(id) : new List<Communication>(),
                };

                var membership = GenerateMembershipForClient(client, year);
                client.Membership = membership;

                clients.Add(client);
            }

            return clients;
        }

        internal static ICollection<Communication> GenerateCommunications(Guid clientId)
        {
            return new List<Communication>
        {
            new Communication
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                Type = CommunicationTypeEnum.PrivateCellPhone,
                Value = "12301456014",
                Prefix = "041",
                Description = "SomeDescription"
            }
        };
        }

        internal static TruncatedClient TruncatedClient()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            return JsonSerializer.Deserialize<TruncatedClient>(FakeDateSerializeString.Data.clientsSimpleList, options)!;
        }

        internal static TruncatedClientResource TruncatedClientResource()
        {
            var truncatedClient = TruncatedClient();
            return new TruncatedClientResource
            {
                Clients = truncatedClient.Clients.Select(c => new ClientListItemResource
                {
                    Id = c.Id.ToString(),
                    IdNumber = c.IdNumber,
                    FirstName = c.FirstName ?? string.Empty,
                    Name = c.Name,
                    Company = c.Company ?? string.Empty,
                    Type = (int)c.Type,
                    IsDeleted = c.IsDeleted
                }).ToList(),
                MaxItems = truncatedClient.MaxItems,
                MaxPages = truncatedClient.MaxPages,
                CurrentPage = truncatedClient.CurrentPage,
                FirstItemOnPage = truncatedClient.FirstItemOnPage
            };
        }

        private static List<AbsenceTokenFilter> ConvertAbsencesToTokenFilters(List<Absence> absences)
        {
            return absences.Select(a => new AbsenceTokenFilter
            {
                Id = a.Id,
                Name = a.Name.De!,
                Checked = true
            }).ToList();
        }

        private static void createBreakPlaceholders(List<Client> clients, List<BreakPlaceholder> breakPlaceholders, List<Absence> absences, int year, int count)
        {
            Random rand = new Random();

            DateTime startOfYear = new DateTime(year, 1, 1);
            DateTime endOfYear = new DateTime(year, 12, 31);

            for (int i = 0; i < count; i++)
            {
                var client = clients[i];
                var absence = absences[rand.Next(absences.Count)];

                int range = (endOfYear - startOfYear).Days - absence.DefaultLength;
                DateTime start = startOfYear.AddDays(rand.Next(range));
                DateTime end = start.AddDays(absence.DefaultLength);
                var breakPlaceholderEntry = new BreakPlaceholder
                {
                    Absence = absence,
                    AbsenceId = absence.Id,
                    Client = client,
                    ClientId = client.Id,
                    Information = $"BreakPlaceholder_{i}",
                    From = start,
                    Until = end
                };

                breakPlaceholders.Add(breakPlaceholderEntry);
            }
        }

        private static Membership GenerateMembershipForClient(Client client, int year)
        {
            Random rand = new Random();

            DateTime startOfYear = new DateTime(year, 1, 1);
            DateTime start = startOfYear.AddYears(-2).AddDays(rand.Next(365));
            DateTime? end = (rand.Next(2) == 0) ? null : start.AddYears(3);

            var membership = new Membership
            {
                Id = Guid.NewGuid(),
                Client = client,
                ClientId = client.Id,
                ValidFrom = start,
                ValidUntil = end,
                Type = rand.Next(5)
            };

            return membership;
        }

        private static string GenerateMockName(int index)
        {
            char[] name = new char[6]; // Nehmen wir an, dass jeder Name 6 Buchstaben hat

            for (int i = 0; i < name.Length; i++)
            {
                int asciiValue = 65 + (index + i) % 26; // 65 ist der ASCII-Wert für "A"
                name[i] = (char)asciiValue;
            }

            return new string(name);
        }

        private static string GenerateRandomColor(Random rand)
        {
            string[] colorComponents = new string[3];
            for (int i = 0; i < 3; i++)
            {
                colorComponents[i] = rand.Next(0, 256).ToString("X2");
            }
            return $"#{colorComponents[0]}{colorComponents[1]}{colorComponents[2]}";
        }
    }
}
