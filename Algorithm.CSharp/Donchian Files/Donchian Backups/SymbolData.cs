using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Indicators
{
    /// <summary>
    /// Basic Template Library Class
    /// Library classes are snippets of code you can reuse between projects. They are added to projects on compile. This can be useful for reusing
    /// indicators, math components, risk modules etc. If you use a custom namespace make sure you add the correct using statement to the
    /// algorithm-user.
    /// </summary>

    public class SymbolData
    {
        public SymbolData(string symbol)
        {
            Symbol = symbol;
        }
        public SymbolData(string symbol, IntraweekVwap vwap, AverageTrueRange atr, DonchianChannel donchian)
        {
            Symbol = symbol;
            VWAP_week = vwap;
            ATR = atr;
            Donchian = donchian;
            testIsOpen = false;
            testEntryPrice = 0;
            testATR = 0;
        }
        public string Symbol { get; set; }
        public MLTradeInfo MLData { get; set; }
        public bool StartUp { get; set; } = true;

        public decimal? BuyStop { get; set; }
        public decimal? SellStop { get; set; }
        public decimal? StopInTicks { get; set; }
        public decimal BuyQuantity { get; set; } = 0;
        public decimal SellQuantity { get; set; } = 0;
        public decimal? OpenLongEntry { get; set; }
        public decimal? OpenShortEntry { get; set; }
        public decimal? TrailStopTrigger { get; set; }
        public decimal? ProfitTrigger { get; set; }
        public bool TradeProfit { get; set; } = false;

        public AverageTrueRange ATR { get; set; }
        public DonchianChannel Donchian { get; set; }
        public IntraweekVwap VWAP_week { get; set; }
        public LinearWeightedMovingAverage TrendMA { get; set; }

        public RollingWindow<IndicatorDataPoint> TrendMAWindow = new RollingWindow<IndicatorDataPoint>(5);

        public decimal SQN
        {
            get
            {
                if (HypotheticalTrades.Count() < 2)
                {
                    return 0;
                }
                else
                {
                    return (decimal)Math.Sqrt(HypotheticalTrades.Count()) * RollingWindowAverage / RollingWindowStDev;
                }
            }
        }
        public bool Active { get; set; } = false;
        public bool testIsOpen { get; set; }
        public decimal testEntryPrice { get; set; }
        public decimal testATR { get; set; }
        public RollingWindow<decimal> HypotheticalTrades = new RollingWindow<decimal>(30);

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
                decimal avg = values.Sum();
                return avg;
            }
        }
    }
    public class MLTradeInfo
    {
        public DateTime Time { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public decimal UpperDonchian { get; set; }
        public decimal LowerDonchian { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public int Quantity { get; set; }
        public decimal RiskAmount { get; set; }
        public decimal VWAP { get; set; }
        public decimal ATR { get; set; }
        public bool OpenPosition { get; set; } = false;
        public decimal PriceChange
        {
            get
            {
                if (!OpenPosition)
                {
                    return ExitPrice - EntryPrice;
                }
                else
                { return 0; }
            }
        }
        public decimal PctPriceChange
        {
            get
            {
                if (!OpenPosition && EntryPrice != 0)
                {
                    return (ExitPrice - EntryPrice) / EntryPrice;
                }
                else
                { return 0; }
            }
        }
        public int TradeOutcome
        {
            get
            {
                if (PriceChange * Quantity > RiskAmount)
                    return 1;
                else if (PriceChange * Quantity < RiskAmount * -1)
                    return -1;
                else
                    return 0;
            }
        }

    }
    public class MarketType
    {
        // the VolStat indicator, which is ATR (one measure of volatility) reduced to a percentage of price and
        //plotted with a 100 period SMA and 100 period Bollingers.
        //https://evilspeculator.com/arisen/
        public int LookBackDays { get; set; } = 100;
        public RollingWindow<decimal> LookBackDays_Window = new RollingWindow<decimal>(250);
        public RollingWindow<decimal> MarketATR_Window = new RollingWindow<decimal>(100);
        public AverageTrueRange MarketATR { get; set; }
        public decimal PercentChangeCalc(decimal CurrentPrice, RollingWindow<decimal> LookBackDays_Window, int LookBackDays)
        {
            try
            {
                if (LookBackDays_Window.Count() >= LookBackDays)
                {
                    decimal change = 0;
                    change = (CurrentPrice - LookBackDays_Window[LookBackDays]) / CurrentPrice;
                    change = change * 100;
                    return change;
                }
                else
                {
                    return 0;
                }
            }
            catch { return 0; }
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
        Both,
        None
    }
}