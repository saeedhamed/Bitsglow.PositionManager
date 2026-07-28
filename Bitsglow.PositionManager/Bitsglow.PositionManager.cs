using System;
using System.Threading;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(AccessRights = AccessRights.None)]
    public class BitsglowPositionManager : Robot
    {
        public enum SizingMode
        {
            FixedVolume,
            FixedLoss,
            PercentRisk
        }

        public enum StopMode
        {
            FixedPips,
            AtrMultiple
        }

        private const string TradeLabel = "Bitsglow.PositionManager";
        private const int SessionDrawDays = 15;

        [Parameter("Sizing Mode", DefaultValue = SizingMode.PercentRisk, Group = "Risk")]
        public SizingMode Sizing { get; set; }

        [Parameter("Fixed Volume (lots)", DefaultValue = 0.01, MinValue = 0.0, Group = "Risk")]
        public double FixedLots { get; set; }

        [Parameter("Fixed Loss (account currency)", DefaultValue = 100.0, MinValue = 0.0, Group = "Risk")]
        public double FixedLossMoney { get; set; }

        [Parameter("Risk % of Balance", DefaultValue = 1.0, MinValue = 0.0, MaxValue = 100.0, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Max Recovery Multiplier", DefaultValue = 8, MinValue = 1, Group = "Risk")]
        public int MaxMultiplier { get; set; }

        [Parameter("SL Mode", DefaultValue = StopMode.FixedPips, Group = "Stop Loss")]
        public StopMode SlMode { get; set; }

        [Parameter("SL Pips", DefaultValue = 20.0, MinValue = 0.0, Group = "Stop Loss")]
        public double SlPips { get; set; }

        [Parameter("SL ATR Multiple", DefaultValue = 1.5, MinValue = 0.0, Group = "Stop Loss")]
        public double SlAtrMultiple { get; set; }

        [Parameter("TP Mode", DefaultValue = StopMode.FixedPips, Group = "Take Profit")]
        public StopMode TpMode { get; set; }

        [Parameter("TP Pips (0 = none)", DefaultValue = 40.0, MinValue = 0.0, Group = "Take Profit")]
        public double TpPips { get; set; }

        [Parameter("TP ATR Multiple (0 = none)", DefaultValue = 3.0, MinValue = 0.0, Group = "Take Profit")]
        public double TpAtrMultiple { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1, Group = "ATR")]
        public int AtrPeriod { get; set; }

        [Parameter("Start Hour (server time)", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "Trading Time")]
        public int StartHour { get; set; }

        [Parameter("End Hour (server time)", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "Trading Time")]
        public int EndHour { get; set; }

        [Parameter("Tick Delay (ms)", DefaultValue = 10, MinValue = 0, Group = "Backtest Slow Motion")]
        public int TickDelayMs { get; set; }

        [Parameter("Enable Signal", DefaultValue = true, Group = "Holy Grail Signal")]
        public bool SignalEnabled { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 1, Group = "Holy Grail Signal")]
        public int AdxPeriod { get; set; }

        [Parameter("ADX Threshold", DefaultValue = 30, MinValue = 10, MaxValue = 60, Group = "Holy Grail Signal")]
        public int AdxThreshold { get; set; }

        [Parameter("Signal EMA Period", DefaultValue = 20, MinValue = 1, Group = "Holy Grail Signal")]
        public int SignalEmaPeriod { get; set; }

        private AverageTrueRange _atr;
        private DirectionalMovementSystem _dms;
        private ExponentialMovingAverage _ema;
        private readonly Random _random = new Random();
        private int _lossStreak;
        private DateTime _lastSessionDrawDay = DateTime.MinValue;
        private bool _slowMotion;
        private bool? _wasInSession;

        private TradeType? _pendingSignal;
        private double _triggerPrice;
        private TradeType? _firedSignal;
        private int _signalCount;

        private TextBlock _sessionText;
        private TextBlock _riskText;
        private TextBlock _recoveryText;
        private TextBlock _signalText;
        private TextBlock _slowMotionText;

        private bool AllDay => StartHour == 0 && EndHour == 0;

        protected override void OnStart()
        {
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);
            _dms = Indicators.DirectionalMovementSystem(AdxPeriod);
            _ema = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SignalEmaPeriod);

            RestoreLossStreakFromHistory();
            Positions.Closed += OnPositionClosed;

            if (Chart != null)
            {
                Chart.AddHotkey(_ => PlaceOrder(TradeType.Buy), Key.B, ModifierKeys.None);
                Chart.AddHotkey(_ => PlaceOrder(TradeType.Sell), Key.S, ModifierKeys.None);
                Chart.AddHotkey(_ => PlaceOrder(_random.Next(2) == 0 ? TradeType.Buy : TradeType.Sell), Key.R, ModifierKeys.None);
                Chart.AddHotkey(_ => ToggleSlowMotion(), Key.D, ModifierKeys.None);
                DrawSessionHighlight();
                CreateRiskBox();
                UpdateRiskBox();
            }

            Print("{0} started. B = buy, S = sell, R = random, D = slow motion. Session: {1}",
                TradeLabel, AllDay ? "all day" : string.Format("{0:00}:00-{1:00}:00 server time", StartHour, EndHour));
        }

        protected override void OnTick()
        {
            AutoToggleSlowMotion();
            CheckSignalTrigger();
            UpdateRiskBox();

            if (IsBacktesting && _slowMotion && TickDelayMs > 0)
                Thread.Sleep(TickDelayMs);
        }

        protected override void OnBar()
        {
            UpdateHolyGrailSetup();

            if (Chart != null && Bars.OpenTimes.LastValue.Date != _lastSessionDrawDay)
                DrawSessionHighlight();
        }

        // Raschke "Holy Grail": ADX above threshold and rising, pullback touches the EMA,
        // entry trigger = break of the touch bar's extreme. Evaluated on the last closed bar.
        private void UpdateHolyGrailSetup()
        {
            if (!SignalEnabled)
                return;

            var i = Bars.Count - 2;
            if (i < 1)
                return;

            var adx = _dms.ADX[i];
            var adxRising = adx > _dms.ADX[i - 1];
            var trendUp = _dms.DIPlus[i] > _dms.DIMinus[i];

            // Setup is void when trend strength fades or direction flips.
            if (adx < AdxThreshold - 5 ||
                (_pendingSignal == TradeType.Buy && !trendUp) ||
                (_pendingSignal == TradeType.Sell && trendUp))
                _pendingSignal = null;

            if (adx <= AdxThreshold || !adxRising)
                return;

            var ema = _ema.Result[i];
            var touchedEma = Bars.LowPrices[i] <= ema && ema <= Bars.HighPrices[i];

            if (touchedEma)
            {
                _pendingSignal = trendUp ? TradeType.Buy : TradeType.Sell;
                _triggerPrice = trendUp ? Bars.HighPrices[i] : Bars.LowPrices[i];
                _firedSignal = null;
            }
            else if (_pendingSignal == TradeType.Buy && Bars.HighPrices[i] < _triggerPrice)
                _triggerPrice = Bars.HighPrices[i];   // untriggered: trail the trigger down lower highs
            else if (_pendingSignal == TradeType.Sell && Bars.LowPrices[i] > _triggerPrice)
                _triggerPrice = Bars.LowPrices[i];
        }

        private void CheckSignalTrigger()
        {
            if (_pendingSignal == null)
                return;

            var direction = _pendingSignal.Value;
            var fired = direction == TradeType.Buy ? Symbol.Ask > _triggerPrice : Symbol.Bid < _triggerPrice;
            if (!fired)
                return;

            _pendingSignal = null;
            _firedSignal = direction;
            Print("Holy Grail {0} signal fired at {1}.", direction, _triggerPrice);

            if (Chart == null)
                return;

            var offset = _atr.Result.LastValue * 0.3;
            var name = "bpm_hg_" + (++_signalCount);
            if (direction == TradeType.Buy)
                Chart.DrawIcon(name, ChartIconType.UpArrow, Bars.OpenTimes.LastValue,
                    Bars.LowPrices.LastValue - offset, Color.FromArgb(255, 80, 220, 120));
            else
                Chart.DrawIcon(name, ChartIconType.DownArrow, Bars.OpenTimes.LastValue,
                    Bars.HighPrices.LastValue + offset, Color.FromArgb(255, 240, 90, 90));
        }

        private void ToggleSlowMotion()
        {
            _slowMotion = !_slowMotion;
            Print("Slow motion {0}.", _slowMotion ? "ON" : "OFF");
            UpdateRiskBox();
        }

        // Slow motion turns itself on when the backtest enters the trading session and off when it leaves.
        private void AutoToggleSlowMotion()
        {
            if (!IsBacktesting || AllDay)
                return;

            var inSession = IsWithinSession(Server.Time);
            if (_wasInSession.HasValue && inSession != _wasInSession.Value)
            {
                _slowMotion = inSession;
                Print("Slow motion auto {0} (session {1}).", inSession ? "ON" : "OFF", inSession ? "started" : "ended");
            }

            _wasInSession = inSession;
        }

        private void PlaceOrder(TradeType tradeType)
        {
            if (Positions.Find(TradeLabel, SymbolName) != null)
            {
                Print("Blocked {0}: a position is already open.", tradeType);
                return;
            }

            if (!IsWithinSession(Server.Time))
            {
                Print("Blocked {0}: outside trading time {1:00}:00-{2:00}:00 (server time is {3:HH:mm}).",
                    tradeType, StartHour, EndHour, Server.Time);
                return;
            }

            var slPips = ResolvePips(SlMode, SlPips, SlAtrMultiple);
            var tpPips = ResolvePips(TpMode, TpPips, TpAtrMultiple);
            var volume = CalculateVolume(slPips);

            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("Blocked {0}: calculated volume {1} is below the symbol minimum {2}.",
                    tradeType, volume, Symbol.VolumeInUnitsMin);
                return;
            }

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, TradeLabel,
                slPips > 0 ? slPips : (double?)null,
                tpPips > 0 ? tpPips : (double?)null);

            if (result.IsSuccessful)
                Print("{0} {1} lots, SL {2} pips, TP {3} pips, recovery x{4}.",
                    tradeType, Symbol.VolumeInUnitsToQuantity(volume), slPips, tpPips, CurrentMultiplier());
            else
                Print("Order failed: {0}", result.Error);
        }

        private double ResolvePips(StopMode mode, double fixedPips, double atrMultiple)
        {
            if (mode == StopMode.FixedPips)
                return Math.Round(fixedPips, 1);

            var atrPips = _atr.Result.LastValue / Symbol.PipSize;
            return Math.Round(atrPips * atrMultiple, 1);
        }

        private int CurrentMultiplier()
        {
            var multiplier = 1;
            for (var i = 0; i < _lossStreak && multiplier < MaxMultiplier; i++)
                multiplier *= 2;
            return Math.Min(multiplier, MaxMultiplier);
        }

        private double CalculateVolume(double slPips)
        {
            var multiplier = CurrentMultiplier();
            double units;

            if (Sizing == SizingMode.FixedVolume || slPips <= 0)
                units = Symbol.QuantityToVolumeInUnits(FixedLots) * multiplier;
            else
                units = RiskMoney() * multiplier / (slPips * Symbol.PipValue);

            units = Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);
            return Math.Min(units, Symbol.VolumeInUnitsMax);
        }

        private double RiskMoney()
        {
            return Sizing == SizingMode.FixedLoss ? FixedLossMoney : Account.Balance * RiskPercent / 100.0;
        }

        private bool IsWithinSession(DateTime serverTime)
        {
            if (AllDay)
                return true;

            var hour = serverTime.Hour;
            return StartHour < EndHour
                ? hour >= StartHour && hour < EndHour
                : hour >= StartHour || hour < EndHour;   // overnight range, e.g. 22-6
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label != TradeLabel || args.Position.SymbolName != SymbolName)
                return;

            if (args.Reason == PositionCloseReason.StopLoss)
            {
                _lossStreak++;
                Print("Stop loss hit. Next position volume x{0}.", CurrentMultiplier());
            }
            else if (args.Position.NetProfit > 0)
            {
                if (_lossStreak > 0)
                    Print("Profit taken. Volume back to normal.");
                _lossStreak = 0;
            }

            UpdateRiskBox();
        }

        // After a restart, rebuild the loss streak from closed trades so recovery sizing carries on.
        private void RestoreLossStreakFromHistory()
        {
            _lossStreak = 0;
            for (var i = History.Count - 1; i >= 0; i--)
            {
                var trade = History[i];
                if (trade.Label != TradeLabel || trade.SymbolName != SymbolName)
                    continue;
                if (trade.NetProfit > 0)
                    break;
                _lossStreak++;
            }

            if (_lossStreak > 0)
                Print("Restored loss streak of {0} from history. Next volume x{1}.", _lossStreak, CurrentMultiplier());
        }

        private void DrawSessionHighlight()
        {
            if (AllDay)
                return;

            _lastSessionDrawDay = Bars.OpenTimes.LastValue.Date;
            var fill = Color.FromArgb(12, 0, 160, 255);

            for (var d = 0; d <= SessionDrawDays; d++)
            {
                var day = _lastSessionDrawDay.AddDays(-d);
                var start = day.AddHours(StartHour);
                var end = StartHour < EndHour ? day.AddHours(EndHour) : day.AddDays(1).AddHours(EndHour);

                var rect = Chart.DrawRectangle("bpm_session_" + day.ToString("yyyyMMdd"),
                    start, 0, end, Symbol.Bid * 1000, fill);
                rect.IsFilled = true;
            }
        }

        private void CreateRiskBox()
        {
            var panel = new StackPanel { Orientation = Orientation.Vertical };

            _sessionText = MakeLine(13);
            _sessionText.FontWeight = FontWeight.Bold;
            _riskText = MakeLine(12);
            _recoveryText = MakeLine(12);
            _signalText = MakeLine(12);
            _signalText.IsVisible = SignalEnabled;
            _slowMotionText = MakeLine(12);
            _slowMotionText.IsVisible = IsBacktesting;

            panel.AddChild(_sessionText);
            panel.AddChild(_riskText);
            panel.AddChild(_recoveryText);
            panel.AddChild(_signalText);
            panel.AddChild(_slowMotionText);

            var box = new Border
            {
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(12, 12, 0, 0),
                Padding = new Thickness(12, 8, 12, 8),
                Width = 190,
                CornerRadius = new CornerRadius(8),
                BackgroundColor = Color.FromArgb(200, 16, 20, 28),
                BorderColor = Color.FromArgb(45, 255, 255, 255),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                Child = panel
            };

            Chart.AddControl(box);
        }

        private TextBlock MakeLine(int fontSize)
        {
            return new TextBlock
            {
                ForegroundColor = Color.FromArgb(220, 255, 255, 255),
                FontSize = fontSize,
                FontFamily = "Consolas",
                Margin = new Thickness(0, 1, 0, 1)
            };
        }

        private void UpdateRiskBox()
        {
            if (_riskText == null)
                return;

            var inSession = IsWithinSession(Server.Time);
            var slPips = ResolvePips(SlMode, SlPips, SlAtrMultiple);
            var nextLots = Symbol.VolumeInUnitsToQuantity(CalculateVolume(slPips));
            var multiplier = CurrentMultiplier();

            // One status dot: green = ready, orange = ready but in recovery, red = session closed.
            _sessionText.Text = string.Format("● {0} {1}",
                inSession ? "LIVE" : "CLOSED",
                AllDay ? "24h" : string.Format("{0:00}-{1:00}", StartHour, EndHour));
            _sessionText.ForegroundColor = !inSession
                ? Color.FromArgb(255, 240, 90, 90)
                : multiplier > 1
                    ? Color.FromArgb(255, 255, 170, 60)
                    : Color.FromArgb(255, 80, 220, 120);

            var riskLabel = Sizing == SizingMode.FixedVolume
                ? "fixed"
                : Sizing == SizingMode.FixedLoss
                    ? string.Format("-{0:0}", RiskMoney() * multiplier)
                    : string.Format("{0:0.#}%", RiskPercent * multiplier);
            _riskText.Text = string.Format("{0:0.00} lot  {1}", nextLots, riskLabel);

            _recoveryText.IsVisible = multiplier > 1;
            _recoveryText.Text = string.Format("RECOVERY ×{0}", multiplier);
            _recoveryText.ForegroundColor = Color.FromArgb(255, 255, 170, 60);

            if (SignalEnabled)
            {
                if (_pendingSignal != null)
                {
                    _signalText.Text = string.Format("HG {0} @{1}",
                        _pendingSignal == TradeType.Buy ? "▲" : "▼",
                        Math.Round(_triggerPrice, Symbol.Digits));
                    _signalText.ForegroundColor = Color.FromArgb(255, 255, 220, 90);
                }
                else if (_firedSignal != null)
                {
                    _signalText.Text = _firedSignal == TradeType.Buy ? "HG ▲ LONG NOW" : "HG ▼ SHORT NOW";
                    _signalText.ForegroundColor = _firedSignal == TradeType.Buy
                        ? Color.FromArgb(255, 80, 220, 120)
                        : Color.FromArgb(255, 240, 90, 90);
                }
                else
                {
                    _signalText.Text = "HG —";
                    _signalText.ForegroundColor = Color.FromArgb(120, 255, 255, 255);
                }
            }

            if (IsBacktesting)
            {
                _slowMotionText.Text = _slowMotion
                    ? string.Format("SLOW {0}ms   [D]", TickDelayMs)
                    : "FULL SPEED  [D]";
                _slowMotionText.ForegroundColor = _slowMotion
                    ? Color.FromArgb(255, 90, 180, 255)
                    : Color.FromArgb(140, 255, 255, 255);
            }
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
        }
    }
}
