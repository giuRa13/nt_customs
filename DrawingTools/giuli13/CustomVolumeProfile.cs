#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Draw a rectangle with two clicks. The tool loads the 1-tick history of the selected time range
// by itself (BarsRequest), so it does not need Tick Replay and does not depend on the chart bar type.
//
//  Mode VolumeAndDelta : volume profile from the LEFT edge of the rectangle to the right,
//                        delta profile from the LEFT edge to the left (outside the rectangle).
//  Mode DeltaOnlyLeft  : delta profile only, inside the rectangle, from its LEFT edge to the right.
//  Mode DeltaOnlyRight : delta profile only, inside the rectangle, from its RIGHT edge to the left.
//                        With Follow Price the rectangle is made wider (space candles + profile width),
//                        so the profile sits to the right of the live candles instead of over them.
//
// Volume profile = same method as LegVolumeProfile: price in ticks -> volume, rows of N ticks
// (from the range high or on a global grid), POC tie -> row closest to the middle,
// value area = standard two-row expansion from the POC.
//
// Delta = standard bid/ask delta (same rule as NinjaTrader's BuySellVolume / Order Flow "BidAsk"):
//   trade at or above the ask -> buy  (+volume)
//   trade at or below the bid -> sell (-volume)
//   trade inside the spread   -> neutral (not counted)
// The bid/ask used are the ones stored with each historical tick. Only if a tick has no valid
// bid/ask (old data / some feeds) the up/down tick rule is used for that tick.

namespace NinjaTrader.NinjaScript.DrawingTools.giuli13
{
    public enum CVPMode
    {
        VolumeAndDelta = 0, // volume profile inside (left edge -> right) + delta profile outside (left edge -> left)
        DeltaOnlyLeft = 1,  // delta profile only, inside the rectangle, on its left side  (left edge -> right)
        DeltaOnlyRight = 2      // delta profile only, inside the rectangle, on its right side (right edge -> left)
    }

    public enum CVPRowAlign
    {
        FromRangeHigh = 0,  // first row starts at the highest traded price of the range, rows go down from there
        GlobalGrid = 1      // rows sit on one fixed price grid
    }

    public class CustomVolumeProfile : DrawingTool
    {
        #region Variables
        private const double cursorSensitivity = 15;

        // which anchor receives the X (time) / Y (price) of the mouse while a corner is dragged
        private ChartAnchor editXAnchor;
        private ChartAnchor editYAnchor;

        // ---- tick cache (filled by the BarsRequest thread, read by the render thread under lock) ----
        private readonly object sync = new object();
        private List<long> tickTime = new List<long>();     // DateTime.Ticks, ascending
        private List<double> tickPrice = new List<double>();
        private List<int> tickVol = new List<int>();
        private List<sbyte> tickDir = new List<sbyte>();    // +1 buy / -1 sell / 0 neutral
        private BarsRequest barsRequest;
        private DateTime reqFrom = DateTime.MinValue;
        private DateTime reqTo = DateTime.MinValue;
        private bool loading;
        private string loadError;
        private int lastTickIdx = -1;
        private int outOfOrderTicks;            // ticks delivered with an earlier timestamp than the tick before them
        private int replacedTicks;              // stored ticks dropped because the feed delivered the same period again
        private const int redeliveryRun = 20;           // this many out-of-order ticks in a row = the feed is re-sending a period

        // out-of-order ticks waiting for a decision (stray late trades, or the start of a re-delivered period)
        private readonly List<long> pendTime = new List<long>();
        private readonly List<double> pendPrice = new List<double>();
        private readonly List<int> pendVol = new List<int>();
        private readonly List<sbyte> pendDir = new List<sbyte>();
        private long pendLimit;
        private int dataVersion;
        private int ruleDir;                    // tick-rule state (only for ticks without bid/ask)
        private double prevLast = double.NaN;
        private ChartControl lastChartControl;

        // ---- aggregated profile (render thread only) ----
        private readonly Dictionary<long, long> volTicks = new Dictionary<long, long>();    // price in ticks -> volume
        private readonly Dictionary<long, long> deltaTicks = new Dictionary<long, long>();  // price in ticks -> delta
        private readonly Dictionary<long, long> deltaRows = new Dictionary<long, long>();   // lowest tick of the row -> delta
        private long[] volRows = new long[256]; // index 0 = lowest row
        private long volMinKey;                     // lowest tick of row 0
        private int volRowCount;
        private int pocRow, vaLowRow, vaHighRow;
        private string aggKey;
        private long maxVol, maxAbsDelta, totalVol, totalDelta;
        private bool alwaysSendToBack;
        private string debugBars = string.Empty;
        private string debugRange = string.Empty;
        private string debugVolume = string.Empty;
        private long rangeHiTick, rangeLoTick;      // highest / lowest traded price (in ticks) of the range
        #endregion

        #region Icon
        // Same menu icon as NinjaTrader's Order Flow Volume Profile drawing tool.
        // The icon is looked up by name in NinjaTrader.Gui.Tools.Icons at runtime, so the file compiles on every
        // NT8 build; if no volume profile icon is found, the polygon icon is used instead.
        private static object cachedIcon;

        public override object Icon
        {
            get
            {
                if (cachedIcon == null)
                    cachedIcon = FindVolumeProfileIcon() ?? Gui.Tools.Icons.DrawPolygon;
                return cachedIcon;
            }
        }

        private static object FindVolumeProfileIcon()
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                Type icons = typeof(Gui.Tools.Icons);

                // exact names first, then anything that contains "VolumeProfile"
                foreach (string name in new[] { "DrawVolumeProfile", "DrawOrderFlowVolumeProfile", "VolumeProfile" })
                {
                    PropertyInfo pi = icons.GetProperty(name, flags);
                    if (pi != null) return pi.GetValue(null, null);
                    FieldInfo fi = icons.GetField(name, flags);
                    if (fi != null) return fi.GetValue(null);
                }

                foreach (PropertyInfo pi in icons.GetProperties(flags))
                    if (pi.Name.IndexOf("VolumeProfile", StringComparison.OrdinalIgnoreCase) >= 0)
                        return pi.GetValue(null, null);

