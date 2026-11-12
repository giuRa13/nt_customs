#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Xml.Serialization;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

// Speed of Tape (Instant) as an AddOn window.
// Open it from Control Center > New > Speed of Tape.
// Each bar covers a fixed number of seconds ("Bar Seconds"):
//   Body = speed at the end of the bar (activity in the last "Number Seconds"); the newest bar decays live
//   Wick = peak speed reached during the bar
//   SD+1 / SD+2 lines = mean + 1 / 2 std dev of the previous "Std Dev Period" bars
// Mouse: wheel = more / fewer bars, Shift + wheel = scroll back in time, hover = bar details,
//        double-click (or the gear button) = settings.
// Templates: Settings > Template (Save as / Save as default / Load / Delete),
//            stored in Documents\NinjaTrader 8\templates\SpeedOfTape.

namespace NinjaTrader.NinjaScript.AddOns.giuli13
{
    // =========================================================================
    //  ENUMS / SETTINGS
    // =========================================================================
    public enum TapeInput
    {
        Volume = 0,     // contracts traded
        Orders = 1,     // aggressive orders: consecutive prints with the same timestamp and side count as one
        Trades = 2      // number of prints
    }

    public enum TapeColorBy
    {
        Delta = 0,      // buy vs sell aggression inside the bar
        PriceBar = 1    // price direction inside the bar
    }

    // marks a string property that holds a color as #AARRGGBB
    [AttributeUsage(AttributeTargets.Property)]
    public class ColorHexAttribute : Attribute { }

    public class TapeSettings
    {
        public TapeSettings()
        {
            InputData = TapeInput.Volume;
            NumberSeconds = 10;
            BarSeconds = 10;
            DaysToLoad = 1;

            FilterMin = 0;
            FilterMax = 0;
            StdDevFilter = 1.0;
            StdDevPeriod = 50;
            ColorBy = TapeColorBy.Delta;

            BullBorder = "#FF1B88F4";
            BullFill = "#FF1B88F4";
            BearBorder = "#FFFF3755";
            BearFill = "#FFFF3755";
            UnmarkedOpacity = 100;
            UseGradient = true;
            GradientNeutral = "#FFFFFFFF";
            GradientFullAt = 20;
            LineWidth = 1;

            BarsToShow = 10;
            ShowSdLines = true;
            SdLineColor = "#FF2EA043";
            BackgroundColor = "#FF191B22";
            TextColor = "#FFB0B0B0";
            FontFamilyName = "Arial";
            FontSize = 11;
            ShortName = "S-T";
            ShowValueAxis = true;
            AlwaysOnTop = false;

            ShowSeparators = false;
            SeparatorSeconds = 60;
            SeparatorColor = "#FF808080";
            SeparatorDashStyle = DashStyleHelper.Dash;
            SeparatorWidth = 1;
            SeparatorOpacity = 50;

            TipTime = true;
            TipEnd = true;
            TipPeak = true;
            TipDelta = true;
            TipBuySell = true;
        }

        // ===== Data (changing these reloads the data) =====
        [Display(Name = "Input Data", Order = 0, GroupName = "Data",
            Description = "Volume = contracts traded. Orders = aggressive orders (prints with same timestamp and side merged). Trades = number of prints.")]
        public TapeInput InputData { get; set; }

        [Range(1, 3600)]
        [Display(Name = "Number Seconds", Order = 1, GroupName = "Data",
            Description = "Time span the speed is measured over (rolling window, seconds).")]
        public int NumberSeconds { get; set; }

        [Range(1, 3600)]
        [Display(Name = "Bar Seconds", Order = 2, GroupName = "Data",
            Description = "How many seconds each bar covers. Bars are aligned to the clock.")]
        public int BarSeconds { get; set; }

        [Range(1, 30)]
        [Display(Name = "Days To Load", Order = 3, GroupName = "Data",
            Description = "Trading days of tick history to load (weekends skipped). 1 = today's session.")]
        public int DaysToLoad { get; set; }

        // ===== Highlight =====
        [Range(0, double.MaxValue)]
        [Display(Name = "Filter Min", Order = 0, GroupName = "Highlight",
            Description = "Only bars with speed at or above this value are highlighted.")]
        public double FilterMin { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Filter Max", Order = 1, GroupName = "Highlight",
            Description = "Only bars with speed at or below this value are highlighted. 0 = no maximum.")]
        public double FilterMax { get; set; }

        [Range(0, 10)]
        [Display(Name = "Std Dev Per Filter", Order = 2, GroupName = "Highlight",
            Description = "A bar is highlighted only if its speed >= mean + N x std dev of the previous bars. 1 = above the SD+1 line. 0 = off.")]
        public double StdDevFilter { get; set; }

        [Range(2, 1000)]
        [Display(Name = "Std Dev Period", Order = 3, GroupName = "Highlight",
            Description = "Number of previous bars used for the mean / std dev (and the SD lines).")]
        public int StdDevPeriod { get; set; }

        [Display(Name = "Color By", Order = 4, GroupName = "Highlight",
            Description = "Delta = aggressive buys vs sells in the bar. PriceBar = price direction inside the bar.")]
        public TapeColorBy ColorBy { get; set; }

        // ===== Bars =====
        [ColorHex]
        [Display(Name = "Bull Border", Order = 0, GroupName = "Bars")]
        public string BullBorder { get; set; }

        [ColorHex]
        [Display(Name = "Bull Fill", Order = 1, GroupName = "Bars")]
        public string BullFill { get; set; }

        [ColorHex]
        [Display(Name = "Bear Border", Order = 2, GroupName = "Bars")]
        public string BearBorder { get; set; }

        [ColorHex]
        [Display(Name = "Bear Fill", Order = 3, GroupName = "Bars")]
        public string BearFill { get; set; }

        [Range(0, 100)]
        [Display(Name = "Unmarked Opacity (%)", Order = 4, GroupName = "Bars",
            Description = "Opacity of the bars that are not highlighted. 0 (with Std Dev Per Filter = 0) shows only the bars between Filter Min and Filter Max.")]
        public int UnmarkedOpacity { get; set; }

        [Display(Name = "Gradient On Faded Bars", Order = 5, GroupName = "Bars",
            Description = "On: not-highlighted bars blend from the neutral color (buyers = sellers) to the bull / bear color (strong imbalance).")]
        public bool UseGradient { get; set; }

        [ColorHex]
        [Display(Name = "Gradient Neutral Color", Order = 6, GroupName = "Bars")]
        public string GradientNeutral { get; set; }

        [Range(1, 100)]
        [Display(Name = "Gradient Full Color At (%)", Order = 7, GroupName = "Bars",
            Description = "Imbalance at which a faded bar reaches the full color. Imbalance = |buy - sell| / (buy + sell). Lower = more colorful.")]
        public int GradientFullAt { get; set; }

