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

// Delta Map - gradient price bars.
// Every candle is recolored by buyer / seller dominance, measured with a 1-tick data series (no Tick Replay).
//   Bar delta   = aggressive buy volume - aggressive sell volume of the candle
//   Dominance   = smoothed delta (EMA of the bar delta, or sum of the last N bar deltas = cumulative delta change)
//   Hue         = sign of the dominance -> bull color while buyers dominate, bear color while sellers dominate.
//                 Because it is smoothed, the color holds for whole swings and flips when control changes (delta rollover).
//   Shade       = strength of the dominance compared to recent bars: dim (weak) -> bright (strong). No neutral color.

namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
    public enum DeltaMapSource
    {
        BarDelta = 0,           // raw delta of each bar (no smoothing, noisy)
        EmaDelta = 1,           // EMA of the bar delta over "Period" bars
        CumulativeChange = 2    // sum of the last "Period" bar deltas (change of cumulative delta)
    }

    public class DeltaMap : Indicator
    {
        private class BarDelta
        {
            public double Buy;      // aggressive buy volume
            public double Sell;     // aggressive sell volume
            public double Value;    // dominance (smoothed delta)
            public bool Bull = true;
            public bool Active;     // bar is inside "Days To Calculate"
        }

        private List<BarDelta> bars;
        private double lastPrice;
        private int lastSide = 1;
        private DateTime calcStart = DateTime.MinValue;

        private Brush[] bullPalette, bearPalette;   // index 0 = weak shade, index GradientSteps = strong shade

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "DeltaMap";
                Description = "Delta Map: recolors the price candles by buyer / seller dominance (smoothed delta)." +
                    "\r\n- Hue: bull color while buyers dominate, bear color while sellers dominate. The smoothing keeps the color for whole swings and flips it when control changes (delta rollover)." +
                    "\r\n- Shade: dim = weak dominance, bright = strong dominance (compared to the last N bars)." +
                    "\r\n- Source: BarDelta = raw delta per bar, EmaDelta = EMA of the bar delta, CumulativeChange = sum of the last N bar deltas." +
                    "\r\n- Higher Period = longer color runs (follows bigger swings). Lower Period = faster flips.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsAutoScale = false;                 // transparent plots must not rescale the price panel
                DisplayInDataBox = true;
                PaintPriceMarkers = false;
                DrawOnPricePanel = true;
                IsSuspendedWhileInactive = false;
                BarsRequiredToPlot = 0;

                // Parameters
                Source = DeltaMapSource.EmaDelta;
                Period = 6;
                Lookback = 50;
                FullAtMultiple = 2.0;
                DaysToCalculate = 3;

                // Colors (weak -> strong)
                BullWeak = MakeBrush("#FF0F4A85");
                BullStrong = MakeBrush("#FF6FBDFF");
                BearWeak = MakeBrush("#FF7A1C2B");
                BearStrong = MakeBrush("#FFFF6B80");
                GradientSteps = 20;
                ColorOutline = true;

                AddPlot(Brushes.Transparent, "BarDelta");
                AddPlot(Brushes.Transparent, "Dominance");
                AddPlot(Brushes.Transparent, "Intensity");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                bars = new List<BarDelta>();
                lastPrice = 0;
                lastSide = 1;

                bullPalette = BuildPalette(BullWeak, BullStrong, GradientSteps);
                bearPalette = BuildPalette(BearWeak, BearStrong, GradientSteps);

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
        }

        public override string DisplayName
        {
            get
            {
                string src = Source == DeltaMapSource.BarDelta ? "Bar"
                           : Source == DeltaMapSource.EmaDelta ? "EMA " + Period
                           : "Sum " + Period;
                return "Delta Map (" + src + ")";
            }
        }

        // =====================================================================
        //  CALCULATION
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                ProcessTick();
                return;
            }
            if (BarsInProgress != 0)
                return;

            PaintCurrentBar();
        }

        private void ProcessTick()
        {
            if (CurrentBars[1] < 0)
                return;

            DateTime t = Times[1][0];
            if (t <= calcStart)
                return;

            double price = Closes[1][0];
            double vol = Volumes[1][0];
            int side = ClassifySide(price);
            lastPrice = price;
            lastSide = side;

            // Realtime: primary series is updated first -> forming bar is CurrentBars[0].
            // Historical: primary bars are stamped with their close time -> a later trade belongs to the next bar.
            int idx;
            if (State == State.Realtime)
                idx = CurrentBars[0];
            else
                idx = (CurrentBars[0] < 0 || t > Times[0][0]) ? CurrentBars[0] + 1 : CurrentBars[0];
            if (idx < 0)
                return;

            BarDelta b = GetBar(idx);
            if (side > 0) b.Buy += vol;
            else b.Sell += vol;

            if (idx == CurrentBars[0])
                PaintCurrentBar();
        }

        private BarDelta GetBar(int idx)
        {
            while (bars.Count <= idx)
                bars.Add(new BarDelta());
            return bars[idx];
        }

        // Paints primary bar CurrentBars[0] (barsAgo 0 refers to the primary series also from BarsInProgress 1)
        private void PaintCurrentBar()
        {
            int i = CurrentBars[0];
            if (i < 0 || Times[0][0] <= calcStart)
                return;

            BarDelta b = GetBar(i);
            b.Active = true;

            double delta = b.Buy - b.Sell;
            BarDelta prev = i > 0 ? bars[i - 1] : null;
            bool prevActive = prev != null && prev.Active;

            b.Value = Dominance(i, delta, prevActive ? prev : null);

            // hue: sign of the dominance; exactly 0 keeps the previous hue
            if (b.Value > 0) b.Bull = true;
            else if (b.Value < 0) b.Bull = false;
            else b.Bull = prevActive ? prev.Bull : delta >= 0;

            double k = Intensity(i, b.Value);
            int step = (int)Math.Round(k * GradientSteps);
            if (step < 0) step = 0;
            if (step > GradientSteps) step = GradientSteps;

            Brush brush = b.Bull ? bullPalette[step] : bearPalette[step];
            BarBrushes[0] = brush;
            if (ColorOutline)
                CandleOutlineBrushes[0] = brush;

            Values[0][0] = delta;
            Values[1][0] = b.Value;
            Values[2][0] = k * 100.0;
        }

        private double Dominance(int i, double delta, BarDelta prev)
        {
            switch (Source)
            {
                case DeltaMapSource.EmaDelta:
                    {
                        if (prev == null) return delta;   // seed
                        double alpha = 2.0 / (Period + 1);
                        return alpha * delta + (1 - alpha) * prev.Value;
                    }
                case DeltaMapSource.CumulativeChange:
                    {
                        double sum = delta;
                        for (int j = Math.Max(0, i - Period + 1); j < i; j++)
                        {
                            BarDelta p = bars[j];
                            if (p.Active) sum += p.Buy - p.Sell;
                        }
                        return sum;
                    }
                default:
                    return delta;
            }
        }

        // 0 = weak shade, 1 = strong shade: |dominance| vs the average |dominance| of the previous Lookback bars
        private double Intensity(int i, double value)
        {
            double sum = 0;
            int n = 0;
            for (int j = Math.Max(0, i - Lookback); j < i; j++)
            {
                BarDelta p = bars[j];
                if (!p.Active) continue;
                sum += Math.Abs(p.Value);
                n++;
            }
            if (n < 2)
                return 0.5;

            double avg = sum / n;
            if (avg <= 0)
                return value != 0 ? 1.0 : 0.0;
            return Math.Min(1.0, Math.Abs(value) / (avg * FullAtMultiple));
        }

        // Aggressor side: at/above ask = buy, at/below bid = sell, otherwise tick rule
        private int ClassifySide(double price)
        {
            int tickRule = lastPrice == 0 ? lastSide
                : price > lastPrice ? 1
                : price < lastPrice ? -1
                : lastSide;

            double ask, bid;
            if (State == State.Realtime)
            {
                ask = GetCurrentAsk(1);
                bid = GetCurrentBid(1);
            }
            else
            {
                ask = BarsArray[1].GetAsk(CurrentBars[1]);
                bid = BarsArray[1].GetBid(CurrentBars[1]);
            }

            if (ask > 0 && bid > 0 && ask > bid)
            {
                if (price >= ask) return 1;
                if (price <= bid) return -1;
            }
            return tickRule;
        }

        // =====================================================================
        //  COLORS
        // =====================================================================
        private static Brush[] BuildPalette(Brush from, Brush to, int steps)
        {
            Color a = ToColor(from, Colors.Gray);
            Color b = ToColor(to, Colors.White);
            var palette = new Brush[steps + 1];
            for (int k = 0; k <= steps; k++)
            {
                double f = (double)k / steps;
                var c = Color.FromArgb(
                    (byte)Math.Round(a.A + (b.A - a.A) * f),
                    (byte)Math.Round(a.R + (b.R - a.R) * f),
                    (byte)Math.Round(a.G + (b.G - a.G) * f),
                    (byte)Math.Round(a.B + (b.B - a.B) * f));
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                palette[k] = brush;
            }
            return palette;
        }

        private static Color ToColor(Brush b, Color fallback)
        {
            var sc = b as SolidColorBrush;
            return sc != null ? sc.Color : fallback;
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
        public Series<double> BarDeltaSeries { get { return Values[0]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Dominance_ { get { return Values[1]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> IntensityPct { get { return Values[2]; } }

        // ===== Parameters =====
        [NinjaScriptProperty]
        [Display(Name = "Source", Order = 0, GroupName = "Parameters",
            Description = "BarDelta = raw delta per bar. EmaDelta = EMA of the bar delta. CumulativeChange = sum of the last N bar deltas.")]
        public DeltaMapSource Source { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Period", Order = 1, GroupName = "Parameters",
            Description = "Smoothing bars (EmaDelta / CumulativeChange). Higher = longer color runs, lower = faster flips.")]
        public int Period { get; set; }

        [Range(2, 1000)]
        [Display(Name = "Lookback (Intensity)", Order = 2, GroupName = "Parameters",
            Description = "Previous bars used to judge how strong the current dominance is.")]
        public int Lookback { get; set; }

        [Range(0.1, 20)]
        [Display(Name = "Brightest At (x avg)", Order = 3, GroupName = "Parameters",
            Description = "A bar reaches the strong shade when |dominance| >= this multiple of the average |dominance|. Lower = more bright bars.")]
        public double FullAtMultiple { get; set; }

        [Range(0, 365)]
        [Display(Name = "Days To Calculate", Order = 4, GroupName = "Parameters",
            Description = "Only the last N trading sessions are calculated and colored. 0 = all loaded days.")]
        public int DaysToCalculate { get; set; }

        // ===== Colors =====
        [XmlIgnore]
        [Display(Name = "Bull Weak", Order = 0, GroupName = "Colors")]
        public Brush BullWeak { get; set; }

        [Browsable(false)]
        public string BullWeakSerialize
        {
            get { return Serialize.BrushToString(BullWeak); }
            set { BullWeak = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bull Strong", Order = 1, GroupName = "Colors")]
        public Brush BullStrong { get; set; }

        [Browsable(false)]
        public string BullStrongSerialize
        {
            get { return Serialize.BrushToString(BullStrong); }
            set { BullStrong = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bear Weak", Order = 2, GroupName = "Colors")]
        public Brush BearWeak { get; set; }

        [Browsable(false)]
        public string BearWeakSerialize
        {
            get { return Serialize.BrushToString(BearWeak); }
            set { BearWeak = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bear Strong", Order = 3, GroupName = "Colors")]
        public Brush BearStrong { get; set; }

        [Browsable(false)]
        public string BearStrongSerialize
        {
            get { return Serialize.BrushToString(BearStrong); }
            set { BearStrong = Serialize.StringToBrush(value); }
        }

        [Range(2, 100)]
        [Display(Name = "Gradient Steps", Order = 4, GroupName = "Colors",
            Description = "Number of shades between the weak and the strong color.")]
        public int GradientSteps { get; set; }

        [Display(Name = "Color Outline / Wick", Order = 5, GroupName = "Colors",
            Description = "Also color the candle outline and wick.")]
        public bool ColorOutline { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private giuli13.DeltaMap[] cacheDeltaMap;
		public giuli13.DeltaMap DeltaMap(DeltaMapSource source, int period)
		{
			return DeltaMap(Input, source, period);
		}

		public giuli13.DeltaMap DeltaMap(ISeries<double> input, DeltaMapSource source, int period)
		{
			if (cacheDeltaMap != null)
				for (int idx = 0; idx < cacheDeltaMap.Length; idx++)
					if (cacheDeltaMap[idx] != null && cacheDeltaMap[idx].Source == source && cacheDeltaMap[idx].Period == period && cacheDeltaMap[idx].EqualsInput(input))
						return cacheDeltaMap[idx];
			return CacheIndicator<giuli13.DeltaMap>(new giuli13.DeltaMap(){ Source = source, Period = period }, input, ref cacheDeltaMap);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.DeltaMap DeltaMap(DeltaMapSource source, int period)
		{
			return indicator.DeltaMap(Input, source, period);
		}

		public Indicators.giuli13.DeltaMap DeltaMap(ISeries<double> input , DeltaMapSource source, int period)
		{
			return indicator.DeltaMap(input, source, period);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.DeltaMap DeltaMap(DeltaMapSource source, int period)
		{
			return indicator.DeltaMap(Input, source, period);
		}

		public Indicators.giuli13.DeltaMap DeltaMap(ISeries<double> input , DeltaMapSource source, int period)
		{
			return indicator.DeltaMap(input, source, period);
		}
	}
}

#endregion
