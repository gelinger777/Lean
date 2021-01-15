using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Binance.Net;
using Binance.Net.Objects.Spot;
using CryptoExchange.Net.Authentication;
using QuantConnect.Configuration;
using CoinMarketCap;
using CoinMarketCap.Models.Global;
using CoinMarketCap.Models.Cryptocurrency;
using Binance.Net.Objects.Spot.LendingData;
using System.Threading;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Sheets.v4;
using System.IO;
using Google.Apis.Services;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using CsvHelper;
using BinanceFuturesIncomeHistory = Binance.Net.Objects.Futures.FuturesData.BinanceFuturesIncomeHistory;
using System.Globalization;
using QuantConnect.Indicators;
using Binance.Net.Interfaces;

namespace QuantConnect.Algorithm.CSharp
{
    public partial class DonchianCryptoFuturesAlgorithm
    {
        private BinanceClient _apiClient;
        private List<Binance.Net.Objects.Futures.MarketData.BinanceFuturesSymbolBracket> _symbolLimits;
        private CoinMarketCapClient _coinmktcap;
        private Dictionary<string, BinanceSavingsProduct> productIdDict = new Dictionary<string, BinanceSavingsProduct>();
        private bool _productIdInitialize = true;
        static string[] Scopes = { SheetsService.Scope.SpreadsheetsReadonly };
        static string ApplicationName = "Google Sheets API .NET Quickstart";

        /// <summary>
        /// Initializes the API for the savings account, Disperses any USDT in the spot wallet, and checks the savings USDT value
        /// </summary>
        public void InitializeBinanceSavingsAccount()
        {
            Log("DonchianCryptoFuturesAlgorithm.InitializeBinanceSavingsAccount(): Setting Credentials for Account Transfers");
            BinanceClient.SetDefaultOptions(new BinanceClientOptions()
            {
                ApiCredentials = new ApiCredentials(Config.Get(currentAccount + "-api-key"), Config.Get(currentAccount + "-api-secret"))
            });

            BinanceSocketClient.SetDefaultOptions(new BinanceSocketClientOptions()
            {
                ApiCredentials = new ApiCredentials(Config.Get(currentAccount + "-api-key"), Config.Get(currentAccount + "-api-secret")),
            });
            _apiClient = new BinanceClient();

            var brackets = _apiClient.FuturesUsdt.GetBrackets();
            if (brackets.Success)
                _symbolLimits = brackets.Data.ToList();

            SetDipPurchases(true, DateTime.UtcNow);
            /* Log("DonchianCryptoFuturesAlgorithm.InitializeBinanceSavingsAccount(): Checking spot wallet for any unallocated USDT");
             var spot = SpotWalletUSDTTransfer();

             if (!spot)
             {
                 var msg = ("DonchianCryptoFuturesAlgorithm.InitializeBinanceSavingsAccount(): Unable to properly transfer USDT from spot wallet");
                 Log(msg);
                 //Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: InitializeBinanceSavingsAccount", msg);
             }*/

            _coinmktcap = new CoinMarketCapClient("d09423f8-326d-4fbf-8122-d4c5e295422b");
        }

        public void AddSymbols()
        {
            if (LiveMode)
            {
                var syms = _apiClient.FuturesUsdt.Market.GetAllPrices();
                if (syms.Success)
                {
                    symbols = syms.Data.ToList().Select(x => x.Symbol).ToList();
                }
                else
                {
                    throw new ArgumentException("Unable to retreive symbols list for initializtion");
                }
            }
            else
            {
                //TODO: Add all securities we have history for
            }
        }

        public void ResetBinanceTimestamp()
        {
            Log("ResetBinanceTimestamp(): Resetting Timestamp For transfer client");
            _apiClient.FuturesUsdt.System.GetServerTime(true);
        }
        /// <summary>
        /// Retreives the USDT specific holdings in the binance savings account
        /// </summary>
        /// <returns>Quantity of USDT in the savings account</returns>
        public decimal GetBinanceSavingsAccountUSDTHoldings()
        {
            var balance = SavingsAccountUSDTHoldings;

            var savingsAccount = _apiClient.Lending.GetFlexibleProductPosition("USDT");
            if (savingsAccount.Success)
            {
                balance = savingsAccount.Data.Any(x => x.Asset == "USDT") ? savingsAccount.Data.Where(x => x.Asset == "USDT").FirstOrDefault().TotalAmount : 0;
            }
            else
            {
                var msg = ($"DonchianCryptoFuturesAlgorithm.GetBinanceSavingsAccountUSDTValue(): Unable to retreive Savings Account USDT value, reason = {savingsAccount.Error}");
                Log(msg);
                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: GetBinanceSavingsAccountUSDTValue", msg);
            }
            return balance;
        }

        public decimal GetBinanceSwapAccountUSDHoldings()
        {
            var balance = SwapAccountUSDHoldings;

            var swapAccount = _apiClient.BSwap.GetPoolLiquidityInfoAsync();
            if (swapAccount.Result.Success)
            {
                var shares = swapAccount.Result.Data.Select(x => x.Share).ToList();
                var assets = new Dictionary<string, decimal>();
                foreach (var share in shares)
                {
                    foreach (var bal in share.Asset)
                    {
                        var price = 1m;
                        if (bal.Key != "USDT" && bal.Value != 0)
                        {
                            var check = _apiClient.Spot.Market.GetPrice((bal.Key == "WBTC" ? "BTC" : bal.Key) + "USDT");
                            if (check.Success)
                                price = check.Data.Price;
                        }
                        if (assets.ContainsKey(bal.Key))
                            assets[bal.Key] += bal.Value * price;
                        else
                            assets.Add(bal.Key, bal.Value * price);
                    }
                    balance = assets.Values.Sum();
                }
            }
            else
            {
                var msg = ($"DonchianCryptoFuturesAlgorithm.GetBinanceSwapAccountUSDHoldings(): Unable to retreive Swap Account USDT value, reason = {swapAccount.Result.Error}");
                Log(msg);
                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: GetBinanceSwapAccountUSDHoldings", msg);
            }
            return balance;
        }

