﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Entity;
using TradeSharp.Contract.Entity;
using TradeSharp.Contract.Util.BL;
using TradeSharp.Robot.BacktestServerProxy;

namespace TradeSharp.Robot.Robot
{
    public class Pair<T, U>
    {
        public Pair()
        {
        }

        public Pair(T first, U second)
        {
            this.First = first;
            this.Second = second;
        }

        public T First { get; set; }
        public U Second { get; set; }
    };

    ///<summary>
    /// характеристики робота:
    /// 1) интенсивность торговли - при дефолтовых настройках на EURUSD:H1 порядка 1 сделки на 1.5 - 2 свечи 
    /// 
    /// варьируемые параметры:
    /// 1) Period: не меньше 1. Если 2, 3 - не эффективно. Обычно 15,  ..
    /// 2) N, M: Левая и Правая граници временного лага. Предполагается, что чем дальше "коридор" N - M от текущей свечи и чем от уже, тем меньше интенчивность сделок. 
    /// Но на практике это пока не подтвердилось. 
    /// Примерно одинаковые результаты дают измерения на временном лаге 
    /// N = 10 M = 5  и Period = 15 
    /// N = 20 M = 15 и Period = 25 
    /// N = 20 M = 18 и Period = 25  (сужение интервала на 3 единици результата не дало - сделки открываются оочень часто)
    /// второстепенные
    /// 3) CloseOpposite: true, false
    /// 4) StopLoss, TakeProfit: 15 ... 800
    ///</summary>
    [DisplayName("Индекс относительной силы")]
    public class RsiDiverRobot : BaseRobot 
    {
        #region Настройки
        private int stopLossPoints = 250;
        [PropertyXMLTag("Robot.StopLossPoints")]
        [DisplayName("Стоплосс, пп")]
        [Category("Торговые")]
        [Description("Стоплосс, пунктов. 0 - не задан")]
        public int StopLossPoints
        {
            get { return stopLossPoints; }
            set { stopLossPoints = value; }
        }

        private int takeProfitPoints = 250;
        [PropertyXMLTag("Robot.TakeProfitPoints")]
        [DisplayName("Тейкпрофит, пп")]
        [Category("Торговые")]
        [Description("Тейкпрофит, пунктов. 0 - не задан")]
        public int TakeProfitPoints
        {
            get { return takeProfitPoints; }
            set { takeProfitPoints = value; }
        }

        private int period = 15;
        [PropertyXMLTag("Robot.Period")]
        [DisplayName("Период расчёта")]
        [Category("Торговые")]
        [Description("Определяет количество свечей, на которых анализируются цены закрытия")]
        public int Period
        {
            get { return period; }
            set
            {
                period = value;

                if (value < N)
                    N = period;
            }
        }

        private int n = 10;
        [PropertyXMLTag("Robot.N")]
        [DisplayName("Левая граница временного лага")]
        [Category("Торговые")]
        [Description("Левая граница временного лага")]
        public int N
        {
            get { return n; }
            set
            {
                // N не может быть больше чем период расчёта (в крайнем случае равен периоду)
                n = (value > period) ? period : value;

                if (value <= M)
                    M = N - 1;
            }
        }

        private int m = 5;
        [PropertyXMLTag("Robot.M")]
        [DisplayName("Правая граница временного лага")]
        [Category("Торговые")]
        [Description("Правая граница временного лага")]
        public int M
        {
            get { return m; }
            set
            {
                // MN не может быть больше чем N (в крайнем случае меньше на единицу, что бы N - M был равен хотя бы 1)
                m = (value >= N) ? N - 1 : value;
            }
        }

        private bool closeOpposite = true;
        [PropertyXMLTag("CloseOpposite")]
        [DisplayName("Закрывать сделки")]
        [Category("Торговые")]
        [Description("Закрывать сделки, открытые против нового тренда")]
        public bool CloseOpposite
        {
            get { return closeOpposite; }
            set { closeOpposite = value; }
        }
        #endregion

        #region Переменные
        private CandlePacker packer;
        private string ticker;
        private float U { get; set; }
        private float D { get; set; }

        /// <summary>
        /// Очередь с вытеснением из значений цен закрытия свечей
        /// </summary>
        private RestrictedQueue<Pair<float, double?>> closePrices; //TODO rename
        #endregion

        private List<string> events;

        public override BaseRobot MakeCopy()
        {
            var bot = new RsiDiverRobot
            {
                StopLossPoints = StopLossPoints,
                TakeProfitPoints = TakeProfitPoints,
                FixedVolume = FixedVolume,
                Leverage = Leverage,
                RoundType = RoundType,
                NewsChannels = NewsChannels,
                RoundMinVolume = RoundMinVolume,
                RoundVolumeStep = RoundVolumeStep
            };
            CopyBaseSettings(bot);
            return bot;
        }

