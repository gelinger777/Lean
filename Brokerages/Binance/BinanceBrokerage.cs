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

using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using QuantConnect.Util;
using Timer = System.Timers.Timer;
using System.Globalization;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.Binance
{
    /// <summary>
    /// Binance brokerage implementation
    /// </summary>
    [BrokerageFactory(typeof(BinanceBrokerageFactory))]
    public partial class BinanceBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
    {
        private const string WebSocketBaseUrl = "wss://stream.binance.com:9443";

        private readonly IAlgorithm _algorithm;
        private readonly SymbolPropertiesDatabaseSymbolMapper _symbolMapper = new SymbolPropertiesDatabaseSymbolMapper(Market.Binance);

        private readonly RateGate _webSocketRateLimiter = new RateGate(5, TimeSpan.FromSeconds(1));
        private long _lastRequestId;

        private readonly Timer _keepAliveTimer;
        private readonly Timer _reconnectTimer;
        private RealTimeSynchronizedTimer _readjustPositions;
        private readonly TimeSpan _subscribeDelay = TimeSpan.FromMilliseconds(250);
        private HashSet<Symbol> SubscribedSymbols = new HashSet<Symbol>();
        private readonly object _lockerSubscriptions = new object();
        private DateTime _lastSubscribeRequestUtcTime = DateTime.MinValue;
        private bool _subscriptionsPending;
        private readonly BinanceRestApiClient _apiClient;
        private readonly IWebSocket TickerWebSocket;

        /// <summary>
        /// Constructor for brokerage
        /// </summary>
        /// <param name="apiKey">api key</param>
        /// <param name="apiSecret">api secret</param>
        /// <param name="algorithm">the algorithm instance is required to retrieve account type</param>
        /// <param name="aggregator">the aggregator for consolidating ticks</param>
        public BinanceBrokerage(string apiKey, string apiSecret, IAlgorithm algorithm, IDataAggregator aggregator)
            : base(WebSocketBaseUrl, new WebSocketClientWrapper(), null, apiKey, apiSecret, "Binance")
        {
            _algorithm = algorithm;
            _aggregator = aggregator;

            var subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            subscriptionManager.SubscribeImpl += (s, t) =>
            {
                Subscribe(s);
                return true;
            };
            subscriptionManager.UnsubscribeImpl += (s, t) => Unsubscribe(s);

            SubscriptionManager = subscriptionManager;

            _apiClient = new BinanceRestApiClient(
                _symbolMapper,
                algorithm?.Portfolio,
                apiKey,
                apiSecret);

            _apiClient.OrderSubmit += (s, e) => OnOrderSubmit(e);
            _apiClient.OrderStatusChanged += (s, e) => OnOrderEvent(e);
            _apiClient.Message += (s, e) => OnMessage(e);

            var tickerConnectionHandler = new DefaultConnectionHandler();
            tickerConnectionHandler.ReconnectRequested += (s, e) => { ProcessSubscriptionRequest(); };
            TickerWebSocket = new BinanceWebSocketWrapper(
                tickerConnectionHandler
            );

            TickerWebSocket.Message += (s, e) => OnMessageImpl(s, e, OnStreamMessageImpl);
            TickerWebSocket.Message += (s, e) => (s as BinanceWebSocketWrapper)?.ConnectionHandler.KeepAlive(DateTime.UtcNow);
            TickerWebSocket.Error += OnError;

            // User data streams will close after 60 minutes. It's recommended to send a ping about every 30 minutes.
            // Source: https://github.com/binance-exchange/binance-official-api-docs/blob/master/user-data-stream.md#pingkeep-alive-a-listenkey
            _keepAliveTimer = new Timer
            {
                // 30 minutes
                Interval = 30 * 60 * 1000
            };
            _keepAliveTimer.Elapsed += (s, e) => _apiClient.SessionKeepAlive();

            WebSocket.Open += (s, e) =>
            {
                _keepAliveTimer.Start();

                _readjustPositions = new RealTimeSynchronizedTimer(TimeSpan.FromMinutes(29.9), (d) =>
                {

                    Log.Trace("BinanceBrokerage.ReadjustPositions(): Resyncing Open Positions");
                    var holdings = GetCashBalance(_algorithm);
                    if (holdings == null)
                        return;

                    foreach (var security in algorithm.Portfolio.CashBook)
                    {
                        if (holdings.Any(x => x.Currency == security.Key))
                        {
                            var holding = holdings.FirstOrDefault(x => x.Currency == security.Key);
                            security.Value.SetAmount(holding.Amount);
                        }
                        else
                        {
                            security.Value.SetAmount(0);
                        }
                    }
                    
                    var positions = GetAccountHoldings();
                    foreach (var security in algorithm.Securities)
                    {
                        if (positions.Any(x => x.Symbol == security.Key))
                        {
                            var position = positions.FirstOrDefault(x => x.Symbol == security.Key);
                            security.Value.Holdings.SetHoldings(position.AveragePrice, position.Quantity);
                        }
                        else
                        {
                            security.Value.Holdings.SetHoldings(0, 0);
                        }
                        if (security.Value.Holdings.Quantity != 0)
                            Log.Trace($"BinanceBrokerage.ReadjustPositions(): {security.Value.Symbol} has existing holding: {security.Value.Holdings.Quantity}");
                    }
                });
                _readjustPositions.Start();
            };
            WebSocket.Closed += (s, e) => { _keepAliveTimer.Stop(); };

            // A single connection to stream.binance.com is only valid for 24 hours; expect to be disconnected at the 24 hour mark
            // Source: https://github.com/binance-exchange/binance-official-api-docs/blob/master/web-socket-streams.md#general-wss-information
            _reconnectTimer = new Timer
            {
                // 23.5 hours
                Interval = 23.5 * 60 * 60 * 1000
            };
            _reconnectTimer.Elapsed += (s, e) =>
            {
                Log.Trace("Daily websocket restart: disconnect");
                Disconnect();

                Log.Trace("Daily websocket restart: connect");
                Connect();

                ProcessSubscriptionRequest();
            };
        }

        #region IBrokerage

        /// <summary>
        /// Checks if the websocket connection is connected or in the process of connecting
        /// </summary>
        public override bool IsConnected => WebSocket.IsOpen;

        /// <summary>
        /// Creates wss connection
        /// </summary>
        public override void Connect()
        {
            if (IsConnected)
                return;

            _apiClient.CreateListenKey();
            _reconnectTimer.Start();

            WebSocket.Initialize($"{WebSocketBaseUrl}/ws/{_apiClient.SessionId}");

            base.Connect();
        }

        /// <summary>
        /// Closes the websockets connection
        /// </summary>
        public override void Disconnect()
        {
            _reconnectTimer.Stop();

            WebSocket?.Close();
            _apiClient.StopSession();
        }

        /// <summary>
        /// Gets all open positions
        /// </summary>
        /// <returns></returns>
        public override List<Holding> GetAccountHoldings()
        {
            var holdings = new List<Holding>();
            var _tradesDictionary = _apiClient.GetAccountHoldings();

            if (_tradesDictionary != null)
            {
                foreach (var trades in _tradesDictionary.Values)
                {
                    var holding = ConvertHolding(trades.ToList().OrderBy(x => x.Time));
                    if (holding.Symbol.EndsWith("BTC"))
                    {
                        var cash = holding.Symbol.Value.RemoveFromEnd("BTC");
                        holding.Quantity = (_cashHoldings.Any(x => x.Currency == cash) ? _cashHoldings.Where(x => x.Currency == cash).First().Amount : 0);
                    }
                    holdings.Add(holding);
                }
            }

            return holdings;
        }

        private List<CashAmount> _cashHoldings;
        /// <summary>
        /// Gets the total account cash balance for specified account type
        /// </summary>
        /// <returns></returns>
        public override List<CashAmount> GetCashBalance()
        {
            var account = _apiClient.GetCashBalance();
            var balances = account.Balances?.Where(balance => balance.Amount > 0).ToList();
            if (balances == null || !balances.Any())
                return new List<CashAmount>();

            _cashHoldings =  balances
                .Select(b => new CashAmount(b.Amount, b.Asset.LazyToUpper()))
                .ToList();
            return _cashHoldings;
        }

        /// <summary>
        /// Gets the total account cash balance for specified account type
        /// </summary>
        /// <returns></returns>
        public List<CashAmount> GetCashBalance(Interfaces.IAlgorithm algorithm)
        {
            var account = _apiClient.GetCashBalance();
            var balances = account.Balances?.Where(balance => balance.Amount > 0).ToList();
            if (balances == null || !balances.Any())
                return new List<CashAmount>();

            _cashHoldings = balances
                .Select(b => new CashAmount(b.Amount, b.Asset.LazyToUpper()))
                .ToList();
            return _cashHoldings;
        }

        /// <summary>
        /// Gets all orders not yet closed
        /// </summary>
        /// <returns></returns>
        public override List<Order> GetOpenOrders()
        {
            var orders = _apiClient.GetOpenOrders();
            List<Order> list = new List<Order>();
            foreach (var item in orders)
            {
                Order order;
                switch (item.Type.LazyToUpper())
                {
                    case "MARKET":
                        order = new MarketOrder { Price = item.Price };
                        break;
                    case "LIMIT":
                    case "LIMIT_MAKER":
                        order = new LimitOrder { LimitPrice = item.Price };
                        break;
                    case "STOP_LOSS":
                    case "TAKE_PROFIT":
                        order = new StopMarketOrder { StopPrice = item.StopPrice, Price = item.Price };
                        break;
                    case "STOP_LOSS_LIMIT":
                    case "TAKE_PROFIT_LIMIT":
                        order = new StopLimitOrder { StopPrice = item.StopPrice, LimitPrice = item.Price };
                        break;
                    default:
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1,
                            "BinanceBrokerage.GetOpenOrders: Unsupported order type returned from brokerage: " + item.Type));
                        continue;
                }

                order.Quantity = item.Quantity;
                order.BrokerId = new List<string> { item.Id };
                order.Symbol = _symbolMapper.GetLeanSymbol(item.Symbol, SecurityType.Crypto, Market.Binance);
                order.Time = Time.UnixMillisecondTimeStampToDateTime(item.Time);
                order.Status = ConvertOrderStatus(item.Status);
                order.Price = item.Price;

                if (order.Status.IsOpen())
                {
                    var cached = CachedOrderIDs.Where(c => c.Value.BrokerId.Contains(order.BrokerId.First())).ToList();
                    if (cached.Any())
                    {
                        CachedOrderIDs[cached.First().Key] = order;
                    }
                }

                list.Add(order);
            }

            return list;
        }

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public override bool PlaceOrder(Order order)
        {
            var submitted = false;

            WithLockedStream(() =>
            {
                submitted = _apiClient.PlaceOrder(order);
            });

            return submitted;
        }

        /// <summary>
        /// Updates the order with the same id
        /// </summary>
        /// <param name="order">The new order information</param>
        /// <returns>True if the request was made for the order to be updated, false otherwise</returns>
        public override bool UpdateOrder(Order order)
        {
            throw new NotSupportedException("BinanceBrokerage.UpdateOrder: Order update not supported. Please cancel and re-create.");
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was submitted for cancellation, false otherwise</returns>
        public override bool CancelOrder(Order order)
        {
            var submitted = false;

            WithLockedStream(() =>
            {
                submitted = _apiClient.CancelOrder(order);
            });

            return submitted;
        }

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        public override IEnumerable<BaseData> GetHistory(Data.HistoryRequest request)
        {
            if (request.Resolution == Resolution.Tick || request.Resolution == Resolution.Second)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidResolution",
                    $"{request.Resolution} resolution is not supported, no history returned"));
                yield break;
            }

            if (request.TickType != TickType.Trade)
            {/*
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidTickType",
                    $"{request.TickType} tick type not supported, no history returned"));*/
                yield break;
            }

            var period = request.Resolution.ToTimeSpan();

            foreach (var kline in _apiClient.GetHistory(request))
            {
                yield return new TradeBar()
                {
                    Time = Time.UnixMillisecondTimeStampToDateTime(kline.OpenTime),
                    Symbol = request.Symbol,
                    Low = kline.Low,
                    High = kline.High,
                    Open = kline.Open,
                    Close = kline.Close,
                    Volume = kline.Volume,
                    Value = kline.Close,
                    DataType = MarketDataType.TradeBar,
                    Period = period,
                    EndTime = Time.UnixMillisecondTimeStampToDateTime(kline.OpenTime + (long)period.TotalMilliseconds)
                };
            }
        }

        /// <summary>
        /// Wss message handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        public override void OnMessage(object sender, WebSocketMessage e)
        {
            try
            {
                if (_streamLocked)
                {
                    _messageBuffer.Enqueue(e);
                    return;
                }
            }
            catch (Exception err)
            {
                Log.Error(err);
            }

            OnMessageImpl(e);
        }

        #endregion

        #region IDataQueueHandler

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
        }

        /// <summary>
        /// Subscribe to the specified configuration
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
        /// <returns>The new enumerator for this subscription request</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return Enumerable.Empty<BaseData>().GetEnumerator();
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            SubscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        /// <param name="dataConfig">Subscription config to be removed</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            SubscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        /// <summary>
        /// Checks if this brokerage supports the specified symbol
        /// </summary>
        /// <param name="symbol">The symbol</param>
        /// <returns>returns true if brokerage supports the specified symbol; otherwise false</returns>
        private static bool CanSubscribe(Symbol symbol)
        {
            return !symbol.Value.Contains("UNIVERSE") &&
                   symbol.SecurityType == SecurityType.Crypto;
        }

        #endregion

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public override void Dispose()
        {
            _keepAliveTimer.DisposeSafely();
            _reconnectTimer.DisposeSafely();
            _apiClient.DisposeSafely();
            _webSocketRateLimiter.DisposeSafely();
        }

        /// <summary>
        /// Subscribes to the requested symbols (using an individual streaming channel)
        /// </summary>
        /// <param name="symbols">The list of symbols to subscribe</param>
        public override void Subscribe(IEnumerable<Symbol> symbols)
        {
            lock (_lockerSubscriptions)
            {
                List<Symbol> symbolsToSubscribe = new List<Symbol>();
                foreach (var symbol in symbols)
                {
                    if (symbol.Value.Contains("UNIVERSE") ||
                        string.IsNullOrEmpty(_symbolMapper.GetBrokerageSymbol(symbol)) ||
                        SubscribedSymbols.Contains(symbol))
                    {
                        continue;
                    }

                    symbolsToSubscribe.Add(symbol);
                }

                if (symbolsToSubscribe.Count == 0)
                    return;

                Log.Trace("BinanceMarginBrokerage.Subscribe(): {0}", string.Join(",", symbolsToSubscribe.Select(x => x.Value)));

                SubscribedSymbols = symbolsToSubscribe
                    .Union(SubscribedSymbols.ToList())
                    .ToList()
                    .ToHashSet();

                ProcessSubscriptionRequest();
            }
        }

        private void ProcessSubscriptionRequest()
        {
            if (_subscriptionsPending) return;

            _lastSubscribeRequestUtcTime = DateTime.UtcNow;
            _subscriptionsPending = true;

            Task.Run(async () =>
            {
                while (true)
                {
                    DateTime requestTime;
                    List<Symbol> symbolsToSubscribe;
                    lock (_lockerSubscriptions)
                    {
                        requestTime = _lastSubscribeRequestUtcTime.Add(_subscribeDelay);
                        symbolsToSubscribe = SubscribedSymbols.ToList();
                    }

                    if (DateTime.UtcNow > requestTime)
                    {
                        // restart streaming session
                        SubscribeSymbols(symbolsToSubscribe);

                        lock (_lockerSubscriptions)
                        {
                            _lastSubscribeRequestUtcTime = DateTime.UtcNow;
                            if (SubscribedSymbols.Count == symbolsToSubscribe.Count)
                            {
                                // no more subscriptions pending, task finished
                                _subscriptionsPending = false;
                                break;
                            }
                        }
                    }

                    await Task.Delay(200).ConfigureAwait(false);
                }
            });
        }

        private void SubscribeSymbols(List<Symbol> symbolsToSubscribe)
        {
            if (symbolsToSubscribe.Count == 0)
                return;

            //close current connection
            if (TickerWebSocket.IsOpen)
            {
                TickerWebSocket.Close();
            }
            Wait(() => !TickerWebSocket.IsOpen);

            //var streams = symbolsToSubscribe.Select((s) => string.Format(CultureInfo.InvariantCulture, "{0}@depth/{0}@trade", s.Value.LazyToLower()));
            var streams = symbolsToSubscribe.Select((s) => string.Format(CultureInfo.InvariantCulture, "{0}@trade", s.Value.ToLower(CultureInfo.InvariantCulture)));
            TickerWebSocket.Initialize($"{WebSocketBaseUrl}/stream?streams={string.Join("/", streams)}");

            Log.Trace($"BaseWebsocketsBrokerage(): Reconnecting... IsConnected: {IsConnected}");

            TickerWebSocket.Error -= this.OnError;
            try
            {
                //try to clean up state
                if (TickerWebSocket.IsOpen)
                {
                    TickerWebSocket.Close();
                    Wait(() => !TickerWebSocket.IsOpen);
                }
                if (!TickerWebSocket.IsOpen)
                {
                    TickerWebSocket.Connect();
                    Wait(() => TickerWebSocket.IsOpen);
                }
            }
            finally
            {
                TickerWebSocket.Error += this.OnError;
                this.Subscribe(symbolsToSubscribe);
            }

            Log.Trace("BinanceMarginBrokerage.Subscribe: Sent subscribe.");
        }
        
        /// <summary>
        /// Ends current subscriptions
        /// </summary>
        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            lock (_lockerSubscriptions)
            {
                if (WebSocket.IsOpen)
                {
                    var symbolsToUnsubscribe = (from symbol in symbols
                                                where SubscribedSymbols.Contains(symbol)
                                                select symbol).ToList();
                    if (symbolsToUnsubscribe.Count == 0)
                        return true;

                    Log.Trace("BinanceMarginBrokerage.Unsubscribe(): {0}", string.Join(",", symbolsToUnsubscribe.Select(x => x.Value)));

                    SubscribedSymbols = SubscribedSymbols
                        .ToList()
                        .Where(x => !symbolsToUnsubscribe.Contains(x))
                        .ToHashSet();

                    ProcessSubscriptionRequest();
                }
            }
            return true;
        }

        private void Send(IWebSocket webSocket, object obj)
        {
            var json = JsonConvert.SerializeObject(obj);

            if (!_webSocketRateLimiter.WaitToProceed(TimeSpan.Zero))
            {
                _webSocketRateLimiter.WaitToProceed();
            }

            Log.Trace("Send: " + json);

            webSocket.Send(json);
        }

        private long GetNextRequestId()
        {
            return Interlocked.Increment(ref _lastRequestId);
        }

        /// <summary>
        /// Event invocator for the OrderFilled event
        /// </summary>
        /// <param name="e">The OrderEvent</param>
        private void OnOrderSubmit(BinanceOrderSubmitEventArgs e)
        {
            var brokerId = e.BrokerId;
            var order = e.Order;
            if (CachedOrderIDs.ContainsKey(order.Id))
            {
                CachedOrderIDs[order.Id].BrokerId.Clear();
                CachedOrderIDs[order.Id].BrokerId.Add(brokerId);
            }
            else
            {
                order.BrokerId.Add(brokerId);
                CachedOrderIDs.TryAdd(order.Id, order);
            }
        }
    }
}