        [Range(1, 10)]
        [Display(Name = "Line Width", Order = 8, GroupName = "Bars")]
        public int LineWidth { get; set; }

        // ===== Panel =====
        [Range(3, 500)]
        [Display(Name = "Bars To Show", Order = 0, GroupName = "Panel",
            Description = "Number of bars shown. Also changed with the mouse wheel.")]
        public int BarsToShow { get; set; }

        [Display(Name = "Show SD Lines", Order = 1, GroupName = "Panel")]
        public bool ShowSdLines { get; set; }

        [ColorHex]
        [Display(Name = "SD Line Color", Order = 2, GroupName = "Panel")]
        public string SdLineColor { get; set; }

        [ColorHex]
        [Display(Name = "Background Color", Order = 3, GroupName = "Panel")]
        public string BackgroundColor { get; set; }

        [ColorHex]
        [Display(Name = "Text Color", Order = 4, GroupName = "Panel")]
        public string TextColor { get; set; }

        [Display(Name = "Font", Order = 5, GroupName = "Panel")]
        public string FontFamilyName { get; set; }

        [Range(6, 40)]
        [Display(Name = "Font Size", Order = 6, GroupName = "Panel")]
        public double FontSize { get; set; }

        [Display(Name = "Short Name", Order = 7, GroupName = "Panel")]
        public string ShortName { get; set; }

        [Display(Name = "Show Value Axis", Order = 8, GroupName = "Panel",
            Description = "Show the value labels on the right side and the horizontal grid lines.")]
        public bool ShowValueAxis { get; set; }

        [Display(Name = "Always On Top", Order = 9, GroupName = "Panel",
            Description = "Keep the Speed of Tape window above all other windows.")]
        public bool AlwaysOnTop { get; set; }

        // ===== Time separators =====
        [Display(Name = "Show Separators", Order = 0, GroupName = "Time Separators",
            Description = "Vertical lines every N seconds, e.g. to see where each candle of your main chart starts.")]
        public bool ShowSeparators { get; set; }

        [Range(1, 86400)]
        [Display(Name = "Interval (seconds)", Order = 1, GroupName = "Time Separators",
            Description = "Distance between the lines: 30 = 30 sec, 60 = 1 min, 300 = 5 min, 900 = 15 min. Lines sit on round clock times.")]
        public int SeparatorSeconds { get; set; }

        [ColorHex]
        [Display(Name = "Color", Order = 2, GroupName = "Time Separators")]
        public string SeparatorColor { get; set; }

        [Display(Name = "Dash Style", Order = 3, GroupName = "Time Separators")]
        public DashStyleHelper SeparatorDashStyle { get; set; }

        [Range(1, 10)]
        [Display(Name = "Width", Order = 4, GroupName = "Time Separators")]
        public int SeparatorWidth { get; set; }

        [Range(0, 100)]
        [Display(Name = "Opacity (%)", Order = 5, GroupName = "Time Separators")]
        public int SeparatorOpacity { get; set; }

        // ===== Tooltip =====
        [Display(Name = "Show Time", Order = 0, GroupName = "Tooltip", Description = "Start - end time of the bar.")]
        public bool TipTime { get; set; }

        [Display(Name = "Show End", Order = 1, GroupName = "Tooltip", Description = "Speed at the end of the bar (top of the body).")]
        public bool TipEnd { get; set; }

        [Display(Name = "Show Peak", Order = 2, GroupName = "Tooltip", Description = "Highest speed reached inside the bar (top of the wick).")]
        public bool TipPeak { get; set; }

        [Display(Name = "Show Delta", Order = 3, GroupName = "Tooltip", Description = "Aggressive buys minus aggressive sells.")]
        public bool TipDelta { get; set; }

        [Display(Name = "Show Buy / Sell", Order = 4, GroupName = "Tooltip", Description = "Aggressive buys and aggressive sells.")]
        public bool TipBuySell { get; set; }

        // ---- helpers ----
        public string ToXml()
        {
            var ser = new XmlSerializer(typeof(TapeSettings));
            using (var sw = new StringWriter(CultureInfo.InvariantCulture))
            {
                ser.Serialize(sw, this);
                return sw.ToString();
            }
        }

        public static TapeSettings FromXml(string xml)
        {
            try
            {
                var ser = new XmlSerializer(typeof(TapeSettings));
                using (var sr = new StringReader(xml))
                    return (TapeSettings)ser.Deserialize(sr);
            }
            catch
            {
                return new TapeSettings();
            }
        }

        public TapeSettings Clone()
        {
            return FromXml(ToXml());
        }

        // true when the change needs the tick data to be reloaded / recalculated
        public bool NeedsReload(TapeSettings other)
        {
            return other == null
                || InputData != other.InputData
                || NumberSeconds != other.NumberSeconds
                || BarSeconds != other.BarSeconds
                || DaysToLoad != other.DaysToLoad;
        }

        // ---- templates: Documents\NinjaTrader 8\templates\SpeedOfTape\<name>.xml ----
        public const string DefaultTemplate = "Default";

