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
using Python.Runtime;

namespace QuantConnect.Algorithm.CSharp
{
	/// <summary>
	/// Basic template algorithm simply initializes the date range and cash. This is a skeleton
	/// framework you can use for designing an algorithm.
	/// </summary>
	public class DonchianLiveAlgorithmV3 : QCAlgorithm
	{
		private List<SymbolDirection> symbols = new List<SymbolDirection>();

		private Dictionary<string, SymbolData> SymbolInfo = new Dictionary<string, SymbolData>();
		private Dictionary<string, decimal> _Close = new Dictionary<string, decimal>();
		private AverageTrueRange atr;
		private DonchianChannel donchian;
		private VolumeWeightedAveragePriceIndicator vwap_current;

		public Resolution resolution = Resolution.Hour;

		decimal risk = .2m;
		double startingequity = 35000;
		bool closingHour = false, SymbolStateStartup = true;
		decimal portposition = 0;
		decimal PortValue = 0;
		decimal Holdings = 0;
		decimal maxLongDirection = 6;
		decimal maxShortDirection = 6;

		private static decimal UpMoveReduction = .04m; //must be > 0
		private static bool pythonPredictions = false;
		private static bool reduceMaxPositions = true;

		/// <summary>
		/// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
		/// </summary>
		public override void Initialize()
		{
			SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage);
			SetStartDate(2019, 01, 01);
			//SetStartDate(2020, 03, 01);

			SetCash(startingequity);
			SetWarmUp(2500);
			SetBenchmark("SPY");
			var spy = AddEquity("SPY", Resolution.Minute).Symbol;

			symbols.Add(new SymbolDirection("JNJ", Direction.Long));
			symbols.Add(new SymbolDirection("UNP", Direction.Long));
			symbols.Add(new SymbolDirection("MA", Direction.Long));
			symbols.Add(new SymbolDirection("AAPL", Direction.Long));
			symbols.Add(new SymbolDirection("AMD", Direction.Long));
			symbols.Add(new SymbolDirection("NOW", Direction.Long));
			symbols.Add(new SymbolDirection("TMO", Direction.Long));
			symbols.Add(new SymbolDirection("AMZN", Direction.Long));
			symbols.Add(new SymbolDirection("KL", Direction.Long));
			symbols.Add(new SymbolDirection("HD", Direction.Long));
			symbols.Add(new SymbolDirection("DIS", Direction.Long));
			symbols.Add(new SymbolDirection("COST", Direction.Long));
			symbols.Add(new SymbolDirection("NKE", Direction.Long));
			symbols.Add(new SymbolDirection("PAYC", Direction.Long));
			symbols.Add(new SymbolDirection("FSLY", Direction.Long));
			symbols.Add(new SymbolDirection("SSNC", Direction.Long));
			symbols.Add(new SymbolDirection("STNE", Direction.Long));
			symbols.Add(new SymbolDirection("SPLK", Direction.Long)); // both?
			symbols.Add(new SymbolDirection("CDLX", Direction.Long));
			symbols.Add(new SymbolDirection("SHOP", Direction.Long));
			symbols.Add(new SymbolDirection("TWTR", Direction.Long));
			symbols.Add(new SymbolDirection("GOOG", Direction.Long));
			symbols.Add(new SymbolDirection("CGC", Direction.Long));
			symbols.Add(new SymbolDirection("LULU", Direction.Long));
			symbols.Add(new SymbolDirection("LK", Direction.Long));
			symbols.Add(new SymbolDirection("BYND", Direction.Long));
			symbols.Add(new SymbolDirection("AUPH", Direction.Long));
			symbols.Add(new SymbolDirection("TQQQ", Direction.Long));
			symbols.Add(new SymbolDirection("SPXL", Direction.Long));
			symbols.Add(new SymbolDirection("IWM", Direction.Long)); // both
			symbols.Add(new SymbolDirection("XLY", Direction.Long));
			symbols.Add(new SymbolDirection("SHAK", Direction.Long));
			symbols.Add(new SymbolDirection("JNUG", Direction.Long));
			symbols.Add(new SymbolDirection("SILJ", Direction.Long));
			symbols.Add(new SymbolDirection("PALL", Direction.Long));
			symbols.Add(new SymbolDirection("VUG", Direction.Long));
			symbols.Add(new SymbolDirection("VTV", Direction.Long));

