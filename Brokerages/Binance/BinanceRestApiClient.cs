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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace QuantConnect.Brokerages.Binance
{
    /// <summary>
    /// Binance REST API implementation
    /// </summary>
    public class BinanceRestApiClient : IDisposable
    {
        private const string RestApiUrl = "https://api.binance.com";
        private const string UserDataStreamEndpoint = "/api/v3/userDataStream";

        private readonly SymbolPropertiesDatabaseSymbolMapper _symbolMapper;
        private readonly ISecurityProvider _securityProvider;
        private readonly IRestClient _restClient;
        private readonly RateGate _restRateLimiter = new RateGate(10, TimeSpan.FromSeconds(1));
        private readonly object _listenKeyLocker = new object();

        private readonly bool autoTimestamp = true;
        private readonly TimeSpan autoTimestampRecalculationInterval = TimeSpan.FromHours(3);
        private readonly TimeSpan timestampOffset = TimeSpan.Zero;
        private double calculatedTimeOffset;
        private bool timeSynced;
        private DateTime lastTimeSync;
        private int TotalRequestsMade = 1;

        /// <summary>
        /// Event that fires each time an order is filled
        /// </summary>
        public event EventHandler<BinanceOrderSubmitEventArgs> OrderSubmit;

        /// <summary>
        /// Event that fires each time an order is filled
        /// </summary>
        public event EventHandler<OrderEvent> OrderStatusChanged;

        /// <summary>
        /// Event that fires when an error is encountered in the brokerage
        /// </summary>
        public event EventHandler<BrokerageMessageEvent> Message;

        /// <summary>
        /// Key Header
        /// </summary>
        public readonly string KeyHeader = "X-MBX-APIKEY";

        /// <summary>
        /// The api secret
        /// </summary>
        protected string ApiSecret;

        /// <summary>
        /// The api key
        /// </summary>
        protected string ApiKey;

        /// <summary>
        /// Represents UserData Session listen key
        /// </summary>
        public string SessionId { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="BinanceRestApiClient"/> class.
        /// </summary>
        /// <param name="symbolMapper">The symbol mapper.</param>
        /// <param name="securityProvider">The holdings provider.</param>
        /// <param name="apiKey">The Binance API key</param>
        /// <param name="apiSecret">The The Binance API secret</param>
        public BinanceRestApiClient(SymbolPropertiesDatabaseSymbolMapper symbolMapper, ISecurityProvider securityProvider, string apiKey, string apiSecret)
        {
            _symbolMapper = symbolMapper;
            _securityProvider = securityProvider;
            _restClient = new RestClient(RestApiUrl);
            ApiKey = apiKey;
            ApiSecret = apiSecret;
        }

        private Dictionary<string, Messages.TradeList[]> _tradesDictionary;
        /// <summary>
        /// Gets all open positions
        /// </summary>
        /// <returns></returns>
        public Dictionary<string, Messages.TradeList[]> GetAccountHoldings()
        {
            SetAccountTrades();
            return _tradesDictionary;
        }

        public void SetAccountTrades()
        {
            if (_startupAccountInfo != null)
            {
                _tradesDictionary = new Dictionary<string, Messages.TradeList[]>();
                foreach (var bal in _startupAccountInfo.Balances.Where(x => x.Asset != "BTC" && x.Amount != 0))
                {
                    var timestampResult = CheckAutoTimestamp();
                    var symbol = bal.Asset + "BTC";

                    IDictionary<string, object> body = new Dictionary<string, object>()
                    {
                        { "symbol", symbol }
                    };

                    var queryString = $"symbol={symbol}&timestamp={GetNonce()}";
                    var endpoint = $"/api/v3/myTrades?{queryString}&signature={AuthenticationToken(queryString)}";
                    var request = new RestRequest(endpoint, Method.GET);
                    request.AddHeader(KeyHeader, ApiKey);
                    var response = ExecuteRestRequest(request);
                    if (response.StatusCode != HttpStatusCode.OK && response.Content.Contains("-1021"))
                    {
                        GetServerTime(true);
                        response = ExecuteRestRequest(request);
                    }
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (_tradesDictionary.ContainsKey(symbol))
                            _tradesDictionary[symbol] = JsonConvert.DeserializeObject<Messages.TradeList[]>(response.Content);
                        else
                            _tradesDictionary.Add(symbol, JsonConvert.DeserializeObject<Messages.TradeList[]>(response.Content));
                    }
                }
            }
        }

        private Messages.AccountInformation _startupAccountInfo;
        /// <summary>
        /// Gets the total account cash balance for specified account type
        /// </summary>
        /// <returns></returns>
        public Messages.AccountInformation GetCashBalance(bool IsWarmingUp = true)
        {
            var timestampResult = CheckAutoTimestamp();

            var queryString = $"timestamp={GetNonce()}";
            var endpoint = $"/api/v3/account?{queryString}&signature={AuthenticationToken(queryString)}";
            var request = new RestRequest(endpoint, Method.GET);
            request.AddHeader(KeyHeader, ApiKey);

            var response = ExecuteRestRequest(request);
            if (response.StatusCode != HttpStatusCode.OK && response.Content.Contains("-1021"))
            {
                GetServerTime(true);
                response = ExecuteRestRequest(request);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (IsWarmingUp)
                    throw new Exception($"BinanceBrokerage.GetCashBalance: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
                else
                    return null;
            }

            _startupAccountInfo = JsonConvert.DeserializeObject<Messages.AccountInformation>(response.Content);
            return _startupAccountInfo;
        }

        /// <summary>
        /// Gets all orders not yet closed
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Messages.OpenOrder> GetOpenOrders()
        {
            var timestampResult = CheckAutoTimestamp();

            var queryString = $"timestamp={GetNonce()}";
            var endpoint = $"/api/v3/openOrders?{queryString}&signature={AuthenticationToken(queryString)}";
            var request = new RestRequest(endpoint, Method.GET);
            request.AddHeader(KeyHeader, ApiKey);

            var response = ExecuteRestRequest(request);

            if (response.StatusCode != HttpStatusCode.OK && response.Content.Contains("-1021"))
            {
                GetServerTime(true);
                response = ExecuteRestRequest(request);
            }
            if (response.StatusCode != HttpStatusCode.OK &&
                (response.Content.Contains("-1021") || response.Content.Contains("-3008")))
            {
                GetServerTime(true);
                response = ExecuteRestRequest(request);
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"BinanceBrokerage.GetCashBalance: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            return JsonConvert.DeserializeObject<Messages.OpenOrder[]>(response.Content);
        }

        /// <summary>
        /// Places a new order and assigns a new broker ID to the order
        /// </summary>
        /// <param name="order">The order to be placed</param>
        /// <returns>True if the request for a new order has been placed, false otherwise</returns>
        public bool PlaceOrder(Order order)
        {
            // supported time in force values {GTC, IOC, FOK}
            // use GTC as LEAN doesn't support others yet
            var timestampResult = CheckAutoTimestamp();
            if (timestampResult == null)
                return false;

            IDictionary<string, object> body = new Dictionary<string, object>()
            {
                { "symbol", _symbolMapper.GetBrokerageSymbol(order.Symbol) },
                { "quantity", Math.Abs(order.Quantity).ToString(CultureInfo.InvariantCulture) },
                { "side", ConvertOrderDirection(order.Direction) }
            };

            switch (order.Type)
            {
                case OrderType.Limit:
                    body["type"] = (order.Properties as BinanceOrderProperties)?.PostOnly == true
                        ? "LIMIT_MAKER"
                        : "LIMIT";
                    body["price"] = ((LimitOrder) order).LimitPrice.ToString(CultureInfo.InvariantCulture);
                    // timeInForce is not required for LIMIT_MAKER
                    if (Equals(body["type"], "LIMIT"))
                        body["timeInForce"] = "GTC";
                    break;
                case OrderType.Market:
                    body["type"] = "MARKET";
                    break;
                case OrderType.StopLimit:
                    var ticker = GetTickerPrice(order);
                    var stopPrice = ((StopLimitOrder) order).StopPrice;
                    if (order.Direction == OrderDirection.Sell)
                    {
                        body["type"] = stopPrice <= ticker ? "STOP_LOSS_LIMIT" : "TAKE_PROFIT_LIMIT";
                    }
                    else
                    {
                        body["type"] = stopPrice <= ticker ? "TAKE_PROFIT_LIMIT" : "STOP_LOSS_LIMIT";
                    }
                    body["timeInForce"] = "GTC";
                    body["stopPrice"] = stopPrice.ToString(CultureInfo.InvariantCulture);
                    body["price"] = ((StopLimitOrder) order).LimitPrice.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new NotSupportedException($"BinanceBrokerage.ConvertOrderType: Unsupported order type: {order.Type}");
            }

            const string endpoint = "/api/v3/order";
            body["timestamp"] = GetNonce();
            body["signature"] = AuthenticationToken(body.ToQueryString());
            var request = new RestRequest(endpoint, Method.POST);
            request.AddHeader(KeyHeader, ApiKey);
            request.AddParameter(
                "application/x-www-form-urlencoded",
                Encoding.UTF8.GetBytes(body.ToQueryString()),
                ParameterType.RequestBody
            );

            var response = ExecuteRestRequest(request);
            if (response.StatusCode != HttpStatusCode.OK &&
                (response.Content.Contains("-1021") || response.Content.Contains("-3008")))
            {
                GetServerTime(true);
                response = ExecuteRestRequest(request);
            }
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var raw = JsonConvert.DeserializeObject<Messages.NewOrder>(response.Content);

                if (string.IsNullOrEmpty(raw?.Id))
                {
                    var errorMessage = $"Error parsing response from place order: {response.Content}";
                    OnOrderEvent(new OrderEvent(
                        order,
                        DateTime.UtcNow,
                        OrderFee.Zero,
                        "Binance Order Event")
                    { Status = OrderStatus.Invalid, Message = errorMessage });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, (int)response.StatusCode, errorMessage));

                    return true;
                }

                OnOrderSubmit(raw, order);
                return true;
            }

            var message = $"Order failed, Order Id: {order.Id} timestamp: {order.Time} quantity: {order.Quantity} content: {response.Content}";
            OnOrderEvent(new OrderEvent(
                order,
                DateTime.UtcNow,
                OrderFee.Zero,
                "Binance Order Event")
            { Status = OrderStatus.Invalid });
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, -1, message));

            return true;
        }

        /// <summary>
        /// Cancels the order with the specified ID
        /// </summary>
        /// <param name="order">The order to cancel</param>
        /// <returns>True if the request was submitted for cancellation, false otherwise</returns>
        public bool CancelOrder(Order order)
        {
            var success = new List<bool>();
            var timestampResult = CheckAutoTimestamp();
            if (timestampResult == null)
                return false;

            IDictionary<string, object> body = new Dictionary<string, object>()
            {
                { "symbol", _symbolMapper.GetBrokerageSymbol(order.Symbol) }
            };
            foreach (var id in order.BrokerId)
            {
                if (body.ContainsKey("signature"))
                {
                    body.Remove("signature");
                }
                body["orderId"] = id;
                body["timestamp"] = GetNonce();
                body["signature"] = AuthenticationToken(body.ToQueryString());

                var request = new RestRequest("/api/v3/order", Method.DELETE);
                request.AddHeader(KeyHeader, ApiKey);
                request.AddParameter(
                    "application/x-www-form-urlencoded",
                    Encoding.UTF8.GetBytes(body.ToQueryString()),
                    ParameterType.RequestBody
                );

                var response = ExecuteRestRequest(request);
                if (response.StatusCode != HttpStatusCode.OK &&
                    (response.Content.Contains("-1021") || response.Content.Contains("-3008")))
                {
                    GetServerTime(true);
                    response = ExecuteRestRequest(request);
                }
                success.Add(response.StatusCode == HttpStatusCode.OK);
            }

            var canceled = false;
            if (success.All(a => a))
            {
                OnOrderEvent(new OrderEvent(order,
                    DateTime.UtcNow,
                    OrderFee.Zero,
                    "Binance Order Event")
                { Status = OrderStatus.Canceled });

                canceled = true;
            }
            return canceled;
        }

        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of bars covering the span specified in the request</returns>
        public IEnumerable<Messages.Kline> GetHistory(Data.HistoryRequest request)
        {
            var resolution = ConvertResolution(request.Resolution);
            var resolutionInMs = (long)request.Resolution.ToTimeSpan().TotalMilliseconds;
            var symbol = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            var startMs = (long)Time.DateTimeToUnixTimeStamp(request.StartTimeUtc) * 1000;
            var endMs = (long)Time.DateTimeToUnixTimeStamp(request.EndTimeUtc) * 1000;
            var endpoint = $"/api/v3/klines?symbol={symbol}&interval={resolution}&limit=1000";

            while (endMs - startMs >= resolutionInMs)
            {
                var timeframe = $"&startTime={startMs}&endTime={endMs}";

                var restRequest = new RestRequest(endpoint + timeframe, Method.GET);
                var response = ExecuteRestRequest(restRequest);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new Exception($"BinanceBrokerage.GetHistory: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
                }

                var klines = JsonConvert.DeserializeObject<object[][]>(response.Content)
                    .Select(entries => new Messages.Kline(entries))
                    .ToList();
                try
                {
                    startMs = klines.Last().OpenTime + resolutionInMs;
                }
                catch(Exception e)
                {
                    Log.Error(e, $"BinanceResApiClint.GetHistory(): Failed on security {symbol}, startTime = {request.StartTimeUtc}, endTime = {request.EndTimeUtc}");
                }
                foreach (var kline in klines)
                {
                    yield return kline;
                }

            }

        }

        /// <summary>
        /// Check User Data stream listen key is alive
        /// </summary>
        /// <returns></returns>
        public bool SessionKeepAlive()
        {
            if (string.IsNullOrEmpty(SessionId))
            {
                throw new Exception("BinanceBrokerage:UserStream. listenKey wasn't allocated or has been refused.");
            }

            var ping = new RestRequest(UserDataStreamEndpoint, Method.PUT);
            ping.AddHeader(KeyHeader, ApiKey);
            ping.AddParameter(
                "application/x-www-form-urlencoded",
                Encoding.UTF8.GetBytes($"listenKey={SessionId}"),
                ParameterType.RequestBody
            );

            var pong = ExecuteRestRequest(ping);
            return pong.StatusCode == HttpStatusCode.OK;
        }

        /// <summary>
        /// Stops the session
        /// </summary>
        public void StopSession()
        {
            if (string.IsNullOrEmpty(SessionId))
            {
                throw new Exception("BinanceBrokerage:UserStream. listenKey wasn't allocated or has been refused.");
            }

            var request = new RestRequest(UserDataStreamEndpoint, Method.DELETE);
            request.AddHeader(KeyHeader, ApiKey);
            request.AddParameter(
                "application/x-www-form-urlencoded",
                Encoding.UTF8.GetBytes($"listenKey={SessionId}"),
                ParameterType.RequestBody
            );
            ExecuteRestRequest(request);
        }

        /// <summary>
        /// Provides the current tickers price
        /// </summary>
        /// <returns></returns>
        public Messages.PriceTicker[] GetTickers()
        {
            const string endpoint = "/api/v3/ticker/price";
            var req = new RestRequest(endpoint, Method.GET);
            var response = ExecuteRestRequest(req);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"BinanceBrokerage.GetTick: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            return JsonConvert.DeserializeObject<Messages.PriceTicker[]>(response.Content);
        }

        /// <summary>
        /// Start user data stream
        /// </summary>
        public void CreateListenKey()
        {
            var request = new RestRequest(UserDataStreamEndpoint, Method.POST);
            request.AddHeader(KeyHeader, ApiKey);

            var response = ExecuteRestRequest(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new Exception($"BinanceBrokerage.StartSession: request failed: [{(int)response.StatusCode}] {response.StatusDescription}, Content: {response.Content}, ErrorMessage: {response.ErrorMessage}");
            }

            var content = JObject.Parse(response.Content);
            lock (_listenKeyLocker)
            {
                SessionId = content.Value<string>("listenKey");
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            _restRateLimiter.DisposeSafely();
        }

        /// <summary>
        /// If an IP address exceeds a certain number of requests per minute
        /// HTTP 429 return code is used when breaking a request rate limit.
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        private IRestResponse ExecuteRestRequest(IRestRequest request)
        {
            const int maxAttempts = 10;
            var attempts = 0;
            IRestResponse response;

            do
            {
                if (!_restRateLimiter.WaitToProceed(TimeSpan.Zero))
                {
                    Log.Trace("Brokerage.OnMessage(): " + new BrokerageMessageEvent(BrokerageMessageType.Warning, "RateLimit",
                        "The API request has been rate limited. To avoid this message, please reduce the frequency of API calls."));

                    _restRateLimiter.WaitToProceed();
                }

                response = _restClient.Execute(request);
                // 429 status code: Too Many Requests
            } while (++attempts < maxAttempts && (int)response.StatusCode == 429);

            return response;
        }

        private decimal GetTickerPrice(Order order)
        {
            var security = _securityProvider.GetSecurity(order.Symbol);
            var tickerPrice = order.Direction == OrderDirection.Buy ? security.AskPrice : security.BidPrice;
            if (tickerPrice == 0)
            {
                var brokerageSymbol = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                var tickers = GetTickers();
                var ticker = tickers.FirstOrDefault(t => t.Symbol == brokerageSymbol);
                if (ticker == null)
                {
                    throw new KeyNotFoundException($"BinanceBrokerage: Unable to resolve currency conversion pair: {order.Symbol}");
                }
                tickerPrice = ticker.Price;
            }
            return tickerPrice;
        }

        /// <summary>
        /// Timestamp in milliseconds
        /// </summary>
        /// <returns></returns>
        private long GetNonce()
        {
            var offset = autoTimestamp ? calculatedTimeOffset : 0;
            offset += timestampOffset.TotalMilliseconds;
            return ToUnixTimestamp(DateTime.UtcNow.AddMilliseconds(offset));
        }
        private static long ToUnixTimestamp(DateTime time)
        {
            return (long)(time - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private DateTime CheckAutoTimestamp()
        {
            if (autoTimestamp && (!timeSynced || DateTime.UtcNow - lastTimeSync > autoTimestampRecalculationInterval))
                return GetServerTime(timeSynced);

            return DateTime.UtcNow + timestampOffset;
        }

        /// <summary>
        /// Requests the server for the local time. This function also determines the offset between server and local time and uses this for subsequent API calls
        /// </summary>
        /// <param name="resetAutoTimestamp">Whether the response should be used for a new auto timestamp calculation</param>
        /// <returns>Server time</returns>
        public DateTime GetServerTime(bool resetAutoTimestamp = false)
        {
            var endpoint = $"/api/v3/time";
            var request = new RestRequest(endpoint, Method.GET);
            request.AddHeader(KeyHeader, ApiKey);

            if (!autoTimestamp)
            {
                var response = ExecuteRestRequest(request);
                var result = JsonConvert.DeserializeObject<BinanceFutures.Messages.BinanceCheckTime>(response.Content);
                if (!response.IsSuccessful)
                    return DateTime.UtcNow + timestampOffset;

                return result.ServerTime;
            }
            else
            {
                var localTime = DateTime.UtcNow;
                var response = ExecuteRestRequest(request);
                if (!response.IsSuccessful)
                    return DateTime.UtcNow + timestampOffset;

                var result = JsonConvert.DeserializeObject<BinanceFutures.Messages.BinanceCheckTime>(response.Content);

                if (timeSynced && !resetAutoTimestamp)
                    return result.ServerTime;

                if (TotalRequestsMade == 1)
                {
                    // If this was the first request make another one to calculate the offset since the first one can be slower
                    localTime = DateTime.UtcNow;
                    response = ExecuteRestRequest(request);
                    if (!response.IsSuccessful)
                        return DateTime.UtcNow + timestampOffset;
                    result = JsonConvert.DeserializeObject<BinanceFutures.Messages.BinanceCheckTime>(response.Content);
                    TotalRequestsMade++;
                }

                // Calculate time offset between local and server
                var offset = (result.ServerTime - localTime).TotalMilliseconds;
                if (offset >= 0 && offset < 500)
                {
                    // Small offset, probably mainly due to ping. Don't adjust time
                    calculatedTimeOffset = 0;
                    timeSynced = true;
                    lastTimeSync = DateTime.UtcNow;
                    Log.Trace("Brokerage.GetServerTime(): " + new BrokerageMessageEvent(BrokerageMessageType.Information, "0",
                        $"Time offset between 0 and 500ms ({offset}ms), no adjustment needed"));
                    return result.ServerTime;
                }

                calculatedTimeOffset = (result.ServerTime - localTime).TotalMilliseconds;
                timeSynced = true;
                lastTimeSync = DateTime.UtcNow;
                Log.Trace("Brokerage.GetServerTime(): " + new BrokerageMessageEvent(BrokerageMessageType.Information, "0",
                    $"Time offset set to {calculatedTimeOffset}ms"));
                return result.ServerTime;
            }
        }

        /// <summary>
        /// Creates a signature for signed endpoints
        /// </summary>
        /// <param name="payload">the body of the request</param>
        /// <returns>a token representing the request params</returns>
        private string AuthenticationToken(string payload)
        {
            using (HMACSHA256 hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiSecret)))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)).ToHexString();
            }
        }

        private static string ConvertOrderDirection(OrderDirection orderDirection)
        {
            if (orderDirection == OrderDirection.Buy || orderDirection == OrderDirection.Sell)
            {
                return orderDirection.ToString().LazyToUpper();
            }

            throw new NotSupportedException($"BinanceBrokerage.ConvertOrderDirection: Unsupported order direction: {orderDirection}");
        }


        private readonly Dictionary<Resolution, string> _knownResolutions = new Dictionary<Resolution, string>()
        {
            { Resolution.Minute, "1m" },
            { Resolution.Hour,   "1h" },
            { Resolution.Daily,  "1d" }
        };

        private string ConvertResolution(Resolution resolution)
        {
            if (_knownResolutions.ContainsKey(resolution))
            {
                return _knownResolutions[resolution];
            }
            else
            {
                throw new ArgumentException($"BinanceBrokerage.ConvertResolution: Unsupported resolution type: {resolution}");
            }
        }

        /// <summary>
        /// Event invocator for the OrderFilled event
        /// </summary>
        /// <param name="newOrder">The brokerage order submit result</param>
        /// <param name="order">The lean order</param>
        private void OnOrderSubmit(Messages.NewOrder newOrder, Order order)
        {
            try
            {
                OrderSubmit?.Invoke(
                    this,
                    new BinanceOrderSubmitEventArgs(newOrder.Id, order));

                // Generate submitted event
                OnOrderEvent(new OrderEvent(
                    order,
                    Time.UnixMillisecondTimeStampToDateTime(newOrder.TransactionTime),
                    OrderFee.Zero,
                    "Binance Order Event")
                { Status = OrderStatus.Submitted }
                );
                Log.Trace($"Order submitted successfully - OrderId: {order.Id}");
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }

        /// <summary>
        /// Event invocator for the OrderFilled event
        /// </summary>
        /// <param name="e">The OrderEvent</param>
        private void OnOrderEvent(OrderEvent e)
        {
            try
            {
                Log.Debug("Brokerage.OnOrderEvent(): " + e);

                OrderStatusChanged?.Invoke(this, e);
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }

        /// <summary>
        /// Event invocator for the Message event
        /// </summary>
        /// <param name="e">The error</param>
        protected virtual void OnMessage(BrokerageMessageEvent e)
        {
            try
            {
                if (e.Type == BrokerageMessageType.Error)
                {
                    Log.Error("Brokerage.OnMessage(): " + e);
                }
                else
                {
                    Log.Trace("Brokerage.OnMessage(): " + e);
                }

                Message?.Invoke(this, e);
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }
    }
}
