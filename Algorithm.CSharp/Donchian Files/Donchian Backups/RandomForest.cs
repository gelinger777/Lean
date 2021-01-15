using Python.Runtime;
using Newtonsoft.Json;
using System.Collections.Generic;
using System;
using QuantConnect.Algorithm;
using System.Linq;
using QuantConnect.Python;
using QuantConnect.Data;
using Microsoft.VisualBasic.FileIO;
using System.Globalization;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.IO;
using System.Threading;
using QuantConnect.Configuration;

namespace QuantConnect.PythonPredictions
{
	// Python Methods class that allows for calls of the HMM code. The original code can be found here:
	// https://www.quantconnect.com/forum/discussion/6878/from-research-to-production-hidden-markov-models/p1
	public class RandomForest
	{
		public RandomForest(bool _marketStateAudit = false)
		{
			if (!_marketStateAudit)
			{
				string envPythonHome = @"C:\Python36";
				string envPythonLib = envPythonHome + @"\Lib\site-packages";
				PythonInitializer.Initialize();
				PythonEngine.PythonHome = envPythonHome + @"\python36.dll";
				PythonEngine.PythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
				Environment.SetEnvironmentVariable("PYTHONHOME", envPythonHome, EnvironmentVariableTarget.Process);
				Environment.SetEnvironmentVariable("PATH", envPythonHome + ";" + Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
					EnvironmentVariableTarget.Process);

				Environment.SetEnvironmentVariable("PYTHONPATH", envPythonLib, EnvironmentVariableTarget.Process);
			}
		}

		// Dictionary containing the current state of the symbol. Will result in a 1 or 0 value
		private Dictionary<Symbol, double> _symbolState = new Dictionary<Symbol, double>();
		private PyObject _model;

		private List<BitcoinTreesData> _storedData;
		private double _bitcoinMarketState;

		static string[] Scopes = { SheetsService.Scope.Spreadsheets };
		static string ApplicationName = "Google Sheets API .NET Quickstart";

		public class BitcoinTreesData : BaseData
		{
			public decimal dpr, txv, dtv, mtv, /*dmv, xtv, dxt,*/ t1v, d1v, gen, dgn, fee, dfe, req, txs, hgt, dif, inp, /*o,
				open, high, low,*/ close, MA, VWAP, Basis, Upper, Lower, Volume, Volume_MA, ATR;

			public override List<Resolution> SupportedResolutions()
			{
				return DailyResolution;
			}

			public override SubscriptionDataSource GetSource(
				SubscriptionDataConfig config,
				DateTime date,
				bool isLive)
			{
				var source = "../../../Data/bitcoin_daily_fundamental.csv";

				return new SubscriptionDataSource(source,
					SubscriptionTransportMedium.LocalFile);
			}

