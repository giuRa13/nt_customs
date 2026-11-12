#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Windows.Threading;
using System.Xml.Serialization;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Indicators.giuli13;

// WPF namespaces aliased to avoid name clashes with NinjaTrader types
using SW = System.Windows;
using SWC = System.Windows.Controls;
using SWD = System.Windows.Documents;
using SWI = System.Windows.Input;
using SWM = System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
#endregion

// Speed of Tape (Instant) - floating gauge box on the price chart.
// Each bar covers a fixed number of seconds ("Bar Seconds"), independent of the chart timeframe.
//   Body = speed at the end of the bar (activity in the last "Number Seconds"); the newest bar decays live
//   Wick = peak speed reached during the bar
//   SD+1 / SD+2 lines = mean + 1 / 2 std dev of the previous "Std Dev Period" bars
// Mouse: drag to move, drag the bottom-right corner to resize, double-click for settings,
//        wheel = zoom (more / fewer bars), Shift + wheel = scroll back in time, hover = bar details.
// Uses the SpeedOfTapeInput / SpeedOfTapeColorBy enums defined in SpeedOfTape.cs (same namespace).

namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
    public class SpeedOfTapeInstant : Indicator
    {
        private class Bucket
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

        private struct View
        {
            public long Key;
            public bool Exists, Marked;
            public double Speed, Peak, Buy, Sell, Open, Close;
        }

        // Element that draws the whole gauge in WPF
        private class GaugeElement : SW.FrameworkElement
        {
            public Action<SWM.DrawingContext> Draw;
            protected override void OnRender(SWM.DrawingContext dc)
            {
                if (Draw != null) Draw(dc);
            }
        }

        // Layer placed over the whole chart (all panels): hosts a transparent grid that contains the gauge
        private class GaugeAdorner : SWD.Adorner
        {
            private readonly SW.FrameworkElement child;

            public GaugeAdorner(SW.UIElement adorned, SW.FrameworkElement child) : base(adorned)
            {
                this.child = child;
                AddVisualChild(child);
            }

            protected override int VisualChildrenCount { get { return 1; } }

            protected override SWM.Visual GetVisualChild(int index) { return child; }

            protected override SW.Size MeasureOverride(SW.Size constraint)
            {
                child.Measure(AdornedElement.RenderSize);
                return AdornedElement.RenderSize;
            }

            protected override SW.Size ArrangeOverride(SW.Size finalSize)
            {
                child.Arrange(new SW.Rect(AdornedElement.RenderSize));
                return finalSize;
            }
        }

        // ---- data (instrument thread writes, UI thread reads -> lock) ----
        private readonly object sync = new object();
        private List<Bucket> buckets;
        private Dictionary<long, int> bucketIndex;
        private Queue<TapeEvent> window;
        private double windowSum;
        private double lastPrice;
        private int lastSide = 1;
        private DateTime lastTradeTime = DateTime.MinValue;
        private DateTime lastTickTime = DateTime.MinValue;
        private DateTime calcStart = DateTime.MinValue;

        // ---- UI thread only ----
        private GaugeElement gauge;
        private SW.FrameworkElement host;
        private SWD.AdornerLayer adornerLayer;
        private GaugeAdorner adorner;
        private ChartControl chartRef;
        private SWC.Panel addedToPanel;      // set when the container was added straight to the panels grid
        private SWC.Grid addedContainer;
        private DispatcherTimer timer;
        private bool dragging, resizing;
        private SW.Point dragStart;
        private double dragLeft, dragTop, startW, startH;
        private double hoverX = -1, hoverY = -1;
        private int scrollOffset;   // bars scrolled back into history (0 = live)

        private SWM.Color cBullFill, cBullBorder, cBearFill, cBearBorder, cNeutral, cText, cSd;
        private SWM.Brush bgBrush, textBrush, dimTextBrush, sdTextBrush;
        private SWM.Pen framePen, sdPen, gridPen, hoverPen, gripPen, tickPen, separatorPen, borderPen;
        private SWM.Typeface typeface;
        private double fontSize;

        private const double GripSize = 14;
        private const double MinW = 160, MinH = 100;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SpeedOfTapeInstant";
                Description = "Speed of Tape (Instant): floating gauge with the speed of the last bars, each covering a fixed number of seconds." +
                    "\r\n- Body: speed at the end of the bar (activity in the last N seconds). The newest bar updates live." +
                    "\r\n- Wick: the fastest moment reached inside the bar." +
                    "\r\n- SD+1 / SD+2: how fast is unusual compared with the previous bars." +
                    "\r\n- Drag to move, drag the bottom-right corner to resize, double-click for settings." +
                    "\r\n- Mouse wheel: more / fewer bars. Shift + wheel: scroll back in time. Hover a bar to read its time and values.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                IsChartOnly = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;   // the rolling window must see every tick

                // Parameters
                InputData = SpeedOfTapeInput.Volume;
                NumberSeconds = 10;
                BarSeconds = 30;
                BarsToShow = 10;
                FilterMin = 0;
                FilterMax = 0;
                StdDevFilter = 1.0;
                StdDevPeriod = 50;
                ColorBy = SpeedOfTapeColorBy.Delta;
                DaysToCalculate = 2;

                // Bars
                /*BullBorder = MakeBrush("#FF1B88F4");
                BullFill = MakeBrush("#FF1B88F4");
                BearBorder = MakeBrush("#FFD83C3C");
                BearFill = MakeBrush("#FFD83C3C");*/
                BullBorder = MakeBrush("#FF1B88F4");
                BullFill = MakeBrush("#FF1B88F4");
                BearBorder = MakeBrush("#FFFF3755");
                BearFill = MakeBrush("#FFFF3755");
                UnmarkedOpacity = 100;
                UseGradient = true;
                GradientNeutral = WpfBrushes.White;
                GradientFullAt = 20;
                LineWidth = 1;

                // Panel
                ShowSdLines = true;
                SdLineBrush = MakeBrush("#FF2EA043");
                BackgroundBrush = WpfBrushes.Black;
                BackgroundOpacity = 85;
                TextBrush = MakeBrush("#FFB0B0B0");
                TextFont = new SimpleFont("Arial", 11);
                ShortName = "S-T";
                MoveOverAllPanels = true;
                ShowValueAxis = true;
                PanelBorderBrush = MakeBrush("#FFB0B0B0");
                PanelBorderWidth = 1;
                PanelBorderOpacity = 25;
                RoundedCorners = true;

                // Time separators
                ShowSeparators = false;
                SeparatorSeconds = 60;
                SeparatorBrush = MakeBrush("#FF808080");
                SeparatorDashStyle = DashStyleHelper.Dash;
                SeparatorWidth = 1;
                SeparatorOpacity = 50;

                // Tooltip lines
                TipTime = true;
                TipEnd = true;
                TipPeak = true;
                TipDelta = true;
                TipBuySell = true;

                // default position / size: top right, 16 px from the edges
                AnchorRight = true;
                AnchorBottom = false;
                OffsetX = 16;
                OffsetY = 16;
                PanelWidth = 360;
                PanelHeight = 220;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                buckets = new List<Bucket>();
                bucketIndex = new Dictionary<long, int>();
                window = new Queue<TapeEvent>();
                windowSum = 0;

                // Only the last N trading sessions (chart trading hours) are calculated
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
            else if (State == State.Historical)
            {
                if (ChartControl != null && IsVisible && gauge == null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        if (State >= State.Terminated || gauge != null)
                            return;

                        AttachControl(CreateControl());

                        timer = new DispatcherTimer(DispatcherPriority.Render, ChartControl.Dispatcher)
                        {
                            Interval = TimeSpan.FromMilliseconds(250)
                        };
                        timer.Tick += OnTimerTick;
                        timer.Start();
                    });
                }
            }
            else if (State == State.Terminated)
            {
                var t = timer;
                var layer = adornerLayer;
                var ad = adorner;
                var cc = chartRef;
                var parentPanel = addedToPanel;
                var cont = addedContainer;
                timer = null;
                adornerLayer = null;
                adorner = null;
                chartRef = null;
                addedToPanel = null;
                addedContainer = null;

                SW.Threading.Dispatcher d = t != null ? t.Dispatcher
                    : ad != null ? ad.Dispatcher
                    : cont != null ? cont.Dispatcher : null;
                if (d != null)
                {
                    d.InvokeAsync(() =>
                    {
                        if (t != null)
                        {
                            t.Stop();
                            t.Tick -= OnTimerTick;
                        }
                        if (host != null)
                            host.SizeChanged -= OnHostSizeChanged;

                        // the over-all-panels layer is ours, NinjaTrader does not remove it
                        if (layer != null && ad != null)
                            layer.Remove(ad);
                        if (parentPanel != null && cont != null)
                            parentPanel.Children.Remove(cont);
                        if (cc != null)
                            cc.IsVisibleChanged -= OnChartVisibleChanged;
                    });
                }
            }
        }

        public override string DisplayName
        {
            get { return "Speed of Tape Instant (" + NumberSeconds + "s / " + BarSeconds + "s)"; }
        }

        // Keeps the dragged position, size and zoom when the settings dialog applies changes
        public override void CopyTo(NinjaScript ninjaScript)
        {
            if (ninjaScript is SpeedOfTapeInstant other)
            {
                other.OffsetX = OffsetX;
                other.OffsetY = OffsetY;
                other.AnchorRight = AnchorRight;
                other.AnchorBottom = AnchorBottom;
                other.PanelWidth = PanelWidth;
                other.PanelHeight = PanelHeight;
                other.BarsToShow = BarsToShow;
            }
            base.CopyTo(ninjaScript);
        }

        // =====================================================================
        //  CALCULATION (instrument thread)
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
                ProcessTick();
        }

        private void ProcessTick()
        {
            if (CurrentBars[1] < 0)
                return;

            DateTime t = Times[1][0];
            if (t <= calcStart)
                return;   // older than "Days To Calculate"

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

            lock (sync)
            {
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

                lastTickTime = t;
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

        private long KeyOf(DateTime t)
        {
            return t.Ticks / (BarSeconds * TimeSpan.TicksPerSecond);
        }

        private DateTime KeyStart(long key)
        {
            return new DateTime(key * BarSeconds * TimeSpan.TicksPerSecond);
        }

        // call inside lock
        private Bucket GetBucket(long key)
        {
            int idx;
            if (bucketIndex.TryGetValue(key, out idx))
                return buckets[idx];
            var b = new Bucket { Key = key };
            bucketIndex[key] = buckets.Count;
            buckets.Add(b);
            return b;
        }

        // call inside lock: first index with Key >= key
        private int LowerBound(long key)
        {
            int lo = 0, hi = buckets.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (buckets[mid].Key < key) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // call inside lock: mean / std dev of the speed of the StdDevPeriod bars before 'key'
        private bool Stats(long key, out double mean, out double std)
        {
            mean = std = 0;
            int end = LowerBound(key);
            int start = Math.Max(0, end - StdDevPeriod);
            int n = end - start;
            if (n < 2) return false;

            double sum = 0, sq = 0;
            for (int i = start; i < end; i++)
            {
                double x = buckets[i].Speed;
                sum += x;
                sq += x * x;
            }
            mean = sum / n;
            std = Math.Sqrt(Math.Max(0, sq / n - mean * mean));
            return true;
        }

        // call inside lock
        private bool IsMarked(long key, double v)
        {
            if (v <= 0 || v < FilterMin) return false;
            if (FilterMax > 0 && v > FilterMax) return false;
            if (StdDevFilter > 0)
            {
                double mean, std;
                if (Stats(key, out mean, out std) && v < mean + StdDevFilter * std)
                    return false;
            }
            return true;
        }

        // =====================================================================
        //  PANEL (UI thread)
        // =====================================================================
        private SW.FrameworkElement CreateControl()
        {
            BuildResources();

            gauge = new GaugeElement
            {
                Width = Math.Max(MinW, PanelWidth),
                Height = Math.Max(MinH, PanelHeight),
                Cursor = SWI.Cursors.SizeAll,
                ClipToBounds = true,
                Draw = RenderGauge
            };

            gauge.MouseDown += OnMouseDown;
            gauge.MouseMove += OnMouseMove;
            gauge.MouseUp += (s, e) => { if (dragging || resizing) { EndDrag(); e.Handled = true; } };
            gauge.LostMouseCapture += (s, e) => { if (dragging || resizing) EndDrag(); };
            gauge.MouseLeave += (s, e) => { hoverX = -1; gauge.InvalidateVisual(); };
            // handledEventsToo: the wheel reaches the gauge even if the chart already looked at it
            gauge.AddHandler(SW.UIElement.PreviewMouseWheelEvent, new SWI.MouseWheelEventHandler(OnMouseWheel), true);

            gauge.Loaded += (s, e) =>
            {
                EnsureHost();
                ClampOffsets();
            };

            ApplyPosition();
            return gauge;
        }

        private void BuildResources()
        {
            var font = TextFont ?? new SimpleFont("Arial", 11);
            typeface = new SWM.Typeface(font.Family,
                font.Italic ? SW.FontStyles.Italic : SW.FontStyles.Normal,
                font.Bold ? SW.FontWeights.Bold : SW.FontWeights.Normal,
                SW.FontStretches.Normal);
            fontSize = font.Size;

            cBullFill = ToColor(BullFill, SWM.Colors.DodgerBlue);
            cBullBorder = ToColor(BullBorder, SWM.Colors.DodgerBlue);
            cBearFill = ToColor(BearFill, SWM.Colors.Red);
            cBearBorder = ToColor(BearBorder, SWM.Colors.Red);
            cNeutral = ToColor(GradientNeutral, SWM.Colors.White);
            cText = ToColor(TextBrush, SWM.Colors.LightGray);
            cSd = ToColor(SdLineBrush, SWM.Colors.SeaGreen);

            bgBrush = Solid(ToColor(BackgroundBrush, SWM.Colors.Black), BackgroundOpacity / 100.0);
            textBrush = Solid(cText, 1);
            dimTextBrush = Solid(cText, 0.6);
            sdTextBrush = Solid(cSd, 1);

            framePen = MakePen(cText, 0.25, 1);
            gridPen = MakePen(cText, 0.12, 1);
            hoverPen = MakePen(cText, 0.35, 1);
            gripPen = MakePen(cText, 0.5, 1);
            tickPen = MakePen(cText, 0.4, 1);

            borderPen = PanelBorderWidth > 0
                ? MakePen(ToColor(PanelBorderBrush, cText), PanelBorderOpacity / 100.0, PanelBorderWidth)
                : null;   // width 0 = no border

            sdPen = new SWM.Pen(Solid(cSd, 1), 1) { DashStyle = SWM.DashStyles.Dash };
            sdPen.Freeze();

            separatorPen = new SWM.Pen(Solid(ToColor(SeparatorBrush, SWM.Colors.Gray), SeparatorOpacity / 100.0), Math.Max(1, SeparatorWidth))
            {
                DashStyle = ToWpfDash(SeparatorDashStyle)
            };
            separatorPen.Freeze();
        }

        // Over all panels: the gauge goes in a transparent grid on a layer that covers the whole chart.
        // Otherwise (or if that layer is not available) it stays in the price panel like before.
        private void AttachControl(SW.FrameworkElement element)
        {
            if (MoveOverAllPanels && ChartControl != null)
            {
                SW.UIElement area = FindAllPanelsArea();
                if (area != null)
                {
                    // null background: clicks outside the gauge reach the chart
                    var container = new SWC.Grid { Background = null, ClipToBounds = true };
                    container.Children.Add(element);

                    // 1st choice: a layer over the area that holds all the panels
                    adornerLayer = SWD.AdornerLayer.GetAdornerLayer(area);
                    if (adornerLayer != null)
                    {
                        adorner = new GaugeAdorner(area, container);
                        adornerLayer.Add(adorner);
                        Print("SpeedOfTapeInstant: box over all panels (layer on " + area.GetType().Name + ")");
                    }
                    // 2nd choice: add the container directly to the grid that holds the panels
                    else if (area is SWC.Panel)
                    {
                        addedToPanel = (SWC.Panel)area;
                        SWC.Grid.SetRowSpan(container, 1000);
                        SWC.Grid.SetColumnSpan(container, 1000);
                        SWC.Panel.SetZIndex(container, 10000);
                        addedToPanel.Children.Add(container);
                        Print("SpeedOfTapeInstant: box over all panels (added to " + area.GetType().Name + ")");
                    }

                    if (adorner != null || addedToPanel != null)
                    {
                        addedContainer = container;
                        chartRef = ChartControl;
                        chartRef.IsVisibleChanged += OnChartVisibleChanged;
                        OnChartVisibleChanged(null, default(SW.DependencyPropertyChangedEventArgs));

                        host = container;
                        host.SizeChanged += OnHostSizeChanged;
                        return;
                    }
                    container.Children.Remove(element);
                }
                Print("SpeedOfTapeInstant: over-all-panels mode not available, box stays in the price panel");
            }
            UserControlCollection.Add(element);
        }

        // The smallest element that contains every chart panel (price panel + bottom panels)
        private SW.UIElement FindAllPanelsArea()
        {
            var panels = ChartControl.ChartPanels;
            if (panels == null || panels.Count == 0)
                return ChartControl;

            SW.DependencyObject first = panels[0];

            // walk up from the first panel until an element contains all the others
            SW.DependencyObject node = SWM.VisualTreeHelper.GetParent(first);
            while (node != null)
            {
                bool containsAll = true;
                for (int i = 1; i < panels.Count; i++)
                {
                    var v = node as SWM.Visual;
                    if (v == null || !v.IsAncestorOf(panels[i])) { containsAll = false; break; }
                }

                // with a single panel, take the first Panel (grid) above it: future bottom panels go in there too
                if (containsAll && (panels.Count > 1 || node is SWC.Panel) && node is SW.UIElement)
                    return (SW.UIElement)node;

                if (node == ChartControl)
                    return ChartControl;
                node = SWM.VisualTreeHelper.GetParent(node);
            }
            return ChartControl;
        }

        // hide the layer when the chart is not visible (other tab, minimized ...)
        private void OnChartVisibleChanged(object sender, SW.DependencyPropertyChangedEventArgs e)
        {
            if (chartRef == null) return;
            var vis = chartRef.IsVisible ? SW.Visibility.Visible : SW.Visibility.Collapsed;
            if (adorner != null) adorner.Visibility = vis;
            if (addedContainer != null) addedContainer.Visibility = vis;
        }

        // The element the panel is placed in (visual parent, falls back to the chart panel)
        private bool EnsureHost()
        {
            if (host != null) return true;
            if (gauge == null) return false;

            host = SWM.VisualTreeHelper.GetParent(gauge) as SW.FrameworkElement ?? ChartPanel;
            if (host != null)
                host.SizeChanged += OnHostSizeChanged;
            return host != null;
        }

        private void ApplyPosition()
        {
            if (gauge == null) return;

            gauge.HorizontalAlignment = AnchorRight ? SW.HorizontalAlignment.Right : SW.HorizontalAlignment.Left;
            gauge.VerticalAlignment = AnchorBottom ? SW.VerticalAlignment.Bottom : SW.VerticalAlignment.Top;
            gauge.Margin = new SW.Thickness(
                AnchorRight ? 0 : OffsetX,
                AnchorBottom ? 0 : OffsetY,
                AnchorRight ? OffsetX : 0,
                AnchorBottom ? OffsetY : 0);
        }

        // Keeps the panel fully visible when the chart gets smaller
        private void ClampOffsets()
        {
            if (gauge == null || host == null || host.ActualWidth <= 0 || host.ActualHeight <= 0)
                return;

            OffsetX = Math.Max(0, Math.Min(OffsetX, host.ActualWidth - gauge.Width));
            OffsetY = Math.Max(0, Math.Min(OffsetY, host.ActualHeight - gauge.Height));
            ApplyPosition();
        }

        private void OnHostSizeChanged(object sender, SW.SizeChangedEventArgs e)
        {
            if (!dragging && !resizing)
                ClampOffsets();
        }

        private bool InGrip(SW.Point p)
        {
            return p.X >= gauge.ActualWidth - GripSize && p.Y >= gauge.ActualHeight - GripSize;
        }

        private void OnMouseDown(object sender, SWI.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != SWI.MouseButton.Left || !EnsureHost()) return;
            e.Handled = true;   // the chart must not react to clicks on the panel

            if (e.ClickCount > 1)
            {
                ChartControl?.OnIndicatorsHotKey(this, null);
                return;
            }

            SW.Point local = e.GetPosition(gauge);

            // during drag / resize the panel is placed from the top-left corner
            SW.Point p = gauge.TranslatePoint(new SW.Point(0, 0), host);
            dragLeft = p.X;
            dragTop = p.Y;
            dragStart = e.GetPosition(host);

            gauge.HorizontalAlignment = SW.HorizontalAlignment.Left;
            gauge.VerticalAlignment = SW.VerticalAlignment.Top;
            gauge.Margin = new SW.Thickness(dragLeft, dragTop, 0, 0);

            if (InGrip(local))
            {
                resizing = true;
                startW = gauge.Width;
                startH = gauge.Height;
            }
            else
                dragging = true;

            gauge.CaptureMouse();
        }

        private void OnMouseMove(object sender, SWI.MouseEventArgs e)
        {
            if (dragging && host != null)
            {
                SW.Point p = e.GetPosition(host);
                double left = Math.Max(0, Math.Min(dragLeft + (p.X - dragStart.X), host.ActualWidth - gauge.Width));
                double top = Math.Max(0, Math.Min(dragTop + (p.Y - dragStart.Y), host.ActualHeight - gauge.Height));
                gauge.Margin = new SW.Thickness(left, top, 0, 0);
            }
            else if (resizing && host != null)
            {
                SW.Point p = e.GetPosition(host);
                double w = Math.Max(MinW, Math.Min(startW + (p.X - dragStart.X), host.ActualWidth - dragLeft));
                double h = Math.Max(MinH, Math.Min(startH + (p.Y - dragStart.Y), host.ActualHeight - dragTop));
                gauge.Width = w;
                gauge.Height = h;
                PanelWidth = w;
                PanelHeight = h;
            }
            else
            {
                SW.Point local = e.GetPosition(gauge);
                gauge.Cursor = InGrip(local) ? SWI.Cursors.SizeNWSE : SWI.Cursors.SizeAll;
                hoverX = local.X;
                hoverY = local.Y;
                gauge.InvalidateVisual();
            }
        }

        // Saves the position relative to the closest edges
        private void EndDrag()
        {
            dragging = false;
            resizing = false;
            if (gauge.IsMouseCaptured)
                gauge.ReleaseMouseCapture();
            if (host == null) return;

            double left = gauge.Margin.Left;
            double top = gauge.Margin.Top;
            double w = gauge.Width;
            double h = gauge.Height;

            AnchorRight = left + w / 2 > host.ActualWidth / 2;
            AnchorBottom = top + h / 2 > host.ActualHeight / 2;
            OffsetX = AnchorRight ? host.ActualWidth - left - w : left;
            OffsetY = AnchorBottom ? host.ActualHeight - top - h : top;

            ClampOffsets();
        }

        // Wheel = zoom (more / fewer bars), Shift + wheel = scroll back in time
        private void OnMouseWheel(object sender, SWI.MouseWheelEventArgs e)
        {
            e.Handled = true;

            if ((SWI.Keyboard.Modifiers & SWI.ModifierKeys.Shift) != 0)
            {
                int step = Math.Max(1, BarsToShow / 4);
                scrollOffset = Math.Max(0, scrollOffset + (e.Delta > 0 ? step : -step));
            }
            else
            {
                int n = BarsToShow;
                n = e.Delta > 0
                    ? Math.Max(3, Math.Min(n - 1, (int)Math.Round(n / 1.2)))
                    : Math.Min(500, Math.Max(n + 1, (int)Math.Round(n * 1.2)));
                BarsToShow = n;
            }
            gauge.InvalidateVisual();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (gauge != null && !dragging && !resizing)
                gauge.InvalidateVisual();
        }

        // =====================================================================
        //  DRAWING (UI thread)
        // =====================================================================
        private void RenderGauge(SWM.DrawingContext dc)
        {
            double W = gauge.ActualWidth, H = gauge.ActualHeight;
            if (W <= 0 || H <= 0 || typeface == null) return;
            double dpi = SWM.VisualTreeHelper.GetDpi(gauge).PixelsPerDip;

            // panel background + border (inset by half the border width so it is not clipped)
            double radius = RoundedCorners ? 4 : 0;
            double half = borderPen != null ? borderPen.Thickness / 2 : 0;
            dc.DrawRoundedRectangle(bgBrush, borderPen,
                new SW.Rect(half, half, Math.Max(0, W - 2 * half), Math.Max(0, H - 2 * half)), radius, radius);

            const double pad = 6;
            double lineH = Text("0", textBrush, dpi).Height;
            double headerH = lineH + 4, footerH = lineH + 4;

            string header = (string.IsNullOrWhiteSpace(ShortName) ? "S-T" : ShortName) + "(" + NumberSeconds + ") - "
                + BarSeconds + "s - " + (InputData == SpeedOfTapeInput.Volume ? "V" : InputData == SpeedOfTapeInput.Orders ? "O" : "T");
            dc.DrawText(Text(header, dimTextBrush, dpi), new SW.Point(pad, pad));

            // ---- snapshot of the data ----
            int n = Math.Max(1, BarsToShow);
            var views = new View[n];
            bool hasSd = false;
            double sdMean = 0, sdStd = 0;

            lock (sync)
            {
                if (buckets == null || lastTickTime == DateTime.MinValue)
                {
                    var wait = Text("waiting for data...", dimTextBrush, dpi);
                    dc.DrawText(wait, new SW.Point((W - wait.Width) / 2, (H - wait.Height) / 2));
                    DrawGrip(dc, W, H);
                    return;
                }

                DateTime now = Connection.PlaybackConnection != null ? Connection.PlaybackConnection.Now : NinjaTrader.Core.Globals.Now;
                bool live = State == State.Realtime && now >= lastTickTime && (now - lastTickTime).TotalMinutes < 5;
                DateTime anchorTime = live ? now : lastTickTime;   // market closed / historical: show the last data
                long anchorKey = KeyOf(anchorTime);

                long firstKey = buckets.Count > 0 ? buckets[0].Key : anchorKey;
                int maxOffset = (int)Math.Max(0, Math.Min(int.MaxValue, anchorKey - firstKey - n + 1));
                if (scrollOffset > maxOffset) scrollOffset = maxOffset;
                long endKey = anchorKey - scrollOffset;

                for (int i = 0; i < n; i++)
                {
                    long key = endKey - (n - 1) + i;
                    views[i].Key = key;
                    int idx;
                    if (!bucketIndex.TryGetValue(key, out idx))
                        continue;

                    Bucket b = buckets[idx];
                    views[i].Exists = true;
                    views[i].Speed = b.Speed;
                    views[i].Peak = b.Peak;
                    views[i].Buy = b.Buy;
                    views[i].Sell = b.Sell;
                    views[i].Open = b.Open;
                    views[i].Close = b.Close;

                    // newest bar while live: the speed decays when nothing trades
                    if (live && key == anchorKey)
                    {
                        DateTime cutoff = now.AddSeconds(-NumberSeconds);
                        double sum = 0;
                        foreach (var ev in window)
                            if (ev.Time > cutoff) sum += ev.Amount;
                        views[i].Speed = sum;
                    }

                    views[i].Marked = IsMarked(key, views[i].Speed);
                }

                hasSd = ShowSdLines && Stats(endKey, out sdMean, out sdStd);
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

            double axisW = ShowValueAxis
                ? Math.Max(Text(FormatValue(Math.Floor(maxV / step) * step), textBrush, dpi).Width, Text("0", textBrush, dpi).Width) + 8
                : 0;

            double plotLeft = pad;
            double plotRight = W - pad - axisW;
            double plotTop = pad + headerH;
            double plotBottom = H - pad - footerH;
            if (plotRight - plotLeft < 20 || plotBottom - plotTop < 20)
            {
                DrawGrip(dc, W, H);
                return;
            }
            double plotH = plotBottom - plotTop;
            Func<double, double> Y = v => plotBottom - v / maxV * plotH;

            // grid + value labels
            int guard = 0;
            for (double v = 0; v <= maxV && guard++ < 20; v += step)
            {
                double y = Math.Round(Y(v)) + 0.5;
                if (ShowValueAxis)
                {
                    dc.DrawLine(gridPen, new SW.Point(plotLeft, y), new SW.Point(plotRight, y));
                    var ft = Text(FormatValue(v), dimTextBrush, dpi);
                    dc.DrawText(ft, new SW.Point(plotRight + 4, y - ft.Height / 2));
                }
            }

            // time separators (vertical grid), behind the bars
            if (ShowSeparators && separatorPen != null && SeparatorSeconds > 0)
            {
                double slotW = (plotRight - plotLeft) / n;
                long firstSec = views[0].Key * BarSeconds;          // start of the first visible bar (seconds)
                long endSec = firstSec + (long)n * BarSeconds;
                long sep = SeparatorSeconds;
                long s0 = ((firstSec + sep - 1) / sep) * sep;       // first boundary inside the view
                if ((endSec - s0) / sep < 1000)                     // skip if far too many lines
                {
                    for (long s = s0; s < endSec; s += sep)
                    {
                        double x = Math.Round(plotLeft + (s - firstSec) / (double)BarSeconds * slotW) + 0.5;
                        dc.DrawLine(separatorPen, new SW.Point(x, plotTop), new SW.Point(x, plotBottom));
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
                    dc.DrawLine(sdPen, new SW.Point(plotLeft, y), new SW.Point(plotRight, y));
                    var ft = Text("SD+" + k, sdTextBrush, dpi);
                    dc.DrawText(ft, new SW.Point(plotLeft + 2, y - ft.Height));
                }
            }

            // ---- bars ----
            double slot = (plotRight - plotLeft) / n;
            double bodyW = Math.Max(1, slot * 0.72);
            double unmarked = UnmarkedOpacity / 100.0;

            for (int i = 0; i < n; i++)
            {
                View v = views[i];
                if (!v.Exists || (v.Peak <= 0 && v.Speed <= 0)) continue;

                bool bull = IsBull(v);
                double op = v.Marked ? 1.0 : unmarked;
                if (op <= 0) continue;

                SWM.Color fillC, borderC;
                if (!v.Marked && UseGradient)
                {
                    double total = v.Buy + v.Sell;
                    double k = total > 0 ? Math.Min(1.0, Math.Abs(v.Buy - v.Sell) / total / (GradientFullAt / 100.0)) : 0;
                    fillC = Lerp(cNeutral, bull ? cBullFill : cBearFill, k);
                    borderC = Lerp(cNeutral, bull ? cBullBorder : cBearBorder, k);
                }
                else
                {
                    fillC = bull ? cBullFill : cBearFill;
                    borderC = bull ? cBullBorder : cBearBorder;
                }

                var fill = Solid(fillC, op);
                var pen = MakePen(borderC, op, LineWidth);

                double x = plotLeft + slot * (i + 0.5);
                double yS = Y(v.Speed);
                double yP = Y(v.Peak);

                if (v.Peak > v.Speed)
                    dc.DrawLine(pen, new SW.Point(x, yS), new SW.Point(x, yP));

                if (plotBottom - yS >= 0.5)
                    dc.DrawRectangle(fill, pen, new SW.Rect(x - bodyW / 2, yS, bodyW, plotBottom - yS));
            }

            // ---- time axis ----
            DrawTimeAxis(dc, views, n, plotLeft, plotRight, plotBottom, slot, W, dpi);

            // ---- history hint (top right) ----
            if (scrollOffset > 0)
            {
                var ft = Text("\u25C0 history", dimTextBrush, dpi);
                dc.DrawText(ft, new SW.Point(Math.Max(pad, W - pad - ft.Width), pad));
            }

            DrawGrip(dc, W, H);

            // ---- hover: info box next to the mouse, only over a bar with trades ----
            if (hoverX >= plotLeft && hoverX < plotRight && hoverY >= plotTop && hoverY <= plotBottom && !dragging && !resizing)
            {
                int i = Math.Min(n - 1, (int)((hoverX - plotLeft) / slot));
                View v = views[i];
                if (v.Exists && (v.Peak > 0 || v.Speed > 0))
                {
                    double x = Math.Round(plotLeft + slot * (i + 0.5)) + 0.5;
                    dc.DrawLine(hoverPen, new SW.Point(x, plotTop), new SW.Point(x, plotBottom));
                    DrawInfoBox(dc, v, W, H, dpi);
                }
            }
        }

        // Small box near the mouse with the details of the hovered bar
        private void DrawInfoBox(SWM.DrawingContext dc, View v, double W, double H, double dpi)
        {
            DateTime start = KeyStart(v.Key);
            var texts = new List<SWM.FormattedText>();

            if (TipTime)
                texts.Add(Text(start.ToString("HH:mm:ss") + " - " + start.AddSeconds(BarSeconds).ToString("HH:mm:ss"), dimTextBrush, dpi));
            if (TipEnd)
                texts.Add(Text("End:   " + FormatValue(v.Speed), textBrush, dpi));
            if (TipPeak)
                texts.Add(Text("Peak:  " + FormatValue(v.Peak), textBrush, dpi));
            if (TipDelta)
            {
                //texts.Add(Text("Delta: " + (v.Buy - v.Sell).ToString("+0;-0;0", CultureInfo.InvariantCulture), textBrush, dpi));
                double delta = v.Buy - v.Sell;
                SWM.Brush deltaBrush = delta > 0 ? Solid(cBullFill, 1) : delta < 0 ? Solid(cBearFill, 1) : textBrush;
                texts.Add(Text("Delta: " + delta.ToString("+0;-0;0", CultureInfo.InvariantCulture), deltaBrush, dpi));
            }
            if (TipBuySell)
                texts.Add(Text("Buy " + FormatValue(v.Buy) + " / Sell " + FormatValue(v.Sell), textBrush, dpi));

            if (texts.Count == 0)
                return;   // every line switched off: no box

            double tw = 0, th = 0;
            foreach (var ft in texts)
            {
                tw = Math.Max(tw, ft.Width);
                th += ft.Height;
            }

            const double bp = 6;   // inner padding
            double bw = tw + bp * 2, bh = th + bp * 2;

            // right of the mouse; flips to the left if it does not fit; always inside the panel
            double bx = hoverX + 14;
            if (bx + bw > W - 2) bx = hoverX - 14 - bw;
            bx = Math.Max(2, Math.Min(bx, W - bw - 2));
            double by = Math.Max(2, Math.Min(hoverY - bh / 2, H - bh - 2));

            var boxRect = new SW.Rect(bx + 0.5, by + 0.5, bw, bh);
            double r = RoundedCorners ? 3 : 0;
            dc.DrawRoundedRectangle(bgBrush, null, boxRect, r, r);
            dc.DrawRoundedRectangle(bgBrush, framePen, boxRect, r, r);   // second pass = more opaque, plus the frame

            double ty = by + bp;
            foreach (var ft in texts)
            {
                dc.DrawText(ft, new SW.Point(bx + bp, ty));
                ty += ft.Height;
            }
        }

        private void DrawTimeAxis(SWM.DrawingContext dc, View[] views, int n, double plotLeft, double plotRight,
            double plotBottom, double slot, double W, double dpi)
        {
            dc.DrawLine(tickPen, new SW.Point(plotLeft, plotBottom + 0.5), new SW.Point(plotRight, plotBottom + 0.5));

            // label every 'every' bars, on round clock times (5s, 10s, 15s, 30s, 1m, 2m, 5m ...)
            double labelW = Text("00:00:00", textBrush, dpi).Width + 10;
            int need = Math.Max(1, (int)Math.Ceiling(labelW / slot));
            int every = need;
            int[] nice = { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400 };
            foreach (int c in nice)
            {
                if (c % BarSeconds == 0 && c / BarSeconds >= need)
                {
                    every = c / BarSeconds;
                    break;
                }
            }
            string fmt = (every * BarSeconds) % 60 == 0 ? "HH:mm" : "HH:mm:ss";

            for (int i = 0; i < n; i++)
            {
                if (views[i].Key % every != 0) continue;

                // the label marks the start of the bar
                double x = Math.Round(plotLeft + slot * i) + 0.5;
                dc.DrawLine(tickPen, new SW.Point(x, plotBottom), new SW.Point(x, plotBottom + 3));
                var ft = Text(KeyStart(views[i].Key).ToString(fmt), dimTextBrush, dpi);
                double tx = Math.Max(2, Math.Min(W - ft.Width - 2, x - ft.Width / 2));
                dc.DrawText(ft, new SW.Point(tx, plotBottom + 3));
            }
        }

        private void DrawGrip(SWM.DrawingContext dc, double W, double H)
        {
            double gx = W - 3, gy = H - 3;
            for (int k = 1; k <= 3; k++)
            {
                double d = k * 4;
                dc.DrawLine(gripPen, new SW.Point(gx - d, gy), new SW.Point(gx, gy - d));
            }
        }

        private bool IsBull(View v)
        {
            if (ColorBy == SpeedOfTapeColorBy.Delta)
            {
                double d = v.Buy - v.Sell;
                if (d != 0) return d > 0;
            }
            return v.Close >= v.Open;
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================
        private SWM.FormattedText Text(string s, SWM.Brush b, double dpi)
        {
            return new SWM.FormattedText(s, CultureInfo.CurrentUICulture, SW.FlowDirection.LeftToRight,
                typeface, fontSize, b, dpi);
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

        private static SWM.DashStyle ToWpfDash(DashStyleHelper s)
        {
            switch (s)
            {
                case DashStyleHelper.Dash: return SWM.DashStyles.Dash;
                case DashStyleHelper.DashDot: return SWM.DashStyles.DashDot;
                case DashStyleHelper.DashDotDot: return SWM.DashStyles.DashDotDot;
                case DashStyleHelper.Dot: return SWM.DashStyles.Dot;
                default: return SWM.DashStyles.Solid;
            }
        }

        private static SWM.Color ToColor(WpfBrush b, SWM.Color fallback)
        {
            var sc = b as SWM.SolidColorBrush;
            return sc != null ? sc.Color : fallback;
        }

        private static SWM.Color Lerp(SWM.Color a, SWM.Color b, double k)
        {
            return SWM.Color.FromArgb(
                (byte)Math.Round(a.A + (b.A - a.A) * k),
                (byte)Math.Round(a.R + (b.R - a.R) * k),
                (byte)Math.Round(a.G + (b.G - a.G) * k),
                (byte)Math.Round(a.B + (b.B - a.B) * k));
        }

        private static SWM.SolidColorBrush Solid(SWM.Color c, double opacity)
        {
            var b = new SWM.SolidColorBrush(c) { Opacity = opacity };
            b.Freeze();
            return b;
        }

        private static SWM.Pen MakePen(SWM.Color c, double opacity, double width)
        {
            var p = new SWM.Pen(Solid(c, opacity), width);
            p.Freeze();
            return p;
        }

        private static WpfBrush MakeBrush(string hex)
        {
            var b = new SWM.SolidColorBrush((SWM.Color)SWM.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        #region Properties

        // ===== Parameters =====
        [NinjaScriptProperty]
        [Display(Name = "Input Data", Order = 0, GroupName = "Parameters",
            Description = "Volume = contracts traded. Orders = aggressive orders (prints with same timestamp and side merged). Trades = number of prints.")]
        public SpeedOfTapeInput InputData { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Number Seconds", Order = 1, GroupName = "Parameters",
            Description = "Time span the speed is measured over (rolling window, seconds).")]
        public int NumberSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 3600)]
        [Display(Name = "Bar Seconds", Order = 2, GroupName = "Parameters",
            Description = "How many seconds each bar of the gauge covers (independent of the chart timeframe). Bars are aligned to the clock.")]
        public int BarSeconds { get; set; }

        [Range(3, 500)]
        [Display(Name = "Bars To Show", Order = 3, GroupName = "Parameters",
            Description = "Number of bars in the gauge. Also changed with the mouse wheel over the panel.")]
        public int BarsToShow { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Filter Min", Order = 4, GroupName = "Parameters",
            Description = "Only bars with speed at or above this value are highlighted.")]
        public double FilterMin { get; set; }

        [Range(0, double.MaxValue)]
        [Display(Name = "Filter Max", Order = 5, GroupName = "Parameters",
            Description = "Only bars with speed at or below this value are highlighted. 0 = no maximum.")]
        public double FilterMax { get; set; }

        [Range(0, 10)]
        [Display(Name = "Std Dev Per Filter", Order = 6, GroupName = "Parameters",
            Description = "A bar is highlighted only if its speed >= mean + N x std dev of the previous bars. 1 = above the SD+1 line. 0 = off.")]
        public double StdDevFilter { get; set; }

        [Range(2, 1000)]
        [Display(Name = "Std Dev Period", Order = 7, GroupName = "Parameters",
            Description = "Number of previous bars used for the mean / std dev (and the SD lines).")]
        public int StdDevPeriod { get; set; }

        [Display(Name = "Color By", Order = 8, GroupName = "Parameters",
            Description = "Delta = aggressive buys vs sells in the bar. PriceBar = price direction inside the bar.")]
        public SpeedOfTapeColorBy ColorBy { get; set; }

        [Range(0, 365)]
        [Display(Name = "Days To Calculate", Order = 9, GroupName = "Parameters",
            Description = "Only the last N trading sessions are calculated (sessions of the chart's trading hours). 0 = all loaded days.")]
        public int DaysToCalculate { get; set; }

        // ===== Bars =====
        [XmlIgnore]
        [Display(Name = "Bull Border", Order = 0, GroupName = "Bars")]
        public WpfBrush BullBorder { get; set; }

        [Browsable(false)]
        public string BullBorderSerialize
        {
            get { return Serialize.BrushToString(BullBorder); }
            set { BullBorder = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bull Fill", Order = 1, GroupName = "Bars")]
        public WpfBrush BullFill { get; set; }

        [Browsable(false)]
        public string BullFillSerialize
        {
            get { return Serialize.BrushToString(BullFill); }
            set { BullFill = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bear Border", Order = 2, GroupName = "Bars")]
        public WpfBrush BearBorder { get; set; }

        [Browsable(false)]
        public string BearBorderSerialize
        {
            get { return Serialize.BrushToString(BearBorder); }
            set { BearBorder = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Bear Fill", Order = 3, GroupName = "Bars")]
        public WpfBrush BearFill { get; set; }

        [Browsable(false)]
        public string BearFillSerialize
        {
            get { return Serialize.BrushToString(BearFill); }
            set { BearFill = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [Display(Name = "Unmarked Opacity (%)", Order = 4, GroupName = "Bars",
            Description = "Opacity of the bars that are not highlighted. 0 (with Std Dev Per Filter = 0) shows only the bars between Filter Min and Filter Max.")]
        public int UnmarkedOpacity { get; set; }

        [Display(Name = "Gradient On Faded Bars", Order = 5, GroupName = "Bars",
            Description = "On: not-highlighted bars blend from the neutral color (buyers = sellers) to the bull / bear color (strong imbalance).")]
        public bool UseGradient { get; set; }

        [XmlIgnore]
        [Display(Name = "Gradient Neutral Color", Order = 6, GroupName = "Bars")]
        public WpfBrush GradientNeutral { get; set; }

        [Browsable(false)]
        public string GradientNeutralSerialize
        {
            get { return Serialize.BrushToString(GradientNeutral); }
            set { GradientNeutral = Serialize.StringToBrush(value); }
        }

        [Range(1, 100)]
        [Display(Name = "Gradient Full Color At (%)", Order = 7, GroupName = "Bars",
            Description = "Imbalance at which a faded bar reaches the full color. Imbalance = |buy - sell| / (buy + sell). Lower = more colorful.")]
        public int GradientFullAt { get; set; }

        [Range(1, 10)]
        [Display(Name = "Line Width", Order = 8, GroupName = "Bars")]
        public int LineWidth { get; set; }

        // ===== Panel =====
        [Display(Name = "Show SD Lines", Order = 0, GroupName = "Panel")]
        public bool ShowSdLines { get; set; }

        [XmlIgnore]
        [Display(Name = "SD Line Color", Order = 1, GroupName = "Panel")]
        public WpfBrush SdLineBrush { get; set; }

        [Browsable(false)]
        public string SdLineBrushSerialize
        {
            get { return Serialize.BrushToString(SdLineBrush); }
            set { SdLineBrush = Serialize.StringToBrush(value); }
        }

        [XmlIgnore]
        [Display(Name = "Background Color", Order = 2, GroupName = "Panel")]
        public WpfBrush BackgroundBrush { get; set; }

        [Browsable(false)]
        public string BackgroundBrushSerialize
        {
            get { return Serialize.BrushToString(BackgroundBrush); }
            set { BackgroundBrush = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [Display(Name = "Background Opacity (%)", Order = 3, GroupName = "Panel")]
        public int BackgroundOpacity { get; set; }

        [XmlIgnore]
        [Display(Name = "Text Color", Order = 4, GroupName = "Panel")]
        public WpfBrush TextBrush { get; set; }

        [Browsable(false)]
        public string TextBrushSerialize
        {
            get { return Serialize.BrushToString(TextBrush); }
            set { TextBrush = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Font", Order = 5, GroupName = "Panel")]
        public SimpleFont TextFont { get; set; }

        [Display(Name = "Short Name", Order = 6, GroupName = "Panel")]
        public string ShortName { get; set; }

        [Display(Name = "Move Over All Panels", Order = 7, GroupName = "Panel",
            Description = "On: the box can be dragged anywhere on the chart, over the bottom panels too. Off: it stays inside the price panel.")]
        public bool MoveOverAllPanels { get; set; }

        [Display(Name = "Show Value Axis", Order = 8, GroupName = "Panel",
            Description = "Show the value labels on the right side of the box and the horizontal grid lines.")]
        public bool ShowValueAxis { get; set; }

        [XmlIgnore]
        [Display(Name = "Border Color", Order = 9, GroupName = "Panel")]
        public WpfBrush PanelBorderBrush { get; set; }

        [Browsable(false)]
        public string PanelBorderBrushSerialize
        {
            get { return Serialize.BrushToString(PanelBorderBrush); }
            set { PanelBorderBrush = Serialize.StringToBrush(value); }
        }

        [Range(0, 10)]
        [Display(Name = "Border Width", Order = 10, GroupName = "Panel",
            Description = "Thickness of the panel border in pixels. 0 = no border.")]
        public int PanelBorderWidth { get; set; }

        [Range(0, 100)]
        [Display(Name = "Border Opacity (%)", Order = 11, GroupName = "Panel")]
        public int PanelBorderOpacity { get; set; }

        [Display(Name = "Rounded Corners", Order = 12, GroupName = "Panel",
            Description = "On: rounded corners. Off: square corners.")]
        public bool RoundedCorners { get; set; }

        // ===== Time separators (vertical grid lines) =====
        [Display(Name = "Show Separators", Order = 0, GroupName = "Time Separators",
            Description = "Vertical lines every N seconds, e.g. to see where each candle of your main chart starts.")]
        public bool ShowSeparators { get; set; }

        [Range(1, 86400)]
        [Display(Name = "Interval (seconds)", Order = 1, GroupName = "Time Separators",
            Description = "Distance between the lines in seconds: 30 = 30 sec, 60 = 1 min, 300 = 5 min, 900 = 15 min. Lines sit on round clock times.")]
        public int SeparatorSeconds { get; set; }

        [XmlIgnore]
        [Display(Name = "Color", Order = 2, GroupName = "Time Separators")]
        public WpfBrush SeparatorBrush { get; set; }

        [Browsable(false)]
        public string SeparatorBrushSerialize
        {
            get { return Serialize.BrushToString(SeparatorBrush); }
            set { SeparatorBrush = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Dash Style", Order = 3, GroupName = "Time Separators")]
        public DashStyleHelper SeparatorDashStyle { get; set; }

        [Range(1, 10)]
        [Display(Name = "Width", Order = 4, GroupName = "Time Separators")]
        public int SeparatorWidth { get; set; }

        [Range(0, 100)]
        [Display(Name = "Opacity (%)", Order = 5, GroupName = "Time Separators")]
        public int SeparatorOpacity { get; set; }

        // ===== Tooltip (info box shown when the mouse is over a bar) =====
        [Display(Name = "Show Time", Order = 0, GroupName = "Tooltip",
            Description = "Start - end time of the bar, e.g. 15:40:30 - 15:41:00.")]
        public bool TipTime { get; set; }

        [Display(Name = "Show End", Order = 1, GroupName = "Tooltip",
            Description = "Speed at the end of the bar (top of the body).")]
        public bool TipEnd { get; set; }

        [Display(Name = "Show Peak", Order = 2, GroupName = "Tooltip",
            Description = "Highest speed reached inside the bar (top of the wick).")]
        public bool TipPeak { get; set; }

        [Display(Name = "Show Delta", Order = 3, GroupName = "Tooltip",
            Description = "Aggressive buys minus aggressive sells in the bar.")]
        public bool TipDelta { get; set; }

        [Display(Name = "Show Buy / Sell", Order = 4, GroupName = "Tooltip",
            Description = "Aggressive buys and aggressive sells in the bar.")]
        public bool TipBuySell { get; set; }

        // ===== Position / size (set with the mouse, saved with the workspace) =====
        [Browsable(false)]
        public double OffsetX { get; set; }

        [Browsable(false)]
        public double OffsetY { get; set; }

        [Browsable(false)]
        public bool AnchorRight { get; set; }

        [Browsable(false)]
        public bool AnchorBottom { get; set; }

        [Browsable(false)]
        public double PanelWidth { get; set; }

        [Browsable(false)]
        public double PanelHeight { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private giuli13.SpeedOfTapeInstant[] cacheSpeedOfTapeInstant;
		public giuli13.SpeedOfTapeInstant SpeedOfTapeInstant(SpeedOfTapeInput inputData, int numberSeconds, int barSeconds)
		{
			return SpeedOfTapeInstant(Input, inputData, numberSeconds, barSeconds);
		}

		public giuli13.SpeedOfTapeInstant SpeedOfTapeInstant(ISeries<double> input, SpeedOfTapeInput inputData, int numberSeconds, int barSeconds)
		{
			if (cacheSpeedOfTapeInstant != null)
				for (int idx = 0; idx < cacheSpeedOfTapeInstant.Length; idx++)
					if (cacheSpeedOfTapeInstant[idx] != null && cacheSpeedOfTapeInstant[idx].InputData == inputData && cacheSpeedOfTapeInstant[idx].NumberSeconds == numberSeconds && cacheSpeedOfTapeInstant[idx].BarSeconds == barSeconds && cacheSpeedOfTapeInstant[idx].EqualsInput(input))
						return cacheSpeedOfTapeInstant[idx];
			return CacheIndicator<giuli13.SpeedOfTapeInstant>(new giuli13.SpeedOfTapeInstant(){ InputData = inputData, NumberSeconds = numberSeconds, BarSeconds = barSeconds }, input, ref cacheSpeedOfTapeInstant);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.SpeedOfTapeInstant SpeedOfTapeInstant(SpeedOfTapeInput inputData, int numberSeconds, int barSeconds)
		{
			return indicator.SpeedOfTapeInstant(Input, inputData, numberSeconds, barSeconds);
		}

		public Indicators.giuli13.SpeedOfTapeInstant SpeedOfTapeInstant(ISeries<double> input , SpeedOfTapeInput inputData, int numberSeconds, int barSeconds)
		{
			return indicator.SpeedOfTapeInstant(input, inputData, numberSeconds, barSeconds);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.SpeedOfTapeInstant SpeedOfTapeInstant(SpeedOfTapeInput inputData, int numberSeconds, int barSeconds)
		{
			return indicator.SpeedOfTapeInstant(Input, inputData, numberSeconds, barSeconds);
		}

		public Indicators.giuli13.SpeedOfTapeInstant SpeedOfTapeInstant(ISeries<double> input , SpeedOfTapeInput inputData, int numberSeconds, int barSeconds)
		{
			return indicator.SpeedOfTapeInstant(input, inputData, numberSeconds, barSeconds);
		}
	}
}

#endregion
