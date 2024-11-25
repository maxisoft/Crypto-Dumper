# Okx Swap Data

## Summary
- Use a [memory mappable](https://numpy.org/doc/stable/reference/generated/numpy.memmap.html) format. 
- All data are stored as `double` precison floating number aka `np.float64`
- Follow the /okx/swap/\<*instrument*\>/\<*first_timestamp_ms*\>.mm file pattern
- Use the matrix 2d shape `(-1, 16)` (-1 means one can compute this dim according to file size as numpy would do)

## Columns description

| index | name | desc |
|--|--| --|
| 0 | Timestamp | raw Timestamp in millisecond |
| 1 | NextFundingTime | millisecond to wait until next funding time |
| 2 | FundingRate | raw [Funding Rate](https://dataguide.cryptoquant.com/market-data-indicators/funding-rates) |
| 3 | NextFundingRate | next raw funding rate |
| 4 | OpenInterest | edited [Open Interest](https://dataguide.cryptoquant.com/market-data-indicators/open-interest) |
| 5 | OpenInterestInCurrency | raw Open Interest In Currency |
| 6 | SpreadPercent | [spread](https://www.investopedia.com/terms/s/spread.asp) in % between best ask and bid |
| 7 | SpreadToMarkPercent | [spread](https://www.investopedia.com/terms/s/spread.asp) in % between [mark price](https://www.okx.com/learn/understanding-mark-price) and current swap price |
| 8 | AskSize | best ask size |
| 9 | BidSize | best bid size |
| 10 | Change24HPercent | change % of a 24hr price rolling window |
| 11 | ChangeTodayPercent | change % between the current price and the price at 00:00 UTC |
| 12 | ChangeTodayChinaPercent | change % between the current price and the price at 00:00 +8UTC |
| 13 | Volume24H | rolling sum of the traded volume in a 24hr window |
| 14 | PriceRatio24H | ratio of current price according to 24hr low/high |
| 15 | LastPrice | raw last price |


## Notes  

- use ask-bid midpoint `(bid + ask) / 2` when revelent as last price instead of last traded price
- the `OpenInterest` is transformed to take into account the `ctVal` of a given instrument
- in addition, when `OpenInterest` *==* `OpenInterestInCurrency`, `OpenInterest` is multiplied by current mark price in order to suit the following equation: `OpenInterest / OpenInterestInCurrency ~= current mark price`