                foreach (FieldInfo fi in icons.GetFields(flags))
                    if (fi.Name.IndexOf("VolumeProfile", StringComparison.OrdinalIgnoreCase) >= 0)
                        return fi.GetValue(null);
            }
            catch { }
            return null;
        }
        #endregion

        #region Anchors
        [Display(Order = 1)]
        public ChartAnchor StartAnchor { get; set; }

        [Display(Order = 2)]
        public ChartAnchor EndAnchor { get; set; }

        public override IEnumerable<ChartAnchor> Anchors { get { return new[] { StartAnchor, EndAnchor }; } }
        #endregion

        #region State
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Custom Volume Profile";
                Description = "Rectangle range volume profile + delta profile (or delta profile only).";
                DrawingState = DrawingState.Building;

                StartAnchor = new ChartAnchor { IsEditing = true, DrawingTool = this, IsBrowsable = true, DisplayName = "Start" };
                EndAnchor = new ChartAnchor { IsEditing = true, DrawingTool = this, IsBrowsable = true, DisplayName = "End" };

                Mode = CVPMode.VolumeAndDelta;
                VolumeTicksPerRow = 1;
                DeltaTicksPerRow = 4;
                VolumeWidthPercent = 25;
                DeltaWidthPercent = 25;
                RowAlignment = CVPRowAlign.FromRangeHigh;
                MaxDaysToLoad = 10;
                FollowPrice = false;
                FollowOffsetCandles = 2;
                FollowReferenceTime = DateTime.MinValue;

                VolumeBrush = MakeBrush("#FF363B47");
                ValueAreaBrush = Brushes.Chocolate;
                PocBrush = Brushes.Magenta;
                VolumeOpacity = 85;
                ValueAreaPercent = 70;
                ShowPocLine = true;
                PocStroke = new Stroke(Brushes.Magenta, DashStyleHelper.Solid, 1);
                ShowVahLine = true;
                VahStroke = new Stroke(Brushes.Teal, DashStyleHelper.Dash, 1);
                ShowValLine = true;
                ValStroke = new Stroke(Brushes.Teal, DashStyleHelper.Dash, 1);

                DeltaPositiveBrush = MakeBrush("#FF0053A6");
                DeltaNegativeBrush = MakeBrush("#FF75100B");
                DeltaOpacity = 85;
                ShowDeltaBorder = true;
                DeltaBorderBrush = Brushes.Black;
                ShowDeltaValues = true;
                DeltaTextMinSize = 8;
                DeltaTextMaxSize = 18;
                DeltaTextBold = false;
                DeltaPositiveTextBrush = MakeBrush("#FFA8CAEC");
                DeltaNegativeTextBrush = MakeBrush("#FFFF3755");

                OutlineStroke = new Stroke(Brushes.Olive, DashStyleHelper.Solid, 2);
                ShowTotals = true;
                TotalsFontSize = 11;
                AlwaysSendToBack = true;
                ShowDebugInfo = false;
            }
            else if (State == State.Terminated)
            {
                DisposeRequest();
                Dispose();
            }
        }

        private static Brush MakeBrush(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private void DisposeRequest()
        {
            BarsRequest r = barsRequest;
            barsRequest = null;
            if (r != null)
            {
                try { r.Dispose(); } catch { }
            }
        }
        #endregion

        #region Tick data
        // Loads (once per day-range) the 1-tick series that covers the rectangle.
        // Returns false when the range is too large to load.
        private bool EnsureData(DateTime t0, DateTime t1)
        {
            DateTime from = t0.Date;
            DateTime to = t1.Date.AddDays(1);

            if ((to - from).TotalDays > MaxDaysToLoad)
                return false;

            if (from == reqFrom && to == reqTo)
                return true;    // already loaded / loading / failed for this range

            ChartBars chartBars = GetAttachedToChartBars();
            if (chartBars == null || chartBars.Bars == null)
                return true;

            DisposeRequest();

            reqFrom = from;
            reqTo = to;
            loading = true;
            loadError = null;

            try
            {
                BarsRequest req = new BarsRequest(chartBars.Bars.Instrument, from, to)
                {
                    BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 },
                    TradingHours = chartBars.Bars.TradingHours
                };

                req.Update += (s, e) => OnTickUpdate(req, e);
                barsRequest = req;

                req.Request((r, errorCode, errorMessage) =>
                {
                    if (r != barsRequest)
                        return;     // replaced by a newer request

                    if (errorCode != ErrorCode.NoError)
                    {
                        lock (sync)
                        {
                            loading = false;
                            loadError = "Tick data error: " + errorMessage;
                        }
                        RefreshChart();
                        return;
                    }

                    int count = r.Bars.Count;
                    List<long> times = new List<long>(count);
                    List<double> prices = new List<double>(count);
                    List<int> vols = new List<int>(count);
                    List<sbyte> dirs = new List<sbyte>(count);

                    lock (sync)
                    {
                        ruleDir = 0;
                        prevLast = double.NaN;
                        outOfOrderTicks = 0;
                        replacedTicks = 0;
                        ClearPending();

                        for (int i = 0; i < count; i++)
                            AppendTick(r.Bars, i, times, prices, vols, dirs);

                        tickTime = times;
                        tickPrice = prices;
                        tickVol = vols;
                        tickDir = dirs;
                        lastTickIdx = count - 1;
                        loading = false;
                        dataVersion++;
                    }
                    RefreshChart();
                });
            }
            catch (Exception ex)
            {
                loading = false;
                loadError = "Tick data error: " + ex.Message;
            }
            return true;
        }

        // Live ticks: keeps the profile growing when the rectangle reaches the current bar.
        private void OnTickUpdate(BarsRequest req, BarsUpdateEventArgs e)
        {
            if (req != barsRequest)
                return;

            lock (sync)
            {
                if (loading)
                    return;

                Bars bars = req.Bars;
                if (bars == null)
                    return;

                long toTicks = reqTo.Ticks;
                bool added = false;
                for (int i = Math.Max(lastTickIdx + 1, e.MinIndex); i <= e.MaxIndex; i++)
                {
                    lastTickIdx = i;
                    if (bars.GetTime(i).Ticks > toTicks)
                        continue;
                    AppendTick(bars, i, tickTime, tickPrice, tickVol, tickDir);
                    added = true;
                }
                if (added)
                    dataVersion++;
            }
        }

        // Standard bid/ask delta: at/above ask = buy, at/below bid = sell, inside the spread = neutral.
        // Up/down tick rule only for ticks that carry no valid bid/ask.
        private void AppendTick(Bars bars, int i, List<long> times, List<double> prices, List<int> vols, List<sbyte> dirs)
        {
            long vol = bars.GetVolume(i);
            if (vol <= 0)
                return;

            double last = bars.GetClose(i);
            double ask = bars.GetAsk(i);
            double bid = bars.GetBid(i);
            int dir;

            if (ask > 0 && bid > 0 && ask >= bid)
            {
                dir = last >= ask ? +1 : (last <= bid ? -1 : 0);
            }
            else
            {
                if (!double.IsNaN(prevLast))
                {
                    if (last > prevLast) ruleDir = +1;
                    else if (last < prevLast) ruleDir = -1;
                    // same price: keep the previous direction
                }
                dir = ruleDir;
            }
            prevLast = last;

            StoreTick(times, prices, vols, dirs, bars.GetTime(i).Ticks, last, (int)Math.Min(int.MaxValue, vol), (sbyte)dir);
        }

        private void ClearPending()
        {
            pendTime.Clear();
            pendPrice.Clear();
            pendVol.Clear();
            pendDir.Clear();
        }

        // Adds one tick, keeping the stored ticks in ascending time order and free of duplicates.
        //
        // Normally every tick is stamped at or after the previous one and is simply appended.
        // When a tick arrives stamped EARLIER than the last stored one there are two possible reasons:
        //   - the feed is sending again a period it already delivered (seen in Market Replay: the history up to "now"
        //     is loaded and then the same trades arrive a second time). The ticks keep coming in a long ascending run.
        //     -> the stored ticks of that period are dropped and replaced by the new ones, so nothing is counted twice.
        //   - a few late-reported trades. The run is short and the timestamps go back to normal.
        //     -> each one is inserted at the position of its own timestamp.
        // Out-of-order ticks are held in a small pending buffer until the run is long enough to tell which case it is.
        private void StoreTick(List<long> times, List<double> prices, List<int> vols, List<sbyte> dirs, long t, double price, int vol, sbyte dir)
        {
            long lastStored = times.Count > 0 ? times[times.Count - 1] : long.MinValue;

            if (pendTime.Count == 0)
            {
                if (t >= lastStored)
                {
                    times.Add(t); prices.Add(price); vols.Add(vol); dirs.Add(dir);
                    return;
                }
                pendLimit = lastStored;     // an out-of-order run starts here
            }

            if (t < pendLimit)
            {
                outOfOrderTicks++;
                pendTime.Add(t); pendPrice.Add(price); pendVol.Add(vol); pendDir.Add(dir);

                if (pendTime.Count >= redeliveryRun)
                {
                    // re-delivered period: drop what was stored from its start, then continue with the new ticks
                    long start = long.MaxValue;
                    for (int k = 0; k < pendTime.Count; k++)
                        if (pendTime[k] < start) start = pendTime[k];

                    int cut = UpperBound(times, start - 1);     // first stored tick with time >= start
                    replacedTicks += times.Count - cut;
                    times.RemoveRange(cut, times.Count - cut);
                    prices.RemoveRange(cut, prices.Count - cut);
                    vols.RemoveRange(cut, vols.Count - cut);
                    dirs.RemoveRange(cut, dirs.Count - cut);

                    for (int k = 0; k < pendTime.Count; k++)
                        InsertSorted(times, prices, vols, dirs, pendTime[k], pendPrice[k], pendVol[k], pendDir[k]);
                    ClearPending();
                }
                return;
            }

            // timestamps are back to normal after a short run: those were stray late trades
            for (int k = 0; k < pendTime.Count; k++)
                InsertSorted(times, prices, vols, dirs, pendTime[k], pendPrice[k], pendVol[k], pendDir[k]);
            ClearPending();

            times.Add(t); prices.Add(price); vols.Add(vol); dirs.Add(dir);
        }

        private static void InsertSorted(List<long> times, List<double> prices, List<int> vols, List<sbyte> dirs, long t, double price, int vol, sbyte dir)
        {
            int pos = UpperBound(times, t);
            times.Insert(pos, t); prices.Insert(pos, price); vols.Insert(pos, vol); dirs.Insert(pos, dir);
        }

        private void RefreshChart()
        {
            ChartControl cc = lastChartControl;
            if (cc != null)
                cc.Dispatcher.InvokeAsync(() => cc.InvalidateVisual());
        }

        // first index whose value is > v
        private static int UpperBound(List<long> list, long v)
        {
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (list[mid] <= v) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static void AddTo(Dictionary<long, long> map, long key, long value)
        {
            long ex;
            if (map.TryGetValue(key, out ex)) map[key] = ex + value;
            else map[key] = value;
        }

        // Lowest tick of the row that contains 'tick' (same as LegVolumeProfile.RowStart)
        private static long RowStart(long tick, long hiTick, int g, bool fromHigh)
        {
            if (fromHigh)
            {
                long row = (hiTick - tick) / g;     // hiTick >= tick, so this is a plain floor
                return hiTick - row * g - (g - 1);
            }

            long q = tick / g;
            if (tick % g != 0 && tick < 0) q--;     // floor for negative prices
            return q * g;
        }
        #endregion

        #region Aggregation
        // Builds the volume rows (+ POC / value area) and the delta rows for ticks with time in (t0Exclusive, t1].
        private void Aggregate(DateTime t0Exclusive, DateTime t1, double tickSize, long clipLoTick, long clipHiTick)
        {
            int volGroup = Math.Max(1, VolumeTicksPerRow);
            int deltaGroup = Math.Max(1, DeltaTicksPerRow);
            bool fromHigh = RowAlignment == CVPRowAlign.FromRangeHigh;

            string key = string.Concat(t0Exclusive.Ticks, "|", t1.Ticks, "|", dataVersion, "|", volGroup, "|", deltaGroup, "|",
                fromHigh, "|", ValueAreaPercent, "|", clipLoTick, "|", clipHiTick);

            if (key == aggKey)
                return;
            aggKey = key;

            volTicks.Clear();
            deltaTicks.Clear();
            deltaRows.Clear();
            maxVol = maxAbsDelta = totalVol = totalDelta = 0;
            volRowCount = 0;

            long hiTick = long.MinValue, loTick = long.MaxValue;

            // ---- price (in ticks) -> volume / delta ----
            lock (sync)
            {
                // stored ticks are always in ascending time order (see StoreTick)
                int i0 = UpperBound(tickTime, t0Exclusive.Ticks);
                int i1 = UpperBound(tickTime, t1.Ticks);

                for (int i = i0; i < i1; i++)
                {
                    long tick = (long)Math.Round(tickPrice[i] / tickSize);

                    // a trade outside the high / low of the selected candles cannot belong to them (misplaced tick in the data)
                    if (tick < clipLoTick || tick > clipHiTick)
                        continue;

                    long v = tickVol[i];
                    long d = tickDir[i] * v;

                    AddTo(volTicks, tick, v);
                    if (d != 0)
                        AddTo(deltaTicks, tick, d);

                    if (tick > hiTick) hiTick = tick;
                    if (tick < loTick) loTick = tick;

                    totalVol += v;
                    totalDelta += d;
                }
            }

            if (totalVol <= 0 || hiTick < loTick)
                return;

            rangeHiTick = hiTick;
            rangeLoTick = loTick;

            // ---- volume rows (index 0 = lowest row) ----
            long loBin = RowStart(loTick, hiTick, volGroup, fromHigh);
            long hiBin = RowStart(hiTick, hiTick, volGroup, fromHigh);
            long count = (hiBin - loBin) / volGroup + 1;
            if (count <= 0 || count > 200000)
            {
                totalVol = 0;
                return;
            }

            volRowCount = (int)count;
            volMinKey = loBin;
            if (volRows.Length < volRowCount)
                volRows = new long[Math.Max(volRowCount, volRows.Length * 2)];
            Array.Clear(volRows, 0, volRowCount);

            foreach (KeyValuePair<long, long> kv in volTicks)
                volRows[(RowStart(kv.Key, hiTick, volGroup, fromHigh) - loBin) / volGroup] += kv.Value;

            // ---- POC (ties: the row closest to the middle of the range) ----
            pocRow = 0;
            for (int i = 0; i < volRowCount; i++)
                if (volRows[i] > maxVol)
                {
                    maxVol = volRows[i];
                    pocRow = i;
                }

            double mid = (volRowCount - 1) / 2.0;
            for (int i = 0; i < volRowCount; i++)
                if (volRows[i] == maxVol && Math.Abs(i - mid) < Math.Abs(pocRow - mid))
                    pocRow = i;

            // ---- Value area (standard two-row expansion from the POC) ----
            vaLowRow = vaHighRow = pocRow;
            double target = totalVol * (ValueAreaPercent / 100.0);
            double acc = volRows[pocRow];
            while (acc < target && (vaLowRow > 0 || vaHighRow < volRowCount - 1))
            {
                long up = 0, dn = 0;
                if (vaHighRow + 1 < volRowCount) up += volRows[vaHighRow + 1];
                if (vaHighRow + 2 < volRowCount) up += volRows[vaHighRow + 2];
                if (vaLowRow - 1 >= 0) dn += volRows[vaLowRow - 1];
                if (vaLowRow - 2 >= 0) dn += volRows[vaLowRow - 2];

                bool takeUp = vaHighRow + 1 >= volRowCount ? false : (vaLowRow - 1 < 0 ? true : up >= dn);

                if (takeUp)
                {
                    acc += volRows[++vaHighRow];
                    if (vaHighRow + 1 < volRowCount) acc += volRows[++vaHighRow];
                }
                else
                {
                    acc += volRows[--vaLowRow];
                    if (vaLowRow - 1 >= 0) acc += volRows[--vaLowRow];
                }
            }

            // ---- delta rows (own grouping, same alignment / same range high) ----
            foreach (KeyValuePair<long, long> kv in deltaTicks)
                AddTo(deltaRows, RowStart(kv.Key, hiTick, deltaGroup, fromHigh), kv.Value);

            foreach (KeyValuePair<long, long> kv in deltaRows)
                if (Math.Abs(kv.Value) > maxAbsDelta)
                    maxAbsDelta = Math.Abs(kv.Value);
        }
        #endregion

        #region Geometry helpers
        private Point[] GetCorners(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale)
        {
            Point s = StartAnchor.GetPoint(chartControl, chartPanel, chartScale);
            Point e = EndAnchor.GetPoint(chartControl, chartPanel, chartScale);
            double midY = (s.Y + e.Y) * 0.5;
            // corners: 0 = start, 1 = end, 2 = (start X, end Y), 3 = (end X, start Y)
            // side handles: 4 = middle of the start-time side, 5 = middle of the end-time side
            return new[] { s, e, new Point(s.X, e.Y), new Point(e.X, s.Y), new Point(s.X, midY), new Point(e.X, midY) };
        }

        private int GetCornerAt(Point[] corners, Point p)
        {
            for (int i = 0; i < corners.Length; i++)
                if ((corners[i] - p).Length <= cursorSensitivity)
                    return i;
            return -1;
        }

        private static bool IsOnBorder(Point[] c, Point p)
        {
            double l = Math.Min(c[0].X, c[1].X), r = Math.Max(c[0].X, c[1].X);
            double t = Math.Min(c[0].Y, c[1].Y), b = Math.Max(c[0].Y, c[1].Y);
            const double s = cursorSensitivity * 0.5;

            bool inOuter = p.X >= l - s && p.X <= r + s && p.Y >= t - s && p.Y <= b + s;
            bool inInner = p.X > l + s && p.X < r - s && p.Y > t + s && p.Y < b - s;
            return inOuter && !inInner;
        }
        #endregion

        #region Mouse / selection
        public override Cursor GetCursor(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, Point point)
        {
            switch (DrawingState)
            {
                case DrawingState.Building: return Cursors.Pen;
                case DrawingState.Moving: return IsLocked ? Cursors.No : Cursors.SizeAll;
                case DrawingState.Editing: return IsLocked ? Cursors.No : (editYAnchor == null ? Cursors.SizeWE : Cursors.SizeNWSE);
            }

            Point[] corners = GetCorners(chartControl, chartPanel, chartScale);
            int corner = GetCornerAt(corners, point);

            if (corner >= 0)
            {
                if (IsLocked)
                    return Cursors.Arrow;
                if (corner >= 4)
                    return Cursors.SizeWE;      // side handle: moves that side only
                double cx = (corners[0].X + corners[1].X) * 0.5;
                double cy = (corners[0].Y + corners[1].Y) * 0.5;
                return (corners[corner].X - cx) * (corners[corner].Y - cy) >= 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }

            if (IsOnBorder(corners, point))
                return IsLocked ? Cursors.Arrow : Cursors.SizeAll;

            return null;
        }

        public override Point[] GetSelectionPoints(ChartControl chartControl, ChartScale chartScale)
        {
            ChartPanel chartPanel = chartControl.ChartPanels[PanelIndex];
            return GetCorners(chartControl, chartPanel, chartScale);
        }

        public override void OnMouseDown(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
        {
            switch (DrawingState)
            {
                case DrawingState.Building:
                    if (StartAnchor.IsEditing)
                    {
                        dataPoint.CopyDataValues(StartAnchor);
                        dataPoint.CopyDataValues(EndAnchor);
                        StartAnchor.IsEditing = false;
                    }
                    else if (EndAnchor.IsEditing)
                    {
                        dataPoint.CopyDataValues(EndAnchor);
                        EndAnchor.IsEditing = false;
                    }

                    if (!StartAnchor.IsEditing && !EndAnchor.IsEditing)
                    {
                        DrawingState = DrawingState.Normal;
                        IsSelected = false;
                    }
                    break;

                case DrawingState.Normal:
                    Point p = dataPoint.GetPoint(chartControl, chartPanel, chartScale);
                    Point[] corners = GetCorners(chartControl, chartPanel, chartScale);
                    int corner = GetCornerAt(corners, p);

                    if (corner >= 4)
                    {
                        // side handle: only the time of that side changes
                        editXAnchor = corner == 4 ? StartAnchor : EndAnchor;
                        editYAnchor = null;
                        DrawingState = DrawingState.Editing;
                    }
                    else if (corner >= 0)
                    {
                        editXAnchor = corner == 0 || corner == 2 ? StartAnchor : EndAnchor;
                        editYAnchor = corner == 0 || corner == 3 ? StartAnchor : EndAnchor;
                        DrawingState = DrawingState.Editing;
                    }
                    else if (IsOnBorder(corners, p))
                        DrawingState = DrawingState.Moving;
                    else
                        IsSelected = false;
                    break;
            }
        }

        public override void OnMouseMove(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
        {
            if (IsLocked && DrawingState != DrawingState.Building)
                return;

            switch (DrawingState)
            {
                case DrawingState.Building:
                    if (!StartAnchor.IsEditing && EndAnchor.IsEditing)
                        dataPoint.CopyDataValues(EndAnchor);
                    break;

                case DrawingState.Editing:
                    if (editXAnchor != null)
                    {
                        // copy the whole mouse point (as NinjaTrader's own tools do), then put the price back:
                        // only the time side of this anchor changes
                        double keepPrice = editXAnchor.Price;
                        dataPoint.CopyDataValues(editXAnchor);
                        editXAnchor.Price = keepPrice;
                    }
                    if (editYAnchor != null)
                        editYAnchor.Price = dataPoint.Price;
                    break;

                case DrawingState.Moving:
                    foreach (ChartAnchor anchor in Anchors)
                        anchor.MoveAnchor(InitialMouseDownAnchor, dataPoint, chartControl, chartPanel, chartScale, this);
                    break;
            }
        }

        public override void OnMouseUp(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
        {
            if (DrawingState == DrawingState.Building)
                return;

            editXAnchor = null;
            editYAnchor = null;
            DrawingState = DrawingState.Normal;
        }

        public override bool IsVisibleOnChart(ChartControl chartControl, ChartScale chartScale, DateTime firstTimeOnChart, DateTime lastTimeOnChart)
        {
            if (DrawingState == DrawingState.Building)
                return true;

            // the delta profile is drawn to the LEFT of the rectangle, so allow for its width
            ChartPanel chartPanel = chartControl.ChartPanels[PanelIndex];
            Point s = StartAnchor.GetPoint(chartControl, chartPanel, chartScale);
            Point e = EndAnchor.GetPoint(chartControl, chartPanel, chartScale);
            double l = Math.Min(s.X, e.X), r = Math.Max(s.X, e.X);
            double extraLeft = Mode == CVPMode.VolumeAndDelta ? (r - l) * DeltaWidthPercent / 100.0 : 0;

            return l - extraLeft <= chartPanel.X + chartPanel.W && r >= chartPanel.X;
        }

        public override void OnCalculateMinMax()
        {
            MinValue = double.MaxValue;
            MaxValue = double.MinValue;

            if (!IsVisible)
                return;

            foreach (ChartAnchor anchor in Anchors)
            {
                MinValue = Math.Min(MinValue, anchor.Price);
                MaxValue = Math.Max(MaxValue, anchor.Price);
            }
        }
        #endregion

        #region Rendering
        public override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (StartAnchor == null || EndAnchor == null)
                return;

            lastChartControl = chartControl;
            ChartPanel chartPanel = chartControl.ChartPanels[PanelIndex];

            if (!IsInHitTest && DrawingState == DrawingState.Normal)
                FollowLivePrice(chartControl, chartPanel, chartScale);

            Point s = StartAnchor.GetPoint(chartControl, chartPanel, chartScale);
            Point e = EndAnchor.GetPoint(chartControl, chartPanel, chartScale);

            float xL = (float)Math.Min(s.X, e.X);
            float xR = (float)Math.Max(s.X, e.X);
            float yT = (float)Math.Min(s.Y, e.Y);
            float yB = (float)Math.Max(s.Y, e.Y);
            float rectW = xR - xL;

            Stroke outline = OutlineStroke;
            outline.RenderTarget = RenderTarget;

            // hit test pass: only the outline is selectable, so bars behind the profile can still be clicked
            if (IsInHitTest)
            {
                RenderTarget.DrawRectangle(new SharpDX.RectangleF(xL, yT, rectW, yB - yT), chartControl.SelectionBrush, Math.Max(6f, outline.Width));
                return;
            }

            SharpDX.Direct2D1.AntialiasMode oldAa = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

            string status = null;
            bool drawn = false;

            if (DrawingState != DrawingState.Building && rectW >= 2f)
            {
                DateTime t0 = StartAnchor.Time <= EndAnchor.Time ? StartAnchor.Time : EndAnchor.Time;
                DateTime t1 = StartAnchor.Time <= EndAnchor.Time ? EndAnchor.Time : StartAnchor.Time;

                // Bar timestamps are bar CLOSE times: to include the whole left bar, start after the previous bar's timestamp.
                DateTime t0Exclusive = t0.AddTicks(-1);
                DateTime loadTo = t1;
                long clipLoTick = long.MinValue, clipHiTick = long.MaxValue;    // price range of the selected candles, in ticks
                long barsVolume = -1;
                double tickSize = 0;
                ChartBars chartBars = GetAttachedToChartBars();
                if (chartBars != null && chartBars.Bars != null)
                {
                    tickSize = chartBars.Bars.Instrument.MasterInstrument.TickSize;
                    try
                    {
                        // The selected candles are the ones under the rectangle AS DRAWN (left edge .. right edge),
                        // so the profile can never cover candles that are outside the box.
                        int lastIdx = chartBars.Bars.Count - 1;
                        int iL, iR;
                        if (lastIdx >= 0 && TryGetBarIndexByX(chartControl, chartBars, xL, out iL) && TryGetBarIndexByX(chartControl, chartBars, xR, out iR))
                        {
                            iL = Math.Max(0, Math.Min(lastIdx, iL));
                            iR = Math.Max(iL, iR);

                            t0Exclusive = iL > 0 ? chartBars.Bars.GetTime(iL - 1) : chartBars.Bars.GetTime(0).AddDays(-1);
                            t1 = iR >= lastIdx ? DateTime.MaxValue : chartBars.Bars.GetTime(iR);    // MaxValue = up to the live price
                            loadTo = iR >= lastIdx ? chartBars.Bars.GetTime(lastIdx) : t1;
                            debugBars = iL + ".." + (iR >= lastIdx ? lastIdx + " (live)" : iR.ToString());

                            // high / low (and volume) of the selected candles: the profile is limited to this price range,
                            // so the box always ends exactly on the highest / lowest selected candle
                            double tick = chartBars.Bars.Instrument.MasterInstrument.TickSize;
                            int iEnd = Math.Min(iR, lastIdx);
                            if (tick > 0 && iEnd - iL <= 200000)
                            {
                                double hi = double.MinValue, lo = double.MaxValue;
                                long vol = 0;
                                for (int i = iL; i <= iEnd; i++)
                                {
                                    double h = chartBars.Bars.GetHigh(i), l = chartBars.Bars.GetLow(i);
                                    if (h > hi) hi = h;
                                    if (l < lo) lo = l;
                                    vol += chartBars.Bars.GetVolume(i);
                                }
                                if (hi >= lo)
                                {
                                    clipHiTick = (long)Math.Round(hi / tick);
                                    clipLoTick = (long)Math.Round(lo / tick);
                                    barsVolume = vol;
                                }
                            }
                        }
                        else
                        {
                            // fallback (no visible bars to measure with): use the anchor times
                            int idx = chartBars.GetBarIdxByTime(chartControl, t0);
                            if (idx > 0 && idx < chartBars.Bars.Count)
                                t0Exclusive = chartBars.Bars.GetTime(idx - 1);
                            debugBars = "by time";
                        }
                    }
                    catch { }
                }

                if (tickSize > 0)
                {
                    // load only when the user is not dragging; while dragging we re-aggregate what is already cached
                    bool rangeOk = true;
                    if (DrawingState == DrawingState.Normal)
                        rangeOk = EnsureData(t0Exclusive, loadTo);

                    debugVolume = "candles vol " + (barsVolume >= 0 ? barsVolume.ToString("N0") : "?");
                    debugRange = t0Exclusive.ToString("dd/MM HH:mm:ss") + " -> " + (t1 == DateTime.MaxValue ? "live" : t1.ToString("dd/MM HH:mm:ss"));

                    if (!rangeOk)
                        status = "Range larger than Max Days To Load (" + MaxDaysToLoad + ")";
                    else if (loading)
                        status = "Loading ticks...";
                    else if (loadError != null)
                        status = loadError;
                    else
                    {
                        Aggregate(t0Exclusive, t1, tickSize, clipLoTick, clipHiTick);

                        if (totalVol <= 0)
                            status = "No tick data in range";
                        else
                        {
                            // The rectangle always covers the full profile: top = highest traded price, bottom = lowest
                            // traded price of the selected time range. Only the left / right sides are chosen by the user.
                            double topPrice = (rangeHiTick + 0.5) * tickSize;
                            double botPrice = (rangeLoTick - 0.5) * tickSize;
                            yT = Math.Min(chartScale.GetYByValue(topPrice), chartScale.GetYByValue(botPrice));
                            yB = Math.Max(chartScale.GetYByValue(topPrice), chartScale.GetYByValue(botPrice));

                            // snap the anchors too (when not dragging), so the selection handles sit on the fitted rectangle
                            if (DrawingState == DrawingState.Normal)
                            {
                                ChartAnchor upper = StartAnchor.Price >= EndAnchor.Price ? StartAnchor : EndAnchor;
                                ChartAnchor lower = upper == StartAnchor ? EndAnchor : StartAnchor;
                                if (Math.Abs(upper.Price - topPrice) > tickSize * 0.01) upper.Price = topPrice;
                                if (Math.Abs(lower.Price - botPrice) > tickSize * 0.01) lower.Price = botPrice;
                            }

                            RenderProfiles(chartScale, chartPanel, tickSize, xL, xR, rectW);
                            drawn = true;
                        }
                    }
                }
            }

            RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
            RenderTarget.DrawRectangle(new SharpDX.RectangleF(xL, yT, rectW, yB - yT), outline.BrushDX, outline.Width, outline.StrokeStyle);

            if (drawn && ShowTotals)
                status = (Mode == CVPMode.VolumeAndDelta ? "Vol " + totalVol.ToString("N0") + "   " : string.Empty)
                    + "Delta " + (totalDelta > 0 ? "+" : string.Empty) + totalDelta.ToString("N0");

            if (status != null)
            {
                float fontSize = Math.Max(6, TotalsFontSize);
                using (SharpDX.DirectWrite.TextFormat tf = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI", fontSize))
                {
                    tf.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                    RenderTarget.DrawText(status, tf, new SharpDX.RectangleF(xL + 2f, yB + 3f, 2000f, fontSize * 1.6f), outline.BrushDX);
                }
            }

            if (ShowDebugInfo)
            {
                int n;
                string first = "-", last = "-";
                lock (sync)
                {
                    n = tickTime.Count;
                    if (n > 0)
                    {
                        first = new DateTime(tickTime[0]).ToString("dd/MM HH:mm:ss");
                        last = new DateTime(tickTime[n - 1]).ToString("dd/MM HH:mm:ss");
                    }
                }

                string dbg = "state " + DrawingState + " | bars " + debugBars + " | used " + debugRange + " | " + debugVolume
                    + "\nstart " + StartAnchor.Time.ToString("dd/MM HH:mm:ss") + " slot " + StartAnchor.SlotIndex.ToString("0.##")
                    + " | end " + EndAnchor.Time.ToString("dd/MM HH:mm:ss") + " slot " + EndAnchor.SlotIndex.ToString("0.##")
                    + "\nticks " + n + " [" + first + " -> " + last + "] | requested " + reqFrom.ToString("dd/MM") + " -> " + reqTo.ToString("dd/MM")
                    + "\nout-of-order ticks " + outOfOrderTicks + " | replaced (re-delivered) " + replacedTicks;

                using (SharpDX.DirectWrite.TextFormat tf = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Consolas", 11f))
                {
                    tf.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                    RenderTarget.DrawText(dbg, tf, new SharpDX.RectangleF(xL + 2f, yB + 8f + Math.Max(6, TotalsFontSize) * 1.6f, 2000f, 80f), outline.BrushDX);
                }
            }

            RenderTarget.AntialiasMode = oldAa;
        }

        // Follow Price: when the rectangle's right side was on (or beyond) the last candle and a new candle starts,
        // the right side moves to the new candle. A rectangle that ends before the last candle is never touched.
        // FollowReferenceTime = time of the chart's last candle at the previous check; it is saved with the drawing,
        // so a following rectangle also catches up after the chart / workspace is reopened.
        private void FollowLivePrice(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale)
        {
            ChartBars chartBars = GetAttachedToChartBars();
            if (chartBars == null || chartBars.Bars == null || chartBars.Bars.Count == 0)
                return;

            int lastIdx = chartBars.Bars.Count - 1;
            DateTime lastBarTime = chartBars.Bars.GetTime(lastIdx);
            DateTime reference = FollowReferenceTime == DateTime.MinValue ? lastBarTime : FollowReferenceTime;

            if (FollowPrice)
            {
                ChartAnchor right = StartAnchor.Time > EndAnchor.Time ? StartAnchor : EndAnchor;

                // was on (or beyond) the live candle at the previous check, and a newer candle exists now:
                // put the right side N candles to the right of the new last candle.
                // Everything is computed in chart slots (1 slot = 1 candle), never in pixels: the target can be
                // outside the visible part of the chart, where a pixel -> time conversion is not reliable.
                if (right.Time >= reference && lastBarTime > reference)
                {
                    try
                    {
                        double lastSlot = chartControl.GetSlotIndexByTime(lastBarTime);
                        double targetSlot = lastSlot + Math.Max(0, FollowOffsetCandles);

                        // DeltaOnlyRight: the profile lives on the right side of the rectangle, so add its width after the
                        // space candles. The profile width is a % of the final rectangle width W:
                        //   W = (target - left) + W * pct   ->   W = (target - left) / (1 - pct)
                        if (Mode == CVPMode.DeltaOnlyRight)
                        {
                            ChartAnchor left = right == StartAnchor ? EndAnchor : StartAnchor;
                            double leftSlot = chartControl.GetSlotIndexByTime(left.Time);
                            double pct = Math.Min(0.9, DeltaWidthPercent / 100.0);
                            if (targetSlot > leftSlot)
                                targetSlot = Math.Ceiling(leftSlot + (targetSlot - leftSlot) / (1.0 - pct));
                        }

                        DateTime targetTime = chartControl.GetTimeBySlotIndex(targetSlot);

                        // future slots: if the chart cannot give a time for them, estimate it from the last candle's duration
                        if (targetTime <= lastBarTime && targetSlot > lastSlot && lastIdx > 0)
                        {
                            TimeSpan candle = lastBarTime - chartBars.Bars.GetTime(lastIdx - 1);
                            if (candle.Ticks <= 0)
                                candle = TimeSpan.FromSeconds(1);
                            targetTime = lastBarTime.AddTicks((long)(candle.Ticks * (targetSlot - lastSlot)));
                        }

                        if (targetTime >= lastBarTime)
                        {
                            right.SlotIndex = targetSlot;
                            right.Time = targetTime;
                        }
                    }
                    catch { }

                    if (right.Time < lastBarTime)
                        right.Time = lastBarTime;
                }
            }

            FollowReferenceTime = lastBarTime;
        }

        // Bar index of the candle at pixel x, measured from the visible candles (equidistant bar spacing).
        // Works for positions outside the visible area too (plain linear extrapolation).
        private static bool TryGetBarIndexByX(ChartControl chartControl, ChartBars chartBars, double x, out int idx)
        {
            idx = -1;
            int from = chartBars.FromIndex, to = chartBars.ToIndex;
            if (from < 0 || to <= from)
                return false;

            double xFrom = chartControl.GetXByBarIndex(chartBars, from);
            double step = (chartControl.GetXByBarIndex(chartBars, to) - xFrom) / (to - from);
            if (step <= 0)
                return false;

            idx = from + (int)Math.Round((x - xFrom) / step);
            return true;
        }

        private void RenderProfiles(ChartScale chartScale, ChartPanel chartPanel, double tickSize, float xL, float xR, float rectW)
        {
            bool showVolume = Mode == CVPMode.VolumeAndDelta;
            float volW = rectW * VolumeWidthPercent / 100f;
            float deltaW = rectW * DeltaWidthPercent / 100f;
            int volGroup = Math.Max(1, VolumeTicksPerRow);
            int deltaGroup = Math.Max(1, DeltaTicksPerRow);
            float panelTop = chartPanel.Y;
            float panelBottom = chartPanel.Y + chartPanel.H;

            // ---------- volume profile: left edge -> right ----------
            if (showVolume && maxVol > 0)
            {
                using (SharpDX.Direct2D1.Brush volDx = (VolumeBrush ?? Brushes.Gray).ToDxBrush(RenderTarget))
                using (SharpDX.Direct2D1.Brush vaDx = (ValueAreaBrush ?? Brushes.Chocolate).ToDxBrush(RenderTarget))
                using (SharpDX.Direct2D1.Brush pocDx = (PocBrush ?? Brushes.Magenta).ToDxBrush(RenderTarget))
                {
                    volDx.Opacity = VolumeOpacity / 100f;
                    vaDx.Opacity = VolumeOpacity / 100f;
                    pocDx.Opacity = VolumeOpacity / 100f;

                    for (int i = 0; i < volRowCount; i++)
                    {
                        long v = volRows[i];
                        if (v <= 0)
                            continue;

                        float top, height;
                        if (!GetRow(chartScale, volMinKey + i * (long)volGroup, volGroup, tickSize, panelTop, panelBottom, out top, out height))
                            continue;

                        float w = (float)(volW * (v / (double)maxVol));
                        if (w < 0.5f)
                            continue;

                        SharpDX.Direct2D1.Brush brush = i == pocRow ? pocDx : (i >= vaLowRow && i <= vaHighRow ? vaDx : volDx);
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(xL, top, w, height), brush);
                    }
                }

                // POC / VAH / VAL lines across the rectangle, at the center of their row
                if (ShowValLine) DrawLevelLine(chartScale, ValStroke, vaLowRow, volGroup, tickSize, xL, xR, panelTop, panelBottom);
                if (ShowVahLine) DrawLevelLine(chartScale, VahStroke, vaHighRow, volGroup, tickSize, xL, xR, panelTop, panelBottom);
                if (ShowPocLine) DrawLevelLine(chartScale, PocStroke, pocRow, volGroup, tickSize, xL, xR, panelTop, panelBottom);
            }

            // ---------- delta profile ----------
            //  VolumeAndDelta : left edge  -> LEFT  (outside the rectangle)
            //  DeltaOnlyLeft  : left edge  -> right (inside the rectangle)
            //  DeltaOnlyRight : right edge -> left  (inside the rectangle)
            if (maxAbsDelta > 0)
            {
                bool outsideLeft = Mode == CVPMode.VolumeAndDelta;
                bool insideRight = Mode == CVPMode.DeltaOnlyRight;
                bool growLeft = outsideLeft || insideRight;     // bars grow toward the left
                float baseX = insideRight ? xR : xL;            // edge the bars start from

                // Text size follows the row height (zoom in / more ticks per row -> bigger text), between min and max.
                // When a row is too small even for the minimum size, the values are hidden.
                float rowPx = Math.Abs(chartScale.GetYByValue(rangeHiTick * tickSize) - chartScale.GetYByValue((rangeHiTick - deltaGroup) * tickSize));
                int minSize = Math.Max(6, DeltaTextMinSize);
                int maxSize = Math.Max(minSize, DeltaTextMaxSize);
                float textSize = Math.Max(minSize, Math.Min(maxSize, (float)Math.Floor(rowPx * 0.70f)));
                bool textFits = rowPx >= textSize * 1.2f;

                using (SharpDX.Direct2D1.Brush posDx = (DeltaPositiveBrush ?? Brushes.Blue).ToDxBrush(RenderTarget))
                using (SharpDX.Direct2D1.Brush negDx = (DeltaNegativeBrush ?? Brushes.Red).ToDxBrush(RenderTarget))
                using (SharpDX.Direct2D1.Brush borderDx = (DeltaBorderBrush ?? Brushes.Black).ToDxBrush(RenderTarget))
                using (SharpDX.Direct2D1.Brush posTextDx = (DeltaPositiveTextBrush ?? Brushes.White).ToDxBrush(RenderTarget))
                using (SharpDX.Direct2D1.Brush negTextDx = (DeltaNegativeTextBrush ?? Brushes.White).ToDxBrush(RenderTarget))
                using (SharpDX.DirectWrite.TextFormat deltaText = new SharpDX.DirectWrite.TextFormat(Core.Globals.DirectWriteFactory, "Segoe UI",
                    DeltaTextBold ? SharpDX.DirectWrite.FontWeight.Bold : SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, textSize))
                {
                    // values sit in one column next to the edge the bars start from, on the side the bars grow to
                    deltaText.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;
                    deltaText.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                    deltaText.TextAlignment = growLeft ? SharpDX.DirectWrite.TextAlignment.Trailing : SharpDX.DirectWrite.TextAlignment.Leading;
                    float textW = Math.Max(deltaW, textSize * 12f);

                    posDx.Opacity = DeltaOpacity / 100f;
                    negDx.Opacity = DeltaOpacity / 100f;

                    foreach (KeyValuePair<long, long> kv in deltaRows)
                    {
                        if (kv.Value == 0)
                            continue;

                        float top, height;
                        if (!GetRow(chartScale, kv.Key, deltaGroup, tickSize, panelTop, panelBottom, out top, out height))
                            continue;

                        float w = (float)(deltaW * (Math.Abs(kv.Value) / (double)maxAbsDelta));
                        if (w < 0.5f)
                            continue;

                        SharpDX.RectangleF bar = new SharpDX.RectangleF(growLeft ? baseX - w : baseX, top, w, height);
                        RenderTarget.FillRectangle(bar, kv.Value > 0 ? posDx : negDx);

                        if (ShowDeltaBorder && height >= 3f && w >= 3f)
                            RenderTarget.DrawRectangle(new SharpDX.RectangleF(bar.X + 0.5f, bar.Y + 0.5f, bar.Width - 1f, bar.Height - 1f), borderDx, 1f);

                        // value: only when the rows are taller than the text
                        if (ShowDeltaValues && textFits)
                            RenderTarget.DrawText(kv.Value.ToString(), deltaText,
                                new SharpDX.RectangleF(growLeft ? baseX - 3f - textW : baseX + 3f, top, textW, height),
                                kv.Value > 0 ? posTextDx : negTextDx);
                    }
                }
            }
        }

        private void DrawLevelLine(ChartScale chartScale, Stroke stroke, int row, int groupTicks, double tickSize,
            float x0, float x1, float panelTop, float panelBottom)
        {
            if (stroke == null || x1 <= x0)
                return;

            stroke.RenderTarget = RenderTarget;
            if (stroke.BrushDX == null)
                return;

            double price = (volMinKey + row * (long)groupTicks + (groupTicks - 1) / 2.0) * tickSize;
            float y = chartScale.GetYByValue(price);
            if (y < panelTop || y > panelBottom)
                return;

            RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), stroke.BrushDX, stroke.Width, stroke.StrokeStyle);
        }

        // Pixel rectangle of one row. lowTick = lowest tick of the row. Returns false when the row is off screen.
        private static bool GetRow(ChartScale chartScale, long lowTick, int groupTicks, double tickSize, float panelTop, float panelBottom, out float top, out float height)
        {
            float yTop = chartScale.GetYByValue((lowTick + groupTicks - 0.5) * tickSize);
            float yBot = chartScale.GetYByValue((lowTick - 0.5) * tickSize);
            top = Math.Min(yTop, yBot);
            height = Math.Abs(yBot - yTop);

            if (height >= 3f)
                height -= 1f;       // 1 px gap between rows
            else if (height < 1f)
                height = 1f;

            return top + height >= panelTop && top <= panelBottom;
        }
        #endregion

        #region Properties
        // ===== Profile =====
        [Display(Name = "Mode", GroupName = "Profile", Order = 0,
            Description = "VolumeAndDelta: volume profile from the left edge to the right + delta profile from the left edge to the left. DeltaOnlyLeft: delta profile only, inside the rectangle on its left side. DeltaOnlyRight: delta profile only, inside the rectangle on its right side (with Follow Price the rectangle is widened so the profile stays to the right of the live candles).")]
        public CVPMode Mode { get; set; }

        [Range(1, 1000)]
        [Display(Name = "Volume: Ticks Per Row", GroupName = "Profile", Order = 1)]
        public int VolumeTicksPerRow { get; set; }

        [Range(1, 1000)]
        [Display(Name = "Delta: Ticks Per Row", GroupName = "Profile", Order = 2)]
        public int DeltaTicksPerRow { get; set; }

        [Range(1, 100)]
        [Display(Name = "Volume: Width (% of rectangle)", GroupName = "Profile", Order = 3)]
        public int VolumeWidthPercent { get; set; }

        [Range(1, 100)]
        [Display(Name = "Delta: Width (% of rectangle)", GroupName = "Profile", Order = 4)]
        public int DeltaWidthPercent { get; set; }

        [Display(Name = "Row Alignment", GroupName = "Profile", Order = 5,
            Description = "FromRangeHigh: rows start at the highest traded price of the range and go down. GlobalGrid: one fixed price grid.")]
        public CVPRowAlign RowAlignment { get; set; }

        [Range(1, 365)]
        [Display(Name = "Max Days To Load", GroupName = "Profile", Order = 7,
            Description = "Safety limit on how many days of tick data the tool is allowed to load.")]
        public int MaxDaysToLoad { get; set; }

        [Display(Name = "Follow Price", GroupName = "Profile", Order = 8,
            Description = "When the right side of the rectangle is on the last (live) candle, it moves forward by one candle every time a new candle starts. Rectangles that end before the last candle are not affected.")]
        public bool FollowPrice { get; set; }

        [Range(0, 100)]
        [Display(Name = "Follow Price: Space (candles)", GroupName = "Profile", Order = 9,
            Description = "While following, the right side stays this many candles to the right of the last candle (0 = exactly on it). In DeltaOnlyRight mode the delta profile width is added after this space.")]
        public int FollowOffsetCandles { get; set; }

        // time of the chart's last candle at the previous follow check (internal state, saved with the drawing)
        [Browsable(false)]
        [ExcludeFromTemplate]
        public DateTime FollowReferenceTime { get; set; }

        // ===== Volume =====
        [XmlIgnore]
        [Display(Name = "Profile Color (outside value area)", GroupName = "Volume", Order = 0)]
        public Brush VolumeBrush { get; set; }

        [Browsable(false)]
        public string VolumeBrushSerialize
        {
            get { return Serialize.BrushToString(VolumeBrush); }
            set { VolumeBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Value Area Color", GroupName = "Volume", Order = 1)]
        public Brush ValueAreaBrush { get; set; }

        [Browsable(false)]
        public string ValueAreaBrushSerialize
        {
            get { return Serialize.BrushToString(ValueAreaBrush); }
            set { ValueAreaBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "POC Color", GroupName = "Volume", Order = 2)]
        public Brush PocBrush { get; set; }

        [Browsable(false)]
        public string PocBrushSerialize
        {
            get { return Serialize.BrushToString(PocBrush); }
            set { PocBrush = Serialize.StringToBrush(value); }
        }

        [Range(1, 100)]
        [Display(Name = "Volume Opacity (%)", GroupName = "Volume", Order = 3)]
        public int VolumeOpacity { get; set; }

        [Range(10, 100)]
        [Display(Name = "Value Area %", GroupName = "Volume", Order = 4,
            Description = "Share of the range's volume contained in the value area (standard: 70).")]
        public int ValueAreaPercent { get; set; }

        [Display(Name = "Show POC Line", GroupName = "Volume", Order = 5)]
        public bool ShowPocLine { get; set; }

        [Display(Name = "POC Line", GroupName = "Volume", Order = 6)]
        public Stroke PocStroke { get; set; }

        [Display(Name = "Show VAH Line", GroupName = "Volume", Order = 7)]
        public bool ShowVahLine { get; set; }

        [Display(Name = "VAH Line", GroupName = "Volume", Order = 8)]
        public Stroke VahStroke { get; set; }

        [Display(Name = "Show VAL Line", GroupName = "Volume", Order = 9)]
        public bool ShowValLine { get; set; }

        [Display(Name = "VAL Line", GroupName = "Volume", Order = 10)]
        public Stroke ValStroke { get; set; }

        // ===== Delta =====
        [XmlIgnore]
        [Display(Name = "Positive Delta Color", GroupName = "Delta", Order = 0)]
        public Brush DeltaPositiveBrush { get; set; }

        [Browsable(false)]
        public string DeltaPositiveBrushSerialize
        {
            get { return Serialize.BrushToString(DeltaPositiveBrush); }
            set { DeltaPositiveBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Negative Delta Color", GroupName = "Delta", Order = 1)]
        public Brush DeltaNegativeBrush { get; set; }

        [Browsable(false)]
        public string DeltaNegativeBrushSerialize
        {
            get { return Serialize.BrushToString(DeltaNegativeBrush); }
            set { DeltaNegativeBrush = Serialize.StringToBrush(value); }
        }

        [Range(1, 100)]
        [Display(Name = "Delta Opacity (%)", GroupName = "Delta", Order = 2)]
        public int DeltaOpacity { get; set; }

        [Display(Name = "Show Bar Border", GroupName = "Delta", Order = 3,
            Description = "Draws a 1 px border around every delta bar.")]
        public bool ShowDeltaBorder { get; set; }

        [XmlIgnore]
        [Display(Name = "Bar Border Color", GroupName = "Delta", Order = 4)]
        public Brush DeltaBorderBrush { get; set; }

        [Browsable(false)]
        public string DeltaBorderBrushSerialize
        {
            get { return Serialize.BrushToString(DeltaBorderBrush); }
            set { DeltaBorderBrush = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Show Values", GroupName = "Delta", Order = 5,
            Description = "Delta value on each bar. Shown only on bars taller than the text.")]
        public bool ShowDeltaValues { get; set; }

        [Range(6, 60)]
        [Display(Name = "Values Text Min Size", GroupName = "Delta", Order = 6,
            Description = "The text grows with the row height. Below this size the values are hidden.")]
        public int DeltaTextMinSize { get; set; }

        [Range(6, 100)]
        [Display(Name = "Values Text Max Size", GroupName = "Delta", Order = 7,
            Description = "The text never gets bigger than this, however far you zoom in.")]
        public int DeltaTextMaxSize { get; set; }

        [Display(Name = "Values Bold", GroupName = "Delta", Order = 8)]
        public bool DeltaTextBold { get; set; }

        [XmlIgnore]
        [Display(Name = "Positive Value Text Color", GroupName = "Delta", Order = 9)]
        public Brush DeltaPositiveTextBrush { get; set; }

        [Browsable(false)]
        public string DeltaPositiveTextBrushSerialize
        {
            get { return Serialize.BrushToString(DeltaPositiveTextBrush); }
            set { DeltaPositiveTextBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Negative Value Text Color", GroupName = "Delta", Order = 10)]
        public Brush DeltaNegativeTextBrush { get; set; }

        [Browsable(false)]
        public string DeltaNegativeTextBrushSerialize
        {
            get { return Serialize.BrushToString(DeltaNegativeTextBrush); }
            set { DeltaNegativeTextBrush = Serialize.StringToBrush(value); }
        }

        // ===== General =====
        [Display(Name = "Rectangle Outline", GroupName = "General", Order = 0)]
        public Stroke OutlineStroke { get; set; }

        [Display(Name = "Show Totals", GroupName = "General", Order = 1,
            Description = "Total volume and total delta of the range, under the rectangle.")]
        public bool ShowTotals { get; set; }

        [Range(6, 60)]
        [Display(Name = "Totals Text Size", GroupName = "General", Order = 2)]
        public int TotalsFontSize { get; set; }

        [Display(Name = "Show Debug Info", GroupName = "General", Order = 4,
            Description = "Three lines under the rectangle: drawing state, candles and time range used, anchor times, loaded tick range. For troubleshooting.")]
        public bool ShowDebugInfo { get; set; }

        [Display(Name = "Always Send To Back", GroupName = "General", Order = 3,
            Description = "The profile is always drawn first, so candles and everything else on the chart are drawn on top of it.")]
        public bool AlwaysSendToBack
        {
            get { return alwaysSendToBack; }
            set
            {
                alwaysSendToBack = value;
                ZOrderType = value ? DrawingToolZOrder.AlwaysDrawnFirst : DrawingToolZOrder.Normal;
            }
        }
        #endregion
    }
}