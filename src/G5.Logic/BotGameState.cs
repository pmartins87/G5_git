using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;


namespace G5.Logic
{
    public class BotGameState : IDisposable
    {
        private const int RAISE_SIZE_NOM = 2;
        private const int RAISE_SIZE_DEN = 3;

        private int _bigBlingSize;
        private PokerClient _pokerClient;
        private TableType _tableType;

        private Estimators.IActionEstimator _actionEstimator;
        private PreFlopCharts _preFlopCharts;
        private Random _rng = new Random();

        private List<Player> _players;
        private Board _board;
        private HoleCards _heroHoleCards;
        private Hand _currentHand;

        private int _playerToActInd;
        private int _heroInd;
        private int _buttonInd;
        private Street _street = Street.PreFlop;
        private int _numBets;
        private int _numCallers;
        private List<Position> _bettors;

        private int _preFlopChartsLevel;

        public struct ActionSizingContext
        {
            public ActionType ActionType;
            public int ActionAmount;
            public int AmountToCall;
            public int PotBeforeAction;
            public int StackBeforeAction;
            public int MoneyInPotBeforeAction;
            public int MaxMoneyInPotBeforeAction;
            public int BigBlindSize;
            public bool IsAllIn;
            public float BetToPotRatio;
            public float CallToPotRatio;
            public float StackCommitment;
            public float SprBeforeAction;

            public override string ToString()
            {
                return $"action={ActionType}, amount={ActionAmount}, amountToCall={AmountToCall}, pot={PotBeforeAction}, stack={StackBeforeAction}, " +
                       $"allin={IsAllIn}, betPot={BetToPotRatio:F2}, callPot={CallToPotRatio:F2}, commit={StackCommitment:F2}, spr={SprBeforeAction:F2}";
            }
        }

        private ActionSizingContext _lastActionSizingContext;

// Options
private bool _randomlySampleActions;

private string _preFlopChartsPath = "";
private string _preFlopChartsBucket = "";
private int _preFlopChartsEffectiveStackBb = 0;
private int _preFlopChartsLoadedCount = 0;

private static int CalculatePreFlopChartEffectiveStackBb(int[] stackSizes, int heroIndex, int bigBlindSize)
{
    if (stackSizes == null || stackSizes.Length < 2)
        throw new ArgumentException("BotGameState: stackSizes invalido para selecionar charts preflop.");

    if (bigBlindSize <= 0)
        throw new ArgumentException("BotGameState: bigBlindSize invalido para selecionar charts preflop.");

    if (heroIndex < 0 || heroIndex >= stackSizes.Length)
        throw new ArgumentException("BotGameState: heroIndex invalido para selecionar charts preflop.");

    int heroStack = stackSizes[heroIndex];

    if (heroStack <= 0)
        throw new ArgumentException("BotGameState: stack do heroi invalido para selecionar charts preflop.");

    int maxOpponentStack = 0;

    for (int i = 0; i < stackSizes.Length; i++)
    {
        if (i == heroIndex)
            continue;

        if (stackSizes[i] > maxOpponentStack)
            maxOpponentStack = stackSizes[i];
    }

    if (maxOpponentStack <= 0)
        throw new ArgumentException("BotGameState: stacks dos oponentes invalidos para selecionar charts preflop.");

    int effectiveStack = Math.Min(heroStack, maxOpponentStack);

    int effectiveStackBb = (effectiveStack + (bigBlindSize / 2)) / bigBlindSize;

    if (effectiveStackBb <= 0)
        effectiveStackBb = 1;

    return effectiveStackBb;
}

private static string SelectPreFlopChartsFolder(string assemblyFolder, int effectiveStackBb, out string selectedBucket)
{
    if (string.IsNullOrWhiteSpace(assemblyFolder))
        throw new ArgumentException("BotGameState: assemblyFolder vazio ao selecionar charts preflop.");

    string chartsRoot = Path.Combine(assemblyFolder, "PreFlopCharts");

    int target = effectiveStackBb <= 150 ? 100 : 200;
    string targetBucket = target + "bb";
    string targetPath = Path.Combine(chartsRoot, targetBucket);

    if (Directory.Exists(targetPath))
    {
        selectedBucket = targetBucket;
        return targetPath;
    }

    int fallback = target == 100 ? 200 : 100;
    string fallbackBucket = fallback + "bb";
    string fallbackPath = Path.Combine(chartsRoot, fallbackBucket);

    if (Directory.Exists(fallbackPath))
    {
        selectedBucket = fallbackBucket + $" (fallback; alvo era {targetBucket})";
        return fallbackPath;
    }

    throw new DirectoryNotFoundException(
        $"BotGameState: nenhuma pasta de charts preflop encontrada. " +
        $"Esperado: {targetPath} ou {fallbackPath}");
}

public string getPreFlopChartsInfo()
{
    int ignored = (_preFlopCharts == null) ? 0 : _preFlopCharts.IgnoredCount;

    return $"bucket={_preFlopChartsBucket}, effectiveStack={_preFlopChartsEffectiveStackBb}bb, files={_preFlopChartsLoadedCount}, ignored={ignored}, path={_preFlopChartsPath}";
}

