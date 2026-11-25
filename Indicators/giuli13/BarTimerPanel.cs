#region Using declarations
using System;
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
using SWM = System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
#endregion

// Draggable timer panel in its own small window: drag it anywhere (over other chart windows, other
// monitors); double-click opens the indicator settings.
// Modes: time left in the current chart bar, a fixed clock countdown (1h / 30m / 5m / 1m), or the clock (hh:mm:ss).
// The panel's screen position is saved with the workspace; it hides when its chart's workspace is hidden.

namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
    public enum BarTimerMode
    {
        ChartBar = 0,       // time left in the current bar (Minute / Second charts only)
        OneHour = 1,        // countdown to the next full hour
        ThirtyMinutes = 2,  // countdown to the next :00 / :30
        FiveMinutes = 3,    // countdown to the next 5 minute mark
        OneMinute = 4,      // countdown to the next minute
        Clock = 5           // current time hh:mm:ss
    }

    // Named BarTimerPanel because NinjaTrader 8 already ships a built-in "BarTimer" indicator
    [CategoryOrder("First Line", 1)]
    [CategoryOrder("Second Line", 2)]
    [CategoryOrder("Panel", 3)]
    public class BarTimerPanel : Indicator
    {
        private DispatcherTimer timer;

        // ---- UI thread only ----
        private SWC.Border border;
        private SWC.TextBlock textBlock, textBlock2;   // first line, optional second line
        private string lastText, lastText2;
        private int lastTemplateLen = -1, lastTemplateLen2 = -1;

        // The panel lives in its own small borderless window (like NinjaTrader's PositionTracker),
        // so it can be dragged anywhere: over other chart windows, other panels, other monitors.
        private SW.Window panelWindow;
        private SW.Window chartWindow;      // the chart window the indicator is on
        private Dispatcher uiDispatcher;
        private bool closingByCode;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "BarTimerPanel";
                Description = "Draggable timer panel: bar countdown, fixed countdown (1h/30m/5m/1m) or clock.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                IsChartOnly = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;

                Mode = BarTimerMode.ChartBar;
                PaddingX = 12;
                PaddingY = 8;

                BackgroundBrush = WpfBrushes.Black;
                BackgroundOpacity = 70;
                TextBrush = MakeBrush("#FF25D725");
                TextFont = new SimpleFont("Arial", 28);

                // second line: clock under the bar timer
                ClockPlusTimer = true;
                Mode2 = BarTimerMode.Clock;
                TextBrush2 = MakeBrush("#FFFF2EFF");   // rgb(255, 87, 255)
                TextFont2 = new SimpleFont("Arial", 18);

                // border (off by default)
                ShowBorder = false;
                PanelBorderWidth = 2;
                PanelBorderBrush = WpfBrushes.White;

                // screen position of the panel (set by dragging). NaN = not placed yet:
                // the panel then starts at the bottom right corner of the chart
                ScreenLeft = double.NaN;
                ScreenTop = double.NaN;
            }
            else if (State == State.Historical)
            {
                if (ChartControl != null && IsVisible && panelWindow == null)
                {
                    uiDispatcher = ChartControl.Dispatcher;
                    uiDispatcher.InvokeAsync(() =>
                    {
                        if (State >= State.Terminated || panelWindow != null)
                            return;

                        CreatePanelWindow();

                        // update the text on its own, even when no ticks arrive
                        timer = new DispatcherTimer(DispatcherPriority.Render, uiDispatcher)
                        {
                            Interval = TimeSpan.FromMilliseconds(250)
                        };
                        timer.Tick += OnTimerTick;
                        timer.Start();
                        UpdateText();
                    });
                }
            }
            else if (State == State.Terminated)
            {
                var d = uiDispatcher;
                if (d != null)
                {
                    d.InvokeAsync(() =>
                    {
                        if (timer != null)
                        {
                            timer.Stop();
                            timer.Tick -= OnTimerTick;
                            timer = null;
                        }

                        if (chartWindow != null)
                        {
                            chartWindow.IsVisibleChanged -= OnChartWindowVisibleChanged;
                            chartWindow = null;
                        }

                        if (panelWindow != null)
                        {
                            panelWindow.MouseLeftButtonDown -= OnPanelMouseDown;
                            panelWindow.LocationChanged -= OnPanelLocationChanged;
                            closingByCode = true;
                            panelWindow.Close();
                            panelWindow = null;
                        }
                    });
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // nothing to calculate: the panel updates itself from the timer
        }

        // Keeps the dragged position when the settings dialog applies changes
        public override void CopyTo(NinjaScript ninjaScript)
        {
            if (ninjaScript is BarTimerPanel other)
            {
                other.ScreenLeft = ScreenLeft;
                other.ScreenTop = ScreenTop;
            }
            base.CopyTo(ninjaScript);
        }

        // =====================================================================
        //  PANEL (UI thread)
        // =====================================================================
        private SW.FrameworkElement CreateControl()
        {
            var bg = BackgroundBrush != null ? BackgroundBrush.Clone() : new SWM.SolidColorBrush(SWM.Colors.Black);
            bg.Opacity = BackgroundOpacity / 100.0;
            bg.Freeze();

            textBlock = MakeTextBlock(TextFont ?? new SimpleFont("Arial", 28), TextBrush);

            SW.UIElement content = textBlock;
            textBlock2 = null;
            if (ClockPlusTimer)
            {
                // two centered lines, one under the other
                textBlock2 = MakeTextBlock(TextFont2 ?? new SimpleFont("Arial", 18), TextBrush2);
                textBlock2.Margin = new SW.Thickness(0, 2, 0, 0);

                var stack = new SWC.StackPanel { Orientation = SWC.Orientation.Vertical, HorizontalAlignment = SW.HorizontalAlignment.Center };
                stack.Children.Add(textBlock);
                stack.Children.Add(textBlock2);
                content = stack;
            }

            border = new SWC.Border
            {
                Background = bg,
                Padding = new SW.Thickness(PaddingX, PaddingY, PaddingX, PaddingY),
                BorderThickness = new SW.Thickness(ShowBorder ? Math.Max(0, PanelBorderWidth) : 0),
                BorderBrush = PanelBorderBrush ?? WpfBrushes.White,
                Child = content,
                Cursor = System.Windows.Input.Cursors.SizeAll,
                ToolTip = "Drag to move - double-click for settings"
            };

            return border;
        }

        // Creates the floating window that holds the panel and shows it
        private void CreatePanelWindow()
        {
            var content = CreateControl();

            panelWindow = new SW.Window
            {
                Title = "BarTimerPanel",
                WindowStyle = SW.WindowStyle.None,
                ResizeMode = SW.ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = WpfBrushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,           // do not steal the focus from the chart
                Topmost = true,                  // stays above the other windows while you move it around
                SizeToContent = SW.SizeToContent.WidthAndHeight,
                WindowStartupLocation = SW.WindowStartupLocation.Manual,
                Content = content
            };

            // position: the saved one if it is still on a screen, otherwise the chart's bottom right corner
            content.Measure(new SW.Size(double.PositiveInfinity, double.PositiveInfinity));
            SW.Size size = content.DesiredSize;
            if (IsOnScreen(ScreenLeft, ScreenTop, size))
            {
                panelWindow.Left = ScreenLeft;
                panelWindow.Top = ScreenTop;
            }
            else
            {
                SW.Point p = DefaultPosition(size);
                panelWindow.Left = p.X;
                panelWindow.Top = p.Y;
            }
            ScreenLeft = panelWindow.Left;
            ScreenTop = panelWindow.Top;

            panelWindow.MouseLeftButtonDown += OnPanelMouseDown;
            panelWindow.LocationChanged += OnPanelLocationChanged;
            panelWindow.Closing += (s, e) => { if (!closingByCode) e.Cancel = true; };   // only the indicator closes it

            // hide the panel when its chart window is hidden (other workspace) and show it again after
            chartWindow = SW.Window.GetWindow(ChartControl);
            if (chartWindow != null)
                chartWindow.IsVisibleChanged += OnChartWindowVisibleChanged;

            if (chartWindow == null || chartWindow.IsVisible)
                panelWindow.Show();
        }

        // Bottom right corner of the chart, 16 px from the edges (screen coordinates in WPF units)
        private SW.Point DefaultPosition(SW.Size size)
        {
            try
            {
                SW.Point topLeft = ChartControl.PointToScreen(new SW.Point(0, 0));
                SW.Point bottomRight = ChartControl.PointToScreen(new SW.Point(ChartControl.ActualWidth, ChartControl.ActualHeight));

                // PointToScreen gives device pixels: convert to WPF units (matters on scaled displays)
                var source = SW.PresentationSource.FromVisual(ChartControl);
                if (source != null && source.CompositionTarget != null)
                {
                    var m = source.CompositionTarget.TransformFromDevice;
                    topLeft = m.Transform(topLeft);
                    bottomRight = m.Transform(bottomRight);
                }

                return new SW.Point(bottomRight.X - size.Width - 16, bottomRight.Y - size.Height - 16);
            }
            catch
            {
                return new SW.Point(SW.SystemParameters.WorkArea.Right - size.Width - 16,
                                    SW.SystemParameters.WorkArea.Bottom - size.Height - 16);
            }
        }

        // True when at least part of the panel would be on one of the screens
        private static bool IsOnScreen(double left, double top, SW.Size size)
        {
            if (double.IsNaN(left) || double.IsNaN(top))
                return false;

            double vl = SW.SystemParameters.VirtualScreenLeft;
            double vt = SW.SystemParameters.VirtualScreenTop;
            double vr = vl + SW.SystemParameters.VirtualScreenWidth;
            double vb = vt + SW.SystemParameters.VirtualScreenHeight;

            return left + Math.Max(20, size.Width) > vl + 20 && left < vr - 20
                && top + Math.Max(20, size.Height) > vt + 20 && top < vb - 20;
        }

        // Drag anywhere; double-click opens the indicator settings
        private void OnPanelMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1)
            {
                ChartControl?.OnIndicatorsHotKey(this, null);
                return;
            }

            try { panelWindow.DragMove(); }
            catch { }
        }

        private void OnPanelLocationChanged(object sender, EventArgs e)
        {
            if (panelWindow == null) return;
            ScreenLeft = panelWindow.Left;
            ScreenTop = panelWindow.Top;
        }

        private void OnChartWindowVisibleChanged(object sender, SW.DependencyPropertyChangedEventArgs e)
        {
            if (panelWindow == null || chartWindow == null) return;
            if (chartWindow.IsVisible)
                panelWindow.Show();
            else
                panelWindow.Hide();
        }

        private static SWC.TextBlock MakeTextBlock(SimpleFont font, WpfBrush brush)
        {
            return new SWC.TextBlock
            {
                FontFamily = font.Family,
                FontSize = font.Size,
                FontWeight = font.Bold ? SW.FontWeights.Bold : SW.FontWeights.Normal,
                FontStyle = font.Italic ? SW.FontStyles.Italic : SW.FontStyles.Normal,
                Foreground = brush ?? WpfBrushes.White,
                TextAlignment = SW.TextAlignment.Center,
                HorizontalAlignment = SW.HorizontalAlignment.Center,
                VerticalAlignment = SW.VerticalAlignment.Center
            };
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            UpdateText();
        }

        private void UpdateText()
        {
            if (textBlock == null) return;

            // Same clock as NinjaTrader's BarTimer: Playback time when connected to Playback,
            // truncated to the whole second (so ChartBar mode reaches 00:00 exactly when the bar closes)
            DateTime now = Connection.PlaybackConnection != null ? Connection.PlaybackConnection.Now : NinjaTrader.Core.Globals.Now;
            now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));

            UpdateLine(textBlock, BuildText(Mode, now), ref lastText, ref lastTemplateLen);
            if (textBlock2 != null)
                UpdateLine(textBlock2, BuildText(Mode2, now), ref lastText2, ref lastTemplateLen2);
        }

        private static void UpdateLine(SWC.TextBlock tb, string text, ref string last, ref int lastLen)
        {
            if (text == last) return;
            last = text;
            tb.Text = text;

            // fixed width per text length, so the panel does not jitter when digits change
            string template = ToTemplate(text);
            if (template.Length != lastLen)
            {
                lastLen = template.Length;
                var ft = new SWM.FormattedText(template, CultureInfo.CurrentUICulture, SW.FlowDirection.LeftToRight,
                    new SWM.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, SW.FontStretches.Normal),
                    tb.FontSize, WpfBrushes.Black, SWM.VisualTreeHelper.GetDpi(tb).PixelsPerDip);
                tb.Width = Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
            }
        }

        // =====================================================================
        //  TEXT
        // =====================================================================
        private string BuildText(BarTimerMode mode, DateTime now)
        {
            switch (mode)
            {
                case BarTimerMode.Clock:
                    return now.ToString("HH:mm:ss");

                case BarTimerMode.OneHour: return Countdown(now, 3600);
                case BarTimerMode.ThirtyMinutes: return Countdown(now, 1800);
                case BarTimerMode.FiveMinutes: return Countdown(now, 300);
                case BarTimerMode.OneMinute: return Countdown(now, 60);

                default:
                    return ChartBarCountdown(now);
            }
        }

        // Seconds left until the next multiple of 'period' on the clock
        private static string Countdown(DateTime now, int period)
        {
            int sec = (int)now.TimeOfDay.TotalSeconds;
            int rem = (period - sec % period) % period;
            return FormatSeconds(rem);
        }

        // Time left in the forming bar. NinjaTrader stamps time-based bars with their CLOSE time.
        private string ChartBarCountdown(DateTime now)
        {
            var bars = ChartBars != null ? ChartBars.Bars : null;
            if (bars == null || bars.Count == 0)
                return "--:--";

            var type = bars.BarsPeriod.BarsPeriodType;
            if (type != BarsPeriodType.Minute && type != BarsPeriodType.Second)
                return "--:--";   // not a time-based chart

            DateTime barEnd = bars.GetTime(bars.Count - 1);
            double rem = (barEnd - now).TotalSeconds;   // 'now' is on a whole second, so this is a whole number
            if (rem < 0) rem = 0;   // no new tick yet / market closed
            return FormatSeconds((int)Math.Round(rem));
        }

        private static string FormatSeconds(int s)
        {
            if (s >= 3600)
                return string.Format("{0:00}:{1:00}:{2:00}", s / 3600, (s / 60) % 60, s % 60);
            return string.Format("{0:00}:{1:00}", s / 60, s % 60);
        }

        // Same length as the text but with all digits = '0'
        private static string ToTemplate(string text)
        {
            var c = text.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (char.IsDigit(c[i]) || c[i] == '-') c[i] = '0';
            return new string(c);
        }

        private static WpfBrush MakeBrush(string hex)
        {
            var b = new SWM.SolidColorBrush((SWM.Color)SWM.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        #region Properties

        // ===== First Line =====
        [NinjaScriptProperty]
        [Display(Name = "Mode", Order = 0, GroupName = "First Line",
            Description = "Clock = current time (hh:mm:ss). ChartBar = time left in the current bar (Minute/Second charts). OneHour / ThirtyMinutes / FiveMinutes / OneMinute = countdown to the next clock mark (mm:ss).")]
        public BarTimerMode Mode { get; set; }

        [XmlIgnore]
        [Display(Name = "Text Color", Order = 1, GroupName = "First Line")]
        public WpfBrush TextBrush { get; set; }

        [Browsable(false)]
        public string TextBrushSerialize
        {
            get { return Serialize.BrushToString(TextBrush); }
            set { TextBrush = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Font", Order = 2, GroupName = "First Line",
            Description = "NinjaTrader font picker: family, size, bold, italic.")]
        public SimpleFont TextFont { get; set; }

        // ===== Second Line =====
        [Display(Name = "Clock Plus Timer", Order = 0, GroupName = "Second Line",
            Description = "Adds a second line under the first one (by default the clock under the bar timer).")]
        public bool ClockPlusTimer { get; set; }

        [Display(Name = "Mode", Order = 1, GroupName = "Second Line",
            Description = "What the second line shows (same choices as the first line).")]
        public BarTimerMode Mode2 { get; set; }

        [XmlIgnore]
        [Display(Name = "Text Color", Order = 2, GroupName = "Second Line")]
        public WpfBrush TextBrush2 { get; set; }

        [Browsable(false)]
        public string TextBrush2Serialize
        {
            get { return Serialize.BrushToString(TextBrush2); }
            set { TextBrush2 = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Font", Order = 3, GroupName = "Second Line",
            Description = "NinjaTrader font picker: family, size, bold, italic.")]
        public SimpleFont TextFont2 { get; set; }

        // ===== Panel =====
        [XmlIgnore]
        [Display(Name = "Background Color", Order = 0, GroupName = "Panel")]
        public WpfBrush BackgroundBrush { get; set; }

        [Browsable(false)]
        public string BackgroundBrushSerialize
        {
            get { return Serialize.BrushToString(BackgroundBrush); }
            set { BackgroundBrush = Serialize.StringToBrush(value); }
        }

        [Range(0, 100)]
        [Display(Name = "Background Opacity (%)", Order = 1, GroupName = "Panel")]
        public int BackgroundOpacity { get; set; }

        [Range(0, 100)]
        [Display(Name = "Padding Width (px)", Order = 2, GroupName = "Panel",
            Description = "Space between the text and the left/right borders of the panel.")]
        public int PaddingX { get; set; }

        [Range(0, 100)]
        [Display(Name = "Padding Height (px)", Order = 3, GroupName = "Panel",
            Description = "Space between the text and the top/bottom borders of the panel.")]
        public int PaddingY { get; set; }

        [Display(Name = "Border", Order = 4, GroupName = "Panel",
            Description = "Draws a border around the panel.")]
        public bool ShowBorder { get; set; }

        [Range(1, 20)]
        [Display(Name = "Border Width (px)", Order = 5, GroupName = "Panel")]
        public int PanelBorderWidth { get; set; }

        [XmlIgnore]
        [Display(Name = "Border Color", Order = 6, GroupName = "Panel")]
        public WpfBrush PanelBorderBrush { get; set; }

        [Browsable(false)]
        public string PanelBorderBrushSerialize
        {
            get { return Serialize.BrushToString(PanelBorderBrush); }
            set { PanelBorderBrush = Serialize.StringToBrush(value); }
        }

        // ===== Position (set by dragging, saved with the workspace) =====
        [Browsable(false)]
        public double ScreenLeft { get; set; }

        [Browsable(false)]
        public double ScreenTop { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private giuli13.BarTimerPanel[] cacheBarTimerPanel;
		public giuli13.BarTimerPanel BarTimerPanel(BarTimerMode mode)
		{
			return BarTimerPanel(Input, mode);
		}

		public giuli13.BarTimerPanel BarTimerPanel(ISeries<double> input, BarTimerMode mode)
		{
			if (cacheBarTimerPanel != null)
				for (int idx = 0; idx < cacheBarTimerPanel.Length; idx++)
					if (cacheBarTimerPanel[idx] != null && cacheBarTimerPanel[idx].Mode == mode && cacheBarTimerPanel[idx].EqualsInput(input))
						return cacheBarTimerPanel[idx];
			return CacheIndicator<giuli13.BarTimerPanel>(new giuli13.BarTimerPanel(){ Mode = mode }, input, ref cacheBarTimerPanel);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.BarTimerPanel BarTimerPanel(BarTimerMode mode)
		{
			return indicator.BarTimerPanel(Input, mode);
		}

		public Indicators.giuli13.BarTimerPanel BarTimerPanel(ISeries<double> input , BarTimerMode mode)
		{
			return indicator.BarTimerPanel(input, mode);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.BarTimerPanel BarTimerPanel(BarTimerMode mode)
		{
			return indicator.BarTimerPanel(Input, mode);
		}

		public Indicators.giuli13.BarTimerPanel BarTimerPanel(ISeries<double> input , BarTimerMode mode)
		{
			return indicator.BarTimerPanel(input, mode);
		}
	}
}

#endregion
