using AxKHOpenAPILib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StatTrader
{
    class Trader
    {
        public event Action<EPhase> Processed;
        public enum EPhase
        {
            Begin,
            OrderConfirm,
            Balance,            
            Price,
            Order,     
        }

        public EPhase Phase
        {
            get;
            private set;
        }

        public string Error
        {
            get;
            private set;
        }

        private class StockInfo
        {
            public StockInfo(Stock stock)
            {
                Stock = stock;
            }

            public Stock Stock;
            public int Quantity;
            public AskingPrice AskingPrice;
        }

        private class Order
        {
            public Stock Stock;
            public int Quantity;
        }

        private readonly string _account;
        private readonly int _quantity;
        private readonly float _margin;
        private readonly int _duration;
        private readonly StockInfo[] _targets;       
        private readonly List<StockInfo> _stockInfos = new List<StockInfo>();
        private readonly float _gap;

        private bool _wait;
        private readonly List<Order> _buyOrders = new List<Order>();
        private readonly List<Order> _sellOrders = new List<Order>();
        private bool _priceCollected;

        public Trader(string account, int quantity, float margin, int duration, Stock target1, Stock target2)
        {
            _account = account;
            _quantity = quantity;
            _margin = margin;
            _duration = duration;
            _targets = new StockInfo[2];
            _targets[0] = new StockInfo(target1);
            _targets[1] = new StockInfo(target2);           
            _stockInfos.AddRange(_targets);

            _gap = CalculateAvgGap(target1, target2, duration);

            Phase = EPhase.Begin;
        }

        private float CalculateAvgGap(Stock stock1, Stock stock2, int duration)
        {
            DateTime begin = DateTime.Now.AddDays(-(duration * 2));
            DateTime end = DateTime.Now.AddDays(-1);            

            string interval = "";
            var collection1 = StockPriceCollection.Get(stock1.Code, begin, end, interval, null).Result;
            var collection2 = StockPriceCollection.Get(stock2.Code, begin, end, interval, null).Result;

            int count1 = collection1.Items.Count;
            int count2 = collection2.Items.Count;

            if(count1 != count2)
            {
                throw new Exception("평균 갭을구하기 위한 차트 기간이 서로 다릅니다.");
            }

            if (count1 < duration)
            {
                throw new Exception("평균 갭을구하기 위한 차트 기간이 실제 기간보다 짧습니다.");
            }

            long price1Sum = 0;
            long price2Sum = 0;

            for (int i = 0; i < duration; ++i)
            {
                price1Sum += collection1.Items[count1 - i - 1].Price;
                price2Sum += collection2.Items[count1 - i - 1].Price;
            }

            float avgGap = (float)price2Sum / price1Sum;
            Debug.Info("Avg gap: {0:0.000}", avgGap);
            return avgGap;
        }

        public void SetAskingPrice(string code, AskingPrice price)
        {
            foreach(var balance in _stockInfos)
            {
                if(balance.Stock.Code == code)
                {
                    balance.AskingPrice = price;                    
                }
            }

            if(Phase == EPhase.Price)
            {
                ProcessPhase();
            }            
        }

        public void Process()
        {
            if(Phase == EPhase.Begin)
            {
                MoveState(EPhase.OrderConfirm);
            }

            ProcessPhase();

            if (Processed != null)
            {
                Processed(Phase);
            }
        }

        private void MoveState(EPhase phase)
        {
            if (phase != Phase)
            {
                Debug.Info("Phase: " + phase.ToString());
            }

            Phase = phase;
            ProcessPhase();
        }

        private void ProcessPhase()
        {
            if (_wait || !OpenApi.IsTradeable())
            {
                return;
            }

            switch (Phase)
            {
                case EPhase.OrderConfirm:
                    OnOrderConfirm();
                    break;
                case EPhase.Balance:
                    OnBalance();
                    break;
                case EPhase.Price:
                    OnPrice();
                    break;
                case EPhase.Order:
                    OnOrder();
                    break;
            }
        }

        private void OnOrderConfirm()
        {            
            _wait = true;

            OpenApi.SetInputValue("계좌번호", _account);
            OpenApi.SetInputValue("전체종목구분", "0");
            OpenApi.SetInputValue("매매구분", "0");
            OpenApi.SetInputValue("체결구분", "1");
            OpenApi.CommRqData("실시간미체결요청", "opt10075", delegate(_DKHOpenAPIEvents_OnReceiveTrDataEvent e)
            {
                int count = OpenApi.GetRepeatCnt(e);
                bool hasRemain = false;
                for(int i = 0; i < count; ++i)
                {
                    var name = OpenApi.GetTrData(e, "종목명", i);
                    var remain = OpenApi.GetTrData(e, "미체결수량", i).ToInt();
                    if(remain > 0)
                    {
                        hasRemain = true;
                        break;
                    }
                }

                _wait = false;                        
                MoveState(hasRemain ? EPhase.Begin : EPhase.Balance);
            });            
        }

        private void OnBalance()
        {
            _wait = true;

            OpenApi.UpdateBalances(_account, delegate (AxKHOpenAPILib._DKHOpenAPIEvents_OnReceiveTrDataEvent e)
            {
                int count = OpenApi.GetRepeatCnt(e);
                for (int i = 0; i < count; ++i)
                {
                    string 종목명 = OpenApi.GetTrData(e, "종목명", i);
                    string 보유수량 = OpenApi.GetTrData(e, "보유수량", i);
                    foreach(var stock in _stockInfos)
                    {
                        if(stock.Stock.ToString() == 종목명)
                        {
                            int quantity;
                            if (int.TryParse(보유수량, out quantity))
                            {
                                stock.Quantity = quantity;
                            }
                            else
                            {
                                // something wrong;
                                Error = "보유수량 오류: " + 보유수량;
                                return;
                            }
                            break;
                        }
                    }
                }

                _wait = false;
                MoveState(EPhase.Price);
            });
        }

        private void OnPrice()
        {
            var now = DateTime.Now;
            foreach(var info in _stockInfos)
            {
                if(info.AskingPrice == null)
                {
                    return;
                }

                TimeSpan span = now - info.AskingPrice.Time;
                if(Math.Abs(span.TotalSeconds) > 1)
                {
                    return;
                }
            }
            
            if(!_priceCollected)
            {
                Debug.Warn("Price Colleted!");
                _priceCollected = true;
            }

            // 맨 처음
            if (_targets[0].Quantity == 0 && _targets[1].Quantity == 0)
            {
                int price1 = CalculateTotalPrice(_quantity, _targets[0].AskingPrice.Sell);
                int price2 = CalculateTotalPrice(_quantity, _targets[1].AskingPrice.Sell);
                float curGap = (float)price2 / price1;

                _buyOrders.Add(new Order
                {
                    // 현재 갭이 더 크면 우선주가 더 비싸므로 보통주를 삼
                    Stock = curGap >= _gap ? _targets[0].Stock : _targets[1].Stock,
                    Quantity = _quantity
                });
            }
            else if (_targets[0].Quantity != 0 && _targets[1].Quantity != 0)
            {
                throw new Exception("우선주와 보통주 둘 다 가지고 있습니다. 하나는 수동으로 매각 해주세요");
            }
            else
            {
                int mine = _targets[0].Quantity > 0 ? 0 : 1;
                int quantity = _targets[mine].Quantity;
                int other = (mine + 1) % 2;

                int totalPrice1 = CalculateTotalPrice(quantity, _targets[mine].AskingPrice.Buy);
                int totalPrice2 = CalculateTotalPrice(quantity, _targets[other].AskingPrice.Sell);

                // 보통주면 갭이 더 높아지고 우선주면 갭이 작아짐
                float curGap = mine == 0 ? (float)totalPrice2 / totalPrice1 : (float)totalPrice1 / totalPrice2;

                // 보통주를 들고 있지만 우선주가 더 싸짐 or
                // 우선주를 들고 보통주가 더 싸짐
                if ((mine == 0 && curGap + _margin <= _gap) ||
                    (mine == 1 && curGap >= _gap + _margin))
                {
                    _sellOrders.Add(new Order
                    {
                        Stock = _targets[mine].Stock,
                        Quantity = _targets[mine].Quantity
                    });

                    int buyTarget = totalPrice1 / _targets[other].AskingPrice.Sell[0].Price;

                    _buyOrders.Add(new Order
                    {
                        Stock = _targets[other].Stock,
                        Quantity = buyTarget,
                    });

                    Order sell = _sellOrders.Last();
                    Order buy = _buyOrders.Last();
                    Debug.Info("교체 매매: Sell({0}) - {1}, Buy({2}) - {3}", sell.Stock.Name, sell.Quantity,
                        buy.Stock.Name, buy.Quantity);
                }
            }

            if (_buyOrders.Count > 0 || _sellOrders.Count > 0)
            {
                MoveState(EPhase.Order);
            }
        }

        private int CalculateTotalPrice(int quantity, List<Asking> askings)
        {
            int sum = 0;
            foreach(var asking in askings)
            {
                int num = Math.Min(quantity, asking.Quantity);
                quantity -= num;
                sum += asking.Price * num;
                if(quantity <= 0)
                {
                    return sum;
                }
            }

            return -1;
        }

        private void OnOrder()
        {
            for (int i = _sellOrders.Count - 1; i >= 0; --i)
            {
                var order = _sellOrders[i];
                var result = OpenApi.Sell(_account, order.Stock.Code, order.Quantity);
                Debug.Warn("Sell: {0}, {1}, Result: {2}", order.Stock, order.Quantity, result);
            }

            for (int i = _buyOrders.Count - 1; i >= 0; --i)
            {
                var order = _buyOrders[i];
                var result = OpenApi.Buy(_account, order.Stock.Code, order.Quantity);
                Debug.Warn("Buy: {0}, {1}, Result: {2}", order.Stock, order.Quantity, result);
            }

            _sellOrders.Clear();
            _buyOrders.Clear();
            MoveState(EPhase.OrderConfirm);
        }
    }
}

