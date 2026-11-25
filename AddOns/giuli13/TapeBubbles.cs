#region Using declarations
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
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
#endregion

// A Time & Sales tape without numbers: one bubble per print (or per aggressive order), stacked in time order.
//   Bubble size  = size traded
//   Bubble color = side (blue = buy at the offer, red = sell at the bid) and grade:
//                  grade 1 (small, dark) / grade 2 (medium) / grade 3 (big, bright)
//   Prints: like the NT Time & Sales you choose above ask / at ask / between / at bid / below bid;
//           above ask and below bid can get a mark next to the bubble ("AA" / "BB" by default)
//   Time separators: horizontal line between bubbles of different N-second blocks
// Mouse: wheel = scroll back / forward in time (the tape pauses), Ctrl + wheel = bubble zoom,
//        hover = print details, click "PAUSED" (or End / Esc) = back to live,
//        double-click (or the gear button) = settings.

namespace NinjaTrader.NinjaScript.AddOns.giuli13
{
    // =========================================================================
    //  ENUMS / SETTINGS
    // =========================================================================
    public enum BubbleCombine
    {
        Off = 0,                // one bubble per print
        SameTimeAndSide = 1     // consecutive prints with the same timestamp and side (a sweep) = one bubble
    }

    public enum BubbleScale
    {
        Linear = 0,             // diameter proportional to size
        Area = 1                // area proportional to size (big prints stand out less brutally)
    }

    // where the print happened compared to the bid / ask at that moment
    public enum PrintPosition
    {
        BelowBid = -2,
        AtBid = -1,
        Between = 0,
        AtAsk = 1,
        AboveAsk = 2,
        Unknown = 9             // no valid bid / ask for that print (always shown, side by tick rule)
    }

    public enum OutsideHighlight
    {
        Marks = 0,              // short text next to the bubble ("AA" / "BB")
        RowBackground = 1       // colored background across the bubble's row
    }

    public enum BubbleLayout
    {
        Center = 0,             // single column
        SplitSides = 1          // sells on the left, buys on the right
    }

    // marks a string property that holds a color as #AARRGGBB
    [AttributeUsage(AttributeTargets.Property)]
    public class BubbleColorHexAttribute : Attribute { }

    public class BubbleSettings
    {
        public BubbleSettings()
        {
            MinutesToLoad = 10;
            CombinePrints = BubbleCombine.Off;
            MaxPrints = 3000;

            ShowAboveAsk = true;
            ShowAtAsk = true;
            ShowBetween = false;
            ShowAtBid = true;
            ShowBelowBid = true;
            MarkOutside = true;
            AboveAskMark = "AA";
            BelowBidMark = "BB";
            AboveAskMarkColor = "#FF6CC4FF";
            BelowBidMarkColor = "#FFFF6A4A";
            HighlightStyle = OutsideHighlight.Marks;
            AboveAskRowColor = "#FF1B88F4";
            BelowBidRowColor = "#FFB82B2B";
            RowOpacity = 35;

            FilterMinSize = 1;
            Grade2Size = 10;
            Grade3Size = 25;

            Buy1 = "#FF0E4C8C";
            Buy2 = "#FF1B88F4";
            Buy3 = "#FF6CC4FF";
            Sell1 = "#FF75100B";
            Sell2 = "#FFF02102";
            Sell3 = "#FFFF6A4A";

            MinDiameter = 4;
            MaxDiameter = 65;
            SizeForMaxBubble = 50;
            Scale = BubbleScale.Area;
            BubbleOpacity = 95;
            BorderWidth = 1;
            CustomBorder = false;
            BuyBorderColor = "#FFFFFFFF";
            SellBorderColor = "#FFFFFFFF";
            BubbleGap = 0;
            Layout = BubbleLayout.Center;
            NewestOnTop = true;
            ShowSizeLabel = true;
            HideSmallLabels = true;
            LabelMinSize = 1;

            BackgroundColor = "#FF191B22";
            TextColor = "#FFB0B0B0";
            FontFamilyName = "Arial";
            FontSize = 11;
            ShortName = "T-B";
            AlwaysOnTop = false;

            ShowSeparators = true;
            SeparatorSeconds = 30;
            SeparatorColor = "#FF808080";
            SeparatorDashStyle = DashStyleHelper.Dash;
            SeparatorWidth = 1;
            SeparatorOpacity = 50;
            ShowSeparatorTime = true;
            SeparatorFontSize = 8;

            TipTime = true;
            TipPrice = true;
            TipSize = true;
            TipSide = true;
        }

        // ===== Data (changing these reloads the data) =====
        [Range(1, 1440)]
        [Display(Name = "Minutes To Load", Order = 0, GroupName = "Data",
            Description = "Minutes of tick history loaded when the window opens (you can scroll back through them).")]
        public int MinutesToLoad { get; set; }

        [Display(Name = "Combine Prints", Order = 1, GroupName = "Data",
            Description = "SameTimeAndSide = consecutive prints with the same timestamp and the same aggressor side (a sweep through the book) become one bubble. Off = one bubble per print.")]
        public BubbleCombine CombinePrints { get; set; }

        [Range(500, 100000)]
        [Display(Name = "Max Prints Kept", Order = 2, GroupName = "Data",
            Description = "How far back you can scroll. Only the prints that pass the Prints filters are kept; oldest ones are dropped beyond this number.")]
        public int MaxPrints { get; set; }

        // ===== Prints (like the NT Time & Sales filters; changing these reloads the data) =====
        [Display(Name = "Show Above Ask", Order = 0, GroupName = "Prints", Description = "Prints above the ask (aggressive buys sweeping the offer).")]
        public bool ShowAboveAsk { get; set; }

        [Display(Name = "Show At Ask", Order = 1, GroupName = "Prints", Description = "Prints at the ask (buys).")]
        public bool ShowAtAsk { get; set; }

        [Display(Name = "Show Between", Order = 2, GroupName = "Prints",
            Description = "Prints between bid and ask. Their side is guessed with the tick rule. Hidden prints never merge into bubbles.")]
        public bool ShowBetween { get; set; }

        [Display(Name = "Show At Bid", Order = 3, GroupName = "Prints", Description = "Prints at the bid (sells).")]
        public bool ShowAtBid { get; set; }

        [Display(Name = "Show Below Bid", Order = 4, GroupName = "Prints", Description = "Prints below the bid (aggressive sells sweeping the bid).")]
        public bool ShowBelowBid { get; set; }

        [Display(Name = "Highlight Above Ask / Below Bid", Order = 5, GroupName = "Prints",
            Description = "Highlights the bubbles that traded above the ask or below the bid.")]
        public bool MarkOutside { get; set; }

        [Display(Name = "Highlight Style", Order = 6, GroupName = "Prints",
            Description = "Marks = short text next to the bubble. RowBackground = colored background across the bubble's row.")]
        public OutsideHighlight HighlightStyle { get; set; }

        [Display(Name = "Above Ask Mark", Order = 7, GroupName = "Prints", Description = "Text used with Highlight Style = Marks.")]
        public string AboveAskMark { get; set; }

        [Display(Name = "Below Bid Mark", Order = 8, GroupName = "Prints", Description = "Text used with Highlight Style = Marks.")]
        public string BelowBidMark { get; set; }

        // same Order as Below Bid Mark: shown right after it (declaration order)
        [BubbleColorHex]
        [Display(Name = "Above Ask Mark Color", Order = 8, GroupName = "Prints", Description = "Color of the above ask mark (Highlight Style = Marks).")]
        public string AboveAskMarkColor { get; set; }

