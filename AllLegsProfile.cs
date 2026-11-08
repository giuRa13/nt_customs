#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Xml.Serialization;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;

// IMPORTANT: alias WPF brushes to avoid conflict with SharpDX.Direct2D1.Brush
using WpfBrush = System.Windows.Media.Brush;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfColors = System.Windows.Media.Colors;
using WpfBrushes = System.Windows.Media.Brushes;
#endregion

// NOTE: enum / class names are prefixed with "AllLegs" so this indicator can live
// side by side with LegToLegDeltaProfile without type-name conflicts.
// Everything lives in NinjaTrader.NinjaScript.Indicators so it compiles from any folder.
// The "NinjaScript generated code" region is intentionally omitted:
// NinjaTrader adds it automatically the first time you compile.

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum AllLegsContent
    {
        Delta = 0,
        Volume = 1,
        DeltaAndVolume = 2   // volume drawn behind delta
    }

    public enum AllLegsWidthMode
    {
        FitToLeg = 0,   // width = leg width in pixels (capped by Max Width)
        Fixed = 1       // width = Max Width
    }

    public enum AllLegsPivotMode
    {
        PivotToEndingLeg = 0,    // pivot bar belongs to the leg that made the extreme; new leg starts on the NEXT bar
        PivotToStartingLeg = 1,  // pivot bar belongs entirely to the new leg
        SplitByTick = 2          // pivot bar split by tick order around the extreme
    }

    public enum AllLegsRotationMode
    {
        DistanceFromExtreme = 0,
        TrueSwingAlternate = 1
    }

    internal enum AllLegsDir
    {
        Unknown = 0,
        Up = 1,
        Down = -1
    }

    internal class AllLegsAgg
    {
        public int Start;
        public int End;
        public int StartType;
        public int EndType;
        public double Hi = double.MinValue;
        public double Lo = double.MaxValue;
        public Dictionary<double, long> Delta = new Dictionary<double, long>();
        public Dictionary<double, long> Vol = new Dictionary<double, long>();
    }

    public class AllLegsProfile : Indicator
    {
        private readonly object sync = new object();

        // Per primary bar maps (price -> delta / volume)
        private List<Dictionary<double, long>> barDeltaMaps;
        private List<Dictionary<double, long>> barVolMaps;

        // Per primary bar: the part of the bar traded AFTER the last print of the bar's high / low.
        // Used to split a pivot bar between the leg that ends at the pivot and the leg that starts there,
        // so the same ticks are never counted in two legs.
        private List<Dictionary<double, long>> barAfterHighDelta;
        private List<Dictionary<double, long>> barAfterHighVol;
        private List<Dictionary<double, long>> barAfterLowDelta;
        private List<Dictionary<double, long>> barAfterLowVol;
        private int curTickBar = -1;
        private double curBarHigh = double.MinValue;
        private double curBarLow = double.MaxValue;

        // Start bar of every leg (ascending). Leg i spans legStarts[i] .. (legStarts[i+1] or last bar)
        private List<int> legStarts;
        // Type of each leg start: +1 = starts at a swing HIGH pivot, -1 = starts at a swing LOW pivot,
        // 0 = starts at the beginning of the bar (session start / distance rotation)
        private List<int> legTypes;

        // Aggregation cache for COMPLETED legs (key = start bar)
        private Dictionary<int, AllLegsAgg> aggCache;

        // Rotation tracking
        private double legHigh = double.NaN;
        private double legLow = double.NaN;
        private int legHighBar = -1;
        private int legLowBar = -1;
        private AllLegsDir legDir = AllLegsDir.Unknown;
        private int lastSessionBarHandled = -1;

        // Bid/Ask cache (Tick Replay)
        private double lastBid = double.NaN;
        private double lastAsk = double.NaN;
        private double prevLast = double.NaN;
        private int lastDir = 0;      // last known aggressor direction (+1 buy / -1 sell)
        private long quoteVol = 0;      // volume classified with real bid/ask
        private long tickRuleVol = 0;   // volume classified with the tick-rule fallback
        private TextFormat infoFormat;

        // Temp bin dictionaries (UI thread only)
        private readonly Dictionary<long, long> binsDelta = new Dictionary<long, long>();
        private readonly Dictionary<long, long> binsVol = new Dictionary<long, long>();

        // Rendering resources
        private TextFormat textFormat;
        private SolidColorBrush posBrushDx, negBrushDx, textBrushDx, spineBrushDx, volBrushDx;
        private string lastPosSer, lastNegSer, lastTextSer, lastVolSer, lastSpineSer;
        private float lastDeltaOpacity = -1f;
        private float lastVolOpacity = -1f;
        private int lastFontSize = -1;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "AllLegsProfile";
                Description = "Draws a delta and/or volume profile for EVERY leg (rotation based), newest leg on the right, oldest on the left.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;

                // Leg detection
                RotationMode = AllLegsRotationMode.TrueSwingAlternate;
                RotationPoints = 65;
                ResetOnNewSession = true;
                PivotBarMode = AllLegsPivotMode.PivotToEndingLeg;

                // What to show
                Content = AllLegsContent.Delta;
                LegsToDisplay = 10;
                TicksPerRow = 1;

                // Layout (spine always at leg END, profile grows LEFT toward leg START)
                WidthMode = AllLegsWidthMode.FitToLeg;
                MaxProfileWidthPx = 600;

                // Spine
                ShowSpine = true;
                SpineWidthPx = 2;
                ShowBottomLine = true;
                SpineBrush = WpfBrushes.Gray;

                // Delta
                MinAbsDeltaToShow = 1;
                DeltaOpacity = 0.85f;
                PositiveBrush = WpfBrushes.Blue;
                NegativeBrush = WpfBrushes.Red;

                // Volume
                VolumeOpacity = 0.35f;
                VolumeBrush = WpfBrushes.Gray;

                // Text
                ShowValues = true;
                ShowDeltaSourceInfo = true;
                AutoFontMin = 6;
                AutoFontMax = 18;
                TextBrush = WpfBrushes.LightGray;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                barDeltaMaps = new List<Dictionary<double, long>>(4096);
                barVolMaps = new List<Dictionary<double, long>>(4096);
                legStarts = new List<int>(256);
                legTypes = new List<int>(256);
                barAfterHighDelta = new List<Dictionary<double, long>>(4096);
                barAfterHighVol = new List<Dictionary<double, long>>(4096);
                barAfterLowDelta = new List<Dictionary<double, long>>(4096);
                barAfterLowVol = new List<Dictionary<double, long>>(4096);
                curTickBar = -1;
                curBarHigh = double.MinValue;
                curBarLow = double.MaxValue;
                aggCache = new Dictionary<int, AllLegsAgg>();

                legHigh = legLow = double.NaN;
                legHighBar = legLowBar = -1;
                legDir = AllLegsDir.Unknown;
                lastSessionBarHandled = -1;
                prevLast = double.NaN;
                lastDir = 0;
                quoteVol = 0;
                tickRuleVol = 0;

                lastPosSer = lastNegSer = lastTextSer = lastVolSer = lastSpineSer = null;
                lastDeltaOpacity = lastVolOpacity = -1f;
                lastFontSize = -1;
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
            }
        }

        private void DisposeDx()
        {
            try
            {
                textFormat?.Dispose();
                infoFormat?.Dispose();
                posBrushDx?.Dispose();
                negBrushDx?.Dispose();
                textBrushDx?.Dispose();
                spineBrushDx?.Dispose();
                volBrushDx?.Dispose();
            }
            catch { }
            finally
            {
                textFormat = null;
                infoFormat = null;
                posBrushDx = negBrushDx = textBrushDx = spineBrushDx = volBrushDx = null;
                lastFontSize = -1;
            }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();
            base.OnRenderTargetChanged();
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Bid)
                lastBid = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask)
                lastAsk = e.Price;
        }

        // =====================================================================
        //  DATA / LEG DETECTION
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                ProcessTickIntoPrimaryBar();
                return;
            }

            if (BarsInProgress != 0 || CurrentBar < 1)
                return;

            lock (sync)
            {
                EnsureBarMaps(CurrentBar);

                if (legStarts.Count == 0)
                {
                    AddLegStart(0);
                    ResetExtremesToCurrentBar();
                    legDir = AllLegsDir.Unknown;
                }

                if (ResetOnNewSession && Bars.IsFirstBarOfSession && lastSessionBarHandled != CurrentBar)
                {
                    lastSessionBarHandled = CurrentBar;
                    AddLegStart(CurrentBar);
                    ResetExtremesToCurrentBar();
                    legDir = AllLegsDir.Unknown;
                }

                // Update extremes of current leg + remember where they occurred
                if (double.IsNaN(legHigh) || double.IsNaN(legLow))
                {
                    ResetExtremesToCurrentBar();
                }
                else
                {
                    if (High[0] >= legHigh) { legHigh = High[0]; legHighBar = CurrentBar; }
                    if (Low[0] <= legLow) { legLow = Low[0]; legLowBar = CurrentBar; }
                }

                if (RotationPoints > 0)
                {
                    double last = Close[0];

                    if (RotationMode == AllLegsRotationMode.DistanceFromExtreme)
                    {
                        bool rotateDown = last <= (legHigh - RotationPoints);
                        bool rotateUp = last >= (legLow + RotationPoints);

                        if (rotateDown || rotateUp)
                        {
                            AddLegStart(CurrentBar);
                            ResetExtremesToCurrentBar();
                            legDir = AllLegsDir.Unknown;
                        }
                    }
                    else // TrueSwingAlternate
                    {
                        if (legDir == AllLegsDir.Unknown)
                        {
                            if (last >= (legLow + RotationPoints))
                                legDir = AllLegsDir.Up;
                            else if (last <= (legHigh - RotationPoints))
                                legDir = AllLegsDir.Down;
                        }

                        if (legDir == AllLegsDir.Up)
                        {
                            if (last <= (legHigh - RotationPoints))
                                StartNewLegAtBar(legHighBar, AllLegsDir.Down, last);
                        }
                        else if (legDir == AllLegsDir.Down)
                        {
                            if (last >= (legLow + RotationPoints))
                                StartNewLegAtBar(legLowBar, AllLegsDir.Up, last);
                        }
                    }
                }
            }
        }

        private void ResetExtremesToCurrentBar()
        {
            legHigh = High[0];
            legLow = Low[0];
            legHighBar = CurrentBar;
            legLowBar = CurrentBar;
        }

        private void AddLegStart(int bar, int type = 0)
        {
            bar = Math.Max(0, bar);

            // keep list strictly ascending
            while (legStarts.Count > 0 && legStarts[legStarts.Count - 1] > bar)
            {
                legStarts.RemoveAt(legStarts.Count - 1);
                legTypes.RemoveAt(legTypes.Count - 1);
            }

            if (legStarts.Count > 0 && legStarts[legStarts.Count - 1] == bar)
                return;

            legStarts.Add(bar);
            legTypes.Add(type);
        }

        private void StartNewLegAtBar(int startBar, AllLegsDir newDir, double lastPrice)
        {
            // Down leg starts at a swing HIGH (+1), Up leg starts at a swing LOW (-1)
            AddLegStart(startBar, newDir == AllLegsDir.Down ? +1 : (newDir == AllLegsDir.Up ? -1 : 0));
            legDir = newDir;

            if (newDir == AllLegsDir.Down)
            {
                legLow = lastPrice;          // keep swing high as anchor
                legLowBar = CurrentBar;
            }
            else if (newDir == AllLegsDir.Up)
            {
                legHigh = lastPrice;         // keep swing low as anchor
                legHighBar = CurrentBar;
            }
            else
            {
                ResetExtremesToCurrentBar();
            }
        }

        private void EnsureBarMaps(int primaryBarIndex)
        {
            while (barDeltaMaps.Count <= primaryBarIndex)
                barDeltaMaps.Add(new Dictionary<double, long>());

            while (barVolMaps.Count <= primaryBarIndex)
                barVolMaps.Add(new Dictionary<double, long>());

            while (barAfterHighDelta.Count <= primaryBarIndex) barAfterHighDelta.Add(new Dictionary<double, long>());
            while (barAfterHighVol.Count <= primaryBarIndex) barAfterHighVol.Add(new Dictionary<double, long>());
            while (barAfterLowDelta.Count <= primaryBarIndex) barAfterLowDelta.Add(new Dictionary<double, long>());
            while (barAfterLowVol.Count <= primaryBarIndex) barAfterLowVol.Add(new Dictionary<double, long>());
        }

        private static void AddTo(Dictionary<double, long> map, double price, long value)
        {
            if (map.TryGetValue(price, out long ex))
                map[price] = ex + value;
            else
                map[price] = value;
        }

        private void ProcessTickIntoPrimaryBar()
        {
            int primaryIndex = BarsArray[0].GetBar(Time[0]);
            if (primaryIndex < 0) return;

            double last = Close[0];
            long vol = (long)Volume[0];
            if (vol <= 0) return;

            double p = Instrument.MasterInstrument.RoundToTickSize(last);

            // ---------- DELTA sign ----------
            // 1) Bid/Ask available (Tick Replay / realtime): trade at/above ask = buy, at/below bid = sell.
            // 2) Otherwise tick rule. A trade at the SAME price as the previous one keeps the previous
            //    direction (it must NOT count as 0, or most of the volume would be dropped from delta).
            bool haveQuote = !double.IsNaN(lastAsk) && !double.IsNaN(lastBid) && lastAsk > 0 && lastBid > 0 && lastAsk >= lastBid;

            if (haveQuote && last >= lastAsk)
            {
                lastDir = +1;
                quoteVol += vol;
            }
            else if (haveQuote && last <= lastBid)
            {
                lastDir = -1;
                quoteVol += vol;
            }
            else
            {
                tickRuleVol += vol;
                if (!double.IsNaN(prevLast))
                {
                    if (last > prevLast) lastDir = +1;
                    else if (last < prevLast) lastDir = -1;
                    // equal price: keep lastDir
                }
            }

            long signed = lastDir * vol;

            prevLast = last;

            lock (sync)
            {
                EnsureBarMaps(primaryIndex);

                AddTo(barVolMaps[primaryIndex], p, vol);
                if (signed != 0)
                    AddTo(barDeltaMaps[primaryIndex], p, signed);

                // Track what trades AFTER the last print of this bar's high / low
                if (primaryIndex != curTickBar)
                {
                    curTickBar = primaryIndex;
                    curBarHigh = double.MinValue;
                    curBarLow = double.MaxValue;
                }

                if (p >= curBarHigh)
                {
                    curBarHigh = p;
                    barAfterHighVol[primaryIndex].Clear();
                    barAfterHighDelta[primaryIndex].Clear();
                }
                else
                {
                    AddTo(barAfterHighVol[primaryIndex], p, vol);
                    if (signed != 0) AddTo(barAfterHighDelta[primaryIndex], p, signed);
                }

                if (p <= curBarLow)
                {
                    curBarLow = p;
                    barAfterLowVol[primaryIndex].Clear();
                    barAfterLowDelta[primaryIndex].Clear();
                }
                else
                {
                    AddTo(barAfterLowVol[primaryIndex], p, vol);
                    if (signed != 0) AddTo(barAfterLowDelta[primaryIndex], p, signed);
                }
            }
        }

        private static void AddMap(Dictionary<double, long> dest, Dictionary<double, long> src, int sign)
        {
            foreach (var kv in src)
                AddTo(dest, kv.Key, sign * kv.Value);
        }

        // Sums the per-bar maps of one leg.
        //  startBar / startType : first bar of the leg and how the leg begins (0 whole bar, +1 after the bar's high, -1 after the bar's low)
        //  endBar / endType     : last bar used and how the leg ends (0 = endBar is a whole bar, +1 = leg ends AT the bar's high, -1 = AT the bar's low)
        // A pivot bar is split by tick: the part up to the extreme goes to the leg that ends there,
        // the part after the extreme goes to the leg that starts there. Nothing is counted twice.
        private AllLegsAgg GetLegAgg(int startBar, int startType, int endBar, int endType, int lastIdx)
        {
            bool cacheable = endBar < lastIdx;

            if (cacheable && aggCache.TryGetValue(startBar, out AllLegsAgg cached)
                && cached.Start == startBar && cached.End == endBar
                && cached.StartType == startType && cached.EndType == endType)
                return cached;

            var a = new AllLegsAgg { Start = startBar, End = endBar, StartType = startType, EndType = endType };

            lock (sync)
            {
                for (int b = startBar; b <= endBar; b++)
                {
                    if (b < 0 || b >= barDeltaMaps.Count || b >= barVolMaps.Count || b >= barAfterHighVol.Count)
                        continue;

                    // Beginning of the leg
                    if (b == startBar && startType != 0)
                    {
                        // leg starts at a pivot: only what traded after the extreme
                        if (startType > 0)
                        {
                            AddMap(a.Delta, barAfterHighDelta[b], +1);
                            AddMap(a.Vol, barAfterHighVol[b], +1);
                        }
                        else
                        {
                            AddMap(a.Delta, barAfterLowDelta[b], +1);
                            AddMap(a.Vol, barAfterLowVol[b], +1);
                        }
                    }
                    else
                    {
                        AddMap(a.Delta, barDeltaMaps[b], +1);
                        AddMap(a.Vol, barVolMaps[b], +1);
                    }

                    // End of the leg at a pivot: remove what traded after the extreme (belongs to the next leg)
                    if (b == endBar && endType != 0)
                    {
                        if (endType > 0)
                        {
                            AddMap(a.Delta, barAfterHighDelta[b], -1);
                            AddMap(a.Vol, barAfterHighVol[b], -1);
                        }
                        else
                        {
                            AddMap(a.Delta, barAfterLowDelta[b], -1);
                            AddMap(a.Vol, barAfterLowVol[b], -1);
                        }
                    }
                }
            }

            // drop empty rows, then compute price range for the spine
            var emptyV = new List<double>();
            foreach (var kv in a.Vol)
                if (kv.Value <= 0) emptyV.Add(kv.Key);
            foreach (var k in emptyV) a.Vol.Remove(k);

            var emptyD = new List<double>();
            foreach (var kv in a.Delta)
                if (kv.Value == 0) emptyD.Add(kv.Key);
            foreach (var k in emptyD) a.Delta.Remove(k);

            foreach (var k in a.Vol.Keys)
            {
                if (k > a.Hi) a.Hi = k;
                if (k < a.Lo) a.Lo = k;
            }

            if (cacheable)
            {
                if (aggCache.Count > 5000) aggCache.Clear();
                aggCache[startBar] = a;
            }

            return a;
        }

        // =====================================================================
        //  RENDERING
        // =====================================================================
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (BarsArray == null || BarsArray.Length == 0 || BarsArray[0] == null
                || ChartBars == null || legStarts == null || RenderTarget == null)
                return;

            int[] starts;
            int[] types;
            lock (sync)
            {
                if (legStarts.Count == 0) return;
                starts = legStarts.ToArray();
                types = legTypes.ToArray();
            }

            bool showDelta = Content != AllLegsContent.Volume;
            bool showVol = Content != AllLegsContent.Delta;

            EnsureDxResources();

            int from = ChartBars.FromIndex;
            int to = ChartBars.ToIndex;
            int lastIdx = BarsArray[0].Count - 1;   // ALWAYS the primary series, never Bars
            if (lastIdx < 0) return;

            // Row height = TicksPerRow ticks (user defined)
            float tickPx = Math.Abs(chartScale.GetYByValue(TickSize) - chartScale.GetYByValue(0));
            int groupTicks = Math.Max(1, TicksPerRow);

            float rowHeightPx = tickPx * groupTicks;
            int font = ShowValues ? GetEffectiveFontSizeFromRowHeight(rowHeightPx) : 0;
            EnsureTextFormat(font);

            float barW = (float)chartControl.BarWidth;
            int oldestToDraw = Math.Max(0, starts.Length - LegsToDisplay);

            // newest (right) -> oldest (left), only the last N legs
            for (int i = starts.Length - 1; i >= oldestToDraw; i--)
            {
                bool split = PivotBarMode == AllLegsPivotMode.SplitByTick;

                int s = starts[i];
                int st = split ? types[i] : 0;
                // pivot bar already belongs to the previous leg -> this leg starts on the next bar
                if (PivotBarMode == AllLegsPivotMode.PivotToEndingLeg && types[i] != 0)
                    s = starts[i] + 1;

                int e, et = 0;
                if (i + 1 < starts.Length)
                {
                    int nt = types[i + 1];
                    // the leg ends ON the pivot bar unless the new leg owns that whole bar
                    e = (PivotBarMode != AllLegsPivotMode.PivotToStartingLeg && nt != 0) ? starts[i + 1] : starts[i + 1] - 1;
                    if (split) et = nt;
                }
                else
                {
                    e = lastIdx;
                }

                if (s > lastIdx) continue;   // current leg has no bar yet (pivot is the last bar)
                if (e < s) e = s;

                if (e < from) break;       // everything older is further left, off screen
                if (s > to) continue;      // leg is to the right of the visible range

                var agg = GetLegAgg(s, st, e, et, lastIdx);
                if (agg.Vol.Count == 0 && agg.Delta.Count == 0)
                    continue;

                RenderLeg(chartControl, chartScale, agg, barW, groupTicks, font, showDelta, showVol);
            }

            if (ShowDeltaSourceInfo && showDelta)
                DrawDeltaSourceInfo();
        }

        // Small diagnostic label: how much of the volume was classified with real bid/ask vs the tick-rule estimate
        private void DrawDeltaSourceInfo()
        {
            if (infoFormat == null)
            {
                infoFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", 11)
                {
                    TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading,
                    ParagraphAlignment = ParagraphAlignment.Near,
                    WordWrapping = WordWrapping.NoWrap
                };
            }

            long q = quoteVol, t = tickRuleVol;
            long tot = q + t;
            string msg = tot <= 0
                ? "Delta source: no ticks yet"
                : string.Format("Delta source: bid/ask {0:0}% | tick-rule estimate {1:0}%{2}",
                    100.0 * q / tot, 100.0 * t / tot,
                    (q == 0 ? "  (no bid/ask: enable Tick Replay for real delta on history)" : ""));

            RenderTarget.DrawText(msg, infoFormat, new RectangleF(ChartPanel.X + 6, ChartPanel.Y + 4, 700, 18), textBrushDx);
        }

        private void RenderLeg(ChartControl chartControl, ChartScale chartScale, AllLegsAgg agg,
            float barW, int groupTicks, int font, bool showDelta, bool showVol)
        {
            float xStart = chartControl.GetXByBarIndex(ChartBars, agg.Start);
            float xEnd = chartControl.GetXByBarIndex(ChartBars, agg.End);

            float legPx = Math.Abs(xEnd - xStart) + barW;
            float width = (WidthMode == AllLegsWidthMode.Fixed)
                ? MaxProfileWidthPx
                : Math.Min(MaxProfileWidthPx, Math.Max(10f, legPx));

            // Spine at the leg END (right edge of the last bar), profile expands LEFT toward leg start
            const int dir = -1;
            float spineX = xEnd + barW * 0.5f;

            // Spine (spans the price range of the leg)
            if (ShowSpine && agg.Hi >= agg.Lo)
            {
                float yTop = chartScale.GetYByValue(agg.Hi + TickSize * 0.5);
                float yBot = chartScale.GetYByValue(agg.Lo - TickSize * 0.5);
                float top = Math.Min(yTop, yBot);
                float h = Math.Max(1f, Math.Abs(yBot - yTop));
                RenderTarget.FillRectangle(new RectangleF(spineX - SpineWidthPx * 0.5f, top, SpineWidthPx, h), spineBrushDx);

                // Horizontal line at the bottom, from the spine (leg end) back to the leg start
                if (ShowBottomLine)
                {
                    float x0 = xStart - barW * 0.5f;
                    float len = (spineX + SpineWidthPx * 0.5f) - x0;
                    if (len > 0f)
                        RenderTarget.FillRectangle(new RectangleF(x0, top + h - SpineWidthPx, len, SpineWidthPx), spineBrushDx);
                }
            }

            // Volume (behind)
            if (showVol && agg.Vol.Count > 0)
            {
                BuildBins(agg.Vol, groupTicks, binsVol);
                DrawBins(chartScale, binsVol, groupTicks, spineX, dir, width, volBrushDx, false,
                    ShowValues && !showDelta, font, 1);
            }

            // Delta (front)
            if (showDelta && agg.Delta.Count > 0)
            {
                BuildBins(agg.Delta, groupTicks, binsDelta);
                DrawBins(chartScale, binsDelta, groupTicks, spineX, dir, width, null, true,
                    ShowValues, font, Math.Max(1, MinAbsDeltaToShow));
            }
        }

        private void BuildBins(Dictionary<double, long> map, int groupTicks, Dictionary<long, long> bins)
        {
            bins.Clear();
            foreach (var kv in map)
            {
                long tickIdx = (long)Math.Round(kv.Key / TickSize);
                long bin = (long)Math.Floor((double)tickIdx / groupTicks);
                if (bins.TryGetValue(bin, out long ex))
                    bins[bin] = ex + kv.Value;
                else
                    bins[bin] = kv.Value;
            }
        }

        private void DrawBins(ChartScale chartScale, Dictionary<long, long> bins, int groupTicks,
            float spineX, int dir, float maxWidth, SolidColorBrush fixedBrush, bool signed,
            bool drawText, int font, long minAbs)
        {
            long maxAbs = 0;
            foreach (var kv in bins)
            {
                long a = Math.Abs(kv.Value);
                if (a > maxAbs) maxAbs = a;
            }

            if (maxAbs < minAbs || maxAbs <= 0)
                return;

            // Uniform text column: right aligned, next to the spine. Shown for all rows or none.
            float textRight = spineX - SpineWidthPx * 0.5f - 3f;
            float textLeft = spineX - maxWidth;
            int maxChars = maxAbs.ToString().Length + (signed ? 1 : 0);
            bool textOk = drawText && font > 0 && textFormat != null
                && (textRight - textLeft) >= (maxChars * font * 0.62f + 6f);

            float panelTop = ChartPanel.Y;
            float panelBottom = ChartPanel.Y + ChartPanel.H;

            foreach (var kv in bins)
            {
                long v = kv.Value;
                long absV = Math.Abs(v);
                if (absV < minAbs || absV == 0)
                    continue;

                double topEdge = (kv.Key * groupTicks + groupTicks - 1 + 0.5) * TickSize;
                double botEdge = (kv.Key * groupTicks - 0.5) * TickSize;

                float yTop = chartScale.GetYByValue(topEdge);
                float yBot = chartScale.GetYByValue(botEdge);
                float top = Math.Min(yTop, yBot);
                float height = Math.Abs(yBot - yTop);
                if (height < 1.5f) height = 1.5f;

                if (top + height < panelTop || top > panelBottom)
                    continue;

                float w = (float)(maxWidth * (absV / (double)maxAbs));
                if (w <= 0.5f)
                    continue;

                var rect = new RectangleF(dir < 0 ? spineX - w : spineX, top, w, height);
                var brush = signed ? (v >= 0 ? posBrushDx : negBrushDx) : fixedBrush;
                RenderTarget.FillRectangle(rect, brush);

                if (textOk && height >= (font + 2))
                {
                    var textRect = new RectangleF(textLeft, top, textRight - textLeft, height);
                    RenderTarget.DrawText(v.ToString(), textFormat, textRect, textBrushDx);
                }
            }
        }

        private int GetEffectiveFontSizeFromRowHeight(float rowHeightPx)
        {
            if (rowHeightPx < 6f)
                return 0;

            int f = (int)Math.Floor(rowHeightPx * 0.70f);
            if (f < AutoFontMin) f = AutoFontMin;
            if (f > AutoFontMax) f = AutoFontMax;

            if (rowHeightPx < (f + 2))
                return 0;

            return f;
        }

        private void EnsureTextFormat(int effectiveFont)
        {
            if (effectiveFont <= 0)
                return;

            if (textFormat == null || lastFontSize != effectiveFont)
            {
                textFormat?.Dispose();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", effectiveFont)
                {
                    TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing,
                    ParagraphAlignment = ParagraphAlignment.Center,
                    WordWrapping = WordWrapping.NoWrap
                };
                lastFontSize = effectiveFont;
            }
        }

        private void EnsureDxResources()
        {
            string posSer = SafeBrushSerialize(PositiveBrush);
            string negSer = SafeBrushSerialize(NegativeBrush);
            string txtSer = SafeBrushSerialize(TextBrush);
            string volSer = SafeBrushSerialize(VolumeBrush);
            string spineSer = SafeBrushSerialize(SpineBrush);

            bool needsRebuild =
                posBrushDx == null || negBrushDx == null || textBrushDx == null || spineBrushDx == null || volBrushDx == null
                || lastPosSer != posSer
                || lastNegSer != negSer
                || lastTextSer != txtSer
                || lastVolSer != volSer
                || lastSpineSer != spineSer
                || Math.Abs(lastDeltaOpacity - DeltaOpacity) > 0.0001f
                || Math.Abs(lastVolOpacity - VolumeOpacity) > 0.0001f;

            if (!needsRebuild)
                return;

            try
            {
                posBrushDx?.Dispose();
                negBrushDx?.Dispose();
                textBrushDx?.Dispose();
                spineBrushDx?.Dispose();
                volBrushDx?.Dispose();
            }
            catch { }

            posBrushDx = new SolidColorBrush(RenderTarget, ToDx(PositiveBrush, DeltaOpacity));
            negBrushDx = new SolidColorBrush(RenderTarget, ToDx(NegativeBrush, DeltaOpacity));
            textBrushDx = new SolidColorBrush(RenderTarget, ToDx(TextBrush, 1f));
            spineBrushDx = new SolidColorBrush(RenderTarget, ToDx(SpineBrush, 0.8f));
            volBrushDx = new SolidColorBrush(RenderTarget, ToDx(VolumeBrush, VolumeOpacity));

            lastPosSer = posSer;
            lastNegSer = negSer;
            lastTextSer = txtSer;
            lastVolSer = volSer;
            lastSpineSer = spineSer;
            lastDeltaOpacity = DeltaOpacity;
            lastVolOpacity = VolumeOpacity;
        }

        private string SafeBrushSerialize(WpfBrush b)
        {
            try { return Serialize.BrushToString(b); }
            catch { return b?.ToString() ?? ""; }
        }

        private static System.Windows.Media.Color BrushToMediaColor(WpfBrush b)
        {
            if (b is WpfSolidColorBrush sb)
                return sb.Color;
            return WpfColors.White;
        }

        private Color4 ToDx(WpfBrush b, float alphaMult)
        {
            var c = BrushToMediaColor(b ?? WpfBrushes.White);
            return new Color4(c.R / 255f, c.G / 255f, c.B / 255f, (c.A / 255f) * alphaMult);
        }

        #region Properties

        // ===== Leg detection =====
        [NinjaScriptProperty]
        [Display(Name = "Rotation Mode", Order = 0, GroupName = "Leg Detection",
            Description = "DistanceFromExtreme = new leg when price is X away from leg high/low. TrueSwingAlternate = only on reversal X, alternates direction (new leg starts at the swing extreme bar).")]
        public AllLegsRotationMode RotationMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 5000.0)]
        [Display(Name = "Rotation (Points)", Order = 1, GroupName = "Leg Detection",
            Description = "Start a new leg when price rotates by this many POINTS. Set to 0 to disable (single leg).")]
        public double RotationPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reset Legs On New Session", Order = 2, GroupName = "Leg Detection")]
        public bool ResetOnNewSession { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Pivot Bar Mode", Order = 3, GroupName = "Leg Detection",
            Description = "Who owns the swing extreme bar (never counted twice). PivotToEndingLeg: the leg that made the extreme keeps the whole bar, the new leg starts on the next bar. PivotToStartingLeg: the new leg gets the whole bar. SplitByTick: the bar is split by tick order around the extreme.")]
        public AllLegsPivotMode PivotBarMode { get; set; }

        // ===== What to show =====
        [NinjaScriptProperty]
        [Display(Name = "Profile Content", Order = 0, GroupName = "Profiles",
            Description = "Delta, Volume, or both (volume drawn behind delta).")]
        public AllLegsContent Content { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Legs To Display", Order = 1, GroupName = "Profiles",
            Description = "Number of most recent legs that get a profile (1 = current leg only).")]
        public int LegsToDisplay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Ticks Per Row", Order = 2, GroupName = "Profiles",
            Description = "Height of each horizontal bar in ticks (NQ: 1 tick = 0.25, 4 = 1 point). Applies to delta and volume.")]
        public int TicksPerRow { get; set; }

        // ===== Layout =====
        [NinjaScriptProperty]
        [Display(Name = "Width Mode", Order = 1, GroupName = "Layout",
            Description = "FitToLeg: profile spans from leg end back to leg start (capped by Max Width). Fixed: always Max Width.")]
        public AllLegsWidthMode WidthMode { get; set; }

        [NinjaScriptProperty]
        [Range(20, 2000)]
        [Display(Name = "Max Width (px)", Order = 2, GroupName = "Layout")]
        public int MaxProfileWidthPx { get; set; }

        // ===== Spine =====
        [NinjaScriptProperty]
        [Display(Name = "Show Spine", Order = 0, GroupName = "Spine")]
        public bool ShowSpine { get; set; }

        [NinjaScriptProperty]
        [Range(1, 6)]
        [Display(Name = "Spine Width (px)", Order = 1, GroupName = "Spine")]
        public int SpineWidthPx { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Bottom Line", Order = 3, GroupName = "Spine",
            Description = "Horizontal line along the bottom of the leg, from the spine back to the leg start (shows the chart portion the leg covers). Requires Show Spine.")]
        public bool ShowBottomLine { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Spine Brush", Order = 2, GroupName = "Spine")]
        public WpfBrush SpineBrush { get; set; }

        [Browsable(false)]
        public string SpineBrushSerialize
        {
            get { return Serialize.BrushToString(SpineBrush); }
            set { SpineBrush = Serialize.StringToBrush(value); }
        }

        // ===== Delta =====
        [NinjaScriptProperty]
        [Range(0, 1000000)]
        [Display(Name = "Min Abs Delta To Show", Order = 0, GroupName = "Delta")]
        public long MinAbsDeltaToShow { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "Delta Opacity", Order = 2, GroupName = "Delta")]
        public float DeltaOpacity { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Positive Brush", Order = 3, GroupName = "Delta")]
        public WpfBrush PositiveBrush { get; set; }

        [Browsable(false)]
        public string PositiveBrushSerialize
        {
            get { return Serialize.BrushToString(PositiveBrush); }
            set { PositiveBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Negative Brush", Order = 4, GroupName = "Delta")]
        public WpfBrush NegativeBrush { get; set; }

        [Browsable(false)]
        public string NegativeBrushSerialize
        {
            get { return Serialize.BrushToString(NegativeBrush); }
            set { NegativeBrush = Serialize.StringToBrush(value); }
        }

        // ===== Volume =====
        [NinjaScriptProperty]
        [Range(0.05, 1.0)]
        [Display(Name = "Volume Opacity", Order = 0, GroupName = "Volume")]
        public float VolumeOpacity { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Volume Brush", Order = 1, GroupName = "Volume")]
        public WpfBrush VolumeBrush { get; set; }

        [Browsable(false)]
        public string VolumeBrushSerialize
        {
            get { return Serialize.BrushToString(VolumeBrush); }
            set { VolumeBrush = Serialize.StringToBrush(value); }
        }

        // ===== Text =====
        [NinjaScriptProperty]
        [Display(Name = "Show Values", Order = 0, GroupName = "Text",
            Description = "Numbers in a uniform right-aligned column next to the spine (auto sized, auto hidden when rows are too small or the leg is too narrow). Volume numbers only when Content = Volume.")]
        public bool ShowValues { get; set; }

        [NinjaScriptProperty]
        [Range(6, 30)]
        [Display(Name = "Auto Font Min", Order = 1, GroupName = "Text")]
        public int AutoFontMin { get; set; }

        [NinjaScriptProperty]
        [Range(6, 40)]
        [Display(Name = "Auto Font Max", Order = 2, GroupName = "Text")]
        public int AutoFontMax { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Delta Source Info", Order = 4, GroupName = "Text",
            Description = "Top-left label showing how much volume was classified with real bid/ask vs the tick-rule estimate.")]
        public bool ShowDeltaSourceInfo { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text Brush", Order = 3, GroupName = "Text")]
        public WpfBrush TextBrush { get; set; }

        [Browsable(false)]
        public string TextBrushSerialize
        {
            get { return Serialize.BrushToString(TextBrush); }
            set { TextBrush = Serialize.StringToBrush(value); }
        }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private AllLegsProfile[] cacheAllLegsProfile;
		public AllLegsProfile AllLegsProfile(AllLegsRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, AllLegsPivotMode pivotBarMode, AllLegsContent content, int legsToDisplay, int ticksPerRow, AllLegsWidthMode widthMode, int maxProfileWidthPx, bool showSpine, int spineWidthPx, bool showBottomLine, WpfBrush spineBrush, long minAbsDeltaToShow, float deltaOpacity, WpfBrush positiveBrush, WpfBrush negativeBrush, float volumeOpacity, WpfBrush volumeBrush, bool showValues, int autoFontMin, int autoFontMax, bool showDeltaSourceInfo, WpfBrush textBrush)
		{
			return AllLegsProfile(Input, rotationMode, rotationPoints, resetOnNewSession, pivotBarMode, content, legsToDisplay, ticksPerRow, widthMode, maxProfileWidthPx, showSpine, spineWidthPx, showBottomLine, spineBrush, minAbsDeltaToShow, deltaOpacity, positiveBrush, negativeBrush, volumeOpacity, volumeBrush, showValues, autoFontMin, autoFontMax, showDeltaSourceInfo, textBrush);
		}

		public AllLegsProfile AllLegsProfile(ISeries<double> input, AllLegsRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, AllLegsPivotMode pivotBarMode, AllLegsContent content, int legsToDisplay, int ticksPerRow, AllLegsWidthMode widthMode, int maxProfileWidthPx, bool showSpine, int spineWidthPx, bool showBottomLine, WpfBrush spineBrush, long minAbsDeltaToShow, float deltaOpacity, WpfBrush positiveBrush, WpfBrush negativeBrush, float volumeOpacity, WpfBrush volumeBrush, bool showValues, int autoFontMin, int autoFontMax, bool showDeltaSourceInfo, WpfBrush textBrush)
		{
			if (cacheAllLegsProfile != null)
				for (int idx = 0; idx < cacheAllLegsProfile.Length; idx++)
					if (cacheAllLegsProfile[idx] != null && cacheAllLegsProfile[idx].RotationMode == rotationMode && cacheAllLegsProfile[idx].RotationPoints == rotationPoints && cacheAllLegsProfile[idx].ResetOnNewSession == resetOnNewSession && cacheAllLegsProfile[idx].PivotBarMode == pivotBarMode && cacheAllLegsProfile[idx].Content == content && cacheAllLegsProfile[idx].LegsToDisplay == legsToDisplay && cacheAllLegsProfile[idx].TicksPerRow == ticksPerRow && cacheAllLegsProfile[idx].WidthMode == widthMode && cacheAllLegsProfile[idx].MaxProfileWidthPx == maxProfileWidthPx && cacheAllLegsProfile[idx].ShowSpine == showSpine && cacheAllLegsProfile[idx].SpineWidthPx == spineWidthPx && cacheAllLegsProfile[idx].ShowBottomLine == showBottomLine && cacheAllLegsProfile[idx].SpineBrush == spineBrush && cacheAllLegsProfile[idx].MinAbsDeltaToShow == minAbsDeltaToShow && cacheAllLegsProfile[idx].DeltaOpacity == deltaOpacity && cacheAllLegsProfile[idx].PositiveBrush == positiveBrush && cacheAllLegsProfile[idx].NegativeBrush == negativeBrush && cacheAllLegsProfile[idx].VolumeOpacity == volumeOpacity && cacheAllLegsProfile[idx].VolumeBrush == volumeBrush && cacheAllLegsProfile[idx].ShowValues == showValues && cacheAllLegsProfile[idx].AutoFontMin == autoFontMin && cacheAllLegsProfile[idx].AutoFontMax == autoFontMax && cacheAllLegsProfile[idx].ShowDeltaSourceInfo == showDeltaSourceInfo && cacheAllLegsProfile[idx].TextBrush == textBrush && cacheAllLegsProfile[idx].EqualsInput(input))
						return cacheAllLegsProfile[idx];
			return CacheIndicator<AllLegsProfile>(new AllLegsProfile(){ RotationMode = rotationMode, RotationPoints = rotationPoints, ResetOnNewSession = resetOnNewSession, PivotBarMode = pivotBarMode, Content = content, LegsToDisplay = legsToDisplay, TicksPerRow = ticksPerRow, WidthMode = widthMode, MaxProfileWidthPx = maxProfileWidthPx, ShowSpine = showSpine, SpineWidthPx = spineWidthPx, ShowBottomLine = showBottomLine, SpineBrush = spineBrush, MinAbsDeltaToShow = minAbsDeltaToShow, DeltaOpacity = deltaOpacity, PositiveBrush = positiveBrush, NegativeBrush = negativeBrush, VolumeOpacity = volumeOpacity, VolumeBrush = volumeBrush, ShowValues = showValues, AutoFontMin = autoFontMin, AutoFontMax = autoFontMax, ShowDeltaSourceInfo = showDeltaSourceInfo, TextBrush = textBrush }, input, ref cacheAllLegsProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.AllLegsProfile AllLegsProfile(AllLegsRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, AllLegsPivotMode pivotBarMode, AllLegsContent content, int legsToDisplay, int ticksPerRow, AllLegsWidthMode widthMode, int maxProfileWidthPx, bool showSpine, int spineWidthPx, bool showBottomLine, WpfBrush spineBrush, long minAbsDeltaToShow, float deltaOpacity, WpfBrush positiveBrush, WpfBrush negativeBrush, float volumeOpacity, WpfBrush volumeBrush, bool showValues, int autoFontMin, int autoFontMax, bool showDeltaSourceInfo, WpfBrush textBrush)
		{
			return indicator.AllLegsProfile(Input, rotationMode, rotationPoints, resetOnNewSession, pivotBarMode, content, legsToDisplay, ticksPerRow, widthMode, maxProfileWidthPx, showSpine, spineWidthPx, showBottomLine, spineBrush, minAbsDeltaToShow, deltaOpacity, positiveBrush, negativeBrush, volumeOpacity, volumeBrush, showValues, autoFontMin, autoFontMax, showDeltaSourceInfo, textBrush);
		}

		public Indicators.AllLegsProfile AllLegsProfile(ISeries<double> input , AllLegsRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, AllLegsPivotMode pivotBarMode, AllLegsContent content, int legsToDisplay, int ticksPerRow, AllLegsWidthMode widthMode, int maxProfileWidthPx, bool showSpine, int spineWidthPx, bool showBottomLine, WpfBrush spineBrush, long minAbsDeltaToShow, float deltaOpacity, WpfBrush positiveBrush, WpfBrush negativeBrush, float volumeOpacity, WpfBrush volumeBrush, bool showValues, int autoFontMin, int autoFontMax, bool showDeltaSourceInfo, WpfBrush textBrush)
		{
			return indicator.AllLegsProfile(input, rotationMode, rotationPoints, resetOnNewSession, pivotBarMode, content, legsToDisplay, ticksPerRow, widthMode, maxProfileWidthPx, showSpine, spineWidthPx, showBottomLine, spineBrush, minAbsDeltaToShow, deltaOpacity, positiveBrush, negativeBrush, volumeOpacity, volumeBrush, showValues, autoFontMin, autoFontMax, showDeltaSourceInfo, textBrush);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.AllLegsProfile AllLegsProfile(AllLegsRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, AllLegsPivotMode pivotBarMode, AllLegsContent content, int legsToDisplay, int ticksPerRow, AllLegsWidthMode widthMode, int maxProfileWidthPx, bool showSpine, int spineWidthPx, bool showBottomLine, WpfBrush spineBrush, long minAbsDeltaToShow, float deltaOpacity, WpfBrush positiveBrush, WpfBrush negativeBrush, float volumeOpacity, WpfBrush volumeBrush, bool showValues, int autoFontMin, int autoFontMax, bool showDeltaSourceInfo, WpfBrush textBrush)
		{
			return indicator.AllLegsProfile(Input, rotationMode, rotationPoints, resetOnNewSession, pivotBarMode, content, legsToDisplay, ticksPerRow, widthMode, maxProfileWidthPx, showSpine, spineWidthPx, showBottomLine, spineBrush, minAbsDeltaToShow, deltaOpacity, positiveBrush, negativeBrush, volumeOpacity, volumeBrush, showValues, autoFontMin, autoFontMax, showDeltaSourceInfo, textBrush);
		}

		public Indicators.AllLegsProfile AllLegsProfile(ISeries<double> input , AllLegsRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, AllLegsPivotMode pivotBarMode, AllLegsContent content, int legsToDisplay, int ticksPerRow, AllLegsWidthMode widthMode, int maxProfileWidthPx, bool showSpine, int spineWidthPx, bool showBottomLine, WpfBrush spineBrush, long minAbsDeltaToShow, float deltaOpacity, WpfBrush positiveBrush, WpfBrush negativeBrush, float volumeOpacity, WpfBrush volumeBrush, bool showValues, int autoFontMin, int autoFontMax, bool showDeltaSourceInfo, WpfBrush textBrush)
		{
			return indicator.AllLegsProfile(input, rotationMode, rotationPoints, resetOnNewSession, pivotBarMode, content, legsToDisplay, ticksPerRow, widthMode, maxProfileWidthPx, showSpine, spineWidthPx, showBottomLine, spineBrush, minAbsDeltaToShow, deltaOpacity, positiveBrush, negativeBrush, volumeOpacity, volumeBrush, showValues, autoFontMin, autoFontMax, showDeltaSourceInfo, textBrush);
		}
	}
}

#endregion
