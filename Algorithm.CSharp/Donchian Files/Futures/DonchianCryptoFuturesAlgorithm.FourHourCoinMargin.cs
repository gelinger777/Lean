using System;
using System.Collections.Generic;
using System.Linq;
using Binance.Net.Enums;
using Binance.Net.Objects.Futures.FuturesData;
using QuantConnect.Configuration;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.CSharp
{
    public partial class DonchianCryptoFuturesAlgorithm
    {
        private string coinDipPurchase = "ALL";
        private decimal _prevHigh;
        private decimal _neutralBtcTarget = 0.0252m;
        private decimal _maxBtcNeutralBalance = 2500m;
        private decimal _offExchangeFourHour = 2500m;
        private BinanceFuturesAccountAsset _btcBalance;
        private double _btcPosition = 0;
        private decimal _currentPrice = 0m;
        private decimal _btcCurrentBalance => (_btcBalance.WalletBalance + _btcBalance.UnrealizedProfit) * _currentPrice;
        private decimal _btcNeutralBalance => (_neutralBtcTarget * _currentPrice);
        private double _btcNeutralPosition => (double)_btcNeutralBalance / 100;
        private decimal FourHourTotalPortfolioValue => _offExchangeFourHour + _btcCurrentBalance;
        private decimal NeutralBalance => _btcNeutralBalance > _btcCurrentBalance ? 0 : Math.Max(_btcNeutralBalance - _btcCurrentBalance, -_maxBtcNeutralBalance);
        private double NeutralPosition => (double)NeutralBalance / 100;
        private double CurrentPosition => _btcPosition - NeutralPosition;
        public void ProcessFourHourData(TradeBar data)
        {
            if (!LiveMode)
                return;

            try
            {
                if (_btcBalance == null)
                {
                    _prevHigh = data.High;
                    UpdateCoinBalance();
                }
                if (data.High > _prevHigh)
                {
                    _prevHigh = data.High;
                    coinDipPurchase = "ALL";
                }
                SymbolInfo[data.Symbol].OBV_FourHour.Update(data);
                SymbolInfo[data.Symbol].ATR_FourHour.Update(data);
                SymbolInfo[data.Symbol].Donchian_FourHour.Update(data);
                if (!SymbolInfo[data.Symbol].ATR_FourHour.IsReady)
                    return;
                if (!IsWarmingUp)
                {
                    _currentPrice = Securities["BTCUSDT"].Price;
                    Log("DonchianCryptoFuturesAlgorithm.ProcessFourHourData():Processing Four Hour Trade Data");
                    UpdateCoinBalance();
                }
                var OpenShortEntry = SymbolInfo[data.Symbol].Donchian_FourHour.LowerBand.Current.Value;
                var OpenLongEntry = SymbolInfo[data.Symbol].Donchian_FourHour.UpperBand.Current.Value;

                if (data.Close < SymbolInfo[data.Symbol].VWAP_week.Current.Value && SymbolInfo[data.Symbol].fourHourTargetPosition > 0)
                {
                    SymbolInfo[data.Symbol].fourHourTargetPosition = 0;
                    if (!IsWarmingUp)
                    {
                        Log($"DonchianCryptoFuturesAlgorithm.ProcessFourHourData(): Closing Open long Position, target = {SymbolInfo[data.Symbol].fourHourTargetPosition / 100}, positionTotal = {_btcPosition}, neutralized Position = {CurrentPosition}");
                    }
                }
                else if (data.Close > SymbolInfo[data.Symbol].VWAP_week.Current.Value && SymbolInfo[data.Symbol].fourHourTargetPosition < 0)
                {
                    SymbolInfo[data.Symbol].fourHourTargetPosition = 0;
                    if (!IsWarmingUp)
                    {
                        Log($"DonchianCryptoFuturesAlgorithm.ProcessFourHourData(): Closing Open short Position, target = {SymbolInfo[data.Symbol].fourHourTargetPosition / 100}, positionTotal = {_btcPosition}, neutralized Position = {CurrentPosition}");
                    }
                }

                //////////////////////if (index.TradeDirection == Direction.Long || index.TradeDirection == Direction.Both)
                //{
                if (data.Close >= OpenLongEntry && data.Close > SymbolInfo[data.Symbol].VWAP_week.Current.Value
                    && SymbolInfo[data.Symbol].OBV_FourHour.Current.Value > SymbolInfo[data.Symbol].OBV_FourHour.ChannelHigh
                    && SymbolInfo[data.Symbol].fourHourTargetPosition <= 0)
                {
                    SymbolInfo[data.Symbol].fourHourTargetPosition = (double)Math.Max(1, (2m / 100m) * FourHourTotalPortfolioValue / SymbolInfo["BTCUSDT"].ATR_FourHour.Current.Value * Securities["BTCUSDT"].Price);
                    if (!IsWarmingUp)
                    {
                        Log($"DonchianCryptoFuturesAlgorithm.ProcessFourHourData(): Opening long Position, target = {SymbolInfo[data.Symbol].fourHourTargetPosition / 100}, positionTotal = {_btcPosition}, neutralized Position = {CurrentPosition}");
                        SetHoldingsToTargetValue();
                    }
                    return;
                }
                //}

                ///////////////////////if (index.TradeDirection == Direction.Short || index.TradeDirecon == Direction.Both)
                //{
                if (data.Close <= OpenShortEntry && data.Close < SymbolInfo[data.Symbol].VWAP_week.Current.Value
                    && SymbolInfo[data.Symbol].OBV_FourHour.Current.Value < SymbolInfo[data.Symbol].OBV_FourHour.ChannelLow
                    && SymbolInfo[data.Symbol].fourHourTargetPosition >= 0)
                {
                    SymbolInfo[data.Symbol].fourHourTargetPosition = (double)-Math.Max(1, (2m / 100m) * (FourHourTotalPortfolioValue) / SymbolInfo["BTCUSDT"].ATR_FourHour.Current.Value * Securities["BTCUSDT"].Price);
                    if (!IsWarmingUp)
                    {
                        Log($"DonchianCryptoFuturesAlgorithm.ProcessFourHourData(): Opening short Position, target = {SymbolInfo[data.Symbol].fourHourTargetPosition / 100}, positionTotal = {_btcPosition}, neutralized Position = {CurrentPosition}");
                        SetHoldingsToTargetValue();
                    }
                    return;
                }

                if (!IsWarmingUp && SymbolInfo[data.Symbol].fourHourTargetPosition != 0)
                    Log($"DonchianCryptoFuturesAlgorithm.ProcessFourHourData(): No Trade Required, target = {SymbolInfo[data.Symbol].fourHourTargetPosition / 100}, positionTotal = {_btcPosition}, neutralized Position = {CurrentPosition}");
                else if (!IsWarmingUp && SymbolInfo[data.Symbol].fourHourTargetPosition == 0)
                {
                    Log($"DonchianCryptoFuturesAlgorithm.ProcessFourHourData(): No position, Ensuring neutral position, target = {SymbolInfo[data.Symbol].fourHourTargetPosition / 100}, positionTotal = {_btcPosition}, neutralized Position = {CurrentPosition}");
                    SetHoldingsToTargetValue();
                }
                //}
            }
            catch (Exception e)
            {
                Debug("Error Processing ProcessFourHourData(): - " + e.Message + " - " + e.StackTrace);
            }
        }
        public void UpdateCoinBalance()
        {
            if (!LiveMode)
                return;

            var balances = _apiClient.FuturesCoin.Account.GetAccountInfo();
            var positions = _apiClient.FuturesCoin.GetPositionInformation("BTC");
            var price = _apiClient.Spot.Market.GetPrice("BTCUSDT");
            Config.Reset();
            _offExchangeFourHour = Config.GetValue("CoinFuturesOffExchangeValue", _offExchangeFourHour);
            _neutralBtcTarget = Config.GetValue("BtcCoinTarget", _neutralBtcTarget);
            _maxBtcNeutralBalance = Config.GetValue("MaxNeutralValue", _maxBtcNeutralBalance);
            if (!balances.Success || !positions.Success)
            {
                Error($"DonchianCryptoFuturesAlgorithm.NeutralFourHourHoldings(): Unable to retreived balances or positions");
                return;
                //Email Notification
            }
            _currentPrice = price.Success ? price.Data.Price : Securities["BTCUSDT"].Price;
            if (balances.Data.Assets.Where(x => x.Asset == "BTC").Any())
            {
                _btcBalance = balances.Data.Assets.Where(x => x.Asset == "BTC").First();
            }
            _btcPosition = positions.Data.Any() ? (double)positions.Data.First(x => x.Symbol == "BTCUSD_PERP").Quantity : _btcPosition;
        }
        public void NeutralFourHourHoldings(string symbol)
        {
            if (!LiveMode)
                return;

            Log($"DonchianCryptoFuturesAlgorithm.NeutralFourHourHoldings(): Setting Neutral Coin holding Position");
            var balances = _apiClient.FuturesCoin.Account.GetAccountInfo();
            var positions = _apiClient.FuturesCoin.GetPositionInformation("BTC");
            if (!balances.Success || !positions.Success)
            {
                Error($"DonchianCryptoFuturesAlgorithm.NeutralFourHourHoldings(): Unable to retreived balances or positions");
                return;
                //Email Notification
            }

            // set the actual balance info
            if (balances.Data.Assets.Where(x => x.Asset == "BTC").Any())
            {
                _btcBalance = balances.Data.Assets.Where(x => x.Asset == "BTC").First();
            }

            // set the actual coin contract position
            if (positions.Data.Any())
            {
                _btcPosition = (double)positions.Data.First(x => x.Symbol == "BTCUSD_PERP").Quantity;
            }

            var side = CurrentPosition > 0 ? OrderSide.Sell : OrderSide.Buy;
            var orderQty = Math.Abs(CurrentPosition);

            if (orderQty != 0)
            {
                Log($"DonchianCryptoFuturesAlgorithm.NeutralFourHourHoldings(): Placing Neutral Order of {orderQty}");
                var neutralOrder = _apiClient.FuturesCoin.Order.PlaceOrder("BTCUSD_PERP", side, OrderType.Market, (int)orderQty);
                var msg = $"Contract Target Position = {SymbolInfo["BTCUSDT"].fourHourTargetPosition} || ";
                if (neutralOrder.Success)
                    Notify.Email("christopherjholley23@gmail.com", $"{currentAccount}: Four Hour Trade", msg + $"Successful neutralizing order of {orderQty} contracts of BTCUSD_PERP, neutral position: {NeutralPosition}");
                else
                    Notify.Email("christopherjholley23@gmail.com", $"{currentAccount}: FAILED Four Hour Trade", msg + $"FAILED neutralizing order of {orderQty} contracts of BTCUSD_PERP, manual purchase required, target neutral position: {NeutralPosition}");
            }
        }
        public void CloseOpenFourHourPosition(string symbol)
        {
            if (!LiveMode)
                return;

            NeutralFourHourHoldings(symbol);
        }
        public void SetHoldingsToTargetValue(bool updateCoinBalance = false)
        {
            if (!LiveMode)
                return;

            if (!IsWarmingUp && updateCoinBalance)
                UpdateCoinBalance();
            if (SymbolInfo["BTCUSDT"].fourHourTargetPosition == 0)
                NeutralFourHourHoldings("BTCUSDT");
            else if (SymbolInfo["BTCUSDT"].fourHourTargetPosition > 0 && !updateCoinBalance)
                PlaceFourHourBuyOrder("BTCUSDT");
            else if (SymbolInfo["BTCUSDT"].fourHourTargetPosition < 0 && !updateCoinBalance)
                PlaceFourHourSellOrder("BTCUSDT");
        }
        public void PlaceFourHourBuyOrder(string i)
        {
            if (!LiveMode)
                return;

            var qty = Math.Ceiling((SymbolInfo["BTCUSDT"].fourHourTargetPosition / 100) - CurrentPosition);
            if (qty > 0)
            {
                Log($"DonchianCryptoFuturesAlgorithm.PlaceFourHourBuyOrder(): Placing Buy Order of {qty}");
                var buy = _apiClient.FuturesCoin.Order.PlaceOrder("BTCUSD_PERP", OrderSide.Buy, OrderType.Market, (int)qty);
                var msg = $"Contract Target Position = {SymbolInfo["BTCUSDT"].fourHourTargetPosition} || ";
                if (buy.Success)
                    Notify.Email("christopherjholley23@gmail.com", $"{currentAccount}: Four Hour Trade", msg + $"Successful buy of {qty} contracts of BTCUSD_PERP");
                else
                    Notify.Email("christopherjholley23@gmail.com", $"{currentAccount}: FAILED Four Hour Trade", msg + $"FAILED buy of {qty} contracts of BTCUSD_PERP, manual purchase required");
            }

        }
        public void PlaceFourHourSellOrder(string i)
        {
            if (!LiveMode)
                return;

            var qty = Math.Abs(Math.Floor((SymbolInfo["BTCUSDT"].fourHourTargetPosition / 100) - CurrentPosition));
            if (qty > 0)
            {
                Log($"DonchianCryptoFuturesAlgorithm.PlaceFourHourBuyOrder(): Placing Sell Order of {qty}");
                var sell = _apiClient.FuturesCoin.Order.PlaceOrder("BTCUSD_PERP", OrderSide.Sell, OrderType.Market, (int)qty);
                var msg = $"Contract Target Position = {SymbolInfo["BTCUSDT"].fourHourTargetPosition} || ";
                if (sell.Success)
                    Notify.Email("christopherjholley23@gmail.com", $"{currentAccount}: Four Hour Trade", msg + $"Successful sell of {qty} contracts of BTCUSD_PERP");
                else
                    Notify.Email("christopherjholley23@gmail.com", $"{currentAccount}: FAILED Four Hour Trade", msg + $"FAILED sell of {qty} contracts of BTCUSD_PERP, manual purchase required");
            }
        }
        public void SetDipPurchases(bool Initialize, DateTime time)
        {
            if (!LiveMode)
                return;

            if (coinDipPurchase == "None")
                return;

            var info = _apiClient.FuturesCoin.System.GetExchangeInfo();
            var frontContract = "BTC";
            if (info.Success)
            {
                var symbols = info.Data.Symbols.Where(x => x.BaseAsset == "BTC").ToList();
                foreach (var sym in symbols)
                {
                    if (sym.ContractType == ContractType.Perpetual)
                        continue;

                    if (Initialize || !IsWarmingUp)
                    {
                        var cancel = _apiClient.FuturesCoin.Order.CancelAllOrders(sym.Name);
                        if (!cancel.Success)
                            break;
                    }

                    if (sym.ContractType == ContractType.CurrentMonth)
                    {

                        break;
                    }
                    else if (sym.ContractType == ContractType.CurrentQuarter)
                    {
                        var klines = _apiClient.FuturesCoin.Market.GetKlines(sym.Name, KlineInterval.FourHour, endTime: time, limit: 100);
                        if (klines.Success)
                        {
                            var high = klines.Data.Max(x => x.High);
                            var one = Math.Round(high * 0.9m, 1);
                            var two = Math.Round(high * 0.8m, 1);
                            var three = Math.Round(high * 0.7m, 1);
                            var orders = new List<BinanceFuturesBatchOrder>();
                            var close = klines.Data.ToList()[99].Close;
                            var lowSinceHigh = klines.Data.Where(x => x.CloseTime > klines.Data.Where(z => z.High == high).Select(y => y.CloseTime).First()).Min(y => y.Low);
                            int qty = 1;
                            if (close > three && (coinDipPurchase == "ALL" || coinDipPurchase == "Two" || coinDipPurchase == "One"))
                                orders.Add(new BinanceFuturesBatchOrder()
                                {
                                    Side = OrderSide.Buy,
                                    Symbol = sym.Name,
                                    Price = three,
                                    Quantity = 15,
                                    Type = OrderType.Limit,
                                    TimeInForce = TimeInForce.GoodTillCancel
                                });
                            else if (lowSinceHigh < three)
                                coinDipPurchase = "None";
                            if (close > two && (coinDipPurchase == "ALL" || coinDipPurchase == "Two"))
                                orders.Add(new BinanceFuturesBatchOrder()
                                {
                                    Side = OrderSide.Buy,
                                    Symbol = sym.Name,
                                    Price = two,
                                    Quantity = 10,
                                    Type = OrderType.Limit,
                                    TimeInForce = TimeInForce.GoodTillCancel
                                });
                            else if (lowSinceHigh < two)
                                coinDipPurchase = "One";
                            if (close > one && coinDipPurchase == "ALL")
                                orders.Add(new BinanceFuturesBatchOrder()
                                {
                                    Side = OrderSide.Buy,
                                    Symbol = sym.Name,
                                    Price = one,
                                    Quantity = 5,
                                    Type = OrderType.Limit,
                                    TimeInForce = TimeInForce.GoodTillCancel
                                });
                            else if (lowSinceHigh < one)
                                coinDipPurchase = "Two";

                            if (orders.Count > 0 && (Initialize || !IsWarmingUp))
                            {
                                var order = _apiClient.FuturesCoin.Order.PlaceMultipleOrders(orders.ToArray());
                                if (!order.Success)
                                    break;
                            }
                        }
                        break;
                    }
                }
            }
        }
    }
}
