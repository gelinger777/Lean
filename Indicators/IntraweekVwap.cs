using System;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Indicators
{
    /// <summary>
    /// Defines the canonical intraday VWAP indicator
    /// </summary>
    public class IntraweekVwap : IndicatorBase<BaseData>
    {
        private bool _reset = true;
        private decimal _sumOfVolume;
        private decimal _sumOfPriceTimesVolume;

        /// <summary>
        /// Gets a flag indicating when this indicator is ready and fully initialized
        /// </summary>
        public override bool IsReady => _sumOfVolume > 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="IntraweekVwap"/> class
        /// </summary>
        /// <param name="name">The name of the indicator</param>
        public IntraweekVwap(string name)
            : base(name)
        {
        }

        /// <summary>
        /// Computes the new VWAP
        /// </summary>
        protected override IndicatorResult ValidateAndComputeNextValue(BaseData input)
        {
            decimal volume, averagePrice;
            if (!TryGetVolumeAndAveragePrice(input, out volume, out averagePrice))
            {
                return new IndicatorResult(0, IndicatorStatus.InvalidInput);
            }

            var h = input.EndTime.Hour;
            var current = Current.Value;
            // reset vwap on daily boundaries
            if (input.EndTime.Date.DayOfWeek == DayOfWeek.Monday && _reset)
            {
                _sumOfVolume = 0m;
                _sumOfPriceTimesVolume = 0m;
                _reset = false;
                if (current != 0)
                {
                    averagePrice = (averagePrice + Current.Value) / 2m; // #1 Option
                }
            }
            else if (input.EndTime.Date.DayOfWeek == DayOfWeek.Tuesday)
            {
                _reset = true;
            }

            // running totals for Σ PiVi / Σ Vi
            _sumOfVolume += volume;
            _sumOfPriceTimesVolume += averagePrice * volume;

            if (_sumOfVolume == 0m)
            {
                // if we have no trade volume then use the current price as VWAP
                return input.Value;
            }

            return _sumOfPriceTimesVolume / _sumOfVolume;
        }

        /// <summary>
        /// Computes the next value of this indicator from the given state.
        /// NOTE: This must be overriden since it's abstract in the base, but
        /// will never be invoked since we've override the validate method above.
        /// </summary>
        /// <param name="input">The input given to the indicator</param>
        /// <returns>A new value for this indicator</returns>
        protected override decimal ComputeNextValue(BaseData input)
        {
            throw new NotImplementedException($"{nameof(IntraweekVwap)}.{nameof(ComputeNextValue)} should never be invoked.");
        }

        /// <summary>
        /// Determines the volume and price to be used for the current input in the VWAP computation
        /// </summary>
        protected bool TryGetVolumeAndAveragePrice(BaseData input, out decimal volume, out decimal averagePrice)
        {
            var tick = input as Tick;

            if (tick?.TickType == TickType.Trade)
            {
                volume = tick.Quantity;
                averagePrice = tick.LastPrice;
                return true;
            }

            var tradeBar = input as TradeBar;
            if (tradeBar?.IsFillForward == false)
            {
                volume = tradeBar.Volume;
                averagePrice = (tradeBar.High + tradeBar.Low) / 2m;
                if (input.EndTime.Date.DayOfWeek == DayOfWeek.Monday && _reset)
                {
                    //averagePrice += (-tradeBar.High + tradeBar.Low);// #2 Option
                }
                return true;
            }

            volume = 0;
            averagePrice = 0;
            return false;
        }
    }
}