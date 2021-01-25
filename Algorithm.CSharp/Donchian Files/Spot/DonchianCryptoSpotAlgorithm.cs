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
using QuantConnect.Securities;
using QuantConnect.Orders;
using QuantConnect.PythonPredictions;
using QuantConnect.Data.Consolidators;
using QuantConnect.Configuration;
using System.IO;
using System.Globalization;
using QuantConnect.Data.Market;
using Binance.Net;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public partial class DonchianCryptoSpotAlgorithm: QCAlgorithm
    {
        private List<string> symbols = new List<string>();

        private Dictionary<string, SymbolData> SymbolInfo = new Dictionary<string, SymbolData>();
        BinanceFuturesOrderProperties _liquidate = new BinanceFuturesOrderProperties();
        BinanceFuturesOrderProperties _immediateOrder = new BinanceFuturesOrderProperties();

        public Resolution resolution = Resolution.Hour;
        private RandomForest forest;

        private static bool forestPredictions = true;
        private static bool reduceMaxPositions = true;
        private static bool CoinFuturesAudit = true;

        decimal risk = .3m, forestAdjustment = 0.05m, initialRisk, portMax;
        decimal UpMoveReduction = -0.2m; //Must be greater than 0
        decimal DrawdownReduction = -0.04m; // Must be less than 0
        decimal stopMult = 2m, drawDownRiskReduction = 0.05m;
        int atrPeriod = 30, dchPeriod = 15;

        bool decreasingRisk = true;
        string decreasingType = "Drawdown"; //Drawdown, AccountSize
        bool reEnterReducedPositions = false;
        bool volOnVWAP = false;
        string volOnVWAPType = "Straight"; // Straight, StopMult, Sqrt

        double startingequity = 1;
        decimal SavingsAccountUSDTHoldings = 0, SwapAccountUSDHoldings = 0;
        decimal SavingsAccountNonUSDTValue = 0;
        decimal Holdings = 0;
        decimal maxPortExposure = 10;
        decimal maxLongDirection = 10;
        decimal maxShortDirection = 10;

        decimal StartingPortValue, PortfolioProfit, MaxPortValue, HighWaterMark, _prevOffExchangeValue = 0, _prevTotalPortfolioValue = 1;
        DateTime _prevDepostCheckDate;
        string currentAccount = "christopherjholley23";
        bool customerAccount = false;
        bool masterAccount => !customerAccount;
        bool ExpectancyAudit = true;
        int ExpectancyLoadTime = 15;
        bool MarketStateAudit = false;
        decimal profitPercent = 0.2m;

        //Notifications
        int openingTrades = 0, closingTrades = 0;
        int numMessageSymbols => openingTrades + closingTrades;
        bool NotifyBreaker = true;
        //bool NotifyWeekendReduction = true;
        bool NotifyPortfolioOverload = true;
        bool NotifyDrawdownReduction = true;
        bool Startup = true;
        string notifyMessage = " New trades made";

        Symbol btc;

        // Test Variables
        public bool medianExpectancy = false, //done
            tradeLimit = false, //done
            adjustSizeTradeLimit = false, //done
            volTrigger = false, //done
            adjustSizeVolTrigger = false, //done
            adjustSizeDollarVolume = false,
            hardLossLimit = false, //done
            oppoTrades = false, //done
            oppoSizeAdjust = false,
            adjustVWAP = false,
            oppoDifferenceForOppoTrades = false;
        public string adjustVWAPType = "ATRonOpen"; //Entry Adjust Only, Week start, Extreme Distance only
        public decimal hardLossPercent = 0.01m; //done

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.Binance, AccountType.Cash);
            SetStartDate(2020, 1, 1);
            SetEndDate(DateTime.UtcNow.AddDays(-1));

            SetAccountCurrency("BTC");
            SetCash(startingequity);
            ExpectancyAudit = Config.GetValue("ExpectancyAudit", ExpectancyAudit);
            MarketStateAudit = Config.GetValue("MarketStateAudit", MarketStateAudit);
            SetWarmUp(TimeSpan.FromDays(Config.GetValue("ExpectancyAudit", ExpectancyAudit) ? 260 : 18));
            forest = new RandomForest(LiveMode ? MarketStateAudit : false);

            InitializeVariablesFromConfig();

            btc = AddCrypto("BTCUSDT", resolution).Symbol;
            SetBenchmark(btc);

            SetPandasConverter();

            // Initail Symbol Data for Initializing Symbol List
            if (LiveMode)
            {
                if (MarketStateAudit)
                {
                    forest.LoadMarketState(this);
                }
                else
                {
                    forest.GetLiveBitcoinHistory(this);
                    forest.RefitOneDayModel(this, Time);
                    forest.PredictBitcoinReturns(this, Time);
                }
                Log($"Initialize(): Bitcoin Predicted Direction = {forest.GetBitcoinMarketState()}");
                if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear)
                {
                    maxLongDirection = maxPortExposure / 2;
                    maxShortDirection = maxPortExposure;
                }
                else if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull)
                {
                    maxLongDirection = maxPortExposure;
                    maxShortDirection = maxPortExposure / 2;
                }
                else
                {
                    maxLongDirection = maxPortExposure * 0.75m;
                    maxShortDirection = maxPortExposure * 0.75m;
                }

            }
            else if (MarketStateAudit)
            {
                forest.LoadMarketState(this, StartDate);
            }
            else
            {
                forest.GetLiveBitcoinHistory(this);
                forest.RefitOneDayModel(this, StartDate);
            }


            Portfolio.MarginCallModel = MarginCallModel.Null;
            _liquidate.PostOnly = false;
            _liquidate.ReduceOnly = true;

            AddSymbols();

            Debug("Algorithm.Initialize(): Attempting to add " + symbols.Count() + " Symbols to the Algoithim");
            foreach (var index in symbols)
            {
                if (!SymbolInfo.ContainsKey(index))
                    InitializeSymbolInfo(index);
            }

            LoadExpectancyData();

            Train(DateRules.MonthEnd(btc), TimeRules.At(0, 1, NodaTime.DateTimeZone.Utc), () =>
            {
                if (forestPredictions && !MarketStateAudit)
                    forest.RefitOneDayModel(this, Time);
            });
            Train(DateRules.EveryDay(btc), TimeRules.At(0, 10, NodaTime.DateTimeZone.Utc), () =>
            {
                if (forestPredictions)
                {
                    if (MarketStateAudit)
                    {
                        forest.LoadMarketState(this, Time);
                    }
                    else
                    {
                        try
                        {
                            if (LiveMode)
                                forest.GetLiveBitcoinHistory(this);

                            forest.PredictBitcoinReturns(this, Time);
                        }
                        catch { }
                    }

                    Log($"{Time} - Bitcoin Predicted Direction = {forest.GetBitcoinMarketState()}");

                    if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear)
                    {
                        maxLongDirection = maxPortExposure / 2;
                        maxShortDirection = maxPortExposure;
                    }
                    else if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull)
                    {
                        maxLongDirection = maxPortExposure;
                        maxShortDirection = maxPortExposure / 2;
                    }
                    else
                    {
                        maxLongDirection = maxPortExposure * 0.75m;
                        maxShortDirection = maxPortExposure * 0.75m;
                    }
                }
                else
                {
                    maxLongDirection = maxPortExposure;
                    maxShortDirection = maxPortExposure;
                }
            });

            Schedule.On(DateRules.Every(DayOfWeek.Sunday), TimeRules.Midnight, () =>
            {
                foreach (var sec in SymbolInfo)
                {
                    sec.Value.WeekTrades = 0;
                }
            });
        }
        public override void OnWarmupFinished()
        {
            SaveExpectancyData();

            Startup = false;

            var msgbegin = $"Total portfolio Profit: {Math.Round(100 * PortfolioProfit, 2)}%," +
                $" Current DD: {Math.Min(0, Math.Round(100 * (((1 + PortfolioProfit) - (1 + MaxPortValue)) / (1 + MaxPortValue)), 2))}%," +
                $" Total Portfolio Margin: {Math.Round(100 * (Portfolio.GetBuyingPower(Securities["BTCUSDT"].Symbol) / (TotalPortfolioValue)), 2)}%";
            if (SavingsAccountUSDTHoldings + SavingsAccountNonUSDTValue + SwapAccountUSDHoldings != 0)
                msgbegin += $", Binance Open Margin: {Math.Round(100 * (1 - (Portfolio.GetBuyingPower(Securities["BTCUSDT"].Symbol) / Portfolio.TotalPortfolioValue)), 2)}%";
            notifyMessage = msgbegin + $" || {numMessageSymbols + notifyMessage}";

            var holdingsCount = 0;
            var holdingsMessage = "";
            foreach (var security in Securities)
            {
                if (SymbolInfo.ContainsKey(security.Key.Value))
                {
                    if (SymbolInfo[security.Key.Value].ExpectancyTrades.Count > SymbolInfo[security.Key.Value].HypotheticalTrades.Count)
                    {
                        
                        SymbolInfo[security.Key.Value].HypotheticalTrades = new RollingWindow<decimal>(100);
                        foreach (var exp in SymbolInfo[security.Key.Value].ExpectancyTrades)
                            SymbolInfo[security.Key.Value].HypotheticalTrades.Add(exp.Expectancy);
                    }
                }
                if (Portfolio[security.Key.Value].HoldingsValue > 0)
                {
                    holdingsCount++;
                    holdingsMessage += $", LONG Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 4)}, Entry Price = {Portfolio[security.Key].AveragePrice}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
                }
                else if (Portfolio[security.Key.Value].HoldingsValue < 0)
                {
                    holdingsCount++;
                    holdingsMessage += $", SHORT Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 4)}, Entry Price = {Portfolio[security.Key].AveragePrice}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
                }
            }

            symbols = symbols.OrderByDescending(x => SymbolInfo[x].Expectancy).ToList();
            notifyMessage += $", {holdingsCount} Futures Holdings" + holdingsMessage;
            Notify.Email("christopherjholley23@gmail.com", currentAccount + ": BINANCE: Bot Tracking Info", notifyMessage);
            Log(notifyMessage);
        }

        public void InitializeSymbolInfo(string index)
        {
            if (!SymbolInfo.ContainsKey(index))
                SymbolInfo[index] = new SymbolData(index);

            var sym = AddCrypto(index, resolution).Symbol;

            // Establish Symbol Data for Index
            if (SymbolInfo.ContainsKey(index))
            {
                if (dchPeriod != 15)
                {
                    SymbolInfo[index].Donchian = new DonchianChannel(CreateIndicatorName(sym, "DCH" + dchPeriod, resolution), dchPeriod);
                    SymbolInfo[index].RateOfChange = new RateOfChangePercent(CreateIndicatorName(sym, "ROCP" + 1, resolution), 1);
                    SymbolInfo[index].STStdev = new StandardDeviation(CreateIndicatorName(sym, "STD" + 100, resolution), 100);
                    SymbolInfo[index].LTStdev = new StandardDeviation(CreateIndicatorName(sym, "STD" + 500, resolution), 500);
                    //SymbolInfo[index].Hurst = new HurstExponent(CreateIndicatorName(sym, "HURST" + 500, Resolution.Minute), 500);

                    var thirty = new TradeBarConsolidator(TimeSpan.FromMinutes(30));
                    thirty.DataConsolidated += (sender, consolidated) =>
                    {
                        SymbolInfo[index].Donchian.Update(consolidated);
                        SymbolInfo[index].RateOfChange.Update(consolidated.Time, consolidated.Close);
                        SymbolInfo[index].STStdev.Update(consolidated.Time, SymbolInfo[index].RateOfChange.Current.Value);
                        SymbolInfo[index].LTStdev.Update(consolidated.Time, SymbolInfo[index].RateOfChange.Current.Value);
                        // SymbolInfo[index].Hurst.Update(ToPoint(consolidated));
                    };

                    // we need to add this consolidator so it gets auto updates
                    SubscriptionManager.AddConsolidator(sym, thirty);
                }
                else
                {
                    SymbolInfo[index].Donchian = DCH(sym, dchPeriod, dchPeriod, resolution);
                    SymbolInfo[index].RateOfChange = ROCP(sym, 1, resolution);
                    SymbolInfo[index].STStdev = new StandardDeviation(CreateIndicatorName(sym, "STD" + 100, resolution), 100);
                    SymbolInfo[index].LTStdev = new StandardDeviation(CreateIndicatorName(sym, "STD" + 500, resolution), 500);
                }
                SymbolInfo[index].ATR = ATR(sym, 30, MovingAverageType.Simple, resolution);
                SymbolInfo[index].VWAP_week = VWAP(sym, resolution);
                //SymbolInfo[index].LogReturn = LOGR(sym, 1, Resolution.Daily);
                SymbolInfo[index].expMedian = medianExpectancy;

                if (index == "BTCUSDT" && LiveMode && masterAccount)
                {
                    var days = new TradeBarConsolidator(TimeSpan.FromDays(1));
                    days.DataConsolidated += (sender, consolidated) =>
                    {

                    };

                    // we need to add this consolidator so it gets auto updates
                    SubscriptionManager.AddConsolidator(sym, days);

                    if (CoinFuturesAudit)
                    {
                        var fourHour = new TradeBarConsolidator(TimeSpan.FromHours(4));
                        SymbolInfo[index].OBV_FourHour = new OnBalanceVolume(8);
                        SymbolInfo[index].ATR_FourHour = new AverageTrueRange(30, MovingAverageType.Simple);
                        SymbolInfo[index].Donchian_FourHour = new DonchianChannel(7, 9);
                        fourHour.DataConsolidated += (sender, consolidated) =>
                        {

                        };

                        // we need to add this consolidator so it gets auto updates
                        SubscriptionManager.AddConsolidator(sym, fourHour);
                    }
                }

                Debug("Attempting to add " + index + " to the Algoithim");
            }
        }
        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public void OnData(Slice data)
        {
            decimal pctChange = Portfolio.TotalUnrealisedProfit / (TotalPortfolioValue);
            if (TotalPortfolioValue > portMax)
                portMax = TotalPortfolioValue;
            notifyMessage = " New trades made";

            UpdateHoldings();

            var top10 = 0;
            var oppoCount = 0;
            var numSyms = 0;
            foreach (var i in symbols)
            {
                top10++;

                if (data.ContainsKey(SymbolInfo[i].Symbol))
                {
                    numSyms++;
                    if (SymbolInfo[i].RateOfChange.IsReady)
                    {
                        SymbolInfo[i].STStdev.Update(data.Time, SymbolInfo[i].RateOfChange.Current.Value);
                        SymbolInfo[i].LTStdev.Update(data.Time, SymbolInfo[i].RateOfChange.Current.Value);
                    }
                    bool tradetime = IndicatorChecks(data, i);
                    if (!tradetime)
                        continue;

                    SetHypotheticalEntries(data, i);

                    if (IsWarmingUp || SymbolInfo[i].Expectancy <= 0)// || (!Portfolio[i].Invested && SymbolInfo[i].DollarVolume < 100000))
                    {
                        if (SymbolInfo[i].Expectancy != -1)
                            oppoCount++;
                        continue;
                    }

                    bool cont = PreTradePortfolioChecks(data, i, pctChange);
                    if (!cont)
                        continue;

                    if (i == "BNBBTC")
                    {
                        if (Portfolio[i].Quantity < 2 && Portfolio.CashBook["BNB"].Amount < 2)
                        {
                            MarketOrder(i, 1, tag: "BNB for fee purchase");
                            Log($"Purchaseing 1 BNB as Port Qty of {Portfolio[i].Quantity} && Cash Qty of {Portfolio.CashBook["BNB"].Amount} is less than 2");
                        }
                        continue;
                    }
                    if ((SymbolInfo[i].Expectancy > 0) || Portfolio[i].Invested)
                        PlaceTrades(data, i);
                }
            }

            if (oppoTrades && !IsWarmingUp)
            {
                top10 = 0;
                foreach (var i in symbols.OrderBy(x => SymbolInfo[x].Expectancy))
                {
                    if (i == "BNBBTC")
                        continue;
                    top10++;
                    if (data.ContainsKey(SymbolInfo[i].Symbol))
                    {
                        bool tradetime = IndicatorChecks(data, i);
                        if (!tradetime)
                            continue;

                        if (((SymbolInfo[i].Expectancy < 0 && SymbolInfo[i].Expectancy != -1) || Portfolio[i].Invested))
                            PlaceOppoTrades(data, i);
                        else if (SymbolInfo[i].Expectancy > 0)
                            break;
                    }
                }
            }
            /*if (oppoTrades && oppoCount > 0)
            {
                top10 = 0;
                for (int j = 1; j <= Math.Min(10, oppoCount); j++)
                {
                    var s = symbols.Where(x => SymbolInfo[x].Expectancy != -1).ToList();
                    PlaceOppoTrades(data, s[s.Count - j]);
                }
            }*/

            if (IsWarmingUp)
                return;

            PostTradePortfolioChecks(data);

            DataReset(data);
        }

        public void UpdateHoldings()
        {
            foreach (var i in symbols)
            {
                if (Portfolio[i].Invested)
                    Holdings += Portfolio[i].Quantity * Portfolio[i].AveragePrice;
            }
        }

        public bool IndicatorChecks(Slice data, string i)
        {
            try
            {
                SymbolInfo[i].StopInTicks = SymbolInfo[i].ATR.Current.Value;

                if (data[i].Close != null)
                {
                    SymbolInfo[i].OpenLongEntry = SymbolInfo[i].Donchian.UpperBand.Current.Value;
                    SymbolInfo[i].OpenShortEntry = SymbolInfo[i].Donchian.LowerBand.Current.Value;
                    if (data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30) { }
                    else if (!Portfolio[i].Invested)
                        return false;
                }
                else
                {
                    return false;
                }
                if (Portfolio[i].Quantity > 0 && !IsWarmingUp)
                {
                    SymbolInfo[i].OpenTradePortQty = TotalPortfolioValue;
                    SymbolInfo[i].BuyStop = SymbolInfo[i].BuyStop == null ? Portfolio[i].AveragePrice - stopMult * SymbolInfo[i].ATR.Current.Value : SymbolInfo[i].BuyStop;
                }
                else if (Portfolio[i].Quantity < 0 && !IsWarmingUp)
                {
                    SymbolInfo[i].OpenTradePortQty = TotalPortfolioValue;
                    SymbolInfo[i].SellStop = SymbolInfo[i].SellStop == null ? Portfolio[i].AveragePrice + stopMult * SymbolInfo[i].ATR.Current.Value : SymbolInfo[i].SellStop;
                }
                else if (Portfolio[i].Quantity == 0)
                {
                    SymbolInfo[i].BuyStop = null;
                    SymbolInfo[i].SellStop = null;
                }
                return true;
            }
            catch (Exception e)
            {
                Debug("Error Processing index indicator allocations - " + e.Message);
                return false;
            }
        }
        //TODO
        public bool PreTradePortfolioChecks(Slice data, string i, decimal pctChange)
        {
            try
            {
                if (!Startup)
                    return true;

                if (data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
                    Transactions.CancelOpenOrders();

                return true;
                if (pctChange < DrawdownReduction && DrawdownReduction < 0)
                {
                    if (!SymbolInfo[i].BreakerTriggered)
                    {
                        SymbolInfo[i].BreakerTriggered = true;
                        if (Portfolio[i].Invested)
                        {
                            // High Volatility breaker
                            if (Portfolio[i].UnrealizedProfit < (TotalPortfolioValue) / 100 * risk)
                            {
                                Log($"Algorithm.OnData(): Reduce Portfolio DrawdownReduction: removing small profit position on {i}, total profit = {Math.Round(100 * pctChange, 2)}, symbol prodit = {Math.Round(Portfolio[i].UnrealizedProfit, 2)}");
                                MarketOrder(i, -Portfolio[i].Quantity, tag: "PORTFOLIO BREAKER: Closing Losing and Small Winning Positions");
                            }
                            if (NotifyBreaker)
                            {
                                Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Breaker Triggered", currentAccount + ": Portfolio Percent Change = " + pctChange);
                                NotifyBreaker = false;
                            }
                        }
                    }
                    else if (Portfolio[i].Invested)
                    {
                        Log($"Algorithm.OnData(): Reduce Portfolio DrawdownReduction: reducing large winning position on {i}, total profit = {Math.Round(100 * pctChange, 2)}, symbol prodit = {Math.Round(Portfolio[i].UnrealizedProfit, 2)}");
                        MarketOrder(i, -Portfolio[i].Quantity / 2, tag: "PORTFOLIO BREAKER: Reducing Large Winning Positions");
                    }
                }
                if (data[i].EndTime.Minute == 0)
                {
                    SymbolInfo[i].BreakerTriggered = false;
                    NotifyBreaker = true;
                }
                var portfolioOverloaded = (Holdings < (TotalPortfolioValue) * maxShortDirection * -1 || Holdings > (TotalPortfolioValue) * maxLongDirection) ? true : false;

                if (reduceMaxPositions && forestPredictions && portfolioOverloaded)
                {
                    if (Portfolio[i].Invested && forest.GetBitcoinMarketState() != RandomForest.MarketState.Unknown)
                    {
                        if ((forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && Portfolio[i].Quantity > 0)
                        || (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && Portfolio[i].Quantity < 0))
                        {
                            Log($"Algorithm.OnData(): Reduce Maximum Direction: closing losing and small winning positions on {i}");
                            MarketOrder(i, -Portfolio[i].Quantity / 2, tag: "PORTFOLIO DIRECTION REDUCTION: Reducing Position on Wrong Side");
                            if (NotifyPortfolioOverload)
                            {
                                Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Reduce Maximum Direction", $"MarketState = {forest.GetBitcoinMarketState()}, Holdings = {Math.Abs(Holdings / ((TotalPortfolioValue) * maxShortDirection))}");
                                NotifyPortfolioOverload = false;
                            }
                        }
                    }
                }
                if (reEnterReducedPositions && SymbolInfo[i].Expectancy > 0)
                {
                    decimal price = Portfolio[i].Quantity > 0 ? GetPositionPrice(i, Direction.Long) : GetPositionPrice(i, Direction.Short);
                    decimal tradeqty = Portfolio[i].Quantity > 0 ? GetPositionSize(i, Direction.Long, price) : GetPositionSize(i, Direction.Short, price);
                    if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && Portfolio[i].Quantity > 0 && tradeqty > Portfolio[i].Quantity * 1.5m && Portfolio[i].AveragePrice > data[i].Close
                        && Holdings < (TotalPortfolioValue) * maxLongDirection)
                    {
                        Log($"Algorithm.OnData(): ReEnter Position on low quantity {i}");
                        SymbolInfo[i].openOrder = LimitOrder(i, tradeqty - Portfolio[i].Quantity, price, tag: "PORTFOLIO REENTRY LONG: Increasing Size");
                        openingTrades++;
                        notifyMessage += $": ReEnter Buy {i}, Qty {tradeqty - Portfolio[i].Quantity}, " +
                            $"Total Available Qty {Securities[i].BidPrice}  GetOrderbookPriceFill()";
                    }
                    else if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && Portfolio[i].Quantity < 0 && tradeqty < Portfolio[i].Quantity * -1.5m && Portfolio[i].AveragePrice < data[i].Close
                        && Holdings > (TotalPortfolioValue) * maxShortDirection * -1)
                    {
                        Log($"Algorithm.OnData(): ReEnter Position on low quantity {i}");
                        SymbolInfo[i].openOrder = LimitOrder(i, -1 * tradeqty - Portfolio[i].Quantity, price, tag: "PORTFOLIO REENTRY SHORT: Increasing Size");
                        openingTrades++;
                        notifyMessage += $": ReEnter Sell {i}, Qty {-1 * tradeqty - Portfolio[i].Quantity} ";
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Debug("Error Processing PreTradePortfolioChecks - " + e.Message);
                return false;
            }
        }

        public void SetHypotheticalEntries(Slice data, string i)
        {
            try
            {
                if (!SymbolInfo[i].ATR.IsReady)
                    return;

                if (((data[i].Close < SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                         || data[i].Close < SymbolInfo[i].testBuyStop) && SymbolInfo[i].testLongIsOpen)
                {
                    SymbolInfo[i].testBuyQuantity = 0;
                    var ret = SymbolInfo[i].testATR == null || SymbolInfo[i].testATR == 0 ? (data[i].Close - SymbolInfo[i].testLongEntryPrice) / (SymbolInfo[i].ATR.Current.Value) : (data[i].Close - SymbolInfo[i].testLongEntryPrice) / (SymbolInfo[i].testATR.Value);
                    SymbolInfo[i].testLongIsOpen = false;
                    SymbolInfo[i].testLongEntryPrice = 0;
                    SymbolInfo[i].testATR = null;
                    SymbolInfo[i].HypotheticalTrades.Add(ret);
                    if (SymbolInfo[i].ExpectancyTrades.Count > 0)
                        if (data[i].EndTime > SymbolInfo[i].ExpectancyTrades.LastOrDefault().TradeTime)
                            SymbolInfo[i].ExpectancyTrades.Add(new ExpectancyData(i, SymbolInfo[i].ExpectancyTrades.Count + 1, ret, data[i].EndTime));

                    if (LiveMode && !IsWarmingUp)
                        SaveExpectancyData(new ExpectancyData(i, SymbolInfo[i].ExpectancyTrades.Count + 1, ret, data[i].EndTime));

                    if (data[i].EndTime.Minute != 0 && SymbolInfo[i].Expectancy > 0)
                        SymbolInfo[i].VolTrigger = true;

                    if (!SymbolInfo.Values.Where(x => x.Expectancy != -1).Any())
                        return;

                    var Expectancy = SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Where(x => x.Expectancy > -1).Min(y => y.Expectancy) == 0 ? 0.5m : (SymbolInfo[i].Expectancy - SymbolInfo.Values.Where(x => x.Expectancy > -1).Min(y => y.Expectancy)) / (SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Where(x => x.Expectancy > -1).Min(y => y.Expectancy));
                    SymbolInfo[i].SizeAdjustment = Expectancy <= 0 ? 0.1m : 2 * Expectancy;
                    symbols = symbols.OrderByDescending(x => SymbolInfo[x].Expectancy).ToList();
                }
                else if (((data[i].Close > SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                    || data[i].Close > SymbolInfo[i].testSellStop) && SymbolInfo[i].testShortIsOpen)
                {
                    SymbolInfo[i].testSellQuantity = 0;
                    var ret = SymbolInfo[i].testATR == null || SymbolInfo[i].testATR == 0 ? (SymbolInfo[i].testShortEntryPrice - data[i].Close) / (SymbolInfo[i].ATR.Current.Value) : (SymbolInfo[i].testShortEntryPrice - data[i].Close) / (SymbolInfo[i].testATR.Value);
                    SymbolInfo[i].testShortIsOpen = false;
                    SymbolInfo[i].testShortEntryPrice = 0;
                    SymbolInfo[i].testATR = null;
                    SymbolInfo[i].HypotheticalTrades.Add(ret);
                    if (SymbolInfo[i].ExpectancyTrades.Count > 0)
                        if (data[i].EndTime > SymbolInfo[i].ExpectancyTrades.LastOrDefault().TradeTime)
                            SymbolInfo[i].ExpectancyTrades.Add(new ExpectancyData(i, SymbolInfo[i].ExpectancyTrades.Count + 1, ret, data[i].EndTime));

                    if (LiveMode && !IsWarmingUp)
                        SaveExpectancyData(new ExpectancyData(i, SymbolInfo[i].ExpectancyTrades.Count + 1, ret, data[i].EndTime));

                    if (data[i].EndTime.Minute != 0 && SymbolInfo[i].Expectancy > 0)
                        SymbolInfo[i].VolTrigger = true;

                    if (!SymbolInfo.Values.Where(x => x.Expectancy != -1).Any())
                        return;

                    var Expectancy = SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Where(x => x.Expectancy > -1).Min(y => y.Expectancy) == 0 ? 0.5m : (SymbolInfo[i].Expectancy - SymbolInfo.Values.Where(x => x.Expectancy > -1).Min(y => y.Expectancy)) / (SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Where(x => x.Expectancy > -1).Min(y => y.Expectancy));
                    SymbolInfo[i].SizeAdjustment = Expectancy <= 0 ? 0.1m : 2 * Expectancy;
                    symbols = symbols.OrderByDescending(x => SymbolInfo[x].Expectancy).ToList();
                }


                //////////////////////if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
                //{
                if ((data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
                    && data[i].Close > SymbolInfo[i].OpenLongEntry && data[i].Close > SymbolInfo[i].VWAP_week.Current.Value
                    && !SymbolInfo[i].testLongIsOpen && !SymbolInfo[i].testShortIsOpen
                    && SymbolInfo[i].StdevDifference <= 0)
                {
                    SymbolInfo[i].testBuyStop = data[i].Close - stopMult * SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].testLongIsOpen = true;
                    SymbolInfo[i].testLongEntryPrice = data[i].Close;
                    SymbolInfo[i].testATR = SymbolInfo[i].ATR.Current;
                    SymbolInfo[i].WeekTrades++;
                    return;
                }
                else if (SymbolInfo[i].testLongIsOpen && SymbolInfo[i].testBuyStop != null && volOnVWAP)
                {
                    switch (volOnVWAPType)
                    {
                        case "Straight":
                            SymbolInfo[i].testBuyStop = Math.Max((decimal)SymbolInfo[i].testBuyStop, SymbolInfo[i].VWAP_week.Current.Value - SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "StopMult":
                            SymbolInfo[i].testBuyStop = Math.Max((decimal)SymbolInfo[i].testBuyStop, SymbolInfo[i].VWAP_week.Current.Value - stopMult * SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "Sqrt":
                            SymbolInfo[i].testBuyStop = Math.Max((decimal)SymbolInfo[i].testBuyStop, SymbolInfo[i].VWAP_week.Current.Value - (decimal)Math.Sqrt((double)stopMult) * SymbolInfo[i].ATR.Current.Value);
                            break;
                    }
                }
                //}

                ///////////////////////if (index.TradeDirection == Direction.Short || index.TradeDirection == Direction.Both)
                //{
                if ((data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
                    && data[i].Close < SymbolInfo[i].OpenShortEntry && data[i].Close < SymbolInfo[i].VWAP_week.Current.Value
                    && !SymbolInfo[i].testShortIsOpen && !SymbolInfo[i].testLongIsOpen
                    && SymbolInfo[i].StdevDifference <= 0)
                {
                    SymbolInfo[i].testSellStop = data[i].Close + stopMult * SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].testShortIsOpen = true;
                    SymbolInfo[i].testShortEntryPrice = data[i].Close;
                    SymbolInfo[i].testATR = SymbolInfo[i].ATR.Current;
                    SymbolInfo[i].WeekTrades++;
                    return;
                }
                else if (SymbolInfo[i].testShortIsOpen && SymbolInfo[i].testSellStop != null && volOnVWAP)
                {
                    switch (volOnVWAPType)
                    {
                        case "Straight":
                            SymbolInfo[i].testSellStop = Math.Min((decimal)SymbolInfo[i].testSellStop, SymbolInfo[i].VWAP_week.Current.Value + SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "StopMult":
                            SymbolInfo[i].testSellStop = Math.Min((decimal)SymbolInfo[i].testSellStop, SymbolInfo[i].VWAP_week.Current.Value + stopMult * SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "Sqrt":
                            SymbolInfo[i].testSellStop = Math.Min((decimal)SymbolInfo[i].testSellStop, SymbolInfo[i].VWAP_week.Current.Value + (decimal)Math.Sqrt((double)stopMult) * SymbolInfo[i].ATR.Current.Value);
                            break;
                    }
                }
                //}
            }
            catch (Exception e)
            {
                Debug("Error Processing SetHypotheticalEntries() - " + e.Message + " - " + e.StackTrace);
            }
        }

        public void PlaceTrades(Slice data, string i)
        {
            try
            {
                if (Portfolio[i].Invested)
                {
                    if (ExitCondition(data, i))
                    {
                        Log($"Algorithm.OnData(): Close regular order generated on {i}, Expectancy: {Math.Round(SymbolInfo[i].Expectancy, 2)}, Port Value: {Math.Round(TotalPortfolioValue, 8)}");
                        notifyMessage += data[i].EndTime.Minute == 0 ?
                            $": CLOSING on VWAP {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (TotalPortfolioValue) * 100, 2)}%" :
                            $": CLOSING on Vol {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (TotalPortfolioValue) * 100, 2)}%";

                        MarketOrder(i, -Portfolio[i].Quantity, tag: "Liquidating Long Position, VWAP Stop Triggered");
                        closingTrades++;
                        if (data[i].EndTime.Minute != 0)
                            SymbolInfo[i].VolTrigger = true;
                    }
                }
                //////////////////if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
                //{
                var enterCondition = EnterCondition(data, i);
                if (enterCondition == Direction.Long)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Buys signal generated on " + i);
                    var portqty = (risk * 5 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].BuyStop = data[i].Close - stopMult * SymbolInfo[i].ATR.Current.Value;
                    decimal price = GetPositionPrice(i, Direction.Long);
                    decimal qty = GetPositionSize(i, Direction.Long, price);
                    SymbolInfo[i].OpenTradePortQty = TotalPortfolioValue;

                    if (qty > 0)
                    {
                        SymbolInfo[i].openOrder = LimitOrder(i, qty, price, tag: "Buy Signal Generated");
                        notifyMessage += $": Buy {i}, Qty {qty} ";
                        openingTrades++;
                        return;
                    }
                }
                else if (Portfolio[i].Quantity > 0 && SymbolInfo[i].BuyStop != null && volOnVWAP)
                {
                    switch (volOnVWAPType)
                    {
                        case "Straight":
                            SymbolInfo[i].BuyStop = Math.Max((decimal)SymbolInfo[i].BuyStop, SymbolInfo[i].VWAP_week.Current.Value - SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "StopMult":
                            SymbolInfo[i].BuyStop = Math.Max((decimal)SymbolInfo[i].BuyStop, SymbolInfo[i].VWAP_week.Current.Value - stopMult * SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "Sqrt":
                            SymbolInfo[i].BuyStop = Math.Max((decimal)SymbolInfo[i].BuyStop, SymbolInfo[i].VWAP_week.Current.Value - (decimal)Math.Sqrt((double)stopMult) * SymbolInfo[i].ATR.Current.Value);
                            break;
                    }
                }
                //}

                /////////////////if (index.TradeDirection == Direction.Short || index.TradeDirection == Direction.Both)
                //{
                if (enterCondition == Direction.Short)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Sell signal generated on " + i);
                    var portqty = (risk * 5 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].SellStop = data[i].Close + stopMult * SymbolInfo[i].ATR.Current.Value;
                    decimal price = GetPositionPrice(i, Direction.Short);
                    decimal qty = GetPositionSize(i, Direction.Short, price);
                    SymbolInfo[i].OpenTradePortQty = TotalPortfolioValue;

                    if (qty < 0)
                    {
                        SymbolInfo[i].openOrder = LimitOrder(i, qty, price, tag: "Sell Signal Generated");
                        notifyMessage += $": Sell {i}, Qty {qty} ";
                        openingTrades++;
                        return;
                    }
                }
                else if (Portfolio[i].Quantity < 0 && SymbolInfo[i].SellStop != null && volOnVWAP)
                {
                    switch (volOnVWAPType)
                    {
                        case "Straight":
                            SymbolInfo[i].SellStop = Math.Min((decimal)SymbolInfo[i].SellStop, SymbolInfo[i].VWAP_week.Current.Value + SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "StopMult":
                            SymbolInfo[i].SellStop = Math.Min((decimal)SymbolInfo[i].SellStop, SymbolInfo[i].VWAP_week.Current.Value + stopMult * SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "Sqrt":
                            SymbolInfo[i].SellStop = Math.Min((decimal)SymbolInfo[i].SellStop, SymbolInfo[i].VWAP_week.Current.Value + (decimal)Math.Sqrt((double)stopMult) * SymbolInfo[i].ATR.Current.Value);
                            break;
                    }
                }
                //}

            }
            catch (Exception e)
            {
                Debug("Error Processing PlaceTrades() - " + e.Message
                    + " " + e.StackTrace);
            }
        }

        public void PlaceOppoTrades(Slice data, string i)
        {
            try
            {
                if (Portfolio[i].Invested)
                {
                    if (ExitCondition(data, i, true))
                    {
                        Log($"Algorithm.OnData(): Close Oppo order signal generated on on {i}, Expectancy: {Math.Round(SymbolInfo[i].Expectancy, 2)}, Port Value: {Math.Round(TotalPortfolioValue, 8)}");
                        notifyMessage += data[i].EndTime.Minute == 0 ?
                            $": CLOSING Oppo Order on VWAP {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (TotalPortfolioValue) * 100, 2)}%" :
                            $": CLOSING Oppo Order on Vol {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (TotalPortfolioValue) * 100, 2)}%";
                        MarketOrder(i, -Portfolio[i].Quantity, tag: "Liquidating Long Position, VWAP Stop Triggered");
                        closingTrades++;
                        if (data[i].EndTime.Minute != 0)
                            SymbolInfo[i].VolTrigger = true;
                    }
                }
                var enterCondition = EnterCondition(data, i, true);
                if (enterCondition == Direction.Long)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Oppo 'Buy' signal generated on {i}, shorting into strength");

                    var portqty = (risk * 4 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].BuyStop = data[i].Close - stopMult * SymbolInfo[i].ATR.Current.Value;

                    decimal price = GetPositionPrice(i, Direction.Long, true);
                    decimal qty = GetPositionSize(i, Direction.Long, price, true);
                    SymbolInfo[i].OpenTradePortQty = TotalPortfolioValue;

                    if (qty < 0)
                    {
                        LimitOrder(i, qty, price, tag: "Oppo Sell Signal Generated");
                        notifyMessage += $": Oppo Sell {i}, Qty {qty} ";
                        openingTrades++;
                        //SymbolInfo[i].WeekTrades++;
                        return;
                    }
                }
                else if (Portfolio[i].Quantity < 0 && SymbolInfo[i].BuyStop != null && volOnVWAP)
                {
                    switch (volOnVWAPType)
                    {
                        case "Straight":
                            SymbolInfo[i].BuyStop = Math.Max((decimal)SymbolInfo[i].BuyStop, SymbolInfo[i].VWAP_week.Current.Value - SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "StopMult":
                            SymbolInfo[i].BuyStop = Math.Max((decimal)SymbolInfo[i].BuyStop, SymbolInfo[i].VWAP_week.Current.Value - stopMult * SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "Sqrt":
                            SymbolInfo[i].BuyStop = Math.Max((decimal)SymbolInfo[i].BuyStop, SymbolInfo[i].VWAP_week.Current.Value - (decimal)Math.Sqrt((double)stopMult) * SymbolInfo[i].ATR.Current.Value);
                            break;
                    }
                }

                // Short is actually a positive quantity
                if (enterCondition == Direction.Short)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Oppo 'Sell' signal generated on {i}, buying into weakness");
                    var portqty = (risk * 4 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].SellStop = data[i].Close + stopMult * SymbolInfo[i].ATR.Current.Value;

                    decimal price = GetPositionPrice(i, Direction.Short, true);
                    decimal qty = GetPositionSize(i, Direction.Short, price, true);
                    SymbolInfo[i].OpenTradePortQty = TotalPortfolioValue;

                    if (qty > 0)
                    {
                        LimitOrder(i, qty, price, tag: "Oppo Buy Signal Generated");
                        notifyMessage += $": Oppo Buy {i}, Qty {qty} ";
                        openingTrades++;
                        //SymbolInfo[i].WeekTrades++;
                        return;
                    }
                }
                else if (Portfolio[i].Quantity > 0 && SymbolInfo[i].SellStop != null && volOnVWAP)
                {
                    switch (volOnVWAPType)
                    {
                        case "Straight":
                            SymbolInfo[i].SellStop = Math.Min((decimal)SymbolInfo[i].SellStop, SymbolInfo[i].VWAP_week.Current.Value + SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "StopMult":
                            SymbolInfo[i].SellStop = Math.Min((decimal)SymbolInfo[i].SellStop, SymbolInfo[i].VWAP_week.Current.Value + stopMult * SymbolInfo[i].ATR.Current.Value);
                            break;
                        case "Sqrt":
                            SymbolInfo[i].SellStop = Math.Min((decimal)SymbolInfo[i].SellStop, SymbolInfo[i].VWAP_week.Current.Value + (decimal)Math.Sqrt((double)stopMult) * SymbolInfo[i].ATR.Current.Value);
                            break;
                    }
                }

            }
            catch (Exception e)
            {
                Debug("Error Processing PlaceTrades() - " + e.Message);
            }
        }
        public bool ExitCondition(Slice data, string i, bool oppoTrade = false)
        {
            var tradeTime = data[i].EndTime.Minute == 0;
            if ((!oppoTrade && Portfolio[i].Quantity > 0) || (oppoTrade && Portfolio[i].Quantity < 0))
            {
                if (data[i].Close < SymbolInfo[i].VWAP_week.Current.Value && tradeTime)
                    return true;

                if (data[i].Close < SymbolInfo[i].BuyStop)
                    return true;

            }

            else if ((!oppoTrade && Portfolio[i].Quantity < 0) || (oppoTrade && Portfolio[i].Quantity > 0))
            {
                if (data[i].Close > SymbolInfo[i].VWAP_week.Current.Value && tradeTime)
                    return true;

                if (data[i].Close > SymbolInfo[i].SellStop)
                    return true;

            }

            decimal hardLoss = hardLossPercent;
            if (oppoTrade)
                hardLoss = hardLoss / 2;

            if (hardLossLimit && Portfolio[i].UnrealizedProfit / SymbolInfo[i].OpenTradePortQty < -hardLoss)
                return true;

            return false;
        }
        public Direction EnterCondition(Slice data, string i, bool oppoTrade = false)
        {
            var tradeTime = data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30;

            if (volTrigger && SymbolInfo[i].VolTrigger)
            {
                Log($"Volatility Trigger on {i}, no trade");
                return Direction.Both;
            }

            if (tradeLimit && SymbolInfo[i].WeekTrades > SymbolInfo[i].maxWeekTrades)
            {
                Log($"Max Weekly Trades Trigger on {i}, no trade");
                return Direction.Both;
            }

            if (!tradeTime)
            {
                //Log($"Not Currently time to trade on {i}, time = {data[i].EndTime}");
                return Direction.Both;
            }

            if (Portfolio[i].Quantity != 0)
            {
                Log($"Already in a trade for {i}, quantity = {Portfolio[i].Quantity}, oppoTrade = {SymbolInfo[i].Expectancy < 0}," +
                    $" stopTime = {(SymbolInfo[i].Expectancy < 0 ? SymbolInfo[i].VWAP_week.Current < Securities[i].Price : SymbolInfo[i].VWAP_week.Current > Securities[i].Price)}");
                return Direction.Both;
            }

            if (SymbolInfo[i].StdevDifference > 0)
            {
                Log($"STD Difference too large for {i}, difference = {SymbolInfo[i].StdevDifference}");
                return Direction.Both;
            }

            if (data[i].Close > SymbolInfo[i].OpenLongEntry && data[i].Close > SymbolInfo[i].VWAP_week.Current.Value)
            {
#pragma warning disable CA1305 // Specify IFormatProvider
                Log($"Time to place a long trade on {i}, oppoTrade = {oppoTrade}, holdings room? {(oppoTrade ? (Holdings < TotalPortfolioValue * maxLongDirection).ToString() : (Holdings > TotalPortfolioValue * maxShortDirection * -1).ToString())}");
#pragma warning restore CA1305 // Specify IFormatProvider
                if (!oppoTrade &&
                    Holdings < TotalPortfolioValue * maxLongDirection)
                {
                    return Direction.Long;
                }
                else if (oppoTrade &&
                    Holdings > TotalPortfolioValue * maxShortDirection * -1)
                {
                    // return Direction.Long;
                }
            }
            else if (data[i].Close < SymbolInfo[i].OpenShortEntry && data[i].Close < SymbolInfo[i].VWAP_week.Current.Value)
            {
#pragma warning disable CA1305 // Specify IFormatProvider
                Log($"Time to place a short trade on {i}, oppoTrade = {oppoTrade}, holdings room? {(!oppoTrade ? (Holdings < TotalPortfolioValue * maxLongDirection).ToString() : (Holdings > TotalPortfolioValue * maxShortDirection * -1).ToString())}");
#pragma warning restore CA1305 // Specify IFormatProvider
                if (!oppoTrade &&
                    Holdings > TotalPortfolioValue * maxShortDirection * -1)
                {
                    //return Direction.Short;
                }
                else if (oppoTrade &&
                    Holdings < TotalPortfolioValue * maxLongDirection)
                {
                    return Direction.Short;
                }
            }
            return Direction.Both;
        }
        //TODO
        public void PostTradePortfolioChecks(Slice data)
        {
            decimal pctChange = 0;
            if (Portfolio.TotalUnrealisedProfit == 0)
                return;
            else
                pctChange = Portfolio.TotalUnrealisedProfit / (TotalPortfolioValue);

            foreach (var i in symbols.OrderBy(x => SymbolInfo[x].Expectancy))
            {
                try
                {
                    if (!data.ContainsKey(i))
                        continue;

                    var day = data[i].EndTime;
                    if (data[i].EndTime.Minute == 0)
                    {
                        SymbolInfo[i].VolTrigger = false;
                        if (!Portfolio[i].Invested)
                            continue;
                        if (UpMoveReduction <= 0) { }
                        else if (pctChange > UpMoveReduction && Portfolio[i].Invested)
                        {
                            Log($"Algorithm.OnData(): Reduce Portfolio Profit Exposure: reducing small winning position on {i}, Total Portfolio Profit = " + pctChange);
                            var qty = GetPositionSize(i, (Portfolio[i].IsLong ? Direction.Long : Direction.Short), GetPositionPrice(i, (Portfolio[i].IsLong ? Direction.Long : Direction.Short)));
                            if (SymbolInfo[i].Expectancy > 0)
                                MarketOrder(i, -(Portfolio[i].Quantity - qty), tag: $"Preserving Proft: Reducing Position on {i}");
                            else
                                MarketOrder(i, -(Portfolio[i].Quantity), tag: $"Preserving Proft: Reducing Oppo Position on {i}");
                            if (NotifyDrawdownReduction)
                            {
                                Notify.Email("christopherjholley23@gmail.com", "Reduce Large Portfolio Profit", "Total Portfolio Profit = " + pctChange);
                                NotifyDrawdownReduction = false;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug("Error Processing PostTradePortfolioChecks - " + e.Message);
                }
            }
        }

        public void DataReset(Slice data)
        {
            if (IsWarmingUp)
                return;
            Holdings = 0;
            NotifyBreaker = true;
            NotifyPortfolioOverload = true;
            //NotifyWeekendReduction = true;
            NotifyDrawdownReduction = true;
            var msgbegin = $"Total portfolio Profit: {Math.Round(100 * PortfolioProfit, 2)}%," +
                $" Current DD: {Math.Min(0, Math.Round(100 * (((1 + PortfolioProfit) - (1 + MaxPortValue)) / (1 + MaxPortValue)), 2))}%," +
                $" Total Portfolio Margin: {Math.Round(100 * (Portfolio.GetBuyingPower(Securities["BTCUSDT"].Symbol) / (TotalPortfolioValue)), 2)}%";
            if (SavingsAccountUSDTHoldings + SavingsAccountNonUSDTValue + SwapAccountUSDHoldings != 0)
                msgbegin += $", Binance Open Margin: {Math.Round(100 * (1 - (Portfolio.GetBuyingPower(Securities["BTCUSDT"].Symbol) / Portfolio.TotalPortfolioValue)), 2)}%";
            notifyMessage = msgbegin + $" || {numMessageSymbols + notifyMessage}";

            if (data.Time.Minute == 30 || data.Time.Minute == 0 || numMessageSymbols != 0)
            {
                var holdingsCount = 0;
                var holdingsMessage = "";
                foreach (var security in Securities)
                {
                    if (Portfolio[security.Key.Value].HoldingsValue > 0)
                    {
                        holdingsCount++;
                        holdingsMessage += $", LONG Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 4)}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
                    }
                    else if (Portfolio[security.Key.Value].HoldingsValue < 0)
                    {
                        holdingsCount++;
                        holdingsMessage += $", SHORT Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 4)}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
                    }
                }
                notifyMessage += $", {holdingsCount} Futures Holdings" + holdingsMessage;
                if (numMessageSymbols != 0)
                {
                    var subject = openingTrades > 0 && closingTrades > 0 ? "Opening & Closing Trades" : (openingTrades > 1 ? "Opening Trades" : (openingTrades > 0 ? "Opening Trade" : (closingTrades > 1 ? "Closing Trades" : "Closing Trade")));
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + $": BINANCE: {numMessageSymbols} New " + subject, notifyMessage);
                }
                else if (DateTime.UtcNow.Minute == 0 && (DateTime.UtcNow.Hour == 0 || DateTime.UtcNow.Hour == 12))
                {
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + ": BINANCE: Bot Tracking Info", notifyMessage);
                }
                Log(notifyMessage);
            }
            openingTrades = 0;
            closingTrades = 0;
        }

        public decimal GetPositionSize(string i, Direction direction, decimal price, bool oppTrade = false)
        {
            var tickSize = Securities[i].SymbolProperties.LotSize;
            var portqty = (risk * 4 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
            var qty = 0m;
            if (direction == Direction.Long)
            {
                qty = SymbolInfo[i].SizeAdjustment * (risk / 100) * (TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;

                if ((forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && !oppTrade) ||
                    (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && oppTrade))
                    qty = qty * (risk + forestAdjustment) / risk;
            }
            else if (direction == Direction.Short)
            {
                qty = SymbolInfo[i].SizeAdjustment * ((risk + forestAdjustment / 3) / 100) * (TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;

                if ((forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && !oppTrade) ||
                    (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && oppTrade))
                    qty = qty * (risk + forestAdjustment) / risk;
            }

            if (oppTrade)
                qty = -qty / SymbolInfo[i].SizeAdjustment;

            if (oppTrade && oppoSizeAdjust)
                qty = qty * -(SymbolInfo[i].SizeAdjustment - 2);
            //qty = qty * -(SymbolInfo[i].SizeAdjustment / 2 - 1);

            if (adjustSizeDollarVolume)
            {
                //TODO
            }

            if (adjustSizeTradeLimit)
                qty = 2 * qty / Math.Max(2, SymbolInfo[i].WeekTrades);

            if (adjustSizeVolTrigger)
                qty = SymbolInfo[i].VolTrigger ? qty / 2 : qty;

            qty = qty < portqty ? Math.Round(qty / tickSize, 0) * tickSize : Math.Round(portqty / tickSize, 0) * tickSize;

            if (oppTrade)
                qty = Math.Max(qty, -Portfolio.GetBuyingPower(i, OrderDirection.Buy, price));
            else
                qty = Math.Min(qty, Portfolio.GetBuyingPower(i, OrderDirection.Buy, price));

            Log($"Buying power for {i} = {Portfolio.GetBuyingPower(i, OrderDirection.Buy)}, qty = {qty}");
            return (direction == Direction.Short ? -1 * qty : qty);
        }

        public decimal GetPositionPrice(string i, Direction direction, bool oppTrade = false)
        {
            var price = Securities[i].Price;
            var priceRound = Securities[i].SymbolProperties.MinimumPriceVariation;
            if ((direction == Direction.Long && !oppTrade) ||
                (direction == Direction.Short && oppTrade))
                price = Math.Round(Securities[i].Price * 1.0025m / priceRound) * priceRound;

            if ((direction == Direction.Short && !oppTrade) ||
                    (direction == Direction.Long && oppTrade))
                price = Math.Round(Securities[i].Price * (1 - .0025m) / priceRound) * priceRound;

            return price;
        }


        #region Brokerage Events
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (orderEvent.Status == OrderStatus.Invalid && !orderEvent.Message.Contains("Insufficient buying power to complete order"))
            {
                var invalidMessage = $"Invalid order on {order.Symbol}, Reason: {orderEvent.Message}";
                Log($"OnOrderEvent: {invalidMessage}");
                Notify.Email("christopherjholley23@gmail.com", currentAccount + ": BINANCE: Invalid Order", invalidMessage);
            }
        }

        public override void OnBrokerageDisconnect()
        {
            Notify.Email("chrisholley23@gmail.com", currentAccount + ": Brokerage Disconnected", $"Disconnected from live brokerage account for {currentAccount}. Check to ensure connection was reestablished");
        }


        /// <summary>
        /// Brokerage reconnected event handler. This method is called when the brokerage connection is restored after a disconnection.
        /// </summary>
        public override void OnBrokerageReconnect()
        {
            Notify.Email("chrisholley23@gmail.com", currentAccount + ": Brokerage Reconnected", $"Reconnected to live brokerage account for {currentAccount}. Connection was reestablished");
        }

        public override void OnEndOfAlgorithm()
        {
            Notify.Email("chrisholley23@gmail.com", currentAccount + ": FATAL Brokerage Disconnected", $"FATAL Error on {currentAccount}, Immediate response required.");
        }

        #endregion

        #region Initialization & Data Mangement Methods
        public void AddSymbols()
        {/*
            if (LiveMode)
            {
                using (BinanceClient _apiClient = new BinanceClient())
                {
                    var btcPrices = _apiClient.Spot.System.GetExchangeInfo();//.Market.Get24HPrices();
                    if (btcPrices.Success)
                    {
                        foreach (var btcsym in btcPrices.Data.Symbols.ToList().Where(x => x.QuoteAsset == "BTC" && x.Status == Binance.Net.Enums.SymbolStatus.Trading && x.IsSpotTradingAllowed))//.Symbol.EndsWith("BTC")))
                            symbols.Add(btcsym.Name);
                        return;
                    }
                    else
                    {
                        throw new ArgumentException("Unable to retreive symbols list for initializtion");
                    }
                }
            }*/
            string dataFolder = Config.GetValue("data-folder", "../../../Data/") + $"equity/binance/hour";
            var folders = Directory.EnumerateFiles(dataFolder);

            foreach (var path in folders)
            {
                var s = path.Remove(0, (dataFolder.Count() + 1)).RemoveFromEnd(".zip");
#pragma warning disable CA1304 // Specify CultureInfo
                if (s.EndsWith("btc"))
                    symbols.Add(s.ToUpper());
#pragma warning restore CA1304 // Specify CultureInfo
            }/*
            symbols.Add(new SymbolDirection("BTCUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("BNBUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ETHUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("LINKUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ADAUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("XMRUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("XLMUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("LTCUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("BCHUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("EOSUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("LENDUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("AAVEUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ALGOUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("DEFIUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ATOMUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("VETUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("YFIUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("XRPUSDT", Direction.Both));

            symbols.Add(new SymbolDirection("XTZUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ETCUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ZECUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("NEOUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("TRXUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("WAVESUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("SNXUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("KAVAUSDT", Direction.Both));

            symbols.Add(new SymbolDirection("IOTAUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("DOGEUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ZILUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("IOSTUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("THETAUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("BATUSDT", Direction.Both));

            symbols.Add(new SymbolDirection("ONTUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("ZRXUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("OMGUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("QTUMUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("DASHUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("BALUSDT", Direction.Both));
            symbols.Add(new SymbolDirection("CRVUSDT", Direction.Both));*/
        }

        public void InitializeVariablesFromConfig()
        {
            Config.Reset();
            ExpectancyLoadTime = Config.GetValue("ExpectancyLoadTime", ExpectancyLoadTime);
            customerAccount = Config.GetValue("customerAccount", customerAccount);
            currentAccount = Config.GetValue("currentEmail", currentAccount);
            profitPercent = Config.GetValue("profitPercent", profitPercent);
            MarketStateAudit = Config.GetValue("MarketStateAudit", MarketStateAudit);
            risk = Config.GetValue("risk", risk);
            UpMoveReduction = Config.GetValue("UpMoveReduction", UpMoveReduction);
            DrawdownReduction = Config.GetValue("DrawdownReduction", DrawdownReduction);
            stopMult = Config.GetValue("stopMult", stopMult);
            atrPeriod = Config.GetValue("atrPeriod", atrPeriod);
            dchPeriod = Config.GetValue("dchPeriod", dchPeriod);
            drawDownRiskReduction = Config.GetValue("drawDownRiskReduction", drawDownRiskReduction);
            maxPortExposure = Config.GetValue("maxPortExposure", maxPortExposure);
            decreasingRisk = Config.GetValue("decreasingRisk", decreasingRisk);
            decreasingType = Config.GetValue("decreasingType", decreasingType);
            drawDownRiskReduction = Config.GetValue("drawDownRiskReduction", drawDownRiskReduction);
            forestAdjustment = Config.GetValue("forestAdjustment", forestAdjustment);
            forestPredictions = Config.GetValue("forestPredictions", forestPredictions);
            reduceMaxPositions = Config.GetValue("reduceMaxPositions", reduceMaxPositions);
            reEnterReducedPositions = Config.GetValue("reEnterReducedPositions", reEnterReducedPositions);
            volOnVWAP = Config.GetValue("volOnVWAP", volOnVWAP);
            volOnVWAPType = Config.GetValue("volOnVWAPType", volOnVWAPType);
            medianExpectancy = Config.GetValue("medianExpectancy", medianExpectancy); //done
            tradeLimit = Config.GetValue("tradeLimit", tradeLimit); //done
            adjustSizeTradeLimit = Config.GetValue("adjustSizeTradeLimit", adjustSizeTradeLimit); //done
            volTrigger = Config.GetValue("volTrigger", volTrigger); //done
            adjustSizeVolTrigger = Config.GetValue("adjustSizeVolTrigger", adjustSizeVolTrigger); //done
            adjustSizeDollarVolume = Config.GetValue("adjustSizeDollarVolume", adjustSizeDollarVolume);
            hardLossLimit = Config.GetValue("hardLossLimit", hardLossLimit); //done
            oppoTrades = Config.GetValue("oppoTrades", oppoTrades); //done
            oppoSizeAdjust = Config.GetValue("oppoSizeAdjust", oppoSizeAdjust);
            adjustVWAP = Config.GetValue("adjustVWAP", adjustVWAP);
            adjustVWAPType = Config.GetValue("adjustVWAPType", adjustVWAPType); //ATRonOpen, Entry Adjust Only, Week start, Extreme Distance only
            hardLossPercent = Config.GetValue("hardLossPercent", hardLossPercent); //done
            oppoDifferenceForOppoTrades = Config.GetValue("oppoDifferenceForOppoTrades", oppoDifferenceForOppoTrades);
            if (!LiveMode)
                Config.Set("transaction-log", "../../../Data/Backtests/DonchianBinanceBTCBackTest_" + medianExpectancy + "_" + tradeLimit + "_" + adjustSizeTradeLimit + "_" + volTrigger + "_" + adjustSizeVolTrigger + "_" + risk +
                    "_" + adjustSizeDollarVolume + "_" + hardLossLimit + "_" + oppoTrades + "_" + adjustVWAP + "_" + adjustVWAPType + "_" + hardLossPercent + "_" + oppoSizeAdjust + ".csv");

            initialRisk = risk;
            portMax = TotalPortfolioValue;
        }

        /// <summary>
        /// Save expectancy Data
        /// </summary>
        /// <param name="transactions">Transactions list via an OrderProvider</param>
        /// <param name="csvFileName">File path to create</param>
        private void SaveExpectancyData(ExpectancyData add = null)
        {
            string csvFileName = $"../../../Data/SpotExpectancyData.csv";

            var path = Path.GetDirectoryName(csvFileName);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);

            if (add == null)
            {
                using (var writer = new StreamWriter(csvFileName))
                {
                    var header = ($"Symbol,VariableNum,Expectancy,TradeTime");
                    writer.WriteLine(header);
                    foreach (var sym in SymbolInfo)
                    {
                        var num = 0;
                        foreach (var value in sym.Value.ExpectancyTrades)
                        {
                            var symbol = sym.Key;
                            var line = ($"{symbol},") +
                                        ($"{value.TradeNum},{value.Expectancy},{value.TradeTime}");
                            writer.WriteLine(line);
                            num++;
                        }
                    }
                }
            }
            else
            {
                if (!File.Exists(csvFileName))
                {
                    using (var writer = new StreamWriter(csvFileName))
                    {
                        var header = ($"Symbol,VariableNum,Expectancy,TradeTime");
                        writer.WriteLine(header);
                        var line = ($"{add.Symbol},") +
                                            ($"{-1},{add.Expectancy},{DateTime.UtcNow}");
                        writer.WriteLine(line);
                    }
                }
                else
                {
                    using (var writer = new StreamWriter(csvFileName, append: true))
                    {
                        var line = ($"{add.Symbol},") +
                                            ($"{-1},{add.Expectancy},{DateTime.UtcNow}");
                        writer.WriteLine(line);
                    }
                }
            }
        }

        /// <summary>
        /// Save expectancy Data
        /// </summary>
        /// <param name="transactions">Transactions list via an OrderProvider</param>
        /// <param name="csvFileName">File path to create</param>
        private void LoadExpectancyData()
        {
            string csvFileName = $"../../../Data/SpotExpectancyData.csv";

            if (!File.Exists(csvFileName))
            {
                SaveExpectancyData();
            }

            string[] lines = File.ReadAllLines(csvFileName);

            var columnQuery =
                from line in lines.Skip(1)
                let elements = line.Split(',')
                select new ExpectancyData(elements[0], Convert.ToInt32(elements[1], CultureInfo.GetCultureInfo("en-US")), Convert.ToDecimal(elements[2], CultureInfo.GetCultureInfo("en-US")), Convert.ToDateTime(elements[3], CultureInfo.GetCultureInfo("en-US")));

            var _lastSym = "BTC";

            var tenTrades = columnQuery.ToList().Where(x => x.TradeNum == 9).Select(y => y.Symbol).ToList();
            foreach (var info in SymbolInfo)
            {
                info.Value.ExpectancyTrades = new RollingWindow<ExpectancyData>(100);
            }
            foreach (var line in columnQuery)
            {
                if (SymbolInfo.ContainsKey(line.Symbol) && tenTrades.Contains(line.Symbol))
                {
                    SymbolInfo[line.Symbol].ExpectancyTrades.Add(line);
                    _lastSym = line.Symbol;
                }
            }
        }

        public class ExpectancyData
        {
            public ExpectancyData(string symbol, int tradeNum, decimal exp, DateTime _time)
            {
                Symbol = symbol;
                TradeNum = tradeNum;
                Expectancy = exp;
                TradeTime = _time;
            }
            public string Symbol { get; set; }
            public int TradeNum { get; set; }
            public decimal Expectancy { get; set; }
            public DateTime TradeTime { get; set; }
        }

        #endregion

        #region Indicators and Classes
        /// <summary>
        /// Creates the canonical VWAP indicator that resets each day. The indicator will be automatically
        /// updated on the security's configured resolution.
        /// </summary>
        /// <param name="symbol">The symbol whose VWAP we want</param>
        /// <returns>The IntradayVWAP for the specified symbol</returns>
        public IntraweekVwap VWAP(Symbol symbol, Resolution resolution)
        {
            var name = CreateIndicatorName(symbol, "VWAP", resolution);
            var intraweekVwap = new IntraweekVwap(name);
            RegisterIndicator(symbol, intraweekVwap, resolution);
            return intraweekVwap;
        }
        private decimal TotalPortfolioValue => Math.Max(.00000001m, Portfolio.TotalPortfolioValue + SavingsAccountUSDTHoldings + SavingsAccountNonUSDTValue + SwapAccountUSDHoldings + _prevOffExchangeValue);
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
            public decimal? OpenLongEntry { get; set; }
            public decimal? OpenShortEntry { get; set; }
            public bool BreakerTriggered { get; set; } = false;
            public OrderTicket openOrder { get; set; }

            public AverageTrueRange ATR { get; set; }
            public AverageTrueRange ATR_FourHour { get; set; }
            public OnBalanceVolume OBV_FourHour { get; set; }
            public DonchianChannel Donchian { get; set; }
            public DonchianChannel Donchian_FourHour { get; set; }
            public IntraweekVwap VWAP_week { get; set; }
            public LinearWeightedMovingAverage TrendMA { get; set; }
            //public LogReturn LogReturn { get; set; }
            // public HurstExponent Hurst { get; set; }

            public RollingWindow<IndicatorDataPoint> TrendMAWindow = new RollingWindow<IndicatorDataPoint>(5);
            public RateOfChangePercent RateOfChange { get; set; }
            public StandardDeviation LTStdev { get; set; }
            public StandardDeviation STStdev { get; set; }
            public decimal StdevDifference => LTStdev.IsReady && STStdev.IsReady ? STStdev.Current.Value - LTStdev.Current.Value : 0;
            //public int ActiveDays { get; set; } = 0;
            public bool testLongIsOpen { get; set; }
            public bool testShortIsOpen { get; set; }
            public double fourHourTargetPosition { get; set; } = 0;
            public decimal testLongEntryPrice { get; set; }
            public decimal testShortEntryPrice { get; set; }
            public IndicatorDataPoint testATR { get; set; }
            public decimal? testBuyStop { get; set; }
            public decimal? testSellStop { get; set; }
            public decimal testBuyQuantity { get; set; } = 0;
            public decimal testSellQuantity { get; set; } = 0;
            public decimal SizeAdjustment { get; set; } = 1;
            public bool VolTrigger { get; set; } = false;
            public decimal OpenTradePortQty { get; set; } = 0;
            public int WeekTrades { get; set; } = 0;
            public int maxWeekTrades { get; set; } = 3;
            public bool expMedian { get; set; } = false;
            public decimal Expectancy => expMedian ? ExpectancyMedian : ExpectancyAverage;

            public decimal ExpectancyAverage
            {
                get
                {
                    if (HypotheticalTrades.Count() < 10)
                        return -1;
                    else
                    {
                        var pos = HypotheticalTrades.Count(x => x > 0);
                        var total = HypotheticalTrades.Count;
                        var avgWin = HypotheticalTrades.Where(x => x > 0).Any() ? HypotheticalTrades.Where(x => x > 0).Select(x => x).Average() : 0;
                        var avgLoss = HypotheticalTrades.Where(x => x <= 0).Any() ? HypotheticalTrades.Where(x => x <= 0).Select(x => x).Average() : 0;
                        return (avgWin * pos + avgLoss * (total - pos)) / total;
                    }
                }
            }
            public decimal ExpectancyMedian
            {
                get
                {
                    if (HypotheticalTrades.Count() < 10)
                        return -1;
                    else
                    {
                        var pos = HypotheticalTrades.Count(x => x > 0);
                        var total = HypotheticalTrades.Count;
                        var avgWin = HypotheticalTrades.Where(x => x > 0).Any() ? HypotheticalTrades.Where(x => x > 0).Select(x => x).Median() : 0;
                        var avgLoss = HypotheticalTrades.Where(x => x <= 0).Any() ? HypotheticalTrades.Where(x => x <= 0).Select(x => x).Median() : 0;
                        return (avgWin * pos + avgLoss * (total - pos)) / total;
                    }
                }
            }
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
            public RollingWindow<decimal> HypotheticalTrades = new RollingWindow<decimal>(100);
            public RollingWindow<ExpectancyData> ExpectancyTrades = new RollingWindow<ExpectancyData>(100);

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
        public class SymbolDirection
        {
            public SymbolDirection(string symbol, Direction direction)
            {
                Symbol = symbol;
                TradeDirection = direction;
            }
            public string Symbol { get; set; }
            public Direction TradeDirection { get; set; }
        }
        public enum Direction
        {
            Long,
            Short,
            Both
        }
        #endregion
    }
}
