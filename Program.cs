using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;

const string appName = "crypto-arbitrage-scanner";
var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("ARB_")
    .Build();

var capital = GetDouble(args, "--capital", configuration["Scanner:CapitalUsd"], 100d);
var minimumProfit = GetDouble(args, "--min-profit", configuration["Scanner:MinimumNetProfitUsd"], 0d);
var topResults = GetInt(configuration["Scanner:TopResults"], 20);
var timeoutSeconds = GetInt(configuration["Scanner:RequestTimeoutSeconds"], 10);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.WithProperty("Application", appName)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/scanner-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    if (capital <= 0) throw new ArgumentException("Capital must be greater than zero.");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CryptoArbitrageScanner/1.0");

    var exchanges = new IExchangeClient[]
    {
        new BinanceClient(http), new BybitClient(http), new OkxClient(http)
    };

    Log.Information("Fetching spot bid/ask prices from {Count} exchanges...", exchanges.Length);
    var snapshots = await Task.WhenAll(exchanges.Select(async exchange =>
    {
        try { return await exchange.GetQuotesAsync(); }
        catch (Exception ex) { Log.Warning(ex, "Could not fetch {Exchange} prices", exchange.Name); return []; }
    }));

    var quotes = snapshots.SelectMany(x => x).ToList();
    if (quotes.Count == 0) throw new InvalidOperationException("No market data was returned.");

    var fees = new Dictionary<string, FeeSettings>(StringComparer.OrdinalIgnoreCase)
    {
        ["Binance"] = ReadFee(configuration, "Binance"),
        ["Bybit"] = ReadFee(configuration, "Bybit"),
        ["OKX"] = ReadFee(configuration, "OKX")
    };

    var opportunities = FindOpportunities(quotes, fees, capital)
        .Where(x => x.NetProfitUsd >= minimumProfit)
        .OrderByDescending(x => x.NetProfitUsd)
        .Take(topResults)
        .ToList();

    Console.WriteLine();
    Console.WriteLine($"Cross-exchange spot scan | capital: {capital.ToString("C2", CultureInfo.InvariantCulture)} | quotes: {quotes.Count}");
    Console.WriteLine("Assumption: funds are already available on both exchanges; transfer time/cost is not simulated unless configured.");
    Console.WriteLine();
    if (opportunities.Count == 0)
    {
        Console.WriteLine("No opportunities met the configured net-profit threshold.");
        return;
    }

    foreach (var x in opportunities)
    {
        Console.WriteLine($"{x.Symbol,-12} BUY {x.BuyExchange,-7} {x.BuyAsk,14:F8}  -> SELL {x.SellExchange,-7} {x.SellBid,14:F8}");
        Console.WriteLine($"  spread {x.SpreadPercent,7:F3}% | qty {x.Quantity:F8} | gross {x.GrossProfitUsd,8:C2} | fees {x.TradingFeesUsd,8:C2} + withdrawal {x.WithdrawalUsd,8:C2} | NET {x.NetProfitUsd,8:C2}");
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Scanner stopped unexpectedly");
    Environment.ExitCode = 1;
}
finally { await Log.CloseAndFlushAsync(); }

