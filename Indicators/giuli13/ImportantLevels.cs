#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
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

// alias WPF brushes to avoid conflict with SharpDX.Direct2D1.Brush
using WpfBrushes = System.Windows.Media.Brushes;
#endregion

// Important levels drawn as horizontal lines from the right edge of the chart (price axis) to the left.
// Day: High/Low, Cash Open, Cash VAH/VAL/POC, Overnight High/Low, Prev Day High/Low, Prev Day (full and cash) VAH/VAL/POC.
// Week: High/Low, VAH/VAL/POC, VWAP, Prev Week High/Low/VAH/VAL/POC.
// Session times are written in the chosen Session Time Zone (default New York: overnight 18:00-09:30,
// cash 09:30-17:00), whatever time zone NinjaTrader is set to. Highs/lows come from a 1-minute series,
// volume profiles and VWAP from a 1-tick series (exact) or from the 1-minute bars (fast, approximate).
// All extra series use a 24/7 template, so the chart's type and trading hours do not matter.
// Lines and price markers always show the latest levels (last loaded day/week), also when scrolling back.

namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
    public enum ILDayRange
    {
        FullDay = 0,      // overnight start -> cash end
        CashSession = 1   // cash session only
    }

    public enum ILTimeZone
    {
        NewYork = 0,       // Eastern Time (follows US daylight saving time)
        Italy = 1,         // Central European Time (follows EU daylight saving time)
        NinjaTrader = 2    // the time zone set in Tools > Options > General
    }

    public enum ILProfileData
    {
        Tick = 0,     // every trade at its exact price (exact, slower to load)
        Minute = 1    // 1-minute bars, volume spread evenly over each bar's range (fast, approximate)
    }

    [CategoryOrder("General", 1)]
    [CategoryOrder("Sessions", 2)]
    [CategoryOrder("Day High", 3)]
    [CategoryOrder("Day Low", 4)]
    [CategoryOrder("Cash Open", 5)]
    [CategoryOrder("Cash VAH", 6)]
    [CategoryOrder("Cash VAL", 7)]
    [CategoryOrder("Cash POC", 8)]
    [CategoryOrder("Overnight High", 9)]
    [CategoryOrder("Overnight Low", 10)]
    [CategoryOrder("Prev Day High", 11)]
    [CategoryOrder("Prev Day Low", 12)]
    [CategoryOrder("Prev Day VAH", 13)]
    [CategoryOrder("Prev Day VAL", 14)]
    [CategoryOrder("Prev Day POC", 15)]
    [CategoryOrder("Prev Day Cash VAH", 16)]
    [CategoryOrder("Prev Day Cash VAL", 17)]
    [CategoryOrder("Prev Day Cash POC", 18)]
    [CategoryOrder("Week High", 19)]
    [CategoryOrder("Week Low", 20)]
    [CategoryOrder("Week VAH", 21)]
    [CategoryOrder("Week VAL", 22)]
    [CategoryOrder("Week POC", 23)]
    [CategoryOrder("Week VWAP", 24)]
    [CategoryOrder("Prev Week High", 25)]
    [CategoryOrder("Prev Week Low", 26)]
    [CategoryOrder("Prev Week VAH", 27)]
    [CategoryOrder("Prev Week VAL", 28)]
    [CategoryOrder("Prev Week POC", 29)]
    public class ImportantLevels : Indicator
    {
        private const int N = 27;

        // line indexes
        private const int L_DayHigh = 0;
        private const int L_DayLow = 1;
        private const int L_CashOpen = 2;
        private const int L_CashVah = 3;
        private const int L_CashVal = 4;
        private const int L_CashPoc = 5;
        private const int L_OnHigh = 6;
        private const int L_OnLow = 7;
        private const int L_PrevHigh = 8;
        private const int L_PrevLow = 9;
        private const int L_PdVah = 10;
        private const int L_PdVal = 11;
        private const int L_PdPoc = 12;
        private const int L_PdCashVah = 13;
        private const int L_PdCashVal = 14;
        private const int L_PdCashPoc = 15;
        private const int L_WeekHigh = 16;
        private const int L_WeekLow = 17;
        private const int L_WeekVah = 18;
        private const int L_WeekVal = 19;
        private const int L_WeekPoc = 20;
        private const int L_WeekVwap = 21;
        private const int L_PrevWeekHigh = 22;
        private const int L_PrevWeekLow = 23;
        private const int L_PrevWeekVah = 24;
        private const int L_PrevWeekVal = 25;
        private const int L_PrevWeekPoc = 26;

        // level of every line on every primary bar (lines are drawn at the last visible bar's value)
        private double[] lvl;

        private int tickBip = -1;   // BarsInProgress of the 1-tick series (-1 = not used)

        // What the shown lines need (decided once in Configure: hidden lines cost nothing)
        private bool needDayProf, needCashProf, needCashVa, needWeekProf, needVwap, needVolume;

        // First trading day seen in each data series (to check that enough days are loaded)
        private DateTime firstMinuteDate = DateTime.MinValue, firstTickDate = DateTime.MinValue;
        private TimeSpan firstMinuteOff, firstTickOff;       // how far into its session the first data started
        private DateTime prevDayDate = DateTime.MinValue;    // trading day before the current one
        private volatile bool warnWeekData, warnWeekTick, warnDayData, warnDayTick;
        private const string WarnDayDataText = "ImportantLevels: load at least 2 days to see the previous day lines (Data Series > Days to load).";
        private const string WarnDayTickText = "ImportantLevels: not enough tick history for Prev Day VA/POC. Load at least 2 days of tick data or set Profile Data = Minute.";
        private const string WarnWeekDataText = "ImportantLevels: load at least 15 days to see the week lines (Data Series > Days to load).";
        private const string WarnWeekTickText = "ImportantLevels: not enough tick history for Week VA/POC/VWAP. Load at least 15 days of tick data or set Profile Data = Minute.";

        // ---- Day state ----
        private DateTime curDay = DateTime.MinValue;   // calendar date of the day's cash session
        private double dayHi, dayLo, cashHi, cashLo, cashOpen, onHi, onLo;
        private double prevFullHi, prevFullLo, prevCashHi, prevCashLo;
        private double pdVah, pdVal, pdPoc, pdcVah, pdcVal, pdcPoc;
        private double cashVah, cashVal, cashPoc;   // current day's cash session, recalculated once per minute
        private Dictionary<long, double> dayProf, cashProf;   // price in ticks -> volume

        // ---- Week state ----
        private DateTime curWeek = DateTime.MinValue;  // Monday of the trading week
        private double weekHi, weekLo, weekVah, weekVal, weekPoc, weekPv, weekV;
        private double prevWeekHi, prevWeekLo, pwVah, pwVal, pwPoc;
        private Dictionary<long, double> weekProf;
        private int lastVaMinuteBar = -1;
        private double[] vaBuf = new double[4096];

        // ---- Session limits as offsets from the overnight start ----
        private TimeSpan onStartTod, cashStartOff, cashEndOff;
        private TimeZoneInfo sessionTz;   // null = no conversion (NinjaTrader's own time zone)

        // ---- Render-thread only ----
        private TextFormat textFormat;
        private readonly List<RectangleF> labelRects = new List<RectangleF>();

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "ImportantLevels";
                Description = "Day, overnight, previous day and week levels (high/low, value area, POC, VWAP) as lines from the price axis to the left.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = true;
                IsAutoScale = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;   // only the last bar is used

                // General
                DayRange = ILDayRange.FullDay;
                ProfileData = ILProfileData.Tick;
                ValueAreaPercent = 70;
                AllLinesLength = 50;
                LabelFont = new SimpleFont("Arial", 12);

                // Sessions (New York time = CME globex / NYSE hours, correct all year)
                SessionTimeZone = ILTimeZone.NewYork;
                OvernightStart = DateTime.Parse("18:00", CultureInfo.InvariantCulture);
                CashStart = DateTime.Parse("09:30", CultureInfo.InvariantCulture);
                CashEnd = DateTime.Parse("17:00", CultureInfo.InvariantCulture);

                // Lines
                DayHighShow = true; DayHighStroke = new Stroke(MakeBrush("#FF1E90FF"), DashStyleHelper.Solid, 2); DayHighLength = 0; DayHighMarker = true; DayHighLabel = "Day High";
                DayLowShow = true; DayLowStroke = new Stroke(MakeBrush("#FF1E90FF"), DashStyleHelper.Solid, 2); DayLowLength = 0; DayLowMarker = true; DayLowLabel = "Day Low";
                CashOpenShow = false; CashOpenStroke = new Stroke(MakeBrush("#FF25D725"), DashStyleHelper.Solid, 2); CashOpenLength = 0; CashOpenMarker = true; CashOpenLabel = "Cash Open";
                CashVahShow = true; CashVahStroke = new Stroke(MakeBrush("#FFC6C6C6"), DashStyleHelper.Solid, 1); CashVahLength = 0; CashVahMarker = true; CashVahLabel = "Cash VAH";
                CashValShow = true; CashValStroke = new Stroke(MakeBrush("#FFC6C6C6"), DashStyleHelper.Solid, 1); CashValLength = 0; CashValMarker = true; CashValLabel = "Cash VAL";
                CashPocShow = true; CashPocStroke = new Stroke(MakeBrush("#FFFFFFFF"), DashStyleHelper.Solid, 2); CashPocLength = 0; CashPocMarker = true; CashPocLabel = "Cash POC";
                OnHighShow = true; OnHighStroke = new Stroke(MakeBrush("#FFBA55D3"), DashStyleHelper.Solid, 1); OnHighLength = 0; OnHighMarker = true; OnHighLabel = "ON High";
                OnLowShow = true; OnLowStroke = new Stroke(MakeBrush("#FFBA55D3"), DashStyleHelper.Solid, 1); OnLowLength = 0; OnLowMarker = true; OnLowLabel = "ON Low";
                PrevHighShow = true; PrevHighStroke = new Stroke(MakeBrush("#FFFF3755"), DashStyleHelper.Solid, 2); PrevHighLength = 0; PrevHighMarker = true; PrevHighLabel = "PD High";
                PrevLowShow = true; PrevLowStroke = new Stroke(MakeBrush("#FFFF3755"), DashStyleHelper.Solid, 2); PrevLowLength = 0; PrevLowMarker = true; PrevLowLabel = "PD Low";
                PdVahShow = false; PdVahStroke = new Stroke(MakeBrush("#FFAAAAAA"), DashStyleHelper.Solid, 1); PdVahLength = 0; PdVahMarker = true; PdVahLabel = "PD VAH";
                PdValShow = false; PdValStroke = new Stroke(MakeBrush("#FFAAAAAA"), DashStyleHelper.Solid, 1); PdValLength = 0; PdValMarker = true; PdValLabel = "PD VAL";
                PdPocShow = true; PdPocStroke = new Stroke(MakeBrush("#FFAAAAAA"), DashStyleHelper.Solid, 2); PdPocLength = 0; PdPocMarker = true; PdPocLabel = "PD POC";
                PdCashVahShow = true; PdCashVahStroke = new Stroke(MakeBrush("#FFAAAAAA"), DashStyleHelper.Solid, 1); PdCashVahLength = 0; PdCashVahMarker = true; PdCashVahLabel = "PD Cash VAH";
                PdCashValShow = true; PdCashValStroke = new Stroke(MakeBrush("#FFAAAAAA"), DashStyleHelper.Solid, 1); PdCashValLength = 0; PdCashValMarker = true; PdCashValLabel = "PD Cash VAL";
                PdCashPocShow = true; PdCashPocStroke = new Stroke(MakeBrush("#FFAAAAAA"), DashStyleHelper.Solid, 2); PdCashPocLength = 0; PdCashPocMarker = true; PdCashPocLabel = "PD Cash POC";
                WeekHighShow = true; WeekHighStroke = new Stroke(MakeBrush("#FF708090"), DashStyleHelper.Solid, 2); WeekHighLength = 0; WeekHighMarker = true; WeekHighLabel = "Week High";
                WeekLowShow = true; WeekLowStroke = new Stroke(MakeBrush("#FF708090"), DashStyleHelper.Solid, 2); WeekLowLength = 0; WeekLowMarker = true; WeekLowLabel = "Week Low";
                WeekVahShow = true; WeekVahStroke = new Stroke(MakeBrush("#FF708090"), DashStyleHelper.Solid, 1); WeekVahLength = 0; WeekVahMarker = true; WeekVahLabel = "Week VAH";
                WeekValShow = true; WeekValStroke = new Stroke(MakeBrush("#FF708090"), DashStyleHelper.Solid, 1); WeekValLength = 0; WeekValMarker = true; WeekValLabel = "Week VAL";
                WeekPocShow = true; WeekPocStroke = new Stroke(MakeBrush("#FFFFFFFF"), DashStyleHelper.Solid, 2); WeekPocLength = 0; WeekPocMarker = true; WeekPocLabel = "Week POC";
                WeekVwapShow = true; WeekVwapStroke = new Stroke(MakeBrush("#FF708090"), DashStyleHelper.Solid, 2); WeekVwapLength = 0; WeekVwapMarker = true; WeekVwapLabel = "Week VWAP";
                PrevWeekHighShow = true; PrevWeekHighStroke = new Stroke(MakeBrush("#FF717171"), DashStyleHelper.Dash, 2); PrevWeekHighLength = 0; PrevWeekHighMarker = true; PrevWeekHighLabel = "PW High";
                PrevWeekLowShow = true; PrevWeekLowStroke = new Stroke(MakeBrush("#FF717171"), DashStyleHelper.Dash, 2); PrevWeekLowLength = 0; PrevWeekLowMarker = true; PrevWeekLowLabel = "PW Low";
                PrevWeekVahShow = true; PrevWeekVahStroke = new Stroke(MakeBrush("#FF717171"), DashStyleHelper.Dash, 1); PrevWeekVahLength = 0; PrevWeekVahMarker = true; PrevWeekVahLabel = "PW VAH";
                PrevWeekValShow = true; PrevWeekValStroke = new Stroke(MakeBrush("#FF717171"), DashStyleHelper.Dash, 1); PrevWeekValLength = 0; PrevWeekValMarker = true; PrevWeekValLabel = "PW VAL";
                PrevWeekPocShow = true; PrevWeekPocStroke = new Stroke(MakeBrush("#FF717171"), DashStyleHelper.Dash, 2); PrevWeekPocLength = 0; PrevWeekPocMarker = true; PrevWeekPocLabel = "PW POC";

                // Plots are used ONLY for the price markers (they are never drawn, see OnRender)
                AddPlot(MakeBrush("#FF1E90FF"), "DayHigh");
                AddPlot(MakeBrush("#FF1E90FF"), "DayLow");
                AddPlot(MakeBrush("#FF25D725"), "CashOpen");
                AddPlot(MakeBrush("#FFC6C6C6"), "CashVAH");
                AddPlot(MakeBrush("#FFC6C6C6"), "CashVAL");
                AddPlot(MakeBrush("#FFFFFFFF"), "CashPOC");
                AddPlot(MakeBrush("#FFBA55D3"), "OvernightHigh");
                AddPlot(MakeBrush("#FFBA55D3"), "OvernightLow");
                AddPlot(MakeBrush("#FFFF3755"), "PrevDayHigh");
                AddPlot(MakeBrush("#FFFF3755"), "PrevDayLow");
                AddPlot(MakeBrush("#FFAAAAAA"), "PrevDayVAH");
                AddPlot(MakeBrush("#FFAAAAAA"), "PrevDayVAL");
                AddPlot(MakeBrush("#FFAAAAAA"), "PrevDayPOC");
                AddPlot(MakeBrush("#FFAAAAAA"), "PrevDayCashVAH");
                AddPlot(MakeBrush("#FFAAAAAA"), "PrevDayCashVAL");
                AddPlot(MakeBrush("#FFAAAAAA"), "PrevDayCashPOC");
                AddPlot(MakeBrush("#FF708090"), "WeekHigh");
                AddPlot(MakeBrush("#FF708090"), "WeekLow");
                AddPlot(MakeBrush("#FF708090"), "WeekVAH");
                AddPlot(MakeBrush("#FF708090"), "WeekVAL");
                AddPlot(MakeBrush("#FFFFFFFF"), "WeekPOC");
                AddPlot(MakeBrush("#FF708090"), "WeekVWAP");
                AddPlot(MakeBrush("#FF717171"), "PrevWeekHigh");
                AddPlot(MakeBrush("#FF717171"), "PrevWeekLow");
                AddPlot(MakeBrush("#FF717171"), "PrevWeekVAH");
                AddPlot(MakeBrush("#FF717171"), "PrevWeekVAL");
                AddPlot(MakeBrush("#FF717171"), "PrevWeekPOC");
            }
            else if (State == State.Configure)
            {
                // only what the shown lines need is calculated
                needDayProf = GetShow(L_PdVah) || GetShow(L_PdVal) || GetShow(L_PdPoc);
                needCashVa = GetShow(L_CashVah) || GetShow(L_CashVal) || GetShow(L_CashPoc);
                needCashProf = needCashVa || GetShow(L_PdCashVah) || GetShow(L_PdCashVal) || GetShow(L_PdCashPoc);
                needWeekProf = GetShow(L_WeekVah) || GetShow(L_WeekVal) || GetShow(L_WeekPoc)
                            || GetShow(L_PrevWeekVah) || GetShow(L_PrevWeekVal) || GetShow(L_PrevWeekPoc);
                needVwap = GetShow(L_WeekVwap);
                needVolume = needDayProf || needCashProf || needWeekProf || needVwap;

                // 1-minute data (highs/lows) and, only when a profile/VWAP line is shown, 1-tick data. 24/7 template.
                AddDataSeries(Instrument.FullName, new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 1 }, "Default 24 x 7");
                tickBip = -1;
                if (ProfileData == ILProfileData.Tick && needVolume)
                {
                    AddDataSeries(Instrument.FullName, new BarsPeriod { BarsPeriodType = BarsPeriodType.Tick, Value = 1 }, "Default 24 x 7");
                    tickBip = 2;
                }

                sessionTz = ResolveTimeZone(SessionTimeZone);

                onStartTod = OvernightStart.TimeOfDay;
                cashStartOff = Mod24(CashStart.TimeOfDay - onStartTod);
                cashEndOff = Mod24(CashEnd.TimeOfDay - onStartTod);
                if (cashEndOff <= cashStartOff)
                    cashEndOff = TimeSpan.FromHours(24);

                // price marker color = line color
                for (int i = 0; i < N; i++)
                {
                    Stroke s = GetStroke(i);
                    if (s != null && s.Brush != null)
                        Plots[i].Brush = s.Brush;
                }
            }
            else if (State == State.DataLoaded)
            {
                lvl = new double[N];
                for (int i = 0; i < N; i++)
                    lvl[i] = double.NaN;

                dayProf = new Dictionary<long, double>();
                cashProf = new Dictionary<long, double>();
                weekProf = new Dictionary<long, double>();

                curDay = curWeek = DateTime.MinValue;
                firstMinuteDate = firstTickDate = prevDayDate = DateTime.MinValue;
                firstMinuteOff = firstTickOff = TimeSpan.Zero;
                warnWeekData = warnWeekTick = warnDayData = warnDayTick = false;
                lastVaMinuteBar = -1;
                dayHi = dayLo = cashHi = cashLo = cashOpen = onHi = onLo = double.NaN;
                prevFullHi = prevFullLo = prevCashHi = prevCashLo = double.NaN;
                pdVah = pdVal = pdPoc = pdcVah = pdcVal = pdcPoc = double.NaN;
                cashVah = cashVal = cashPoc = double.NaN;
                weekHi = weekLo = weekVah = weekVal = weekPoc = double.NaN;
                weekPv = weekV = 0;
                prevWeekHi = prevWeekLo = pwVah = pwVal = pwPoc = double.NaN;
            }
            else if (State == State.Terminated)
            {
                try { textFormat?.Dispose(); } catch { }
                textFormat = null;
            }
        }

        // =====================================================================
        //  DATA
        // =====================================================================
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 1)
            {
                ProcessMinute();
                WriteOutputs();
            }
            else if (BarsInProgress == tickBip)
            {
                ProcessTick();
                WriteOutputs();
            }
            else if (BarsInProgress == 0)
            {
                WriteOutputs();
            }
        }

        // Places a time (already in the session time zone) in its trading day; starts a new day/week when needed.
        // Returns false when the time is outside the sessions (after the cash close) or belongs to an older day.
        private bool Classify(DateTime t, out TimeSpan off)
        {
            DateTime anchor = t - onStartTod;
            DateTime day = anchor.Date;
            off = anchor - day;

            if (off >= cashEndOff)
                return false;

            // the trading day is named after the date of its cash session (Sunday 18:00 NY -> Monday)
            DateTime tradeDate = (day + onStartTod + cashStartOff).Date;

            if (tradeDate > curDay)
                RollDay(tradeDate);
            else if (tradeDate < curDay)
                return false;

            return true;
        }

        private void ProcessMinute()
        {
            if (CurrentBars[1] < 0)
                return;

            int per = BarsArray[1].BarsPeriod.Value;

            // Minute profile mode: the bar that just CLOSED goes into the profiles and VWAP
            if (needVolume && tickBip < 0 && IsFirstTickOfBar && CurrentBars[1] >= 1)
            {
                TimeSpan offc;
                if (Classify(ToSessionTime(Times[1][1]).AddMinutes(-per), out offc))
                    AddMinuteVolume(Highs[1][1], Lows[1][1], Closes[1][1], Volumes[1][1], offc >= cashStartOff);
            }

            // minute bars are stamped with their END time: classify the bar by its start
            TimeSpan off;
            if (Classify(ToSessionTime(Times[1][0]).AddMinutes(-per), out off))
            {
                if (firstMinuteDate == DateTime.MinValue)
                {
                    firstMinuteDate = curDay;
                    firstMinuteOff = off;
                }

                double h = Highs[1][0];
                double l = Lows[1][0];

                dayHi = Max(dayHi, h);
                dayLo = Min(dayLo, l);
                weekHi = Max(weekHi, h);
                weekLo = Min(weekLo, l);

                if (off < cashStartOff)
                {
                    onHi = Max(onHi, h);
                    onLo = Min(onLo, l);
                }
                else
                {
                    if (double.IsNaN(cashOpen))
                        cashOpen = Opens[1][0];
                    cashHi = Max(cashHi, h);
                    cashLo = Min(cashLo, l);
                }
            }

            // live week and cash value areas: recalculated once per minute
            if ((needWeekProf || needCashVa) && CurrentBars[1] != lastVaMinuteBar)
            {
                lastVaMinuteBar = CurrentBars[1];
                if (needWeekProf)
                    ComputeValueArea(weekProf, out weekVah, out weekVal, out weekPoc);
                if (needCashVa)
                    ComputeValueArea(cashProf, out cashVah, out cashVal, out cashPoc);
            }
        }

        private void ProcessTick()
        {
            if (CurrentBars[tickBip] < 0)
                return;

            double v = Volumes[tickBip][0];
            if (v <= 0)
                return;

            TimeSpan off;
            if (!Classify(ToSessionTime(Times[tickBip][0]), out off))
                return;

            if (firstTickDate == DateTime.MinValue)
            {
                firstTickDate = curDay;
                firstTickOff = off;
            }

            double p = Closes[tickBip][0];
            long k = (long)Math.Round(p / TickSize);

            if (needDayProf) AddTo(dayProf, k, v);
            if (needCashProf && off >= cashStartOff) AddTo(cashProf, k, v);
            if (needWeekProf) AddTo(weekProf, k, v);

            if (needVwap)
            {
                weekPv += p * v;
                weekV += v;
            }
        }

        // Minute mode: the bar's volume is spread evenly over its range; VWAP uses the typical price
        private void AddMinuteVolume(double h, double l, double c, double v, bool cash)
        {
            if (v <= 0 || double.IsNaN(h) || double.IsNaN(l))
                return;

            long lo = (long)Math.Round(l / TickSize);
            long hi = (long)Math.Round(h / TickSize);
            long n = hi - lo + 1;
            if (n <= 0 || n > 20000)
                return;

            if (needDayProf || needCashProf || needWeekProf)
            {
                double each = v / n;
                for (long k = lo; k <= hi; k++)
                {
                    if (needDayProf) AddTo(dayProf, k, each);
                    if (needCashProf && cash) AddTo(cashProf, k, each);
                    if (needWeekProf) AddTo(weekProf, k, each);
                }
            }

            if (needVwap)
            {
                weekPv += (h + l + c) / 3.0 * v;
                weekV += v;
            }
        }

        private void RollDay(DateTime tradeDate)
        {
            // close the day that just ended (days without data are skipped)
            if (curDay != DateTime.MinValue)
            {
                if (!double.IsNaN(dayHi))
                {
                    prevFullHi = dayHi;
                    prevFullLo = dayLo;
                    prevCashHi = cashHi;
                    prevCashLo = cashLo;

                    if (needDayProf)
                        ComputeValueArea(dayProf, out pdVah, out pdVal, out pdPoc);
                    if (needCashProf)
                        ComputeValueArea(cashProf, out pdcVah, out pdcVal, out pdcPoc);   // NaN if the day had no cash session
                }
            }

            // new trading week (Monday): close the week that just ended
            DateTime week = tradeDate.AddDays(-(((int)tradeDate.DayOfWeek + 6) % 7));
            if (week != curWeek)
            {
                if (curWeek != DateTime.MinValue)
                {
                    if (!double.IsNaN(weekHi))
                    {
                        prevWeekHi = weekHi;
                        prevWeekLo = weekLo;
                    }
                    if (needWeekProf && weekProf.Count > 0)
                        ComputeValueArea(weekProf, out pwVah, out pwVal, out pwPoc);
                }

                curWeek = week;
                weekProf.Clear();
                weekHi = weekLo = weekVah = weekVal = weekPoc = double.NaN;
                weekPv = weekV = 0;
            }

            if (curDay != DateTime.MinValue)
                prevDayDate = curDay;

            curDay = tradeDate;
            dayProf.Clear();
            cashProf.Clear();
            dayHi = dayLo = cashHi = cashLo = cashOpen = onHi = onLo = double.NaN;
            cashVah = cashVal = cashPoc = double.NaN;
        }

        // Value area (standard two-row expansion from the POC) on a 1-tick grid
        private void ComputeValueArea(Dictionary<long, double> prof, out double vah, out double val, out double poc)
        {
            vah = val = poc = double.NaN;
            if (prof == null || prof.Count == 0)
                return;

            long min = long.MaxValue, max = long.MinValue;
            foreach (var kv in prof)
            {
                if (kv.Key < min) min = kv.Key;
                if (kv.Key > max) max = kv.Key;
            }

            long size = max - min + 1;
            if (size <= 0 || size > 1000000)
                return;

            int n = (int)size;
            if (vaBuf.Length < n)
                vaBuf = new double[Math.Max(n, vaBuf.Length * 2)];
            Array.Clear(vaBuf, 0, n);

            double total = 0;
            foreach (var kv in prof)
            {
                vaBuf[kv.Key - min] += kv.Value;
                total += kv.Value;
            }
            if (total <= 0)
                return;

            int p = 0;
            double mx = -1;
            for (int i = 0; i < n; i++)
                if (vaBuf[i] > mx) { mx = vaBuf[i]; p = i; }

            // ties: the price closest to the middle of the range
            double mid = (n - 1) / 2.0;
            for (int i = 0; i < n; i++)
                if (vaBuf[i] == mx && Math.Abs(i - mid) < Math.Abs(p - mid))
                    p = i;

            int lo = p, hi = p;
            double target = total * (ValueAreaPercent / 100.0);
            double acc = vaBuf[p];
            while (acc < target && (lo > 0 || hi < n - 1))
            {
                double up = 0, dn = 0;
                if (hi + 1 < n) up += vaBuf[hi + 1];
                if (hi + 2 < n) up += vaBuf[hi + 2];
                if (lo - 1 >= 0) dn += vaBuf[lo - 1];
                if (lo - 2 >= 0) dn += vaBuf[lo - 2];

                bool takeUp = hi + 1 >= n ? false : (lo - 1 < 0 ? true : up >= dn);
                if (takeUp)
                {
                    acc += vaBuf[++hi];
                    if (hi + 1 < n) acc += vaBuf[++hi];
                }
                else
                {
                    acc += vaBuf[--lo];
                    if (lo - 1 >= 0) acc += vaBuf[--lo];
                }
            }

            vah = (min + hi) * TickSize;
            val = (min + lo) * TickSize;
            poc = (min + p) * TickSize;
        }

        private double Level(int i)
        {
            switch (i)
            {
                case 0: return DayRange == ILDayRange.FullDay ? dayHi : cashHi;
                case 1: return DayRange == ILDayRange.FullDay ? dayLo : cashLo;
                case 2: return cashOpen;
                case 3: return cashVah;
                case 4: return cashVal;
                case 5: return cashPoc;
                case 6: return onHi;
                case 7: return onLo;
                case 8: return DayRange == ILDayRange.FullDay ? prevFullHi : prevCashHi;
                case 9: return DayRange == ILDayRange.FullDay ? prevFullLo : prevCashLo;
                case 10: return pdVah;
                case 11: return pdVal;
                case 12: return pdPoc;
                case 13: return pdcVah;
                case 14: return pdcVal;
                case 15: return pdcPoc;
                case 16: return weekHi;
                case 17: return weekLo;
                case 18: return weekVah;
                case 19: return weekVal;
                case 20: return weekPoc;
                case 21: return weekV > 0 ? weekPv / weekV : double.NaN;
                case 22: return prevWeekHi;
                case 23: return prevWeekLo;
                case 24: return pwVah;
                case 25: return pwVal;
                case 26: return pwPoc;
            }
            return double.NaN;
        }

        private void WriteOutputs()
        {
            if (lvl == null || CurrentBars[0] < 0)
                return;

            // Week lines need the whole previous week in the data. Data must start before the previous
            // week's Monday (15 calendar days always cover it). Otherwise the week lines are skipped.
            bool weekOkMinute = WeekDataOk(firstMinuteDate);
            bool weekOkProfile = tickBip >= 0 ? WeekDataOk(firstTickDate) : weekOkMinute;

            bool anyWeekShown = false, anyWeekProfShown = false;
            for (int i = 0; i < N; i++)
            {
                if (!GetShow(i)) continue;
                if (IsWeekLine(i)) anyWeekShown = true;
                if (IsWeekProfileLine(i)) anyWeekProfShown = true;
            }
            if (curWeek != DateTime.MinValue)
            {
                warnWeekData = anyWeekShown && !weekOkMinute;
                warnWeekTick = !warnWeekData && anyWeekProfShown && !weekOkProfile;
            }

            // Prev day lines need the whole previous trading day in the data
            bool dayOkMinute = DayDataOk(firstMinuteDate, firstMinuteOff);
            bool dayOkProfile = tickBip >= 0 ? DayDataOk(firstTickDate, firstTickOff) : dayOkMinute;

            bool anyDayShown = false, anyDayProfShown = false;
            for (int i = 0; i < N; i++)
            {
                if (!GetShow(i)) continue;
                if (IsPrevDayLine(i)) anyDayShown = true;
                if (IsPrevDayProfileLine(i)) anyDayProfShown = true;
            }
            if (curDay != DateTime.MinValue)
            {
                warnDayData = anyDayShown && !dayOkMinute;
                warnDayTick = !warnDayData && anyDayProfShown && !dayOkProfile;
            }

            for (int i = 0; i < N; i++)
            {
                double v = Level(i);
                if (IsWeekLine(i) && (!weekOkMinute || (IsWeekProfileLine(i) && !weekOkProfile)))
                    v = double.NaN;
                if (IsPrevDayLine(i) && (!dayOkMinute || (IsPrevDayProfileLine(i) && !dayOkProfile)))
                    v = double.NaN;
                lvl[i] = v;

                // only the last bar holds a value, so the price marker is always the latest level
                if (GetShow(i) && GetMarker(i) && !double.IsNaN(v))
                    Values[i][0] = v;
                else
                    Values[i].Reset();
                if (CurrentBars[0] > 0)
                    Values[i].Reset(1);
            }
        }

        private bool WeekDataOk(DateTime firstDate)
        {
            return firstDate != DateTime.MinValue && curWeek != DateTime.MinValue && firstDate < curWeek.AddDays(-7);
        }

        // The previous day is complete when the data started before it, or right at its session start
        // (within the first hour, so a load that begins at the session open still counts)
        private bool DayDataOk(DateTime firstDate, TimeSpan firstOff)
        {
            if (firstDate == DateTime.MinValue || prevDayDate == DateTime.MinValue)
                return false;
            return firstDate < prevDayDate || (firstDate == prevDayDate && firstOff <= TimeSpan.FromHours(1));
        }

        private static bool IsPrevDayLine(int i)
        {
            return i == L_PrevHigh || i == L_PrevLow || IsPrevDayProfileLine(i);
        }

        private static bool IsPrevDayProfileLine(int i)
        {
            return i >= L_PdVah && i <= L_PdCashPoc;
        }

        private static bool IsWeekLine(int i)
        {
            return i >= L_WeekHigh && i <= L_PrevWeekPoc;
        }

        // week lines that come from the volume profile / VWAP (tick data in Tick mode)
        private static bool IsWeekProfileLine(int i)
        {
            return i == L_WeekVah || i == L_WeekVal || i == L_WeekPoc || i == L_WeekVwap
                || i == L_PrevWeekVah || i == L_PrevWeekVal || i == L_PrevWeekPoc;
        }

        private static System.Windows.Media.Brush MakeBrush(string hex)
        {
            var b = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static void AddTo(Dictionary<long, double> map, long key, double value)
        {
            double ex;
            if (map.TryGetValue(key, out ex))
                map[key] = ex + value;
            else
                map[key] = value;
        }

        private static double Max(double a, double b) { return double.IsNaN(a) ? b : Math.Max(a, b); }
        private static double Min(double a, double b) { return double.IsNaN(a) ? b : Math.Min(a, b); }

        private static TimeSpan Mod24(TimeSpan t)
        {
            while (t < TimeSpan.Zero) t += TimeSpan.FromHours(24);
            while (t >= TimeSpan.FromHours(24)) t -= TimeSpan.FromHours(24);
            return t;
        }

        private static TimeZoneInfo ResolveTimeZone(ILTimeZone zone)
        {
            string id = zone == ILTimeZone.NewYork ? "Eastern Standard Time"     // Windows id for New York (handles DST)
                      : zone == ILTimeZone.Italy ? "W. Europe Standard Time"     // Windows id for Rome (handles DST)
                      : null;
            if (id == null)
                return null;
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { return null; }
        }

        // Bar times are in NinjaTrader's time zone (Tools > Options > General): convert to the session time zone
        private DateTime ToSessionTime(DateTime t)
        {
            try
            {
                TimeZoneInfo src = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
                if (sessionTz == null || src == null || src.Id == sessionTz.Id)
                    return t;
                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), src, sessionTz);
            }
            catch
            {
                return t;
            }
        }

        // =====================================================================
        //  RENDERING
        // =====================================================================
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            // base.OnRender is NOT called on purpose: the plots only feed the price markers and must not be drawn

            if (lvl == null || RenderTarget == null || ChartPanel == null)
                return;

            if (textFormat == null && LabelFont != null)
            {
                textFormat = LabelFont.ToDirectWriteTextFormat();
                textFormat.WordWrapping = WordWrapping.NoWrap;
            }

            float panelRight = ChartPanel.X + ChartPanel.W;
            float panelTop = ChartPanel.Y;
            float panelBottom = ChartPanel.Y + ChartPanel.H;

            labelRects.Clear();

            for (int i = 0; i < N; i++)
            {
                if (!GetShow(i))
                    continue;

                Stroke s = GetStroke(i);
                if (s == null || s.BrushDX == null)
                    continue;

                double v = lvl[i];   // always the latest level, whatever part of the chart is visible
                if (double.IsNaN(v))
                    continue;

                float y = chartScale.GetYByValue(v);
                if (y < panelTop || y > panelBottom)
                    continue;

                int len = GetLength(i);
                if (len <= 0)
                    len = AllLinesLength;   // 0 = follow the common length
                float pct = Math.Max(1, Math.Min(100, len)) / 100f;
                float xLeft = panelRight - ChartPanel.W * pct;

                RenderTarget.DrawLine(new Vector2(xLeft, y), new Vector2(panelRight, y), s.BrushDX, s.Width, s.StrokeStyle);

                string label = GetLabel(i);
                if (!string.IsNullOrWhiteSpace(label) && textFormat != null)
                    DrawLabel(label, xLeft, y, s);
            }

            if (warnWeekData)
                DrawWarning(WarnWeekDataText, 100f);
            else if (warnWeekTick)
                DrawWarning(WarnWeekTickText, 100f);

            if (warnDayData)
                DrawWarning(WarnDayDataText, 150f);
            else if (warnDayTick)
                DrawWarning(WarnDayTickText, 150f);
        }

        // Warning box centered horizontally, 'top' px from the top of the chart
        private void DrawWarning(string text, float top)
        {
            if (textFormat == null)
                return;

            using (var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, textFormat, Math.Max(50f, ChartPanel.W - 20f), 500f))
            using (var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0f, 0f, 0f, 0.75f)))
            using (var fg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(1f, 0.2f, 0.361f, 1f)))
            {
                layout.WordWrapping = WordWrapping.Wrap;
                float w = layout.Metrics.Width;
                float h = layout.Metrics.Height;
                float x = ChartPanel.X + (ChartPanel.W - w) / 2f;
                float y = ChartPanel.Y + top;

                RenderTarget.FillRectangle(new RectangleF(x - 8f, y - 4f, w + 16f, h + 8f), bg);
                RenderTarget.DrawTextLayout(new Vector2(x, y), layout, fg);
            }
        }

        // Label at the left end of the line, just above it, same color and opacity as the line.
        // Labels that would overlap (e.g. Day High = ON High) are placed side by side.
        private void DrawLabel(string label, float xLeft, float y, Stroke s)
        {
            using (var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, label, textFormat, 2000f, 500f))
            {
                float w = layout.Metrics.Width;
                float h = layout.Metrics.Height;
                float x = xLeft + 3f;
                float top = y - s.Width / 2f - 1f - h;

                for (int guard = 0; guard < N; guard++)
                {
                    bool moved = false;
                    foreach (var o in labelRects)
                    {
                        if (x < o.X + o.Width && o.X < x + w && top < o.Y + o.Height && o.Y < top + h)
                        {
                            x = o.X + o.Width + 8f;
                            moved = true;
                        }
                    }
                    if (!moved) break;
                }

                labelRects.Add(new RectangleF(x, top, w, h));
                RenderTarget.DrawTextLayout(new Vector2(x, top), layout, s.BrushDX);
            }
        }

        public override void OnRenderTargetChanged()
        {
            // NinjaTrader Strokes build their own DirectX brush / dash style from the render target
            if (RenderTarget != null)
            {
                for (int i = 0; i < N; i++)
                {
                    try
                    {
                        Stroke s = GetStroke(i);
                        if (s != null) s.RenderTarget = RenderTarget;
                    }
                    catch { }
                }
            }
            base.OnRenderTargetChanged();
        }

        // =====================================================================
        //  PER-LINE SETTINGS
        // =====================================================================
        private bool GetShow(int i)
        {
            switch (i)
            {
                case 0: return DayHighShow;
                case 1: return DayLowShow;
                case 2: return CashOpenShow;
                case 3: return CashVahShow;
                case 4: return CashValShow;
                case 5: return CashPocShow;
                case 6: return OnHighShow;
                case 7: return OnLowShow;
                case 8: return PrevHighShow;
                case 9: return PrevLowShow;
                case 10: return PdVahShow;
                case 11: return PdValShow;
                case 12: return PdPocShow;
                case 13: return PdCashVahShow;
                case 14: return PdCashValShow;
                case 15: return PdCashPocShow;
                case 16: return WeekHighShow;
                case 17: return WeekLowShow;
                case 18: return WeekVahShow;
                case 19: return WeekValShow;
                case 20: return WeekPocShow;
                case 21: return WeekVwapShow;
                case 22: return PrevWeekHighShow;
                case 23: return PrevWeekLowShow;
                case 24: return PrevWeekVahShow;
                case 25: return PrevWeekValShow;
                case 26: return PrevWeekPocShow;
            }
            return false;
        }

        private Stroke GetStroke(int i)
        {
            switch (i)
            {
                case 0: return DayHighStroke;
                case 1: return DayLowStroke;
                case 2: return CashOpenStroke;
                case 3: return CashVahStroke;
                case 4: return CashValStroke;
                case 5: return CashPocStroke;
                case 6: return OnHighStroke;
                case 7: return OnLowStroke;
                case 8: return PrevHighStroke;
                case 9: return PrevLowStroke;
                case 10: return PdVahStroke;
                case 11: return PdValStroke;
                case 12: return PdPocStroke;
                case 13: return PdCashVahStroke;
                case 14: return PdCashValStroke;
                case 15: return PdCashPocStroke;
                case 16: return WeekHighStroke;
                case 17: return WeekLowStroke;
                case 18: return WeekVahStroke;
                case 19: return WeekValStroke;
                case 20: return WeekPocStroke;
                case 21: return WeekVwapStroke;
                case 22: return PrevWeekHighStroke;
                case 23: return PrevWeekLowStroke;
                case 24: return PrevWeekVahStroke;
                case 25: return PrevWeekValStroke;
                case 26: return PrevWeekPocStroke;
            }
            return null;
        }

        private int GetLength(int i)
        {
            switch (i)
            {
                case 0: return DayHighLength;
                case 1: return DayLowLength;
                case 2: return CashOpenLength;
                case 3: return CashVahLength;
                case 4: return CashValLength;
                case 5: return CashPocLength;
                case 6: return OnHighLength;
                case 7: return OnLowLength;
                case 8: return PrevHighLength;
                case 9: return PrevLowLength;
                case 10: return PdVahLength;
                case 11: return PdValLength;
                case 12: return PdPocLength;
                case 13: return PdCashVahLength;
                case 14: return PdCashValLength;
                case 15: return PdCashPocLength;
                case 16: return WeekHighLength;
                case 17: return WeekLowLength;
                case 18: return WeekVahLength;
                case 19: return WeekValLength;
                case 20: return WeekPocLength;
                case 21: return WeekVwapLength;
                case 22: return PrevWeekHighLength;
                case 23: return PrevWeekLowLength;
                case 24: return PrevWeekVahLength;
                case 25: return PrevWeekValLength;
                case 26: return PrevWeekPocLength;
            }
            return 0;
        }

        private bool GetMarker(int i)
        {
            switch (i)
            {
                case 0: return DayHighMarker;
                case 1: return DayLowMarker;
                case 2: return CashOpenMarker;
                case 3: return CashVahMarker;
                case 4: return CashValMarker;
                case 5: return CashPocMarker;
                case 6: return OnHighMarker;
                case 7: return OnLowMarker;
                case 8: return PrevHighMarker;
                case 9: return PrevLowMarker;
                case 10: return PdVahMarker;
                case 11: return PdValMarker;
                case 12: return PdPocMarker;
                case 13: return PdCashVahMarker;
                case 14: return PdCashValMarker;
                case 15: return PdCashPocMarker;
                case 16: return WeekHighMarker;
                case 17: return WeekLowMarker;
                case 18: return WeekVahMarker;
                case 19: return WeekValMarker;
                case 20: return WeekPocMarker;
                case 21: return WeekVwapMarker;
                case 22: return PrevWeekHighMarker;
                case 23: return PrevWeekLowMarker;
                case 24: return PrevWeekVahMarker;
                case 25: return PrevWeekValMarker;
                case 26: return PrevWeekPocMarker;
            }
            return false;
        }

        private string GetLabel(int i)
        {
            switch (i)
            {
                case 0: return DayHighLabel;
                case 1: return DayLowLabel;
                case 2: return CashOpenLabel;
                case 3: return CashVahLabel;
                case 4: return CashValLabel;
                case 5: return CashPocLabel;
                case 6: return OnHighLabel;
                case 7: return OnLowLabel;
                case 8: return PrevHighLabel;
                case 9: return PrevLowLabel;
                case 10: return PdVahLabel;
                case 11: return PdValLabel;
                case 12: return PdPocLabel;
                case 13: return PdCashVahLabel;
                case 14: return PdCashValLabel;
                case 15: return PdCashPocLabel;
                case 16: return WeekHighLabel;
                case 17: return WeekLowLabel;
                case 18: return WeekVahLabel;
                case 19: return WeekValLabel;
                case 20: return WeekPocLabel;
                case 21: return WeekVwapLabel;
                case 22: return PrevWeekHighLabel;
                case 23: return PrevWeekLowLabel;
                case 24: return PrevWeekVahLabel;
                case 25: return PrevWeekValLabel;
                case 26: return PrevWeekPocLabel;
            }
            return null;
        }

        #region Properties

        // ===== General =====
        [Display(Name = "Day Levels Range", Order = 0, GroupName = "General",
            Description = "Range used for Day High/Low and Prev Day High/Low. FullDay = overnight start to cash end. CashSession = cash session only. (Prev Day VA/POC is always the full day, Prev Day Cash VA/POC the cash session.)")]
        public ILDayRange DayRange { get; set; }

        [Display(Name = "Profile Data", Order = 1, GroupName = "General",
            Description = "Data for value areas, POCs and VWAP. Tick = every trade at its exact price (exact, needs tick history, slower to load). Minute = 1-minute bars with the volume spread over each bar (fast, approximate).")]
        public ILProfileData ProfileData { get; set; }

        [Range(10, 100)]
        [Display(Name = "Value Area %", Order = 2, GroupName = "General",
            Description = "Share of the volume contained in the value area (standard: 70).")]
        public int ValueAreaPercent { get; set; }

        [Range(1, 100)]
        [Display(Name = "All Lines Length (% of chart)", Order = 3, GroupName = "General",
            Description = "Length of every line whose own Length is 0 (the default). Change it to resize all those lines at once. A line with its own Length (1-100) keeps it; set it back to 0 to follow this value again.")]
        public int AllLinesLength { get; set; }

        [Display(Name = "Label Font", Order = 4, GroupName = "General",
            Description = "Font of the line labels (the color comes from each line).")]
        public SimpleFont LabelFont { get; set; }

        // ===== Sessions =====
        [Display(Name = "Session Time Zone", Order = 0, GroupName = "Sessions",
            Description = "Time zone the session times below are written in. NewYork (default) = exchange hours, correct all year. Italy = Italian time. NinjaTrader = the time zone set in Tools > Options > General.")]
        public ILTimeZone SessionTimeZone { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Overnight Start", Order = 1, GroupName = "Sessions",
            Description = "Start of the overnight session and of the trading day (in the Session Time Zone). It can be before midnight (e.g. 18:00 New York). The overnight session ends at Cash Start.")]
        public DateTime OvernightStart { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Cash Start", Order = 2, GroupName = "Sessions",
            Description = "Start of the cash session (in the Session Time Zone). The Cash Open line is the first price at this time.")]
        public DateTime CashStart { get; set; }

        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        [Display(Name = "Cash End", Order = 3, GroupName = "Sessions",
            Description = "End of the cash session and of the trading day (in the Session Time Zone).")]
        public DateTime CashEnd { get; set; }

        // ===== Day High =====
        [Display(Name = "Show", Order = 0, GroupName = "Day High")]
        public bool DayHighShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Day High")]
        public Stroke DayHighStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Day High", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int DayHighLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Day High")]
        public bool DayHighMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Day High", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string DayHighLabel { get; set; }

        // ===== Day Low =====
        [Display(Name = "Show", Order = 0, GroupName = "Day Low")]
        public bool DayLowShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Day Low")]
        public Stroke DayLowStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Day Low", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int DayLowLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Day Low")]
        public bool DayLowMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Day Low", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string DayLowLabel { get; set; }

        // ===== Cash Open =====
        [Display(Name = "Show", Order = 0, GroupName = "Cash Open")]
        public bool CashOpenShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Cash Open")]
        public Stroke CashOpenStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Cash Open", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int CashOpenLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Cash Open")]
        public bool CashOpenMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Cash Open", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string CashOpenLabel { get; set; }

        // ===== Cash VAH =====
        [Display(Name = "Show", Order = 0, GroupName = "Cash VAH")]
        public bool CashVahShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Cash VAH")]
        public Stroke CashVahStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Cash VAH", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int CashVahLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Cash VAH")]
        public bool CashVahMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Cash VAH", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string CashVahLabel { get; set; }

        // ===== Cash VAL =====
        [Display(Name = "Show", Order = 0, GroupName = "Cash VAL")]
        public bool CashValShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Cash VAL")]
        public Stroke CashValStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Cash VAL", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int CashValLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Cash VAL")]
        public bool CashValMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Cash VAL", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string CashValLabel { get; set; }

        // ===== Cash POC =====
        [Display(Name = "Show", Order = 0, GroupName = "Cash POC")]
        public bool CashPocShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Cash POC")]
        public Stroke CashPocStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Cash POC", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int CashPocLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Cash POC")]
        public bool CashPocMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Cash POC", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string CashPocLabel { get; set; }

        // ===== Overnight High =====
        [Display(Name = "Show", Order = 0, GroupName = "Overnight High")]
        public bool OnHighShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Overnight High")]
        public Stroke OnHighStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Overnight High", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int OnHighLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Overnight High")]
        public bool OnHighMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Overnight High", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string OnHighLabel { get; set; }

        // ===== Overnight Low =====
        [Display(Name = "Show", Order = 0, GroupName = "Overnight Low")]
        public bool OnLowShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Overnight Low")]
        public Stroke OnLowStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Overnight Low", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int OnLowLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Overnight Low")]
        public bool OnLowMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Overnight Low", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string OnLowLabel { get; set; }

        // ===== Prev Day High =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day High")]
        public bool PrevHighShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day High")]
        public Stroke PrevHighStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day High", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PrevHighLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day High")]
        public bool PrevHighMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day High", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PrevHighLabel { get; set; }

        // ===== Prev Day Low =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day Low")]
        public bool PrevLowShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day Low")]
        public Stroke PrevLowStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day Low", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PrevLowLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day Low")]
        public bool PrevLowMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day Low", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PrevLowLabel { get; set; }

        // ===== Prev Day VAH =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day VAH")]
        public bool PdVahShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day VAH")]
        public Stroke PdVahStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day VAH", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PdVahLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day VAH")]
        public bool PdVahMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day VAH", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PdVahLabel { get; set; }

        // ===== Prev Day VAL =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day VAL")]
        public bool PdValShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day VAL")]
        public Stroke PdValStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day VAL", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PdValLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day VAL")]
        public bool PdValMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day VAL", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PdValLabel { get; set; }

        // ===== Prev Day POC =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day POC")]
        public bool PdPocShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day POC")]
        public Stroke PdPocStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day POC", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PdPocLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day POC")]
        public bool PdPocMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day POC", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PdPocLabel { get; set; }

        // ===== Prev Day Cash VAH =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day Cash VAH")]
        public bool PdCashVahShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day Cash VAH")]
        public Stroke PdCashVahStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day Cash VAH", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PdCashVahLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day Cash VAH")]
        public bool PdCashVahMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day Cash VAH", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PdCashVahLabel { get; set; }

        // ===== Prev Day Cash VAL =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day Cash VAL")]
        public bool PdCashValShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day Cash VAL")]
        public Stroke PdCashValStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day Cash VAL", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PdCashValLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day Cash VAL")]
        public bool PdCashValMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day Cash VAL", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PdCashValLabel { get; set; }

        // ===== Prev Day Cash POC =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Day Cash POC")]
        public bool PdCashPocShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Day Cash POC")]
        public Stroke PdCashPocStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Day Cash POC", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PdCashPocLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Day Cash POC")]
        public bool PdCashPocMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Day Cash POC", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PdCashPocLabel { get; set; }

        // ===== Week High =====
        [Display(Name = "Show", Order = 0, GroupName = "Week High")]
        public bool WeekHighShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Week High")]
        public Stroke WeekHighStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Week High", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int WeekHighLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Week High")]
        public bool WeekHighMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Week High", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string WeekHighLabel { get; set; }

        // ===== Week Low =====
        [Display(Name = "Show", Order = 0, GroupName = "Week Low")]
        public bool WeekLowShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Week Low")]
        public Stroke WeekLowStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Week Low", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int WeekLowLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Week Low")]
        public bool WeekLowMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Week Low", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string WeekLowLabel { get; set; }

        // ===== Week VAH =====
        [Display(Name = "Show", Order = 0, GroupName = "Week VAH")]
        public bool WeekVahShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Week VAH")]
        public Stroke WeekVahStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Week VAH", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int WeekVahLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Week VAH")]
        public bool WeekVahMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Week VAH", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string WeekVahLabel { get; set; }

        // ===== Week VAL =====
        [Display(Name = "Show", Order = 0, GroupName = "Week VAL")]
        public bool WeekValShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Week VAL")]
        public Stroke WeekValStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Week VAL", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int WeekValLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Week VAL")]
        public bool WeekValMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Week VAL", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string WeekValLabel { get; set; }

        // ===== Week POC =====
        [Display(Name = "Show", Order = 0, GroupName = "Week POC")]
        public bool WeekPocShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Week POC")]
        public Stroke WeekPocStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Week POC", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int WeekPocLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Week POC")]
        public bool WeekPocMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Week POC", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string WeekPocLabel { get; set; }

        // ===== Week VWAP =====
        [Display(Name = "Show", Order = 0, GroupName = "Week VWAP")]
        public bool WeekVwapShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Week VWAP")]
        public Stroke WeekVwapStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Week VWAP", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int WeekVwapLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Week VWAP")]
        public bool WeekVwapMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Week VWAP", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string WeekVwapLabel { get; set; }

        // ===== Prev Week High =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Week High")]
        public bool PrevWeekHighShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Week High")]
        public Stroke PrevWeekHighStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Week High", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PrevWeekHighLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Week High")]
        public bool PrevWeekHighMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Week High", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PrevWeekHighLabel { get; set; }

        // ===== Prev Week Low =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Week Low")]
        public bool PrevWeekLowShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Week Low")]
        public Stroke PrevWeekLowStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Week Low", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PrevWeekLowLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Week Low")]
        public bool PrevWeekLowMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Week Low", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PrevWeekLowLabel { get; set; }

        // ===== Prev Week VAH =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Week VAH")]
        public bool PrevWeekVahShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Week VAH")]
        public Stroke PrevWeekVahStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Week VAH", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PrevWeekVahLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Week VAH")]
        public bool PrevWeekVahMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Week VAH", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PrevWeekVahLabel { get; set; }

        // ===== Prev Week VAL =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Week VAL")]
        public bool PrevWeekValShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Week VAL")]
        public Stroke PrevWeekValStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Week VAL", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PrevWeekValLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Week VAL")]
        public bool PrevWeekValMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Week VAL", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PrevWeekValLabel { get; set; }

        // ===== Prev Week POC =====
        [Display(Name = "Show", Order = 0, GroupName = "Prev Week POC")]
        public bool PrevWeekPocShow { get; set; }

        [Display(Name = "Line", Order = 1, GroupName = "Prev Week POC")]
        public Stroke PrevWeekPocStroke { get; set; }

        [Range(0, 100)]
        [Display(Name = "Length (% of chart)", Order = 2, GroupName = "Prev Week POC", Description = "0 = use 'All Lines Length' (General). 1-100 = own length for this line (100 = from the price axis to the left edge of the chart).")]
        public int PrevWeekPocLength { get; set; }

        [Display(Name = "Price Marker", Order = 3, GroupName = "Prev Week POC")]
        public bool PrevWeekPocMarker { get; set; }

        [Display(Name = "Label", Order = 4, GroupName = "Prev Week POC", Description = "Text at the left end of the line. Leave empty for no label.")]
        public string PrevWeekPocLabel { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private giuli13.ImportantLevels[] cacheImportantLevels;
		public giuli13.ImportantLevels ImportantLevels()
		{
			return ImportantLevels(Input);
		}

		public giuli13.ImportantLevels ImportantLevels(ISeries<double> input)
		{
			if (cacheImportantLevels != null)
				for (int idx = 0; idx < cacheImportantLevels.Length; idx++)
					if (cacheImportantLevels[idx] != null &&  cacheImportantLevels[idx].EqualsInput(input))
						return cacheImportantLevels[idx];
			return CacheIndicator<giuli13.ImportantLevels>(new giuli13.ImportantLevels(), input, ref cacheImportantLevels);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.ImportantLevels ImportantLevels()
		{
			return indicator.ImportantLevels(Input);
		}

		public Indicators.giuli13.ImportantLevels ImportantLevels(ISeries<double> input )
		{
			return indicator.ImportantLevels(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.ImportantLevels ImportantLevels()
		{
			return indicator.ImportantLevels(Input);
		}

		public Indicators.giuli13.ImportantLevels ImportantLevels(ISeries<double> input )
		{
			return indicator.ImportantLevels(input);
		}
	}
}

#endregion