        /// <summary>
        /// Retreives the USDT value in the binance savings account
        /// </summary>
        /// <returns>Quantity of USDT in the savings account</returns>
        public decimal GetBinanceTotalSavingsAccountNonUSDValue()
        {
            var balance = SavingsAccountNonUSDTValue;

            var savingsAccount = _apiClient.Lending.GetLendingAccount();
            if (savingsAccount.Success)
            {
                balance = savingsAccount.Data.TotalAmountInUSDT - SavingsAccountUSDTHoldings;
            }
            else
            {
                var msg = ($"DonchianCryptoFuturesAlgorithm.GetBinanceSavingsAccountUSDTValue(): Unable to retreive Savings Account USDT value, reason = {savingsAccount.Error}");
                Log(msg);
                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: GetBinanceSavingsAccountUSDTValue", msg);
            }
            return balance;
        }

        /// <summary>
        /// Retreives the USDT value in the binance pool account
        /// </summary>
        /// <returns>Quantity of USDT in the savings account</returns>
        public decimal GetBinanceTotalPoolAccountUSDValue()
        {
            var balance = SavingsAccountNonUSDTValue;

            var savingsAccount = _apiClient.Lending.GetLendingAccount();
            if (savingsAccount.Success)
            {
                balance = savingsAccount.Data.TotalAmountInUSDT - SavingsAccountUSDTHoldings;
            }
            else
            {
                var msg = ($"DonchianCryptoFuturesAlgorithm.GetBinanceSavingsAccountUSDTValue(): Unable to retreive Savings Account USDT value, reason = {savingsAccount.Error}");
                Log(msg);
                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: GetBinanceSavingsAccountUSDTValue", msg);
            }
            return balance;
        }
        
        public bool CheckForDepositsWithdrawals()
        {
            var depdata = _apiClient.WithdrawDeposit.GetDepositHistory(startTime: _prevDepostCheckDate);
            if (depdata.Success)
            {
                if (depdata.Data.Any())
                {
                    var deposits = depdata.Data;
                    var depamount = 0m;
                    foreach (var deposit in deposits)
                    {
                        depamount += _apiClient.Spot.Market.GetPrice(deposit.Coin).Data.Price * deposit.Amount;
                    }
                    SaveTotalAccountValue("DEPOSIT");
                }
                _prevDepostCheckDate = Time;
            }
            var withdraws = _apiClient.WithdrawDeposit.GetWithdrawalHistory(startTime: _prevDepostCheckDate);
            return true;
        }
        public bool IsSpotExchangeActive()
        {
            var active = false;
            var check = _apiClient.Spot.System.GetSystemStatus();
            if (check.Success)
                if (check.Data.Status == Binance.Net.Enums.SystemStatus.Normal)
                    active = true;

            return active;
        }

        /// <summary>
        /// Checks the spot account for USDT and transfers between futures and trading accounts
        /// </summary>
        /// <returns>True if the transfer was successful</returns>
        public bool SpotWalletUSDTTransfer()
        {
            var spot = _apiClient.General.GetUserCoins();
            if (!spot.Success)
            {
                if (spot.Error.Code == -1021)
                    spot = _apiClient.General.GetUserCoins();
            }
            if (spot.Success)
            {
                var amount = spot.Data.Where(x => x.Coin == "USDT")?.Select(balance => balance.Free).Sum();
                if (amount >= 10)
                {
                    var tradeAccountTarget = (TotalPortfolioValue + (decimal)amount) * 0.4m;
                    var t_amount = Math.Min((decimal)amount, Math.Max(0, tradeAccountTarget - Portfolio.TotalPortfolioValue));
                    var s_amount = (decimal)amount - t_amount;

                    var productID = GetOrAddSavingsProductID("USDT");

                    var transfer = false;
                    if (t_amount > 0)
                    {
                        var t = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", t_amount, Binance.Net.Enums.FuturesTransferType.FromSpotToUsdtFutures);
                        transfer = t.Success;
                    }
                    if (s_amount >= GetOrAddMinimumSavingsAmount("USDT") && productID != "")
                    {
                        try
                        {
                            var savings = _apiClient.Lending.PurchaseFlexibleProduct(productID, s_amount);
                            if (transfer && savings.Success)
                                return true;
                            else if (transfer)
                            {
                                var msg = ($"DonchianCryptoFuturesAlgorithm.SpotWalletUSDTTransfer(): Unable to transfer to Savings account, reason = {savings.Error}");
                                Log(msg);
                                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: SpotWalletUSDTTransfer", msg);
                            }
                        }
                        catch (Exception e)
                        {
                            Log(e.ToString());
                        }
                    }
                    return transfer;
                }
                else
                    return true;
            }
            else
            {
                var msg = ($"DonchianCryptoFuturesAlgorithm.SpotWalletUSDTTransfer(): Unable to retreive Spot Account holdings, reason = {spot.Error}");
                Log(msg);
                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: SpotWalletUSDTTransfer", msg);
            }
            return false;
        }

        public bool SpotWalletToSavingsTransfer()
        {
            var spot = _apiClient.General.GetUserCoins();
            if (!spot.Success)
            {
                if (spot.Error.Code == -1021)
                    spot = _apiClient.General.GetUserCoins();
            }
            if (spot.Success)
            {
                var msg = ($"SpotWalletToSavingsTransfer(): Initiating Transfer of spot coins");
                var notify = false;
                foreach (var coin in spot.Data.Where(y => y.Free > 0 && !(new[] { "ATD", "EON", "EOP", "ADD", "MEETONE" }).Contains(y.Coin, StringComparer.OrdinalIgnoreCase)).Select(x => x.Coin).ToList())
                {
                    var amount = spot.Data.Where(x => x.Coin == coin)?.Select(balance => balance.Free).Sum();
                    if (amount >= GetOrAddMinimumSavingsAmount(coin))
                    {
                        var productID = GetOrAddSavingsProductID(coin);

                        if (productID != "")
                        {
                            var savings = _apiClient.Lending.PurchaseFlexibleProduct(productID, (decimal)amount);
                            if (savings.Success)
                                msg += $": Transfer of {amount} {coin} successful";
                            else
                            {
                                msg += $": FAILED Transfer of {amount} {coin}, reason = {savings.Error}";
                                notify = true;
                            }
                        }
                        else
                            msg += $": FAILED Transfer of {amount} {coin}, reason = No savings product exists";
                    }
                    else
                        msg += $": FAILED Transfer of {amount} {coin}, reason = Less than minimum savings amount";
                }
                Log(msg);
                if (notify)
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + " | SpotWalletToSavingsTransfer", msg);

                return true;
            }
            else
            {
                var msg = ($"DonchianCryptoFuturesAlgorithm.SpotWalletUSDTTransfer(): Unable to retreive Spot Account holdings, reason = {spot.Error}");
                Log(msg);
                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: SpotWalletUSDTTransfer", msg);
            }
            return false;
        }

