using System.Globalization;
using System.Net.Mail;

namespace AstraJourneyPriceCalculation;

public enum DestinationType { Moon, Mars, Europa }
public enum TravelClassType { Explorer, Premium, FirstClass }
public enum MissionStatus { Open, Limited, Planned }

public sealed record BookingRequest(
    string FullName, string Email, DestinationType Destination,
    int PassengerCount, TravelClassType TravelClass);

public sealed record ImportError(int LineNumber, string RawLine, string Message);
public sealed record ImportResult(IReadOnlyList<BookingRequest> Bookings, IReadOnlyList<ImportError> Errors);
public sealed record Mission(DestinationType Destination, decimal BasePrice, DateOnly DepartureDate, string Duration, MissionStatus Status);
public sealed record PreparedJourney(BookingRequest Booking, Mission Mission);
public sealed record PricedJourney(PreparedJourney Journey, decimal FarePerPassenger);
public sealed record BookingQuote(
    string FullName, string Email, DestinationType Destination,
    DateOnly DepartureDate, string Duration, int PassengerCount,
    TravelClassType TravelClass, MissionStatus Status,
    decimal FarePerPassenger, decimal TotalPrice, bool CanBook);
public sealed record JourneySummary(IReadOnlyList<BookingQuote> Quotes, decimal TotalEstimatedPrice);

public static class BookingImporter
{
    public static bool TryParseBooking(
        string? rawLine, out BookingRequest? booking, out string error)
    {
        booking = null;
        error = "";
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            error = "Booking input is missing or blank.";
            return false;
        }
        string[] parts = rawLine.Split('|');
        if (parts.Length != 5)
        {
            error = "Expected 5 fields: FullName|Email|Destination|Passengers|TravelClass.";
            return false;
        }
        string name = parts[0].Trim();
        string email = parts[1].Trim();
        if (name.Length < 2 || name.Length > 80)
        {
            error = "Full name must contain 2 to 80 characters.";
            return false;
        }
        if (!MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
        {
            error = "A valid plain email address is required.";
            return false;
        }
        DestinationType? destination = parts[2].Trim().ToLowerInvariant() switch
        {
            "moon" => DestinationType.Moon,
            "mars" => DestinationType.Mars,
            "europa" => DestinationType.Europa,
            _ => null
        };
        if (destination is null)
        {
            error = "Destination must be Moon, Mars or Europa.";
            return false;
        }
        if (!int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int passengers) || passengers < 1 || passengers > 6)
        {
            error = "Passenger count must be an integer from 1 to 6.";
            return false;
        }
        TravelClassType? travelClass = parts[4].Trim().ToLowerInvariant() switch
        {
            "explorer" => TravelClassType.Explorer,
            "premium" => TravelClassType.Premium,
            "first" or "firstclass" or "first class" => TravelClassType.FirstClass,
            _ => null
        };
        if (travelClass is null)
        {
            error = "Travel class must be Explorer, Premium or First Class.";
            return false;
        }
        booking = new BookingRequest(name, email, destination.Value,
            passengers, travelClass.Value);
        return true;
    }

    public static ImportResult Import(IEnumerable<string?>? lines)
    {
        if (lines is null)
            throw new ArgumentNullException(nameof(lines), "Input dataset is missing.");
        var bookings = new List<BookingRequest>();
        var errors = new List<ImportError>();
        int lineNumber = 0;
        foreach (string? line in lines)
        {
            lineNumber++;
            if (TryParseBooking(line, out var booking, out string error) && booking is not null)
                bookings.Add(booking);
            else
                errors.Add(new ImportError(lineNumber, line ?? "<null>", error));
        }
        return new ImportResult(Array.AsReadOnly(bookings.ToArray()), Array.AsReadOnly(errors.ToArray()));
    }
}

public static class AstraJourneyPricePipeline
{
    public static Mission GetMission(DestinationType destination) => destination switch
    {
        DestinationType.Moon => new Mission(destination, 49_000m,
            new DateOnly(2030, 6, 15), "7 days", MissionStatus.Open),
        DestinationType.Mars => new Mission(destination, 2_400_000m,
            new DateOnly(2030, 9, 10), "180 days", MissionStatus.Limited),
        DestinationType.Europa => new Mission(destination, 8_700_000m,
            new DateOnly(2031, 3, 20), "2 years", MissionStatus.Planned),
        _ => throw new ArgumentOutOfRangeException(nameof(destination))
    };

    public static decimal GetClassMultiplier(TravelClassType travelClass) => travelClass switch
    {
        TravelClassType.Explorer => 1m,
        TravelClassType.Premium => 1.35m,
        TravelClassType.FirstClass => 1.80m,
        _ => throw new ArgumentOutOfRangeException(nameof(travelClass))
    };

    public static IReadOnlyList<PreparedJourney> PrepareJourneys(IEnumerable<BookingRequest> bookings)
    {
        return Array.AsReadOnly(bookings
            .Select(b => new PreparedJourney(b, GetMission(b.Destination)))
            .ToArray());
    }

