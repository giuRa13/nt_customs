#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.giuli13;
#endregion

// For every chart bar it shows the contracts traded by aggressive buyers and sellers:
//   Up bar   (buy color)  = contracts executed at the ask  -> Bar Statistics "Ask" row
//   Down bar (sell color) = contracts executed at the bid  -> Bar Statistics "Bid" row
// The values come from a hidden Volumetric (Order Flow+) series with the same period as the chart,
// so they match a footprint / Bar Statistics exactly. The hidden series is never drawn.


namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
    public class BidAskVolume : Indicator
    {
        private class BarStat
        {
            public double Buy;      // contracts executed at the ask
            public double Sell;     // contracts executed at the bid
        }

        // ---- data (instrument thread writes, UI thread reads -> lock) ----
        private readonly object sync = new object();
        private List<BarStat> stats;
        private NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType volumetric;
        private DateTime calcStart = DateTime.MinValue;   // bars at or before this time are skipped
        private Series<double> bidVolume;                 // sells as a positive number (for strategies)

        // ---- SharpDX resources (UI thread) ----
        private SharpDX.Direct2D1.Brush dxBuy, dxSell, dxZero;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "BidAskVolume";
                Description = "Bid/Ask Volume: for every candle, contracts executed at the ask (aggressive buys, drawn up from the center) " +
                    "and contracts executed at the bid (aggressive sells, drawn down from the center). " +
                    "Values are read from a hidden Volumetric series, so they match the footprint Bar Statistics. Requires Order Flow+.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DrawOnPricePanel = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = true;
                IsSuspendedWhileInactive = false;
                BarsRequiredToPlot = 0;
                ScaleJustification = ScaleJustification.Right;

                // Parameters
                DaysToCalculate = 3;

                // Visual
                BuyColor = MakeBrush("#FF1B88F4");
                SellColor = MakeBrush("#FFF02102");
                MatchChartBarWidth = true;
                BarWidth = 3;
                ShowZeroLine = true;
                ZeroLineColor = Brushes.DimGray;

                // Plots feed the Data Box, the price markers on the axis and strategies.
                // The histogram itself is drawn in OnRender (base.OnRender is not called, so plots are not drawn as lines).
                AddPlot(Brushes.DodgerBlue, "Ask");          // buys (positive)
                AddPlot(Brushes.Red, "Bid");                 // sells, stored NEGATIVE so the marker sits at the bottom of the red bar
                AddPlot(Brushes.Transparent, "Delta");       // buys - sells (Data Box only)
            }
            else if (State == State.Configure)
            {
                // plot colors = histogram colors, so the axis markers match the bars
                Plots[0].Brush = BuyColor;
                Plots[1].Brush = SellColor;

                // Hidden Volumetric series, same bar type and period as the chart, bid/ask delta, 1 tick per level.
                // null = the chart's instrument.
                AddVolumetric(null, BarsPeriod.BarsPeriodType, BarsPeriod.Value, VolumetricDeltaType.BidAsk, 1);
            }
            else if (State == State.DataLoaded)
            {
                stats = new List<BarStat>();
                bidVolume = new Series<double>(this, MaximumBarsLookBack.Infinite);
                volumetric = BarsArray.Length > 1
                    ? BarsArray[1].BarsType as NinjaTrader.NinjaScript.BarsTypes.VolumetricBarsType
                    : null;
                if (volumetric == null)
                    Log("BidAskVolume: the Volumetric series could not be created (Order Flow+ license or unsupported chart type).", LogLevel.Error);

                // Only the last N trading sessions (chart trading hours) are calculated.
                calcStart = DateTime.MinValue;
                if (DaysToCalculate > 0 && BarsArray[0].Count > 0)
                {
                    int sessions = 0;
                    for (int i = BarsArray[0].Count - 1; i >= 0; i--)
                    {
                        if (BarsArray[0].IsFirstBarOfSessionByIndex(i) && ++sessions >= DaysToCalculate)
                        {
                            if (i > 0) calcStart = BarsArray[0].GetTime(i - 1);
                            break;
                        }
                    }
                }
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
            }
        }

        public override string DisplayName
        {
            get { return "Bid/Ask Volume"; }
        }

        // =====================================================================
        //  CALCULATION
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                ReadVolumetric();
                return;
            }
            if (BarsInProgress != 0)
                return;

            BarStat s;
            lock (sync)
                s = GetStat(CurrentBar, false);
            SetPlots(s);
        }

        private void ReadVolumetric()
        {
            if (volumetric == null || CurrentBars[1] < 0)
                return;

            DateTime t = Times[1][0];
            if (t <= calcStart)
                return;   // older than "Days To Calculate"

            // Same period as the chart -> same bar time. If the chart bar with this time has not been
            // processed yet (later time than the last chart bar), it is the next one.
            int idx = (CurrentBars[0] < 0 || t > Times[0][0]) ? CurrentBars[0] + 1 : CurrentBars[0];
            if (idx < 0)
                return;

            var v = volumetric.Volumes[CurrentBars[1]];
            BarStat s;
            lock (sync)
            {
                s = GetStat(idx, true);
                s.Buy = v.TotalBuyingVolume;     // "Ask" row of Bar Statistics
                s.Sell = v.TotalSellingVolume;   // "Bid" row of Bar Statistics
            }

            if (idx == CurrentBars[0] && CurrentBars[0] >= 0)
                SetPlots(s);
        }

        private void SetPlots(BarStat s)
        {
            double buy = s != null ? s.Buy : 0;
            double sell = s != null ? s.Sell : 0;
            Values[0][0] = buy;
            Values[1][0] = -sell;     // negative: marker at the bottom of the sell bar
            Values[2][0] = buy - sell;
            bidVolume[0] = sell;
        }

        // call inside lock
        private BarStat GetStat(int idx, bool create)
        {
            if (idx < 0) return null;
            if (create)
                while (stats.Count <= idx)
                    stats.Add(new BarStat());
            return idx < stats.Count ? stats[idx] : null;
        }

        // =====================================================================
        //  RENDERING
        // =====================================================================
        public override void OnCalculateMinMax()
        {
            if (ChartBars == null || stats == null)
            {
                base.OnCalculateMinMax();
                return;
            }

            double max = 0;
            lock (sync)
            {
                int from = Math.Max(0, ChartBars.FromIndex);
                int to = Math.Min(ChartBars.ToIndex, stats.Count - 1);
                for (int i = from; i <= to; i++)
                {
                    if (stats[i].Buy > max) max = stats[i].Buy;
                    if (stats[i].Sell > max) max = stats[i].Sell;
                }
            }
            if (max <= 0) max = 1;
            // symmetric scale: zero stays in the center of the panel
            MinValue = -max;
            MaxValue = max;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartBars == null || stats == null || RenderTarget == null)
                return;
            if (dxBuy == null)
                OnRenderTargetChanged();
            if (dxBuy == null)
                return;

            var oldAA = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

            float width = MatchChartBarWidth
                ? (float)Math.Max(1.0, chartControl.GetBarPaintWidth(ChartBars))   // same width as the candles
                : BarWidth;
            float half = width / 2f;
            float yZero = chartScale.GetYByValue(0);

            lock (sync)
            {
                int from = Math.Max(0, ChartBars.FromIndex);
                int to = Math.Min(ChartBars.ToIndex, stats.Count - 1);

                for (int i = from; i <= to; i++)
                {
                    BarStat s = stats[i];
                    float x = chartControl.GetXByBarIndex(ChartBars, i);

                    // buys at the ask: from center up
                    if (s.Buy > 0)
                    {
                        float yBuy = chartScale.GetYByValue(s.Buy);
                        float h = Math.Max(1f, yZero - yBuy);
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(x - half, yZero - h, half * 2f, h), dxBuy);
                    }

                    // sells at the bid: from center down
                    if (s.Sell > 0)
                    {
                        float ySell = chartScale.GetYByValue(-s.Sell);
                        float h = Math.Max(1f, ySell - yZero);
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(x - half, yZero, half * 2f, h), dxSell);
                    }
                }
            }

            if (ShowZeroLine)
                RenderTarget.DrawLine(new SharpDX.Vector2(ChartPanel.X, yZero),
                    new SharpDX.Vector2(ChartPanel.X + ChartPanel.W, yZero), dxZero, 1f);

            RenderTarget.AntialiasMode = oldAA;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            if (RenderTarget == null)
                return;

            dxBuy = (BuyColor ?? Brushes.DodgerBlue).ToDxBrush(RenderTarget);
            dxSell = (SellColor ?? Brushes.Red).ToDxBrush(RenderTarget);
            dxZero = (ZeroLineColor ?? Brushes.DimGray).ToDxBrush(RenderTarget);
        }

        private void DisposeDx()
        {
            if (dxBuy != null) { dxBuy.Dispose(); dxBuy = null; }
            if (dxSell != null) { dxSell.Dispose(); dxSell = null; }
            if (dxZero != null) { dxZero.Dispose(); dxZero = null; }
        }

        private static Brush MakeBrush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        #region Properties

        // ===== Outputs (for strategies / other indicators) =====
        [Browsable(false)]
        [XmlIgnore]
        public Series<double> AskVolume { get { return Values[0]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> BidVolume { get { return bidVolume; } }   // positive number

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Delta { get { return Values[2]; } }

        // ===== Parameters =====
        [NinjaScriptProperty]
        [Range(0, 365)]
        [Display(Name = "Days To Calculate", Order = 0, GroupName = "Parameters",
            Description = "Only the last N trading sessions are calculated and drawn (sessions of the chart's trading hours). 0 = all loaded days.")]
        public int DaysToCalculate { get; set; }

        // ===== Visual =====
        [XmlIgnore]
        [Display(Name = "Buy Color (at ask, up)", Order = 0, GroupName = "Visual")]
        public Brush BuyColor { get; set; }

        [Browsable(false)]
        public string BuyColorSerialize
        {
            get { return Serialize.BrushToString(BuyColor); }
            set { BuyColor = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Sell Color (at bid, down)", Order = 1, GroupName = "Visual")]
        public Brush SellColor { get; set; }

        [Browsable(false)]
        public string SellColorSerialize
        {
            get { return Serialize.BrushToString(SellColor); }
            set { SellColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Match Chart Bar Width", Order = 2, GroupName = "Visual",
            Description = "On: the histogram bars have the same width as the chart candles. Off: use Bar Width.")]
        public bool MatchChartBarWidth { get; set; }

        [Range(1, 50)]
        [Display(Name = "Bar Width", Order = 3, GroupName = "Visual",
            Description = "Fixed width of the histogram bars in pixels (used when Match Chart Bar Width is off).")]
        public int BarWidth { get; set; }

        [Display(Name = "Show Zero Line", Order = 4, GroupName = "Visual")]
        public bool ShowZeroLine { get; set; }

        [XmlIgnore]
        [Display(Name = "Zero Line Color", Order = 5, GroupName = "Visual")]
        public Brush ZeroLineColor { get; set; }

        [Browsable(false)]
        public string ZeroLineColorSerialize
        {
            get { return Serialize.BrushToString(ZeroLineColor); }
            set { ZeroLineColor = Serialize.StringToBrush(value); }
        }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private giuli13.BidAskVolume[] cacheBidAskVolume;
		public giuli13.BidAskVolume BidAskVolume(int daysToCalculate)
		{
			return BidAskVolume(Input, daysToCalculate);
		}

		public giuli13.BidAskVolume BidAskVolume(ISeries<double> input, int daysToCalculate)
		{
			if (cacheBidAskVolume != null)
				for (int idx = 0; idx < cacheBidAskVolume.Length; idx++)
					if (cacheBidAskVolume[idx] != null && cacheBidAskVolume[idx].DaysToCalculate == daysToCalculate && cacheBidAskVolume[idx].EqualsInput(input))
						return cacheBidAskVolume[idx];
			return CacheIndicator<giuli13.BidAskVolume>(new giuli13.BidAskVolume(){ DaysToCalculate = daysToCalculate }, input, ref cacheBidAskVolume);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.BidAskVolume BidAskVolume(int daysToCalculate)
		{
			return indicator.BidAskVolume(Input, daysToCalculate);
		}

		public Indicators.giuli13.BidAskVolume BidAskVolume(ISeries<double> input , int daysToCalculate)
		{
			return indicator.BidAskVolume(input, daysToCalculate);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.BidAskVolume BidAskVolume(int daysToCalculate)
		{
			return indicator.BidAskVolume(Input, daysToCalculate);
		}

		public Indicators.giuli13.BidAskVolume BidAskVolume(ISeries<double> input , int daysToCalculate)
		{
			return indicator.BidAskVolume(input, daysToCalculate);
		}
	}
}

#endregion