			symbols.Add(new SymbolDirection("PTON", Direction.Long));//both?
			symbols.Add(new SymbolDirection("TLRY", Direction.Long));//BOTH
			symbols.Add(new SymbolDirection("RAD", Direction.Long));//BOTH
			symbols.Add(new SymbolDirection("LDOS", Direction.Both));
			//symbols.Add(new SymbolDirection("CRON", Direction.Long));//BOTH

			symbols.Add(new SymbolDirection("GS", Direction.Both));
			symbols.Add(new SymbolDirection("PG", Direction.Both));
			symbols.Add(new SymbolDirection("BA", Direction.Both));
			symbols.Add(new SymbolDirection("TSLA", Direction.Both));
			symbols.Add(new SymbolDirection("MCK", Direction.Both));
			symbols.Add(new SymbolDirection("NVDA", Direction.Both));
			symbols.Add(new SymbolDirection("BRK.B", Direction.Both));
			symbols.Add(new SymbolDirection("ROKU", Direction.Both));
			symbols.Add(new SymbolDirection("CRWD", Direction.Both));
			symbols.Add(new SymbolDirection("MAR", Direction.Both));//2/23
			symbols.Add(new SymbolDirection("EOG", Direction.Both));//2/23
			symbols.Add(new SymbolDirection("MAXR", Direction.Both));
			symbols.Add(new SymbolDirection("BABA", Direction.Both));
			symbols.Add(new SymbolDirection("FDX", Direction.Both));
			symbols.Add(new SymbolDirection("BHC", Direction.Both));
			symbols.Add(new SymbolDirection("MJ", Direction.Both));
			symbols.Add(new SymbolDirection("XLI", Direction.Both));
			symbols.Add(new SymbolDirection("BWA", Direction.Both));
			symbols.Add(new SymbolDirection("BPY", Direction.Both));
			symbols.Add(new SymbolDirection("SPCE", Direction.Long));//Both
			symbols.Add(new SymbolDirection("INMD", Direction.Long));//Both
			symbols.Add(new SymbolDirection("MNK", Direction.Both));
			symbols.Add(new SymbolDirection("NIO", Direction.Both));
			symbols.Add(new SymbolDirection("BMO", Direction.Both));
			symbols.Add(new SymbolDirection("ABBV", Direction.Both));
			symbols.Add(new SymbolDirection("CCL", Direction.Both));
			symbols.Add(new SymbolDirection("WYNN", Direction.Both));
			symbols.Add(new SymbolDirection("WGO", Direction.Both));
			symbols.Add(new SymbolDirection("DELL", Direction.Both));
			symbols.Add(new SymbolDirection("GDXJ", Direction.Both));
			symbols.Add(new SymbolDirection("PENN", Direction.Both));
			symbols.Add(new SymbolDirection("BNS", Direction.Both));


			symbols.Add(new SymbolDirection("PFE", Direction.Short));
			symbols.Add(new SymbolDirection("SPG", Direction.Short));
			symbols.Add(new SymbolDirection("MGA", Direction.Short));
			symbols.Add(new SymbolDirection("WORK", Direction.Short));
			symbols.Add(new SymbolDirection("HYG", Direction.Short));
			symbols.Add(new SymbolDirection("SIX", Direction.Short));//both?
			symbols.Add(new SymbolDirection("ENDP", Direction.Short));
			symbols.Add(new SymbolDirection("WFC", Direction.Short));
			symbols.Add(new SymbolDirection("EWC", Direction.Short));
			symbols.Add(new SymbolDirection("SYY", Direction.Short));//both?
			symbols.Add(new SymbolDirection("PRGO", Direction.Short));
			symbols.Add(new SymbolDirection("PRU", Direction.Short));// BOTH
			symbols.Add(new SymbolDirection("MU", Direction.Short));
			symbols.Add(new SymbolDirection("WB", Direction.Short));
			symbols.Add(new SymbolDirection("JNK", Direction.Short));//BOTH
			symbols.Add(new SymbolDirection("PCG", Direction.Short));
			symbols.Add(new SymbolDirection("CXO", Direction.Short));