    public static decimal StandardPricing(PreparedJourney journey) =>
        journey.Mission.BasePrice * GetClassMultiplier(journey.Booking.TravelClass);

    public static Func<PreparedJourney, decimal> CreateDiscountPolicy(decimal discountRate)
    {
        if (discountRate < 0m || discountRate > 1m)
            throw new ArgumentOutOfRangeException(nameof(discountRate));
        return journey => StandardPricing(journey) * (1m - discountRate);
    }


    public static IReadOnlyList<PricedJourney> CalculatePrices(
        IEnumerable<PreparedJourney> journeys, Func<PreparedJourney, decimal> policy)
    {
        var priced = new List<PricedJourney>();
        foreach (var journey in journeys)
        {
            decimal fare = policy(journey);
            if (fare < 0m)
                throw new InvalidOperationException("Policy returned a negative fare.");
            priced.Add(new PricedJourney(journey, decimal.Round(fare, 2, MidpointRounding.AwayFromZero)));
        }
        return Array.AsReadOnly(priced.ToArray());
    }

    public static JourneySummary BuildSummary(IEnumerable<PricedJourney> priced)
    {
        var quotes = priced.Select(p => new BookingQuote(
                p.Journey.Booking.FullName, p.Journey.Booking.Email,
                p.Journey.Booking.Destination, p.Journey.Mission.DepartureDate,
                p.Journey.Mission.Duration, p.Journey.Booking.PassengerCount,
                p.Journey.Booking.TravelClass, p.Journey.Mission.Status,
                p.FarePerPassenger,
                p.FarePerPassenger * p.Journey.Booking.PassengerCount,
                p.Journey.Mission.Status != MissionStatus.Planned))
            .OrderBy(q => q.DepartureDate)
            .ThenBy(q => q.Email, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new JourneySummary(Array.AsReadOnly(quotes), quotes.Sum(q => q.TotalPrice));
    }

    public static JourneySummary Run(
        IEnumerable<BookingRequest> bookings, Func<PreparedJourney, decimal> policy)
    {
        var prepared = PrepareJourneys(bookings);
        var priced = CalculatePrices(prepared, policy);
        return BuildSummary(priced);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--test")
            return AutomatedTests.RunAll();
        if (args.Length > 1)
        {
            Console.Error.WriteLine("Usage: dotnet run [bookings.txt | --test]");
            return 1;
        }
        try
        {
            string[] lines = args.Length == 1 ? File.ReadAllLines(args[0]) : SampleLines();
            var imported = BookingImporter.Import(lines);
            foreach (var error in imported.Errors)
                Console.WriteLine($"REJECTED line {error.LineNumber}: {error.Message}");
            Console.WriteLine($"Accepted: {imported.Bookings.Count}; rejected: {imported.Errors.Count}");
            Func<PreparedJourney, decimal> standard = AstraJourneyPricePipeline.StandardPricing;
            Func<PreparedJourney, decimal> promotional =
                AstraJourneyPricePipeline.CreateDiscountPolicy(0.10m);
            Display("Standard pricing", AstraJourneyPricePipeline.Run(imported.Bookings, standard));
            Display("Promotional pricing: 10% discount",
                AstraJourneyPricePipeline.Run(imported.Bookings, promotional));
            return imported.Errors.Count == 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Input error: {ex.Message}");
            return 1;
        }
    }

    public static string[] SampleLines() => new[]
    {
        "Alex Moon|alex@example.com|Moon|1|Explorer",
        "Dana Moon|dana@example.com|Moon|2|Premium",
        "Sam Mars|sam@example.com|Mars|6|FirstClass",
        "Eva Europa|eva@example.com|Europa|1|Explorer",
        "Invalid Count|count@example.com|Moon|0|Explorer",
        "Invalid Email|-|Moon|2|Premium"
    };

    private static void Display(string title, JourneySummary summary)
    {
        Console.WriteLine($"\n{title}");
        foreach (var q in summary.Quotes)
        {
            Console.WriteLine($"{q.FullName} | {q.Destination} | {q.DepartureDate:yyyy-MM-dd} | " +
                $"{q.PassengerCount} passenger(s) | {q.TravelClass} | " +
                $"{(q.CanBook ? "Available for booking" : "Preview only")} | " +
                $"Fare: {Usd(q.FarePerPassenger)} | Total: {Usd(q.TotalPrice)}");
        }
        Console.WriteLine($"Total estimate (includes previews): {Usd(summary.TotalEstimatedPrice)}");
    }
    private static string Usd(decimal amount) =>
        amount.ToString("N2", CultureInfo.InvariantCulture) + " USD";
}

public static class AutomatedTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static BookingRequest Booking(int passengers = 2) =>
        new("Test Person", "test@example.com", DestinationType.Moon,
            passengers, TravelClassType.Premium);
    private static JourneySummary Standard(IEnumerable<BookingRequest> bookings) =>
        AstraJourneyPricePipeline.Run(bookings, AstraJourneyPricePipeline.StandardPricing);

