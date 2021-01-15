using Python.Runtime;
//using QuantConnect.Storage;
using QuantConnect.Securities.Future;
using Newtonsoft.Json;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using QuantConnect.Data.Consolidators;
using System;
using QuantConnect.Algorithm;
using System.Linq;
using QuantConnect.Python;

namespace QuantConnect.PythonPredictions
{
	// Python Methods class that allows for calls of the HMM code. The original code can be found here:
	// https://www.quantconnect.com/forum/discussion/6878/from-research-to-production-hidden-markov-models/p1
	public class PythonMethods
	{
		public PythonMethods()
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
		private string PythonModelKeys = "pyModelKeys";
		private string PythonModelValues = "pyModelValues";

		// Dictionary containing the current state of the symbol. Will result in a 1 or 0 value
		private Dictionary<Symbol, double> _symbolState = new Dictionary<Symbol, double>();

		// A Dictionary of the models the HMM function creates. Used to create a prediction and will be
		// periodically updated
		private Dictionary<Symbol, PyObject> _models = null;

		private PyObject _pythonModels;
		private PyObject _pythonPredictions;

		private List<DataDictionary<TradeBar>> _futuresBars = new List<DataDictionary<TradeBar>>();
		private Symbol _frontContractUnderlying;
		private Symbol _frontContract;
		private IEnumerable<DataDictionary<TradeBar>> _storedBars;

		public void UpdateMarketState(QCAlgorithm algorithm, Symbol sym, bool UseStorage = false, Resolution resolution = Resolution.Daily)
		{
			List<Symbol> symList = new List<Symbol>();
			symList.Add(sym);
			UpdateMarketState(algorithm, symList, UseStorage, resolution);
		}

		// Update the market state of a selected symbol
		public void UpdateMarketState(QCAlgorithm algorithm, List<Symbol> sym = null, bool UseStorage = false, Resolution resolution = Resolution.Daily)
		{
			if (_models == null)
				RefitModels(algorithm, sym, UseStorage, resolution);

			IEnumerable<DataDictionary<TradeBar>> bars;
			if (_storedBars == null)
			{
				var symbols = algorithm.Securities.Keys.Where(x => x.SecurityType == SecurityType.Equity);
				bars = sym == null ? algorithm.History<TradeBar>(symbols, 50, resolution) : algorithm.History<TradeBar>(sym, 50, resolution);
			}
			else
			{
				bars = _storedBars;
			}
			var securities = sym == null ? algorithm.Securities.Keys.Where(x => x.SecurityType == SecurityType.Equity).ToList() : sym;
			InternalUpdateMarketState(algorithm, securities, UseStorage, bars, resolution);
		}

		public void UpdateMarketState(QCAlgorithm algorithm, Future sym, bool UseStorage = false, Resolution resolution = Resolution.Daily)
		{
			if (_models == null)
				RefitModels(algorithm, sym, UseStorage, resolution);

			var securities = sym.Symbol;
			var bars = FetchFuturesBars(algorithm, sym, resolution, 50);
			List<Symbol> symList = new List<Symbol>();
			symList.Add(securities);

			InternalUpdateMarketState(algorithm, symList, UseStorage, bars, resolution);
		}