        /// <summary>
        /// Transfers USDT between futures and savings accounts
        /// </summary>
        /// <param name="direction">Direction of the transfer (to/from savings/futures)</param>
        /// <param name="quantity">Quantity of USDT to transfer</param>
        /// <returns>Id's for the placed order</returns>
        public bool FuturesUSDTSavingsTransfer(TransferDirection direction, decimal quantity)
        {
            if (direction == TransferDirection.FuturesToSavings)
            {
                //Futures to savings
                var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", quantity, Binance.Net.Enums.FuturesTransferType.FromUsdtFuturesToSpot);
                if (transfer.Success)
                {
                    var productID = GetOrAddSavingsProductID("USDT");
                    if (productID != "" && quantity >= GetOrAddMinimumSavingsAmount("USDT"))
                    {
                        return SpotWalletUSDTTransfer();
                    }
                }
                else
                {
                    var msg = $"DonchianCryptoFuturesAlgorithm.FuturesUSDTSavingsTransfer(): Unable to Transfer ${quantity} from Futures to spot account, reason = {transfer.Error}";
                    Log(msg);
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: FuturesUSDTSavingsTransfer", msg);
                }
            }
            else if (direction == TransferDirection.SavingsToFutures)
            {
                //savings to futures
                var productID = GetOrAddSavingsProductID("USDT");
                if (productID != "")
                {
                    var redQuote = _apiClient.Lending.GetLeftDailyRedemptionQuotaOfFlexibleProduct(productID, Binance.Net.Enums.RedeemType.Fast);
                    if (redQuote.Success)
                    {
                        quantity = quantity > redQuote.Data.LeftQuota ? redQuote.Data.LeftQuota : quantity;
                        if (quantity < redQuote.Data.MinimalRedemptionAmount)
                            return false;
                        var savings = _apiClient.Lending.RedeemFlexibleProduct(productID, quantity, Binance.Net.Enums.RedeemType.Fast);
                        if (savings.Success)
                        {
                            var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", quantity, Binance.Net.Enums.FuturesTransferType.FromSpotToUsdtFutures);
                            if (!transfer.Success)
                            {
                                var msg = $"DonchianCryptoFuturesAlgorithm.FuturesUSDTSavingsTransfer(): Unable to Transfer from spot to futures account, reason = {transfer.Error}";
                                Log(msg);
                                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: FuturesUSDTSavingsTransfer", msg);
                            }
                            SavingsAccountUSDTHoldings -= quantity;
                            return transfer.Success;
                        }
                        else
                        {
                            var msg = ($"DonchianCryptoFuturesAlgorithm.FuturesUSDTSavingsTransfer(): Unable to Transfer from savings to spot account, reason = {savings.Error}");
                            Log(msg);
                            Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: FuturesUSDTSavingsTransfer", msg);
                        }
                    }
                }
            }
            else if (direction == TransferDirection.SwapToFutures)
            {
                //swap to futures
                var productID = GetOrAddSwapProductID("USDT");
                if (productID != "")
                {
                    var redQuote = _apiClient.Lending.GetLeftDailyRedemptionQuotaOfFlexibleProduct(productID, Binance.Net.Enums.RedeemType.Fast);
                    if (redQuote.Success)
                    {
                        quantity = quantity > redQuote.Data.LeftQuota ? redQuote.Data.LeftQuota : quantity;
                        if (quantity < redQuote.Data.MinimalRedemptionAmount)
                            return false;
                        var savings = _apiClient.Lending.RedeemFlexibleProduct(productID, quantity, Binance.Net.Enums.RedeemType.Fast);
                        if (savings.Success)
                        {
                            var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", quantity, Binance.Net.Enums.FuturesTransferType.FromSpotToUsdtFutures);
                            if (!transfer.Success)
                            {
                                var msg = $"DonchianCryptoFuturesAlgorithm.FuturesUSDTSavingsTransfer(): Unable to Transfer from spot to futures account, reason = {transfer.Error}";
                                Log(msg);
                                Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: FuturesUSDTSavingsTransfer", msg);
                            }
                            SavingsAccountUSDTHoldings -= quantity;
                            return transfer.Success;
                        }
                        else
                        {
                            var msg = ($"DonchianCryptoFuturesAlgorithm.FuturesUSDTSavingsTransfer(): Unable to Transfer from savings to spot account, reason = {savings.Error}");
                            Log(msg);
                            Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: FuturesUSDTSavingsTransfer", msg);
                        }
                    }
                }
            }
            else if (direction == TransferDirection.FuturesToSwap)
            {//TODO transfer to swap
                //Futures to swap
                var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", quantity, Binance.Net.Enums.FuturesTransferType.FromUsdtFuturesToSpot);
                if (transfer.Success)
                {
                    var productID = GetOrAddSwapProductID("USDT");
                    if (productID != "" && quantity >= GetOrAddMinimumSwapAmount("USDT"))
                    {
                        return SpotWalletUSDTTransfer();
                    }
                }
                else
                {
                    var msg = $"DonchianCryptoFuturesAlgorithm.FuturesUSDTSavingsTransfer(): Unable to Transfer ${quantity} from Futures to spot account, reason = {transfer.Error}";
                    Log(msg);
                    Notify.Email("christopherjholley23@gmail.com", currentAccount + " | Error: FuturesUSDTSavingsTransfer", msg);
                }
            }
            return false;
        }