        [BubbleColorHex]
        [Display(Name = "Below Bid Mark Color", Order = 8, GroupName = "Prints", Description = "Color of the below bid mark (Highlight Style = Marks).")]
        public string BelowBidMarkColor { get; set; }

        [BubbleColorHex]
        [Display(Name = "Above Ask Row Color", Order = 9, GroupName = "Prints", Description = "Used with Highlight Style = RowBackground.")]
        public string AboveAskRowColor { get; set; }

        [BubbleColorHex]
        [Display(Name = "Below Bid Row Color", Order = 10, GroupName = "Prints", Description = "Used with Highlight Style = RowBackground.")]
        public string BelowBidRowColor { get; set; }

        [Range(5, 100)]
        [Display(Name = "Row Opacity (%)", Order = 11, GroupName = "Prints",
            Description = "Opacity of the row background. Keep it low, otherwise a blue bubble on a blue row (or red on red) disappears.")]
        public int RowOpacity { get; set; }

        // ===== Grades =====
        [Range(0, double.MaxValue)]
        [Display(Name = "Filter Min Size", Order = 0, GroupName = "Grades",
            Description = "Prints smaller than this are not shown.")]
        public double FilterMinSize { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Grade 2 From Size", Order = 1, GroupName = "Grades",
            Description = "Prints at or above this size use the grade 2 (medium) color. Below = grade 1 (small).")]
        public double Grade2Size { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Grade 3 From Size", Order = 2, GroupName = "Grades",
            Description = "Prints at or above this size use the grade 3 (big / aggressive) color.")]
        public double Grade3Size { get; set; }

        // ===== Colors =====
        [BubbleColorHex]
        [Display(Name = "Dominant Buy 1 (small)", Order = 0, GroupName = "Colors")]
        public string Buy1 { get; set; }

        [BubbleColorHex]
        [Display(Name = "Dominant Buy 2 (medium)", Order = 1, GroupName = "Colors")]
        public string Buy2 { get; set; }

        [BubbleColorHex]
        [Display(Name = "Dominant Buy 3 (big)", Order = 2, GroupName = "Colors")]
        public string Buy3 { get; set; }

        [BubbleColorHex]
        [Display(Name = "Dominant Sell 1 (small)", Order = 3, GroupName = "Colors")]
        public string Sell1 { get; set; }

        [BubbleColorHex]
        [Display(Name = "Dominant Sell 2 (medium)", Order = 4, GroupName = "Colors")]
        public string Sell2 { get; set; }

        [BubbleColorHex]
        [Display(Name = "Dominant Sell 3 (big)", Order = 5, GroupName = "Colors")]
        public string Sell3 { get; set; }

        // ===== Bubbles =====
        [Range(2, 200)]
        [Display(Name = "Min Diameter (px)", Order = 0, GroupName = "Bubbles")]
        public double MinDiameter { get; set; }

        [Range(4, 400)]
        [Display(Name = "Max Diameter (px)", Order = 1, GroupName = "Bubbles",
            Description = "Also changed with Ctrl + mouse wheel. Never wider than the column.")]
        public double MaxDiameter { get; set; }

        [Range(1, double.MaxValue)]
        [Display(Name = "Size For Max Bubble", Order = 2, GroupName = "Bubbles",
            Description = "Prints of this size or bigger get the max diameter.")]
        public double SizeForMaxBubble { get; set; }

        [Display(Name = "Size Scale", Order = 3, GroupName = "Bubbles",
            Description = "Linear = diameter grows with size. Area = bubble area grows with size.")]
        public BubbleScale Scale { get; set; }

        [Range(5, 100)]
        [Display(Name = "Opacity (%)", Order = 5, GroupName = "Bubbles")]
        public int BubbleOpacity { get; set; }

        [Range(0, 5)]
        [Display(Name = "Border Width", Order = 6, GroupName = "Bubbles", Description = "0 = no border.")]
        public double BorderWidth { get; set; }

        // same Order as Border Width: shown right after it (declaration order)
        [Display(Name = "Custom Border Color", Order = 6, GroupName = "Bubbles",
            Description = "Off = each bubble gets a slightly lighter border of its own color. On = the colors below.")]
        public bool CustomBorder { get; set; }

        [BubbleColorHex]
        [Display(Name = "Buy Border Color", Order = 6, GroupName = "Bubbles", Description = "Used with Custom Border Color on (all buy grades).")]
        public string BuyBorderColor { get; set; }

        [BubbleColorHex]
        [Display(Name = "Sell Border Color", Order = 6, GroupName = "Bubbles", Description = "Used with Custom Border Color on (all sell grades).")]
        public string SellBorderColor { get; set; }

        [Range(0, 20)]
        [Display(Name = "Gap (px)", Order = 7, GroupName = "Bubbles", Description = "Vertical space between bubbles.")]
        public double BubbleGap { get; set; }

        [Display(Name = "Layout", Order = 8, GroupName = "Bubbles",
            Description = "Center = one column. SplitSides = sells on the left, buys on the right.")]
        public BubbleLayout Layout { get; set; }

        [Display(Name = "Newest On Top", Order = 9, GroupName = "Bubbles",
            Description = "On = new prints enter at the top (like Time & Sales). Off = at the bottom.")]
        public bool NewestOnTop { get; set; }

        [Display(Name = "Show Size In Bubble", Order = 10, GroupName = "Bubbles",
            Description = "Writes the size inside the bubbles that are big enough.")]
        public bool ShowSizeLabel { get; set; }

        [Display(Name = "Hide Small Size Labels", Order = 11, GroupName = "Bubbles",
            Description = "On: no size written inside bubbles with size <= Label Min Size.")]
        public bool HideSmallLabels { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Label Min Size", Order = 12, GroupName = "Bubbles",
            Description = "With Hide Small Size Labels on, bubbles of this size or smaller show no number.")]
        public double LabelMinSize { get; set; }

        // ===== Panel =====
        [BubbleColorHex]
        [Display(Name = "Background Color", Order = 0, GroupName = "Panel")]
        public string BackgroundColor { get; set; }

        [BubbleColorHex]
        [Display(Name = "Text Color", Order = 1, GroupName = "Panel")]
        public string TextColor { get; set; }

        [Display(Name = "Font", Order = 2, GroupName = "Panel")]
        public string FontFamilyName { get; set; }

        [Range(6, 40)]
        [Display(Name = "Font Size", Order = 3, GroupName = "Panel")]
        public double FontSize { get; set; }

        [Display(Name = "Short Name", Order = 4, GroupName = "Panel")]
        public string ShortName { get; set; }

        [Display(Name = "Always On Top", Order = 5, GroupName = "Panel",
            Description = "Keep the Tape Bubbles window above all other windows.")]
        public bool AlwaysOnTop { get; set; }

        // ===== Time separators =====
        [Display(Name = "Show Separators", Order = 0, GroupName = "Time Separators",
            Description = "Horizontal line between two bubbles that fall in different time blocks (e.g. every 10 / 30 / 60 seconds).")]
        public bool ShowSeparators { get; set; }

        [Range(1, 86400)]
        [Display(Name = "Interval (seconds)", Order = 1, GroupName = "Time Separators",
            Description = "10 = a line every 10 sec, 30 = 30 sec, 60 = 1 min. Blocks sit on round clock times.")]
        public int SeparatorSeconds { get; set; }

        [BubbleColorHex]
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

        [Display(Name = "Show Values On Separator", Order = 6, GroupName = "Time Separators",
            Description = "Writes the time (HH:mm:ss) above each separator line, on the far left, in the separator color.")]
        public bool ShowSeparatorTime { get; set; }

        [Range(5, 12)]
        [Display(Name = "Values Font Size", Order = 7, GroupName = "Time Separators",
            Description = "Keep it small so the time does not overlap the bubbles.")]
        public double SeparatorFontSize { get; set; }

        // ===== Tooltip =====
        [Display(Name = "Show Time", Order = 0, GroupName = "Tooltip")]
        public bool TipTime { get; set; }

        [Display(Name = "Show Price", Order = 1, GroupName = "Tooltip", Description = "Price (from - to when a sweep went through several levels).")]
        public bool TipPrice { get; set; }

        [Display(Name = "Show Size", Order = 2, GroupName = "Tooltip", Description = "Size traded and number of prints merged.")]
        public bool TipSize { get; set; }

        [Display(Name = "Show Side", Order = 3, GroupName = "Tooltip", Description = "Buy / Sell and grade.")]
        public bool TipSide { get; set; }

        // ---- helpers ----
        public string ToXml()
        {
            var ser = new XmlSerializer(typeof(BubbleSettings));
            using (var sw = new StringWriter(CultureInfo.InvariantCulture))
            {
                ser.Serialize(sw, this);
                return sw.ToString();
            }
        }

        public static BubbleSettings FromXml(string xml)
        {
            try
            {
                var ser = new XmlSerializer(typeof(BubbleSettings));
                using (var sr = new StringReader(xml))
                    return (BubbleSettings)ser.Deserialize(sr);
            }
            catch
            {
                return new BubbleSettings();
            }
        }

        public BubbleSettings Clone()
        {
            return FromXml(ToXml());
        }

        // true when the change needs the tick data to be reloaded
        public bool NeedsReload(BubbleSettings other)
        {
            return other == null
                || MinutesToLoad != other.MinutesToLoad
                || CombinePrints != other.CombinePrints
                || ShowAboveAsk != other.ShowAboveAsk
                || ShowAtAsk != other.ShowAtAsk
                || ShowBetween != other.ShowBetween
                || ShowAtBid != other.ShowAtBid
                || ShowBelowBid != other.ShowBelowBid;
        }

        public bool Shows(PrintPosition pos)
        {
            switch (pos)
            {
                case PrintPosition.AboveAsk: return ShowAboveAsk;
                case PrintPosition.AtAsk: return ShowAtAsk;
                case PrintPosition.Between: return ShowBetween;
                case PrintPosition.AtBid: return ShowAtBid;
                case PrintPosition.BelowBid: return ShowBelowBid;
                default: return true;   // Unknown: no bid / ask data, never hide everything
            }
        }

        // 1, 2 or 3
        public int GradeOf(double size)
        {
            if (Grade3Size > 0 && size >= Grade3Size) return 3;
            if (Grade2Size > 0 && size >= Grade2Size) return 2;
            return 1;
        }

        // ---- templates: Documents\NinjaTrader 8\templates\TapeBubbles\<name>.xml ----
        public const string DefaultTemplate = "Default";

        public static string TemplateDir
        {
            get { return Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "TapeBubbles"); }
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
        public static BubbleSettings LoadTemplate(string name)
        {
            try
            {
                string path = TemplatePath(name);
                if (!File.Exists(path))
                    return null;
                var ser = new XmlSerializer(typeof(BubbleSettings));
                using (var sr = new StringReader(File.ReadAllText(path)))
                    return (BubbleSettings)ser.Deserialize(sr);
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
    //  ENGINE: list of prints (thread safe through Sync)
    // =========================================================================
    public class BubbleEngine
    {
        public class Print
        {
            public long Seq;            // increasing id, stays the same when prints are merged into it
            public DateTime Time;
            public double PriceFrom;    // first price of the bubble
            public double Price;        // last price of the bubble
            public double Size;
            public int Side;            // 1 = buy, -1 = sell
            public int Count;           // prints merged
            public PrintPosition Pos;   // most aggressive position of the prints merged
        }

        public readonly object Sync = new object();

        public List<Print> Prints = new List<Print>();
        public int LastBarIndex = -1;          // last tick (bar index of the request) already processed
        public bool HistoryLoaded;
        public volatile int Version;           // changes on every new print: the view repaints only when needed

        public BubbleCombine Combine = BubbleCombine.Off;
        public int MaxPrints = 3000;
        private BubbleSettings filter = new BubbleSettings();   // which positions are kept

        private long nextSeq;
        private double lastPrice;
        private int lastSide = 1;

        // call inside lock
        public void Reset(BubbleSettings s)
        {
            Combine = s.CombinePrints;
            filter = s.Clone();
            MaxPrints = Math.Max(500, s.MaxPrints);
            Prints = new List<Print>();
            LastBarIndex = -1;
            HistoryLoaded = false;
            nextSeq = 0;
            lastPrice = 0;
            lastSide = 1;
            Version++;
        }

        // call inside lock
        public void Process(DateTime t, double price, double volume, double ask, double bid)
        {
            PrintPosition pos = PositionOf(price, ask, bid);
            int side = pos == PrintPosition.Between || pos == PrintPosition.Unknown
                ? TickRule(price)
                : ((int)pos > 0 ? 1 : -1);
            lastPrice = price;      // the tick rule follows every print, hidden ones too
            lastSide = side;
            if (volume <= 0 || !filter.Shows(pos))
                return;

            Print last = Prints.Count > 0 ? Prints[Prints.Count - 1] : null;

            // sweep: same timestamp, same aggressor, price moving only in the aggressor's direction
            if (Combine == BubbleCombine.SameTimeAndSide && last != null
                && last.Time == t && last.Side == side
                && (side > 0 ? price >= last.Price : price <= last.Price))
            {
                last.Size += volume;
                last.Price = price;
                last.Count++;
                if (Aggression(pos) > Aggression(last.Pos))
                    last.Pos = pos;
            }
            else
            {
                Prints.Add(new Print
                {
                    Seq = nextSeq++,
                    Time = t,
                    PriceFrom = price,
                    Price = price,
                    Size = volume,
                    Side = side,
                    Count = 1,
                    Pos = pos
                });

                // trim in blocks, not on every print
                if (Prints.Count > MaxPrints + MaxPrints / 10)
                    Prints.RemoveRange(0, Prints.Count - MaxPrints);
            }
            Version++;
        }

        // position vs the bid / ask at the time of the print
        private static PrintPosition PositionOf(double price, double ask, double bid)
        {
            if (!(ask > 0 && bid > 0 && ask > bid))
                return PrintPosition.Unknown;
            const double eps = 1e-9;
            if (price > ask + eps) return PrintPosition.AboveAsk;
            if (price >= ask - eps) return PrintPosition.AtAsk;
            if (price < bid - eps) return PrintPosition.BelowBid;
            if (price <= bid + eps) return PrintPosition.AtBid;
            return PrintPosition.Between;
        }

        // 2 = outside the spread, 1 = at the quote, 0 = between / unknown
        private static int Aggression(PrintPosition p)
        {
            switch (p)
            {
                case PrintPosition.AboveAsk:
                case PrintPosition.BelowBid: return 2;
                case PrintPosition.AtAsk:
                case PrintPosition.AtBid: return 1;
                default: return 0;
            }
        }

        // used for prints between bid and ask (or without bid / ask data)
        private int TickRule(double price)
        {
            return lastPrice == 0 ? lastSide
                : price > lastPrice ? 1
                : price < lastPrice ? -1
                : lastSide;
        }

        // call inside lock: last index with Seq <= seq (-1 if none)
        public int IndexAtOrBefore(long seq)
        {
            int lo = 0, hi = Prints.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Prints[mid].Seq <= seq) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }
    }

    // =========================================================================
    //  VIEW: draws the bubbles (UI thread)
    // =========================================================================
    public class BubbleTapeView : FrameworkElement
    {
        private struct Hit
        {
            public double X, Y, R;
            public DateTime Time;
            public double PriceFrom, Price, Size;
            public int Side, Count, Grade;
            public PrintPosition Pos;
        }

        public BubbleSettings Settings;
        public BubbleEngine Engine;
        public string Status;                       // shown in the middle when not null
        public Func<double, string> PriceFormatter; // set by the window from the instrument tick size
        public event EventHandler SettingsRequested;

        private double hoverX = -1, hoverY = -1;
        private long anchorSeq = -1;                // -1 = live, otherwise the newest print shown (paused)
        private int lastVersion = -1;
        private bool dirty = true;
        private Rect pausedRect = Rect.Empty;
        private readonly List<Hit> hits = new List<Hit>();

        // index 0-2 = buy grade 1-3, 3-5 = sell grade 1-3
        private readonly Brush[] fills = new Brush[6];
        private readonly Pen[] borders = new Pen[6];
        private readonly Brush[] labelBrushes = new Brush[6];
        private Brush aboveAskBrush, belowBidBrush, separatorTextBrush, aboveAskRowBrush, belowBidRowBrush;
        private Typeface markTypeface;
        private Color cText;
        private Brush bgBrush, textBrush, dimTextBrush, pausedBrush, boxBrush;
        private Pen framePen, hoverPen, separatorPen;
        private Typeface typeface;
        private double fontSize;

        public BubbleTapeView(BubbleSettings settings, BubbleEngine engine)
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
                Focus();
                if (e.ClickCount > 1)
                {
                    if (SettingsRequested != null)
                    {
                        e.Handled = true;
                        SettingsRequested(this, EventArgs.Empty);
                    }
                    return;
                }
                if (anchorSeq >= 0 && !pausedRect.IsEmpty && pausedRect.Contains(e.GetPosition(this)))
                {
                    e.Handled = true;
                    GoLive();
                }
            };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.End || e.Key == Key.Escape)
                {
                    e.Handled = true;
                    GoLive();
                }
            };
            MouseWheel += OnWheel;
        }

        public bool IsPaused { get { return anchorSeq >= 0; } }

        public void GoLive()
        {
            anchorSeq = -1;
            InvalidateVisual();
        }

        // called by the window timer: repaint only when something changed
        public void Tick()
        {
            int v = Engine.Version;
            if (v != lastVersion || dirty)
            {
                lastVersion = v;
                dirty = false;
                InvalidateVisual();
            }
        }

        public void BuildResources()
        {
            var s = Settings;
            FontFamily family;
            try { family = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamilyName) ? "Arial" : s.FontFamilyName); }
            catch { family = new FontFamily("Arial"); }
            typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            markTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            fontSize = Math.Max(6, s.FontSize);

            Color[] colors =
            {
                Hex(s.Buy1, Color.FromRgb(0x0E, 0x4C, 0x8C)),
                Hex(s.Buy2, Color.FromRgb(0x1B, 0x88, 0xF4)),
                Hex(s.Buy3, Color.FromRgb(0x6C, 0xC4, 0xFF)),
                Hex(s.Sell1, Color.FromRgb(0x7A, 0x1A, 0x0E)),
                Hex(s.Sell2, Color.FromRgb(0xF0, 0x21, 0x02)),
                Hex(s.Sell3, Color.FromRgb(0xFF, 0x6A, 0x4A))
            };
            double op = Math.Max(0.05, Math.Min(1, s.BubbleOpacity / 100.0));
            for (int i = 0; i < 6; i++)
            {
                Color c = colors[i];
                fills[i] = Solid(c, op);
                Color bc = !s.CustomBorder ? Lerp(c, Colors.White, 0.25)
                    : i < 3 ? Hex(s.BuyBorderColor, Colors.White)
                    : Hex(s.SellBorderColor, Colors.White);
                borders[i] = s.BorderWidth > 0 ? MakePen(bc, op, s.BorderWidth) : null;
                double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                labelBrushes[i] = lum > 0.6 ? Brushes.Black : Brushes.White;
            }
            aboveAskBrush = Solid(Hex(s.AboveAskMarkColor, colors[2]), 1);
            belowBidBrush = Solid(Hex(s.BelowBidMarkColor, colors[5]), 1);
            double rowOp = Math.Max(0.05, Math.Min(1, s.RowOpacity / 100.0));
            aboveAskRowBrush = Solid(Hex(s.AboveAskRowColor, Color.FromRgb(0x1B, 0x88, 0xF4)), rowOp);
            belowBidRowBrush = Solid(Hex(s.BelowBidRowColor, Color.FromRgb(0xB8, 0x2B, 0x2B)), rowOp);

            cText = Hex(s.TextColor, Colors.LightGray);
            bgBrush = Solid(Hex(s.BackgroundColor, Color.FromRgb(0x19, 0x1B, 0x22)), 1);
            boxBrush = bgBrush;
            textBrush = Solid(cText, 1);
            dimTextBrush = Solid(cText, 0.6);
            pausedBrush = Solid(Color.FromRgb(0xFF, 0xB3, 0x00), 1);

            framePen = MakePen(cText, 0.25, 1);
            hoverPen = MakePen(cText, 0.9, 1.5);

            separatorPen = new Pen(Solid(Hex(s.SeparatorColor, Colors.Gray), s.SeparatorOpacity / 100.0), Math.Max(1, s.SeparatorWidth))
            {
                DashStyle = ToWpfDash(s.SeparatorDashStyle)
            };
            separatorPen.Freeze();
            separatorTextBrush = Solid(Hex(s.SeparatorColor, Colors.Gray), s.SeparatorOpacity / 100.0);

            dirty = true;
            InvalidateVisual();
        }

        // Wheel = scroll in time (pauses the tape), Ctrl + wheel = bubble zoom
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var s = Settings;

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                double step = e.Delta > 0 ? 4 : -4;
                s.MaxDiameter = Math.Max(Math.Max(4, s.MinDiameter), Math.Min(400, s.MaxDiameter + step));
                InvalidateVisual();
                return;
            }

            int amount = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 20 : 3;
            // newest on top: wheel down = older (like scrolling down a Time & Sales)
            bool older = s.NewestOnTop ? e.Delta < 0 : e.Delta > 0;
            Scroll(older ? amount : -amount);
        }

        private bool Qualifies(BubbleEngine.Print p)
        {
            return p.Size >= Settings.FilterMinSize && p.Size > 0;
        }

        // call inside lock: index of the newest print to draw
        private int StartIndex(BubbleEngine eng)
        {
            int n = eng.Prints.Count;
            if (anchorSeq < 0 || n == 0)
                return n - 1;
            int idx = eng.IndexAtOrBefore(anchorSeq);
            return idx < 0 ? 0 : idx;
        }

        // steps > 0 = older, < 0 = newer (counted in visible bubbles)
        private void Scroll(int steps)
        {
            var eng = Engine;
            lock (eng.Sync)
            {
                var list = eng.Prints;
                int n = list.Count;
                if (n == 0) return;

                // start from the newest visible bubble
                int i = StartIndex(eng);
                while (i > 0 && !Qualifies(list[i])) i--;

                if (steps > 0)
                {
                    int moved = 0, j = i;
                    for (int k = i - 1; k >= 0 && moved < steps; k--)
                        if (Qualifies(list[k])) { j = k; moved++; }
                    if (moved > 0)
                        anchorSeq = list[j].Seq;
                }
                else
                {
                    int need = -steps, moved = 0, j = i;
                    for (int k = i + 1; k < n && moved < need; k++)
                        if (Qualifies(list[k])) { j = k; moved++; }

                    bool newerExists = false;
                    for (int k = j + 1; k < n; k++)
                        if (Qualifies(list[k])) { newerExists = true; break; }

                    anchorSeq = newerExists ? list[j].Seq : -1;
                }
            }
            InvalidateVisual();
        }

        private double Diameter(double size)
        {
            var s = Settings;
            double t = Math.Max(0, Math.Min(1, size / Math.Max(1e-9, s.SizeForMaxBubble)));
            if (s.Scale == BubbleScale.Area) t = Math.Sqrt(t);
            double minD = Math.Max(2, s.MinDiameter);
            double maxD = Math.Max(minD, s.MaxDiameter);
            return minD + (maxD - minD) * t;
        }

        protected override void OnRender(DrawingContext dc)
        {
            hits.Clear();
            pausedRect = Rect.Empty;

            double W = ActualWidth, H = ActualHeight;
            if (W <= 0 || H <= 0 || typeface == null) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var s = Settings;

            dc.DrawRectangle(bgBrush, null, new Rect(0, 0, W, H));

            const double pad = 6;
            double lineH = Text("0", textBrush, dpi).Height;
            double headerH = lineH + 4;

            string header = (string.IsNullOrWhiteSpace(s.ShortName) ? "T-B" : s.ShortName)
                + (s.FilterMinSize > 1 ? " ≥" + FormatSize(s.FilterMinSize) : "");
            dc.DrawText(Text(header, dimTextBrush, dpi), new Point(pad, pad));

            if (Status != null)
            {
                DrawCentered(dc, Status, W, H, dpi);
                return;
            }

            // paused indicator (click = live)
            if (anchorSeq >= 0)
            {
                var ft = Text("PAUSED ▶", pausedBrush, dpi);
                double px = Math.Max(pad, W - pad - ft.Width);
                dc.DrawText(ft, new Point(px, pad));
                pausedRect = new Rect(px - 4, 0, ft.Width + 8 + pad, pad + ft.Height + 4);
            }

            double plotLeft = pad, plotRight = W - pad;
            double plotTop = pad + headerH, plotBottom = H - pad;
            if (plotRight - plotLeft < 10 || plotBottom - plotTop < 10)
                return;

            var eng = Engine;
            lock (eng.Sync)
            {
                var list = eng.Prints;
                if (list.Count == 0)
                {
                    DrawCentered(dc, eng.HistoryLoaded ? "waiting for prints..." : "loading...", W, H, dpi);
                    return;
                }

                int start = StartIndex(eng);

                bool split = s.Layout == BubbleLayout.SplitSides;

                // room for the AA / BB marks, reserved on BOTH sides so the bubbles stay exactly centered
                bool useMarks = s.MarkOutside && s.HighlightStyle == OutsideHighlight.Marks;
                bool useRows = s.MarkOutside && s.HighlightStyle == OutsideHighlight.RowBackground;
                double markW = 0;
                if (useMarks)
                    markW = Math.Max(MarkText(s.AboveAskMark, aboveAskBrush, dpi).Width,
                                     MarkText(s.BelowBidMark, belowBidBrush, dpi).Width) + 4;

                double colLeft = plotLeft + markW;
                double colW = plotRight - markW - colLeft;
                if (colW < 6) return;

                double maxD = Math.Max(2, (split ? colW / 2 : colW) - 2);
                double gap = Math.Max(0, s.BubbleGap);
                bool top = s.NewestOnTop;
                double cursor = top ? plotTop : plotBottom;

                // time separators: a line between two bubbles that fall in different N-second blocks
                long sepTicks = s.ShowSeparators ? Math.Max(1, s.SeparatorSeconds) * TimeSpan.TicksPerSecond : 0;
                long prevKey = long.MinValue;

                dc.PushClip(new RectangleGeometry(new Rect(plotLeft, plotTop, plotRight - plotLeft, plotBottom - plotTop)));

                for (int i = start; i >= 0; i--)
                {
                    var p = list[i];
                    if (!Qualifies(p)) continue;
                    if (top ? cursor > plotBottom : cursor < plotTop) break;

                    // separator before this bubble? (drawn after the row background, so it stays visible)
                    bool drawSep = false;
                    double ly = 0;
                    DateTime lineTime = DateTime.MinValue;
                    if (sepTicks > 0)
                    {
                        long key = p.Time.Ticks / sepTicks;
                        if (prevKey != long.MinValue && key != prevKey)
                        {
                            drawSep = true;
                            // middle of the gap between the previous (newer) bubble and this one
                            ly = Math.Round(top ? cursor - gap / 2 : cursor + gap / 2) + 0.5;
                            // start of the newer block = the clock time where the line sits
                            lineTime = new DateTime(Math.Max(prevKey, key) * sepTicks);
                        }
                        prevKey = key;
                    }

                    double d = Math.Min(maxD, Diameter(p.Size));
                    double r = d / 2;
                    double cy;
                    if (top) { cy = cursor + r; cursor += d + gap; }
                    else { cy = cursor - r; cursor -= d + gap; }

                    double cx = split
                        ? (p.Side > 0 ? colLeft + colW * 0.75 : colLeft + colW * 0.25)
                        : colLeft + colW / 2;

                    bool outside = p.Pos == PrintPosition.AboveAsk || p.Pos == PrintPosition.BelowBid;

                    // row background across the whole width (bubble + half the gap on each side)
                    if (useRows && outside)
                        dc.DrawRectangle(p.Pos == PrintPosition.AboveAsk ? aboveAskRowBrush : belowBidRowBrush, null,
                            new Rect(plotLeft, cy - r - gap / 2, plotRight - plotLeft, d + gap));

                    if (drawSep)
                    {
                        dc.DrawLine(separatorPen, new Point(plotLeft, ly), new Point(plotRight, ly));
                        if (s.ShowSeparatorTime)
                        {
                            var ft = new FormattedText(lineTime.ToString("HH:mm:ss"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                typeface, Math.Max(5, Math.Min(12, s.SeparatorFontSize)), separatorTextBrush, dpi);
                            dc.DrawText(ft, new Point(plotLeft, ly - ft.Height));
                        }
                    }

                    int grade = s.GradeOf(p.Size);
                    int ci = (p.Side > 0 ? 0 : 3) + grade - 1;

                    dc.DrawEllipse(fills[ci], borders[ci], new Point(cx, cy), r, r);

                    if (s.ShowSizeLabel && !(s.HideSmallLabels && p.Size <= s.LabelMinSize))
                    {
                        var ft = Text(FormatSize(p.Size), labelBrushes[ci], dpi);
                        if (ft.Width + 2 <= d && ft.Height <= d)
                            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
                    }

                    if (useMarks && outside)
                    {
                        bool aa = p.Pos == PrintPosition.AboveAsk;
                        var ft = MarkText(aa ? s.AboveAskMark : s.BelowBidMark, aa ? aboveAskBrush : belowBidBrush, dpi);
                        // sells in split layout: mark on the left of the bubble, otherwise on the right
                        double mx = split && p.Side < 0 ? cx - r - 3 - ft.Width : cx + r + 3;
                        dc.DrawText(ft, new Point(mx, cy - ft.Height / 2));
                    }

                    hits.Add(new Hit
                    {
                        X = cx,
                        Y = cy,
                        R = r,
                        Time = p.Time,
                        PriceFrom = p.PriceFrom,
                        Price = p.Price,
                        Size = p.Size,
                        Side = p.Side,
                        Count = p.Count,
                        Grade = grade,
                        Pos = p.Pos
                    });
                }

                dc.Pop();
            }

            // ---- hover ----
            if (hoverX >= 0 && hoverY >= plotTop)
            {
                foreach (var h in hits)
                {
                    double dx = hoverX - h.X, dy = hoverY - h.Y;
                    double rr = Math.Max(h.R + 2, 5);
                    if (dx * dx + dy * dy <= rr * rr)
                    {
                        dc.DrawEllipse(null, hoverPen, new Point(h.X, h.Y), h.R + 2, h.R + 2);
                        DrawInfoBox(dc, h, W, H, dpi);
                        break;
                    }
                }
            }
        }

        private void DrawCentered(DrawingContext dc, string msg, double W, double H, double dpi)
        {
            var ft = Text(msg, dimTextBrush, dpi);
            dc.DrawText(ft, new Point(Math.Max(4, (W - ft.Width) / 2), (H - ft.Height) / 2));
        }

        private void DrawInfoBox(DrawingContext dc, Hit h, double W, double H, double dpi)
        {
            var s = Settings;
            var texts = new List<FormattedText>();
            int ci = (h.Side > 0 ? 0 : 3) + h.Grade - 1;

            if (s.TipTime)
                texts.Add(Text(h.Time.ToString("HH:mm:ss.fff"), dimTextBrush, dpi));
            if (s.TipPrice)
            {
                string price = PriceFormatter != null ? PriceFormatter(h.Price) : h.Price.ToString(CultureInfo.InvariantCulture);
                if (h.PriceFrom != h.Price)
                    price = (PriceFormatter != null ? PriceFormatter(h.PriceFrom) : h.PriceFrom.ToString(CultureInfo.InvariantCulture)) + " → " + price;
                texts.Add(Text("Price: " + price, textBrush, dpi));
            }
            if (s.TipSize)
                texts.Add(Text("Size:  " + FormatSize(h.Size) + (h.Count > 1 ? "  (" + h.Count + " prints)" : ""), textBrush, dpi));
            if (s.TipSide)
            {
                Color c = Hex(ci < 3 ? s.Buy2 : s.Sell2, Colors.White);
                texts.Add(Text((h.Side > 0 ? "Buy " : "Sell ") + h.Grade + "  " + PositionName(h.Pos), Solid(c, 1), dpi));
            }

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

        // ---- helpers ----
        private FormattedText Text(string str, Brush b, double dpi)
        {
            return new FormattedText(str, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, fontSize, b, dpi);
        }

        private FormattedText MarkText(string str, Brush b, double dpi)
        {
            return new FormattedText(str ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, markTypeface, fontSize, b, dpi);
        }

        private static string PositionName(PrintPosition p)
        {
            switch (p)
            {
                case PrintPosition.AboveAsk: return "above ask";
                case PrintPosition.AtAsk: return "at ask";
                case PrintPosition.Between: return "between";
                case PrintPosition.AtBid: return "at bid";
                case PrintPosition.BelowBid: return "below bid";
                default: return "no bid/ask";
            }
        }

        private static string FormatSize(double v)
        {
            if (v >= 10000)
                return (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return v.ToString(v == Math.Floor(v) ? "0" : "0.##", CultureInfo.InvariantCulture);
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

        // keeps the alpha of 'a'
        private static Color Lerp(Color a, Color b, double k)
        {
            return Color.FromArgb(
                a.A,
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
    //  COLOR PICKER (saturation / brightness square + hue bar + opacity + hex)
    // =========================================================================
    public class BubbleColorPicker : NTWindow
    {
        private const double SvW = 240, SvH = 170, HueW = 22;

        private double hue, sat, val;      // 0-360, 0-1, 0-1
        private byte alpha;
        private bool updating;
        private string result;

        private readonly Border svBase;                        // pure hue behind the white / black gradients
        private readonly Canvas svMarker, hueMarker;
        private readonly Slider opacity;
        private readonly TextBlock opacityText;
        private readonly Border preview;
        private readonly TextBox hexBox;

        // returns the picked color as #AARRGGBB, or null if cancelled
        public static string Pick(Window owner, string currentHex)
        {
            var dlg = new BubbleColorPicker(BubbleTapeView.Hex(currentHex, Colors.White)) { Owner = owner };
            dlg.ShowDialog();
            return dlg.result;
        }

        private BubbleColorPicker(Color start)
        {
            Caption = "Pick a Color";
            Width = SvW + HueW + 60;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(10) };

            // ---- saturation / brightness square ----
            var svHost = new Grid { Width = SvW, Height = SvH, Cursor = Cursors.Cross, ClipToBounds = true };
            svBase = new Border();
            var whiteLayer = new Border
            {
                Background = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(1, 0))
            };
            var blackLayer = new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1))
            };
            svMarker = new Canvas { IsHitTestVisible = false };
            svMarker.Children.Add(new System.Windows.Shapes.Ellipse { Width = 14, Height = 14, Stroke = Brushes.Black, StrokeThickness = 1 });
            var inner = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2 };
            Canvas.SetLeft(inner, 1);
            Canvas.SetTop(inner, 1);
            svMarker.Children.Add(inner);
            var svCanvas = new Canvas { IsHitTestVisible = false };
            svCanvas.Children.Add(svMarker);

            svHost.Children.Add(svBase);
            svHost.Children.Add(whiteLayer);
            svHost.Children.Add(blackLayer);
            svHost.Children.Add(svCanvas);
            AttachDrag(svHost, p =>
            {
                sat = Clamp01(p.X / SvW);
                val = 1 - Clamp01(p.Y / SvH);
                UpdateAll(false);
            });

            // ---- hue bar ----
            var hueBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            Color[] stops = { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red };
            for (int i = 0; i < stops.Length; i++)
                hueBrush.GradientStops.Add(new GradientStop(stops[i], i / 6.0));

            var hueHost = new Grid { Width = HueW, Height = SvH, Cursor = Cursors.Hand, Margin = new Thickness(10, 0, 0, 0) };
            hueHost.Children.Add(new Border { Background = hueBrush, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
            hueMarker = new Canvas { IsHitTestVisible = false };
            hueMarker.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = HueW + 6,
                Height = 5,
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            });
            var hueCanvas = new Canvas { IsHitTestVisible = false };
            hueCanvas.Children.Add(hueMarker);
            hueHost.Children.Add(hueCanvas);
            AttachDrag(hueHost, p =>
            {
                hue = Clamp01(p.Y / SvH) * 360;
                if (hue >= 360) hue = 359.999;
                UpdateAll(false);
            });

            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(svHost);
            top.Children.Add(hueHost);
            root.Children.Add(top);

            // ---- preview strip ----
            preview = new Border
            {
                Width = SvW,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1)
            };
            root.Children.Add(preview);

            // ---- opacity ----
            var opRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var opLabel = new TextBlock { Text = "Opacity", Width = 60, VerticalAlignment = VerticalAlignment.Center };
            opacityText = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            opacity = new Slider { Minimum = 0, Maximum = 255, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            opacity.ValueChanged += (s, e) =>
            {
                if (updating) return;
                alpha = (byte)opacity.Value;
                UpdateAll(false);
            };
            DockPanel.SetDock(opLabel, Dock.Left);
            DockPanel.SetDock(opacityText, Dock.Right);
            opRow.Children.Add(opLabel);
            opRow.Children.Add(opacityText);
            opRow.Children.Add(opacity);
            root.Children.Add(opRow);

            // ---- hex ----
            var hexRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var hexLabel = new TextBlock { Text = "Hex", Width = 60, VerticalAlignment = VerticalAlignment.Center };
            hexBox = new TextBox { VerticalContentAlignment = VerticalAlignment.Center };
            hexBox.TextChanged += (s, e) =>
            {
                if (updating) return;
                try { SetFromColor((Color)ColorConverter.ConvertFromString(hexBox.Text.Trim()), true); }
                catch { }   // ignore while the user is still typing
            };
            DockPanel.SetDock(hexLabel, Dock.Left);
            hexRow.Children.Add(hexLabel);
            hexRow.Children.Add(hexBox);
            root.Children.Add(hexRow);

            // ---- buttons ----
            var ok = new Button { Content = "OK", MinWidth = 70, Margin = new Thickness(4), IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 70, Margin = new Thickness(4), IsCancel = true };
            ok.Click += (s, e) => { result = ToHex(CurrentColor()); Close(); };
            cancel.Click += (s, e) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
            SetFromColor(start, false);
        }

        // click + drag inside an element; 'pick' gets the mouse position
        private static void AttachDrag(Grid host, Action<Point> pick)
        {
            host.Background = Brushes.Transparent;   // make the whole area clickable
            host.MouseLeftButtonDown += (s, e) =>
            {
                host.CaptureMouse();
                pick(e.GetPosition(host));
                e.Handled = true;
            };
            host.MouseMove += (s, e) =>
            {
                if (host.IsMouseCaptured)
                    pick(e.GetPosition(host));
            };
            host.MouseLeftButtonUp += (s, e) =>
            {
                if (host.IsMouseCaptured)
                    host.ReleaseMouseCapture();
            };
        }

        private void SetFromColor(Color c, bool fromHex)
        {
            double h, s, v;
            ToHsv(c, out h, out s, out v);
            if (h >= 0) hue = h;   // grey / white / black: keep the current hue
            sat = s;
            val = v;
            alpha = c.A;
            UpdateAll(fromHex);
        }

        private Color CurrentColor()
        {
            return FromHsv(alpha, hue, sat, val);
        }

        private void UpdateAll(bool fromHex)
        {
            updating = true;
            Color c = CurrentColor();

            svBase.Background = new SolidColorBrush(FromHsv(255, hue, 1, 1));
            Canvas.SetLeft(svMarker, sat * SvW - 7);
            Canvas.SetTop(svMarker, (1 - val) * SvH - 7);
            Canvas.SetLeft(hueMarker, -3);
            Canvas.SetTop(hueMarker, hue / 360 * SvH - 2.5);

            preview.Background = new SolidColorBrush(c);
            opacity.Value = alpha;
            opacityText.Text = Math.Round(alpha / 255.0 * 100) + "%";
            if (!fromHex)
                hexBox.Text = ToHex(c);

            updating = false;
        }

        // ---- color math ----
        private static double Clamp01(double x)
        {
            return x < 0 ? 0 : x > 1 ? 1 : x;
        }

        private static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        }

        private static Color FromHsv(byte a, double h, double s, double v)
        {
            double c = v * s;
            double hp = (h % 360) / 60;
            double x = c * (1 - Math.Abs(hp % 2 - 1));
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            double m = v - c;
            return Color.FromArgb(a,
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        // h = -1 when the color has no hue (grey, white, black)
        private static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            v = max;
            s = max <= 0 ? 0 : d / max;

            if (d == 0) h = -1;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0 && d != 0) h += 360;
        }
    }

    // =========================================================================
    //  SETTINGS DIALOG (built from the [Display] attributes of BubbleSettings)
    // =========================================================================
    public class BubbleSettingsWindow : NTWindow
    {
        private BubbleSettings edit;
        private readonly Action<BubbleSettings> apply;
        private readonly List<Func<string>> commits = new List<Func<string>>();   // each returns an error or null
        private readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        private static readonly string[] GroupOrder = { "Data", "Prints", "Grades", "Colors", "Bubbles", "Panel", "Time Separators", "Tooltip" };

        public BubbleSettingsWindow(BubbleSettings current, Action<BubbleSettings> apply)
        {
            Caption = "Tape Bubbles - Settings";
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

            var props = typeof(BubbleSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
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
                MessageBox.Show(string.Join("\n", errors), "Tape Bubbles", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            var saveDefault = new MenuItem { Header = "Save as default", ToolTip = "New Tape Bubbles windows start with these settings." };
            saveDefault.Click += (s, e) => SaveTemplate(BubbleSettings.DefaultTemplate, true);
            menu.Items.Add(saveDefault);

            menu.Items.Add(new Separator());

            List<string> names = BubbleSettings.ListTemplates();

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

            if (!isDefault && BubbleSettings.TemplateExists(name)
                && MessageBox.Show("Template \"" + name + "\" already exists. Overwrite it?", "Tape Bubbles",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                edit.SaveTemplate(name);
                MessageBox.Show(isDefault
                        ? "Saved as default. New Tape Bubbles windows will start with these settings."
                        : "Template \"" + name + "\" saved.",
                    "Tape Bubbles", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the template:\n" + ex.Message, "Tape Bubbles", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // loads into the dialog only: OK / Apply sends it to the window
        private void LoadTemplate(string name)
        {
            BubbleSettings t = BubbleSettings.LoadTemplate(name);
            if (t == null)
            {
                MessageBox.Show("Could not read template \"" + name + "\".", "Tape Bubbles", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            edit = t;
            BuildEditors();
        }

        private void DeleteTemplate(string name)
        {
            if (MessageBox.Show("Delete template \"" + name + "\"?", "Tape Bubbles",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                BubbleSettings.DeleteTemplate(name);
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

            if (t == typeof(string) && p.GetCustomAttribute<BubbleColorHexAttribute>() != null)
            {
                var panel = new DockPanel();
                var swatch = new Border
                {
                    Width = 20,
                    Height = 18,
                    Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(1),
                    BorderBrush = Brushes.Gray,
                    Cursor = Cursors.Hand,
                    ToolTip = "Click to pick a color"
                };
                var tb = new TextBox { Text = (string)p.GetValue(edit) };
                Action refresh = () =>
                {
                    try { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tb.Text.Trim())); }
                    catch { swatch.Background = null; }
                };
                tb.TextChanged += (s, e) => refresh();
                refresh();

                // click on the swatch: color picker
                swatch.MouseLeftButtonUp += (s, e) =>
                {
                    string picked = BubbleColorPicker.Pick(this, tb.Text);
                    if (picked != null)
                        tb.Text = picked;
                };

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
    public class TapeBubblesWindow : Window, IWorkspacePersistence //public class TapeBubblesWindow : NTWindow, IWorkspacePersistence
    {
        // new windows start from the "Default" template when one was saved (workspace restore overrides it)
        private BubbleSettings settings = BubbleSettings.LoadTemplate(BubbleSettings.DefaultTemplate) ?? new BubbleSettings();
        private readonly BubbleEngine engine = new BubbleEngine();
        private readonly BubbleTapeView view;
        private readonly InstrumentSelector instrumentSelector;
        private readonly DispatcherTimer timer;

        private Instrument instrument;
        private BarsRequest request;                                   // history
        private MarketData marketData;                                 // realtime
        private EventHandler<MarketDataEventArgs> mdHandler;
        private double liveAsk, liveBid;                               // last quote received (inside engine.Sync)

        private struct LiveTick
        {
            public DateTime Time;
            public double Price, Volume, Ask, Bid;
        }
        private readonly List<LiveTick> pending = new List<LiveTick>();
        private int generation;   // increases on every reload: old requests are ignored

        public TapeBubblesWindow()
        {
            Title = "Tape Bubbles"; //Caption = "Tape Bubbles";
            Width = 180;
            Height = 800;

            // ---- toolbar: instrument selector + settings button ----
            instrumentSelector = new InstrumentSelector
            {
                LastUsedGroup = "TapeBubbles",
                MinWidth = 100,
                MinHeight = 0,     // the style may set a minimum height: remove it
                Height = 24,       // try 20-24
                Margin = new Thickness(2, 4, 2, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            instrumentSelector.InstrumentChanged += OnInstrumentChanged;

            var settingsButton = new Button
            {
                Content = "⚙",
                ToolTip = "Settings",
                MinWidth = 0,
                MinHeight = 0,
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 8, 4)
            };
            settingsButton.Click += (s, e) => OpenSettings();

            var toolbar = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(instrumentSelector, Dock.Left);
            DockPanel.SetDock(settingsButton, Dock.Right);
            toolbar.Children.Add(instrumentSelector);
            toolbar.Children.Add(settingsButton);

            // ---- tape ----
            view = new BubbleTapeView(settings, engine) { Status = "Select an instrument" };
            view.SettingsRequested += (s, e) => OpenSettings();

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // row 0: tape, fills the space
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                        // row 1: toolbar, as tall as needed
            Grid.SetRow(view, 0);
            Grid.SetRow(toolbar, 1);
            grid.Children.Add(view);
            grid.Children.Add(toolbar);
            Content = grid;
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));   // #141414 // system window only ////////////////////////////////////////////
            Topmost = settings.AlwaysOnTop;

            // repaints only when new prints arrived (or something changed)
            timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (s, e) => view.Tick();
            timer.Start();

            // WorkspaceOptions property must be set
            Loaded += (o, e) =>
            {
                if (WorkspaceOptions == null)
                    WorkspaceOptions = new WorkspaceOptions("TapeBubbles-" + Guid.NewGuid().ToString("N"), this);
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
            view.GoLive();

            if (inst == null)
            {
                Title = "Tape Bubbles"; //Caption = "Tape Bubbles";
                view.Status = "Select an instrument";
                view.PriceFormatter = null;
                return;
            }

            //Caption = "Tape Bubbles - " + inst.FullName;
            Title = "Tape Bubbles"; //Caption = "Tape Bubbles";
            view.PriceFormatter = MakePriceFormatter(inst);
            view.Status = "Loading " + inst.FullName + "...";

            lock (engine.Sync)
            {
                pending.Clear();
                liveAsk = liveBid = 0;
            }

            // realtime: Last / Ask / Bid stream, the same one the NT Time & Sales uses.
            // Subscribed before the history request; prints arriving while history loads wait in 'pending'.
            var md = new MarketData(inst);
            mdHandler = (s, e) => OnMarketData(e, gen);
            md.Update += mdHandler;
            marketData = md;

            // history only: 1-tick bars (ask / bid stored with each tick)
            var req = new BarsRequest(inst, DateTime.Now.AddMinutes(-Math.Max(1, settings.MinutesToLoad)), DateTime.Now)
            {
                BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 },
                TradingHours = inst.MasterInstrument.TradingHours
            };
            request = req;

            req.Request((r, errorCode, errorMessage) =>
            {
                if (gen != generation)
                    return;

                if (errorCode != ErrorCode.NoError)
                {
                    // no history: keep going with realtime only
                    lock (engine.Sync)
                        FlushPending(DateTime.MinValue);
                    Dispatcher.InvokeAsync(() => { if (gen == generation) view.Status = null; });
                    return;
                }

                ProcessRange(r, 0, r.Bars.Count - 1, gen);

                lock (engine.Sync)
                {
                    if (gen != generation)
                        return;
                    DateTime lastHist = r.Bars.Count > 0 ? r.Bars.GetTime(r.Bars.Count - 1) : DateTime.MinValue;
                    FlushPending(lastHist);
                }

                Dispatcher.InvokeAsync(() => { if (gen == generation) view.Status = null; });
            });
        }

        // call inside lock: realtime prints received while the history was loading (those already in the history are skipped)
        private void FlushPending(DateTime lastHistTime)
        {
            foreach (var t in pending)
                if (t.Time > lastHistTime)
                    engine.Process(t.Time, t.Price, t.Volume, t.Ask, t.Bid);
            pending.Clear();
            engine.HistoryLoaded = true;
            engine.Version++;
        }

        private void OnMarketData(MarketDataEventArgs e, int gen)
        {
            lock (engine.Sync)
            {
                if (gen != generation)
                    return;

                if (e.MarketDataType == MarketDataType.Ask)
                    liveAsk = e.Price;
                else if (e.MarketDataType == MarketDataType.Bid)
                    liveBid = e.Price;
                else if (e.MarketDataType == MarketDataType.Last)
                {
                    // compare with the quote on screen when the print arrived
                    double ask = liveAsk > 0 ? liveAsk : e.Ask;
                    double bid = liveBid > 0 ? liveBid : e.Bid;
                    if (!engine.HistoryLoaded)
                        pending.Add(new LiveTick { Time = e.Time, Price = e.Price, Volume = e.Volume, Ask = ask, Bid = bid });
                    else
                        engine.Process(e.Time, e.Price, e.Volume, ask, bid);
                }
            }
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
            var md = marketData;
            marketData = null;
            if (md != null)
            {
                if (mdHandler != null)
                    md.Update -= mdHandler;
                var disp = md as IDisposable;
                if (disp != null)
                    disp.Dispose();
            }
            mdHandler = null;

            var req = request;
            request = null;
            if (req != null)
                req.Dispose();
        }

        // price text with the decimals of the tick size (e.g. ES 0.25 -> 2 decimals)
        private static Func<double, string> MakePriceFormatter(Instrument inst)
        {
            double tick = inst.MasterInstrument.TickSize;
            int decimals = 0;
            if (tick > 0)
            {
                double x = tick;
                while (decimals < 8 && Math.Abs(x - Math.Round(x)) > 1e-9)
                {
                    x *= 10;
                    decimals++;
                }
            }
            string fmt = "F" + decimals;
            return p => p.ToString(fmt, CultureInfo.InvariantCulture);
        }

        // ---- settings ----
        private void OpenSettings()
        {
            var dlg = new BubbleSettingsWindow(settings, ApplySettings) { Owner = this };
            dlg.ShowDialog();
        }

        private void ApplySettings(BubbleSettings ns)
        {
            bool reload = settings.NeedsReload(ns);
            settings = ns;
            view.Settings = ns;
            view.BuildResources();
            Topmost = ns.AlwaysOnTop;
            lock (engine.Sync)
                engine.MaxPrints = Math.Max(500, ns.MaxPrints);
            if (reload && instrument != null)
                LoadInstrument(instrument);
            view.InvalidateVisual();
        }

        // ---- workspace ----
        // IWorkspacePersistence member. Required for restoring window from workspace
        public void Restore(XDocument document, XElement element)
        {
            var xs = element.Element("TapeBubblesSettings");
            if (xs != null)
            {
                settings = BubbleSettings.FromXml(xs.Value);
                view.Settings = settings;
                view.BuildResources();
                Topmost = settings.AlwaysOnTop;
            }

            var xi = element.Element("TapeBubblesInstrument");
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
            element.Add(new XElement("TapeBubblesSettings", settings.ToXml()));
            element.Add(new XElement("TapeBubblesInstrument", instrument != null ? instrument.FullName : string.Empty));
        }

        // IWorkspacePersistence member
        public WorkspaceOptions WorkspaceOptions { get; set; }
    }

    // =========================================================================
    //  ADD-ON: menu item in Control Center > New
    // =========================================================================
    public class TapeBubblesAddOn : AddOnBase
    {
        private NTMenuItem menuItem;
        private NTMenuItem existingMenuItem;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TapeBubbles";
                Description = "Tape Bubbles: Time & Sales as color-graded bubbles, in its own window.";
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
                Header = "Tape Bubbles",
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
            NinjaTrader.Core.Globals.RandomDispatcher.BeginInvoke(new Action(() => new TapeBubblesWindow().Show()));
        }
    }
}