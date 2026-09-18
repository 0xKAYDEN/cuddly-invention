# Crypto Arbitrage Scanner (C#)

A read-only .NET 8 console application that compares live **spot best bid/ask** prices on Binance, Bybit and OKX. It reports the best buy exchange, best sell exchange, raw spread, estimated quantity for a USD budget, trading fees, optional withdrawal cost, and estimated net profit.

> This is a market-data scanner, not an auto-trading bot. It never places orders and does not need API keys.

## Why bid/ask and not the displayed price?

A realistic cross-exchange check buys at the **ask** and sells at the **bid**. Using last-traded prices can make an opportunity look profitable when it is not. The scanner uses only USDT spot pairs that exist on both exchanges.

For a $100 budget the calculation is:

```text
quantity       = budget / (buy ask × (1 + buy taker fee))
buy fee        = quantity × buy ask × buy taker fee
sell proceeds  = quantity × sell bid
sell fee       = sell proceeds × sell taker fee
net profit     = sell proceeds - budget - sell fee - withdrawal cost
```

The default taker rate is 0.10% for each leg, but fees vary by account tier, region, product, fee-token discounts and whether an order is maker or taker. Change them in `appsettings.json`. `WithdrawalUsd` is optional and defaults to zero because withdrawal fees depend on the asset and network.

## Run

Requires the .NET 8 SDK:

```bash
dotnet restore
dotnet run -- --capital 100 --min-profit 0.01
```

Useful settings:

- `--capital 100` overrides the configured USD budget.
- `--min-profit 0.01` only prints opportunities with at least $0.01 estimated net profit.
- `Scanner:Fees:<Exchange>:TakerRate` is a decimal rate, so `0.001` means 0.10%.
- `Scanner:Fees:<Exchange>:WithdrawalUsd` is a fixed USD estimate for the transfer leg.

Logs are written to the console and `logs/scanner-YYYYMMDD.log` through Serilog.

## Important limitations

- Prices are fetched concurrently over REST and are not an atomic snapshot. A spread may disappear before two orders fill.
- The result assumes funds are already on both exchanges. If the asset must be transferred between them, account for withdrawal fees, deposit/network delays, minimums and chain congestion. The simple fixed USD setting is only an estimate.
- This version uses top-of-book prices and does not model order-book depth, slippage, partial fills, spread widening, stablecoin depeg, taxes, limits, or regional product restrictions. A $100 order can still move an illiquid market.
- A displayed opportunity is not a guaranteed profit. Validate with small paper/live-sized orders, exchange rules, and your own fee schedule before taking any risk.
- Public APIs can rate-limit or temporarily fail; the program logs a failed exchange and continues with the remaining responses.

## Sources consulted

The implementation uses the documented public market-data endpoints:

- [Binance Spot API, book ticker](https://binance-docs.github.io/apidocs/spot/en/#symbol-order-book-ticker)
- [Bybit V5 market tickers](https://bybit-exchange.github.io/docs/v5/market/tickers)
- [OKX V5 market data tickers](https://www.okx.com/docs-v5/en/#rest-api-market-data-get-tickers)
- [OKX fee-rate API](https://www.okx.com/docs-v5/en/#trading-account-rest-api-get-fee-rates)

As a sanity check on typical base rates, the comparison research returned approximately 0.10% standard taker fees for Binance, Bybit and OKX, but rates are account-specific and should be confirmed in each exchange account. See also the [Binance fee schedule](https://www.binance.com/en/fee/trading), [Bybit fee schedule](https://www.bybit.com/en/help-center/article/Trading-Fee-Structure/), and [OKX fee schedule](https://www.okx.com/fees).