        /// <summary>
        /// Given a quantity of bitcoin this Transfers USD from the futures account, purchases bitcoin, and transfers into the savings wallet
        /// </summary>
        /// <param name="qty">Quantity of bitcoin to invest in</param>
        /// <param name="tag">Tag for the order</param>
        /// <returns>Whether or not the trade was properly executed</returns>
        public bool BitcoinInvestment(decimal qty, string tag = "")
        {//TODO need to add error checking
            // Transfer dollar amount to spot account for purchase
            var BTCOnly = Config.GetValue("BtcDipPurchaseOnly", false);
            if (qty > 10)
            {
                var tqty = qty;
                if (CoinFuturesAudit && BTCOnly)
                    tqty = tqty * .25m;

                var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", tqty, Binance.Net.Enums.FuturesTransferType.FromUsdtFuturesToSpot);
                if (!transfer.Success)
                    return false;
            }
            else
                return false;

            // If successful, place order for bitcoin + based on market cap
            var param = new ListingLatestParameters();
            param.Limit = 10;
            var msg = $"Attempting to purchase {param.Limit} crypto investments for a total of ${qty}";
            decimal btcqty = qty;
            try
            {
                if (!BTCOnly)
                {
                    var coins = _coinmktcap.GetLatestListings(param);
                    var market = _coinmktcap.GetAggregateMarketMetrics(new AggregateMarketMetricsParams());
                    foreach (var coin in coins.Data.OrderByDescending(x => x.CmcRank).Where(y => symbols.Any(x => x.Contains(y.Symbol)) && !y.Symbol.Contains("USDT") && !y.Symbol.Contains("BNB")).ToList())
                    {
                        var tradeqty = 0m;
                        if (coin.Symbol != "BTC" && coin.Symbol != "BNB")
                        {
                            var coinCap = coin.Quote.Values.Sum(x => x.MarketCap);
                            var mktcap = market.Data.Quote.Values.Sum(x => x.TotalMarketCap);
                            tradeqty = coinCap == null ? 0 : Math.Round(Math.Max(qty * .035m, qty * ((decimal)((double)coinCap / mktcap))), 2);
                            Log(tag + $" - Investing ${tradeqty} in {coin.Symbol}");
                            msg += $" - Investing ${tradeqty} in {coin.Symbol}";
                            var order = _apiClient.Spot.Order.PlaceOrder(coin.Symbol + "USDT", Binance.Net.Enums.OrderSide.Buy, Binance.Net.Enums.OrderType.Market, quoteOrderQuantity: tradeqty);
                            if (order.Success)
                            {
                                btcqty -= tradeqty;
                                msg += $" Success = true";
                            }
                            else
                                msg += $" Success = false, Error = {order.Error}";
                        }
                    }
                }
                
                // BNB Investment for fees
                var bnbqty = Math.Round(qty * .15m, 2);
                Log(tag + $" - Investing ${bnbqty} in BNB");
                msg += $" - Investing ${bnbqty} in BNB";
                var bnborder = _apiClient.Spot.Order.PlaceOrder("BNBUSDT", Binance.Net.Enums.OrderSide.Buy, Binance.Net.Enums.OrderType.Market, quoteOrderQuantity: bnbqty);
                if (bnborder.Success)
                {
                    btcqty -= bnbqty;
                    msg += $" Success = true";
                }
                else
                    msg += $" Success = false, Error = {bnborder.Error}";

                msg += $" - Investing ${btcqty} in BTC";
                if (CoinFuturesAudit && _offExchangeValues.Where(x => x.Asset == "USDT" || x.Asset == "USDC" || x.Asset == "DAI").Sum(y => y.USDTValue) / TotalPortfolioValue < 0.25m)
                {
                    Log($"DonchianCryptoFuturesAlgorithm.Supplemental.BitcoinInvestment(): Calculating and Adding BTC Target Amount = ${btcqty}");
                    var target = AddNewBitcoinTargetAmount(btcqty);
                    msg += target ? " Success = true" : $" Success = false";
                }
                else
                {
                    var btcorder = _apiClient.Spot.Order.PlaceOrder("BTCUSDT", Binance.Net.Enums.OrderSide.Buy, Binance.Net.Enums.OrderType.Market, quoteOrderQuantity: btcqty);
                    msg += btcorder.Success ? " Success = true" : $" Success = false, Error = {btcorder.Error}";
                }
            }
            catch (Exception e)
            {
                if (e.Message.Contains("404"))
                {
                    msg += $" - 404 Error - Unable to retreive CoinMarketCap info; Investing ${qty} in BTC";
                    var order = _apiClient.Spot.Order.PlaceOrder("BTCUSDT", Binance.Net.Enums.OrderSide.Buy, Binance.Net.Enums.OrderType.Market, quoteOrderQuantity: qty);
                    msg += order.Success ? " Success = true" : $" Success = false, Error = {order.Error}";
                }
            }
            // Get BTC account value and Transfer to savings account
            var spot = _apiClient.General.GetUserCoins();

            if (spot.Success)
            {
                if (_productIdInitialize)
                {
                    AddAllProductIDs(spot.Data.Select(x => x.Coin).ToList());
                    _productIdInitialize = false;
                }
                foreach (var sym in spot.Data.Where(x => x.Free > 0)?.ToList())
                {
                    if (sym.Coin == "USDT")
                        SpotWalletUSDTTransfer();

                    else
                    {
                        var s_amount = sym.Free;
                        var productID = GetOrAddSavingsProductID(sym.Coin);
                        if (productID != "" && s_amount >= GetOrAddMinimumSavingsAmount(sym.Coin))
                        {
                            var savings = _apiClient.Lending.PurchaseFlexibleProduct(productID, s_amount);
                        }
                    }
                }
                msg += " - Savings transfer of new investment complete";
                if (tag == "")
                    Log(msg);
                else
                    Log(msg); Notify.Email("christopherjholley23@gmail.com", "Bitcoin Investment " + tag, msg);
                return true;
            }
            msg += " - Error retreiving spot account info, unable to transfer to savings account";
            if (tag == "")
                Log(msg);
            else
                Log(msg); Notify.Email("christopherjholley23@gmail.com", "Bitcoin Investment " + tag, msg);
            return false;
        }
        public bool AddNewBitcoinTargetAmount(decimal dollarQty)
        {
            var success = false;
            var qty = dollarQty / Securities["BTCUSDT"].Price;
            if (_btcCurrentBalance + NeutralBalance >= dollarQty)
            {
                _neutralBtcTarget += Math.Round(qty, 6);
                _offExchangeFourHour += dollarQty;
                var maxNeutral = Config.GetValue("MaxNeutralValue", 0);
                if (maxNeutral != 0)
                    Config.SetDecimal("MaxNeutralValue", Math.Max(0, maxNeutral - dollarQty));
                else
                    Config.SetDecimal("CoinFuturesOffExchangeValue", _offExchangeFourHour);

                Log($"DonchianCryptoFuturesAlgorithm.Supplemental.AddNewBitcoinTargetAmount(): Adding BTC Target Amount To Config = {qty}, final target = {_neutralBtcTarget}");
                Config.SetDecimal("BtcCoinTarget", _neutralBtcTarget);
                Config.Write();
                success = true;
            }
            else
            {
                Log($"DonchianCryptoFuturesAlgorithm.Supplemental.AddNewBitcoinTargetAmount(): Insufficient space in Coin Account, purchasing ${dollarQty} directly");
                var btcorder = _apiClient.Spot.Order.PlaceOrder("BTCUSDT", Binance.Net.Enums.OrderSide.Buy, Binance.Net.Enums.OrderType.Market, quoteOrderQuantity: dollarQty);
                return btcorder.Success;
            }
            return success;
        }
        public string goldDipPurchase(QuantConnect.Data.Market.TradeBar data)
        {
            if (data.EndTime.Hour != 0)
                return $" - Not time to purchase Gold - Hour = {data.EndTime.Hour}";

            string msg = " - ";

            var paxgKline = _apiClient.Spot.Market.GetKlines("PAXGUSDT", Binance.Net.Enums.KlineInterval.OneDay,limit:2);

            if (paxgKline.Success)
            {
                var candle = paxgKline.Data.Where(y => y.CloseTime < DateTime.UtcNow.AddMinutes(1)).OrderByDescending(x => x.CloseTime).ToList()[0];
                var PriceChangePercent = Math.Round(100 * (candle.Close - candle.Open) / candle.Open,2);
                
                if (PriceChangePercent <= -1)
                {
                    var qty = Math.Floor(TotalPortfolioValue * .005m);
                    msg += $"Purchasing ${qty} worth of gold on price change of {PriceChangePercent}. Success = ";
                    if (qty > 10)
                    {
                        var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", qty, Binance.Net.Enums.FuturesTransferType.FromUsdtFuturesToSpot);
                        if (!transfer.Success)
                            msg += $"false - Futures Transfer Error = {transfer.Error}";
                        else
                        {
                            var order = _apiClient.Spot.Order.PlaceOrder("PAXGUSDT", Binance.Net.Enums.OrderSide.Buy, Binance.Net.Enums.OrderType.Market, quoteOrderQuantity: qty);
                            if (order.Success)
                                msg += "true";
                            else
                                msg += $"false - Spot order Error = {order.Error}";
                        }
                    }
                    else
                        msg += "false, Quantity less than minimum of $10";
                }
                else
                    msg += $"No gold investment required on Price Change of {PriceChangePercent}% ";
            }
            else
                msg += $"Unable to retrieve price for Gold Dip purchase. Reason = {paxgKline.Error}";
            
            return msg;
        }