			// Initail Symbol Data for Initializing Symbol List
			PortValue = Portfolio.TotalPortfolioValue;

			foreach (var i in symbols)
			{
				string index = i.Symbol;
				if (index == "JNJ")
					Log("Attempting to add " + symbols.Count() + " Symbols to the Algoithim");
				SymbolInfo[index] = new SymbolData(index);
				// Add Asset Classes to QuantBook
				AddEquity(index, Resolution.Minute);
				//Securities[index].SetLeverage(10);

				_Close[index] = 0;
				// Establish Symbol Data for Index
				if (SymbolInfo.ContainsKey(index))
				{
					SymbolInfo[index].ATR = ATR(index, 30, MovingAverageType.Simple, resolution);
					SymbolInfo[index].VWAP_week = VWAP(SymbolInfo[index].Symbol);
					SymbolInfo[index].BuyQuantity = 0;
					SymbolInfo[index].SellQuantity = 0;
					SymbolInfo[index].Donchian = DCH(SymbolInfo[index].Symbol, 15, 15, resolution);

					var history = History(index, TimeSpan.FromDays(10), Resolution.Hour);
					foreach (TradeBar bar in history)
					{
						SymbolInfo[index].VWAP_week.Update(bar);
						SymbolInfo[index].ATR.Update(bar);
						SymbolInfo[index].Donchian.Update(bar);
					}
				}
			}
			Log("All Symbols added to the algorithm");

			Schedule.On(DateRules.EveryDay("SPY"), TimeRules.AfterMarketOpen("SPY"), () =>
			{
				closingHour = false;
			});
			Schedule.On(DateRules.EveryDay("SPY"), TimeRules.BeforeMarketClose("SPY", 25), () =>
			{
				closingHour = true;
			});
			/*Train(DateRules.EveryDay("SPY"), TimeRules.At(5,0), () =>
        	{
        		if (pythonPredictions)
	        	{
		        	var bars = History<TradeBar>(Securities.Keys, 100, Resolution.Daily);
		        	Log("Updating Market State");
		        	UpdateMarketState(spy, bars);
		        	Debug("Current State of the market = " + GetSymbolState(spy));
		        	
		        	if (GetSymbolState(spy) == 1)
		        	{
				        maxLongDirection = 1;
				        maxShortDirection = 6;
		        	}
		        	else if (GetSymbolState(spy) == 0)
		        	{
				        maxLongDirection = 6;
				        maxShortDirection = 1;
		        	}
		        	else
		        	{
				        maxLongDirection = 6;
				        maxShortDirection = 6;
		        	}
	        	}
        	});
        	Train(DateRules.MonthStart("SPY"), TimeRules.At(19,0), () =>
        	{
	        	if (pythonPredictions)
	        	{
		        	Log("Refitting Models");
		        	var bars = History<TradeBar>(Securities.Keys, 900, Resolution.Daily);
		        	RefitModels(spy, bars);
	        	}
        	});*/
		}

