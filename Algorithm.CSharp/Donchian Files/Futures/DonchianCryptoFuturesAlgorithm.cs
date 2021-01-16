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

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public partial class DonchianCryptoFuturesAlgorithm : QCAlgorithm
    {
        private List<string> symbols = new List<string>();

        private Dictionary<string, SymbolData> SymbolInfo = new Dictionary<string, SymbolData>();
        BinanceFuturesOrderProperties _liquidate = new BinanceFuturesOrderProperties();
        BinanceFuturesOrderProperties _immediateOrder = new BinanceFuturesOrderProperties();

        public Resolution resolution = Resolution.Hour;
        //private PythonMethods python = new PythonMethods();
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

        double startingequity = 45000;
        decimal SavingsAccountUSDTHoldings = 0, SwapAccountUSDHoldings = 0;
        decimal SavingsAccountNonUSDTValue = 0;
        decimal Holdings = 0;
        decimal maxPortExposure = 10;
        decimal maxLongDirection = 10;
        decimal maxShortDirection = 10;

        decimal StartingPortValue, PortfolioProfit, MaxPortValue, HighWaterMark, _prevOffExchangeValue = 0, _prevTotalPortfolioValue = 1;
        DateTime _prevDepostCheckDate;
        string currentAccount = "chrisholley23";
        bool customerAccount = false;
        bool masterAccount => !customerAccount;
        bool ExpectancyAudit = false;
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
            adjustVWAP = false;
        public string adjustVWAPType = "ATRonOpen"; //Entry Adjust Only, Week start, Extreme Distance only
        public decimal hardLossPercent = 0.01m; //done

        Symbol btc;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.BinanceFutures);
            SetStartDate(2020, 1, 1);
            SetEndDate(DateTime.UtcNow.AddDays(-1));

            SetAccountCurrency("USDT");
            SetCash(startingequity);
            ExpectancyAudit = Config.GetValue("ExpectancyAudit", ExpectancyAudit);
            MarketStateAudit = Config.GetValue("MarketStateAudit", MarketStateAudit);
            CoinFuturesAudit = Config.GetValue("CoinFuturesAudit", CoinFuturesAudit);
            if (LiveMode)
                SetWarmUp(TimeSpan.FromDays(ExpectancyAudit ? 260 : (CoinFuturesAudit ? 18 : 12)));
            else
                SetWarmUp(260);
            forest = new RandomForest(LiveMode ? MarketStateAudit : false);

            InitializeVariablesFromConfig();

            InitializeBinanceSavingsAccount();

            btc = AddEquity("BTCUSDT", Resolution.Minute, leverage: 20).Symbol;
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
            _immediateOrder.TIF = BinanceTimeInForce.IOC;

            try
            {
                RecalculatePortfolioValues();
                LoadPortfolioValues();
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            if (!ExpectancyAudit && LiveMode)
            {
                if (CheckSheetsForAudit())
                    ProcessSheetsExpectancyData(_expectancyData);
                else
                    LoadExpectancyData();
            }
            else
            {
                AddSymbols();

                Debug("Algorithm.Initialize(): Attempting to add " + symbols.Count() + " Symbols to the Algoithim");
                foreach (var index in symbols)
                {
                    if (!SymbolInfo.ContainsKey(index))
                        InitializeSymbolInfo(index);
                }
            }

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

                if (LiveMode)
                {
                    SavingsTransferAndConfigReset();

                    if (masterAccount)
                        ExpectancyEmail();
                }

            });

            Schedule.On(DateRules.EveryDay(btc), TimeRules.At(12, ExpectancyLoadTime, NodaTime.DateTimeZone.Utc), () =>
            {
                if (LiveMode)
                {
                    if (!ExpectancyAudit)
                    {
                        if (CheckSheetsForAudit())
                            ProcessSheetsExpectancyData(_expectancyData);
                        else
                            LoadExpectancyData();
                    }

                    if (masterAccount)
                        ExpectancyEmail();

                    if (customerAccount)
                        DailyProfitFee();

                    SavingsTransferAndConfigReset();

                    GetBinanceHistoricalTransactions(new DateTime(2020, 01, 01), DateTime.UtcNow, false);
                }
            });

            Schedule.On(DateRules.MonthEnd(btc), TimeRules.Midnight, () =>
            {
                if (LiveMode)
                {
                    SendMonthlyReport();
                }
            });
        }
        public override void OnWarmupFinished()
        {
            if (LiveMode)
            {
                try
                {
                    RecalculatePortfolioValues();
                    LoadPortfolioValues();
                }
                catch (Exception e)
                {
                    Log(e.Message);
                }

                Log("DonchianCryptoFuturesAlgorithm.OnWarmupFinished(): Retreiving Savings Account USDT value");

                SavingsAccountUSDTHoldings = GetBinanceSavingsAccountUSDTHoldings();
                SavingsAccountNonUSDTValue = GetBinanceTotalSavingsAccountNonUSDValue();
                SwapAccountUSDHoldings = GetBinanceSwapAccountUSDHoldings();

                SavingsTransferAndConfigReset();

                SavingsAccountUSDTHoldings = GetBinanceSavingsAccountUSDTHoldings();
                SavingsAccountNonUSDTValue = GetBinanceTotalSavingsAccountNonUSDValue();
                SwapAccountUSDHoldings = GetBinanceSwapAccountUSDHoldings();

                Log($"DonchianCryptoFuturesAlgorithm.OnWarmupFinished(): Savings account USDT value = {SavingsAccountUSDTHoldings + SavingsAccountNonUSDTValue + SwapAccountUSDHoldings}");

                Startup = false;

                if (ExpectancyAudit)
                {
                    SaveExpectancyData();
                }

                if (masterAccount)
                {
                    ExpectancyEmail();
                    if (CoinFuturesAudit)
                        SetHoldingsToTargetValue(true);
                }

                var holdingsCount = 0;
                var holdingsMessage = "";
                foreach (var security in Securities)
                {
                    if (Portfolio[security.Key.Value].HoldingsValue > 0)
                    {
                        holdingsCount++;
                        holdingsMessage += $", LONG Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 2)}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
                    }
                    else if (Portfolio[security.Key.Value].HoldingsValue < 0)
                    {
                        holdingsCount++;
                        holdingsMessage += $", SHORT Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 2)}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
                    }
                }
                notifyMessage = $"{holdingsCount} Futures Holdings" + holdingsMessage;

                Log(notifyMessage);

                GetBinanceHistoricalTransactions(new DateTime(2020, 01, 01), DateTime.UtcNow, false);
            }
            Startup = false;
        }

        public void InitializeSymbolInfo(string index)
        {
            if (!SymbolInfo.ContainsKey(index))
                SymbolInfo[index] = new SymbolData(index);

            int mult = 20;
            if (LiveMode && _symbolLimits != null)
            {
                mult = _symbolLimits.Where(y => y.SymbolOrPair == index).Any() ? _symbolLimits.Where(y => y.SymbolOrPair == index).First().Brackets.Where(x => x.Cap > _prevTotalPortfolioValue / 3).First().InitialLeverage : 20;
                var margin = _apiClient.FuturesUsdt.ChangeInitialLeverage(index, Math.Min(20, mult));
                Log($"Setting Multiplier for {index} to {Math.Min(20, mult)}");
            }

            var sym = AddEquity(index, Resolution.Minute, leverage: Math.Min(20, mult)).Symbol;

            // Establish Symbol Data for Index
            if (SymbolInfo.ContainsKey(index))
            {
                if (dchPeriod != 15)
                {
                    SymbolInfo[index].Donchian = new DonchianChannel(CreateIndicatorName(sym, "DCH" + dchPeriod, Resolution.Minute), dchPeriod);
                    SymbolInfo[index].RateOfChange = new RateOfChangePercent(CreateIndicatorName(sym, "ROCP" + 1, Resolution.Minute), 1);
                    SymbolInfo[index].STStdev = new StandardDeviation(CreateIndicatorName(sym, "STD" + 100, Resolution.Minute), 100);
                    SymbolInfo[index].LTStdev = new StandardDeviation(CreateIndicatorName(sym, "STD" + 500, Resolution.Minute), 500);

                    var thirty = new TradeBarConsolidator(TimeSpan.FromMinutes(30));
                    thirty.DataConsolidated += (sender, consolidated) =>
                    {
                        SymbolInfo[index].Donchian.Update(consolidated);
                        SymbolInfo[index].RateOfChange.Update(consolidated.Time, consolidated.Close);
                        SymbolInfo[index].STStdev.Update(consolidated.Time, SymbolInfo[index].RateOfChange.Current.Value);
                        SymbolInfo[index].LTStdev.Update(consolidated.Time, SymbolInfo[index].RateOfChange.Current.Value);
                    };

                    // we need to add this consolidator so it gets auto updates
                    SubscriptionManager.AddConsolidator(sym, thirty);
                }
                else
                {
                    SymbolInfo[index].Donchian = DCH(sym, dchPeriod, dchPeriod, resolution);
                }
                SymbolInfo[index].ATR = ATR(sym, 30, MovingAverageType.Simple, resolution);
                SymbolInfo[index].VWAP_week = VWAP(sym, resolution);
                SymbolInfo[index].BuyQuantity = 0;
                SymbolInfo[index].SellQuantity = 0;
                SymbolInfo[index].expMedian = medianExpectancy;

                if (index == "BTCUSDT" && LiveMode && masterAccount)
                {
                    var fourHour = new TradeBarConsolidator(TimeSpan.FromHours(4));
                    SymbolInfo[index].OBV_FourHour = new OnBalanceVolume(8);
                    SymbolInfo[index].ATR_FourHour = new AverageTrueRange(30, MovingAverageType.Simple);
                    SymbolInfo[index].Donchian_FourHour = new DonchianChannel(9, 7);
                    SymbolInfo[index].LogReturn = new LogReturn(1);
                    fourHour.DataConsolidated += (sender, consolidated) =>
                    {
                        SymbolInfo[index].LogReturn.Update(consolidated.EndTime, consolidated.Close);
                        if (CoinFuturesAudit)
                        {
                            ProcessFourHourData(consolidated);
                            //SetDipPurchases(false, consolidated.Time);
                        }

                        if (masterAccount)
                            DipPurchase(consolidated);
                    };

                    // we need to add this consolidator so it gets auto updates
                    SubscriptionManager.AddConsolidator(sym, fourHour);
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
            decimal pctChange = Portfolio.TotalUnrealisedProfit / (Portfolio.TotalPortfolioValue);
            if (TotalPortfolioValue > portMax)
                portMax = TotalPortfolioValue;
            notifyMessage = " New trades made";

            UpdateHoldings();

            openingTrades = 0;
            closingTrades = 0;
            if (decreasingRisk)
            {
                switch (decreasingType)
                {
                    case "AccountSize":
                        if ((Portfolio.TotalPortfolioValue) > 5000000)
                        {
                            risk = initialRisk / 3;
                        }
                        else if ((Portfolio.TotalPortfolioValue) > 1000000)
                        {
                            risk = initialRisk * 2 / 3 - (1 / 3 * initialRisk * (Portfolio.TotalPortfolioValue) / 5000000);
                        }
                        else
                        {
                            risk = initialRisk - (1 / 3 * initialRisk * (Portfolio.TotalPortfolioValue) / 1000000);
                        }
                        break;

                    case "Drawdown":
                        if (Portfolio.TotalPortfolioValue < (1 - drawDownRiskReduction) * portMax)
                        {
                            var subAmt = (Portfolio.TotalPortfolioValue - portMax) / portMax;
                            risk = initialRisk + drawDownRiskReduction + subAmt;
                        }
                        else
                            risk = initialRisk;
                        break;
                }
            }

            var top10 = 0;
            var oppoCount = 0;
            foreach (var i in symbols)
            {
                top10++;

                if (data.ContainsKey(SymbolInfo[i].Symbol))
                {
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

                    bool cont = PreTradePortfolioChecksNew(data, i, pctChange);
                    if (!cont)
                        continue;


                    if (top10 <= 10 || Portfolio[i].Invested)
                        PlaceTradesNew(data, i);
                }
            }

            if (oppoTrades && !IsWarmingUp)
            {
                top10 = 0;
                foreach (var i in symbols.OrderBy(x => SymbolInfo[x].Expectancy))
                {
                    if (SymbolInfo[i].Expectancy > 0)
                        break;
                    top10++;
                    if (data.ContainsKey(SymbolInfo[i].Symbol))
                    {
                        bool tradetime = IndicatorChecks(data, i);
                        if (!tradetime)
                            continue;

                        if ((SymbolInfo[i].Expectancy < 0 && SymbolInfo[i].Expectancy != -1 && top10 <= 10) || (SymbolInfo[i].Expectancy < 0 && SymbolInfo[i].Expectancy != -1 && Portfolio[i].Invested))
                            PlaceOppoTrades(data, i);
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

            PostTradePortfolioChecksNew(data);

            if (IsWarmingUp)
                return;

            DataReset();
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
                if (Portfolio[i].Quantity > 0 && SymbolInfo[i].BuyStop == null && !IsWarmingUp)
                {
                    SymbolInfo[i].BuyStop = Portfolio[i].AveragePrice - stopMult * SymbolInfo[i].ATR.Current.Value;
                }
                else if (Portfolio[i].Quantity < 0 && SymbolInfo[i].SellStop == null && !IsWarmingUp)
                {
                    SymbolInfo[i].SellStop = Portfolio[i].AveragePrice + stopMult * SymbolInfo[i].ATR.Current.Value;
                }
                else if (Portfolio[i].Quantity == 0)
                {
                    SymbolInfo[i].BuyQuantity = 0;
                    SymbolInfo[i].BuyStop = null;
                    SymbolInfo[i].SellQuantity = 0;
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

        public bool PreTradePortfolioChecks(Slice data, string i, decimal pctChange)
        {
            try
            {
                if (!Startup)
                    return true;

                if (data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
                    Transactions.CancelOpenOrders();

                if (pctChange < DrawdownReduction && DrawdownReduction < 0)
                {
                    if (!SymbolInfo[i].BreakerTriggered)
                    {
                        SymbolInfo[i].BreakerTriggered = true;
                        if (Portfolio[i].Invested)
                        {
                            // High Volatility breaker
                            if (Portfolio[i].UnrealizedProfit < (Portfolio.TotalPortfolioValue) / 100 * risk)
                            {
                                Log($"Algorithm.OnData(): Reduce Portfolio Profit Exposure: reducing small winning position on {i}");
                                BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity, _liquidate, tag: "PORTFOLIO BREAKER: Closing Losing and Small Winning Positions");
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
                        Log($"Algorithm.OnData(): Reduce Portfolio Profit Exposure: reducing large winning position on {i}");
                        BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity / 2, _liquidate, tag: "PORTFOLIO BREAKER: Reducing Large Winning Positions");
                    }
                }
                if (data[i].EndTime.Minute == 0)
                {
                    SymbolInfo[i].BreakerTriggered = false;
                    NotifyBreaker = true;
                }
                var portfolioOverloaded = (Holdings < (Portfolio.TotalPortfolioValue) * maxShortDirection * -1 || Holdings > (Portfolio.TotalPortfolioValue) * maxLongDirection) ? true : false;

                if (reduceMaxPositions && forestPredictions && portfolioOverloaded)
                {
                    if (Portfolio[i].Invested && forest.GetBitcoinMarketState() != RandomForest.MarketState.Unknown)
                    {
                        if ((forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && Portfolio[i].Quantity > 0)
                        || (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && Portfolio[i].Quantity < 0))
                        {
                            Log($"Algorithm.OnData(): Reduce Maximum Direction: closing losing and small winning positions on {i}");
                            BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity / 2, _liquidate, tag: "PORTFOLIO DIRECTION REDUCTION: Reducing Position on Wrong Side");
                            if (NotifyPortfolioOverload)
                            {
                                Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Reduce Maximum Direction", $"MarketState = {forest.GetBitcoinMarketState()}, Holdings = {Math.Abs(Holdings / ((Portfolio.TotalPortfolioValue) * maxShortDirection))}");
                                NotifyPortfolioOverload = false;
                            }
                        }
                    }
                }
                if (reEnterReducedPositions && SymbolInfo[i].openOrder?.Time.Day != Time.Day)
                {
                    var qty = (risk / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    var portqty = (risk * 4 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    var tradeqty = qty < portqty ? qty : portqty;
                    var priceRound = Securities[i].SymbolProperties.MinimumPriceVariation;
                    if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && Portfolio[i].Quantity > 0 && tradeqty > Portfolio[i].Quantity * 1.5m && Portfolio[i].AveragePrice > data[i].Close
                        && Holdings < (Portfolio.TotalPortfolioValue) * maxLongDirection)
                    {
                        Log($"Algorithm.OnData(): ReEnter Position on low quantity {i}");
                        decimal price = Math.Round(data[i].Close * (1 + .0006m) / priceRound) * priceRound;
                        SymbolInfo[i].openOrder = BinanceFuturesLimitOrder(i, tradeqty - Portfolio[i].Quantity, price, _immediateOrder, tag: "PORTFOLIO REENTRY LONG: Increasing Size");
                        openingTrades++;
                        notifyMessage += $": ReEnter Buy {i}, Qty {qty - Portfolio[i].Quantity}, " +
                            $"Total Available Qty {Securities[i].BidPrice}  GetOrderbookPriceFill()";
                    }
                    else if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && Portfolio[i].Quantity < 0 && tradeqty > Portfolio[i].Quantity * -1.5m && Portfolio[i].AveragePrice < data[i].Close
                        && Holdings > (Portfolio.TotalPortfolioValue) * maxShortDirection * -1)
                    {
                        Log($"Algorithm.OnData(): ReEnter Position on low quantity {i}");
                        decimal price = Math.Round(data[i].Close * (1 - .0006m) / priceRound) * priceRound;
                        SymbolInfo[i].openOrder = BinanceFuturesLimitOrder(i, -1 * tradeqty - Portfolio[i].Quantity, price, _immediateOrder, tag: "PORTFOLIO REENTRY SHORT: Increasing Size");
                        openingTrades++;
                        notifyMessage += $": ReEnter Sell {i}, Qty {-1 * qty - Portfolio[i].Quantity} ";
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

        public bool PreTradePortfolioChecksNew(Slice data, string i, decimal pctChange)
        {
            try
            {
                if (!Startup)
                    return true;

                Transactions.CancelOpenOrders();

                if (pctChange < DrawdownReduction && DrawdownReduction < 0)
                {
                    if (!SymbolInfo[i].BreakerTriggered)
                    {
                        SymbolInfo[i].BreakerTriggered = true;
                        if (Portfolio[i].Invested)
                        {
                            // High Volatility breaker
                            if (Portfolio[i].UnrealizedProfit < (Portfolio.TotalPortfolioValue) / 100 * risk)
                            {
                                Log($"Algorithm.OnData(): Reduce Portfolio DrawdownReduction: removing small profit position on {i}");
                                BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity, _liquidate, tag: "PORTFOLIO BREAKER: Closing Losing and Small Winning Positions");
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
                        Log($"Algorithm.OnData(): Reduce Portfolio DrawdownReduction: reducing large winning position on {i}");
                        BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity / 2, _liquidate, tag: "PORTFOLIO BREAKER: Reducing Large Winning Positions");
                    }
                }
                if (data[i].EndTime.Minute == 0)
                {
                    SymbolInfo[i].BreakerTriggered = false;
                    NotifyBreaker = true;
                }
                var portfolioOverloaded = (Holdings < (Portfolio.TotalPortfolioValue) * maxShortDirection * -1 || Holdings > (Portfolio.TotalPortfolioValue) * maxLongDirection) ? true : false;

                if (reduceMaxPositions && forestPredictions && portfolioOverloaded)
                {
                    if (Portfolio[i].Invested && forest.GetBitcoinMarketState() != RandomForest.MarketState.Unknown)
                    {
                        if ((forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && Portfolio[i].Quantity > 0)
                        || (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && Portfolio[i].Quantity < 0))
                        {
                            Log($"Algorithm.OnData(): Reduce Maximum Direction: closing losing and small winning positions on {i}");
                            BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity / 2, _liquidate, tag: "PORTFOLIO DIRECTION REDUCTION: Reducing Position on Wrong Side");
                            if (NotifyPortfolioOverload)
                            {
                                Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Reduce Maximum Direction", $"MarketState = {forest.GetBitcoinMarketState()}, Holdings = {Math.Abs(Holdings / ((Portfolio.TotalPortfolioValue) * maxShortDirection))}");
                                NotifyPortfolioOverload = false;
                            }
                        }
                    }
                }
                if (reEnterReducedPositions && SymbolInfo[i].Expectancy > 0)
                {
                    decimal tradeqty = Portfolio[i].Quantity > 0 ? GetPositionSize(i, Direction.Long) : GetPositionSize(i, Direction.Short);
                    if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && Portfolio[i].Quantity > 0 && tradeqty > Portfolio[i].Quantity * 1.5m && Portfolio[i].AveragePrice > data[i].Close
                        && Holdings < (Portfolio.TotalPortfolioValue) * maxLongDirection)
                    {
                        Log($"Algorithm.OnData(): ReEnter Position on low quantity {i}");
                        SymbolInfo[i].openOrder = BinanceFuturesMarketOrder(i, tradeqty - Portfolio[i].Quantity, _immediateOrder, tag: "PORTFOLIO REENTRY LONG: Increasing Size");
                        openingTrades++;
                        notifyMessage += $": ReEnter Buy {i}, Qty {tradeqty - Portfolio[i].Quantity}, " +
                            $"Total Available Qty {Securities[i].BidPrice}  GetOrderbookPriceFill()";
                    }
                    else if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && Portfolio[i].Quantity < 0 && tradeqty < Portfolio[i].Quantity * -1.5m && Portfolio[i].AveragePrice < data[i].Close
                        && Holdings > (Portfolio.TotalPortfolioValue) * maxShortDirection * -1)
                    {
                        Log($"Algorithm.OnData(): ReEnter Position on low quantity {i}");
                        SymbolInfo[i].openOrder = BinanceFuturesMarketOrder(i, -1 * tradeqty - Portfolio[i].Quantity, _immediateOrder, tag: "PORTFOLIO REENTRY SHORT: Increasing Size");
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
                /*
                if (data[i].EndTime.Minute == 0)
                    SymbolInfo[i].VolTrigger = false;
                */
                if (((data[i].Close < SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                         || data[i].Close < SymbolInfo[i].testBuyStop) && SymbolInfo[i].testLongIsOpen)
                {
                    SymbolInfo[i].testBuyQuantity = 0;
                    var ret = SymbolInfo[i].testATR == null || SymbolInfo[i].testATR == 0 ? (data[i].Close - SymbolInfo[i].testLongEntryPrice) / (SymbolInfo[i].ATR.Current.Value) : (data[i].Close - SymbolInfo[i].testLongEntryPrice) / (SymbolInfo[i].testATR.Value);
                    SymbolInfo[i].testLongIsOpen = false;
                    SymbolInfo[i].testLongEntryPrice = 0;
                    SymbolInfo[i].testATR = null;
                    if (!IsWarmingUp)
                    {
                        SymbolInfo[i].HypotheticalTrades.Add(ret);
                        if (masterAccount)
                            SaveExpectancyData(new ExpectancyData(i, -1, ret, DateTime.UtcNow));
                    }
                    else if (ExpectancyAudit)
                        SymbolInfo[i].HypotheticalTrades.Add(ret);

                    if (data[i].EndTime.Minute != 0 && SymbolInfo[i].Expectancy > 0)
                        SymbolInfo[i].VolTrigger = true;

                    if (!SymbolInfo.Values.Where(x => x.Expectancy > -1).Any())
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
                    if (!IsWarmingUp)
                    {
                        SymbolInfo[i].HypotheticalTrades.Add(ret);
                        if (masterAccount)
                            SaveExpectancyData(new ExpectancyData(i, -1, ret, DateTime.UtcNow));
                    }
                    else if (ExpectancyAudit)
                        SymbolInfo[i].HypotheticalTrades.Add(ret);

                    if (data[i].EndTime.Minute != 0 && SymbolInfo[i].Expectancy > 0)
                        SymbolInfo[i].VolTrigger = true;

                    if (!SymbolInfo.Values.Where(x => x.Expectancy > -1).Any())
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
                var tickSize = Securities[i].SymbolProperties.LotSize;
                var priceRound = Securities[i].SymbolProperties.MinimumPriceVariation;
                var tradeTime = data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30;


                if (((data[i].Close < SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                    || data[i].Close < SymbolInfo[i].BuyStop) && Portfolio[i].Quantity > 0)
                {
                    Log($"Algorithm.OnData(): Close buy order signal generated on on {i}, Close: {data[i].Close}, VWAP: {Math.Round(SymbolInfo[i].VWAP_week.Current.Value, 3)}, Buy Stop: {SymbolInfo[i].BuyStop}");
                    notifyMessage += data[i].EndTime.Minute == 0 ?
                        $": CLOSING on VWAP Long on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%" :
                        $": CLOSING on Vol Long on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%";
                    closingTrades++;
                    BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity, _liquidate, tag: "Liquidating Long Position, VWAP Stop Triggered");
                }
                else if (((data[i].Close > SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                    || data[i].Close > SymbolInfo[i].SellStop) && Portfolio[i].Quantity < 0)
                {
                    Log($"Algorithm.OnData(): Close sell order signal generated on {i}, Close: {data[i].Close}, VWAP: {Math.Round(SymbolInfo[i].VWAP_week.Current.Value, 3)}, Sell Stop: {SymbolInfo[i].SellStop}");
                    notifyMessage += data[i].EndTime.Minute == 0 ?
                        $": CLOSING on VWAP Short on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%" :
                        $": CLOSING on Vol Short on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%";
                    closingTrades++;
                    BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity, _liquidate, tag: "Liquidating Short Position, VWAP Stop Triggered");
                }

                //////////////////if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
                //{
                if (tradeTime && data[i].Close > SymbolInfo[i].OpenLongEntry && data[i].Close > SymbolInfo[i].VWAP_week.Current.Value
                    && Portfolio[i].Quantity == 0
                    && Holdings < (Portfolio.TotalPortfolioValue) * maxLongDirection
                    && SymbolInfo[i].StdevDifference <= 0
                    && SymbolInfo[i].VolTrigger == false)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Buys signal generated on " + i);
                    var portqty = (risk * 5 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].BuyQuantity = (decimal)SymbolInfo[i].SizeAdjustment * (risk / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].BuyStop = data[i].Close - stopMult * SymbolInfo[i].ATR.Current.Value;
                    if (SymbolInfo[i].BuyQuantity != 0)
                    {
                        if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull)
                            SymbolInfo[i].BuyQuantity = SymbolInfo[i].BuyQuantity * (risk + forestAdjustment) / risk;
                        decimal qty = SymbolInfo[i].BuyQuantity < portqty ? Math.Round(SymbolInfo[i].BuyQuantity / tickSize, 0) * tickSize : Math.Round(portqty / tickSize, 0) * tickSize;
                        decimal price = Math.Round(Securities[i].Price * 1.0014m / priceRound) * priceRound;

                        if (qty > 0)
                        {
                            SymbolInfo[i].openOrder = BinanceFuturesLimitOrder(i, qty, price, tag: "Buy Signal Generated");
                            notifyMessage += $": Buy {i}, Qty {qty} ";
                            openingTrades++;
                            return;
                        }
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
                if (tradeTime && data[i].Close < SymbolInfo[i].OpenShortEntry && data[i].Close < SymbolInfo[i].VWAP_week.Current.Value
                    && Portfolio[i].Quantity == 0
                    && Holdings > (Portfolio.TotalPortfolioValue) * maxShortDirection * -1
                    && SymbolInfo[i].StdevDifference <= 0
                    && SymbolInfo[i].VolTrigger == false)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Sell signal generated on " + i);
                    var portqty = (risk * 5 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].SellQuantity = (decimal)SymbolInfo[i].SizeAdjustment * ((risk + forestAdjustment / 3) / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                    SymbolInfo[i].SellStop = data[i].Close + stopMult * SymbolInfo[i].ATR.Current.Value;
                    if (SymbolInfo[i].SellQuantity != 0)
                    {
                        if (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear)
                            SymbolInfo[i].SellQuantity = SymbolInfo[i].SellQuantity * (risk + forestAdjustment) / risk;
                        decimal qty = SymbolInfo[i].SellQuantity < portqty ? -1 * Math.Round(SymbolInfo[i].SellQuantity / tickSize, 0) * tickSize : -1 * Math.Round(portqty / tickSize, 0) * tickSize;
                        decimal price = Math.Round(Securities[i].Price * (1 - .0014m) / priceRound) * priceRound;

                        if (qty < 0)
                        {
                            SymbolInfo[i].openOrder = BinanceFuturesLimitOrder(i, qty, price, tag: "Sell Signal Generated");
                            notifyMessage += $": Sell {i}, Qty {qty} ";
                            openingTrades++;
                            return;
                        }
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
                Debug("Error Processing PlaceTrades() - " + e.Message);
            }
        }

        public void PlaceTradesNew(Slice data, string i)
        {
            try
            {
                if (Portfolio[i].Invested)
                {
                    if (ExitCondition(data, i))
                    {
                        Log($"Algorithm.OnData(): Close regular order generated on {i}, Expectancy: {Math.Round(SymbolInfo[i].Expectancy, 2)}, Port Value: {Math.Round(Portfolio.TotalPortfolioValue, 2)}");
                        notifyMessage += data[i].EndTime.Minute == 0 ?
                            $": CLOSING on VWAP {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%" :
                            $": CLOSING on Vol {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%";
                        closingTrades++;

                        BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity, _liquidate, tag: "Liquidating Long Position, VWAP Stop Triggered");
                        if (data[i].EndTime.Minute != 0)
                            SymbolInfo[i].VolTrigger = true;
                    }
                }

                var tradeTime = data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30;
                //////////////////if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
                //{
                if (EnterCondition(data, i) == Direction.Long)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Buys signal generated on " + i);
                    SymbolInfo[i].BuyStop = data[i].Close - stopMult * SymbolInfo[i].ATR.Current.Value;

                    decimal qty = GetPositionSize(i, Direction.Long);
                    decimal price = GetPositionPrice(i, Direction.Long);

                    SymbolInfo[i].OpenTradePortQty = Portfolio.TotalPortfolioValue;

                    if (qty > 0)
                    {
                        SymbolInfo[i].openOrder = BinanceFuturesLimitOrder(i, qty, price, tag: "Buy Signal Generated");
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
                if (EnterCondition(data, i) == Direction.Short)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Sell signal generated on " + i);
                    SymbolInfo[i].SellStop = data[i].Close + stopMult * SymbolInfo[i].ATR.Current.Value;

                    decimal qty = GetPositionSize(i, Direction.Short);
                    decimal price = GetPositionPrice(i, Direction.Short);

                    SymbolInfo[i].OpenTradePortQty = Portfolio.TotalPortfolioValue;

                    if (qty < 0)
                    {
                        SymbolInfo[i].openOrder = BinanceFuturesLimitOrder(i, qty, price, tag: "Sell Signal Generated");
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
                Debug($"Error Processing PlaceTradesNew() - {e.Message}, {e.StackTrace}");
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
                        Log($"Algorithm.OnData(): Close Oppo order signal generated on on {i}, Expectancy: {Math.Round(SymbolInfo[i].Expectancy, 2)}, Port Value: {Math.Round(Portfolio.TotalPortfolioValue, 2)}");
                        notifyMessage += data[i].EndTime.Minute == 0 ?
                            $": CLOSING Oppo Order on VWAP {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%" :
                            $": CLOSING Oppo Order on Vol {(Portfolio[i].IsLong ? "Long" : "Short")} on {i}, Profit {Math.Round(Portfolio[i].UnrealizedProfit / (Portfolio.TotalPortfolioValue) * 100, 2)}%";
                        closingTrades++;
                        BinanceFuturesMarketOrder(i, -Portfolio[i].Quantity, _liquidate, tag: "Liquidating Long Position, VWAP Stop Triggered");
                        if (data[i].EndTime.Minute != 0)
                            SymbolInfo[i].VolTrigger = true;
                    }
                }

                var tradeTime = data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30;
                if (EnterCondition(data, i, true) == Direction.Long)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Oppo 'Buy' signal generated on {i}, shorting into strength");

                    SymbolInfo[i].BuyStop = data[i].Close - stopMult * SymbolInfo[i].ATR.Current.Value;

                    decimal qty = GetPositionSize(i, Direction.Long, true);
                    decimal price = GetPositionPrice(i, Direction.Long, true);

                    SymbolInfo[i].OpenTradePortQty = Portfolio.TotalPortfolioValue;

                    if (qty < 0)
                    {
                        BinanceFuturesLimitOrder(i, qty, price, tag: "Oppo Buy Signal Generated");
                        notifyMessage += $": Oppo 'Buy', Going Short {i}, Qty {qty} ";
                        openingTrades++;
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
                if (EnterCondition(data, i, true) == Direction.Short)
                {
                    Log($"Algorithm.OnData(): {data[i].EndTime} - Oppo 'Sell' signal generated on {i}, buying into weakness");
                    SymbolInfo[i].SellStop = data[i].Close + stopMult * SymbolInfo[i].ATR.Current.Value;

                    decimal qty = GetPositionSize(i, Direction.Short, true);
                    decimal price = GetPositionPrice(i, Direction.Short, true);
                    SymbolInfo[i].OpenTradePortQty = Portfolio.TotalPortfolioValue;

                    if (qty > 0)
                    {
                        BinanceFuturesLimitOrder(i, qty, price, tag: "Oppo Sell Signal Generated");
                        notifyMessage += $": Oppo 'Sell', Going Long {i}, Qty {qty} ";
                        openingTrades++;
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
                Debug($"Error Processing PlaceOppoTrades() - {e.Message}, {e.StackTrace}");
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

            if (hardLossLimit && Portfolio[i].UnrealizedProfit / (SymbolInfo[i].OpenTradePortQty == 0 ? Portfolio.TotalPortfolioValue : SymbolInfo[i].OpenTradePortQty) < -hardLossPercent)
                return true;

            return false;
        }
        public Direction EnterCondition(Slice data, string i, bool oppoTrade = false)
        {
            var tradeTime = data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30;

            if (volTrigger && SymbolInfo[i].VolTrigger)
            {
                if (LiveMode)
                    Log($"Volatility Trigger on {i}, no trade");
                return Direction.Both;
            }

            if (tradeLimit && SymbolInfo[i].WeekTrades > SymbolInfo[i].maxWeekTrades)
            {
                if (LiveMode)
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
                if (LiveMode)
                    Log($"Already in a trade for {i}, quantity = {Portfolio[i].Quantity}");
                return Direction.Both;
            }

            if (SymbolInfo[i].StdevDifference > 0)
            {
                if (LiveMode)
                    Log($"STD Difference too large for {i}, difference = {SymbolInfo[i].StdevDifference}");
                return Direction.Both;
            }

            if (data[i].Close > SymbolInfo[i].OpenLongEntry && data[i].Close > SymbolInfo[i].VWAP_week.Current.Value)
            {
#pragma warning disable CA1305 // Specify IFormatProvider
                Log($"Time to place a long trade on {i}, oppoTrade = {oppoTrade}, holdings room? {(oppoTrade ? (Holdings < Portfolio.TotalPortfolioValue * maxLongDirection).ToString() : (Holdings > Portfolio.TotalPortfolioValue * maxShortDirection * -1).ToString())}");
#pragma warning restore CA1305 // Specify IFormatProvider
                if (!oppoTrade &&
                    Holdings < Portfolio.TotalPortfolioValue * maxLongDirection)
                {
                    return Direction.Long;
                }
                else if (oppoTrade &&
                    Holdings > Portfolio.TotalPortfolioValue * maxShortDirection * -1)
                {
                    return Direction.Long;
                }
            }
            else if (data[i].Close < SymbolInfo[i].OpenShortEntry && data[i].Close < SymbolInfo[i].VWAP_week.Current.Value)
            {
#pragma warning disable CA1305 // Specify IFormatProvider
                Log($"Time to place a short trade on {i}, oppoTrade = {oppoTrade}, holdings room? {(!oppoTrade ? (Holdings < Portfolio.TotalPortfolioValue * maxLongDirection).ToString() : (Holdings > Portfolio.TotalPortfolioValue * maxShortDirection * -1).ToString())}");
#pragma warning restore CA1305 // Specify IFormatProvider
                if (!oppoTrade &&
                    Holdings > Portfolio.TotalPortfolioValue * maxShortDirection * -1)
                {
                    return Direction.Short;
                }
                else if (oppoTrade &&
                    Holdings < Portfolio.TotalPortfolioValue * maxLongDirection)
                {
                    return Direction.Short;
                }
            }
            return Direction.Both;
        }
        public void PostTradePortfolioChecks(Slice data)
        {
            decimal pctChange = 0;
            if (Portfolio.TotalUnrealisedProfit == 0)
                return;
            else
                pctChange = Portfolio.TotalUnrealisedProfit / (Portfolio.TotalPortfolioValue);

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
                        if (!Portfolio[i].Invested || SymbolInfo[i].Expectancy > -1 || IsWarmingUp)
                            continue;
                        if (UpMoveReduction <= 0) { }
                        else if (pctChange > UpMoveReduction && Portfolio[i].Invested)
                        {
                            Log($"Algorithm.OnData(): Reduce Portfolio Profit Exposure: reducing small winning position on {i}");
                            var qty = (risk / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
                            qty = Portfolio[i].IsLong ? qty : qty * -1;
                            BinanceFuturesMarketOrder(i, -(Portfolio[i].Quantity - qty), _liquidate, tag: "Preserving Proft: Reducing Positions");
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

        public void PostTradePortfolioChecksNew(Slice data)
        {
            decimal pctChange = 0;
            pctChange = Portfolio.TotalUnrealisedProfit / (Portfolio.TotalPortfolioValue);

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
                        if (!Portfolio[i].Invested || IsWarmingUp)
                            continue;
                        if (UpMoveReduction <= 0) { }
                        else if (pctChange > UpMoveReduction && Portfolio[i].Invested)
                        {
                            Log($"Algorithm.OnData(): Reduce Portfolio Profit Exposure: reducing small winning position on {i}, Total Portfolio Profit = " + pctChange);
                            var qty = GetPositionSize(i, (Portfolio[i].IsLong ? Direction.Long : Direction.Short));
                            if (SymbolInfo[i].Expectancy > 0)
                                BinanceFuturesMarketOrder(i, -(Portfolio[i].Quantity - qty), _liquidate, tag: $"Preserving Proft: Reducing Position on {i}");
                            else
                                BinanceFuturesMarketOrder(i, -(Portfolio[i].Quantity), _liquidate, tag: $"Preserving Proft: Reducing Oppo Position on {i}");
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

        public void DataReset()
        {
            Holdings = 0;

            if (!LiveMode)
                return;

            NotifyBreaker = true;
            NotifyPortfolioOverload = true;
            //NotifyWeekendReduction = true;
            NotifyDrawdownReduction = true;
            var msgbegin = $"Total portfolio Profit: {Math.Round(100 * PortfolioProfit, 2)}%," +
                $" Current DD: {Math.Min(0, Math.Round(100 * (((1 + PortfolioProfit) - (1 + MaxPortValue)) / (1 + MaxPortValue)), 2))}%," +
                $" Total Portfolio Margin: {Math.Round(100 * (Portfolio.GetBuyingPower(Securities["BTCUSDT"].Symbol) / (Portfolio.TotalPortfolioValue)), 2)}%";
            if (SavingsAccountUSDTHoldings + SavingsAccountNonUSDTValue + SwapAccountUSDHoldings != 0)
                msgbegin += $", Binance Open Margin: {Math.Round(100 * (1 - (Portfolio.GetBuyingPower(Securities["BTCUSDT"].Symbol) / Portfolio.TotalPortfolioValue)), 2)}%";
            notifyMessage = msgbegin + $" || {numMessageSymbols + notifyMessage}";

            if (DateTime.UtcNow.Minute == 30 || DateTime.UtcNow.Minute == 0 || numMessageSymbols != 0)
            {
                var holdingsCount = 0;
                var holdingsMessage = "";
                foreach (var security in Securities)
                {
                    if (Portfolio[security.Key.Value].HoldingsValue > 0)
                    {
                        holdingsCount++;
                        holdingsMessage += $", LONG Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 2)}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
                    }
                    else if (Portfolio[security.Key.Value].HoldingsValue < 0)
                    {
                        holdingsCount++;
                        holdingsMessage += $", SHORT Value of {security.Key.Value} = {Math.Round(Portfolio[security.Key.Value].HoldingsValue, 2)}, Expectancy = {Math.Round(SymbolInfo[security.Key.Value].Expectancy, 2)}";
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
        }

        public decimal GetPositionSize(string i, Direction direction, bool oppTrade = false)
        {
            var tickSize = Securities[i].SymbolProperties.LotSize;
            var portqty = (risk * 5 / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;
            var qty = 0m;
            if (direction == Direction.Long)
            {
                qty = SymbolInfo[i].SizeAdjustment * (risk / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;

                if ((forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && !oppTrade) ||
                    (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && oppTrade))
                    qty = qty * (risk + forestAdjustment) / risk;
            }
            else if (direction == Direction.Short)
            {
                qty = SymbolInfo[i].SizeAdjustment * ((risk + forestAdjustment / 3) / 100) * (Portfolio.TotalPortfolioValue) / SymbolInfo[i].ATR.Current.Value;

                if ((forest.GetBitcoinMarketState() == RandomForest.MarketState.Bear && !oppTrade) ||
                    (forest.GetBitcoinMarketState() == RandomForest.MarketState.Bull && oppTrade))
                    qty = qty * (risk + forestAdjustment) / risk;
            }

            if (oppTrade)
                qty = -qty / (SymbolInfo[i].SizeAdjustment == 0 ? 1 : SymbolInfo[i].SizeAdjustment);

            if (oppTrade && oppoSizeAdjust)
                qty = qty * -((SymbolInfo[i].SizeAdjustment == 0 ? 1 : SymbolInfo[i].SizeAdjustment) - 2);
            //qty = qty * -(SymbolInfo[i].SizeAdjustment / 2 - 1);

            if (adjustSizeDollarVolume)
            {
                //TODO
            }

            if (adjustSizeTradeLimit)
                qty = 2 * qty / Math.Max(2, SymbolInfo[i].WeekTrades);

            if (adjustSizeVolTrigger)
                qty = SymbolInfo[i].VolTrigger ? qty / 2 : qty;

            qty = Math.Abs(qty) < portqty ? qty : (qty < 0 ? -portqty : portqty);
            /*if (_symbolLimits != null)
            {
                qty = _symbolLimits.Any(x => x.SymbolOrPair == i) ? Math.Min(_symbolLimits.First(x => x.SymbolOrPair == i).Brackets.Where(x => x.Cap > TotalPortfolioValue / 3).First().Cap / Securities[i].Price, qty) : qty;
            }*/

            qty = Math.Round(qty / tickSize, 0) * tickSize;
            return (direction == Direction.Short ? -1 * qty : qty);
        }

        public decimal GetPositionPrice(string i, Direction direction, bool oppTrade = false)
        {
            var price = Securities[i].Price;
            var priceRound = Securities[i].SymbolProperties.MinimumPriceVariation;
            if ((direction == Direction.Long && !oppTrade) ||
                (direction == Direction.Short && oppTrade))
                price = Math.Round(Securities[i].Price * 1.0014m / priceRound) * priceRound;

            if ((direction == Direction.Short && !oppTrade) ||
                    (direction == Direction.Long && oppTrade))
                price = Math.Round(Securities[i].Price * (1 - .0014m) / priceRound) * priceRound;

            return price;
        }
        public void SavingsTransferAndConfigReset()
        {
            if (!LiveMode)
                return;

            string dipMsg = "Daily Account Adjustment";
            if (IsWarmingUp && !Startup)
                return;

            ResetBinanceTimestamp();
            /*
            if (IsSpotExchangeActive())
            {
                var spot = SpotWalletUSDTTransfer();
                var save = SpotWalletToSavingsTransfer();
                dipMsg += $": Spot wallet dispersion success = {spot}";
            }
            else
            {
                dipMsg += $": Spot Exchange Inactive, Please try again later";
            }*/
            // Move dollars to savings if greater than 50% of total account values
            SavingsAccountUSDTHoldings = GetBinanceSavingsAccountUSDTHoldings();
            SwapAccountUSDHoldings = GetBinanceSwapAccountUSDHoldings();
            SavingsAccountNonUSDTValue = GetBinanceTotalSavingsAccountNonUSDValue();

            ResetConfigVariables();
            /*
            var totalAccount = TotalPortfolioValue;
            if (Portfolio.CashBook["USDT"].ValueInAccountCurrency / totalAccount > 0.35m)
            {
                var value = Math.Ceiling(Math.Max(Portfolio.CashBook["USDT"].ValueInAccountCurrency - (0.3m * totalAccount), 0));
                if (value > 0)
                {//TODO Switch to swap
                    FuturesUSDTSavingsTransfer(TransferDirection.FuturesToSavings, value);
                    //FuturesUSDTSavingsTransfer(TransferDirection.SwapToFutures, swapqty);
                    dipMsg += $" : ${value} Futures USDT transferred to savings";
                }
                else
                {
                    dipMsg += $" : MANUAL TRANSFER REQUIRED : ${(int)(Portfolio.CashBook["USDT"].ValueInAccountCurrency - (0.3m * totalAccount))} Futures USDT transfer to savings";
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Binance Daily Account Adjustment", dipMsg);
                }
            }
            // Move dollars to trade account if less than 25% of total account values
            else if (Portfolio.CashBook["USDT"].ValueInAccountCurrency / totalAccount < 0.2m)
            {
                var value = Math.Floor(Math.Min(SavingsAccountUSDTHoldings + SwapAccountUSDHoldings, (0.3m * totalAccount) - Portfolio.CashBook["USDT"].ValueInAccountCurrency));
                if (value > 0)
                {
                    var savingsqty = Math.Min(0, value - SwapAccountUSDHoldings);
                    var swapqty = Math.Min(0, value - SavingsAccountUSDTHoldings);
                    FuturesUSDTSavingsTransfer(TransferDirection.SavingsToFutures, savingsqty);
                    //FuturesUSDTSavingsTransfer(TransferDirection.SwapToFutures, swapqty);
                    dipMsg += $" : ${value} Savings USDT transferred to futures";
                }
                else
                {
                    dipMsg += $" : MANUAL TRANSFER REQUIRED : ${(int)((0.3m * totalAccount) - Portfolio.CashBook["USDT"].ValueInAccountCurrency)} Savings USDT transfer to futures";
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Binance Daily Account Adjustment", dipMsg);
                }
            }
            else
            {
                dipMsg += $" : No transfer required, account quantities satisfactory";
                Log(dipMsg);
            }
            if (IsWarmingUp)
                return;
            */
        }

        public void DipPurchase(Data.Market.TradeBar data)
        {
            if (IsWarmingUp || !LiveMode)
                return;

            string dipMsg = "Daily Dip Purchase";
            string i = "BTCUSDT";
            var ret = SymbolInfo[i].LogReturn.Current.Value;
            var longCondition = ret < -.01m;
            if (longCondition)
            {
                dipMsg += $" : Puchasing bitcoin as investment on dip of { Math.Round(ret * 100, 2)}%, qty = $";
                ret = Math.Abs(ret);
                Log($"Algorithm.OnData(): {Time} - Buys signal generated on " + i);
                decimal buyqty = Math.Abs(ret * (TotalPortfolioValue) / (6 * (decimal)Math.Sqrt(100 * (double)ret)));
                decimal qty = Math.Round(buyqty, 2);
                if (Portfolio.TotalPortfolioValue / TotalPortfolioValue < 0.2m && !CoinFuturesAudit)
                {
                    dipMsg += $" : Not enough funds in trading account to make ${qty} bitcoin purchase required on return of { Math.Round(ret * 100, 2)}%";
                }
                else if (qty > 10)
                {
                    dipMsg += $"{qty}";
                    var invest = BitcoinInvestment(qty);
                    dipMsg += $" - Trade success = {invest}";
                }
            }
            else
            {
                dipMsg += $" : No bitcoin purchase required on return of {Math.Round(ret * 100, 2)}%";
            }
            try
            {
                dipMsg += goldDipPurchase(data);
            }
            catch (Exception e)
            {
                Log(e.ToString());
                Log(e.StackTrace);
            }

            Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Binance Daily Dip Purchase", dipMsg);
            SaveTotalAccountValue("EOD");
        }

        public void DailyProfitFee()
        {
            if (IsWarmingUp || !LiveMode)
                return;

            if (PortfolioProfit > HighWaterMark)
            {
                //Invest in something and transfer to Main account
                decimal qty = Math.Round(profitPercent * TotalPortfolioValue * (PortfolioProfit - HighWaterMark), 2);
                var feepmt = MasterFeePayment(qty);
                if (!feepmt)
                    feepmt = MasterFeePayment(qty);

                if (feepmt)
                {
                    string dipMsg = $"Customer Fee Withdrawal : SUCCESS : ${(int)qty} fee on high water mark difference of ${(int)(TotalPortfolioValue * (PortfolioProfit - HighWaterMark))} and fee of {profitPercent}%";
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + ": Customer Fee Withdrawal", dipMsg);
                    SaveTotalAccountValue("EOD");
                }
                else
                {
                    string dipMsg = $"Customer Fee Withdrawal : FAIL : ${(int)qty} fee on high water mark difference of ${(int)(TotalPortfolioValue * (PortfolioProfit - HighWaterMark))} and fee of {profitPercent}%, Manual transfer and protfolio value update required";
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + ": FAIL Customer Fee Withdrawal", dipMsg);
                }
            }
            else
                SaveTotalAccountValue("EOD");

            LoadPortfolioValues();
        }


        #region Brokerage Events
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            var order = Transactions.GetOrderById(orderEvent.OrderId);
            if (orderEvent.Status == OrderStatus.Invalid)
            {
                notifyMessage = $"Invalid order on {order.Symbol}, Reason: {orderEvent.Message}";
                Log($"OnOrderEvent: {notifyMessage}");
                Notify.Email("christopherjholley23@gmail.com", currentAccount + ": BINANCE: Invalid Order", notifyMessage);
            }
            else if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.Canceled)
            {
                SaveTotalAccountValue();
            }
            else
            {
                Log($"OnOrderEvent: {orderEvent} - Total open qty = {Portfolio[orderEvent.Symbol].Quantity}");
            }
        }

        public override void OnBrokerageMessage(BrokerageMessageEvent messageEvent)
        {
            if (messageEvent.Code == "1006")
            {
                Notify.Email("chrisholley23@gmail.com", currentAccount + ": Brokerage Disconnected - 1006", messageEvent.Message);
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
        /// <summary>
        /// Save a list of trades to disk for a given path
        /// </summary>
        /// <param name="transactions">Transactions list via an OrderProvider</param>
        /// <param name="csvFileName">File path to create</param>
        private void SaveTotalAccountValue(string transactionType = "TRADE")
        {
            if (!LiveMode)
                return;

            string csvFileName = $"../../../Data/TotalPortfolioValues/{currentAccount}/{currentAccount}_TotalPortfolioValues.csv";

            var path = Path.GetDirectoryName(csvFileName);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);

            if (!File.Exists(csvFileName))
            {
                using (var writer = new StreamWriter(csvFileName))
                {
                    var header = ($"Time,binanceBalance,totalAccountsBalance,TransactionType,tradeProfitPercent,totalPortfolioProfit,portfolioDrawdown,offExchangeValue");
                    var line = ($"{Time.ToStringInvariant("yyyy-MM-dd HH:mm:ss")},") +
                                ($"{Math.Round(TotalPortfolioValue - _prevOffExchangeValue, 2)},{Math.Round(TotalPortfolioValue, 2)},EOD,0,0,0,{_prevOffExchangeValue}");
                    writer.WriteLine(header);
                    writer.WriteLine(line);
                }
            }
            else
            {
                using (var writer = new StreamWriter(csvFileName, append: true))
                {
                    var percentGain = transactionType == "TRADE" ? (TotalPortfolioValue - _prevTotalPortfolioValue) / _prevTotalPortfolioValue : 0m;
                    PortfolioProfit = ((PortfolioProfit + 1) * (percentGain + 1)) - 1;

                    if (transactionType == "EOD" && PortfolioProfit > MaxPortValue)
                        HighWaterMark = PortfolioProfit;

                    MaxPortValue = PortfolioProfit > MaxPortValue ? PortfolioProfit : MaxPortValue;
                    var portDD = ((1 + PortfolioProfit) - (1 + MaxPortValue)) / (1 + MaxPortValue);

                    if (masterAccount && portDD == 0
                        && transactionType == "TRADE")
                    {
                        // BitcoinInvestment((TotalPortfolioValue - _prevTotalPortfolioValue) * 0.4m, "New Account High BTC Investment");
                        SavingsAccountUSDTHoldings = GetBinanceSavingsAccountUSDTHoldings();
                        SavingsAccountNonUSDTValue = GetBinanceTotalSavingsAccountNonUSDValue();
                        SwapAccountUSDHoldings = GetBinanceSwapAccountUSDHoldings();
                    }

                    var line = ($"{Time.ToStringInvariant("yyyy-MM-dd HH:mm:ss")},") +
                                ($"{Math.Round(TotalPortfolioValue - _prevOffExchangeValue, 2)},{Math.Round(TotalPortfolioValue, 2)},{transactionType},{percentGain},{PortfolioProfit},{portDD},{_prevOffExchangeValue}");
                    writer.WriteLine(line);
                    _prevTotalPortfolioValue = TotalPortfolioValue;
                }
            }
        }

        public void LoadPortfolioValues()
        {
            if (!LiveMode)
                return;

            string csvFileName = $"../../../Data/TotalPortfolioValues/{currentAccount}/{currentAccount}_TotalPortfolioValues.csv";

            if (!File.Exists(csvFileName))
            {
                SaveTotalAccountValue();
            }
            string[] lines = File.ReadAllLines(csvFileName);

            var columnQuery =
                from line in lines.Skip(1)
                let elements = line.Split(',')
                select new StoredPortfolioValue(Convert.ToDateTime(elements[0], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[1], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[2], CultureInfo.GetCultureInfo("en-US")),
                elements[3],
                Convert.ToDecimal(elements[4], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[5], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[6], CultureInfo.GetCultureInfo("en-US")));

            // Execute the query and cache the results to improve  
            // performance. This is helpful only with very large files.  
            var results = columnQuery.ToList();

            MaxPortValue = results.Where(x => x.TotalPortfolioProfit < 100).Select(x => x.TotalPortfolioProfit).Max();
            PortfolioProfit = results.Select(x => x.TransactionProfit + 1).Aggregate((a, x) => a * x) - 1;
            StartingPortValue = results[0].TotalPortfolioValue;
            if (results.Any(x => x.TransactionType == "EOD"))
                HighWaterMark = results.Where(x => x.TransactionType == "EOD").Select(x => x.TotalPortfolioProfit).Max();
            else
            {
                SaveTotalAccountValue("EOD");
                HighWaterMark = PortfolioProfit;
            }
            var res = results.Last();
            _prevTotalPortfolioValue = res.TotalPortfolioValue;
            _prevOffExchangeValue = res.OffExchangeValue;
        }

        public void RecalculatePortfolioValues()
        {
            if (!LiveMode)
                return;

            string csvFileName = $"../../../Data/TotalPortfolioValues/{currentAccount}/{currentAccount}_TotalPortfolioValues.csv";

            if (!File.Exists(csvFileName))
            {
                SaveTotalAccountValue();
            }
            string[] lines = File.ReadAllLines(csvFileName);

            var columnQuery =
                from line in lines.Skip(1)
                let elements = line.Split(',')
                select new StoredPortfolioValue(Convert.ToDateTime(elements[0], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[1], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[2], CultureInfo.GetCultureInfo("en-US")),
                elements[3],
                Convert.ToDecimal(elements[4], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[5], CultureInfo.GetCultureInfo("en-US")),
                Convert.ToDecimal(elements[6], CultureInfo.GetCultureInfo("en-US")));

            // Execute the query and cache the results to improve  
            // performance. This is helpful only with very large files.  
            var results = columnQuery.ToList();

            using (var writer = new StreamWriter(csvFileName))
            {
                var header = ($"Time,binanceBalance,totalAccountsBalance,TransactionType,tradeProfitPercent,totalPortfolioProfit,portfolioDrawdown,offExchangeValue");
                writer.WriteLine(header);
                for (var i = 0; i < results.Count; i++)
                {
                    if (i != 0)
                    {

                        results[i].TransactionProfit = results[i].TransactionType == "TRADE" ? (results[i].TotalPortfolioValue - results[i - 1].TotalPortfolioValue) / results[i - 1].TotalPortfolioValue : 0;
                        results[i].TotalPortfolioProfit = results[i].TransactionType == "TRADE" ? (1 + results[i].TransactionProfit) * (1 + results[i - 1].TotalPortfolioProfit) - 1 : results[i - 1].TotalPortfolioProfit;
                        results[i].TransactionDrawdown = results[i].TransactionType == "TRADE" ? Math.Min(0, ((1 + results[i].TransactionProfit) * (1 + results[i - 1].TransactionDrawdown)) - 1) : results[i - 1].TransactionDrawdown;
                    }
                    var line = ($"{results[i].Date.ToStringInvariant("yyyy-MM-dd HH:mm:ss")},") +
                                ($"{results[i].OnExchangeValue},{results[i].TotalPortfolioValue},{results[i].TransactionType},{results[i].TransactionProfit},{results[i].TotalPortfolioProfit},{results[i].TransactionDrawdown},{results[i].OffExchangeValue}");
                    writer.WriteLine(line);
                }
            }
        }

        public class StoredPortfolioValue
        {
            public StoredPortfolioValue(DateTime date, decimal onValue, decimal totalValue, string ttype, decimal transactionProfit, decimal totalProfit, decimal drawdown)
            {
                Date = date;
                OnExchangeValue = onValue;
                TotalPortfolioValue = totalValue;
                TransactionType = ttype;
                TransactionProfit = transactionProfit;
                TotalPortfolioProfit = totalProfit;
                TransactionDrawdown = drawdown;
            }
            public DateTime Date { get; set; }
            public decimal OnExchangeValue { get; set; }
            public decimal OffExchangeValue => TotalPortfolioValue - OnExchangeValue;
            public decimal TotalPortfolioValue { get; set; }
            public string TransactionType { get; set; }
            public decimal TransactionProfit { get; set; }
            public decimal TotalPortfolioProfit { get; set; }
            public decimal TransactionDrawdown { get; set; }
        }

        /// <summary>
        /// Save expectancy Data
        /// </summary>
        /// <param name="transactions">Transactions list via an OrderProvider</param>
        /// <param name="csvFileName">File path to create</param>
        private void SaveExpectancyData(ExpectancyData add = null)
        {
            if (!LiveMode)
                return;

            string csvFileName = $"../../../Data/ExpectancyData.csv";

            var path = Path.GetDirectoryName(csvFileName);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);

            if (add == null)
            {
                using (var writer = new StreamWriter(csvFileName))
                {
                    var header = ($"Symbol,VariableNum,Expectancy,LastAuditTime");
                    writer.WriteLine(header);
                    foreach (var sym in SymbolInfo)
                    {
                        var num = 0;
                        foreach (var value in sym.Value.HypotheticalTrades)
                        {
                            var symbol = sym.Key;
                            var line = ($"{symbol},") +
                                        ($"{num},{value},{DateTime.UtcNow}");
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
                        var header = ($"Symbol,VariableNum,Expectancy,LastAuditTime");
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
            if (!LiveMode)
                return;

            string csvFileName = $"../../../Data/ExpectancyData.csv";

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
            if (Startup)
            {
                Debug("Algorithm.Initialize(): Attempting to add " + tenTrades.Count() + " Symbols to the Algoithim");
                foreach (var line in columnQuery)
                {
                    if (tenTrades.Contains(line.Symbol))
                    {
                        if (!SymbolInfo.ContainsKey(line.Symbol))
                            InitializeSymbolInfo(line.Symbol);

                        SymbolInfo[line.Symbol].HypotheticalTrades.Add(line.Expectancy);
                        _lastSym = line.Symbol;
                    }
                }
                symbols = tenTrades;
            }
            else
            {
                foreach (var info in SymbolInfo)
                {
                    info.Value.HypotheticalTrades = new RollingWindow<decimal>(100);
                }
                foreach (var line in columnQuery)
                {
                    if (SymbolInfo.ContainsKey(line.Symbol) && tenTrades.Contains(line.Symbol))
                    {
                        SymbolInfo[line.Symbol].HypotheticalTrades.Add(line.Expectancy);
                        _lastSym = line.Symbol;
                    }
                }
            }
        }

        private class ExpectancyData
        {
            public ExpectancyData(string symbol, int tradeNum, decimal exp, DateTime? _time)
            {
                Symbol = symbol;
                TradeNum = tradeNum;
                Expectancy = exp;
                LastAuditTime = _time;
            }
            public string Symbol { get; set; }
            public int TradeNum { get; set; }
            public decimal Expectancy { get; set; }
            public DateTime? LastAuditTime { get; set; }
        }

        public void ExpectancyEmail()
        {
            if (!LiveMode)
                return;

            var expectancyMessage = $"Market State = {forest.GetBitcoinMarketState()} - Expectancy Order";
            int expnum = 0;
            foreach (var sym in symbols.Where(x => SymbolInfo[x].Expectancy > -1))
            {
                expnum++;
                expectancyMessage += $" : #{expnum} = {sym}, Expectancy = {(int)(100 * SymbolInfo[sym].Expectancy)}";
            }
            Notify.Email("christopherjholley23@gmail.com", "Expectancy Update", expectancyMessage);
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
            if (!LiveMode)
                Config.Set("transaction-log", "../../../Data/Backtests/DonchianBacktest" + "_" + risk + "_" + UpMoveReduction + "_" + DrawdownReduction + "_" + forestPredictions +
                    "_" + forestAdjustment + "_" + stopMult + "_" + drawDownRiskReduction + "_" + maxPortExposure + "_" + decreasingRisk + "_" + decreasingType + "_" + drawDownRiskReduction +
                    "_" + reduceMaxPositions + ".csv");

            initialRisk = risk;
            portMax = TotalPortfolioValue;
        }

        public void ResetConfigVariables()
        {
            if (!LiveMode)
                return;

            var _offExchangeValue = Config.GetValue(currentAccount + "-IBAccountBalance", _prevOffExchangeValue);
            if (_offExchangeValue == -23)
            {
                _offExchangeValue = GetSheetsOffExchangeValues();
            }
            if (_offExchangeValue != _prevOffExchangeValue && Portfolio.TotalPortfolioValue != (decimal)startingequity)
            {
                var direction = _offExchangeValue > _prevOffExchangeValue ? "DEPOSIT" : "WITHDRAW";
                _prevOffExchangeValue = _offExchangeValue;
                SaveTotalAccountValue(direction);
            }
            profitPercent = Config.GetValue("profitPercent", profitPercent);
            MarketStateAudit = Config.GetValue("MarketStateAudit", MarketStateAudit);
            risk = Config.GetValue("risk", risk);
            UpMoveReduction = Config.GetValue("UpMoveReduction", UpMoveReduction);
            DrawdownReduction = Config.GetValue("DrawdownReduction", DrawdownReduction);
            stopMult = Config.GetValue("stopMult", stopMult);
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
            if (!LiveMode)
                Config.Set("transaction-log", "../../../Data/Backtests/DonchianBacktest_" + medianExpectancy + "_" + tradeLimit + "_" + adjustSizeTradeLimit + "_" + volTrigger + "_" + adjustSizeVolTrigger + "_" + risk +
                    "_" + adjustSizeDollarVolume + "_" + hardLossLimit + "_" + oppoTrades + "_" + adjustVWAP + "_" + adjustVWAPType + "_" + hardLossPercent + "_" + oppoSizeAdjust + ".csv");


            initialRisk = risk;
            portMax = TotalPortfolioValue;
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
        private decimal TotalPortfolioValue => Math.Max(1, Portfolio.TotalPortfolioValue + SavingsAccountUSDTHoldings + SavingsAccountNonUSDTValue + SwapAccountUSDHoldings + _prevOffExchangeValue);
        public class SymbolData
        {
            public SymbolData(string symbol)
            {
                Symbol = symbol;
            }
            public string Symbol { get; set; }
            public bool StartUp { get; set; } = true;
            public int Leverage { get; set; } = 20;
            public decimal? BuyStop { get; set; }
            public decimal? SellStop { get; set; }
            public decimal? StopInTicks { get; set; }
            public decimal BuyQuantity { get; set; } = 0;
            public decimal SellQuantity { get; set; } = 0;
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
            public LogReturn LogReturn { get; set; }

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
    static class ExtendMethod
    {
        public static decimal Median(this IEnumerable<decimal> source)
        {
            var sortedList = from number in source
                             orderby number
                             select number;

            int count = sortedList.Count();
            int itemIndex = count / 2;
            if (count % 2 == 0) // Even number of items. 
                return (sortedList.ElementAt(itemIndex) +
                        sortedList.ElementAt(itemIndex - 1)) / 2;

            // Odd number of items. 
            return sortedList.ElementAt(itemIndex);
        }
    }
}