        /// <summary>
        /// Given a quantity of bitcoin this Transfers USD from the futures account, purchases bitcoin, and transfers into the savings wallet
        /// </summary>
        /// <param name="qty">Quantity of bitcoin to invest in</param>
        /// <param name="tag">Tag for the order</param>
        /// <returns>Whether or not the trade was properly executed</returns>
        public bool MasterFeePayment(decimal qty)
        {
            var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", qty, Binance.Net.Enums.FuturesTransferType.FromUsdtFuturesToSpot);
            if (!transfer.Success && transfer.Error.Code != -9000)
            {
                Log("MasterFeePayment.FuturesTransfer():" + transfer.Error.Message);
                return false;
            }

            Thread.SpinWait(500);

            for (int i = 1; i <= 3; i++)
            {
                Log("MasterFeePayment(): Attempting to confirm account balance");
                var acct = _apiClient.General.GetAccountInfo();
                if (acct.Success)
                {
                    var success = acct.Data.Balances.Where(x => x.Asset == "USDT").FirstOrDefault().Free >= qty ? true : false;
                    if (success)
                        break;
                }
                Thread.Sleep(100);
            }

            var masterTransfer = _apiClient.SubAccount.TransferSubAccountToMaster("USDT", (int)qty);
            if (!masterTransfer.Success)
            {
                Log("MasterFeePayment.MasterTransfer():" + masterTransfer.Error);
            }
            return masterTransfer.Success;
        }

        /// <summary>
        /// Retreives and saves in memory the Product ID for a given asset
        /// </summary>
        /// <param name="Asset">The symbol the savings product request is for</param>
        /// <returns>Id's for the Savings product</returns>
        private string GetOrAddSavingsProductID(string Asset, bool reset = false)
        {
            if (!productIdDict.ContainsKey(Asset) || reset)
            {
                var data = _apiClient.Lending.GetFlexibleProductList(Binance.Net.Enums.ProductStatus.Subscribable);

                if (data.Success)
                {
                    if (data.Data.Any(x => x.Asset == Asset))
                    {
                        productIdDict.Add(Asset, data.Data.FirstOrDefault(x => x.Asset == Asset));
                        return productIdDict[Asset].ProductId;
                    }
                }
                else if (data.Error.Message != "")
                {
                    var msg = ($"ERROR: DonchianCryptoFuturesAlgorithm.GetOrAddSavingsProductID(): Unable to retreive savings product ID for {Asset}, reason = {data.Error.Message}");
                    Log(msg);
                }
            }

            return productIdDict[Asset].ProductId;
        }