			public override BaseData Reader(
				SubscriptionDataConfig config,
				string line,
				DateTime date,
				bool isLive)
			{
				if (string.IsNullOrWhiteSpace(line) ||
					char.IsLetter(line[0]))
					return null;

				var data = line.Split(',');

				if (isLive)
				{

					try
					{
#pragma warning disable CA1305 // Specify IFormatProvider
						var dpr = Convert.ToDecimal(data[0]);
						var txv = Convert.ToDecimal(data[1]);
						var dtv = Convert.ToDecimal(data[2]);
						var mtv = Convert.ToDecimal(data[3]);
						//var dmv = Convert.ToDecimal(data[4]);
						//var xtv = Convert.ToDecimal(data[5]);
						//var dxt = Convert.ToDecimal(data[6]);
						var t1v = Convert.ToDecimal(data[7]);
						var d1v = Convert.ToDecimal(data[8]);
						var gen = Convert.ToDecimal(data[9]);
						var dgn = Convert.ToDecimal(data[10]);
						var fee = Convert.ToDecimal(data[11]);
						var dfe = Convert.ToDecimal(data[12]);
						var req = Convert.ToDecimal(data[13]);
						var txs = Convert.ToDecimal(data[14]);
						var hgt = Convert.ToDecimal(data[15]);
						var dif = Decimal.Parse((string)data[16], NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint);
						var inp = Convert.ToDecimal(data[17]);
						var o = Convert.ToDecimal(data[18]);
						//var open = Convert.ToDecimal(data[32]);
						//var high = Convert.ToDecimal(data[33]);
						//var low = Convert.ToDecimal(data[34]);
						var close = Convert.ToDecimal(data[45]);
						var MA = Convert.ToDecimal(data[46]);
						var VWAP = Convert.ToDecimal(data[47]);
						var Basis = Convert.ToDecimal(data[48]);
						var Upper = Convert.ToDecimal(data[49]);
						var Lower = Convert.ToDecimal(data[50]);
						var Volume = Convert.ToDecimal(data[51]);
						var Volume_MA = Convert.ToDecimal(data[52]);
						var ATR = Convert.ToDecimal(data[53]);

						var tree = new BitcoinTreesData()
						{
							// Make sure we only get this data AFTER trading day - don't want forward bias.
							Time = Convert.ToDateTime(data[29]),

							dpr = dpr,
							txv = txv,
							dtv = dtv,
							mtv = mtv,
							//dmv = dmv,
							//xtv = xtv,
							//dxt = dxt,
							t1v = t1v,
							d1v = d1v,
							gen = gen,
							dgn = dgn,
							fee = fee,
							dfe = dfe,
							req = req,
							txs = txs,
							hgt = hgt,
							dif = dif,
							inp = inp,
							//o = o,

							//open = open,
							//high = high,
							//low = low,
							close = close,
							MA = MA,
							VWAP = VWAP,
							Basis = Basis,
							Upper = Upper,
							Lower = Lower,
							Volume = Volume,
							Volume_MA = Volume_MA,
							ATR = ATR
						};

#pragma warning restore CA1305 // Specify IFormatProvider
						return tree;
					}
					catch (Exception e)
					{
						return new BitcoinTreesData();
					}
				}
				else
				{
					return new BitcoinTreesData()
					{
						// Make sure we only get this data AFTER trading day - don't want forward bias.
						Time = DateTime.ParseExact(data[30], "yyyy-MM-dd", null),
						Symbol = config.Symbol,

#pragma warning disable CA1305 // Specify IFormatProvider
						dpr = Convert.ToDecimal(data[0]),
						txv = Convert.ToDecimal(data[1]),
						dtv = Convert.ToDecimal(data[2]),
						mtv = Convert.ToDecimal(data[3]),
						//dmv = Convert.ToDecimal(data[4]),
						//xtv = Convert.ToDecimal(data[5]),
						//dxt = Convert.ToDecimal(data[6]),
						t1v = Convert.ToDecimal(data[7]),
						d1v = Convert.ToDecimal(data[8]),
						gen = Convert.ToDecimal(data[9]),
						dgn = Convert.ToDecimal(data[10]),
						fee = Convert.ToDecimal(data[11]),
						dfe = Convert.ToDecimal(data[12]),
						req = Convert.ToDecimal(data[13]),
						txs = Convert.ToDecimal(data[14]),
						hgt = Convert.ToDecimal(data[15]),
						dif = Convert.ToDecimal(data[16]),
						inp = Convert.ToDecimal(data[18]),
						//o = Convert.ToDecimal(data[18]),

						//open = Convert.ToDecimal(data[32]),
						//high = Convert.ToDecimal(data[33]),
						//low = Convert.ToDecimal(data[34]),
						close = Convert.ToDecimal(data[35]),
						MA = Convert.ToDecimal(data[36]),
						VWAP = Convert.ToDecimal(data[37]),
						Basis = Convert.ToDecimal(data[38]),
						Upper = Convert.ToDecimal(data[39]),
						Lower = Convert.ToDecimal(data[40]),
						Volume = Convert.ToDecimal(data[41]),
						Volume_MA = Convert.ToDecimal(data[42]),
						ATR = Convert.ToDecimal(data[43])

#pragma warning restore CA1305 // Specify IFormatProvider
					};
				}
			}
		}

