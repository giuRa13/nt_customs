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

// Speed of Tape - bottom panel.
// For every chart bar it measures how much activity (volume, orders or trades) happened in a rolling
// window of "Number Seconds", using a 1-tick data series (no Tick Replay needed).
//   Body  = speed at the last trade of the bar (activity in the last N seconds)
//   Wick  = peak speed reached during the bar
//   Color = bull / bear from the bar delta (aggressive buys vs sells) or from the price bar direction
// Bars that pass the filters (Filter Min / Filter Max / Std Dev) are drawn at full color,
// the others are faded ("Unmarked Opacity").

namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
    public enum SpeedOfTapeInput
    {
        Volume = 0,     // contracts traded
        Orders = 1,     // aggressive orders: consecutive prints with the same timestamp and side count as one
        Trades = 2      // number of prints
    }

    public enum SpeedOfTapeColorBy
    {
        Delta = 0,      // buy vs sell aggression inside the bar
        PriceBar = 1    // close >= open of the price bar
    }

    public class SpeedOfTape : Indicator
    {
        private class BarStat
        {
            public double Speed;    // rolling-window value at the last trade of the bar
            public double Peak;     // highest rolling-window value during the bar
            public double Buy;      // aggressive buy amount (same unit as Input Data)
            public double Sell;     // aggressive sell amount
        }

        private struct TapeEvent
        {
            public readonly DateTime Time;
            public readonly double Amount;
            public TapeEvent(DateTime time, double amount) { Time = time; Amount = amount; }
        }

        // ---- data (instrument thread writes, UI thread reads -> lock) ----
        private readonly object sync = new object();
        private List<BarStat> stats;
        private Queue<TapeEvent> window;
        private double windowSum;

        private double lastPrice;
        private int lastSide = 1;
        private DateTime lastTradeTime = DateTime.MinValue;
        private DateTime calcStart = DateTime.MinValue;   // ticks at or before this time are skipped

        // ---- SharpDX resources (UI thread) ----
        private SharpDX.Direct2D1.Brush dxBullFill, dxBullBorder, dxBearFill, dxBearBorder;
        private SharpDX.Direct2D1.StrokeStyle dxStroke;
        private SharpDX.Direct2D1.SolidColorBrush dxGradFill, dxGradBorder;   // recolored per bar
        private SharpDX.Color4 cBullFill, cBullBorder, cBearFill, cBearBorder, cNeutral;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SpeedOfTape";
                Description = "Speed of Tape: activity (volume / orders / trades) in a rolling N-second window, drawn as bars in a bottom panel." +
                    "\r\n- Body: speed at the end of the candle (activity in the last N seconds)." +
                    "\r\n- Wick: the fastest moment reached inside the candle." +
                    "\r\n- Long wick, short body: there was a burst of activity during the candle, but it ended before the close. Something happened (a big player, a stop run, a level being hit) and then the market calmed down." +
                    "\r\n- No wick, or a very short one: the candle ended at its fastest moment. The pressure is still active right now." +
                    "\r\n- Color: every trade has both a buyer and a seller. Bull color means aggressive buyers (who buy at the ask, i.e. market orders) traded more than aggressive sellers during that candle; bear color means the opposite." +
                    "\r\n- Gradient (faded bars): the closer buyers and sellers are to balance, the closer the bar is to the neutral color; the stronger the imbalance, the closer it is to the full bull / bear color." +
                    "\r\n- Filters: to show ONLY the bars that pass Filter Min / Filter Max, set Std Dev Per Filter = 0 and Unmarked Opacity = 0. The filter checks the body value, not the wick.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                DrawOnPricePanel = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;   // the rolling window must see every tick
                BarsRequiredToPlot = 0;
                ScaleJustification = ScaleJustification.Right;

                // Parameters
                InputData = SpeedOfTapeInput.Volume;
                NumberSeconds = 10;
                FilterMin = 0;
                FilterMax = 0;
                StdDevFilter = 0.5;
                StdDevPeriod = 50;
                ColorBy = SpeedOfTapeColorBy.Delta;
                DaysToCalculate = 3;

                // Subgraphs
                /*BullBorder = MakeBrush("#FF1B88F4");
                BullFill = MakeBrush("#FF1B88F4");
                BearBorder = MakeBrush("#FFD83C3C");
                BearFill = MakeBrush("#FFD83C3C");*/
                BullBorder = MakeBrush("#FF1B88F4");
                BullFill = MakeBrush("#FF1B88F4");
                BearBorder = MakeBrush("#FFFF3755");
                BearFill = MakeBrush("#FFFF3755");
                UnmarkedOpacity = 35;
                UseGradient = true;
                GradientNeutral = Brushes.White;
                GradientFullAt = 50;
                LineStyle = DashStyleHelper.Solid;
                LineWidth = 1;
                ShortName = "Speed of Tape";

                // transparent plots: they feed the Data Box and strategies; drawing is done in OnRender
                AddPlot(Brushes.Transparent, "Speed");
                AddPlot(Brushes.Transparent, "Peak");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                stats = new List<BarStat>();
                window = new Queue<TapeEvent>();
                windowSum = 0;
                lastPrice = 0;
                lastSide = 1;
                lastTradeTime = DateTime.MinValue;

                // Only the last N trading sessions (chart trading hours) are calculated.
                // Walk back over the chart bars and stop at the first bar of the N-th session.
                calcStart = DateTime.MinValue;
                if (DaysToCalculate > 0 && BarsArray[0].Count > 0)
                {
                    int sessions = 0;
                    for (int i = BarsArray[0].Count - 1; i >= 0; i--)
                    {
                        if (BarsArray[0].IsFirstBarOfSessionByIndex(i) && ++sessions >= DaysToCalculate)
                        {
                            // bars are stamped with their close time: ticks after the previous bar's time belong to this session
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
            get { return (string.IsNullOrWhiteSpace(ShortName) ? "Speed of Tape" : ShortName) + " (" + NumberSeconds + ")"; }
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

            BarStat s;
            lock (sync)
                s = GetStat(CurrentBar, false);
            Values[0][0] = s != null ? s.Speed : 0;
            Values[1][0] = s != null ? s.Peak : 0;
        }

        private void ProcessTick()
        {
            if (CurrentBars[1] < 0)
                return;

            DateTime t = Times[1][0];
            if (t <= calcStart)
                return;   // older than "Days To Calculate": skip, almost no cost
            double price = Closes[1][0];
            double vol = Volumes[1][0];
            int side = ClassifySide(price);

            double amount;
            switch (InputData)
            {
                case SpeedOfTapeInput.Trades: amount = 1; break;
                case SpeedOfTapeInput.Orders: amount = (t != lastTradeTime || side != lastSide) ? 1 : 0; break;
                default: amount = vol; break;
            }

            lastPrice = price;
            lastSide = side;
            lastTradeTime = t;

            // rolling window of NumberSeconds
            if (amount > 0)
            {
                window.Enqueue(new TapeEvent(t, amount));
                windowSum += amount;
            }
            DateTime cutoff = t.AddSeconds(-NumberSeconds);
            while (window.Count > 0 && window.Peek().Time <= cutoff)
                windowSum -= window.Dequeue().Amount;
            if (window.Count == 0)
                windowSum = 0;  // no floating point drift

            // Which chart bar does this trade belong to? Primary bars are stamped with their close time:
            // a trade later than the last primary bar's time belongs to the next (still forming) bar.
            int idx = (CurrentBars[0] < 0 || t > Times[0][0]) ? CurrentBars[0] + 1 : CurrentBars[0];
            if (idx < 0)
                return;

            BarStat s;
            lock (sync)
            {
                s = GetStat(idx, true);
                s.Speed = windowSum;
                if (windowSum > s.Peak) s.Peak = windowSum;
                if (side > 0) s.Buy += amount; else s.Sell += amount;
            }

            if (idx == CurrentBars[0] && CurrentBars[0] >= 0)
            {
                Values[0][0] = s.Speed;
                Values[1][0] = s.Peak;
            }
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

        // call inside lock
        private BarStat GetStat(int idx, bool create)
        {
            if (idx < 0) return null;
            if (create)
                while (stats.Count <= idx)
                    stats.Add(new BarStat());
            return idx < stats.Count ? stats[idx] : null;
        }

        // call inside lock
        private bool IsMarked(int i, double v)
        {
            if (v <= 0 || v < FilterMin) return false;
            if (FilterMax > 0 && v > FilterMax) return false;

            if (StdDevFilter > 0)
            {
                int start = Math.Max(0, i - StdDevPeriod);
                int n = i - start;
                if (n >= 2)
                {
                    double sum = 0, sumSq = 0;
                    for (int k = start; k < i; k++)
                    {
                        double x = stats[k].Speed;
                        sum += x;
                        sumSq += x * x;
                    }
                    double mean = sum / n;
                    double std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
                    if (v < mean + StdDevFilter * std) return false;
                }
            }
            return true;
        }

        private bool IsBull(int i, BarStat s)
        {
            if (ColorBy == SpeedOfTapeColorBy.Delta)
            {
                double d = s.Buy - s.Sell;
                if (d != 0) return d > 0;
            }
            return BarsArray[0].GetClose(i) >= BarsArray[0].GetOpen(i);
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
                    if (stats[i].Peak > max) max = stats[i].Peak;
            }
            MinValue = 0;
            MaxValue = max > 0 ? max : 1;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (ChartBars == null || stats == null || RenderTarget == null)
                return;
            if (dxBullFill == null)
                OnRenderTargetChanged();
            if (dxBullFill == null)
                return;

            var oldAA = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

            float half = (float)Math.Max(1.0, chartControl.GetBarPaintWidth(ChartBars)) / 2f;
            float yZero = chartScale.GetYByValue(0);
            float faded = UnmarkedOpacity / 100f;

            lock (sync)
            {
                int from = Math.Max(0, ChartBars.FromIndex);
                int to = Math.Min(ChartBars.ToIndex, stats.Count - 1);

                for (int i = from; i <= to; i++)
                {
                    BarStat s = stats[i];
                    if (s.Peak <= 0 && s.Speed <= 0) continue;

                    bool bull = IsBull(i, s);
                    bool marked = IsMarked(i, s.Speed);
                    float op = marked ? 1f : faded;

                    SharpDX.Direct2D1.Brush fill, border;
                    if (!marked && UseGradient)
                    {
                        // 0 = buyers and sellers balanced (neutral color), 1 = imbalance >= "Gradient Full At" (full color)
                        double total = s.Buy + s.Sell;
                        float k = total > 0
                            ? (float)Math.Min(1.0, Math.Abs(s.Buy - s.Sell) / total / (GradientFullAt / 100.0))
                            : 0f;
                        dxGradFill.Color = Lerp(cNeutral, bull ? cBullFill : cBearFill, k);
                        dxGradBorder.Color = Lerp(cNeutral, bull ? cBullBorder : cBearBorder, k);
                        fill = dxGradFill;
                        border = dxGradBorder;
                    }
                    else
                    {
                        fill = bull ? dxBullFill : dxBearFill;
                        border = bull ? dxBullBorder : dxBearBorder;
                    }
                    fill.Opacity = op;
                    border.Opacity = op;

                    float x = chartControl.GetXByBarIndex(ChartBars, i);
                    float yBody = chartScale.GetYByValue(s.Speed);
                    float yPeak = chartScale.GetYByValue(s.Peak);

                    // wick: from the top of the body up to the peak
                    if (s.Peak > s.Speed)
                        RenderTarget.DrawLine(new SharpDX.Vector2(x, yBody), new SharpDX.Vector2(x, yPeak), border, LineWidth, dxStroke);

                    // body: from 0 up to the current speed
                    if (yZero - yBody >= 1)
                    {
                        var rect = new SharpDX.RectangleF(x - half, yBody, half * 2f, yZero - yBody);
                        RenderTarget.FillRectangle(rect, fill);
                        RenderTarget.DrawRectangle(rect, border, LineWidth, dxStroke);
                    }
                }
            }

            dxBullFill.Opacity = dxBullBorder.Opacity = dxBearFill.Opacity = dxBearBorder.Opacity = 1f;
            RenderTarget.AntialiasMode = oldAA;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            if (RenderTarget == null)
                return;

            dxBullFill = (BullFill ?? Brushes.Green).ToDxBrush(RenderTarget);
            dxBullBorder = (BullBorder ?? Brushes.Green).ToDxBrush(RenderTarget);
            dxBearFill = (BearFill ?? Brushes.Red).ToDxBrush(RenderTarget);
            dxBearBorder = (BearBorder ?? Brushes.Red).ToDxBrush(RenderTarget);

            dxStroke = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = ToDxDash(LineStyle) });

            // gradient: two brushes recolored per bar + the colors to blend
            dxGradFill = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 1f, 1f, 1f));
            dxGradBorder = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 1f, 1f, 1f));
            cBullFill = ToColor4(BullFill, Colors.Green);
            cBullBorder = ToColor4(BullBorder, Colors.Green);
            cBearFill = ToColor4(BearFill, Colors.Red);
            cBearBorder = ToColor4(BearBorder, Colors.Red);
            cNeutral = ToColor4(GradientNeutral, Colors.White);
        }

        private static SharpDX.Color4 ToColor4(Brush b, Color fallback)
        {
            var sc = b as SolidColorBrush;
            Color c = sc != null ? sc.Color : fallback;
            return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
        }

        private static SharpDX.Color4 Lerp(SharpDX.Color4 a, SharpDX.Color4 b, float k)
        {
            return new SharpDX.Color4(
                a.Red + (b.Red - a.Red) * k,
                a.Green + (b.Green - a.Green) * k,
                a.Blue + (b.Blue - a.Blue) * k,
                a.Alpha + (b.Alpha - a.Alpha) * k);
        }

        private static SharpDX.Direct2D1.DashStyle ToDxDash(DashStyleHelper s)
        {
            switch (s)
            {
                case DashStyleHelper.Dash: return SharpDX.Direct2D1.DashStyle.Dash;
                case DashStyleHelper.DashDot: return SharpDX.Direct2D1.DashStyle.DashDot;
                case DashStyleHelper.DashDotDot: return SharpDX.Direct2D1.DashStyle.DashDotDot;
                case DashStyleHelper.Dot: return SharpDX.Direct2D1.DashStyle.Dot;
                default: return SharpDX.Direct2D1.DashStyle.Solid;
            }
        }

        private void DisposeDx()
        {
            if (dxBullFill != null) { dxBullFill.Dispose(); dxBullFill = null; }
            if (dxBullBorder != null) { dxBullBorder.Dispose(); dxBullBorder = null; }
            if (dxBearFill != null) { dxBearFill.Dispose(); dxBearFill = null; }
            if (dxBearBorder != null) { dxBearBorder.Dispose(); dxBearBorder = null; }
            if (dxStroke != null) { dxStroke.Dispose(); dxStroke = null; }
            if (dxGradFill != null) { dxGradFill.Dispose(); dxGradFill = null; }
            if (dxGradBorder != null) { dxGradBorder.Dispose(); dxGradBorder = null; }
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
        public Series<double> Speed { get { return Values[0]; } }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Peak { get { return Values[1]; } }

        // ===== Parameters =====
        [NinjaScriptProperty]
        [Display(Name = "Input Data", Order = 0, GroupName = "Parameters",
            Description = "Volume = contracts traded. Orders = aggressive orders (prints with same timestamp and side merged). Trades = number of prints.")]
        public SpeedOfTapeInput InputData { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Filter Min", Order = 1, GroupName = "Parameters",
            Description = "Only bars with speed at or above this value are marked (full color).")]
        public double FilterMin { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Filter Max", Order = 2, GroupName = "Parameters",
            Description = "Only bars with speed at or below this value are marked. 0 = no maximum.")]
        public double FilterMax { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Number Seconds", Order = 3, GroupName = "Parameters",
            Description = "Length of the rolling window (seconds) used to measure the speed.")]
        public int NumberSeconds { get; set; }

        [Range(0, 10)]
        [Display(Name = "Std Dev Per Filter", Order = 4, GroupName = "Parameters",
            Description = "A bar is marked only if its speed >= mean + N x std dev of the previous bars. 0 = off.")]
        public double StdDevFilter { get; set; }

        [Range(2, 1000)]
        [Display(Name = "Std Dev Period", Order = 5, GroupName = "Parameters",
            Description = "Number of previous bars used for the mean / std dev.")]
        public int StdDevPeriod { get; set; }

        [Display(Name = "Color By", Order = 6, GroupName = "Parameters",
            Description = "Delta = aggressive buys vs sells in the bar. PriceBar = direction of the price bar.")]
        public SpeedOfTapeColorBy ColorBy { get; set; }

        [Range(0, 365)]
        [Display(Name = "Days To Calculate", Order = 7, GroupName = "Parameters",
            Description = "Only the last N trading sessions are calculated and drawn (sessions of the chart's trading hours). 0 = all loaded days.")]
        public int DaysToCalculate { get; set; }

        // ===== Subgraphs =====
        [XmlIgnore]
        [Display(Name = "Bull Border", Order = 0, GroupName = "Subgraphs")]
        public Brush BullBorder { get; set; }

        [Browsable(false)]
        public string BullBorderSerialize
        {
            get { return Serialize.BrushToString(BullBorder); }
            set { BullBorder = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bull Fill", Order = 1, GroupName = "Subgraphs")]
        public Brush BullFill { get; set; }

        [Browsable(false)]
        public string BullFillSerialize
        {
            get { return Serialize.BrushToString(BullFill); }
            set { BullFill = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bear Border", Order = 2, GroupName = "Subgraphs")]
        public Brush BearBorder { get; set; }

        [Browsable(false)]
        public string BearBorderSerialize
        {
            get { return Serialize.BrushToString(BearBorder); }
            set { BearBorder = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bear Fill", Order = 3, GroupName = "Subgraphs")]
        public Brush BearFill { get; set; }

        [Browsable(false)]
        public string BearFillSerialize
        {
            get { return Serialize.BrushToString(BearFill); }
            set { BearFill = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [Display(Name = "Unmarked Opacity (%)", Order = 4, GroupName = "Subgraphs",
            Description = "Opacity of the bars that do not pass the filters. Set 0 (with Std Dev Per Filter = 0) to show only the bars between Filter Min and Filter Max. With the gradient on, 100 gives solid pastel bars.")]
        public int UnmarkedOpacity { get; set; }

        [Display(Name = "Gradient On Faded Bars", Order = 8, GroupName = "Subgraphs",
            Description = "On: faded bars blend from the neutral color (buyers = sellers) to the bull / bear color (strong imbalance). Off: faded bars use the plain bull / bear color.")]
        public bool UseGradient { get; set; }

        [XmlIgnore]
        [Display(Name = "Gradient Neutral Color", Order = 9, GroupName = "Subgraphs",
            Description = "Color of a faded bar when buyers and sellers are balanced.")]
        public Brush GradientNeutral { get; set; }

        [Browsable(false)]
        public string GradientNeutralSerialize
        {
            get { return Serialize.BrushToString(GradientNeutral); }
            set { GradientNeutral = Serialize.StringToBrush(value); }
        }

        [Range(1, 100)]
        [Display(Name = "Gradient Full Color At (%)", Order = 10, GroupName = "Subgraphs",
            Description = "Imbalance at which a faded bar reaches the full bull / bear color. Imbalance = |buy - sell| / (buy + sell). Lower = more colorful, higher = more neutral.")]
        public int GradientFullAt { get; set; }

        [Display(Name = "Line Style", Order = 5, GroupName = "Subgraphs")]
        public DashStyleHelper LineStyle { get; set; }

        [Range(1, 10)]
        [Display(Name = "Line Width", Order = 6, GroupName = "Subgraphs",
            Description = "Width of the candle border and wick.")]
        public int LineWidth { get; set; }

        [Display(Name = "Short Name", Order = 7, GroupName = "Subgraphs")]
        public string ShortName { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private giuli13.SpeedOfTape[] cacheSpeedOfTape;
		public giuli13.SpeedOfTape SpeedOfTape(SpeedOfTapeInput inputData, int numberSeconds)
		{
			return SpeedOfTape(Input, inputData, numberSeconds);
		}

		public giuli13.SpeedOfTape SpeedOfTape(ISeries<double> input, SpeedOfTapeInput inputData, int numberSeconds)
		{
			if (cacheSpeedOfTape != null)
				for (int idx = 0; idx < cacheSpeedOfTape.Length; idx++)
					if (cacheSpeedOfTape[idx] != null && cacheSpeedOfTape[idx].InputData == inputData && cacheSpeedOfTape[idx].NumberSeconds == numberSeconds && cacheSpeedOfTape[idx].EqualsInput(input))
						return cacheSpeedOfTape[idx];
			return CacheIndicator<giuli13.SpeedOfTape>(new giuli13.SpeedOfTape(){ InputData = inputData, NumberSeconds = numberSeconds }, input, ref cacheSpeedOfTape);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.SpeedOfTape SpeedOfTape(SpeedOfTapeInput inputData, int numberSeconds)
		{
			return indicator.SpeedOfTape(Input, inputData, numberSeconds);
		}

		public Indicators.giuli13.SpeedOfTape SpeedOfTape(ISeries<double> input , SpeedOfTapeInput inputData, int numberSeconds)
		{
			return indicator.SpeedOfTape(input, inputData, numberSeconds);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.SpeedOfTape SpeedOfTape(SpeedOfTapeInput inputData, int numberSeconds)
		{
			return indicator.SpeedOfTape(Input, inputData, numberSeconds);
		}

		public Indicators.giuli13.SpeedOfTape SpeedOfTape(ISeries<double> input , SpeedOfTapeInput inputData, int numberSeconds)
		{
			return indicator.SpeedOfTape(input, inputData, numberSeconds);
		}
	}
}

#endregion
