/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using QuantConnect.Orders;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Brokerages.Binance
{
    /// <summary>
    /// Binance utility methods
    /// </summary>
    public partial class BinanceBrokerage
    {
        private static OrderStatus ConvertOrderStatus(string raw)
        {
            switch (raw.LazyToUpper())
            {
                case "NEW":
                    return OrderStatus.New;

                case "PARTIALLY_FILLED":
                    return OrderStatus.PartiallyFilled;

                case "FILLED":
                    return OrderStatus.Filled;

                case "PENDING_CANCEL":
                    return OrderStatus.CancelPending;

                case "CANCELED":
                    return OrderStatus.Canceled;

                case "REJECTED":
                case "EXPIRED":
                    return OrderStatus.Invalid;

                default:
                    return OrderStatus.None;
            }
        }
        private Holding ConvertHolding(IOrderedEnumerable<Messages.TradeList> trades)
        {
            if (trades.Count() == 0)
                return new Holding();

            var symbol = trades.FirstOrDefault().Symbol;
            var qty = 0m;
            var avgPrice = 0m;
            foreach (var trade in trades)
            {
                if (!trade.IsBuyer)
                    break;
                avgPrice = (avgPrice * qty + trade.Price * trade.Qty) / (qty + trade.Qty);
                qty += trade.Qty;
            }
            var holding = new Holding
            {
                Symbol = _symbolMapper.GetLeanSymbol(symbol, SecurityType.Crypto, Market.Binance),
                AveragePrice = avgPrice,
                Quantity = qty,
                CurrencySymbol = "$",
                Type = SecurityType.Crypto,
            };

            return holding;
        }

    }
}