        /// <summary>
        /// Retreives and saves in memory the Swap Product ID for a given asset
        /// </summary>
        /// <param name="Asset">The symbol the swap product request is for</param>
        /// <returns>Id's for the Savings product</returns>
        private string GetOrAddSwapProductID(string Asset)
        {
           /* if (!productIdDict.ContainsKey(Asset))
            {
                var data = _apiClient.Lending.GetFlexibleProductList(Binance.Net.Enums.ProductStatus.Subscribable);

                if (data.Success)
                {
                    if (data.Data.Any(x => x.Asset == Asset))
                    {
                        productIdDict.Add(Asset, data.Data.FirstOrDefault(x => x.Asset == Asset));
                        return productIdDict[Asset].ProductId;
                    }
                }
                else if (data.Error.Message != "")
                {
                    var msg = ($"ERROR: DonchianCryptoFuturesAlgorithm.GetOrAddSavingsProductID(): Unable to retreive savings product ID for {Asset}, reason = {data.Error.Message}");
                    Log(msg);
                }
            }*/

            return "TODO";
        }

        /// <summary>
        /// Retreives and saves in memory the Product ID for a given asset
        /// </summary>
        /// <param name="Asset">The symbol the savings product request is for</param>
        /// <returns>Id's for the Savings product</returns>
        private void AddAllProductIDs(List<string> Assets)
        {

            var data = _apiClient.Lending.GetFlexibleProductList(Binance.Net.Enums.ProductStatus.Subscribable);
            if (data.Success)
            {
                foreach (var Asset in Assets)
                {
                    if (!productIdDict.ContainsKey(Asset))
                    {
                        if (data.Data.Any(x => x.Asset == Asset))
                            productIdDict.Add(Asset, data.Data.FirstOrDefault(x => x.Asset == Asset));

                        else
                            productIdDict.Add(Asset, new BinanceSavingsProduct());
                    }
                }
            }
            else
            {
                var msg = ($"ERROR: DonchianCryptoFuturesAlgorithm.AddAllProductIDs(): Unable to retreive savings product ID for all products, reason = {data.Error}");
                Log(msg);
            }
        }

        private decimal GetOrAddMinimumSavingsAmount(string Asset)
        {

            if (!productIdDict.ContainsKey(Asset))
            {
                var data = _apiClient.Lending.GetFlexibleProductList(Binance.Net.Enums.ProductStatus.Subscribable);

                if (data.Success
                    && data.Data.Any(x => x.Asset == Asset))
                {
                    productIdDict.Add(Asset, data.Data.FirstOrDefault(x => x.Asset == Asset));
                    return productIdDict[Asset].MinimalPurchaseAmount;
                }
                else
                {
                    var msg = ($"DonchianCryptoFuturesAlgorithm.GetOrAddMinimumSavingsAmount(): Unable to retreive minimum savings amount for {Asset}, reason = {data.Error}");
                    Log(msg);
                }
            }

            return productIdDict.ContainsKey(Asset) ? productIdDict[Asset].MinimalPurchaseAmount : Decimal.MaxValue;
        }

        private decimal GetOrAddMinimumSwapAmount(string Asset)
        {
            /*
            if (!productIdDict.ContainsKey(Asset))
            {
                var data = _apiClient.Lending.GetFlexibleProductList(Binance.Net.Enums.ProductStatus.Subscribable);

                if (data.Success
                    && data.Data.Any(x => x.Asset == Asset))
                {
                    productIdDict.Add(Asset, data.Data.FirstOrDefault(x => x.Asset == Asset));
                    return productIdDict[Asset].MinimalPurchaseAmount;
                }
                else
                {
                    var msg = ($"DonchianCryptoFuturesAlgorithm.GetOrAddMinimumSavingsAmount(): Unable to retreive minimum savings amount for {Asset}, reason = {data.Error}");
                    Log(msg);
                }
            }*/

            return Decimal.MaxValue;
        }

