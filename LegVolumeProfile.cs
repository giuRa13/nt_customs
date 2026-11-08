#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.giuli13;

using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;

// IMPORTANT: alias WPF brushes to avoid conflict with SharpDX.Direct2D1.Brush
using WpfBrush = System.Windows.Media.Brush;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfColors = System.Windows.Media.Colors;
using WpfBrushes = System.Windows.Media.Brushes;
#endregion

// Volume profile for every leg (rotation based). Completed legs are cached, the current leg
// updates in real time (only the forming bar is re-added on each repaint).
// Enum / class names are prefixed with "LegVP" so it can live next to the other leg indicators.
// The "NinjaScript generated code" region is intentionally omitted: NinjaTrader adds it on compile.

namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
    public enum LegVPWidthMode
    {
        FitToLeg = 0,   // width = leg width in pixels (capped by Max Width)
        Fixed = 1       // width = Max Width
    }

    public enum LegVPRowAlign
    {
        FromLegHigh = 0,   // first row starts at the leg's highest traded price, rows go down from there
        GlobalGrid = 1     // rows sit on one fixed price grid shared by all legs
    }

    public enum LegVPLineStart
    {
        CandleStart = 0,    // lines start at the left edge of the leg's first candle
        CandleMiddle = 1,   // ...at the middle of the first candle
        CandleEnd = 2       // ...at the right edge of the first candle
    }

    public enum LegVPRotationMode
    {
        DistanceFromExtreme = 0,
        TrueSwingAlternate = 1
    }

    // Volume of one bar traded AFTER the last print of its high / low (used to split pivot bars)
    internal class LegVPSplit
    {
        public long HiTick = long.MinValue;
        public long LoTick = long.MaxValue;
        public Dictionary<long, long> AfterHigh = new Dictionary<long, long>();
        public Dictionary<long, long> AfterLow = new Dictionary<long, long>();
    }

    internal enum LegVPDir
    {
        Unknown = 0,
        Up = 1,
        Down = -1
    }

    // Aggregated volume of a span of CLOSED bars (key = price in ticks)
    internal class LegVPAgg
    {
        public int Start;
        public int End;
        public int StartPart;   // +1 = first bar counts only after its high, -1 = only after its low, 0 = whole bar
        public int EndPart;     // +1 = last bar counts only up to its high,  -1 = only up to its low,  0 = whole bar
        public Dictionary<long, long> Vol = new Dictionary<long, long>();
    }

    public class LegVolumeProfile : Indicator
    {
        private readonly object sync = new object();

        // ---- Data (written by OnBarUpdate, read by OnRender under lock) ----
        private List<Dictionary<long, long>> barVol;   // per primary bar: price (in ticks) -> volume
        private List<int> legStarts;                   // first bar of every leg (ascending)
        private List<int> legPivot;                    // +1 = leg starts at a swing HIGH bar, -1 = at a swing LOW bar, 0 = not a pivot
        private int lastPrimaryBar = -1;               // last primary bar processed: render never goes past it

        // Split data, kept only for bars that are (or can still become) a pivot: the current leg's
        // high/low bars and the pivots already used. Everything else is dropped as soon as it is closed.
        private Dictionary<int, LegVPSplit> splits;
        private HashSet<int> pinnedPivots;
        private int curTickBar = -1;

        // ---- Leg detection state ----
        private double legHigh = double.NaN;
        private double legLow = double.NaN;
        private int legHighBar = -1;
        private int legLowBar = -1;
        private LegVPDir legDir = LegVPDir.Unknown;
        private int lastSessionBarHandled = -1;
        private int lastDetectedBar = -1;              // last CLOSED bar evaluated by the leg detection

        // ---- Render-thread only ----
        private Dictionary<int, LegVPAgg> aggCache;                                    // closed spans, key = leg start bar
        private readonly Dictionary<long, long> openLegTicks = new Dictionary<long, long>(); // scratch for the live leg
        private long[] rowBuf = new long[256];

        private TextFormat textFormat;
        private int lastFontSize = -1;
        private SolidColorBrush profileBrushDx, vaBrushDx, pocBrushDx, textBrushDx;
        private WpfBrush srcProfile, srcVa, srcPoc, srcText;
        private float srcOpacity = -1f;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "LegVolumeProfile";
                Description = "Volume profile for every leg (rotation based), with value area, POC and leg frame. Newest leg on the right.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;

                // Leg detection
                RotationMode = LegVPRotationMode.TrueSwingAlternate;
                RotationPoints = 65;
                ResetOnNewSession = true;

                // Profiles
                LegsToDisplay = 10;
                TicksPerRow = 1;
                ValueAreaPercent = 70;

                // Layout (profile anchored at leg END, grows LEFT toward leg START)
                WidthMode = LegVPWidthMode.FitToLeg;
                RowAlignment = LegVPRowAlign.FromLegHigh;
                MaxProfileWidthPx = 600;
                CurrentLegOffsetPx = 50;
                LinesStartAt = LegVPLineStart.CandleMiddle;

                // Leg frame: each side can be shown or hidden
                ShowLeftLine = true;
                LeftStroke = new Stroke(WpfBrushes.Black, DashStyleHelper.Dash, 1);
                ShowTopLine = true;
                TopStroke = new Stroke(WpfBrushes.Black, DashStyleHelper.Dash, 1);
                ShowBottomLine = false;
                BottomStroke = new Stroke(WpfBrushes.Black, DashStyleHelper.Dash, 1);
                ShowRightLine = false;
                RightStroke = new Stroke(WpfBrushes.Black, DashStyleHelper.Dash, 1);

                // Colors
                ProfileBrush = MakeBrush("#FF363B47");
                ValueAreaBrush = MakeBrush("#FF5B6473");
                PocBrush = WpfBrushes.White;
                ProfileOpacity = 0.60f;

                // Lines (standard NinjaTrader stroke settings: color, dash style, width, opacity)
                ShowPocLine = true;
                PocStroke = new Stroke(WpfBrushes.White, DashStyleHelper.Solid, 2);
                ShowVahLine = true;
                VahStroke = new Stroke(MakeBrush("#FF5B6473"), DashStyleHelper.Dash, 1);
                ShowValLine = true;
                ValStroke = new Stroke(MakeBrush("#FF5B6473"), DashStyleHelper.Dash, 1);

                // Text
                ShowValues = true;
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
                barVol = new List<Dictionary<long, long>>(4096);
                legStarts = new List<int>(256);
                legPivot = new List<int>(256);
                lastPrimaryBar = -1;
                splits = new Dictionary<int, LegVPSplit>();
                pinnedPivots = new HashSet<int>();
                curTickBar = -1;
                aggCache = new Dictionary<int, LegVPAgg>();

                legHigh = legLow = double.NaN;
                legHighBar = legLowBar = -1;
                legDir = LegVPDir.Unknown;
                lastSessionBarHandled = -1;
                lastDetectedBar = -1;
            }
            else if (State == State.Terminated)
            {
                DisposeDx();
            }
        }

        // =====================================================================
        //  DATA
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                ProcessTick();
                return;
            }

            if (BarsInProgress != 0 || CurrentBar < 1)
                return;

            lock (sync)
            {
                EnsureBarMap(CurrentBar);
                lastPrimaryBar = CurrentBar;

                // Legs are detected on CLOSED bars only: when a new bar opens, the previous bar is evaluated.
                // This is identical live, on reload, with or without Tick Replay and in any Calculate mode,
                // so a wick that touches the rotation distance intrabar and then retraces never creates a
                // leg live that would disappear on reload.
                int closedBar = CurrentBar - 1;
                if (closedBar >= 0 && closedBar > lastDetectedBar)
                {
                    DetectLegs(CurrentBar - closedBar);
                    lastDetectedBar = closedBar;
                }
            }
        }

        private void ProcessTick()
        {
            // Realtime: the tick belongs to the bar forming now (GetBar is ambiguous when several
            // Range/Renko/Tick bars share a timestamp). Historical: map by time.
            int bar = (State == State.Realtime) ? CurrentBars[0] : BarsArray[0].GetBar(Time[0]);
            if (bar < 0) return;

            long vol = (long)Volume[0];
            if (vol <= 0) return;

            long tick = (long)Math.Round(Close[0] / TickSize);

            lock (sync)
            {
                EnsureBarMap(bar);
                AddTo(barVol[bar], tick, vol);

                // Track what trades after the last print of this bar's high / low (for SplitAtExtreme)
                if (!splits.TryGetValue(bar, out LegVPSplit sp))
                {
                    if (bar < curTickBar) return;   // bar already closed and dropped, nothing to track
                    sp = new LegVPSplit();
                    splits[bar] = sp;
                }
                curTickBar = Math.Max(curTickBar, bar);

                if (tick >= sp.HiTick) { sp.HiTick = tick; sp.AfterHigh.Clear(); }
                else AddTo(sp.AfterHigh, tick, vol);

                if (tick <= sp.LoTick) { sp.LoTick = tick; sp.AfterLow.Clear(); }
                else AddTo(sp.AfterLow, tick, vol);
            }
        }

        // Drops the split data of a closed bar that can no longer be a pivot
        private void DropSplitIfUnused(int bar)
        {
            if (bar < 0 || bar == legHighBar || bar == legLowBar || pinnedPivots.Contains(bar))
                return;
            splits.Remove(bar);
        }

        private void EnsureBarMap(int bar)
        {
            while (barVol.Count <= bar)
                barVol.Add(new Dictionary<long, long>());
        }

        private static void AddTo(Dictionary<long, long> map, long key, long value)
        {
            if (map.TryGetValue(key, out long ex))
                map[key] = ex + value;
            else
                map[key] = value;
        }

        private static void AddMap(Dictionary<long, long> dest, Dictionary<long, long> src, int sign = 1)
        {
            foreach (var kv in src)
                AddTo(dest, kv.Key, sign * kv.Value);
        }

        // =====================================================================
        //  LEG DETECTION (called under lock, BarsInProgress 0)
        // =====================================================================
        // Evaluates ONE closed bar ('ago' bars back from the current one)
        private void DetectLegs(int ago)
        {
            int bar = CurrentBar - ago;
            int oldHighBar = legHighBar, oldLowBar = legLowBar;

            DetectLegsCore(ago, bar);

            // free split data of bars that are no longer a possible pivot
            DropSplitIfUnused(bar);
            if (oldHighBar != legHighBar) DropSplitIfUnused(oldHighBar);
            if (oldLowBar != legLowBar) DropSplitIfUnused(oldLowBar);
        }

        private void DetectLegsCore(int ago, int bar)
        {
            if (legStarts.Count == 0)
            {
                AddLegStart(0);
                ResetExtremesTo(ago);
                legDir = LegVPDir.Unknown;
            }

            if (ResetOnNewSession && Bars.IsFirstBarOfSessionByIndex(bar) && lastSessionBarHandled != bar)
            {
                lastSessionBarHandled = bar;
                AddLegStart(bar);
                ResetExtremesTo(ago);
                legDir = LegVPDir.Unknown;
            }

            // Extremes of the current leg + where they occurred
            if (double.IsNaN(legHigh) || double.IsNaN(legLow))
            {
                ResetExtremesTo(ago);
            }
            else
            {
                if (High[ago] >= legHigh) { legHigh = High[ago]; legHighBar = bar; }
                if (Low[ago] <= legLow) { legLow = Low[ago]; legLowBar = bar; }
            }

            if (RotationPoints <= 0)
                return;

            double last = Close[ago];

            if (RotationMode == LegVPRotationMode.DistanceFromExtreme)
            {
                if (last <= legHigh - RotationPoints || last >= legLow + RotationPoints)
                {
                    AddLegStart(bar);
                    ResetExtremesTo(ago);
                    legDir = LegVPDir.Unknown;
                }
                return;
            }

            // TrueSwingAlternate
            if (legDir == LegVPDir.Unknown)
            {
                if (last >= legLow + RotationPoints) legDir = LegVPDir.Up;
                else if (last <= legHigh - RotationPoints) legDir = LegVPDir.Down;
            }

            if (legDir == LegVPDir.Up && last <= legHigh - RotationPoints)
            {
                // new DOWN leg starts at the swing high; the high stays as anchor
                AddLegStart(legHighBar, +1);
                legDir = LegVPDir.Down;
                legLow = last;
                legLowBar = bar;
            }
            else if (legDir == LegVPDir.Down && last >= legLow + RotationPoints)
            {
                // new UP leg starts at the swing low; the low stays as anchor
                AddLegStart(legLowBar, -1);
                legDir = LegVPDir.Up;
                legHigh = last;
                legHighBar = bar;
            }
        }

        private void ResetExtremesTo(int ago)
        {
            legHigh = High[ago];
            legLow = Low[ago];
            legHighBar = CurrentBar - ago;
            legLowBar = CurrentBar - ago;
        }

        private void AddLegStart(int bar, int pivotType = 0)
        {
            bar = Math.Max(0, bar);

            // keep list strictly ascending
            while (legStarts.Count > 0 && legStarts[legStarts.Count - 1] > bar)
            {
                legStarts.RemoveAt(legStarts.Count - 1);
                legPivot.RemoveAt(legPivot.Count - 1);
            }

            if (legStarts.Count > 0 && legStarts[legStarts.Count - 1] == bar)
                return;

            legStarts.Add(bar);
            legPivot.Add(pivotType);

            if (pivotType != 0)
                pinnedPivots.Add(bar);   // keep its split data: two legs share this bar
        }

        // =====================================================================
        //  AGGREGATION (render thread)
        // =====================================================================

        // Sum of CLOSED bars start..end. Closed bars never change, so it is cached.
        // startPart / endPart split a pivot bar at its extreme (see LegVPAgg).
        private LegVPAgg GetClosedAgg(int start, int end, int startPart, int endPart)
        {
            if (aggCache.TryGetValue(start, out LegVPAgg c) && c.End == end
                && c.StartPart == startPart && c.EndPart == endPart)
                return c;

            var a = new LegVPAgg { Start = start, End = end, StartPart = startPart, EndPart = endPart };

            lock (sync)
            {
                int last = Math.Min(end, barVol.Count - 1);
                for (int b = Math.Max(0, start); b <= last; b++)
                {
                    splits.TryGetValue(b, out LegVPSplit sp);

                    if (b == start && startPart != 0 && sp != null)
                    {
                        // leg starts at a pivot: only what traded after the extreme
                        AddMap(a.Vol, startPart > 0 ? sp.AfterHigh : sp.AfterLow);
                        continue;
                    }

                    AddMap(a.Vol, barVol[b]);

                    // leg ends at a pivot: what traded after the extreme belongs to the next leg
                    if (b == end && endPart != 0 && sp != null)
                        AddMap(a.Vol, endPart > 0 ? sp.AfterHigh : sp.AfterLow, -1);
                }
            }

            if (aggCache.Count > 2000) aggCache.Clear();
            aggCache[start] = a;
            return a;
        }

        // Volume by price of one leg. If the leg touches the forming bar, the closed part comes from
        // the cache and only the forming bar is added (that is what makes the live leg cheap).
        private Dictionary<long, long> GetLegTicks(int s, int e, int startPart, int endPart, int lastIdx)
        {
            if (e < lastIdx)
                return GetClosedAgg(s, e, startPart, endPart).Vol;

            // live leg: cached closed part + the forming bar
            int closedEnd = lastIdx - 1;

            openLegTicks.Clear();
            if (closedEnd >= s)
                AddMap(openLegTicks, GetClosedAgg(s, closedEnd, startPart, 0).Vol);

            lock (sync)
            {
                if (lastIdx < barVol.Count)
                    AddMap(openLegTicks, barVol[lastIdx]);
            }

            return openLegTicks;
        }

        // =====================================================================
        //  RENDERING
        // =====================================================================
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || ChartBars.Bars == null || legStarts == null || RenderTarget == null)
                return;

            int[] starts, pivots;
            int lastIdx;
            lock (sync)
            {
                if (legStarts.Count == 0) return;
                starts = legStarts.ToArray();
                pivots = legPivot.ToArray();
                lastIdx = lastPrimaryBar;
            }

            // primary series only (never Bars, which can point to the tick series)
            lastIdx = Math.Min(lastIdx, ChartBars.Bars.Count - 1);
            if (lastIdx < 0) return;

            EnsureDxResources();

            int from = ChartBars.FromIndex;
            int to = ChartBars.ToIndex;

            int groupTicks = Math.Max(1, TicksPerRow);
            float tickPx = Math.Abs(chartScale.GetYByValue(TickSize) - chartScale.GetYByValue(0));
            int font = ShowValues ? GetEffectiveFontSize(tickPx * groupTicks) : 0;
            EnsureTextFormat(font);

            float barW = (float)chartControl.BarWidth;
            int oldestToDraw = Math.Max(0, starts.Length - LegsToDisplay);

            // newest (right) -> oldest (left)
            // A swing bar is always split at its extreme: up to the high/low -> ending leg, after it -> new leg.
            for (int i = starts.Length - 1; i >= oldestToDraw; i--)
            {
                int s = starts[i];
                int startPart = pivots[i];   // 0 = whole first bar, +1/-1 = only what traded after the extreme
                int endPart = 0;

                int e = lastIdx;
                if (i + 1 < starts.Length)
                {
                    if (pivots[i + 1] != 0)
                    {
                        e = starts[i + 1];        // ends ON the next swing bar...
                        endPart = pivots[i + 1];  // ...exactly at its extreme
                    }
                    else
                    {
                        e = starts[i + 1] - 1;    // session / distance rotation: next leg starts on a fresh bar
                    }
                }

                if (s > lastIdx) continue;
                if (e > lastIdx) e = lastIdx;
                if (e < s) e = s;
                if (s == e && startPart != 0) endPart = 0;   // one-bar leg between two pivots: keep the after-extreme part

                if (e < from) break;         // everything older is further left, off screen
                if (s > to) continue;        // leg is right of the visible range

                var ticks = GetLegTicks(s, e, startPart, endPart, lastIdx);
                if (ticks.Count == 0) continue;

                // Only the live leg (the one ending on the last bar) is pushed right, away from the live candles.
                // As soon as a new leg takes over the last bar, this one goes back to its own last bar.
                float offsetPx = (e == lastIdx) ? CurrentLegOffsetPx : 0f;

                RenderLeg(chartControl, chartScale, s, e, ticks, barW, groupTicks, font, offsetPx);
            }
        }

        private void RenderLeg(ChartControl chartControl, ChartScale chartScale, int startBar, int endBar,
            Dictionary<long, long> ticks, float barW, int groupTicks, int font, float offsetPx)
        {
            if (!BuildRows(ticks, groupTicks, out long minKey, out int rowCount, out long hiTick, out long loTick))
                return;

            long[] vols = rowBuf;

            float xStart = chartControl.GetXByBarIndex(ChartBars, startBar);
            float xEnd = chartControl.GetXByBarIndex(ChartBars, endBar);
            float legPx = Math.Abs(xEnd - xStart) + barW;
            float width = (WidthMode == LegVPWidthMode.Fixed)
                ? MaxProfileWidthPx
                : Math.Min(MaxProfileWidthPx, Math.Max(10f, legPx));

            float spineX = xEnd + barW * 0.5f + offsetPx;   // profile anchor: right edge of the leg's last bar (+ live leg offset)
            // where the left line and all horizontal lines start, inside the leg's first candle
            float legLeftX = LinesStartAt == LegVPLineStart.CandleEnd ? xStart + barW * 0.5f
                : LinesStartAt == LegVPLineStart.CandleMiddle ? xStart
                : xStart - barW * 0.5f;
            float panelTop = ChartPanel.Y;
            float panelBottom = ChartPanel.Y + ChartPanel.H;

            // ---- Leg frame: left (leg start), top (leg high), bottom (leg low), right (leg end) ----
            float frameTop = chartScale.GetYByValue((hiTick + 0.5) * TickSize);
            float frameBot = chartScale.GetYByValue((loTick - 0.5) * TickSize);

            if (ShowLeftLine)
                DrawFrameLine(LeftStroke, legLeftX, frameTop, legLeftX, frameBot);

            if (ShowTopLine && spineX > legLeftX)
                DrawFrameLine(TopStroke, legLeftX, frameTop, spineX, frameTop);

            if (ShowBottomLine && spineX > legLeftX)
                DrawFrameLine(BottomStroke, legLeftX, frameBot, spineX, frameBot);

            if (ShowRightLine)
                DrawFrameLine(RightStroke, spineX, frameTop, spineX, frameBot);

            // ---- POC ----
            long total = 0, maxVol = 0;
            int poc = 0;
            for (int i = 0; i < rowCount; i++)
            {
                total += vols[i];
                if (vols[i] > maxVol) { maxVol = vols[i]; poc = i; }
            }
            if (maxVol <= 0) return;

            // ties: take the row closest to the middle of the leg's range
            double mid = (rowCount - 1) / 2.0;
            for (int i = 0; i < rowCount; i++)
                if (vols[i] == maxVol && Math.Abs(i - mid) < Math.Abs(poc - mid))
                    poc = i;

            // ---- Value area (standard two-row expansion from the POC) ----
            int vaLow = poc, vaHigh = poc;
            double target = total * (ValueAreaPercent / 100.0);
            double acc = vols[poc];
            while (acc < target && (vaLow > 0 || vaHigh < rowCount - 1))
            {
                long up = 0, dn = 0;
                if (vaHigh + 1 < rowCount) up += vols[vaHigh + 1];
                if (vaHigh + 2 < rowCount) up += vols[vaHigh + 2];
                if (vaLow - 1 >= 0) dn += vols[vaLow - 1];
                if (vaLow - 2 >= 0) dn += vols[vaLow - 2];

                bool takeUp = vaHigh + 1 >= rowCount ? false : (vaLow - 1 < 0 ? true : up >= dn);

                if (takeUp)
                {
                    acc += vols[++vaHigh];
                    if (vaHigh + 1 < rowCount) acc += vols[++vaHigh];
                }
                else
                {
                    acc += vols[--vaLow];
                    if (vaLow - 1 >= 0) acc += vols[--vaLow];
                }
            }

            // ---- Rows ----
            float textRight = spineX - 3f;
            float textLeft = spineX - width;
            bool textOk = font > 0 && textFormat != null
                && (textRight - textLeft) >= (maxVol.ToString().Length * font * 0.62f + 6f);

            for (int i = 0; i < rowCount; i++)
            {
                long v = vols[i];
                if (v <= 0) continue;

                long lowTick = minKey + i * (long)groupTicks;
                float yTop = chartScale.GetYByValue((lowTick + groupTicks - 0.5) * TickSize);
                float yBot = chartScale.GetYByValue((lowTick - 0.5) * TickSize);
                float top = Math.Min(yTop, yBot);
                float height = Math.Max(1.5f, Math.Abs(yBot - yTop));

                if (top + height < panelTop || top > panelBottom)
                    continue;

                float w = (float)(width * (v / (double)maxVol));
                if (w <= 0.5f) continue;

                var brush = (i == poc) ? pocBrushDx : ((i >= vaLow && i <= vaHigh) ? vaBrushDx : profileBrushDx);
                RenderTarget.FillRectangle(new RectangleF(spineX - w, top, w, height), brush);

                if (textOk && height >= font + 2)
                    RenderTarget.DrawText(v.ToString(), textFormat,
                        new RectangleF(textLeft, top, textRight - textLeft, height), textBrushDx);
            }

            // ---- POC / VAH / VAL lines: from the leg start to the profile anchor, at the center of their row ----
            float lineRight = spineX;
            if (ShowValLine) DrawLevelLine(chartScale, ValStroke, minKey, vaLow, groupTicks, legLeftX, lineRight, panelTop, panelBottom);
            if (ShowVahLine) DrawLevelLine(chartScale, VahStroke, minKey, vaHigh, groupTicks, legLeftX, lineRight, panelTop, panelBottom);
            if (ShowPocLine) DrawLevelLine(chartScale, PocStroke, minKey, poc, groupTicks, legLeftX, lineRight, panelTop, panelBottom);
        }

        private void DrawFrameLine(Stroke stroke, float x0, float y0, float x1, float y1)
        {
            if (stroke == null || stroke.BrushDX == null)
                return;
            RenderTarget.DrawLine(new Vector2(x0, y0), new Vector2(x1, y1), stroke.BrushDX, stroke.Width, stroke.StrokeStyle);
        }

        private void DrawLevelLine(ChartScale chartScale, Stroke stroke, long minKey, int row, int groupTicks,
            float x0, float x1, float panelTop, float panelBottom)
        {
            if (stroke == null || stroke.BrushDX == null || x1 <= x0)
                return;

            double price = (minKey + row * (long)groupTicks + (groupTicks - 1) / 2.0) * TickSize;
            float y = chartScale.GetYByValue(price);
            if (y < panelTop || y > panelBottom)
                return;

            RenderTarget.DrawLine(new Vector2(x0, y), new Vector2(x1, y), stroke.BrushDX, stroke.Width, stroke.StrokeStyle);
        }

        // Fills rowBuf[0..rowCount) with volume per row (index 0 = lowest row).
        // Row i covers ticks (minKey + i*g) .. (minKey + i*g + g - 1).
        private bool BuildRows(Dictionary<long, long> ticks, int g, out long minKey, out int rowCount, out long hiTick, out long loTick)
        {
            minKey = 0;
            rowCount = 0;
            hiTick = long.MinValue;
            loTick = long.MaxValue;

            foreach (var kv in ticks)
            {
                if (kv.Value <= 0) continue;
                if (kv.Key > hiTick) hiTick = kv.Key;
                if (kv.Key < loTick) loTick = kv.Key;
            }
            if (hiTick < loTick) return false;

            bool fromHigh = RowAlignment == LegVPRowAlign.FromLegHigh;
            long loBin = RowStart(loTick, hiTick, g, fromHigh);
            long hiBin = RowStart(hiTick, hiTick, g, fromHigh);
            long count = (hiBin - loBin) / g + 1;
            if (count <= 0 || count > 100000) return false;

            rowCount = (int)count;
            minKey = loBin;

            if (rowBuf.Length < rowCount)
                rowBuf = new long[Math.Max(rowCount, rowBuf.Length * 2)];
            Array.Clear(rowBuf, 0, rowCount);

            foreach (var kv in ticks)
            {
                if (kv.Value <= 0) continue;
                rowBuf[(RowStart(kv.Key, hiTick, g, fromHigh) - loBin) / g] += kv.Value;
            }
            return true;
        }

        // Lowest tick of the row that contains 'tick'
        private static long RowStart(long tick, long hiTick, int g, bool fromHigh)
        {
            if (fromHigh)
            {
                long row = (hiTick - tick) / g;   // hiTick >= tick, so this is a plain floor
                return hiTick - row * g - (g - 1);
            }

            long q = tick / g;
            if (tick % g != 0 && tick < 0) q--;   // floor for negative prices
            return q * g;
        }

        private int GetEffectiveFontSize(float rowHeightPx)
        {
            if (rowHeightPx < 6f)
                return 0;

            int f = (int)Math.Floor(rowHeightPx * 0.70f);
            f = Math.Max(AutoFontMin, Math.Min(AutoFontMax, f));

            return rowHeightPx < f + 2 ? 0 : f;
        }

        private void EnsureTextFormat(int font)
        {
            if (font <= 0 || (textFormat != null && lastFontSize == font))
                return;

            textFormat?.Dispose();
            textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", font)
            {
                TextAlignment = SharpDX.DirectWrite.TextAlignment.Trailing,
                ParagraphAlignment = ParagraphAlignment.Center,
                WordWrapping = WordWrapping.NoWrap
            };
            lastFontSize = font;
        }

        // Rebuilds the DirectX brushes only when a brush or the opacity actually changed
        private void EnsureDxResources()
        {
            bool rebuild = profileBrushDx == null
                || !ReferenceEquals(srcProfile, ProfileBrush)
                || !ReferenceEquals(srcVa, ValueAreaBrush)
                || !ReferenceEquals(srcPoc, PocBrush)
                || !ReferenceEquals(srcText, TextBrush)
                || Math.Abs(srcOpacity - ProfileOpacity) > 0.0001f;

            if (!rebuild)
                return;

            DisposeBrushes();

            profileBrushDx = new SolidColorBrush(RenderTarget, ToDx(ProfileBrush, ProfileOpacity));
            vaBrushDx = new SolidColorBrush(RenderTarget, ToDx(ValueAreaBrush, ProfileOpacity));
            pocBrushDx = new SolidColorBrush(RenderTarget, ToDx(PocBrush, ProfileOpacity));
            textBrushDx = new SolidColorBrush(RenderTarget, ToDx(TextBrush, 1f));

            srcProfile = ProfileBrush;
            srcVa = ValueAreaBrush;
            srcPoc = PocBrush;
            srcText = TextBrush;
            srcOpacity = ProfileOpacity;
        }

        private void DisposeBrushes()
        {
            try
            {
                profileBrushDx?.Dispose();
                vaBrushDx?.Dispose();
                pocBrushDx?.Dispose();
                textBrushDx?.Dispose();
            }
            catch { }

            profileBrushDx = vaBrushDx = pocBrushDx = textBrushDx = null;
        }

        private void DisposeDx()
        {
            DisposeBrushes();
            try { textFormat?.Dispose(); } catch { }
            textFormat = null;
            lastFontSize = -1;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDx();

            // NinjaTrader Strokes build their own DirectX brush / dash style from the render target
            if (RenderTarget != null)
            {
                try
                {
                    if (PocStroke != null) PocStroke.RenderTarget = RenderTarget;
                    if (VahStroke != null) VahStroke.RenderTarget = RenderTarget;
                    if (ValStroke != null) ValStroke.RenderTarget = RenderTarget;
                    if (LeftStroke != null) LeftStroke.RenderTarget = RenderTarget;
                    if (TopStroke != null) TopStroke.RenderTarget = RenderTarget;
                    if (BottomStroke != null) BottomStroke.RenderTarget = RenderTarget;
                    if (RightStroke != null) RightStroke.RenderTarget = RenderTarget;
                }
                catch { }
            }

            base.OnRenderTargetChanged();
        }

        private static WpfBrush MakeBrush(string hex)
        {
            var b = new WpfSolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static Color4 ToDx(WpfBrush b, float alphaMult)
        {
            var c = (b as WpfSolidColorBrush)?.Color ?? WpfColors.White;
            return new Color4(c.R / 255f, c.G / 255f, c.B / 255f, (c.A / 255f) * alphaMult);
        }

        #region Properties

        // ===== Leg detection =====
        [NinjaScriptProperty]
        [Display(Name = "Rotation Mode", Order = 0, GroupName = "Leg Detection",
            Description = "DistanceFromExtreme = new leg when price is X away from leg high/low. TrueSwingAlternate = only on reversal X, alternates direction (new leg starts at the swing extreme bar).")]
        public LegVPRotationMode RotationMode { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 5000.0)]
        [Display(Name = "Rotation (Points)", Order = 1, GroupName = "Leg Detection",
            Description = "Start a new leg when price rotates by this many POINTS. Set to 0 to disable (single leg).")]
        public double RotationPoints { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reset Legs On New Session", Order = 2, GroupName = "Leg Detection")]
        public bool ResetOnNewSession { get; set; }

        // ===== Profiles =====
        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Legs To Display", Order = 0, GroupName = "Profiles",
            Description = "Number of most recent legs that get a profile (1 = current leg only).")]
        public int LegsToDisplay { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Ticks Per Row", Order = 1, GroupName = "Profiles",
            Description = "Height of each horizontal bar in ticks (NQ: 1 tick = 0.25, 4 = 1 point).")]
        public int TicksPerRow { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Value Area %", Order = 2, GroupName = "Profiles",
            Description = "Share of the leg's volume contained in the value area (standard: 70).")]
        public int ValueAreaPercent { get; set; }

        // ===== Layout =====
        [NinjaScriptProperty]
        [Display(Name = "Width Mode", Order = 0, GroupName = "Layout",
            Description = "FitToLeg: profile spans from leg end back to leg start (capped by Max Width). Fixed: always Max Width.")]
        public LegVPWidthMode WidthMode { get; set; }

        [NinjaScriptProperty]
        [Range(20, 2000)]
        [Display(Name = "Max Width (px)", Order = 1, GroupName = "Layout")]
        public int MaxProfileWidthPx { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Row Alignment", Order = 2, GroupName = "Layout",
            Description = "FromLegHigh: rows start at each leg's highest price and go down. GlobalGrid: one fixed price grid for all legs.")]
        public LegVPRowAlign RowAlignment { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2000)]
        [Display(Name = "Current Leg Offset (px)", Order = 3, GroupName = "Layout",
            Description = "Moves ONLY the live leg's profile this many pixels to the right of the last bar, so it does not cover the live candles. When a new leg starts, the old one returns to its normal position. Increase the chart's Right Side Margin to make room.")]
        public int CurrentLegOffsetPx { get; set; }

        // ===== Leg Frame =====
        [NinjaScriptProperty]
        [Display(Name = "Lines Start At", Order = -1, GroupName = "Leg Frame",
            Description = "Where the left line and all horizontal lines (top, bottom, POC, VAH, VAL) start inside the leg's first candle: its left edge, its middle or its right edge.")]
        public LegVPLineStart LinesStartAt { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Left Line", Order = 0, GroupName = "Leg Frame",
            Description = "Vertical line at the leg start, spanning the leg's price range.")]
        public bool ShowLeftLine { get; set; }

        [Display(Name = "Left Line", Order = 1, GroupName = "Leg Frame")]
        public Stroke LeftStroke { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Top Line", Order = 2, GroupName = "Leg Frame",
            Description = "Horizontal line at the leg high, from the leg start to the leg end.")]
        public bool ShowTopLine { get; set; }

        [Display(Name = "Top Line", Order = 3, GroupName = "Leg Frame")]
        public Stroke TopStroke { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Bottom Line", Order = 4, GroupName = "Leg Frame",
            Description = "Horizontal line at the leg low, from the leg start to the leg end.")]
        public bool ShowBottomLine { get; set; }

        [Display(Name = "Bottom Line", Order = 5, GroupName = "Leg Frame")]
        public Stroke BottomStroke { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Right Line", Order = 6, GroupName = "Leg Frame",
            Description = "Vertical line at the leg end (where the profile bars start), spanning the leg's price range.")]
        public bool ShowRightLine { get; set; }

        [Display(Name = "Right Line", Order = 7, GroupName = "Leg Frame")]
        public Stroke RightStroke { get; set; }

        // ===== Colors =====
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Profile Color", Order = 0, GroupName = "Colors")]
        public WpfBrush ProfileBrush { get; set; }

        [Browsable(false)]
        public string ProfileBrushSerialize
        {
            get { return Serialize.BrushToString(ProfileBrush); }
            set { ProfileBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Value Area Color", Order = 1, GroupName = "Colors")]
        public WpfBrush ValueAreaBrush { get; set; }

        [Browsable(false)]
        public string ValueAreaBrushSerialize
        {
            get { return Serialize.BrushToString(ValueAreaBrush); }
            set { ValueAreaBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "POC Color", Order = 2, GroupName = "Colors")]
        public WpfBrush PocBrush { get; set; }

        [Browsable(false)]
        public string PocBrushSerialize
        {
            get { return Serialize.BrushToString(PocBrush); }
            set { PocBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Range(0.05, 1.0)]
        [Display(Name = "Profile Opacity", Order = 3, GroupName = "Colors",
            Description = "Opacity of the profile bars (normal rows, value area and POC row). Lines and text are not affected.")]
        public float ProfileOpacity { get; set; }

        // ===== Lines =====
        [NinjaScriptProperty]
        [Display(Name = "Show POC Line", Order = 0, GroupName = "Lines",
            Description = "Horizontal line at the POC from the leg start to the leg end.")]
        public bool ShowPocLine { get; set; }

        [Display(Name = "POC Line", Order = 1, GroupName = "Lines")]
        public Stroke PocStroke { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VAH Line", Order = 2, GroupName = "Lines",
            Description = "Horizontal line at the value area high from the leg start to the leg end.")]
        public bool ShowVahLine { get; set; }

        [Display(Name = "VAH Line", Order = 3, GroupName = "Lines")]
        public Stroke VahStroke { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show VAL Line", Order = 4, GroupName = "Lines",
            Description = "Horizontal line at the value area low from the leg start to the leg end.")]
        public bool ShowValLine { get; set; }

        [Display(Name = "VAL Line", Order = 5, GroupName = "Lines")]
        public Stroke ValStroke { get; set; }

        // ===== Text =====
        [NinjaScriptProperty]
        [Display(Name = "Show Values", Order = 0, GroupName = "Text",
            Description = "Volume numbers in a uniform right-aligned column at the leg end (auto sized, auto hidden when rows are too small or the leg is too narrow).")]
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
		private giuli13.LegVolumeProfile[] cacheLegVolumeProfile;
		public giuli13.LegVolumeProfile LegVolumeProfile(LegVPRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, int legsToDisplay, int ticksPerRow, int valueAreaPercent, LegVPWidthMode widthMode, int maxProfileWidthPx, LegVPRowAlign rowAlignment, int currentLegOffsetPx, LegVPLineStart linesStartAt, bool showLeftLine, bool showTopLine, bool showBottomLine, bool showRightLine, WpfBrush profileBrush, WpfBrush valueAreaBrush, WpfBrush pocBrush, float profileOpacity, bool showPocLine, bool showVahLine, bool showValLine, bool showValues, int autoFontMin, int autoFontMax, WpfBrush textBrush)
		{
			return LegVolumeProfile(Input, rotationMode, rotationPoints, resetOnNewSession, legsToDisplay, ticksPerRow, valueAreaPercent, widthMode, maxProfileWidthPx, rowAlignment, currentLegOffsetPx, linesStartAt, showLeftLine, showTopLine, showBottomLine, showRightLine, profileBrush, valueAreaBrush, pocBrush, profileOpacity, showPocLine, showVahLine, showValLine, showValues, autoFontMin, autoFontMax, textBrush);
		}

		public giuli13.LegVolumeProfile LegVolumeProfile(ISeries<double> input, LegVPRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, int legsToDisplay, int ticksPerRow, int valueAreaPercent, LegVPWidthMode widthMode, int maxProfileWidthPx, LegVPRowAlign rowAlignment, int currentLegOffsetPx, LegVPLineStart linesStartAt, bool showLeftLine, bool showTopLine, bool showBottomLine, bool showRightLine, WpfBrush profileBrush, WpfBrush valueAreaBrush, WpfBrush pocBrush, float profileOpacity, bool showPocLine, bool showVahLine, bool showValLine, bool showValues, int autoFontMin, int autoFontMax, WpfBrush textBrush)
		{
			if (cacheLegVolumeProfile != null)
				for (int idx = 0; idx < cacheLegVolumeProfile.Length; idx++)
					if (cacheLegVolumeProfile[idx] != null && cacheLegVolumeProfile[idx].RotationMode == rotationMode && cacheLegVolumeProfile[idx].RotationPoints == rotationPoints && cacheLegVolumeProfile[idx].ResetOnNewSession == resetOnNewSession && cacheLegVolumeProfile[idx].LegsToDisplay == legsToDisplay && cacheLegVolumeProfile[idx].TicksPerRow == ticksPerRow && cacheLegVolumeProfile[idx].ValueAreaPercent == valueAreaPercent && cacheLegVolumeProfile[idx].WidthMode == widthMode && cacheLegVolumeProfile[idx].MaxProfileWidthPx == maxProfileWidthPx && cacheLegVolumeProfile[idx].RowAlignment == rowAlignment && cacheLegVolumeProfile[idx].CurrentLegOffsetPx == currentLegOffsetPx && cacheLegVolumeProfile[idx].LinesStartAt == linesStartAt && cacheLegVolumeProfile[idx].ShowLeftLine == showLeftLine && cacheLegVolumeProfile[idx].ShowTopLine == showTopLine && cacheLegVolumeProfile[idx].ShowBottomLine == showBottomLine && cacheLegVolumeProfile[idx].ShowRightLine == showRightLine && cacheLegVolumeProfile[idx].ProfileBrush == profileBrush && cacheLegVolumeProfile[idx].ValueAreaBrush == valueAreaBrush && cacheLegVolumeProfile[idx].PocBrush == pocBrush && cacheLegVolumeProfile[idx].ProfileOpacity == profileOpacity && cacheLegVolumeProfile[idx].ShowPocLine == showPocLine && cacheLegVolumeProfile[idx].ShowVahLine == showVahLine && cacheLegVolumeProfile[idx].ShowValLine == showValLine && cacheLegVolumeProfile[idx].ShowValues == showValues && cacheLegVolumeProfile[idx].AutoFontMin == autoFontMin && cacheLegVolumeProfile[idx].AutoFontMax == autoFontMax && cacheLegVolumeProfile[idx].TextBrush == textBrush && cacheLegVolumeProfile[idx].EqualsInput(input))
						return cacheLegVolumeProfile[idx];
			return CacheIndicator<giuli13.LegVolumeProfile>(new giuli13.LegVolumeProfile(){ RotationMode = rotationMode, RotationPoints = rotationPoints, ResetOnNewSession = resetOnNewSession, LegsToDisplay = legsToDisplay, TicksPerRow = ticksPerRow, ValueAreaPercent = valueAreaPercent, WidthMode = widthMode, MaxProfileWidthPx = maxProfileWidthPx, RowAlignment = rowAlignment, CurrentLegOffsetPx = currentLegOffsetPx, LinesStartAt = linesStartAt, ShowLeftLine = showLeftLine, ShowTopLine = showTopLine, ShowBottomLine = showBottomLine, ShowRightLine = showRightLine, ProfileBrush = profileBrush, ValueAreaBrush = valueAreaBrush, PocBrush = pocBrush, ProfileOpacity = profileOpacity, ShowPocLine = showPocLine, ShowVahLine = showVahLine, ShowValLine = showValLine, ShowValues = showValues, AutoFontMin = autoFontMin, AutoFontMax = autoFontMax, TextBrush = textBrush }, input, ref cacheLegVolumeProfile);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.LegVolumeProfile LegVolumeProfile(LegVPRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, int legsToDisplay, int ticksPerRow, int valueAreaPercent, LegVPWidthMode widthMode, int maxProfileWidthPx, LegVPRowAlign rowAlignment, int currentLegOffsetPx, LegVPLineStart linesStartAt, bool showLeftLine, bool showTopLine, bool showBottomLine, bool showRightLine, WpfBrush profileBrush, WpfBrush valueAreaBrush, WpfBrush pocBrush, float profileOpacity, bool showPocLine, bool showVahLine, bool showValLine, bool showValues, int autoFontMin, int autoFontMax, WpfBrush textBrush)
		{
			return indicator.LegVolumeProfile(Input, rotationMode, rotationPoints, resetOnNewSession, legsToDisplay, ticksPerRow, valueAreaPercent, widthMode, maxProfileWidthPx, rowAlignment, currentLegOffsetPx, linesStartAt, showLeftLine, showTopLine, showBottomLine, showRightLine, profileBrush, valueAreaBrush, pocBrush, profileOpacity, showPocLine, showVahLine, showValLine, showValues, autoFontMin, autoFontMax, textBrush);
		}

		public Indicators.giuli13.LegVolumeProfile LegVolumeProfile(ISeries<double> input , LegVPRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, int legsToDisplay, int ticksPerRow, int valueAreaPercent, LegVPWidthMode widthMode, int maxProfileWidthPx, LegVPRowAlign rowAlignment, int currentLegOffsetPx, LegVPLineStart linesStartAt, bool showLeftLine, bool showTopLine, bool showBottomLine, bool showRightLine, WpfBrush profileBrush, WpfBrush valueAreaBrush, WpfBrush pocBrush, float profileOpacity, bool showPocLine, bool showVahLine, bool showValLine, bool showValues, int autoFontMin, int autoFontMax, WpfBrush textBrush)
		{
			return indicator.LegVolumeProfile(input, rotationMode, rotationPoints, resetOnNewSession, legsToDisplay, ticksPerRow, valueAreaPercent, widthMode, maxProfileWidthPx, rowAlignment, currentLegOffsetPx, linesStartAt, showLeftLine, showTopLine, showBottomLine, showRightLine, profileBrush, valueAreaBrush, pocBrush, profileOpacity, showPocLine, showVahLine, showValLine, showValues, autoFontMin, autoFontMax, textBrush);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.LegVolumeProfile LegVolumeProfile(LegVPRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, int legsToDisplay, int ticksPerRow, int valueAreaPercent, LegVPWidthMode widthMode, int maxProfileWidthPx, LegVPRowAlign rowAlignment, int currentLegOffsetPx, LegVPLineStart linesStartAt, bool showLeftLine, bool showTopLine, bool showBottomLine, bool showRightLine, WpfBrush profileBrush, WpfBrush valueAreaBrush, WpfBrush pocBrush, float profileOpacity, bool showPocLine, bool showVahLine, bool showValLine, bool showValues, int autoFontMin, int autoFontMax, WpfBrush textBrush)
		{
			return indicator.LegVolumeProfile(Input, rotationMode, rotationPoints, resetOnNewSession, legsToDisplay, ticksPerRow, valueAreaPercent, widthMode, maxProfileWidthPx, rowAlignment, currentLegOffsetPx, linesStartAt, showLeftLine, showTopLine, showBottomLine, showRightLine, profileBrush, valueAreaBrush, pocBrush, profileOpacity, showPocLine, showVahLine, showValLine, showValues, autoFontMin, autoFontMax, textBrush);
		}

		public Indicators.giuli13.LegVolumeProfile LegVolumeProfile(ISeries<double> input , LegVPRotationMode rotationMode, double rotationPoints, bool resetOnNewSession, int legsToDisplay, int ticksPerRow, int valueAreaPercent, LegVPWidthMode widthMode, int maxProfileWidthPx, LegVPRowAlign rowAlignment, int currentLegOffsetPx, LegVPLineStart linesStartAt, bool showLeftLine, bool showTopLine, bool showBottomLine, bool showRightLine, WpfBrush profileBrush, WpfBrush valueAreaBrush, WpfBrush pocBrush, float profileOpacity, bool showPocLine, bool showVahLine, bool showValLine, bool showValues, int autoFontMin, int autoFontMax, WpfBrush textBrush)
		{
			return indicator.LegVolumeProfile(input, rotationMode, rotationPoints, resetOnNewSession, legsToDisplay, ticksPerRow, valueAreaPercent, widthMode, maxProfileWidthPx, rowAlignment, currentLegOffsetPx, linesStartAt, showLeftLine, showTopLine, showBottomLine, showRightLine, profileBrush, valueAreaBrush, pocBrush, profileOpacity, showPocLine, showVahLine, showValLine, showValues, autoFontMin, autoFontMax, textBrush);
		}
	}
}

#endregion