		/// <summary>
		/// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
		/// </summary>
		/// <param name="data">Slice object keyed by symbol containing the stock data</param>
		public void OnData(Slice data)
		{
			var pctChange = (-PortValue + Portfolio.TotalPortfolioValue) / PortValue;
			var portfolioOverloaded = LiveMode ? ((Holdings < Portfolio.TotalPortfolioValue * maxShortDirection * -1 || Holdings > Portfolio.TotalPortfolioValue * maxLongDirection) ? true : false) : false;
			if (IsWarmingUp)
				return;
			foreach (var index in symbols)
			{
				string i = index.Symbol;
				if (Portfolio[i].Invested)
					Holdings += Portfolio[i].Quantity * Portfolio[i].AveragePrice;
			}

			foreach (var index in symbols)
			{
				string i = index.Symbol;
				try
				{
					if (data.ContainsKey(SymbolInfo[i].Symbol))
					{
						if (i == "JNJ")
							Log("Processing next time loop");
						if (!SymbolInfo[i].ATR.IsReady)
							Log("STILL HAVING ISSUES");
						SymbolInfo[i].OpenLongEntry = SymbolInfo[i].Donchian.UpperBand.Current.Value;
						SymbolInfo[i].OpenShortEntry = SymbolInfo[i].Donchian.LowerBand.Current.Value;
						SymbolInfo[i].StopInTicks = SymbolInfo[i].ATR.Current.Value;

						if (data[i].Close != null)
						{
							_Close[i] = data[i].Close;
							if (data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30) { }
							else if (!Portfolio[i].Invested || _Close[i] < 2.5m)
								continue;
						}
						else
						{
							continue;
						}
						if (i == "JNJ")
							Log("Processing next trade time loop");

						if (Portfolio[i].Quantity > 0 && SymbolInfo[i].BuyStop == null)
						{
							SymbolInfo[i].BuyStop = Portfolio[i].AveragePrice - 3 * SymbolInfo[i].ATR.Current.Value;
						}
						else if (Portfolio[i].Quantity < 0 && SymbolInfo[i].SellStop == null)
						{
							SymbolInfo[i].SellStop = Portfolio[i].AveragePrice + 3 * SymbolInfo[i].ATR.Current.Value;
						}
						else if (Portfolio[i].Quantity == 0)
						{
							SymbolInfo[i].BuyQuantity = 0;
							SymbolInfo[i].BuyStop = null;
						}

						if (pctChange < -.04m)
						{
							if (UpMoveReduction < 0) { }
							else if (Math.Abs(pctChange) > UpMoveReduction && Portfolio[i].Invested)
							{
								if (Portfolio[i].Profit < Portfolio.TotalPortfolioValue / 100 * risk)
								{
									Log("Reduce Portfolio Profit Exposure: reducing small winning position");
									MarketOrder(i, -Portfolio[i].Quantity, tag: "PORTFOLIO BREAKER: Closing Losing and Small Winning Positions");
								}
							}
						}
						if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
						{
							if ((data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
								&& data[i].Close > SymbolInfo[i].Donchian.UpperBand.Current.Value && data[i].Close > SymbolInfo[i].VWAP_week.Current.Value
								&& Portfolio[i].Quantity == 0
								&& Holdings < Portfolio.TotalPortfolioValue * maxLongDirection)
							{
								Log("Buys signal generated on " + i);
								SymbolInfo[i].BuyQuantity = Convert.ToInt32((risk / 100) * Portfolio.TotalPortfolioValue / SymbolInfo[i].ATR.Current.Value);
								SymbolInfo[i].BuyStop = data[i].Close - 3 * SymbolInfo[i].ATR.Current.Value;
								if (SymbolInfo[i].BuyQuantity != 0)
								{
									var buy = Portfolio.MarginRemaining * .9m;
									int qty = SymbolInfo[i].BuyQuantity * _Close[i] > buy * .99m ? (int)(buy * .99m / _Close[i]) : (int)SymbolInfo[i].BuyQuantity;

									if (qty > 0 && !closingHour)
									{
										MarketOrder(i, qty, tag: "Buy Signal Generated");
										continue;
									}
								}

							}
							else if (((data[i].Close < SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
								|| data[i].Close < SymbolInfo[i].BuyStop) && Portfolio[i].Quantity > 0)
							{
								Log("Close buy order signal generated on " + i);
								if (!closingHour)
								{
									Liquidate(i, tag: "Liquidating Long Position, Stop Triggered");
									continue;
								}
								else
								{
									int qty = (int)Portfolio[i].Quantity * -1;
									var price = Math.Round(_Close[i], 2) - .05m;
									LimitOrder(i, qty, price, tag: "Closing Long Position w/ EOD Limit Order");
									continue;
								}
							}
						}




						if (index.TradeDirection == Direction.Short || index.TradeDirection == Direction.Both)
						{
							if ((data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
								&& data[i].Close < SymbolInfo[i].Donchian.LowerBand.Current.Value && data[i].Close < SymbolInfo[i].VWAP_week.Current.Value
								&& Portfolio[i].Quantity == 0
								&& Holdings > Portfolio.TotalPortfolioValue * maxShortDirection * -1)
							{
								Log("Sell signal generated on " + i);
								SymbolInfo[i].SellQuantity = -1 * Convert.ToInt32((risk / 100) * Portfolio.TotalPortfolioValue / SymbolInfo[i].ATR.Current.Value);
								SymbolInfo[i].SellStop = data[i].Close + 3 * SymbolInfo[i].ATR.Current.Value;
								if (SymbolInfo[i].SellQuantity != 0)
								{
									var sell = Portfolio.MarginRemaining * .9m;
									int qty = SymbolInfo[i].SellQuantity * _Close[i] * -1 > sell * .99m ? (int)(sell * -0.99m / _Close[i]) : (int)(SymbolInfo[i].SellQuantity);

									if (qty < 0 && !closingHour)
									{
										MarketOrder(i, qty, tag: "Sell Signal Generated");
										continue;
									}
								}
							}
							else if (((data[i].Close > SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
								|| data[i].Close > SymbolInfo[i].SellStop) && Portfolio[i].Quantity < 0)
							{
								Log("Close sell order signal generated on " + i);
								if (!closingHour)
								{
									Liquidate(i, tag: "Liquidating Short Position, Stop Triggered");
									continue;
								}
								else
								{
									int qty = (int)Portfolio[i].Quantity * -1;
									var price = Math.Round(_Close[i], 2) + .05m;
									LimitOrder(i, qty, price, tag: "Closing Short Position w/ EOD Limit Order");
									continue;
								}
							}
						}
					}
				}
				catch (Exception e)
				{
					Debug("Error Processing index indicator allocations - " + e.Message);

				}
			}

			foreach (var index in symbols)
			{
				try
				{
					string i = index.Symbol;
					if (!SymbolInfo[i].ATR.IsReady)
						continue;
					if (!SymbolInfo[i].Donchian.IsReady)
						continue;
					if (!SymbolInfo[i].VWAP_week.IsReady)
						continue;
					if (!data.ContainsKey(i))
						continue;
					if (!Portfolio[i].Invested)
						continue;

					if (data[i].EndTime.DayOfWeek == DayOfWeek.Friday && closingHour && data[i].EndTime.Minute >= 50)
					{
						if (Math.Abs(Holdings) > 1.5m * Portfolio.TotalPortfolioValue)
						{
							if (Portfolio[i].Profit < Portfolio.TotalPortfolioValue / 100 * risk)
							{
								Log("Reduce Weekend Exposure");
								MarketOrder(i, -Portfolio[i].Quantity / 2, tag: "Reducing Weekend Exposure");
							}
						}
					}
					var day = data[i].EndTime;
					if (closingHour && data[i].EndTime.Minute >= 50)
					{
						if (UpMoveReduction < 0) { }
						else if (pctChange > UpMoveReduction && Portfolio[i].Invested)
						{
							if (Holdings > 1.5m * Portfolio.TotalPortfolioValue ||
								Holdings < .5m * Portfolio.TotalPortfolioValue)
							{
								if (Portfolio[i].Profit < 0)
								{
									Log("Reduce Portfolio Profit Exposure: cutting losing position");
									MarketOrder(i, -Portfolio[i].Quantity, tag: "Preserving Proft: Cutting Losing Position");
								}
								else if (Portfolio[i].Profit < Portfolio.TotalPortfolioValue / 100 * risk)
								{
									Log("Reduce Portfolio Profit Exposure: reducing small winning position");
									MarketOrder(i, -Portfolio[i].Quantity / 2, tag: "Preserving Proft: Reducing Small Winning Position");
								}
							}
						}
					}
					if (closingHour && data[i].EndTime.Minute == 0)
						PortValue = Portfolio.TotalPortfolioValue;
				}
				catch (Exception e) { Debug("Error Processing Portfolio Management - " + e.Message); }
			}
			Holdings = 0;
		}



		public override void OnOrderEvent(OrderEvent orderEvent)
		{
			var order = Transactions.GetOrderById(orderEvent.OrderId);
			if (orderEvent.Status == OrderStatus.Filled)
			{

			}
			if (LiveMode)
			{
				if (orderEvent.Status == OrderStatus.Invalid)
				{
					Log($"Invalid order on Security {order.Symbol}: Reason - {orderEvent.Message}, Invalid ID {orderEvent.OrderId}");
					if (orderEvent.OrderId == -5)
					{

					}
				}
			}
			else
			{
				if (orderEvent.Status == OrderStatus.Invalid)
				{
					Log($"Invalid order on Security {order.Symbol}: Reason - {orderEvent.Message}, Invalid ID {orderEvent.OrderId}");
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
	}
}