        List<ExchangeValue> _offExchangeValues = new List<ExchangeValue>();
        public decimal GetSheetsOffExchangeValues()
        {
            var total = _prevOffExchangeValue;

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
            String range = "Holdings!A:D";
            SpreadsheetsResource.ValuesResource.GetRequest request =
                    service.Spreadsheets.Values.Get(spreadsheetId, range);
            try
            {
                ValueRange response = request.Execute();
                IList<IList<Object>> values = response.Values;
                _offExchangeValues = new List<ExchangeValue>();
                if (values != null && values.Count > 0)
                {
                    var i = 0;
                    try
                    {
                        foreach (var data in values)
                        {
                            if (i == 0)
                            {
                                i++;
                                continue;
                            }
                            if (data.Count > 1)
                            {
#pragma warning disable CA1305 // Specify IFormatProvider
                                string _exchange = Convert.ToString(data[0]);
                                string _asset = Convert.ToString(data[1]);
                                decimal _quantity = Convert.ToDecimal(data[2]);
                                decimal _usdtvalue = Convert.ToDecimal(data[3]);
#pragma warning restore CA1305 // Specify IFormatProvider
                                var value = new ExchangeValue(_exchange, _asset, _quantity, _usdtvalue);
                                _offExchangeValues.Add(value);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Log($"DonchianCryptoFuturesAlgorithm.Supplemental.GetSheetsOffExchangeValues(): Error thrown at i = {i}, error = {e}");
                        Error(e);
                    }
                    if (_offExchangeValues.Any(x => x.Exchange != "Binance" && x.Exchange != "Binance BTC Target"))
                    {
                        total = _offExchangeValues.Where(x => x.Exchange != "Binance" && x.Exchange != "Binance BTC Target").Sum(y => y.USDTValue);
                        Log($"DonchianCryptoFuturesAlgorithm.Supplemental.GetSheetsOffExchangeValues(): Off Exchange value retreived successfully, set to {total}");
                    }
                    else if (_offExchangeValues.Any(x => x.Exchange == "Binance BTC Target" && x.Asset == "BTC"))
                    {
                        _neutralBtcTarget = _offExchangeValues.Where(x => x.Exchange == "Binance BTC Target" && x.Asset == "BTC").Sum(y => y.Quantity);
                        Log($"DonchianCryptoFuturesAlgorithm.Supplemental.GetSheetsOffExchangeValues(): BTC Coin target retreived successfully, setting to {_neutralBtcTarget}");
                    }
                }
                else
                {
                    Log("DonchianCryptoFuturesAlgorithm.Supplemental.GetSheetsOffExchangeValues(): Unable to retreive data from Google Sheets");
                }
            }
            catch(Exception e)
            {
                Log(e.ToString());
            }
            return total == 0 ? _prevOffExchangeValue : total;
        }

        public List<BinanceFuturesIncomeHistory> GetBinanceHistoricalTransactions(DateTime startDate, DateTime endDate, bool audit = false)
        {
            string csvFileName = $"../../../Data/TotalPortfolioValues/{currentAccount}/{currentAccount}_CompleteTransactionHistory.csv";

            var path = Path.GetDirectoryName(csvFileName);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);

            var tradesList = new List<BinanceFuturesIncomeHistory>();
            var msTime = startDate;
            if (endDate > DateTime.UtcNow)
                endDate = DateTime.UtcNow;
            try
            {
                if (File.Exists(csvFileName) && !audit)
                {
                    string[] lines = File.ReadAllLines(csvFileName);

                    var columnQuery =
                        from line in lines.Skip(1)
                        let elements = line.Split(',')
                        select new BinanceFuturesIncomeHistory()
                        {
                            Symbol = elements[0],
                            IncomeTypeString = elements[1],
                            Income = elements[3].ToDecimal(),
                            Asset = elements[4],
                            Info = elements[5],
                            Time = Convert.ToDateTime(elements[6], CultureInfo.GetCultureInfo("en-US")),
                            TransactionId = elements[7],
                            TradeId = elements[8],
                        };

                    tradesList = columnQuery.ToList();

                    msTime = tradesList.OrderBy(x => x.Time).Last().Time.AddSeconds(1);

                }
                while (msTime < endDate)
                {
                    var trades = _apiClient.FuturesUsdt.GetIncomeHistory(startTime: msTime, limit: 1000);

                    if (trades.Success)
                    {
                        var data = trades.Data;
                        foreach (var item in data.OrderBy(x => x.Time))
                            tradesList.Add(item);

                        if (data.Count() < 1000)
                            msTime = endDate;
                        else
                            msTime = data.OrderBy(x => x.Time).Last().Time;
                    }

                }
                using (var writer = new StreamWriter(csvFileName, false, Encoding.UTF8))
                using (var csvWriter = new CsvWriter(writer, CultureInfo.CurrentCulture))
                {

                    csvWriter.Configuration.Delimiter = ",";
                    csvWriter.Configuration.HasHeaderRecord = true;
                    csvWriter.Configuration.AutoMap<BinanceFuturesIncomeHistory>();

                    // csvWriter.WriteHeader<BinanceFuturesIncomeHistory>();
                    csvWriter.WriteRecords(tradesList);
                }
            }
            catch (Exception e)
            {
                Error($"GetBinanceHistoricalTransactions(): Unable to retreive Transaction History {e.Message} stacktrace: {e.StackTrace}");
            }
            return tradesList.Where(x => x.Time >= startDate && x.Time <= endDate).ToList();
        }

        private void NewSaveTotalAccountValue(string transactionType = "TRADE")
        {
            string csvFileName = $"../../../Data/TotalPortfolioValues/{currentAccount}/{currentAccount}_TotalPortfolioValues.csv";

            var path = Path.GetDirectoryName(csvFileName);
            if (path != null && !Directory.Exists(path))
                Directory.CreateDirectory(path);

            if (!File.Exists(csvFileName))
            {
                using (var writer = new StreamWriter(csvFileName, false, Encoding.UTF8))
                using (var csvWriter = new CsvWriter(writer, CultureInfo.CurrentCulture))
                {
                    csvWriter.Configuration.Delimiter = ",";
                    csvWriter.Configuration.HasHeaderRecord = true;
                    csvWriter.Configuration.AutoMap<StoredPortfolioValue>();

                    var line = new List<StoredPortfolioValue> ()
                        { 
                        new StoredPortfolioValue(Time, Math.Round(TotalPortfolioValue - _prevOffExchangeValue, 2), Math.Round(TotalPortfolioValue, 2), "EOD", 0, 0, 0),
                    };

                    csvWriter.WriteHeader<StoredPortfolioValue>();
                    csvWriter.WriteRecords(line);
                }
            }
            else
            {
                using (var writer = new StreamWriter(csvFileName, true, Encoding.UTF8))
                using (var csvWriter = new CsvWriter(writer, CultureInfo.CurrentCulture))
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

                    csvWriter.Configuration.Delimiter = ",";
                    csvWriter.Configuration.HasHeaderRecord = true;
                    csvWriter.Configuration.AutoMap<StoredPortfolioValue>();

                    var line = new List<StoredPortfolioValue>()
                        {
                        new StoredPortfolioValue(Time, Math.Round(TotalPortfolioValue - _prevOffExchangeValue, 2), Math.Round(TotalPortfolioValue, 2), transactionType, percentGain, PortfolioProfit, portDD),
                    };
                    line[0].TransactionDrawdown = portDD;

                    csvWriter.WriteRecords(line);
                    
                    _prevTotalPortfolioValue = TotalPortfolioValue;
                }
            }
        }

        public void NewLoadPortfolioValues()
        {
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
                elements[1].ToDecimal(),
                elements[2].ToDecimal(),
                elements[3],
                elements[4].ToDecimal(),
                elements[5].ToDecimal(),
                elements[6].ToDecimal());

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

