#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators.giuli13
{
	public class BarCounter : Indicator
	{
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description									= @"Custom bar counter";
				Name										= "BarCounter";
				Calculate									= Calculate.OnBarClose;
				IsOverlay									= false;
				DisplayInDataBox							= true;
				DrawOnPricePanel							= true;
				DrawHorizontalGridLines						= true;
				DrawVerticalGridLines						= true;
				PaintPriceMarkers							= true;
				ScaleJustification							= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				//Disable this property if your indicator requires custom values that cumulate with each new market data event. 
				//See Help Guide for additional information.
				IsSuspendedWhileInactive					= true;
			}
			else if (State == State.Configure)
			{
			}
		}

		protected override void OnBarUpdate()
		{
			//Add your custom indicator logic here.
		}
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private giuli13.BarCounter[] cacheBarCounter;
		public giuli13.BarCounter BarCounter()
		{
			return BarCounter(Input);
		}

		public giuli13.BarCounter BarCounter(ISeries<double> input)
		{
			if (cacheBarCounter != null)
				for (int idx = 0; idx < cacheBarCounter.Length; idx++)
					if (cacheBarCounter[idx] != null &&  cacheBarCounter[idx].EqualsInput(input))
						return cacheBarCounter[idx];
			return CacheIndicator<giuli13.BarCounter>(new giuli13.BarCounter(), input, ref cacheBarCounter);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.giuli13.BarCounter BarCounter()
		{
			return indicator.BarCounter(Input);
		}

		public Indicators.giuli13.BarCounter BarCounter(ISeries<double> input )
		{
			return indicator.BarCounter(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.giuli13.BarCounter BarCounter()
		{
			return indicator.BarCounter(Input);
		}

		public Indicators.giuli13.BarCounter BarCounter(ISeries<double> input )
		{
			return indicator.BarCounter(input);
		}
	}
}

#endregion
