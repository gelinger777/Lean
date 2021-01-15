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

using QuantConnect.Brokerages.BinanceFutures.Messages;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace QuantConnect.Brokerages.BinanceFutures
{
    public partial class BinanceFuturesBrokerage
    {
        private readonly ConcurrentQueue<WebSocketMessage> _messageBuffer = new ConcurrentQueue<WebSocketMessage>();
        private volatile bool _streamLocked;
        private readonly IDataAggregator _aggregator;
        /// <summary>
        /// Locking object for the Ticks list in the data queue handler
        /// </summary>
        protected readonly object TickLocker = new object();

        /// <summary>
        /// Lock the streaming processing while we're sending orders as sometimes they fill before the REST call returns.
        /// </summary>
        private void LockStream()
        {
            _streamLocked = true;
        }

        /// <summary>
        /// Unlock stream and process all backed up messages.
        /// </summary>
        private void UnlockStream()
        {
            while (_messageBuffer.Any())
            {
                WebSocketMessage e;
                _messageBuffer.TryDequeue(out e);

                OnMessageImpl(e);
            }

            // Once dequeued in order; unlock stream.
            _streamLocked = false;
        }

        private void WithLockedStream(Action code)
        {
            try
            {
                LockStream();
                code();
            }
            finally
            {
                UnlockStream();
            }
        }

        private void OnMessageImpl(WebSocketMessage e)
        {
            try
            {
                var msg = BaseMessage.Parse(e.Message);
                if (msg != null)
                {
                    switch (msg.Event)
                    {
                        case EventType.Execution:
                            var upd = msg.ToObject<Execution>();
                            if (upd.Executions.ExecutionType.Equals("TRADE", StringComparison.OrdinalIgnoreCase))
                            {
                                OnFillOrder(upd);
                            }
                            break;
                        case EventType.OrderBook:
                            var updates = msg.ToObject<OrderBookUpdateMessage>();
                            //OnOrderBookUpdate(updates);
                            break;
                        case EventType.Trade:
                            var trade = msg.ToObject<Trade>();
                            EmitTradeTick(
                                _symbolMapper.GetLeanSymbol(trade.Symbol, SecurityType.Equity, Market.Binance),
                                Time.UnixMillisecondTimeStampToDateTime(trade.Time),
                                trade.Price,
                                trade.Quantity
                            );
                            break;
                        default:
                            return;
                    }
                }
            }
            catch (Exception exception)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, -1, $"Parsing wss message failed. Data: {e.Message} Exception: {exception}"));
                throw;
            }
        }

        private void EmitTradeTick(Symbol symbol, DateTime time, decimal price, decimal quantity)
        {
            var tick = new Tick
            {
                Symbol = symbol,
                Value = price,
                Quantity = Math.Abs(quantity),
                Time = time,
                TickType = TickType.Trade
            };

            lock (TickLocker)
            {
                _aggregator.Update(tick);
            }
        }

        private void OnFillOrder(Execution data)
        {
            try
            {
                var order = FindOrderByExternalId(data.Executions.OrderId);
                if (order == null)
                {
                    // not our order, nothing else to do here
                    return;
                }

                var symbol = _symbolMapper.GetLeanSymbol(data.Executions.Symbol, SecurityType.Equity, Market.Binance);
                var fillPrice = data.Executions.LastExecutedPrice;
                var fillQuantity = data.Executions.Side == "SELL" ? -1 * Math.Abs(data.Executions.LastExecutedQuantity) : data.Executions.LastExecutedQuantity;
                var updTime = Time.UnixMillisecondTimeStampToDateTime(data.Executions.TransactionTime);
                var orderFee = new OrderFee(new CashAmount(data.Executions.Fee, data.Executions.FeeCurrency));
                var status = ConvertOrderStatus(data.Executions.OrderStatus);
                var orderEvent = new OrderEvent
                (
                    order.Id, symbol, updTime, status,
                    data.Executions.Direction, fillPrice, fillQuantity,
                    orderFee, $"Binance Order Event {data.Executions.Direction}"
                );

                if (status == OrderStatus.Filled)
                {
                    Orders.Order outOrder;
                    CachedOrderIDs.TryRemove(order.Id, out outOrder);
                }

                OnOrderEvent(orderEvent);
            }
            catch (Exception e)
            {
                Log.Error(e);
                throw;
            }
        }

        private Orders.Order FindOrderByExternalId(string brokerId)
        {
            var order = CachedOrderIDs
                    .FirstOrDefault(o => o.Value.BrokerId.Contains(brokerId))
                    .Value;
            if (order == null)
            {
                order = _algorithm.Transactions.GetOrderByBrokerageId(brokerId);
            }

            return order;
        }
    }
}