static double GetDouble(string[] args, string name, string? configured, double fallback)
{
    var arg = args.SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
    return double.TryParse(arg ?? configured, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
static int GetInt(string? value, int fallback) => int.TryParse(value, out var i) ? i : fallback;
static FeeSettings ReadFee(IConfiguration config, string exchange) => new(
    GetDouble([], "", config[$"Scanner:Fees:{exchange}:TakerRate"], 0.001),
    GetDouble([], "", config[$"Scanner:Fees:{exchange}:WithdrawalUsd"], 0));

static IEnumerable<Opportunity> FindOpportunities(IEnumerable<Quote> quotes, Dictionary<string, FeeSettings> fees, double capital)
{
    foreach (var group in quotes.GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase))
    foreach (var buy in group)
    foreach (var sell in group)
    {
        if (buy.Exchange == sell.Exchange || buy.Ask <= 0 || sell.Bid <= 0) continue;
        var buyRate = fees[buy.Exchange].TakerRate;
        var quantity = capital / (buy.Ask * (1 + buyRate));
        var buyNotional = quantity * buy.Ask;
        var sellNotional = quantity * sell.Bid;
        var buyFee = buyNotional * buyRate;
        var sellFee = sellNotional * fees[sell.Exchange].TakerRate;
        var withdrawal = fees[buy.Exchange].WithdrawalUsd;
        var net = sellNotional - capital - sellFee - withdrawal;
        yield return new Opportunity(group.Key, buy.Exchange, sell.Exchange, buy.Ask, sell.Bid, quantity,
            (sell.Bid / buy.Ask - 1) * 100, sellNotional - buyNotional, buyFee + sellFee, withdrawal, net);
    }
}

record FeeSettings(double TakerRate, double WithdrawalUsd);
record Opportunity(string Symbol, string BuyExchange, string SellExchange, double BuyAsk, double SellBid,
    double Quantity, double SpreadPercent, double GrossProfitUsd, double TradingFeesUsd, double WithdrawalUsd, double NetProfitUsd);
record Quote(string Exchange, string Symbol, double Bid, double Ask);

interface IExchangeClient { string Name { get; } Task<IReadOnlyList<Quote>> GetQuotesAsync(); }

abstract class ExchangeClient(HttpClient http) : IExchangeClient
{
    protected readonly HttpClient Http = http;
    public abstract string Name { get; }
    public abstract Task<IReadOnlyList<Quote>> GetQuotesAsync();
    protected static double Number(JsonElement value) => double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;
    protected static bool IsUsdt(string symbol) => symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase);
}

sealed class BinanceClient(HttpClient http) : ExchangeClient(http)
{
    public override string Name => "Binance";
    public override async Task<IReadOnlyList<Quote>> GetQuotesAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.binance.com/api/v3/ticker/bookTicker"));
        return doc.RootElement.EnumerateArray().Select(x => (Symbol: x.GetProperty("symbol").GetString()!, Bid: Number(x.GetProperty("bidPrice")), Ask: Number(x.GetProperty("askPrice"))))
            .Where(x => IsUsdt(x.Symbol) && x.Bid > 0 && x.Ask > 0).Select(x => new Quote(Name, x.Symbol[..^4] + "/USDT", x.Bid, x.Ask)).ToList();
    }
}

sealed class BybitClient(HttpClient http) : ExchangeClient(http)
{
    public override string Name => "Bybit";
    public override async Task<IReadOnlyList<Quote>> GetQuotesAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.bybit.com/v5/market/tickers?category=spot"));
        return doc.RootElement.GetProperty("result").GetProperty("list").EnumerateArray()
            .Select(x => (Symbol: x.GetProperty("symbol").GetString()!, Bid: Number(x.GetProperty("bid1Price")), Ask: Number(x.GetProperty("ask1Price"))))
            .Where(x => IsUsdt(x.Symbol) && x.Bid > 0 && x.Ask > 0).Select(x => new Quote(Name, x.Symbol[..^4] + "/USDT", x.Bid, x.Ask)).ToList();
    }
}

sealed class OkxClient(HttpClient http) : ExchangeClient(http)
{
    public override string Name => "OKX";
    public override async Task<IReadOnlyList<Quote>> GetQuotesAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://www.okx.com/api/v5/market/tickers?instType=SPOT"));
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(x => (Symbol: x.GetProperty("instId").GetString()!, Bid: Number(x.GetProperty("bidPx")), Ask: Number(x.GetProperty("askPx"))))
            .Where(x => x.Symbol.EndsWith("-USDT", StringComparison.OrdinalIgnoreCase) && x.Bid > 0 && x.Ask > 0).Select(x => new Quote(Name, x.Symbol[..^5] + "/USDT", x.Bid, x.Ask)).ToList();
    }
}