        public static string TemplateDir
        {
            get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "SpeedOfTape"); }
        }

        private static string TemplatePath(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return Path.Combine(TemplateDir, name.Trim() + ".xml");
        }

        public static List<string> ListTemplates()
        {
            try
            {
                if (!Directory.Exists(TemplateDir))
                    return new List<string>();
                return Directory.GetFiles(TemplateDir, "*.xml")
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(n => n == DefaultTemplate ? "" : n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        public static bool TemplateExists(string name)
        {
            return File.Exists(TemplatePath(name));
        }

        public void SaveTemplate(string name)
        {
            Directory.CreateDirectory(TemplateDir);
            File.WriteAllText(TemplatePath(name), ToXml());
        }

        // null if the template does not exist or cannot be read
        public static TapeSettings LoadTemplate(string name)
        {
            try
            {
                string path = TemplatePath(name);
                if (!File.Exists(path))
                    return null;
                var ser = new XmlSerializer(typeof(TapeSettings));
                using (var sr = new StringReader(File.ReadAllText(path)))
                    return (TapeSettings)ser.Deserialize(sr);
            }
            catch
            {
                return null;
            }
        }

        public static void DeleteTemplate(string name)
        {
            try { File.Delete(TemplatePath(name)); }
            catch { }
        }
    }

    // =========================================================================
    //  ENGINE: rolling-window speed per time bucket (thread safe through Sync)
    // =========================================================================
    public class TapeEngine
    {
        public class Bucket
        {
            public long Key;
            public double Speed, Peak, Buy, Sell, Open, Close;
        }

        private struct TapeEvent
        {
            public readonly DateTime Time;
            public readonly double Amount;
            public TapeEvent(DateTime time, double amount) { Time = time; Amount = amount; }
        }

        public readonly object Sync = new object();

        public List<Bucket> Buckets = new List<Bucket>();
        public Dictionary<long, int> Index = new Dictionary<long, int>();
        public DateTime LastTickTime = DateTime.MinValue;
        public int LastBarIndex = -1;          // last tick (bar index of the request) already processed
        public bool HistoryLoaded;

        public TapeInput Input;
        public int NumberSeconds = 10;
        public int BarSeconds = 30;

        private Queue<TapeEvent> window = new Queue<TapeEvent>();
        private double windowSum;
        private double lastPrice;
        private int lastSide = 1;
        private DateTime lastTradeTime = DateTime.MinValue;

        // call inside lock
        public void Reset(TapeSettings s)
        {
            Input = s.InputData;
            NumberSeconds = Math.Max(1, s.NumberSeconds);
            BarSeconds = Math.Max(1, s.BarSeconds);
            Buckets = new List<Bucket>();
            Index = new Dictionary<long, int>();
            window = new Queue<TapeEvent>();
            windowSum = 0;
            lastPrice = 0;
            lastSide = 1;
            lastTradeTime = DateTime.MinValue;
            LastTickTime = DateTime.MinValue;
            LastBarIndex = -1;
            HistoryLoaded = false;
        }

        // call inside lock
        public void Process(DateTime t, double price, double volume, double ask, double bid)
        {
            int side = ClassifySide(price, ask, bid);

            double amount;
            switch (Input)
            {
                case TapeInput.Trades: amount = 1; break;
                case TapeInput.Orders: amount = (t != lastTradeTime || side != lastSide) ? 1 : 0; break;
                default: amount = volume; break;
            }

            lastPrice = price;
            lastSide = side;
            lastTradeTime = t;

            if (amount > 0)
            {
                window.Enqueue(new TapeEvent(t, amount));
                windowSum += amount;
            }
            DateTime cutoff = t.AddSeconds(-NumberSeconds);
            while (window.Count > 0 && window.Peek().Time <= cutoff)
                windowSum -= window.Dequeue().Amount;
            if (window.Count == 0)
                windowSum = 0;

            Bucket b = GetBucket(KeyOf(t));
            b.Speed = windowSum;
            if (windowSum > b.Peak) b.Peak = windowSum;
            if (side > 0) b.Buy += amount; else b.Sell += amount;
            if (b.Open == 0) b.Open = price;
            b.Close = price;

            LastTickTime = t;
        }

        // Aggressor side: at/above ask = buy, at/below bid = sell, otherwise tick rule
        private int ClassifySide(double price, double ask, double bid)
        {
            int tickRule = lastPrice == 0 ? lastSide
                : price > lastPrice ? 1
                : price < lastPrice ? -1
                : lastSide;

            if (ask > 0 && bid > 0 && ask > bid)
            {
                if (price >= ask) return 1;
                if (price <= bid) return -1;
            }
            return tickRule;
        }

        // call inside lock: speed of the rolling window at 'now' (live decay)
        public double WindowSumAt(DateTime now)
        {
            DateTime cutoff = now.AddSeconds(-NumberSeconds);
            double sum = 0;
            foreach (var ev in window)
                if (ev.Time > cutoff) sum += ev.Amount;
            return sum;
        }

        public long KeyOf(DateTime t)
        {
            return t.Ticks / (BarSeconds * TimeSpan.TicksPerSecond);
        }

        public DateTime KeyStart(long key)
        {
            return new DateTime(key * BarSeconds * TimeSpan.TicksPerSecond);
        }

        private Bucket GetBucket(long key)
        {
            int idx;
            if (Index.TryGetValue(key, out idx))
                return Buckets[idx];
            var b = new Bucket { Key = key };
            Index[key] = Buckets.Count;
            Buckets.Add(b);
            return b;
        }

        // first index with Key >= key
        private int LowerBound(long key)
        {
            int lo = 0, hi = Buckets.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Buckets[mid].Key < key) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // call inside lock: mean / std dev of the speed of the 'period' bars before 'key'
        public bool Stats(long key, int period, out double mean, out double std)
        {
            mean = std = 0;
            int end = LowerBound(key);
            int start = Math.Max(0, end - period);
            int n = end - start;
            if (n < 2) return false;

            double sum = 0, sq = 0;
            for (int i = start; i < end; i++)
            {
                double x = Buckets[i].Speed;
                sum += x;
                sq += x * x;
            }
            mean = sum / n;
            std = Math.Sqrt(Math.Max(0, sq / n - mean * mean));
            return true;
        }

        // call inside lock
        public bool IsMarked(long key, double v, TapeSettings s)
        {
            if (v <= 0 || v < s.FilterMin) return false;
            if (s.FilterMax > 0 && v > s.FilterMax) return false;
            if (s.StdDevFilter > 0)
            {
                double mean, std;
                if (Stats(key, s.StdDevPeriod, out mean, out std) && v < mean + s.StdDevFilter * std)
                    return false;
            }
            return true;
        }
    }

    // =========================================================================
    //  GAUGE: draws the bars (UI thread)
    // =========================================================================
    public class TapeGauge : FrameworkElement
    {
        private struct View
        {
            public long Key;
            public bool Exists, Marked;
            public double Speed, Peak, Buy, Sell, Open, Close;
        }

        public TapeSettings Settings;
        public TapeEngine Engine;
        public string Status;                       // shown in the middle when not null
        public event EventHandler SettingsRequested;

        private double hoverX = -1, hoverY = -1;
        private int scrollOffset;

        private Color cBullFill, cBullBorder, cBearFill, cBearBorder, cNeutral, cText, cSd;
        private Brush bgBrush, textBrush, dimTextBrush, sdTextBrush, boxBrush;
        private Pen framePen, sdPen, gridPen, hoverPen, tickPen, separatorPen;
        private Typeface typeface;
        private double fontSize;

        public TapeGauge(TapeSettings settings, TapeEngine engine)
        {
            Settings = settings;
            Engine = engine;
            ClipToBounds = true;
            Focusable = true;
            BuildResources();

            MouseMove += (s, e) =>
            {
                Point p = e.GetPosition(this);
                hoverX = p.X;
                hoverY = p.Y;
                InvalidateVisual();
            };
            MouseLeave += (s, e) => { hoverX = hoverY = -1; InvalidateVisual(); };
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount > 1 && SettingsRequested != null)
                {
                    e.Handled = true;
                    SettingsRequested(this, EventArgs.Empty);
                }
            };
            MouseWheel += OnWheel;
        }

        public void ResetScroll()
        {
            scrollOffset = 0;
        }

        public void BuildResources()
        {
            var s = Settings;
            FontFamily family;
            try { family = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamilyName) ? "Arial" : s.FontFamilyName); }
            catch { family = new FontFamily("Arial"); }
            typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            fontSize = Math.Max(6, s.FontSize);

            cBullFill = Hex(s.BullFill, Colors.DodgerBlue);
            cBullBorder = Hex(s.BullBorder, Colors.DodgerBlue);
            cBearFill = Hex(s.BearFill, Colors.Red);
            cBearBorder = Hex(s.BearBorder, Colors.Red);
            cNeutral = Hex(s.GradientNeutral, Colors.White);
            cText = Hex(s.TextColor, Colors.LightGray);
            cSd = Hex(s.SdLineColor, Colors.SeaGreen);

            bgBrush = Solid(Hex(s.BackgroundColor, Color.FromRgb(0x19, 0x1B, 0x22)), 1);
            boxBrush = bgBrush;
            textBrush = Solid(cText, 1);
            dimTextBrush = Solid(cText, 0.6);
            sdTextBrush = Solid(cSd, 1);

            framePen = MakePen(cText, 0.25, 1);
            gridPen = MakePen(cText, 0.12, 1);
            hoverPen = MakePen(cText, 0.35, 1);
            tickPen = MakePen(cText, 0.4, 1);

            sdPen = new Pen(Solid(cSd, 1), 1) { DashStyle = DashStyles.Dash };
            sdPen.Freeze();

            separatorPen = new Pen(Solid(Hex(s.SeparatorColor, Colors.Gray), s.SeparatorOpacity / 100.0), Math.Max(1, s.SeparatorWidth))
            {
                DashStyle = ToWpfDash(s.SeparatorDashStyle)
            };
            separatorPen.Freeze();

            InvalidateVisual();
        }

        // Wheel = zoom (more / fewer bars), Shift + wheel = scroll back in time
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                int step = Math.Max(1, Settings.BarsToShow / 4);
                scrollOffset = Math.Max(0, scrollOffset + (e.Delta > 0 ? step : -step));
            }
            else
            {
                int n = Settings.BarsToShow;
                n = e.Delta > 0
                    ? Math.Max(3, Math.Min(n - 1, (int)Math.Round(n / 1.2)))
                    : Math.Min(500, Math.Max(n + 1, (int)Math.Round(n * 1.2)));
                Settings.BarsToShow = n;
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            if (W <= 0 || H <= 0 || typeface == null) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var s = Settings;

            dc.DrawRectangle(bgBrush, null, new Rect(0, 0, W, H));

            const double pad = 6;
            double lineH = Text("0", textBrush, dpi).Height;
            double headerH = lineH + 4, footerH = lineH + 4;

            string header = (string.IsNullOrWhiteSpace(s.ShortName) ? "S-T" : s.ShortName) + "(" + s.NumberSeconds + ") - "
                + s.BarSeconds + "s - " + (s.InputData == TapeInput.Volume ? "V" : s.InputData == TapeInput.Orders ? "O" : "T");
            dc.DrawText(Text(header, dimTextBrush, dpi), new Point(pad, pad));

            if (Status != null)
            {
                DrawCentered(dc, Status, W, H, dpi);
                return;
            }

            // ---- snapshot of the data ----
            int n = Math.Max(1, s.BarsToShow);
            var views = new View[n];
            bool hasSd = false;
            double sdMean = 0, sdStd = 0;
            var eng = Engine;

            lock (eng.Sync)
            {
                if (eng.LastTickTime == DateTime.MinValue)
                {
                    DrawCentered(dc, eng.HistoryLoaded ? "waiting for data..." : "loading...", W, H, dpi);
                    return;
                }

                DateTime now = Connection.PlaybackConnection != null ? Connection.PlaybackConnection.Now : NinjaTrader.Core.Globals.Now;
                bool live = eng.HistoryLoaded && now >= eng.LastTickTime && (now - eng.LastTickTime).TotalMinutes < 5;
                DateTime anchorTime = live ? now : eng.LastTickTime;   // market closed: show the last data
                long anchorKey = eng.KeyOf(anchorTime);

                long firstKey = eng.Buckets.Count > 0 ? eng.Buckets[0].Key : anchorKey;
                int maxOffset = (int)Math.Max(0, Math.Min(int.MaxValue, anchorKey - firstKey - n + 1));
                if (scrollOffset > maxOffset) scrollOffset = maxOffset;
                long endKey = anchorKey - scrollOffset;

                for (int i = 0; i < n; i++)
                {
                    long key = endKey - (n - 1) + i;
                    views[i].Key = key;
                    int idx;
                    if (!eng.Index.TryGetValue(key, out idx))
                        continue;

                    var b = eng.Buckets[idx];
                    views[i].Exists = true;
                    views[i].Speed = b.Speed;
                    views[i].Peak = b.Peak;
                    views[i].Buy = b.Buy;
                    views[i].Sell = b.Sell;
                    views[i].Open = b.Open;
                    views[i].Close = b.Close;

                    // newest bar while live: the speed decays when nothing trades
                    if (live && key == anchorKey)
                        views[i].Speed = eng.WindowSumAt(now);

                    views[i].Marked = eng.IsMarked(key, views[i].Speed, s);
                }

                hasSd = s.ShowSdLines && eng.Stats(endKey, s.StdDevPeriod, out sdMean, out sdStd);
            }

            // ---- value scale ----
            double maxV = 0;
            for (int i = 0; i < n; i++)
                if (views[i].Exists)
                    maxV = Math.Max(maxV, Math.Max(views[i].Peak, views[i].Speed));
            if (hasSd)
                maxV = Math.Max(maxV, sdMean + 2 * sdStd);
            if (maxV <= 0) maxV = 1;
            maxV *= 1.1;
            double step = NiceStep(maxV / 4);

            double axisW = s.ShowValueAxis
                ? Math.Max(Text(FormatValue(Math.Floor(maxV / step) * step), textBrush, dpi).Width, Text("0", textBrush, dpi).Width) + 8
                : 0;

            double plotLeft = pad;
            double plotRight = W - pad - axisW;
            double plotTop = pad + headerH;
            double plotBottom = H - pad - footerH;
            if (plotRight - plotLeft < 20 || plotBottom - plotTop < 20)
                return;
            double plotH = plotBottom - plotTop;
            Func<double, double> Y = v => plotBottom - v / maxV * plotH;

            // grid + value labels
            if (s.ShowValueAxis)
            {
                int guard = 0;
                for (double v = 0; v <= maxV && guard++ < 20; v += step)
                {
                    double y = Math.Round(Y(v)) + 0.5;
                    dc.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));
                    var ft = Text(FormatValue(v), dimTextBrush, dpi);
                    dc.DrawText(ft, new Point(plotRight + 4, y - ft.Height / 2));
                }
            }

            double slot = (plotRight - plotLeft) / n;

            // time separators (vertical grid), behind the bars
            if (s.ShowSeparators && s.SeparatorSeconds > 0)
            {
                int bs = Engine.BarSeconds;
                long firstSec = views[0].Key * bs;
                long endSec = firstSec + (long)n * bs;
                long sep = s.SeparatorSeconds;
                long s0 = ((firstSec + sep - 1) / sep) * sep;
                if ((endSec - s0) / sep < 1000)
                {
                    for (long t = s0; t < endSec; t += sep)
                    {
                        double x = Math.Round(plotLeft + (t - firstSec) / (double)bs * slot) + 0.5;
                        dc.DrawLine(separatorPen, new Point(x, plotTop), new Point(x, plotBottom));
                    }
                }
            }

            // SD lines
            if (hasSd)
            {
                for (int k = 1; k <= 2; k++)
                {
                    double v = sdMean + k * sdStd;
                    if (v <= 0 || v > maxV) continue;
                    double y = Math.Round(Y(v)) + 0.5;
                    dc.DrawLine(sdPen, new Point(plotLeft, y), new Point(plotRight, y));
                    var ft = Text("SD+" + k, sdTextBrush, dpi);
                    dc.DrawText(ft, new Point(plotLeft + 2, y - ft.Height));
                }
            }

            // ---- bars ----
            double bodyW = Math.Max(1, slot * 0.72);
            double unmarked = s.UnmarkedOpacity / 100.0;

            for (int i = 0; i < n; i++)
            {
                View v = views[i];
                if (!v.Exists || (v.Peak <= 0 && v.Speed <= 0)) continue;

                bool bull = IsBull(v);
                double op = v.Marked ? 1.0 : unmarked;
                if (op <= 0) continue;

                Color fillC, borderC;
                if (!v.Marked && s.UseGradient)
                {
                    double total = v.Buy + v.Sell;
                    double k = total > 0 ? Math.Min(1.0, Math.Abs(v.Buy - v.Sell) / total / (s.GradientFullAt / 100.0)) : 0;
                    fillC = Lerp(cNeutral, bull ? cBullFill : cBearFill, k);
                    borderC = Lerp(cNeutral, bull ? cBullBorder : cBearBorder, k);
                }
                else
                {
                    fillC = bull ? cBullFill : cBearFill;
                    borderC = bull ? cBullBorder : cBearBorder;
                }

                var fill = Solid(fillC, op);
                var pen = MakePen(borderC, op, s.LineWidth);

                double x = plotLeft + slot * (i + 0.5);
                double yS = Y(v.Speed);
                double yP = Y(v.Peak);

                if (v.Peak > v.Speed)
                    dc.DrawLine(pen, new Point(x, yS), new Point(x, yP));

                if (plotBottom - yS >= 0.5)
                    dc.DrawRectangle(fill, pen, new Rect(x - bodyW / 2, yS, bodyW, plotBottom - yS));
            }

            // ---- time axis ----
            DrawTimeAxis(dc, views, n, plotLeft, plotRight, plotBottom, slot, W, dpi);

            // ---- history hint ----
            if (scrollOffset > 0)
            {
                var ft = Text("\u25C0 history", dimTextBrush, dpi);
                dc.DrawText(ft, new Point(Math.Max(pad, W - pad - ft.Width), pad));
            }

            // ---- hover info box ----
            if (hoverX >= plotLeft && hoverX < plotRight && hoverY >= plotTop && hoverY <= plotBottom)
            {
                int i = Math.Min(n - 1, (int)((hoverX - plotLeft) / slot));
                View v = views[i];
                if (v.Exists && (v.Peak > 0 || v.Speed > 0))
                {
                    double x = Math.Round(plotLeft + slot * (i + 0.5)) + 0.5;
                    dc.DrawLine(hoverPen, new Point(x, plotTop), new Point(x, plotBottom));
                    DrawInfoBox(dc, v, W, H, dpi);
                }
            }
        }

        private void DrawCentered(DrawingContext dc, string msg, double W, double H, double dpi)
        {
            var ft = Text(msg, dimTextBrush, dpi);
            dc.DrawText(ft, new Point(Math.Max(4, (W - ft.Width) / 2), (H - ft.Height) / 2));
        }

        private void DrawTimeAxis(DrawingContext dc, View[] views, int n, double plotLeft, double plotRight,
            double plotBottom, double slot, double W, double dpi)
        {
            dc.DrawLine(tickPen, new Point(plotLeft, plotBottom + 0.5), new Point(plotRight, plotBottom + 0.5));

            int bs = Engine.BarSeconds;
            double labelW = Text("00:00:00", textBrush, dpi).Width + 10;
            int need = Math.Max(1, (int)Math.Ceiling(labelW / slot));
            int every = need;
            int[] nice = { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400 };
            foreach (int c in nice)
            {
                if (c % bs == 0 && c / bs >= need)
                {
                    every = c / bs;
                    break;
                }
            }
            string fmt = (every * bs) % 60 == 0 ? "HH:mm" : "HH:mm:ss";

            for (int i = 0; i < n; i++)
            {
                if (views[i].Key % every != 0) continue;

                double x = Math.Round(plotLeft + slot * i) + 0.5;
                dc.DrawLine(tickPen, new Point(x, plotBottom), new Point(x, plotBottom + 3));
                var ft = Text(Engine.KeyStart(views[i].Key).ToString(fmt), dimTextBrush, dpi);
                double tx = Math.Max(2, Math.Min(W - ft.Width - 2, x - ft.Width / 2));
                dc.DrawText(ft, new Point(tx, plotBottom + 3));
            }
        }

        private void DrawInfoBox(DrawingContext dc, View v, double W, double H, double dpi)
        {
            var s = Settings;
            DateTime start = Engine.KeyStart(v.Key);
            var texts = new List<FormattedText>();

            if (s.TipTime)
                texts.Add(Text(start.ToString("HH:mm:ss") + " - " + start.AddSeconds(Engine.BarSeconds).ToString("HH:mm:ss"), dimTextBrush, dpi));
            if (s.TipEnd)
                texts.Add(Text("End:   " + FormatValue(v.Speed), textBrush, dpi));
            if (s.TipPeak)
                texts.Add(Text("Peak:  " + FormatValue(v.Peak), textBrush, dpi));
            if (s.TipDelta)
            {
                //texts.Add(Text("Delta: " + (v.Buy - v.Sell).ToString("+0;-0;0", CultureInfo.InvariantCulture), textBrush, dpi));
                double delta = v.Buy - v.Sell;
                Brush deltaBrush = delta > 0 ? Solid(cBullFill, 1) : delta < 0 ? Solid(cBearFill, 1) : textBrush;
                texts.Add(Text("Delta: " + delta.ToString("+0;-0;0", CultureInfo.InvariantCulture), deltaBrush, dpi));
            }
            if (s.TipBuySell)
                texts.Add(Text("Buy " + FormatValue(v.Buy) + " / Sell " + FormatValue(v.Sell), textBrush, dpi));

            if (texts.Count == 0)
                return;

            double tw = 0, th = 0;
            foreach (var ft in texts)
            {
                tw = Math.Max(tw, ft.Width);
                th += ft.Height;
            }

            const double bp = 6;
            double bw = tw + bp * 2, bh = th + bp * 2;

            double bx = hoverX + 14;
            if (bx + bw > W - 2) bx = hoverX - 14 - bw;
            bx = Math.Max(2, Math.Min(bx, W - bw - 2));
            double by = Math.Max(2, Math.Min(hoverY - bh / 2, H - bh - 2));

            dc.DrawRoundedRectangle(boxBrush, framePen, new Rect(bx + 0.5, by + 0.5, bw, bh), 3, 3);

            double ty = by + bp;
            foreach (var ft in texts)
            {
                dc.DrawText(ft, new Point(bx + bp, ty));
                ty += ft.Height;
            }
        }

        private bool IsBull(View v)
        {
            if (Settings.ColorBy == TapeColorBy.Delta)
            {
                double d = v.Buy - v.Sell;
                if (d != 0) return d > 0;
            }
            return v.Close >= v.Open;
        }

        // ---- helpers ----
        private FormattedText Text(string str, Brush b, double dpi)
        {
            return new FormattedText(str, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, b, dpi);
        }

        private static string FormatValue(double v)
        {
            if (v >= 10000)
                return (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return v.ToString("0", CultureInfo.InvariantCulture);
        }

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double r = raw / mag;
            double nice = r <= 1 ? 1 : r <= 2 ? 2 : r <= 5 ? 5 : 10;
            return Math.Max(1, nice * mag);
        }

        public static Color Hex(string hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return (Color)ColorConverter.ConvertFromString(hex.Trim());
            }
            catch { }
            return fallback;
        }

        private static DashStyle ToWpfDash(DashStyleHelper d)
        {
            switch (d)
            {
                case DashStyleHelper.Dash: return DashStyles.Dash;
                case DashStyleHelper.DashDot: return DashStyles.DashDot;
                case DashStyleHelper.DashDotDot: return DashStyles.DashDotDot;
                case DashStyleHelper.Dot: return DashStyles.Dot;
                default: return DashStyles.Solid;
            }
        }

        private static Color Lerp(Color a, Color b, double k)
        {
            return Color.FromArgb(
                (byte)Math.Round(a.A + (b.A - a.A) * k),
                (byte)Math.Round(a.R + (b.R - a.R) * k),
                (byte)Math.Round(a.G + (b.G - a.G) * k),
                (byte)Math.Round(a.B + (b.B - a.B) * k));
        }

        private static SolidColorBrush Solid(Color c, double opacity)
        {
            var b = new SolidColorBrush(c) { Opacity = opacity };
            b.Freeze();
            return b;
        }

        private static Pen MakePen(Color c, double opacity, double width)
        {
            var p = new Pen(Solid(c, opacity), width);
            p.Freeze();
            return p;
        }
    }

    // =========================================================================
    //  SETTINGS DIALOG (built from the [Display] attributes of TapeSettings)
    // =========================================================================
    public class TapeSettingsWindow : NTWindow
    {
        private TapeSettings edit;
        private readonly Action<TapeSettings> apply;
        private readonly List<Func<string>> commits = new List<Func<string>>();   // each returns an error or null
        private readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        private static readonly string[] GroupOrder = { "Data", "Highlight", "Bars", "Panel", "Time Separators", "Tooltip" };

        public TapeSettingsWindow(TapeSettings current, Action<TapeSettings> apply)
        {
            Caption = "Speed of Tape - Settings";
            Width = 470;
            Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            edit = current.Clone();
            this.apply = apply;

            BuildEditors();

            // ---- bottom bar: Template menu on the left, OK / Cancel / Apply on the right ----
            var templateBtn = new Button { Content = "Template ▾", MinWidth = 90, Margin = new Thickness(4) };
            templateBtn.Click += (s, e) => ShowTemplateMenu(templateBtn);

            var ok = new Button { Content = "OK", MinWidth = 70, Margin = new Thickness(4), IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(4), IsCancel = true };
            var applyBtn = new Button { Content = "Apply", MinWidth = 70, Margin = new Thickness(4) };
            ok.Click += (s, e) => { if (Commit()) Close(); };
            applyBtn.Click += (s, e) => Commit();
            cancel.Click += (s, e) => Close();

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(ok);
            right.Children.Add(cancel);
            right.Children.Add(applyBtn);

            var bar = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(templateBtn, Dock.Left);
            bar.Children.Add(templateBtn);
            bar.Children.Add(right);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(scroll);
            Content = root;
        }

        // (re)creates every editor from 'edit' - also used after loading a template
        private void BuildEditors()
        {
            commits.Clear();

            var grid = new Grid { Margin = new Thickness(10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var props = typeof(TapeSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => new { P = p, D = p.GetCustomAttribute<DisplayAttribute>() })
                .Where(x => x.D != null)
                .ToList();

            int row = 0;
            foreach (string group in GroupOrder)
            {
                var items = props.Where(x => x.D.GetGroupName() == group).OrderBy(x => x.D.GetOrder() ?? 0).ToList();
                if (items.Count == 0) continue;

                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var header = new TextBlock
                {
                    Text = group,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 4)
                };
                Grid.SetRow(header, row);
                Grid.SetColumnSpan(header, 2);
                grid.Children.Add(header);
                row++;

                foreach (var it in items)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var label = new TextBlock
                    {
                        Text = it.D.GetName(),
                        ToolTip = it.D.GetDescription(),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 2, 6, 2)
                    };
                    Grid.SetRow(label, row);
                    grid.Children.Add(label);

                    FrameworkElement editor = MakeEditor(it.P, it.D);
                    editor.Margin = new Thickness(0, 2, 0, 2);
                    editor.ToolTip = it.D.GetDescription();
                    Grid.SetRow(editor, row);
                    Grid.SetColumn(editor, 1);
                    grid.Children.Add(editor);
                    row++;
                }
            }

            scroll.Content = grid;
        }

        // reads every editor into 'edit'; false (and a message) if a value is invalid
        private bool ReadEditors()
        {
            var errors = commits.Select(c => c()).Where(e => e != null).ToList();
            if (errors.Count > 0)
            {
                MessageBox.Show(string.Join("\n", errors), "Speed of Tape", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private bool Commit()
        {
            if (!ReadEditors())
                return false;
            apply(edit.Clone());
            return true;
        }

        // =====================================================================
        //  TEMPLATES
        // =====================================================================
        private void ShowTemplateMenu(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };

            var save = new MenuItem { Header = "Save as..." };
            save.Click += (s, e) => SaveTemplateAs();
            menu.Items.Add(save);

            var saveDefault = new MenuItem { Header = "Save as default", ToolTip = "New Speed of Tape windows start with these settings." };
            saveDefault.Click += (s, e) => SaveTemplate(TapeSettings.DefaultTemplate, true);
            menu.Items.Add(saveDefault);

            menu.Items.Add(new Separator());

            List<string> names = TapeSettings.ListTemplates();

            var load = new MenuItem { Header = "Load", IsEnabled = names.Count > 0 };
            var delete = new MenuItem { Header = "Delete", IsEnabled = names.Count > 0 };
            foreach (string n in names)
            {
                string name = n;
                var li = new MenuItem { Header = name };
                li.Click += (s, e) => LoadTemplate(name);
                load.Items.Add(li);

                var di = new MenuItem { Header = name };
                di.Click += (s, e) => DeleteTemplate(name);
                delete.Items.Add(di);
            }
            menu.Items.Add(load);
            menu.Items.Add(delete);

            menu.IsOpen = true;
        }

        private void SaveTemplateAs()
        {
            string name = AskName();
            if (string.IsNullOrWhiteSpace(name))
                return;
            SaveTemplate(name.Trim(), false);
        }

        private void SaveTemplate(string name, bool isDefault)
        {
            if (!ReadEditors())
                return;

            if (!isDefault && TapeSettings.TemplateExists(name)
                && MessageBox.Show("Template \"" + name + "\" already exists. Overwrite it?", "Speed of Tape",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                edit.SaveTemplate(name);
                MessageBox.Show(isDefault
                        ? "Saved as default. New Speed of Tape windows will start with these settings."
                        : "Template \"" + name + "\" saved.",
                    "Speed of Tape", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the template:\n" + ex.Message, "Speed of Tape", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // loads into the dialog only: OK / Apply sends it to the window
        private void LoadTemplate(string name)
        {
            TapeSettings t = TapeSettings.LoadTemplate(name);
            if (t == null)
            {
                MessageBox.Show("Could not read template \"" + name + "\".", "Speed of Tape", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            edit = t;
            BuildEditors();
        }

        private void DeleteTemplate(string name)
        {
            if (MessageBox.Show("Delete template \"" + name + "\"?", "Speed of Tape",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                TapeSettings.DeleteTemplate(name);
        }

        // small dialog asking for the template name
        private string AskName()
        {
            var dlg = new NTWindow
            {
                Caption = "Save Template",
                Width = 320,
                Height = 150,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            var tb = new TextBox { Margin = new Thickness(10, 4, 10, 4) };
            var ok = new Button { Content = "Save", MinWidth = 70, Margin = new Thickness(4), IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(4), IsCancel = true };
            string result = null;
            ok.Click += (s, e) => { result = tb.Text; dlg.Close(); };
            cancel.Click += (s, e) => dlg.Close();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Template name:", Margin = new Thickness(10, 10, 10, 0) });
            panel.Children.Add(tb);
            panel.Children.Add(buttons);
            dlg.Content = panel;

            dlg.Loaded += (s, e) => tb.Focus();
            dlg.ShowDialog();
            return result;
        }

        private FrameworkElement MakeEditor(PropertyInfo p, DisplayAttribute d)
        {
            Type t = p.PropertyType;
            string name = d.GetName();
            var range = p.GetCustomAttribute<RangeAttribute>();

            if (t == typeof(bool))
            {
                var cb = new CheckBox { IsChecked = (bool)p.GetValue(edit), VerticalAlignment = VerticalAlignment.Center };
                commits.Add(() => { p.SetValue(edit, cb.IsChecked == true); return null; });
                return cb;
            }

            if (t.IsEnum)
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues(t), SelectedItem = p.GetValue(edit) };
                commits.Add(() => { if (combo.SelectedItem != null) p.SetValue(edit, combo.SelectedItem); return null; });
                return combo;
            }

            if (t == typeof(int) || t == typeof(double))
            {
                var tb = new TextBox { Text = Convert.ToString(p.GetValue(edit), CultureInfo.CurrentCulture) };
                commits.Add(() =>
                {
                    double v;
                    if (!double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v)
                        && !double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        return name + ": not a number";
                    if (range != null)
                        v = Math.Max(Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture),
                            Math.Min(Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture), v));
                    if (t == typeof(int))
                        p.SetValue(edit, (int)Math.Round(v));
                    else
                        p.SetValue(edit, v);
                    tb.Text = Convert.ToString(p.GetValue(edit), CultureInfo.CurrentCulture);
                    return null;
                });
                return tb;
            }

            if (t == typeof(string) && p.GetCustomAttribute<ColorHexAttribute>() != null)
            {
                var panel = new DockPanel();
                var swatch = new Border
                {
                    Width = 20,
                    Height = 18,
                    Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray
                };
                var tb = new TextBox { Text = (string)p.GetValue(edit) };
                Action refresh = () =>
                {
                    try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tb.Text.Trim())); }
                    catch { swatch.Background = null; }
                };
                tb.TextChanged += (s, e) => refresh();
                refresh();
                DockPanel.SetDock(swatch, Dock.Left);
                panel.Children.Add(swatch);
                panel.Children.Add(tb);
                commits.Add(() =>
                {
                    try { ColorConverter.ConvertFromString(tb.Text.Trim()); }
                    catch { return name + ": color must be like #FF1B88F4 or a name like Red"; }
                    p.SetValue(edit, tb.Text.Trim());
                    return null;
                });
                return panel;
            }

            var text = new TextBox { Text = Convert.ToString(p.GetValue(edit), CultureInfo.CurrentCulture) };
            commits.Add(() => { p.SetValue(edit, text.Text); return null; });
            return text;
        }
    }

    // =========================================================================
    //  MAIN WINDOW
    // =========================================================================
    public class SpeedOfTapeWindow : NTWindow, IWorkspacePersistence
    {
        // new windows start from the "Default" template when one was saved (workspace restore overrides it)
        private TapeSettings settings = TapeSettings.LoadTemplate(TapeSettings.DefaultTemplate) ?? new TapeSettings();
        private readonly TapeEngine engine = new TapeEngine();
        private readonly TapeGauge gauge;
        private readonly InstrumentSelector instrumentSelector;
        private readonly DispatcherTimer timer;

        private Instrument instrument;
        private BarsRequest request;
        private EventHandler<BarsUpdateEventArgs> updateHandler;
        private int generation;   // increases on every reload: old requests are ignored

        public SpeedOfTapeWindow()
        {
            Caption = "Speed of Tape";
            Width = 420;
            Height = 320;

            // ---- toolbar: instrument selector + settings button ----
            instrumentSelector = new InstrumentSelector
            {
                LastUsedGroup = "SpeedOfTape",
                MinWidth = 150,
                Margin = new Thickness(4, 4, 4, 4)
            };
            instrumentSelector.InstrumentChanged += OnInstrumentChanged;

            var settingsButton = new Button
            {
                Content = "\u2699",
                ToolTip = "Settings",
                MinWidth = 28,
                Margin = new Thickness(0, 4, 4, 4)
            };
            settingsButton.Click += (s, e) => OpenSettings();

            var toolbar = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(instrumentSelector, Dock.Left);
            DockPanel.SetDock(settingsButton, Dock.Right);
            toolbar.Children.Add(instrumentSelector);
            toolbar.Children.Add(settingsButton);

            // ---- gauge ----
            gauge = new TapeGauge(settings, engine) { Status = "Select an instrument" };
            gauge.SettingsRequested += (s, e) => OpenSettings();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(toolbar, 0);
            Grid.SetRow(gauge, 1);
            grid.Children.Add(toolbar);
            grid.Children.Add(gauge);
            Content = grid;
            Topmost = settings.AlwaysOnTop;

            timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) => gauge.InvalidateVisual();
            timer.Start();

            // WorkspaceOptions property must be set
            Loaded += (o, e) =>
            {
                if (WorkspaceOptions == null)
                    WorkspaceOptions = new WorkspaceOptions("SpeedOfTape-" + Guid.NewGuid().ToString("N"), this);
            };

            Closed += (o, e) =>
            {
                timer.Stop();
                StopRequest();
            };
        }

        // ---- instrument / data ----
        private void OnInstrumentChanged(object sender, EventArgs e)
        {
            Instrument inst = sender as Instrument ?? instrumentSelector.Instrument;
            if (inst != null && inst != instrument)
                LoadInstrument(inst);
        }

        private void LoadInstrument(Instrument inst)
        {
            StopRequest();
            instrument = inst;
            int gen = ++generation;

            lock (engine.Sync)
                engine.Reset(settings);
            gauge.ResetScroll();

            if (inst == null)
            {
                Caption = "Speed of Tape";
                gauge.Status = "Select an instrument";
                return;
            }

            Caption = "Speed of Tape - " + inst.FullName;
            gauge.Status = "Loading " + inst.FullName + "...";

            var req = new BarsRequest(inst, StartDate(settings.DaysToLoad), DateTime.Now)
            {
                BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 },
                TradingHours = inst.MasterInstrument.TradingHours
            };

            // realtime: every new tick arrives through Update
            updateHandler = (s, e) => ProcessRange(req, e.MinIndex, e.MaxIndex, gen);
            req.Update += updateHandler;
            request = req;

            req.Request((r, errorCode, errorMessage) =>
            {
                if (gen != generation)
                    return;

                if (errorCode != ErrorCode.NoError)
                {
                    Dispatcher.InvokeAsync(() => { if (gen == generation) gauge.Status = "Error: " + errorMessage; });
                    return;
                }

                // history (anything already processed by Update is skipped)
                ProcessRange(r, 0, r.Bars.Count - 1, gen);
                lock (engine.Sync)
                    engine.HistoryLoaded = true;

                Dispatcher.InvokeAsync(() => { if (gen == generation) gauge.Status = null; });
            });
        }

        // Processes tick bars [from..to] in chunks, so the lock is never held for long
        private void ProcessRange(BarsRequest r, int from, int to, int gen)
        {
            if (r == null || r.Bars == null)
                return;
            Bars b = r.Bars;
            int i = Math.Max(0, from);

            while (i <= to)
            {
                lock (engine.Sync)
                {
                    if (gen != generation)
                        return;
                    int end = Math.Min(to, i + 5000);
                    for (; i <= end; i++)
                    {
                        if (i <= engine.LastBarIndex)
                            continue;
                        engine.Process(b.GetTime(i), b.GetClose(i), b.GetVolume(i), b.GetAsk(i), b.GetBid(i));
                        engine.LastBarIndex = i;
                    }
                }
            }
        }

        private void StopRequest()
        {
            var req = request;
            request = null;
            if (req != null)
            {
                if (updateHandler != null)
                    req.Update -= updateHandler;
                req.Dispose();
            }
            updateHandler = null;
        }

        // N trading days back (weekends skipped), from 6 hours before midnight so evening session starts are included
        private static DateTime StartDate(int days)
        {
            DateTime d = DateTime.Now.Date;
            while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday)
                d = d.AddDays(-1);
            int counted = 1;
            while (counted < Math.Max(1, days))
            {
                d = d.AddDays(-1);
                if (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday)
                    counted++;
            }
            return d.AddHours(-6);
        }

        // ---- settings ----
        private void OpenSettings()
        {
            var dlg = new TapeSettingsWindow(settings, ApplySettings) { Owner = this };
            dlg.ShowDialog();
        }

        private void ApplySettings(TapeSettings ns)
        {
            bool reload = settings.NeedsReload(ns);
            settings = ns;
            gauge.Settings = ns;
            gauge.BuildResources();
            Topmost = ns.AlwaysOnTop;
            if (reload && instrument != null)
                LoadInstrument(instrument);
            gauge.InvalidateVisual();
        }

        // ---- workspace ----
        // IWorkspacePersistence member. Required for restoring window from workspace
        public void Restore(XDocument document, XElement element)
        {
            var xs = element.Element("SpeedOfTapeSettings");
            if (xs != null)
            {
                settings = TapeSettings.FromXml(xs.Value);
                gauge.Settings = settings;
                gauge.BuildResources();
                Topmost = settings.AlwaysOnTop;
            }

            var xi = element.Element("SpeedOfTapeInstrument");
            if (xi != null && !string.IsNullOrEmpty(xi.Value))
            {
                Instrument inst = Instrument.GetInstrument(xi.Value);
                if (inst != null)
                {
                    instrumentSelector.Instrument = inst;
                    if (inst != instrument)
                        LoadInstrument(inst);
                }
            }
        }

        // IWorkspacePersistence member. Required for saving window to workspace
        public void Save(XDocument document, XElement element)
        {
            element.Add(new XElement("SpeedOfTapeSettings", settings.ToXml()));
            element.Add(new XElement("SpeedOfTapeInstrument", instrument != null ? instrument.FullName : string.Empty));
        }

        // IWorkspacePersistence member
        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    // =========================================================================
    //  ADD-ON: menu item in Control Center > New
    // =========================================================================
    public class SpeedOfTapeAddOn : AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem existingMenuItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SpeedOfTape";
                Description = "Speed of Tape (Instant) in its own window.";
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            ControlCenter cc = window as ControlCenter;
            if (cc == null)
                return;

            existingMenuItem = cc.FindFirst("ControlCenterMenuItemNew") as NTMenuItem;
            if (existingMenuItem == null)
                return;

            menuItem = new NTMenuItem
            {
                Header = "Speed of Tape",
                Style = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            existingMenuItem.Items.Add(menuItem);
            menuItem.Click += OnMenuItemClick;
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (menuItem != null && window is ControlCenter)
            {
                if (existingMenuItem != null && existingMenuItem.Items.Contains(menuItem))
                    existingMenuItem.Items.Remove(menuItem);

                menuItem.Click -= OnMenuItemClick;
                menuItem = null;
            }
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            NinjaTrader.Core.Globals.RandomDispatcher.BeginInvoke(new Action(() => new SpeedOfTapeWindow().Show()));
        }
    }
}