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
using System.IO;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Sheets.v4;
using Google.Apis.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using System.Threading;
using System.Globalization;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public partial class DonchianCryptoExpectancyAudit : QCAlgorithm
    {
        private List<SymbolDirection> symbols = new List<SymbolDirection>();

        private Dictionary<string, SymbolData> SymbolInfo = new Dictionary<string, SymbolData>();

        public Resolution resolution = Resolution.Hour;

        decimal stopMult = 2m;
        int atrPeriod = 30, dchPeriod = 30;
        int percent = 0;

        bool volOnVWAP = true;
        string volOnVWAPType = "Straight"; // Straight, StopMult, Sqrt
        string currentAccount = "chrisholley23";

        double startingequity = 45000;
        bool AAVETransition = true;

        //Notifications

        Symbol btc;
        DateTime StartingDate;

        static string[] Scopes = { SheetsService.Scope.Spreadsheets };
        static string ApplicationName = "Donchian Expectancy Audit";
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            var auditComplete = CheckSheetsForAudit();
            if (auditComplete)
                throw new Exception("Initialize(): Audit loaded from sheets & complete, ending algorithm");

            Log("DonchianCryptoExpectancyAudit.Initialize(): Retreiving Tradeable Securities");
            SetBrokerageModel(BrokerageName.BinanceFutures);
            SetStartDate(DateTime.Now - TimeSpan.FromDays(360));

            SetAccountCurrency("USDT");
            SetCash(startingequity);
            SetWarmUp(7);

            AddSymbols();
            btc = AddEquity("BTCUSDT", Resolution.Minute, "BinanceFutures", leverage: 20).Symbol;
            SetBenchmark(btc);

            SetVariablesFromConfig();

            Portfolio.MarginCallModel = MarginCallModel.Null;

            Debug("Algorithm.Initialize(): Attempting to add " + symbols.Count() + " Symbols to the Algoithim");

            foreach (var i in symbols)
            {
                string index = i.Symbol;
                SymbolInfo[index] = new SymbolData(index);

                var sym = AddEquity(index, Resolution.Minute, "BinanceFutures", leverage: 20).Symbol;

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
                    SymbolInfo[index].OBV = OBV(sym, 15, resolution);

                    Debug("Attempting to add " + index + " to the Algoithim");
                }
            }

        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public void OnData(Slice data)
        {
            if (StartingDate == null)
                StartingDate = Time;
            else
            {
                var now = (Time - StartDate).Days / 3.6;
                if (now > percent + 10)
                {
                    percent = percent + 10;
                    Log($"Algorithm running: {percent}% complete");
                }
            }
            if (AAVETransition && data.ContainsKey("AAVEUSDT") && symbols.Where(x => x.Symbol == "LENDUSDT").Any() && SymbolInfo.ContainsKey("AAVEUSDT"))
            {
                var remove = symbols.Where(x => x.Symbol == "LENDUSDT").First();
                var trades = SymbolInfo["LENDUSDT"].HypotheticalTrades;
                SymbolInfo["AAVEUSDT"].HypotheticalTrades = trades;
                symbols.Remove(remove);
                AAVETransition = false;
            }
            foreach (var index in symbols)
            {
                string i = index.Symbol;

                if (data.ContainsKey(SymbolInfo[i].Symbol))
                {
                    bool tradetime = IndicatorChecks(data, i);
                    if (!tradetime)
                        continue;

                    SetHypotheticalEntries(data, index);
                }
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
                    SymbolInfo[i].BuyStop = Portfolio[i].AveragePrice - stopMult *  SymbolInfo[i].ATR.Current.Value;
                }
                else if (Portfolio[i].Quantity < 0 && SymbolInfo[i].SellStop == null && !IsWarmingUp)
                {
                    SymbolInfo[i].SellStop = Portfolio[i].AveragePrice + stopMult *  SymbolInfo[i].ATR.Current.Value;
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

        public void SetHypotheticalEntries(Slice data, SymbolDirection index)
        {
            try
            {
                string i = index.Symbol;
                if (((data[i].Close < SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                         || data[i].Close < SymbolInfo[i].testBuyStop) && SymbolInfo[i].testLongIsOpen)
                {
                    SymbolInfo[i].testBuyQuantity = 0;
                    var ret = SymbolInfo[i].testATR == null || SymbolInfo[i].testATR == 0 ? (data[i].Close - SymbolInfo[i].testLongEntryPrice) / (SymbolInfo[i].ATR.Current.Value) : (data[i].Close - SymbolInfo[i].testLongEntryPrice) / (SymbolInfo[i].testATR.Value);
                    SymbolInfo[i].testLongIsOpen = false;
                    SymbolInfo[i].testLongEntryPrice = 0;
                    SymbolInfo[i].testATR = null;
                    if (ret != 0)
                        SymbolInfo[i].HypotheticalTrades.Add(ret);

                    var Expectancy = SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Min(y => y.Expectancy) == 0 ? 0.5m : (SymbolInfo[i].Expectancy - SymbolInfo.Values.Min(y => y.Expectancy)) / (SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Min(y => y.Expectancy));
                    SymbolInfo[i].SizeAdjustment = 2 * Expectancy;
                }
                else if (((data[i].Close > SymbolInfo[i].VWAP_week.Current.Value && data[i].EndTime.Minute == 0)
                    || data[i].Close > SymbolInfo[i].testSellStop) && SymbolInfo[i].testShortIsOpen)
                {
                    SymbolInfo[i].testSellQuantity = 0;
                    var ret = SymbolInfo[i].testATR == null || SymbolInfo[i].testATR == 0 ? (SymbolInfo[i].testShortEntryPrice - data[i].Close) / (SymbolInfo[i].ATR.Current.Value) : (SymbolInfo[i].testShortEntryPrice - data[i].Close) / (SymbolInfo[i].testATR.Value);
                    SymbolInfo[i].testShortIsOpen = false;
                    SymbolInfo[i].testShortEntryPrice = 0;
                    SymbolInfo[i].testATR = null;
                    if (ret != 0)
                        SymbolInfo[i].HypotheticalTrades.Add(ret);
                    
                    var Expectancy = SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Min(y => y.Expectancy) == 0 ? 0.5m : (SymbolInfo[i].Expectancy - SymbolInfo.Values.Min(y => y.Expectancy)) / (SymbolInfo.Values.Max(y => y.Expectancy) - SymbolInfo.Values.Min(y => y.Expectancy));
                    SymbolInfo[i].SizeAdjustment = 2 * Expectancy;
                }


                if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
                {
                    if ((data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
                        && data[i].Close > SymbolInfo[i].OpenLongEntry && data[i].Close > SymbolInfo[i].VWAP_week.Current.Value
                        && !SymbolInfo[i].testLongIsOpen && !SymbolInfo[i].testShortIsOpen
                        && SymbolInfo[i].StdevDifference <= 0)
                    {
                        SymbolInfo[i].testBuyStop = data[i].Close - stopMult * SymbolInfo[i].ATR.Current.Value;
                        SymbolInfo[i].testLongIsOpen = true;
                        SymbolInfo[i].testLongEntryPrice = data[i].Close;
                        SymbolInfo[i].testATR = SymbolInfo[i].ATR.Current;
                        return;
                    }
                    else if (SymbolInfo[i].testLongIsOpen && SymbolInfo[i].BuyStop != null && volOnVWAP)
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
                }

                if (index.TradeDirection == Direction.Short || index.TradeDirection == Direction.Both)
                {
                    if ((data[i].EndTime.Minute == 0 || data[i].EndTime.Minute == 30)
                        && data[i].Close < SymbolInfo[i].OpenShortEntry && data[i].Close < SymbolInfo[i].VWAP_week.Current.Value
                        && !SymbolInfo[i].testShortIsOpen && !SymbolInfo[i].testLongIsOpen
                        && SymbolInfo[i].StdevDifference <= 0)
                    {
                        SymbolInfo[i].testSellStop = data[i].Close + stopMult * SymbolInfo[i].ATR.Current.Value;
                        SymbolInfo[i].testShortIsOpen = true;
                        SymbolInfo[i].testShortEntryPrice = data[i].Close;
                        SymbolInfo[i].testATR = SymbolInfo[i].ATR.Current;
                        return;
                    }
                    else if (SymbolInfo[i].testShortIsOpen && SymbolInfo[i].SellStop != null && volOnVWAP)
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
                }
            }
            catch (Exception e)
            {
                Debug("Error Processing SetHypotheticalEntries() - " + e.Message + " - " + e.StackTrace);
            }
        }

        public override void OnEndOfAlgorithm()
        {
            symbols = symbols.OrderByDescending(x => SymbolInfo[x.Symbol].Expectancy).ToList();

            var auditComplete = CheckSheetsForAudit();
            SaveExpectancyData();

            if (!auditComplete)
                SaveExpectancyDataToSheets();

        }
        #region Initialization Methods
        List<ExpectancyData> sheetsData;
        private bool CheckSheetsForAudit()
        {
            var auditComplete = false;

            UserCredential credential;

            using (var stream =
                new FileStream("../../credentials.json", FileMode.Open, FileAccess.Read))
            {
                // The file token.json stores the user's access and refresh tokens, and is created
                // automatically when the authorization flow completes for the first time.
                string credPath = "token.json";
                credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    Scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore(credPath, true)).Result;
                Console.WriteLine("Credential file saved to: " + credPath);
            }

            // Create Google Sheets API service.
            var service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            // Define request parameters.
            String spreadsheetId = "1coT7M0vCTp6vJDeaapqdt2gFRnCn6Udn1gWbwQ7PdCk";
            String range = "Expectancy Audit!A:D";
            SpreadsheetsResource.ValuesResource.GetRequest request =
                    service.Spreadsheets.Values.Get(spreadsheetId, range);

            ValueRange response = request.Execute();
            IList<IList<Object>> values = response.Values;
            if (values != null && values.Count > 1000)
            {
                if (Convert.ToDateTime(values[1][3], CultureInfo.GetCultureInfo("en-US")) > DateTime.UtcNow.AddMinutes(-90))
                {
                    var data = 
                    from line in values.Skip(1)
                    let elements = line
                    select new ExpectancyData(elements[0].ToString(), Convert.ToDecimal(elements[2].ToString(), CultureInfo.GetCultureInfo("en-US")), Convert.ToDateTime(elements[3].ToString(), CultureInfo.GetCultureInfo("en-US")), Convert.ToInt32(elements[1].ToString(), CultureInfo.GetCultureInfo("en-US")));
                    sheetsData = data.ToList();
                    auditComplete = true;
                }
            }

            return auditComplete;
        }
        
        /// <summary>
        /// Save expectancy Data
        /// </summary>
        /// <param name="transactions">Transactions list via an OrderProvider</param>
        /// <param name="csvFileName">File path to create</param>
        private void SaveExpectancyData()
        {
            string csvFileName = Config.GetValue("data-folder", "../../../Data/") + $"ExpectancyData.csv";

            var path = Path.GetDirectoryName(csvFileName);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);

            if (sheetsData == null)
            {
                using (var writer = new StreamWriter(csvFileName))
                {
                    var header = ($"Symbol,VariableNum,Expectancy,LastAuditTime");
                    writer.WriteLine(header);
                    foreach (var sym in symbols)
                    {
                        var num = 0;
                        foreach (var value in SymbolInfo[sym.Symbol].HypotheticalTrades)
                        {
                            if (sym.Symbol == "LENDUSDT")
                                continue;
                            var symbol = sym.Symbol;
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
                using (var writer = new StreamWriter(csvFileName))
                {
                    var header = ($"Symbol,VariableNum,Expectancy,LastAuditTime");
                    writer.WriteLine(header);
                    foreach (var sym in sheetsData)
                    {
                        var num = 0;
                        if (sym.Symbol == "LENDUSDT")
                            continue;
                        var symbol = sym.Symbol;
                        var line = ($"{symbol},") +
                                    ($"{sym.num},{sym.Expectancy},{sym.LastAuditDate}");
                        writer.WriteLine(line);
                        num++;
                    }
                }
            }
        }
        
        /// <summary>
        /// Save expectancy Data
        /// </summary>
        /// <param name="transactions">Transactions list via an OrderProvider</param>
        /// <param name="csvFileName">File path to create</param>
        private void SaveExpectancyDataToSheets(ExpectancyData add = null)
        {
            UserCredential credential;

            using (var stream =
                new FileStream("../../credentials.json", FileMode.Open, FileAccess.Read))
            {
                // The file token.json stores the user's access and refresh tokens, and is created
                // automatically when the authorization flow completes for the first time.
                string credPath = "token.json";
                credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    Scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore(credPath, true)).Result;
                Console.WriteLine("Credential file saved to: " + credPath);
            }

            // Create Google Sheets API service.
            var service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            // Define request parameters.
            String spreadsheetId = "1coT7M0vCTp6vJDeaapqdt2gFRnCn6Udn1gWbwQ7PdCk";
            String range = "Expectancy Audit";

            var spreadsheet = service.Spreadsheets.Get(spreadsheetId).Execute();
            var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == range);
            int sheetId = (int)sheet.Properties.SheetId;

            var requests = new BatchUpdateSpreadsheetRequest { Requests = new List<Request>() };

            if (add == null)
            {
                //TODO GetExpectancyDataFromSheets()
                GridCoordinate gc = new GridCoordinate
                {
                    ColumnIndex = 0,
                    RowIndex = 1,
                    SheetId = sheetId
                };

                var request = new Request { UpdateCells = new UpdateCellsRequest { Start = gc, Fields = "*" } };

                var listRowData = new List<RowData>();

                foreach (var sym in symbols)
                {
                    if (sym.Symbol == "LENDUSDT")
                        continue;

                    var num = 0;
                    foreach (var value in SymbolInfo[sym.Symbol].HypotheticalTrades)
                    {
                        var rowData = new RowData();
                        var listCellData = new List<CellData>();
                        //BTC
                        var btccellData = new CellData();
                        var btcextendedValue = new ExtendedValue { StringValue = sym.Symbol == "LENDUSDT" ? "AAVEUSDT" : sym.Symbol };
                        btccellData.UserEnteredValue = btcextendedValue;
                        listCellData.Add(btccellData);

                        //number
                        var statecellData = new CellData();
                        var stateextendedValue = new ExtendedValue { NumberValue = num };
                        statecellData.UserEnteredValue = stateextendedValue;
                        listCellData.Add(statecellData);

                        //value
                        var valuecellData = new CellData();
                        var valueextendedValue = new ExtendedValue { NumberValue = (double)value };
                        valuecellData.UserEnteredValue = valueextendedValue;
                        listCellData.Add(valuecellData);

                        //time
                        var cellData = new CellData();
                        var extendedValue = new ExtendedValue { StringValue = DateTime.UtcNow.ToString(CultureInfo.GetCultureInfo("en-US")) };
                        cellData.UserEnteredValue = extendedValue;
                        listCellData.Add(cellData);

                        rowData.Values = listCellData;
                        listRowData.Add(rowData);
                        num++;
                    }

                }
                request.UpdateCells.Rows = listRowData;

                requests.Requests.Add(request);

                service.Spreadsheets.BatchUpdate(requests, spreadsheetId).Execute();
            }
            else
            {

                //TODO GetExpectancyDataFromSheets()
                GridCoordinate gc = new GridCoordinate
                {
                    ColumnIndex = 0,
                    RowIndex = 1,//_marketStates.Count + 1,
                    SheetId = sheetId
                };

                var request = new Request { UpdateCells = new UpdateCellsRequest { Start = gc, Fields = "*" } };

                var symbol = add.Symbol == "LENDUSDT" ? "AAVEUSDT" : add.Symbol;
                var line = ($"{symbol},") +
                                    ($"{-1},{add.Expectancy}");
            }
        }

        private class ExpectancyData
        {
            public ExpectancyData(string symbol, decimal exp, DateTime? auditDate = null, int? _num = null)
            {
                Symbol = symbol;
                Expectancy = exp;
                LastAuditDate = auditDate;
                num = _num;
            }
            public string Symbol { get; set; }
            public decimal Expectancy { get; set; }
            public DateTime? LastAuditDate { get; set; }
            public int? num { get; set; }
        }

        public void AddSymbols()
        {
            /*
                Log("Limiting number of symbols for debug mode");
                symbols.Add(new SymbolDirection("ETHUSDT", Direction.Both));
                symbols.Add(new SymbolDirection("BNBUSDT", Direction.Both));
                symbols.Add(new SymbolDirection("BTCUSDT", Direction.Both));*/
            string dataFolder = Config.GetValue("data-folder", "../../../Data/") + $"equity/binancefutures/minute";
            var folders = Directory.EnumerateDirectories(dataFolder);
            foreach (var path in folders)
            {
                var s = path.Remove(0, (dataFolder.Count() + 1));
#pragma warning disable CA1304 // Specify CultureInfo
                symbols.Add(new SymbolDirection(s.ToUpper(), Direction.Both));
#pragma warning restore CA1304 // Specify CultureInfo
            }
        }
        public void SetVariablesFromConfig()
        {
            stopMult = Config.GetValue("stopMult", stopMult);
            atrPeriod = Config.GetValue("atrPeriod", atrPeriod);
            dchPeriod = Config.GetValue("dchPeriod", dchPeriod);
            volOnVWAP = Config.GetValue("volOnVWAP", volOnVWAP);
            volOnVWAPType = Config.GetValue("volOnVWAPType", volOnVWAPType);

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
            public bool BreakerTriggered { get; set; } = false;
            public OrderTicket openOrder { get; set; }

            public AverageTrueRange ATR { get; set; }
            public OnBalanceVolume OBV { get; set; }
            public DonchianChannel Donchian { get; set; }
            public IntraweekVwap VWAP_week { get; set; }
            public LinearWeightedMovingAverage TrendMA { get; set; }
            public LogReturn LogReturn { get; set; }

            public RollingWindow<IndicatorDataPoint> TrendMAWindow = new RollingWindow<IndicatorDataPoint>(5);
            public RateOfChangePercent RateOfChange { get; set; }
            public StandardDeviation LTStdev { get; set; }
            public StandardDeviation STStdev { get; set; }
            public decimal StdevDifference => LTStdev.IsReady && STStdev.IsReady ? STStdev.Current.Value - LTStdev.Current.Value : 0;
            public int ActiveDays { get; set; } = 0;
            public bool testLongIsOpen { get; set; }
            public bool testShortIsOpen { get; set; }
            public decimal testLongEntryPrice { get; set; }
            public decimal testShortEntryPrice { get; set; }
            public IndicatorDataPoint testATR { get; set; }
            public decimal? testBuyStop { get; set; }
            public decimal? testSellStop { get; set; }
            public decimal testBuyQuantity { get; set; } = 0;
            public decimal testSellQuantity { get; set; } = 0;
            public decimal SizeAdjustment { get; set; } = 1;

            public decimal Expectancy
            {
                get
                {
                    if (HypotheticalTrades.Count() < 10)
                        return -1;
                    else
                    {
                        var pos = HypotheticalTrades.Count(x => x > 0);
                        var total = HypotheticalTrades.Count;
                        var avgWin = HypotheticalTrades.Where(x => x > 0).Average();
                        var avgLoss = HypotheticalTrades.Where(x => x <= 0).Average();
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
}
