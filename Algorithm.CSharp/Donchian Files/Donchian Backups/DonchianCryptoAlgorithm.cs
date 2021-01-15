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

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Brokerages;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Interfaces;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public class DonchianCryptoAlgorithm : QCAlgorithm
    {
        private List<SymbolDirection> symbols = new List<SymbolDirection>();

        private Dictionary<string, SymbolData> SymbolInfo = new Dictionary<string, SymbolData>();
        private Dictionary<string, decimal> Prices = new Dictionary<string, decimal>();
        private Dictionary<string, decimal> _Open = new Dictionary<string, decimal>();
        private Dictionary<string, decimal> _Close = new Dictionary<string, decimal>();
        BinanceMarginOrderProperties _liquidate = new BinanceMarginOrderProperties();

        public Resolution resolution = Resolution.Hour;

        decimal risk = .1m;
        decimal initialrisk;
        double startingequity = 30000;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.BinanceMargin);
            SetStartDate(2018, 1, 1);

            SetAccountCurrency("BTC");
            SetCash(startingequity);
            SetWarmUp(180);
            SetBenchmark(SecurityType.Crypto, "BTCUSDT");
            DefaultOrderProperties = new BinanceMarginOrderProperties();

            symbols.Add(new SymbolDirection("BNBBTC", "BNB", Direction.Long));
            symbols.Add(new SymbolDirection("ADABTC", "ADA", Direction.Both));
            symbols.Add(new SymbolDirection("ETHBTC", "ETH", Direction.Both));
            symbols.Add(new SymbolDirection("LTCBTC", "LTC", Direction.Both));
            symbols.Add(new SymbolDirection("BCHBTC", "BCH", Direction.Both));
            symbols.Add(new SymbolDirection("MATICBTC", "MATIC", Direction.Both));
            symbols.Add(new SymbolDirection("EOSBTC", "EOS", Direction.Both));
            symbols.Add(new SymbolDirection("XLMBTC", "XLM", Direction.Both));
            symbols.Add(new SymbolDirection("XTZBTC", "XTZ", Direction.Both));
            symbols.Add(new SymbolDirection("ZECBTC", "ZEC", Direction.Both));
            symbols.Add(new SymbolDirection("TRXBTC", "TRX", Direction.Short));
            symbols.Add(new SymbolDirection("XRPBTC", "XRP", Direction.Short));
            symbols.Add(new SymbolDirection("DASHBTC", "DASH", Direction.Short));

            _liquidate.AutoBorrow = false;
            _liquidate.AutoRepay = true;
            _liquidate.PostOnly = false;

            // Initail Symbol Data for Initializing Symbol List
            initialrisk = risk;
            
            foreach (var i in symbols)
            {
                string index = i.Symbol;
                SymbolInfo[index] = new SymbolData(index);
               // Debug("Attempting to add " + SymbolInfo.Count().ToString() + " Symbols to the Algoithim");
                // Add Asset Classes to QuantBook
                AddCrypto(index, resolution);
                Securities[index].SetLeverage(10);

                Prices[index] = 0;
                _Close[index] = 0;
                _Open[index] = 0;
                // Establish Symbol Data for Index
                if (SymbolInfo.ContainsKey(index))
                {
                    SymbolInfo[index].ATR = ATR(index, 30, MovingAverageType.Simple, resolution);
                    SymbolInfo[index].VWAP_week = VWAP(SymbolInfo[index].Symbol);
                    SymbolInfo[index].BuyQuantity = 0;
                    SymbolInfo[index].SellQuantity = 0;
                    SymbolInfo[index].Donchian = DCH(SymbolInfo[index].Symbol, 15, 15, resolution);
                    SymbolInfo[index].TrendMA = LWMA(index, 25, Resolution.Daily);

                    /*var history = History(index, TimeSpan.FromDays(5), Resolution.Hour);
                    foreach (TradeBar bar in history)
                    {
                        SymbolInfo[index].VWAP_week.Update(bar);
                    }*/

                    Debug("Attempting to add " + index + " to the Algoithim");

                    SymbolInfo[index].TrendMA.Updated += (sender, updated) => SymbolInfo[index].TrendMAWindow.Add(updated);
                }
            }
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public void OnData(Slice data)
        {
            if (!IsWarmingUp)
                Debug($"Total Portfolio Value: {Portfolio.TotalPortfolioValue}");
            foreach (var index in symbols)
            {
                if (IsWarmingUp)
                    continue;
                string i = index.Symbol;
                var shortsymbol = index.Asset;
                Debug($"Data received for {index.Symbol}, Open Quantity: {Portfolio.CashBook[shortsymbol].Amount}");
                try
                {
                    // Indicator Checks
                    if (!SymbolInfo[i].ATR.IsReady)
                    {
                        Debug("ATR Not Ready");
                        continue;
                    }
                    if (!SymbolInfo[i].Donchian.IsReady)
                    {
                        Debug("Donchian Not Ready");
                        continue;
                    }
                    if (!SymbolInfo[i].VWAP_week.IsReady)
                    {
                        Debug("VWAP Not Ready");
                        continue;
                    }
                }
                catch { Debug("Error Processing Indicator Checks"); }

                try
                {
                    if (data.ContainsKey(SymbolInfo[i].Symbol))
                    {
                        SymbolInfo[i].OpenLongEntry = SymbolInfo[i].Donchian.UpperBand.Current.Value;
                        SymbolInfo[i].OpenShortEntry = SymbolInfo[i].Donchian.LowerBand.Current.Value;
                        SymbolInfo[i].StopInTicks = SymbolInfo[i].ATR.Current.Value;
                        var _openQty = 0m;

                        if (data[i].Close != null && data[i].Open != null && data[i].Price != null)
                        {
                            Prices[i] = data[i].Close;
                            _Close[i] = data[i].Close;
                            _Open[i] = data[i].Open;
                            if (data[i].EndTime.Minute != 0 && Portfolio.CashBook[shortsymbol].Amount == 0)
                                continue;
                            else
                            {
                                if (shortsymbol == "BNB" && Portfolio.CashBook[shortsymbol].Amount < 2)
                                {
                                    var bnbOrder = BinanceMarginMarketOrder(i, -(Portfolio.CashBook[shortsymbol].Amount - 2), _liquidate);
                                }

                                _openQty = shortsymbol == "BNB" ? Math.Max(Portfolio.CashBook[shortsymbol].Amount - 2, 0) : Portfolio.CashBook[shortsymbol].Amount;
                            }
                        }
                        else
                        {
                            continue;
                        }

                        if (_openQty > 0 && SymbolInfo[i].BuyStop == null)
                        {
                            SymbolInfo[i].BuyStop = _Close[i] - 2 * SymbolInfo[i].ATR.Current.Value;
                        }
                        else if (_openQty < 0 && SymbolInfo[i].SellStop == null)
                        {
                            SymbolInfo[i].SellStop = _Close[i] + 2 * SymbolInfo[i].ATR.Current.Value;
                        }

                        if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
                        {
                            if (data[i].EndTime.Minute == 0
                                && data[i].Close > SymbolInfo[i].Donchian.UpperBand.Current.Value && _openQty == 0)
                            {
                                Debug("Buys signal generated on " + i);
                                SymbolInfo[i].BuyQuantity = (risk / 100) * Portfolio.TotalPortfolioValue / SymbolInfo[i].ATR.Current.Value;
                                SymbolInfo[i].BuyStop = data[i].Close - 2 * SymbolInfo[i].ATR.Current.Value;
                                if (SymbolInfo[i].BuyQuantity != 0)
                                {
                                    var buy = Portfolio.GetBuyingPower(i) * .9m;
                                    decimal qty = SymbolInfo[i].BuyQuantity * _Close[i] > buy * .99m ? (buy * .99m / _Close[i]) : SymbolInfo[i].BuyQuantity;
                                    BinanceMarginMarketOrder(i, qty);
                                    continue;
                                }

                            }
                            else if (((data[i].Close < SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                                || data[i].Close < SymbolInfo[i].BuyStop) && _openQty > 0)
                            {
                                Debug("Closing open Long Position on " + i);
                                BinanceMarginMarketOrder(i, -_openQty, _liquidate);
                                SymbolInfo[i].BuyQuantity = 0;
                                SymbolInfo[i].BuyStop = null;
                                continue;
                            }
                        }




                        if (index.TradeDirection == Direction.Short || index.TradeDirection == Direction.Both)
                        {
                            if (data[i].EndTime.Minute == 0
                                && data[i].Close < SymbolInfo[i].Donchian.LowerBand.Current.Value && _openQty == 0)
                            {
                                Debug("Sell signal generated on " + i);
                                SymbolInfo[i].SellQuantity = -1 * (risk / 100) * Portfolio.TotalPortfolioValue / SymbolInfo[i].ATR.Current.Value;
                                SymbolInfo[i].SellStop = data[i].Close + 2 * SymbolInfo[i].ATR.Current.Value;
                                if (SymbolInfo[i].SellQuantity != 0)
                                {
                                    var sell = Portfolio.GetBuyingPower(i, OrderDirection.Sell) * .9m;
                                    decimal qty = SymbolInfo[i].SellQuantity * _Close[i] * -1 > sell * .99m ? (sell * -0.99m / _Close[i]) : (SymbolInfo[i].SellQuantity);
                                    BinanceMarginMarketOrder(i, qty);
                                    continue;
                                }
                            }
                            else if (((data[i].Close > SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                                || data[i].Close > SymbolInfo[i].SellStop) && _openQty < 0)
                            {
                                Debug("Closing open Short Position on " + i);
                                BinanceMarginMarketOrder(i, -_openQty, _liquidate);
                                SymbolInfo[i].SellQuantity = 0;
                                SymbolInfo[i].SellStop = null;
                                continue;
                            }
                        }
                        
                        Debug("No action taken on " + i);
                    }
                }
                catch { Debug("Error Processing index indicator allocations"); }
            }

        }



        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (LiveMode)
            {
                if (orderEvent.Status == OrderStatus.Filled)
                    //Log(string.Format("{0}: {1}: {2}", Time, order.Type, orderEvent));
                if (orderEvent.Status == OrderStatus.Invalid)
                {
                    //Log(string.Format("Invalid order on Security {0}: Invalid ID {1}", order.Symbol, orderEvent.OrderId));
                    if (orderEvent.OrderId == -5)
                    {

                    }
                }
                if (orderEvent.Message.Contains("-1013")) 
                {

                }
            }
            else
            {
                if (orderEvent.Status == OrderStatus.Invalid)
                {
                    //Log(string.Format("Invalid order on Security {0}: Reason - {2}, Invalid ID {1}", order.Symbol, orderEvent.OrderId, orderEvent.Message));
                    if (orderEvent.OrderId == -5)
                    {

                    }
                }
            }
        }

        /// <summary>
        /// Creates the canonical VWAP indicator that resets each day. The indicator will be automatically
        /// updated on the security's configured resolution.
        /// </summary>
        /// <param name="symbol">The symbol whose VWAP we want</param>
        /// <returns>The IntradayVWAP for the specified symbol</returns>
        public IntraweekVwap VWAP(Symbol symbol)
        {
            var name = CreateIndicatorName(symbol, "VWAP", null);
            var intraweekVwap = new IntraweekVwap(name);
            RegisterIndicator(symbol, intraweekVwap);
            return intraweekVwap;
        }

        public class SymbolData
        {
            public SymbolData(string symbol)
            {
                Symbol = symbol;
            }
            public string Symbol { get; set; }
            public bool StartUp { get; set; } = true;

            public decimal? BuyStop { get; set; }
            public decimal? SellStop { get; set; }
            public decimal? StopInTicks { get; set; }
            public decimal BuyQuantity { get; set; } = 0;
            public decimal SellQuantity { get; set; } = 0;
            public decimal? OpenLongEntry { get; set; }
            public decimal? OpenShortEntry { get; set; }

            public AverageTrueRange ATR { get; set; }
            public DonchianChannel Donchian { get; set; }
            public IntraweekVwap VWAP_week { get; set; }
            public LinearWeightedMovingAverage TrendMA { get; set; }

            public RollingWindow<IndicatorDataPoint> TrendMAWindow = new RollingWindow<IndicatorDataPoint>(5);

            public decimal SQN
            {
                get
                {
                    if (HypotheticalTrades.Count() < 20)
                    {
                        return 0;
                    }
                    else
                    {
                        return (decimal)Math.Sqrt(HypotheticalTrades.Count()) * RollingWindowAverage / RollingWindowStDev;
                    }
                }
            }
            public bool testIsOpen { get; set; }
            public decimal testEntryPrice { get; set; }
            public decimal testATR { get; set; }
            public RollingWindow<decimal> HypotheticalTrades = new RollingWindow<decimal>(30);

            private decimal RollingWindowStDev
            {
                get
                {
                    IEnumerable<decimal> values = HypotheticalTrades;
                    double avg = (double)values.Average();
                    decimal o = (decimal)Math.Sqrt(values.Average(v => Math.Pow((double)v - avg, 2)));
                    return o;
                }
            }

            private decimal RollingWindowAverage
            {
                get
                {
                    IEnumerable<decimal> values = HypotheticalTrades;
                    decimal avg = values.Average();
                    return avg;
                }
            }
        }
        public class MarketType
        {
            // the VolStat indicator, which is ATR (one measure of volatility) reduced to a percentage of price and
            //plotted with a 100 period SMA and 100 period Bollingers.
            //https://evilspeculator.com/arisen/
            public int LookBackDays { get; set; } = 100;
            public RollingWindow<decimal> LookBackDays_Window = new RollingWindow<decimal>(250);
            public RollingWindow<decimal> MarketATR_Window = new RollingWindow<decimal>(100);
            public AverageTrueRange MarketATR { get; set; }
            public decimal PercentChangeCalc(decimal CurrentPrice, RollingWindow<decimal> LookBackDays_Window, int LookBackDays)
            {
                try
                {
                    if (LookBackDays_Window.Count() >= LookBackDays)
                    {
                        decimal change = 0;
                        change = (CurrentPrice - LookBackDays_Window[LookBackDays]) / CurrentPrice;
                        change = change * 100;
                        return change;
                    }
                    else
                    {
                        return 0;
                    }
                }
                catch { return 0; }
            }
        }
        public class SymbolDirection
        {
            public SymbolDirection(string symbol, string asset, Direction direction)
            {
                Symbol = symbol;
                Asset = asset;
                TradeDirection = direction;
            }
            public string Symbol { get; set; }
            public string Asset { get; set; }
            public Direction TradeDirection { get; set; }
        }
        public enum Direction
        {
            Long,
            Short,
            Both
        }

    }
}
