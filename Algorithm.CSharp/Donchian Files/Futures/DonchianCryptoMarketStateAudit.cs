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
using QuantConnect.Data.Consolidators;
using QuantConnect.Configuration;
using QuantConnect.PythonPredictions;
using System.IO;
using static QuantConnect.PythonPredictions.RandomForest;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public partial class DonchianCryptoMarketStateAudit : QCAlgorithm
    {
        private RandomForest forest = new RandomForest();
        bool MarketStateAudit = false;
        List<SavedMarketStateData> marketStates = new List<SavedMarketStateData>();

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetBrokerageModel(BrokerageName.BinanceFutures);
            if (LiveMode)
                throw new ArgumentException("Crypto Market State Audit Algorithm cannot be ran in LiveMode");

            SetAccountCurrency("USDT");
            MarketStateAudit = Config.GetValue("MarketStateAudit", MarketStateAudit);
            SetStartDate(DateTime.Now - TimeSpan.FromDays(5));
            SetEndDate(DateTime.Now);
            SetWarmUp(1);
            var btc = AddEquity("BTCUSDT", Resolution.Daily).Symbol;

            SetPandasConverter();

            if (MarketStateAudit)
            {
                SetStartDate(2018, 1, 1);

                forest.GetLiveBitcoinHistory(this);
                forest.RefitOneDayModel(this, StartDate);

                Train(DateRules.MonthEnd(btc), TimeRules.At(0, 1, NodaTime.DateTimeZone.Utc), () =>
                {
                    if (MarketStateAudit)
                        forest.RefitOneDayModel(this, Time);
                });

                Train(DateRules.EveryDay(btc), TimeRules.At(0, 5, NodaTime.DateTimeZone.Utc), () =>
                {
                    try
                    {
                        forest.PredictBitcoinReturns(this, Time);
                        marketStates.Add(new SavedMarketStateData(Time, "BTC", forest.GetBitcoinMarketState().GetHashCode()));
                        Log($"{Time} - Bitcoin Predicted Direction = {forest.GetBitcoinMarketState()}");
                    }
                    catch { }
                });

            }


        }

        public override void OnWarmupFinished()
        {
            if (!MarketStateAudit)
            {
                Log($"WarmUpFinished(): Downloading Data to predict Market State");
                var cont = forest.GetLiveBitcoinHistory(this); 
                if (!cont)
                {
                    forest.SaveMarketState(DateTime.UtcNow.AddDays(-1));
                    Log($"Initialize(): Exisitng Market state for {DateTime.UtcNow.Date.AddDays(-1)}, Bitcoin Predicted Direction = {forest.GetBitcoinMarketState()}");
                    return;
                }
                double finalState = 0;
                int j = 0;
                for (int i = 0; i < 3; i++)
                {
                    j++;
                    forest.RefitOneDayModel(this, DateTime.UtcNow);
                    forest.PredictBitcoinReturns(this, DateTime.UtcNow);
                    finalState += forest.GetBitcoinMarketState().GetHashCode();
                    if (j >= 3 && (finalState / j == 1 || finalState / j == 0))
                        break;
                }
                var state = finalState / j == 1 || finalState / j == 0 ? finalState / j : -1;
                forest.SetBitcoinMarketState((int)state);

                forest.SaveMarketState(DateTime.UtcNow - TimeSpan.FromDays(1));
                Log($"Initialize(): Bitcoin Predicted Direction = {forest.GetBitcoinMarketState()}");
            }
        }

        public override void OnEndOfAlgorithm()
        {
            if (MarketStateAudit)
                forest.SaveMarketState(Time,marketStates);
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public void OnData(Slice data)
        {
        }

    }
}