        public bool CheckAndBalanceFees()
        {
            var transactions = GetBinanceHistoricalTransactions(DateTime.Now.AddMonths(-1), DateTime.Now);
            var pastUSDCosts = transactions.Where(x => x.IncomeType == Binance.Net.Enums.IncomeType.Commission && x.Asset == "USDT")?.Select(y => y.Income).Sum();
            var pastBNBCosts = transactions.Where(x => x.IncomeType == Binance.Net.Enums.IncomeType.Commission && x.Asset == "BNB")?.Select(y => y.Income).Sum();
            var balances = _apiClient.FuturesUsdt.Account.GetBalance();
            if (!balances.Success)
                return false;

            var bnbBalance = balances.Data.Where(x => x.Asset == "BNB").Any() ? balances.Data.Where(x => x.Asset == "BNB").First().Balance : 0m;
            var bnbPrice = _apiClient.Spot.Market.GetPrice("BNBUSDT");
            if (!bnbPrice.Success)
                return false;

            var totalCoverage = bnbBalance * bnbPrice.Data.Price;
            var requiredCoverage = pastUSDCosts + pastBNBCosts * bnbPrice.Data.Price;

            if (requiredCoverage - totalCoverage > 0)
                return PurchaseAndHedgeBinanceForFees(requiredCoverage - totalCoverage);

            return true;
        }

        public bool PurchaseAndHedgeBinanceForFees(decimal? qty)
        {
            if (qty == null)
                return false;

            decimal quantity = (decimal)qty;
            //Futures to savings
            var transfer = _apiClient.Spot.Futures.TransferFuturesAccount("USDT", quantity, Binance.Net.Enums.FuturesTransferType.FromUsdtFuturesToSpot);
            if (!transfer.Success)
                return false;
            

            return true;
        }
        public void SendMonthlyReport()
        {
            var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month - 1, 01);
            var monthTrades = GetBinanceHistoricalTransactions(start, start.AddMonths(1), false);
            // Send email to account
        }

        public void CalculateFuturesTaxes(int year)
        {
            var start = new DateTime(year, 1, 1);
            var end = new DateTime(year + 1, 1, 1);
            var yearTrades = GetBinanceHistoricalTransactions(start, end, false);
            var assets = yearTrades.GroupBy(x => x.Asset).Select(g => g.First().Asset);
            var priceDict = new Dictionary<string, IEnumerable<IBinanceKline>>();
            foreach (var symbol in assets)
            {
                if (symbol == "USDT" || priceDict.ContainsKey(symbol))
                    continue;

                var price = _apiClient.Spot.Market.GetKlines(symbol, Binance.Net.Enums.KlineInterval.OneDay, start, end);

                if (price.Success)
                    priceDict.Add(symbol, price.Data);
            }
            var usdtPrice = _apiClient.Spot.Market.GetKlines("USDTUSD", Binance.Net.Enums.KlineInterval.OneDay, start, end);

            var TaxData = new List<BinanceFuturesIncomeHistoryUSD>();
            foreach (var trans in yearTrades)
            {
                var assetprice = trans.Asset == "USDT" ? 1 : priceDict[trans.Asset].Where(x => x.CloseTime.Date == trans.Time.Date).FirstOrDefault().Close;
                var adjTrans = new BinanceFuturesIncomeHistoryUSD()
                    {
                    Asset = trans.Asset,
                    TransactionId = trans.TransactionId,
                    Time = trans.Time,
                    TradeId = trans.TradeId,
                    IncomeTypeString = trans.IncomeTypeString,
                    Income = trans.Income,
                    Info = trans.Info,
                    Symbol = trans.Symbol,
                    ExchangeRateUSD = assetprice * usdtPrice.Data.Where(x => x.CloseTime.Date == trans.Time.Date).FirstOrDefault().Close
                };
                TaxData.Add(adjTrans);
            }
        }

        IList<IList<Object>> _expectancyData;
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

            try
            {
                ValueRange response = request.Execute();
                _expectancyData = response.Values;
                if (_expectancyData != null && _expectancyData.Count > 1000)
                {
                    if (Convert.ToDateTime(_expectancyData[1][3], CultureInfo.GetCultureInfo("en-US")) > DateTime.UtcNow.AddMinutes(-30))
                    {
                        auditComplete = true;
                    }
                }
            }
            catch (Exception e)
            {
                Log(e.ToString());
            }
            return auditComplete;
        }

        /// <summary>
        /// Save expectancy Data
        /// </summary>
        /// <param name="transactions">Transactions list via an OrderProvider</param>
        /// <param name="csvFileName">File path to create</param>
        private void LoadExpectancyDataFromSheets()
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
            try
            {
                ValueRange response = request.Execute();
                IList<IList<Object>> values = response.Values;
                if (values != null && values.Count > 1000)
                {
                    if (Convert.ToDateTime(values[1][3], CultureInfo.GetCultureInfo("en-US")) > DateTime.UtcNow.AddMinutes(-90))
                    {
                        ProcessSheetsExpectancyData(values);
                        auditComplete = true;
                    }
                }
            }
            catch(Exception e)
            {
                Log(e.ToString());
            }
            //return auditComplete;

        }

        public void ProcessSheetsExpectancyData(IList<IList<Object>> values)
        {
            var _lastSym = "BTC";

            var columnQuery =
                from line in values.Skip(1)
                let elements = line
                select new ExpectancyData(elements[0].ToString(), Convert.ToInt32(elements[1], CultureInfo.GetCultureInfo("en-US")), Convert.ToDecimal(elements[2], CultureInfo.GetCultureInfo("en-US")), Convert.ToDateTime(elements[3], CultureInfo.GetCultureInfo("en-US")));

            var tenTrades = columnQuery.ToList().Where(x => x.TradeNum == 9).Take(100).Select(y => y.Symbol).ToList();
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
        private class ExchangeValue
        {
            public ExchangeValue(string _exchange, string _asset, decimal _quantity, decimal _usdtvalue)
            {
                Exchange = _exchange;
                Asset = _asset;
                Quantity = _quantity;
                USDTValue = _usdtvalue;
            }
            public string Exchange { get; private set; }
            public string Asset { get; private set; }
            public decimal Quantity { get; private set; }
            public decimal USDTValue { get; set; }
        }
        private class BinanceFuturesIncomeHistoryUSD : BinanceFuturesIncomeHistory
        {
            public decimal? ExchangeRateUSD { get; set; }
            public decimal? IncomeUSD => Income * ExchangeRateUSD;
        }
        public enum TransferDirection
        {
            FuturesToSavings,
            SavingsToFutures,
            FuturesToSwap,
            SwapToFutures
        }

    }
}