		private void InternalUpdateMarketState(QCAlgorithm algorithm, List<Symbol> securities, bool UseStorage, IEnumerable<DataDictionary<TradeBar>> bars, Resolution resolution)
		{
			if (bars.Count() < 50)
				return;
			algorithm.Log($"PythonMethods.InternalUpdateMarketState(): Updating Market State Predicitions using {bars.Count()} bars");
			var marketstates = "";
			if (_models == null && UseStorage)
			{
				/*if (algorithm.ObjectStore.ContainsKey(PythonModelKeys) && algorithm.ObjectStore.ContainsKey(PythonModelValues))
				{
					// our object store has our historical data saved, read the data
					// and push it through the indicators to warm everything up
					var keys = algorithm.ObjectStore.ReadJson<Symbol[]>(PythonModelKeys);
					var values = algorithm.ObjectStore.ReadJson<PyObject[]>(PythonModelValues);
					algorithm.Log($"PythonMethods.InternalUpdateMarketState(): {PythonModelKeys} exists in object store. Count: {keys.Length} unique keys/n" +
									" & {PythonModelValues} exists in object store. Count: {values.Length} unique values");
				}
				else
				{
					algorithm.Log($"PythonMethods.InternalUpdateMarketState(): {PythonModelKeys} key does not exist in object store. Creating new models...");

					// if our object store doesn't have our data, fetch the history to initialize
					// we're pulling the last year's worth of SPY daily trade bars to fee into our indicators
					_models = new Dictionary<Symbol, PyObject>();
					RefitModels(algorithm, securities, UseStorage, resolution);
				}*/
			}
			else if (_models == null)
				_models = new Dictionary<Symbol, PyObject>();
			/*else if (UseStorage && (!algorithm.ObjectStore.ContainsKey(PythonModelValues) || !algorithm.ObjectStore.ContainsKey(PythonModelKeys)))
			{
				SaveModels(algorithm);
			}*/
			// Convert the history TradeBars to a list that allows for easy conversion into a DataFrame
			// object in python
			foreach (var symbol in securities)
			{
				var history = new List<TradeBar>();
				foreach (var bar in bars)
				{
					if (!bar.ContainsKey(symbol))
						return;
					history.Add(bar[symbol]);
				}
				// Check to see if we already have a model for the given symbol. If not we will have to create one
				if (_models.ContainsKey(symbol))
				{
					var model = _models[symbol];
					// This is where we implement our python code. This creates the script that we can then call
					using (Py.GIL())
					{
						dynamic GetState = PythonEngine.ModuleFromString("GetStateModule",
							@"
from clr import AddReference
AddReference(""QuantConnect.Common"")
from QuantConnect import *
from QuantConnect.Data.Custom import *
import numpy as np
import pandas as pd
from statsmodels.tsa.stattools import adfuller
from hmmlearn.hmm import GaussianHMM

def to_dict(TradeBar):
	return{
		'close': TradeBar.Close,
		'EndTime': TradeBar.EndTime,
		'Symbol': TradeBar.Symbol,
	}

def TestStationartiy(returns):
    return adfuller(returns)[1] < 0.05

def PredictState(df, model):
	price = np.array(df.iloc[0].close).reshape((1,1))
	return model.predict(price)[0]

def GetState(history, model):
	state = -1
	df = pd.DataFrame([to_dict(s) for s in history], columns = ['close', 'Symbol', 'EndTime'])
	df.set_index('EndTime')
	#returns = np.array(df.close.pct_change().dropna())
	#returns = np.array(returns).reshape((len(returns),1))
	returns = df.unstack(level = 1).close.transpose().pct_change().dropna()
	stationarity = TestStationartiy(returns)
	if stationarity:
		state = PredictState(df, model)
	        
	return state
").GetAttr("GetState");

						// Run the GetState method we created above
						double state = GetState(history, model);
						GetState.Dispose();
						// Add or update the state of the selected symbol
						if (!_symbolState.ContainsKey(symbol))
							_symbolState.Add(symbol, state);
						else if (_symbolState[symbol] != state && state != -1)
							_symbolState[symbol] = state;
					}
				}
				// Create a new model
				else
				{
					// Implementing python code to create the HMM model
					using (Py.GIL())
					{
						dynamic CreateHMM = PythonEngine.ModuleFromString("CreateHMMModule",
							@"
from clr import AddReference
AddReference(""QuantConnect.Common"")
from QuantConnect import *
from QuantConnect.Data.Custom import *
import numpy as np
import pandas as pd
from hmmlearn.hmm import GaussianHMM

def to_dict(TradeBar):
	return{
		'close': TradeBar.Close,
		'EndTime': TradeBar.EndTime,
		'Symbol': TradeBar.Symbol,
	}
	
def CreateHMM(history, symbol):
	df = pd.DataFrame([to_dict(s) for s in history], columns = ['close', 'Symbol', 'EndTime'])
	df.set_index('EndTime')
	returns = np.array(df.close.pct_change().dropna())
	returns = np.array(returns).reshape((len(returns),1))
	_model = GaussianHMM(n_components=2, covariance_type=""full"", n_iter=10000).fit(returns)
	return _model

").GetAttr("CreateHMM");

						// Call the Create HMM model script we created above and add to the dictionary
						var _model = CreateHMM(history, symbol);
						CreateHMM.Dispose();
						_models[symbol] = _model;
					}
				}
				marketstates += $" - {symbol}_{GetSymbolState(symbol)}";
			}
			algorithm.Log("PythonMethods.UpdateMarketState(): COMPLETE - Updating all symbol market states" + marketstates);
		}



		public void RefitModels(QCAlgorithm algorithm, Symbol sym, bool UseStorage = false, Resolution resolution = Resolution.Daily)
		{
			List<Symbol> symList = new List<Symbol>();
			symList.Add(sym);
			RefitModels(algorithm, symList, UseStorage, resolution);
		}

		// Refit the HMM of a selected symbol
		public void RefitModels(QCAlgorithm algorithm, List<Symbol> sym = null, bool UseStorage = false, Resolution resolution = Resolution.Daily)
		{
			IEnumerable<DataDictionary<TradeBar>> bars;
			if (_storedBars == null)
			{
				var symbols = algorithm.Securities.Keys.Where(x => x.SecurityType == SecurityType.Equity);
				bars = sym == null ? algorithm.History<TradeBar>(symbols, 900, resolution) : algorithm.History<TradeBar>(sym, 900, resolution);
			}
			else
			{
				bars = _storedBars;
			}
			List<Symbol> securities = sym == null ? algorithm.Securities.Keys.Where(x => x.SecurityType == SecurityType.Equity).ToList() : sym;
			InternalRefitModels(algorithm, securities, UseStorage, bars);
		}

		public void RefitModels(QCAlgorithm algorithm, Future sym, bool UseStorage = false, Resolution resolution = Resolution.Daily)
		{
			var securities = sym.Symbol;
			var bars = FetchFuturesBars(algorithm, sym, resolution, 900);
			List<Symbol> symList = new List<Symbol>();
			symList.Add(securities);
			InternalRefitModels(algorithm, symList, UseStorage, bars);
		}

		private void InternalRefitModels(QCAlgorithm algorithm, List<Symbol> securities, bool UseStorage, IEnumerable<DataDictionary<TradeBar>> bars)
		{
			// Convert the history TradeBars to a list that allows for easy conversion into a DataFrame
			// object in python
			if (bars.Count() < 50)
				return;
			if (_models == null)
			{
				_models = new Dictionary<Symbol, PyObject>();
			}

			algorithm.Log($"PythonMethods.RefitModels(): Running refit on {bars.Count()} bars for {bars.ToList()[0].Count} symbols");
			foreach (var symbol in securities)
			{
				var history = new List<TradeBar>();
				foreach (var bar in bars)
				{
					if (!bar.ContainsKey(symbol))
						return;
					history.Add(bar[symbol]);
				}
				// Check to see if we already have a model for the given symbol. If not we will have to create one
				if (_models.ContainsKey(symbol))
				{
					// This is where we implement our python code. This creates the script that we can then call
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
from hmmlearn.hmm import GaussianHMM

def to_dict(TradeBar):
	return{
		'close': TradeBar.Close,
		'EndTime': TradeBar.EndTime,
		'Symbol': TradeBar.Symbol,
	}


def RefitModel(history, symbol, model):
	df = pd.DataFrame([to_dict(s) for s in history], columns = ['close', 'Symbol', 'EndTime'])
	df.set_index('EndTime')
	returns = np.array(df.close.pct_change().dropna())
	returns = np.array(returns).reshape((len(returns),1))
	return model.fit(returns)
").GetAttr("RefitModel");

						// Call the Refit HMM model script we created above and update the dictionary
						var model = _models[symbol];
						var _model = RefitModel(history, symbol, model);
						RefitModel.Dispose();
						_models[symbol] = _model;
						continue;
					}
				}
				// Create a new model
				else
				{
					// Implementing python code to create the HMM model
					using (Py.GIL())
					{
						dynamic CreateHMM = PythonEngine.ModuleFromString("CreateHMMModule",
							@"
from clr import AddReference
AddReference(""QuantConnect.Common"")
from QuantConnect import *
from QuantConnect.Data.Custom import *
import numpy as np
import pandas as pd
from hmmlearn.hmm import GaussianHMM

def to_dict(TradeBar):
	return{
		'close': TradeBar.Close,
		'EndTime': TradeBar.EndTime,
		'Symbol': TradeBar.Symbol,
	}
	
def CreateHMM(history, symbol):
	df = pd.DataFrame([to_dict(s) for s in history], columns = ['close', 'Symbol', 'EndTime'])
	df.set_index('EndTime')
	returns = np.array(df.close.pct_change().dropna())
	returns = np.array(returns).reshape((len(returns),1))
	_model = GaussianHMM(n_components=2, covariance_type=""full"", n_iter=10000).fit(returns)
	return _model

").GetAttr("CreateHMM");

						// Call the Create HMM model script we created above and add to the dictionary
						var _model = CreateHMM(history, symbol);
						CreateHMM.Dispose();
						_models[symbol] = _model;
					}
					continue;
				}
			}
			algorithm.Log("PythonMethods.RefitModels(): Refit Models Complete");
			if (UseStorage)
			{
				SaveModels(algorithm);
			}
		}

		// Request and process the TradeBars for futures contracts not working. The history request does not return any values.
		private IEnumerable<DataDictionary<TradeBar>> FetchFuturesBars(QCAlgorithm algorithm, Future sym, Resolution resolution, int numIntervals)
		{
			var bars = resolution == Resolution.Daily ? algorithm.History(_frontContract, TimeSpan.FromDays(numIntervals)) : algorithm.History(_frontContract, numIntervals, resolution);
			_futuresBars = new List<DataDictionary<TradeBar>>();
			if (resolution == Resolution.Daily)
			{
				var consolidator = new TradeBarConsolidator(TimeSpan.FromDays(1));
				consolidator.DataConsolidated += NewFuturesTradebar;
				foreach (var data in bars)
				{
					consolidator.Update(data);
				}
			}
			return _futuresBars;
		}

		private void NewFuturesTradebar(object sender, TradeBar bar)
		{
			DataDictionary<TradeBar> newBar = new DataDictionary<TradeBar>();
			newBar.Add(_frontContractUnderlying, bar);
			_futuresBars.Add(newBar);
		}
		// Retreive the symbol state of a selected symbol
		public MarketState GetSymbolState(Symbol symbol)
		{
			// Check to see if we have a state for the symbol and return that value. Otherwise return -1.
			if (_symbolState.ContainsKey(symbol))
				return (MarketStateConverter(_symbolState[symbol]));
			else
			{
				return MarketState.Unknown;
			}
		}

		public enum MarketState
		{
			Bear = 1,
			Bull = 0,
			Unknown = -1
		}

		public void SetFuturesFrontContract(Symbol FrontContract, Symbol Underlying)
		{
			if (_frontContract != FrontContract)
			{
				_frontContract = FrontContract;
			}
			if (_frontContractUnderlying != Underlying)
			{
				_frontContractUnderlying = Underlying;
			}
		}

		public void UpdateStoredBars(IEnumerable<DataDictionary<TradeBar>> bars)
		{
			if (_storedBars != bars)
			{
				_storedBars = bars;
			}
		}
		private MarketState MarketStateConverter(double state)
		{
			if (state == 1)
				return MarketState.Bear;
			else if (state == 0)
				return MarketState.Bull;
			else
				return MarketState.Unknown;
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