        public int smallBlindInd()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PreFlopPosition == Position.SB)
                    return i;
            }

            return -1;
        }

        public int bigBlindInd()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].PreFlopPosition == Position.BB)
                    return i;
            }

            return -1;
        }

        public BotGameState(string[] playerNames,
            int[] stackSizes,
            int heroIndex, 
            int buttonInd, 
            int bigBlingSize, 
            PokerClient client, 
            TableType tableType, 
            Estimators.IActionEstimator actionEstimator,
            bool randomlySampleActions = false,
            int preFlopChartsLevel=4)
        {
            if (playerNames.Count() != stackSizes.Count())
                throw new Exception("Length of playerNames and stackSizes arrays must be the same");

            _actionEstimator = actionEstimator;
            _randomlySampleActions = randomlySampleActions;

            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            _preFlopChartsEffectiveStackBb = CalculatePreFlopChartEffectiveStackBb(stackSizes, heroIndex, bigBlingSize);
            _preFlopChartsPath = SelectPreFlopChartsFolder(assemblyFolder, _preFlopChartsEffectiveStackBb, out _preFlopChartsBucket);
            _preFlopCharts = new PreFlopCharts(_preFlopChartsPath);
            _preFlopChartsLoadedCount = _preFlopCharts.LoadedCount;

            _tableType = tableType;
            _players = new List<Player>();
            _board = new Board();
            _heroHoleCards = new HoleCards(0);

            _bigBlingSize = bigBlingSize;
            _pokerClient = client;
            _heroInd = heroIndex;
            _buttonInd = buttonInd;

            for (int i=0; i< playerNames.Count(); i++)
            {
                Position position = (Position)i;
                _players.Add(new Player(playerNames[i], stackSizes[i], null, position));
            }

            _preFlopChartsLevel = preFlopChartsLevel;
        }

        /// <summary>
        /// Sets upplayer positions (smallblind, bigblind, button). Return player to act index.
        /// </summary>
        /// <param name="sittingOutPlayers"></param>
        /// <returns></returns>
        private int setupPlayerPositions(List<int> sittingOutPlayers)
        {
            int numPlayersInPlay = _players.Count - sittingOutPlayers.Count;
            List<Position> positionsToAssign = new List<Position>();
            Position playerToActPosition = Position.Empty;

            if (numPlayersInPlay < 2)
            {
                return -1;
            }
            else if (numPlayersInPlay == 2)
            {
                positionsToAssign = new List<Position>
                {
                    Position.SB,
                    Position.BB
                };

                playerToActPosition = Position.SB;
            }
            else if (numPlayersInPlay == 3)
            {
                positionsToAssign = new List<Position>
                {
                    Position.BU,
                    Position.SB,
                    Position.BB
                };

                playerToActPosition = Position.BU;
            }
            else if (numPlayersInPlay == 4)
            {
                positionsToAssign = new List<Position>
                {
                    Position.BU,
                    Position.SB,
                    Position.BB,
                    Position.CO,
                };

                playerToActPosition = Position.CO;
            }
            else if (numPlayersInPlay == 5)
            {
                positionsToAssign = new List<Position>
                {
                    Position.BU,
                    Position.SB,
                    Position.BB,
                    Position.HJ,
                    Position.CO,
                };

                playerToActPosition = Position.HJ;
            }
            else if (numPlayersInPlay == 6)
            {
                positionsToAssign = new List<Position>
                {
                    Position.BU,
                    Position.SB,
                    Position.BB,
                    Position.UTG,
                    Position.HJ,
                    Position.CO,
                };

                playerToActPosition = Position.UTG;
            }

            int ind = _buttonInd;

            while (positionsToAssign.Count > 0)
            {
                if (positionsToAssign[0] == Position.BU) // Even sittingOutPlayers can be dealer so just assign dealer!
                {
                    _players[ind].PreFlopPosition = Position.BU;
                    positionsToAssign.RemoveAt(0);
                }
                else if (sittingOutPlayers.Contains(ind))
                {
                    _players[ind].PreFlopPosition = Position.Empty;
                }
                else
                {
                    _players[ind].PreFlopPosition = positionsToAssign[0];
                    positionsToAssign.RemoveAt(0);
                }

                ind = (ind + 1) % _players.Count;
            }

            for (int i = 0; i <= _players.Count; i++)
            {
                if (_players[i].PreFlopPosition == playerToActPosition)
                    return i;
            }

            return -1;
        }

        public void startNewHand(List<int> sittingOutPlayers=null)
        {
            _street = Street.PreFlop;
            _numBets = 0;
            _numCallers = 0;
            _bettors = new List<Position>();

            if (sittingOutPlayers == null)
                sittingOutPlayers = new List<int>();

            _actionEstimator.newHand(this);

            foreach (Player player in _players)
                player.ResetHand();

            for (int i = 0; i < _players.Count; i++)
            {
                if (sittingOutPlayers.Contains(i))
                    _players[i].StatusInHand = Status.Folded;
            }

            Debug.Assert(_players.Count >= 2);

            // Setup player positions (button, smallblind, bigblind
            _playerToActInd = setupPlayerPositions(sittingOutPlayers);

            _currentHand = new Hand
            {
                Client = _pokerClient,
                GameType = GameType.HoldEm,
                BigBlindSize = _bigBlingSize,
                HeroName = _players[_heroInd].Name
            };

            if (_players.Count == 2)
            {
                if (smallBlindInd() > -1)
                    _currentHand.addPlayer(_players[smallBlindInd()].Name, _players[smallBlindInd()].Stack);

                if (bigBlindInd() > -1)
                    _currentHand.addPlayer(_players[bigBlindInd()].Name, _players[bigBlindInd()].Stack);
            }
            else
            {
                for (int i = 1; i <= _players.Count; i++)
                {
                    int ind = (_buttonInd + i) % _players.Count;
                    _currentHand.addPlayer(_players[ind].Name, _players[ind].Stack);
                }
            }

            _board = new Board();

            if (smallBlindInd() > -1)
                _players[smallBlindInd()].Posts(_bigBlingSize / 2);

            if (bigBlindInd() > -1)
                _players[bigBlindInd()].Posts(_bigBlingSize);
        }

        public void finishHand(List<int> winnings)
        {
            Debug.Assert(_players.Count == winnings.Count);

            for (int i = 0; i < winnings.Count; i++)
            {
                if (winnings[i] > 0)
                {
                    _currentHand.addAction(_street, _players[i].Name, ActionType.Wins, winnings[i]);
                    _players[i].WinsHand(winnings[i]);
                }
            }

            var playerSaldo = new Dictionary<string, int>();

            foreach (var player in _players)
                playerSaldo[player.Name] = player.MoneyWon - player.MoneyInPot;

            _currentHand.addPlayerWinnings(playerSaldo);
            _buttonInd = (_buttonInd + 1) % _players.Count;
        }

        public TableType getTableType()
        {
            return _tableType;
        }

        public int getBigBlingSize()
        {
            return _bigBlingSize;
        }

        public Hand getCurrentHand()
        {
            return _currentHand;
        }

        public Street getStreet()
        {
            return _street;
        }

        public int getPlayerToActInd()
        {
            return _playerToActInd;
        }

        public int getButtonInd()
        {
            return _buttonInd;
        }

        public void setButtonInd(int index)
        {
            _buttonInd = index;
        }

        public List<Player> getPlayers()
        {
            return _players;
        }

        public Player getHero()
        {
            return _players[_heroInd];
        }

        public int getHeroInd()
        {
            return _heroInd;
        }

        public HoleCards getHeroHoleCards()
        {
            return _heroHoleCards;
        }

        public Board getBoard()
        {
            return _board;
        }

        /// <summary>
        ///  Calculates the hand strength of the hero cards.
        /// </summary>
        public HandStrength calculateHeroHandStrength()
        {
            return HandStrength.calculateHandStrength(_heroHoleCards, _board);
        }

        public int getNumBets()
        {
            return _numBets;
        }

        public List<Position> getBettors()
        {
            return _bettors;
        }

        public int getNumCallers()
        {
            return _numCallers;
        }

        public int potSize()
        {
            int sum = 0;

            foreach (var player in _players)
                sum += player.MoneyInPot;

            return sum;
        }

        public int getMaxMoneyInThePot()
        {
            int max = 0;

            foreach (var player in _players)
            {
                if (max < player.MoneyInPot)
                    max = player.MoneyInPot;
            }

            return max;
        }

        public int getRaiseAmount()
        {
            int amountToCall = getMaxMoneyInThePot() - getPlayerToAct().MoneyInPot;

            if (_street == Street.PreFlop)
            {
                return potSize() + 2 * amountToCall;
            }
            else
            {
                return (RAISE_SIZE_NOM * (potSize() + amountToCall)) / RAISE_SIZE_DEN + amountToCall;
            }
        }

        public int getAmountToCall()
        {
            int amountToCall = getMaxMoneyInThePot() - getPlayerToAct().MoneyInPot;
            int playerToActStack = getPlayerToAct().Stack;

            if (amountToCall >= playerToActStack)
                amountToCall = playerToActStack;

            return amountToCall;
        }

        private static float SafeRatio(int numerator, int denominator)
        {
            if (denominator <= 0)
                return 0.0f;

            return numerator / (float)denominator;
        }

        private void captureActionSizingContext(ActionType actionType, int actionAmount, bool isAllIn)
        {
            if (_playerToActInd < 0 || _playerToActInd >= _players.Count)
            {
                _lastActionSizingContext = new ActionSizingContext
                {
                    ActionType = actionType,
                    ActionAmount = actionAmount,
                    BigBlindSize = _bigBlingSize,
                    IsAllIn = isAllIn
                };
                return;
            }

            Player player = getPlayerToAct();
            int potBeforeAction = potSize();
            int amountToCall = getAmountToCall();
            int stackBeforeAction = player.Stack;
            int maxMoneyInPot = getMaxMoneyInThePot();
            int effectiveActionAmount = Math.Max(0, Math.Min(actionAmount, stackBeforeAction));

            _lastActionSizingContext = new ActionSizingContext
            {
                ActionType = actionType,
                ActionAmount = effectiveActionAmount,
                AmountToCall = amountToCall,
                PotBeforeAction = potBeforeAction,
                StackBeforeAction = stackBeforeAction,
                MoneyInPotBeforeAction = player.MoneyInPot,
                MaxMoneyInPotBeforeAction = maxMoneyInPot,
                BigBlindSize = _bigBlingSize,
                IsAllIn = isAllIn || effectiveActionAmount >= stackBeforeAction,
                BetToPotRatio = SafeRatio(effectiveActionAmount, Math.Max(1, potBeforeAction + amountToCall)),
                CallToPotRatio = SafeRatio(amountToCall, Math.Max(1, potBeforeAction + amountToCall)),
                StackCommitment = SafeRatio(effectiveActionAmount, Math.Max(1, stackBeforeAction)),
                SprBeforeAction = SafeRatio(stackBeforeAction, Math.Max(1, potBeforeAction))
            };
        }

        public ActionSizingContext getLastActionSizingContext()
        {
            return _lastActionSizingContext;
        }

        public Player getPlayerToAct()
        {
            return _players[_playerToActInd];
        }

        public int numActivePlayers()
        {
            int activePlayers = 0;

            foreach (var player in _players)
            {
                if (player.StatusInHand != Status.Folded)
                    activePlayers++;
            }

            return activePlayers;
        }

        public int numActiveNonAllInPlayers()
        {
            int activePlayers = 0;

            foreach (var player in _players)
            {
                if (player.StatusInHand != Status.Folded && player.StatusInHand != Status.AllIn)
                    activePlayers++;
            }

            return activePlayers;
        }

        private float getPossibleWinnings(int bigBlindSize)
        {
            int winnings = 0;
            int maxOppMoneyInThePot = 0;

            for (int i = 0; i < _players.Count; i++)
            {
                winnings += Math.Min(_players[i].MoneyInPot, _players[_heroInd].MoneyInPot);

                if (i != _heroInd)
                {
                    maxOppMoneyInThePot = Math.Max(maxOppMoneyInThePot, _players[i].MoneyInPot);
                }
            }

            int rakableWinnings = winnings - Math.Max(_players[_heroInd].MoneyInPot - maxOppMoneyInThePot, 0);
            int rake;

            if (bigBlindSize <= 10)
            {
                rake = rakableWinnings / 10;
                rake = Math.Min(rake, 10);
            }
            else
            {
                rake = rakableWinnings / 15;
            }

            winnings -= rake;
            return (float)winnings;
        }

        public void playerBringsIn(int playerInd, int ammont)
        {
            _players[playerInd].BringsIn(ammont);
        }

        public void dealHoleCards(HoleCards holeCards)
        {
            dealHoleCards(holeCards.Card0, holeCards.Card1);
        }

        public void dealHoleCards(Card card0, Card card1)
        {
            Debug.Assert(_street == Street.PreFlop);
            _heroHoleCards = new HoleCards(card0, card1);
            _currentHand.setHoleCardsHoldem(card0, card1);

            for (int i = 0; i < _players.Count; i++)
            {
                if (i != _heroInd)
                {
                    _players[i].BanCardInRange(card0, false);
                    _players[i].BanCardInRange(card1, false);
                }
            }
        }

        private void goToFirstPlayer()
        {
            if (numActivePlayers() <= 1)
            {
                _playerToActInd = -1;
                return;
            }

            for (int i = 1; i <= _players.Count; i++)
            {
                int ind = (_buttonInd + i) % _players.Count;

                if (_players[ind].StatusInHand == Status.ToAct)
                {
                    _playerToActInd = ind;
                    return;
                }
            }

            _playerToActInd = -1;
        }

        public void goToNextStreet(Card card)
        {
            goToNextStreet(new List<Card>(new Card[] { card }));
        }

        private void ValidateNextStreetCards(List<Card> cards)
        {
            if (!(_street == Street.PreFlop || _street == Street.Flop || _street == Street.Turn))
                throw new InvalidOperationException($"goToNextStreet invalido: street atual={_street}.");

            if (cards == null)
                throw new ArgumentNullException(nameof(cards));

            int expectedCards = (_street == Street.PreFlop) ? 3 : 1;

            if (cards.Count != expectedCards)
                throw new InvalidOperationException(
                    $"goToNextStreet invalido: street atual={_street}, cartas recebidas={cards.Count}, esperado={expectedCards}.");

            HashSet<int> seen = new HashSet<int>();

            foreach (Card boardCard in _board.Cards)
                seen.Add(boardCard.ToInt());

            foreach (Card card in cards)
            {
                if (card.rank == Card.Rank.Unknown || card.suit == Card.Suit.Unknown)
                    throw new InvalidOperationException("goToNextStreet recebeu carta invalida.");

                int cardIndex = card.ToInt();

                if (seen.Contains(cardIndex))
                    throw new InvalidOperationException($"goToNextStreet recebeu carta duplicada no board: {card}.");

                seen.Add(cardIndex);
            }
        }

        public void goToNextStreet(List<Card> cards)
        {
            ValidateNextStreetCards(cards);

            // Reset acted players after street
            int acted = 0;
            int allin = 0;

            foreach (var player in _players)
            {
                if (player.StatusInHand == Status.Acted)
                    acted++;

                if (player.StatusInHand == Status.AllIn)
                    allin++;
            }

            // Svi su AllIn osim jednog koji ima vise novca - On postaje AllIn takodje.
            if (acted == 1 && allin > 0)
            {
                foreach (var player in _players)
                {
                    if (player.StatusInHand == Status.Acted)
                        player.StatusInHand = Status.AllIn;
                }
            }

            foreach (var player in _players)
            {
                if (player.StatusInHand == Status.Acted)
                    player.StatusInHand = Status.ToAct;
            }

            // Players go to next street
            foreach (var player in _players)
            {
                player.NextStreet();

                foreach (Card card in cards)
                    player.BanCardInRange(card, true);
            }

            // Add card to board
            foreach (Card card in cards)
            {
                _currentHand.Board.AddCard(card);
                _board.AddCard(card);
            }

            // Set other parameters
            goToFirstPlayer();
            _street = _street + 1;
            _numBets = 0;
            _bettors = new List<Position>();
           _numCallers = 0;

            if (_street == Street.Flop)
                _actionEstimator.flopShown(getBoard(), getHeroHoleCards());
        }

        private void goToNextPlayer()
        {
            if (numActivePlayers() <= 1)
            {
                _playerToActInd = -1;
                return;
            }

            for (int i = _playerToActInd + 1; i < _players.Count; i++)
            {
                if (_players[i].StatusInHand == Status.ToAct)
                {
                    _playerToActInd = i;
                    return;
                }
            }

            for (int i = 0; i < _playerToActInd; i++)
            {
                if (_players[i].StatusInHand == Status.ToAct)
                {
                    _playerToActInd = i;
                    return;
                }
            }

            _playerToActInd = -1;
        }

        public bool isPlayerInPosition(int playerIndex)
        {
            // Button is in position in each situation
            if (playerIndex == _buttonInd)
                return true;

            // For each opponent up to button
            for (int i = 1; i < _players.Count; i++)
            {
                int oppInd = (playerIndex + i) % _players.Count;
                Debug.Assert(oppInd != playerIndex);

                if (_players[oppInd].StatusInHand == Status.ToAct || _players[oppInd].StatusInHand == Status.Acted)
                    return false;

                if (oppInd == _buttonInd)
                    break;
            }

            return true;
        }

        public ActionType playerActs(ActionType actionType, int byAmount)
        {
            if (actionType == ActionType.Fold)
            {
                playerFolds();
                return ActionType.Fold;
            }
            else if (actionType == ActionType.Check || actionType == ActionType.Call)
            {
                return playerCheckCalls();
            }
            else if (actionType == ActionType.AllIn)
            {
                int amountToCall = getAmountToCall();

                if (amountToCall > 0 && (amountToCall >= getPlayerToAct().Stack || byAmount <= amountToCall))
                    return playerCheckCalls();

                return playerBetRaisesBy(getPlayerToAct().Stack);
            }
            else
            {
                return playerBetRaisesBy(byAmount);
            }
        }

        public ActionType playerCheckCalls()
        {
            int amountToCall = getAmountToCall();
            ActionType actionType;

            if (amountToCall >= getPlayerToAct().Stack)
            {
                amountToCall = getPlayerToAct().Stack;

                // Important that this is before player state changes (getPlayerToAct().GoesAllIn(...))
                // This is an all-in call, not an all-in raise. Keep the range-cut branch as Call,
                // while the sizing context records full stack commitment.
                captureActionSizingContext(ActionType.Call, amountToCall, true);
                _actionEstimator.newAction(ActionType.Call, this);

                getPlayerToAct().GoesAllIn();
                actionType = ActionType.AllIn;
                _numCallers++;
            }
            else if (amountToCall > 0) // Ima betova
            {
                // Important that this is before player state changes (getPlayerToAct().Calls(...))
                captureActionSizingContext(ActionType.Call, amountToCall, false);
                _actionEstimator.newAction(ActionType.Call, this);

                getPlayerToAct().Calls(amountToCall);
                actionType = ActionType.Call;
                _numCallers++;
            }
            else
            {
                // Important that this is before player state changes (getPlayerToAct().Checks())
                captureActionSizingContext(ActionType.Check, 0, false);
                _actionEstimator.newAction(ActionType.Check, this);

                getPlayerToAct().Checks();
                actionType = ActionType.Check;
            }

            //Console.WriteLine($"{getPlayerToAct().Name} checks/calls: {amountToCall}");
            _currentHand.addAction(_street, getPlayerToAct().Name, actionType, amountToCall);

            goToNextPlayer();
            return actionType;
        }

        private void resetActedPlayersAfterRaise()
        {
            int newAmountInPot = getMaxMoneyInThePot();

            foreach (var player in _players)
            {
                if (player.MoneyInPot < newAmountInPot && player.StatusInHand == Status.Acted)
                    player.StatusInHand = Status.ToAct;
            }
        }

        public ActionType playerBetRaisesBy(int amount)
        {
            ActionType actionType = ActionType.Fold;
            var betOrRaise = (getAmountToCall() == 0) ? ActionType.Bet : ActionType.Raise;

            if (amount <= 0)
                amount = getRaiseAmount();

            if (amount >= getPlayerToAct().Stack)
            {
                amount = getPlayerToAct().Stack;

                // Important that this is before player state changes (getPlayerToAct().GoesAllIn())
                captureActionSizingContext(ActionType.AllIn, amount, true);
                _actionEstimator.newAction(ActionType.AllIn, this);

                getPlayerToAct().GoesAllIn();
                actionType = ActionType.AllIn;
            }
            else
            {
                // Important that this is before player state changes (getPlayerToAct().BetsOrRaisesTo(...))
                captureActionSizingContext(betOrRaise, amount, false);
                _actionEstimator.newAction(betOrRaise, this);

                getPlayerToAct().BetsOrRaisesTo(getPlayerToAct().MoneyInPot + amount);
                actionType = betOrRaise;
            }

            //Console.WriteLine($"{getPlayerToAct().Name} bets/raises by: {amount}");
            _currentHand.addAction(_street, getPlayerToAct().Name, actionType, amount);

            if (actionType != ActionType.AllIn)
                _bettors.Add(getPlayerToAct().PreFlopPosition);

            resetActedPlayersAfterRaise();
            goToNextPlayer();
            _numCallers = 0;
            _numBets += 1;
            

            return actionType;
        }

        public void playerFolds()
        {
            //Console.WriteLine(getPlayerToAct().Name + " folds");
            captureActionSizingContext(ActionType.Fold, 0, false);
            _actionEstimator.newAction(ActionType.Fold, this);
            _currentHand.addAction(_street, getPlayerToAct().Name, ActionType.Fold, 0);

            getPlayerToAct().Folds();
            goToNextPlayer();
        }

        public void playerGoesAllIn()
        {
            //Console.WriteLine(getPlayerToAct().Name + " goes All-in");
            int amount = getPlayerToAct().Stack;
            captureActionSizingContext(ActionType.AllIn, amount, true);
            _actionEstimator.newAction(ActionType.AllIn, this);
            _currentHand.addAction(_street, getPlayerToAct().Name, ActionType.AllIn, amount);

            getPlayerToAct().GoesAllIn();
            goToNextPlayer();
        }

        public struct BotDecision
        {
            public ActionType actionType;
            public int byAmount;
            public float checkCallEV;
            public float betRaiseEV;
            public double timeSpentSeconds;
            public string message;
        }

        private ActionType randomSampleAction(float brEv, float ccEv)
        {
            // If one EV is less than 0 just return other one
            if (brEv < 0)
                return ActionType.Call;

            if (ccEv < 0)
                return ActionType.Raise;

            // Normalize to sum of 1
            float brProb = brEv / (brEv + ccEv);
            float ccProb = ccEv / (brEv + ccEv);

            // For very small probabilities just choose other one
            if (brProb < 0.1)
                return ActionType.Call;

            if (ccProb < 0.1)
                return ActionType.Raise;

            // Otherwise randomize
            if ((float)_rng.NextDouble() < brProb)
            {
                return ActionType.Raise;
            }
            else
            {
                return ActionType.Call;
            }
        }

        /// <summary>
        /// Problematic sutuation we have is after flop when we are in position and opponent checks we are always raising.
        /// </summary>
        /// <returns></returns>
        private bool isProblematicSituation(float betRaiseEV, float checkCallEV)
        {
            if ((_street == Street.Flop || _street == Street.Turn) && 
                numActivePlayers() == 2 &&
                isPlayerInPosition(_heroInd) &&
                getAmountToCall() == 0)
            {
                var handStrength = HandStrength.calculateHandStrength(_heroHoleCards, _board);

                // Problematic situation is only when we dont have anything (High card) and want to raise.
                return betRaiseEV > checkCallEV && checkCallEV > 0 && handStrength.rank == HandRank.HighCard;
            }

            return false;
        }


        private float calculateBoardWetnessForSizing()
        {
            if (_board == null || _board.Count < 3)
                return 0.0f;

            int[] rankCount = new int[15];
            int[] suitCount = new int[4];
            bool[] ranks = new bool[15];

            foreach (var card in _board.Cards)
            {
                int r = (int)card.rank;
                int s = (int)card.suit;

                if (r >= 2 && r <= 14)
                {
                    rankCount[r]++;
                    ranks[r] = true;

                    if (r == 14)
                        ranks[1] = true;
                }

                if (s >= 0 && s < 4)
                    suitCount[s]++;
            }

            bool paired = rankCount.Any(x => x >= 2);
            int maxSuit = suitCount.Max();
            bool twoTone = maxSuit == 2;
            bool monotone = maxSuit >= 3;

            int bestStraightWindow = 0;
            for (int high = 14; high >= 5; high--)
            {
                int count = 0;
                for (int r = high - 4; r <= high; r++)
                {
                    if (ranks[r])
                        count++;
                }

                if (count > bestStraightWindow)
                    bestStraightWindow = count;
            }

            float wetness = 0.0f;

            if (monotone)
                wetness += 0.35f;
            else if (twoTone)
                wetness += 0.18f;

            if (bestStraightWindow >= 3)
                wetness += 0.25f;

            if (paired)
                wetness += 0.10f;

            return Math.Max(0.0f, Math.Min(1.0f, wetness));
        }

        private int clampBetAmount(int amount)
        {
            int stack = _players[_heroInd].Stack;

            if (amount < _bigBlingSize)
                amount = _bigBlingSize;

            if (amount > stack)
                amount = stack;

            return amount;
        }

        private int calculatePostFlopBetRaiseAmount(float checkCallEV, float betRaiseEV)
        {
            int amountToCall = getAmountToCall();
            int pot = potSize();
            int stack = _players[_heroInd].Stack;

            if (stack <= 0)
                return 0;

            if (_street == Street.PreFlop)
                return getRaiseAmount();

            float edge = betRaiseEV - checkCallEV;
            float wetness = calculateBoardWetnessForSizing();
            HandStrength handStrength = HandStrength.calculateHandStrength(_heroHoleCards, _board);
            bool hasStrongMadeHand = handStrength.rank >= HandRank.TwoPair;
            bool hasWeakShowdown = handStrength.rank <= HandRank.HighCard;

            int sizingBase = Math.Max(1, pot + amountToCall);
            float fraction;

            if (amountToCall == 0)
            {
                if (hasStrongMadeHand && wetness >= 0.35f)
                    fraction = 0.75f;
                else if (hasStrongMadeHand)
                    fraction = 0.60f;
                else if (hasWeakShowdown && edge > 0.0f)
                    fraction = 0.33f;
                else if (wetness >= 0.45f)
                    fraction = 0.55f;
                else
                    fraction = 0.45f;
            }
            else
            {
                if (hasStrongMadeHand && wetness >= 0.35f)
                    fraction = 0.95f;
                else if (hasStrongMadeHand)
                    fraction = 0.80f;
                else if (edge > amountToCall)
                    fraction = 0.75f;
                else
                    fraction = 0.60f;
            }

            int amount = amountToCall + (int)Math.Round(sizingBase * fraction);

            return clampBetAmount(amount);
        }


        public BotDecision calculateHeroAction()
        {
            int nOfOpponents = numActivePlayers() - 1;

            BotDecision bd = new BotDecision
            {
                actionType = ActionType.Fold,
                byAmount = 0,
                betRaiseEV = 0.0f,
                checkCallEV = 0.0f,
                timeSpentSeconds = 0,
                message = ""
            };

            if (_playerToActInd != _heroInd)
            {
                bd.actionType = ActionType.NoAction;
                bd.message = "Player to act is not hero";
                return bd;
            }

            if (nOfOpponents == 0)
            {
                bd.message = "Number of opponents is 0";
                bd.actionType = ActionType.Check;
                return bd;
            }

            var startTime = DateTime.Now;
            int amountToCall = getAmountToCall();

            // If we are post flop with many opponents than its too time consumming to calculate.
            if (nOfOpponents < 4 || _street == Street.PreFlop)
            {
                _actionEstimator.estimateEV(out bd.checkCallEV, out bd.betRaiseEV, this);
            }
            else
            {
                // If amountToCall is 0, it can check
                bd.checkCallEV = -amountToCall;
                bd.betRaiseEV = -10.0f;
            }

            // Try to read preflop charts
            var pfcActionDistribution = _preFlopCharts.GetActionDistribution(this, _preFlopChartsLevel);

            if (pfcActionDistribution != null)
            {
                var heroPos = getHero().PreFlopPosition;
                var villainPos = (_bettors.Count > 0) ? _bettors.Last() : Position.Empty;

                bd.message += $" -> We have pre-flop chart for this situation (Hero pos {heroPos}, villianPos {villainPos}, num bets {_numBets}, num callers {_numCallers}).\n";
bd.message += $" -> Preflop chart set: {getPreFlopChartsInfo()}.\n";
bd.message += $" -> Chart lookup: {_preFlopCharts.LastLookupInfo}.\n";
bd.message += $" -> We are reading AD, allin prob {pfcActionDistribution.allinProb} br prob {pfcActionDistribution.brProb}, cc prob {pfcActionDistribution.ccProb} (Modeling estimator gave br {bd.betRaiseEV:F2} cc {bd.checkCallEV:F2}).\n";
                bd.actionType = pfcActionDistribution.sample(_rng);
                bd.message += $" -> Sampled action is {bd.actionType}";
            }
            else
            {
                if (_street == Street.PreFlop)
                {
                    var heroPos = getHero().PreFlopPosition;
                    var villainPos = (_bettors.Count > 0) ? _bettors.Last() : Position.Empty;
                    bd.message += $" -> We do NOT have pre-flop chart for this situation (Hero pos {heroPos}, villianPos {villainPos}, num bets {_numBets}, num callers {_numCallers}).\n";
                    bd.message += $" -> Preflop chart set: {getPreFlopChartsInfo()}.\n";

if (_numBets == 0 && _numCallers > 0)
{
    bd.message += $" -> Spot identificado como open limp/limp antes do heroi.\n";
    bd.message += $" -> Chart lookup: {_preFlopCharts.LastLookupInfo}.\n";
}
                }

                bd.message += $" -> Using modeling estimator result br {bd.betRaiseEV:F2} cc {bd.checkCallEV:F2}.\n";

                if (bd.checkCallEV < 0 && bd.betRaiseEV <= 0)
                {
                    bd.actionType = ActionType.Fold;
                    bd.message += " -> Both EVs are less then 0 so fold.\n";
                }
                /*else if (isProblematicSituation(bd.betRaiseEV, bd.checkCallEV))
                {
                    // In case of problematic situation mix strategy a bit.
                    bd.actionType = randomSampleAction(bd.betRaiseEV, bd.checkCallEV);
                    bd.message += $" -> This is problematic situation in position after flop after opponent checks. Mix strategy. Randomly sampling {bd.actionType}.\n";
                }*/
                else if (_randomlySampleActions && bd.checkCallEV > 0 && bd.betRaiseEV > 0)
                {
                    bd.actionType = randomSampleAction(bd.betRaiseEV, bd.checkCallEV);
                    bd.message += $" -> Both EVs are positive. Randomly sampling {bd.actionType}.\n";
                }
                else if (bd.checkCallEV > bd.betRaiseEV)
                {
                    bd.actionType = ActionType.Call;
                    bd.message += " -> Check/call EV is positive and larger than bet/raise EV so check/call.\n";
                }
                else
                {
                    bd.actionType = ActionType.Raise;
                    bd.message += " -> Bet/raise EV is positive and larger than check/call EV so bet/raise.\n";
                }
            }

            bd.timeSpentSeconds = (DateTime.Now - startTime).TotalSeconds;
            bd.byAmount = 0;

            // If both EVs are less than zero then fold
            if (bd.actionType == ActionType.Fold)
            {
                bd.byAmount = 0;

                if (amountToCall == 0)
                {
                    bd.message += " -> But amount to call is 0 so check.\n";
                    bd.actionType = ActionType.Check;
                }
            }
            else if (bd.actionType == ActionType.Call)
            {
                bd.byAmount = amountToCall;

                if (amountToCall == 0)
                {
                    bd.message += " -> AmountToCall is 0 -> check.\n";
                    bd.actionType = ActionType.Check;
                }
            }
            else // its raise
            {
                bd.byAmount = calculatePostFlopBetRaiseAmount(bd.checkCallEV, bd.betRaiseEV);
            }

            if ((3 * bd.byAmount / 2) >= _players[_heroInd].Stack)
            {
                bd.message += " -> But amount to put in pot is close to (or larger than) players stack so go all in!\n";
                bd.byAmount = _players[_heroInd].Stack;
                bd.actionType = ActionType.AllIn;
            }

            // Remove all leading and traiing white-space characters 
            bd.message = bd.message.Trim();

            return bd;
        }

        public void Dispose()
        {
            _actionEstimator.Dispose();
        }
    }
}
