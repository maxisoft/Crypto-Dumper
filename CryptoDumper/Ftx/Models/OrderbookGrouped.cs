﻿using Maxisoft.Utils.Collections.Lists;

namespace CryptoDumper.Ftx.Models;

public record GroupedOrderbook(PriceSizePair[] Bids, PriceSizePair[] Asks)
{
    public static readonly GroupedOrderbook Empty =
        new GroupedOrderbook(Array.Empty<PriceSizePair>(), Array.Empty<PriceSizePair>());
}