		public void PredictBitcoinReturns(QCAlgorithm algorithm, DateTime startTime)
		{
			/*if (_storedData == null)
			{
				if (liveMode)
					GetLiveBitcoinHistory();
				else
					GetBacktestBitcoinHistory(startTime);
			}*/
			if (_storedData.Count() == 0 || _storedData == null)
				return;
			using (Py.GIL())
			{
				dynamic PredictModel = PythonEngine.ModuleFromString("PredictModelModule",
					@"
from clr import AddReference
AddReference(""QuantConnect.Common"")
from QuantConnect import *
from QuantConnect.Data.Custom import *
import numpy as np
import pandas as pd
from pandas_summary import DataFrameSummary
from fastai.imports import *
from pandas_datareader import data
from sklearn.ensemble import RandomForestRegressor, RandomForestClassifier
from IPython.display import display
from statsmodels.tsa.stattools import adfuller
from sklearn import metrics
from fastai.tabular import *

def clean_dataset(df):
	assert isinstance(df, pd.DataFrame), ""df needs to be a pd.DataFrame""

	df.dropna(inplace = True)

	indices_to_keep = ~df.isin([np.nan, np.inf, -np.inf]).any(1)

	return df[indices_to_keep].astype(np.float64)

def split_vals(a,n): return a[:n].copy(), a[n:].copy()

def PredictModel(history, model):
	df_raw = history[history['dpr'] !=0]
	df_raw['one_day_returns'] = np.log(df_raw.close.shift(-1)) - np.log(df_raw.close)
	df_raw['two_day_returns'] = np.log(df_raw.close.shift(-2)) - np.log(df_raw.close)

	num = df_raw['two_day_returns']._get_numeric_data()
	num[num <= 0] = 0
	num[num > 0] = 1

	num = df_raw['one_day_returns']._get_numeric_data()
	num[num <= 0] = 0
	num[num > 0] = 1


	df_raw = df_raw.drop('dpr', axis=1)

	for column in df_raw:
		if not np.issubdtype(df_raw[column].dtype, np.number):
			continue
        
		if df_raw[column].isnull().values.any():
			continue
        
		if column == 'Elapsed' or column == 'Year':
			continue
		dftest = adfuller(df_raw[column])
		if dftest[1] > 0.05:
			df_raw[column] = df_raw[column].pct_change()


	ypred = df_raw.tail(1).drop(['one_day_returns', 'two_day_returns'], axis=1)
	
	return model.predict(ypred)
").GetAttr("PredictModel");

				// Call the Refit HMM model script we created above and update the dictionary
				var data = algorithm.PandasConverter.GetDataFrame(_storedData.Where(tree => tree.Time.Date <= startTime.Date).ToList());
				_bitcoinMarketState = PredictModel(data, _model);
				PredictModel.Dispose();
			}

		}

		public void RefitModel(QCAlgorithm algorithm, DateTime startTime)
		{
			if (_storedData.Count() == 0 || _storedData == null)
				return;
			using (Py.GIL())
			{
				dynamic RefitModel = PythonEngine.ModuleFromString("RefitModelModule",
					@"
from clr import AddReference
AddReference(""QuantConnect.Common"")
from QuantConnect import *
from QuantConnect.Data.Custom import *
import numpy as np
import pandas as pd
from pandas_summary import DataFrameSummary
from fastai.imports import *
from pandas_datareader import data
from sklearn.ensemble import RandomForestRegressor, RandomForestClassifier
from IPython.display import display
from statsmodels.tsa.stattools import adfuller
from sklearn import metrics
from fastai.tabular import *

def clean_dataset(df):
	assert isinstance(df, pd.DataFrame), ""df needs to be a pd.DataFrame""

	df.dropna(inplace = True)

	indices_to_keep = ~df.isin([np.nan, np.inf, -np.inf]).any(1)

	return df[indices_to_keep].astype(np.float64)

def split_vals(a,n): return a[:n].copy(), a[n:].copy()

def RefitModel(history):
	df_raw = history[history['dpr'] !=0]
	df_raw['one_day_returns'] = np.log(df_raw.close.shift(-1)) - np.log(df_raw.close)
	df_raw['two_day_returns'] = np.log(df_raw.close.shift(-2)) - np.log(df_raw.close)

	num = df_raw['two_day_returns']._get_numeric_data()
	num[num <= 0] = 0
	num[num > 0] = 1

	num = df_raw['one_day_returns']._get_numeric_data()
	num[num <= 0] = 0
	num[num > 0] = 1

	df_raw = df_raw[:len(df_raw) - 2].drop('dpr', axis=1)

	for column in df_raw:
		if not np.issubdtype(df_raw[column].dtype, np.number):
			continue
        
		if df_raw[column].isnull().values.any():
			continue
        
		if column == 'Elapsed' or column == 'Year':
			continue
		dftest = adfuller(df_raw[column])
		if dftest[1] > 0.05:
			df_raw[column] = df_raw[column].pct_change()


	df_raw = clean_dataset(df_raw)
	df = df_raw.drop(['two_day_returns', 'one_day_returns'], axis=1)
	y = df_raw['two_day_returns']

	m = RandomForestClassifier(n_estimators=500, n_jobs=-1)
	m.fit(df, y)
	
	return m
").GetAttr("RefitModel");

				// Call the Refit HMM model script we created above and update the dictionary
				var data = algorithm.PandasConverter.GetDataFrame(_storedData.Where(tree => tree.Time.Date <= startTime.Date).ToList());
				_model = RefitModel(data);
				RefitModel.Dispose();
			}
		}

		public void RefitOneDayModel(QCAlgorithm algorithm, DateTime startTime)
		{
			if (_storedData.Count() == 0 || _storedData == null)
				return;
			using (Py.GIL())
			{
				dynamic RefitModel = PythonEngine.ModuleFromString("RefitModelModule",
					@"
from clr import AddReference
AddReference(""QuantConnect.Common"")
from QuantConnect import *
from QuantConnect.Data.Custom import *
import numpy as np
import pandas as pd
from pandas_summary import DataFrameSummary
from fastai.imports import *
from pandas_datareader import data
from sklearn.ensemble import RandomForestRegressor, RandomForestClassifier
from IPython.display import display
from statsmodels.tsa.stattools import adfuller
from sklearn import metrics
from fastai.tabular import *

def clean_dataset(df):
	assert isinstance(df, pd.DataFrame), ""df needs to be a pd.DataFrame""

	df.dropna(inplace = True)

	indices_to_keep = ~df.isin([np.nan, np.inf, -np.inf]).any(1)

	return df[indices_to_keep].astype(np.float64)

def split_vals(a,n): return a[:n].copy(), a[n:].copy()

def RefitModel(history):
	df_raw = history[history['dpr'] !=0]
	df_raw['one_day_returns'] = np.log(df_raw.close.shift(-1)) - np.log(df_raw.close)
	df_raw['two_day_returns'] = np.log(df_raw.close.shift(-2)) - np.log(df_raw.close)

	num = df_raw['two_day_returns']._get_numeric_data()
	num[num <= 0] = 0
	num[num > 0] = 1

	num = df_raw['one_day_returns']._get_numeric_data()
	num[num <= 0] = 0
	num[num > 0] = 1

	df_raw = df_raw[:len(df_raw) - 2].drop('dpr', axis=1)

	for column in df_raw:
		if not np.issubdtype(df_raw[column].dtype, np.number):
			continue
        
		if df_raw[column].isnull().values.any():
			continue
        
		if column == 'Elapsed' or column == 'Year':
			continue
		dftest = adfuller(df_raw[column])
		if dftest[1] > 0.05:
			df_raw[column] = df_raw[column].pct_change()


	df_raw = clean_dataset(df_raw)
	df = df_raw.drop(['two_day_returns', 'one_day_returns'], axis=1)
	y = df_raw['one_day_returns']

	m = RandomForestClassifier(n_estimators=500, n_jobs=-1)
	m.fit(df, y)
	
	return m
").GetAttr("RefitModel");

				// Call the Refit HMM model script we created above and update the dictionary
				var data = algorithm.PandasConverter.GetDataFrame(_storedData.Where(tree => tree.Time.Date <= startTime.Date).ToList());
				_model = RefitModel(data);
				RefitModel.Dispose();
			}
		}

		public bool GetLiveBitcoinHistory(QCAlgorithm algorithm)
		{
			var success = true;
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
			String marketStatespreadsheetId = "1coT7M0vCTp6vJDeaapqdt2gFRnCn6Udn1gWbwQ7PdCk";
			String dataspreadsheetId = "1d53Gwj4OioFbj9RYWrenZHF4DaUDpgz6BepAhXOLrLk";

			// Check if marketstate exits
			String marketStaterange = "MarketState";
			SpreadsheetsResource.ValuesResource.GetRequest MSrequest =
					service.Spreadsheets.Values.Get(marketStatespreadsheetId, marketStaterange);

			ValueRange MSresponse = MSrequest.Execute();
			IList<IList<Object>> MSvalues = MSresponse.Values;
			if (MSvalues != null && MSvalues.Count > 0)
			{
				try
				{
					IEnumerable<SavedMarketStateData> data = from line in MSvalues.Skip(1)
						   select new SavedMarketStateData(Convert.ToDateTime(line[0], CultureInfo.GetCultureInfo("en-US")), line[1].ToString(), Convert.ToDouble(line[2], CultureInfo.GetCultureInfo("en-US")));

					_marketStates = data.ToList();
					var marketState = data.ToList().Last();
					if (marketState?.Date.Date == DateTime.UtcNow.AddDays(-1).Date)
					{
						_bitcoinMarketState = marketState.MarketState;
						success = false;
					}
				}
				catch
				{ }
			}

			// If not, then run
			String range = "Sheet1";
			SpreadsheetsResource.ValuesResource.GetRequest request =
					service.Spreadsheets.Values.Get(dataspreadsheetId, range);

			ValueRange response = request.Execute();
			IList<IList<Object>> values = response.Values;
			var _outData = new List<BitcoinTreesData>();
			if (values != null && values.Count > 0)
			{
				try
				{
					_outData = (from data in values.Skip(1)
								where data[47] != ""
								select new BitcoinTreesData()
								{
#pragma warning disable CA1305 // Specify IFormatProvider
									// Make sure we only get this data AFTER trading day - don't want forward bias.
									Time = (DateTime)data[35],
									Symbol = algorithm.Securities["BTCUSDT"].Symbol,

									dpr = Convert.ToDecimal(data[0]),
									txv = Convert.ToDecimal(data[1]),
									dtv = Convert.ToDecimal(data[2]),
									mtv = Convert.ToDecimal(data[3]),
									//var dmv = Convert.ToDecimal(data[4]);
									//var xtv = Convert.ToDecimal(data[5]);
									//var dxt = Convert.ToDecimal(data[6]);
									t1v = Convert.ToDecimal(data[7]),
									d1v = Convert.ToDecimal(data[8]),
									gen = Convert.ToDecimal(data[9]),
									dgn = Convert.ToDecimal(data[10]),
									fee = Convert.ToDecimal(data[11]),
									dfe = Convert.ToDecimal(data[12]),
									req = Convert.ToDecimal(data[13]),
									txs = Convert.ToDecimal(data[14]),
									hgt = Convert.ToDecimal(data[15]),
									dif = Decimal.Parse((string)data[16], NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint),
									inp = Convert.ToDecimal(data[17]),
									//o = Convert.ToDecimal(data[18]),
									//var open = Convert.ToDecimal(data[32]);
									//var high = Convert.ToDecimal(data[33]);
									//var low = Convert.ToDecimal(data[34]);
									close = Convert.ToDecimal(data[45]),
									MA = Convert.ToDecimal(data[46]),
									VWAP = Convert.ToDecimal(data[47]),
									Basis = Convert.ToDecimal(data[48]),
									Upper = Convert.ToDecimal(data[49]),
									Lower = Convert.ToDecimal(data[50]),
									Volume = Convert.ToDecimal(data[51]),
									Volume_MA = Convert.ToDecimal(data[52]),
									ATR = Convert.ToDecimal(data[53])

#pragma warning restore CA1305 // Specify IFormatProvider
								}).ToList();
				}
				catch (Exception e)
				{ 

				}
			}
			else
			{
				Console.WriteLine("No data found.");
			}
			_storedData = _outData;
			return success;
		}

		public void GetBacktestBitcoinHistory(QCAlgorithm algorithm)
		{
			_storedData = new List<BitcoinTreesData>();
			using (TextFieldParser parser = new TextFieldParser(@"../../../Data/bitcoin_daily_fundamental.csv"))
			{
				parser.TextFieldType = FieldType.Delimited;
				parser.SetDelimiters(",");
				int start = 0;
				var _outData = new List<BitcoinTreesData>();
				while (!parser.EndOfData)
				{
					string[] data = parser.ReadFields();
					//Process row
					if (start == 0)
						start++;
					else
					{
						start++;
						try
						{
#pragma warning disable CA1305 // Specify IFormatProvider
							var dpr = Convert.ToDecimal(data[0]);
							var txv = Convert.ToDecimal(data[1]);
							var dtv = Convert.ToDecimal(data[2]);
							var mtv = Convert.ToDecimal(data[3]);
							//var dmv = Convert.ToDecimal(data[4]);
							//var xtv = Convert.ToDecimal(data[5]);
							//var dxt = Convert.ToDecimal(data[6]);
							var t1v = Convert.ToDecimal(data[7]);
							var d1v = Convert.ToDecimal(data[8]);
							var gen = Convert.ToDecimal(data[9]);
							var dgn = Convert.ToDecimal(data[10]);
							var fee = Convert.ToDecimal(data[11]);
							var dfe = Convert.ToDecimal(data[12]);
							var req = Convert.ToDecimal(data[13]);
							var txs = Convert.ToDecimal(data[14]);
							var hgt = Convert.ToDecimal(data[15]);
							var dif = Decimal.Parse((string)data[16], NumberStyles.AllowExponent | NumberStyles.AllowDecimalPoint);
							var inp = Convert.ToDecimal(data[17]);
							var o = Convert.ToDecimal(data[18]);
							//var open = Convert.ToDecimal(data[32]);
							//var high = Convert.ToDecimal(data[33]);
							//var low = Convert.ToDecimal(data[34]);
							var close = Convert.ToDecimal(data[45]);
							var MA = Convert.ToDecimal(data[46]);
							var VWAP = Convert.ToDecimal(data[47]);
							var Basis = Convert.ToDecimal(data[48]);
							var Upper = Convert.ToDecimal(data[49]);
							var Lower = Convert.ToDecimal(data[50]);
							var Volume = Convert.ToDecimal(data[51]);
							var Volume_MA = Convert.ToDecimal(data[52]);
							var ATR = Convert.ToDecimal(data[53]);

#pragma warning restore CA1305 // Specify IFormatProvider
							var tree = new BitcoinTreesData()
							{
								// Make sure we only get this data AFTER trading day - don't want forward bias.
								Time = DateTime.ParseExact(data[30], "yyyy-MM-dd", null),
								Symbol = algorithm.Securities["BTCUSDT"].Symbol,

								dpr = dpr,
								txv = txv,
								dtv = dtv,
								mtv = mtv,
								//dmv = dmv,
								//xtv = xtv,
								//dxt = dxt,
								t1v = t1v,
								d1v = d1v,
								gen = gen,
								dgn = dgn,
								fee = fee,
								dfe = dfe,
								req = req,
								txs = txs,
								hgt = hgt,
								dif = dif,
								inp = inp,
								//o = o,

								//open = open,
								//high = high,
								//low = low,
								close = close,
								MA = MA,
								VWAP = VWAP,
								Basis = Basis,
								Upper = Upper,
								Lower = Lower,
								Volume = Volume,
								Volume_MA = Volume_MA,
								ATR = ATR
							};
							_outData.Add(tree);
						}
						catch (Exception e)
						{

						}
					}
				}
				_storedData = _outData;
			}
		}

		public MarketState GetBitcoinMarketState()
		{
			if (_bitcoinMarketState == 1)
				return MarketState.Bull;
			else if (_bitcoinMarketState == 0)
				return MarketState.Bear;
			else
				return MarketState.Unknown;
		}

		public void SetBitcoinMarketState(int state)
		{
			_bitcoinMarketState = state;
		}

		List<SavedMarketStateData> _marketStates;
		public bool LoadMarketState(QCAlgorithm algorithm, DateTime checkTime = new DateTime())
		{
			string csvFileName = Config.GetValue("data-folder", "../../../Data/") + "MarketStateData.csv";

			if (!File.Exists(csvFileName))
			{
				SaveMarketState(algorithm.Time);
			}

			string[] lines = File.ReadAllLines(csvFileName);

			if (_marketStates == null || algorithm.LiveMode)
			{
				var columnQuery =
					from line in lines.Skip(1)
					let elements = line.Split(',')
					select new SavedMarketStateData(Convert.ToDateTime(elements[0], CultureInfo.GetCultureInfo("en-US")), elements[1], Convert.ToDouble(elements[2], CultureInfo.GetCultureInfo("en-US")));
				
				_marketStates = columnQuery.ToList();
			}

			var _lastSym = "BTC";

			//_bitcoinMarketState = columnQuery.ToList().Where(line => line.Date <= algorithm.Time && line.Symbol == _lastSym).LastOrDefault().MarketState;
			if (checkTime == new DateTime())
				_bitcoinMarketState = _marketStates.Where(line => line.Symbol == _lastSym).LastOrDefault().MarketState;

			else if (_marketStates.Where(line => line.Symbol == _lastSym && line.Date <= checkTime).Any())
				_bitcoinMarketState = _marketStates.Where(line => line.Symbol == _lastSym && line.Date <= checkTime).LastOrDefault().MarketState;

			else if (algorithm.LiveMode && _marketStates.Where(line => line.Symbol == _lastSym).Any())
				_bitcoinMarketState = _marketStates.Where(line => line.Symbol == _lastSym).LastOrDefault().MarketState;
			else
				_bitcoinMarketState = -1;

			return true;
		}
		public bool LoadMarketStateFromSheets(QCAlgorithm algorithm, DateTime checkTime = new DateTime())
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

			// Check if marketstate exits
			String marketStaterange = "MarketState";
			SpreadsheetsResource.ValuesResource.GetRequest MSrequest =
					service.Spreadsheets.Values.Get(spreadsheetId, marketStaterange);

			ValueRange MSresponse = MSrequest.Execute();
			IList<IList<Object>> MSvalues = MSresponse.Values;
			if (MSvalues != null && MSvalues.Count > 0)
			{
				try
				{
					IEnumerable<SavedMarketStateData> data = from line in MSvalues.Skip(1)
															 select new SavedMarketStateData(Convert.ToDateTime(line[0], CultureInfo.GetCultureInfo("en-US")), line[1].ToString(), Convert.ToDouble(line[2], CultureInfo.GetCultureInfo("en-US")));

					_marketStates = data.ToList();
				}
				catch
				{ }
			}

			var _lastSym = "BTC";

			//_bitcoinMarketState = columnQuery.ToList().Where(line => line.Date <= algorithm.Time && line.Symbol == _lastSym).LastOrDefault().MarketState;
			if (checkTime == new DateTime())
				_bitcoinMarketState = _marketStates.Where(line => line.Symbol == _lastSym).LastOrDefault().MarketState;

			else if (_marketStates.Where(line => line.Symbol == _lastSym && line.Date <= checkTime).Any())
				_bitcoinMarketState = _marketStates.Where(line => line.Symbol == _lastSym && line.Date <= checkTime).LastOrDefault().MarketState;

			else if (algorithm.LiveMode && _marketStates.Where(line => line.Symbol == _lastSym).Any())
				_bitcoinMarketState = _marketStates.Where(line => line.Symbol == _lastSym).LastOrDefault().MarketState;
			else
				_bitcoinMarketState = -1;

			return true;
		}
		public bool SaveMarketState(DateTime time, List<SavedMarketStateData> marketDataList = null)
		{
			string csvFileName = Config.GetValue("data-folder", "../../../Data/") + "MarketStateData.csv";

			var path = Path.GetDirectoryName(csvFileName);
			if (path != null && !Directory.Exists(path))
			{
				Directory.CreateDirectory(path);

				using (var writer = new StreamWriter(csvFileName))
				{
					var header = ($"Date,Symbol,MarketState");
					writer.WriteLine(header);

					if (marketDataList == null)
					{
						var line = ($"{time},") +
										($"BTC,{_bitcoinMarketState}");
						writer.WriteLine(line);
					}
					else
					{
						foreach (var data in marketDataList)
						{
							var line = ($"{data.Date},") +
											($"{data.Symbol},{data.MarketState}");
							writer.WriteLine(line);
						}
					}
				}
				return true;
			}
			else
			{
				if (!File.Exists(csvFileName))
				{
					using (var writer = new StreamWriter(csvFileName))
					{
						var header = ($"Symbol,VariableNum,Expectancy");
						writer.WriteLine(header);
						if (marketDataList == null)
						{
							var line = ($"{time},") +
										($"BTC,{_bitcoinMarketState}");
							writer.WriteLine(line);
						}
						else
						{
							foreach (var data in marketDataList)
							{
								var line = ($"{data.Date},") +
												($"{data.Symbol},{data.MarketState}");
								writer.WriteLine(line);
							}
						}
					}
				}
				else
				{
					using (var writer = new StreamWriter(csvFileName, append: true))
					{

						if (marketDataList == null)
						{
							var line = ($"{time},") +
										($"BTC,{_bitcoinMarketState}");
							writer.WriteLine(line);
						}
						else
						{
							foreach (var data in marketDataList)
							{
								var line = ($"{data.Date},") +
												($"{data.Symbol},{data.MarketState}");
								writer.WriteLine(line);
							}
						}
					}
				}
				return true;
			}
		}
		//Google Example
		public bool SaveMarketStateToSheets(DateTime time)
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

			// Check if marketstate exits
			String marketStaterange = "MarketState";

			var spreadsheet = service.Spreadsheets.Get(spreadsheetId).Execute();
			var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == marketStaterange);
			int sheetId = (int)sheet.Properties.SheetId;

			var requests = new BatchUpdateSpreadsheetRequest { Requests = new List<Request>() };

			GridCoordinate gc = new GridCoordinate
			{
				ColumnIndex = 0,
				RowIndex = _marketStates.Count + 1,
				SheetId = sheetId
			};

			var request = new Request { UpdateCells = new UpdateCellsRequest { Start = gc, Fields = "*" } };

			var listRowData = new List<RowData>();
			var rowData = new RowData();
			var listCellData = new List<CellData>();

			//time
			var cellData = new CellData();
			var extendedValue = new ExtendedValue { StringValue = time.ToString(CultureInfo.GetCultureInfo("en-US")) };
			cellData.UserEnteredValue = extendedValue;
			listCellData.Add(cellData);

			//BTC
			var btccellData = new CellData();
			var btcextendedValue = new ExtendedValue { StringValue = "BTC"};
			btccellData.UserEnteredValue = btcextendedValue;
			listCellData.Add(btccellData);

			//Value
			var statecellData = new CellData();
			var stateextendedValue = new ExtendedValue { NumberValue = _bitcoinMarketState };
			statecellData.UserEnteredValue = stateextendedValue;
			listCellData.Add(statecellData);

			rowData.Values = listCellData;

			listRowData.Add(rowData);
			request.UpdateCells.Rows = listRowData;

			// It's a batch request so you can create more than one request and send them all in one batch. Just use reqs.Requests.Add() to add additional requests for the same spreadsheet
			requests.Requests.Add(request);

			service.Spreadsheets.BatchUpdate(requests, spreadsheetId).Execute();
			return true;
		}
		public class SavedMarketStateData
		{
			public SavedMarketStateData(DateTime _date, string _symbol, double _state)
			{
				Date = _date;
				Symbol = _symbol;
				MarketState = _state;
			}
			public DateTime Date;
			public string Symbol;
			public double MarketState;
		}
		public enum MarketState
		{
			Bear = 0,
			Bull = 1,
			Unknown = -1
		}

		//Error trying to save the PyObject (HMMGaussian), is there another way to do this?
		//Current Error: TypeError : 'GaussianHMM' object is not iterable TypeError : 'GaussianHMM' object is not iterable
		[Obsolete]
		public void SaveModels(QCAlgorithm algorithm)
		{//TODO add in code to manage the stored keys and values
			/*algorithm.Log("PythonMethods.SaveModels(): Attempting to save models to Data Storage");
			var keys = _models.Keys.ToArray();
			var values = _models.Values.ToArray();

			algorithm.ObjectStore.SaveJson(PythonModelKeys, keys);
			algorithm.ObjectStore.SaveJson(PythonModelValues, values);
			algorithm.Log("PythonMethods.SaveModels(): Save to Data Storage Complete");*/
		}

	}
}