    public static int RunAll()
    {
        var tests = new (string Name, Action Body)[]
        {
            ("Normal complete workflow", () =>
            {
                var imported = BookingImporter.Import(Program.SampleLines());
                Check(imported.Bookings.Count == 4 && imported.Errors.Count == 2, "Import counts differ.");
                var result = Standard(imported.Bookings);
                Check(result.Quotes.Count == 4, "Quote count differs.");
                Check(result.TotalEstimatedPrice == 34_801_300m, "Standard aggregate differs.");
                Check(result.Quotes.Single(q => q.Email == "dana@example.com").TotalPrice == 132_300m,
                    "Moon Premium quote differs.");
            }),
            ("Valid empty dataset", () =>
            {
                var imported = BookingImporter.Import(Array.Empty<string>());
                var result = Standard(imported.Bookings);
                Check(imported.Errors.Count == 0 && result.Quotes.Count == 0 &&
                    result.TotalEstimatedPrice == 0m, "Empty input is not handled correctly.");
            }),
            ("Passenger boundaries", () =>
            {
                foreach (int n in new[] { 1, 6 })
                {
                    Check(BookingImporter.TryParseBooking($"Test Person|test@example.com|Moon|{n}|Explorer",
                        out var booking, out _) && booking is not null, "Valid boundary rejected.");
                    Check(Standard(new[] { Booking(n) }).Quotes[0].TotalPrice == 66_150m * n,
                        "Boundary price differs.");
                }
                foreach (int n in new[] { 0, 7 })
                    Check(!BookingImporter.TryParseBooking($"Test Person|test@example.com|Moon|{n}|Explorer",
                        out _, out _), "Invalid boundary accepted.");
            }),
            ("Malformed and missing fields", () =>
            {
                string?[] invalid = { null, "", "A|b", "Test Person|-|Moon|2|Explorer",
                    "Test Person|test@example.com|Unknown|2|Explorer",
                    "Test Person|test@example.com|Moon|2|Unknown",
                    "Test Person|test@example.com|Moon|two|Explorer",
                    "|test@example.com|Moon|2|Explorer" };
                foreach (var line in invalid)
                    Check(!BookingImporter.TryParseBooking(line, out var booking, out string error) &&
                        booking is null && error.Length > 0, "Invalid input fabricated a success.");
            }),
            ("Missing dataset is not empty dataset", () =>
            {
                bool rejected = false;
                try { BookingImporter.Import(null); }
                catch (ArgumentNullException) { rejected = true; }
                Check(rejected, "Missing dataset was not rejected.");
            }),
            ("Two policies on the same dataset", () =>
            {
                var bookings = BookingImporter.Import(Program.SampleLines()).Bookings;
                var normal = Standard(bookings);
                var discounted = AstraJourneyPricePipeline.Run(bookings,
                    AstraJourneyPricePipeline.CreateDiscountPolicy(0.10m));
                Check(normal.TotalEstimatedPrice == 34_801_300m &&
                    discounted.TotalEstimatedPrice == 31_321_170m, "Policy results differ from expectations.");
            }),
            ("Individual stage, purity and policy invocation", () =>
            {
                var input = new List<BookingRequest> { Booking() };
                var before = input.ToArray();
                var prepared = AstraJourneyPricePipeline.PrepareJourneys(input);
                Check(prepared[0].Mission.DepartureDate == new DateOnly(2030, 6, 15), "Schedule differs.");
                int calls = 0;
                var priced = AstraJourneyPricePipeline.CalculatePrices(prepared, journey =>
                {
                    calls++;
                    return AstraJourneyPricePipeline.StandardPricing(journey);
                });
                var result = AstraJourneyPricePipeline.BuildSummary(priced);
                Check(calls == 1 && result.Quotes[0].TotalPrice == 132_300m, "Policy invocation differs.");
                Check(input.SequenceEqual(before), "Caller-owned input was changed.");
                Check(Standard(input).Quotes.SequenceEqual(Standard(input).Quotes), "Output is unstable.");
            }),
            ("Stable captured configuration", () =>
            {
                decimal discount = 0.10m;
                var policy = AstraJourneyPricePipeline.CreateDiscountPolicy(discount);
                discount = 0.50m;
                var result = AstraJourneyPricePipeline.Run(new[] { Booking() }, policy);
                Check(result.Quotes[0].TotalPrice == 119_070m, "Captured configuration changed.");
            }),
            ("Europa preview and correct year", () =>
            {
                var booking = Booking() with { Destination = DestinationType.Europa };
                var quote = Standard(new[] { booking }).Quotes[0];
                Check(!quote.CanBook && quote.Status == MissionStatus.Planned &&
                    quote.DepartureDate == new DateOnly(2031, 3, 20), "Europa preview differs.");
            })
        };
        int failed = 0;
        foreach (var test in tests)
        {
            try { test.Body(); Console.WriteLine($"PASS: {test.Name}"); }
            catch (Exception ex) { failed++; Console.WriteLine($"FAIL: {test.Name}: {ex.Message}"); }
        }
        Console.WriteLine($"Tests: {tests.Length}; passed: {tests.Length - failed}; failed: {failed}");
        return failed == 0 ? 0 : 1;
    }
}