        public override void Initialize(RobotContext robotContext, CurrentProtectedContext protectedContext)
        {
            base.Initialize(robotContext, protectedContext);

            if (Graphics.Count != 1)
                return;

            packer = new CandlePacker(Graphics[0].b);
            ticker = Graphics[0].a;

            closePrices = new RestrictedQueue<Pair<float, double?>>(period);
            lastMessages = new List<string>();
        }

        public override Dictionary<string, DateTime> GetRequiredSymbolStartupQuotes(DateTime startTrade)
        {
            if (Graphics.Count == 0)
                return null;

            try
            {
                return new Dictionary<string, DateTime> { { Graphics[0].a, Graphics[0].b.GetDistanceTime(period, -1, startTrade) } };
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public override List<string> OnQuotesReceived(string[] names, CandleDataBidAsk[] quotes, bool isHistoryStartOff)
        {
            events = lastMessages.ToList();
            lastMessages.Clear();

            #region Получение текущей свечи
            var tickerIndex = -1;
            for (var i = 0; i < names.Length; i++)
                if (names[i] == ticker)
                {
                    tickerIndex = i;
                    break;
                }
            if (tickerIndex < 0)
                return events;

            var quote = quotes[tickerIndex];

            var candle = packer.UpdateCandle(quote);
            if (candle == null)
                return events;
            #endregion

            var currentPair = new Pair<float, double?>(candle.close, null);
            closePrices.Add(currentPair);
            if (closePrices.Length < closePrices.MaxQueueLength) return events;

            currentPair.Second = CalcRsi();


            for (var i = M; i < N; i++)
            {
                var previousPair = closePrices.ElementAt(closePrices.MaxQueueLength - i - 1);
                if (!previousPair.Second.HasValue)
                    continue;

                var diffClose = Math.Sign(currentPair.First - previousPair.First);
                var diffRsi = Math.Sign(currentPair.Second.Value - previousPair.Second.Value);

                if (diffClose == diffRsi || diffRsi == 0)
                    continue;

                if (CloseOpposite)
                    CloseCounterOrders(diffRsi, ticker);

                OpenOrder(diffRsi, ticker, quote);
                break;
            }

            return events;
        }

        private double CalcRsi()
        {
            U = 0;
            D = 0;
            for (var i = 1; i < closePrices.MaxQueueLength; i++)
            {
                var closePrice = closePrices.ElementAt(i - 1).First - closePrices.ElementAt(i).First;
                if (closePrice < 0)
                    D -= closePrice;
                else
                    U += closePrice;
            }

            double RSI;
            if (U == D && U == 0) RSI = 50;
            else if (D == 0) RSI = 100;
            else
                RSI = 100 - 100.0/(1 + U/D);
            return RSI;
        }


        private void CloseCounterOrders(int dealSign, string symbol)
        {
            List<MarketOrder> orders;
            GetMarketOrders(out orders, true);
            if (orders.Count == 0) return;
            var ordersToClose = orders.Where(o => o.Symbol == symbol && o.Side != dealSign);
            foreach (var order in ordersToClose)
                CloseMarketOrder(order.ID);
        }

        private void OpenOrder(int dealSign, string symbol, CandleDataBidAsk lastCandle)
        {
            var volume = CalculateVolume(symbol, base.Leverage);
            if (volume == 0)
            {
                events.Add(string.Format("{0} {1} отменена - объем равен 0",
                    dealSign > 0 ? "покупка" : "продажа", symbol));
                return;
            }

            var enterPrice = dealSign > 0 ? lastCandle.closeAsk : lastCandle.close;
            var stopLoss = enterPrice - dealSign * DalSpot.Instance.GetAbsValue(symbol, (float)StopLossPoints);
            var takeProfit = enterPrice + dealSign * DalSpot.Instance.GetAbsValue(symbol, (float)TakeProfitPoints);

            var order = new MarketOrder
            {
                AccountID = robotContext.AccountInfo.ID,    // Уникальный идентификатор счёта
                Magic = Magic,                              // Этот параметр позволяет отличать сделки разных роботов
                Symbol = symbol,                            // Инструмент по которому совершается сделка
                Volume = volume,                            // Объём средств, на который совершается сделка
                Side = dealSign,                            // Устанавливаем тип сделки - покупка или продажа
                StopLoss = stopLoss,                        // Устанавливаем величину Stop loss для открываемой сделки
                TakeProfit = takeProfit,                    // Устанавливаем величину Take profit для открываемой сделки
                ExpertComment = "TornAssholeRobot"          // Комментарий по сделке, оставленный роботом
            };
            var status = NewOrder(order,
                OrderType.Market, // исполнение по рыночной ценец - можно везде выбирать такой вариант
                0, 0); // последние 2 параметра для OrderType.Market не имеют значения
            if (status != RequestStatus.OK)
                events.Add(string.Format("Ошибка добавления ордера {0} {1}: {2}",
                    dealSign > 0 ? "BUY" : "SELL", symbol, status));
        }
    }
}