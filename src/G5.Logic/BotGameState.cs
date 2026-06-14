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
        private static bool _fastPostFlopEVEnabled = true;
        private static bool _allowAllInByCommitment = true;
        private static int _allInCommitmentPercent = 66;
        private static bool _allowHighSprAllInCandidate = false;
        private static float _maxSprForAllInCandidate = 1.25f;

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
        private readonly PostFlopLineHistory _executionLineHistory = new PostFlopLineHistory();
        private readonly LineContextClassifier _executionLineClassifier = new LineContextClassifier();
        private string _lastExecutionSizingReason = "";

// Options
private bool _randomlySampleActions;

private string _preFlopChartsPath = "";
private string _preFlopChartsBucket = "";
private int _preFlopChartsEffectiveStackBb = 0;
private int _preFlopChartsLoadedCount = 0;

private static readonly ConcurrentDictionary<string, PreFlopCharts> _preFlopChartsCache =
    new ConcurrentDictionary<string, PreFlopCharts>(StringComparer.OrdinalIgnoreCase);

private static PreFlopCharts GetCachedPreFlopCharts(string path)
{
    if (string.IsNullOrWhiteSpace(path))
        throw new ArgumentException("BotGameState: path vazio para cache de charts preflop.");

    string fullPath = Path.GetFullPath(path);

    return _preFlopChartsCache.GetOrAdd(fullPath, p => new PreFlopCharts(p));
}

public static void ConfigureRuntimeOptions(
    bool fastPostFlopEVEnabled,
    bool allowAllInByCommitment,
    int allInCommitmentPercent,
    bool allowHighSprAllInCandidate,
    double maxSprForAllInCandidate)
{
    _fastPostFlopEVEnabled = fastPostFlopEVEnabled;
    _allowAllInByCommitment = allowAllInByCommitment;

    if (allInCommitmentPercent < 1)
        allInCommitmentPercent = 1;

    if (allInCommitmentPercent > 100)
        allInCommitmentPercent = 100;

    _allInCommitmentPercent = allInCommitmentPercent;
    _allowHighSprAllInCandidate = allowHighSprAllInCandidate;

    if (double.IsNaN(maxSprForAllInCandidate) || double.IsInfinity(maxSprForAllInCandidate) || maxSprForAllInCandidate <= 0.0)
        maxSprForAllInCandidate = 1.25;

    if (maxSprForAllInCandidate > 20.0)
        maxSprForAllInCandidate = 20.0;

    _maxSprForAllInCandidate = (float)maxSprForAllInCandidate;
}

public static bool IsFastPostFlopEVEnabled()
{
    return _fastPostFlopEVEnabled;
}

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
            _preFlopCharts = GetCachedPreFlopCharts(_preFlopChartsPath);
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

            for (int i = 0; i < _players.Count; i++)
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
            _executionLineHistory.ResetForNewHand();

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
            int heroInPot = getPlayerToAct().MoneyInPot;
            int heroStack = getPlayerToAct().Stack;
            int maxInPot = getMaxMoneyInThePot();
            int amountToCall = maxInPot - heroInPot;

            if (amountToCall < 0)
                amountToCall = 0;

            if (heroStack <= 0)
                return 0;

            if (amountToCall > heroStack)
                amountToCall = heroStack;

            if (_street == Street.PreFlop)
            {
                if (_bigBlingSize <= 0)
                    return amountToCall;

                int targetTotal;

                if (_numBets <= 0)
                {
                    int callers = _numCallers;

                    if (callers < 0)
                        callers = 0;

                    targetTotal = (3 + callers) * _bigBlingSize;
                }
                else
                {
                    targetTotal = 3 * maxInPot;
                }

                int amountToAdd = targetTotal - heroInPot;

                if (amountToAdd < amountToCall)
                    amountToAdd = amountToCall;

                if (amountToAdd > heroStack)
                    amountToAdd = heroStack;

                return amountToAdd;
            }

            return (RAISE_SIZE_NOM * (potSize() + amountToCall)) / RAISE_SIZE_DEN + amountToCall;
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

        private void observeExecutionLineContext(ActionType actionType)
        {
            if (_executionLineHistory == null || _executionLineClassifier == null)
                return;

            try
            {
                _executionLineHistory.EnsureStreet(_street);

                PostFlopLineContext context = null;

                if (_street != Street.PreFlop)
                    context = _executionLineClassifier.Classify(this, actionType, _executionLineHistory);

                _executionLineHistory.ObserveAction(this, actionType, context);
            }
            catch
            {
                // A classificacao topologica nunca deve quebrar a maquina de estados.
                // Se houver inconsistencia transitoria, a arvore e o range update continuam usando os caminhos ja existentes.
            }
        }

        private PostFlopLineContext classifyPlannedHeroExecutionLine(ActionType plannedAction, int plannedAmount)
        {
            if (_street == Street.PreFlop)
                return null;

            ActionSizingContext previousSizing = _lastActionSizingContext;

            try
            {
                captureActionSizingContext(plannedAction, plannedAmount, plannedAmount >= getHero().Stack);
                _executionLineHistory.EnsureStreet(_street);
                return _executionLineClassifier.Classify(this, plannedAction, _executionLineHistory);
            }
            catch
            {
                return new PostFlopLineContext
                {
                    Street = _street,
                    ObservedAction = plannedAction,
                    ActorIndex = _playerToActInd,
                    HeroIndex = _heroInd,
                    ActorIsHero = _playerToActInd == _heroInd,
                    ActorInPosition = _playerToActInd >= 0 ? isPlayerInPosition(_playerToActInd) : false,
                    FacingBet = getAmountToCall() > 0,
                    IsMultiway = numActivePlayers() >= 3,
                    NumActivePlayers = numActivePlayers(),
                    NumBetsThisStreetBeforeAction = getNumBets(),
                    SizeClass = plannedAmount >= getHero().Stack ? PostFlopSizeClass.AllIn : PostFlopSizeClass.Normal,
                    BoardTexture = BoardTextureClass.Unknown,
                    BetToPotRatio = previousSizing.BetToPotRatio,
                    CallToPotRatio = previousSizing.CallToPotRatio,
                    StackCommitment = previousSizing.StackCommitment,
                    Spr = previousSizing.SprBeforeAction,
                    PotBeforeAction = potSize(),
                    AmountToCall = getAmountToCall(),
                    ActionAmount = plannedAmount,
                    LineClass = getAmountToCall() > 0 ? PostFlopLineClass.GenericRaise : PostFlopLineClass.GenericBet,
                    Reason = "fallback: classificador topologico indisponivel"
                };
            }
            finally
            {
                _lastActionSizingContext = previousSizing;
            }
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
            _executionLineHistory.EnsureStreet(_street);
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
                observeExecutionLineContext(ActionType.Call);

                getPlayerToAct().GoesAllIn();
                actionType = ActionType.AllIn;
                _numCallers++;
            }
            else if (amountToCall > 0) // Ima betova
            {
                // Important that this is before player state changes (getPlayerToAct().Calls(...))
                captureActionSizingContext(ActionType.Call, amountToCall, false);
                _actionEstimator.newAction(ActionType.Call, this);
                observeExecutionLineContext(ActionType.Call);

                getPlayerToAct().Calls(amountToCall);
                actionType = ActionType.Call;
                _numCallers++;
            }
            else
            {
                // Important that this is before player state changes (getPlayerToAct().Checks())
                captureActionSizingContext(ActionType.Check, 0, false);
                _actionEstimator.newAction(ActionType.Check, this);
                observeExecutionLineContext(ActionType.Check);

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
                observeExecutionLineContext(ActionType.AllIn);

                getPlayerToAct().GoesAllIn();
                actionType = ActionType.AllIn;
            }
            else
            {
                // Important that this is before player state changes (getPlayerToAct().BetsOrRaisesTo(...))
                captureActionSizingContext(betOrRaise, amount, false);
                _actionEstimator.newAction(betOrRaise, this);
                observeExecutionLineContext(betOrRaise);

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
            observeExecutionLineContext(ActionType.Fold);
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
            observeExecutionLineContext(ActionType.AllIn);
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
            public bool usedMultiSizeEV;
            public string sizingReport;
        }

        private static bool IsAtLeastAllInCommitmentThreshold(int intendedAmount, int stack)
        {
            if (!_allowAllInByCommitment)
                return false;

            if (stack <= 0 || intendedAmount <= 0)
                return false;

            return intendedAmount * 100 >= stack * _allInCommitmentPercent;
        }

        public static bool ShouldConvertToAllInByCommitmentForRegression(int intendedAmount, int stack)
        {
            return IsAtLeastAllInCommitmentThreshold(intendedAmount, stack);
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
            _lastExecutionSizingReason = "";

            if (_street == Street.PreFlop)
                return getRaiseAmount();

            int canonicalTreeAmount = getRaiseAmount();
            ActionType plannedAction = getAmountToCall() > 0 ? ActionType.Raise : ActionType.Bet;
            PostFlopLineContext context = classifyPlannedHeroExecutionLine(plannedAction, canonicalTreeAmount);

            ExecutionSizingResult sizing = ExecutionSizingPolicy.CalculatePostFlopAmount(
                this,
                context,
                canonicalTreeAmount,
                plannedAction);

            _lastExecutionSizingReason = sizing.Reason;
            return clampBetAmount(sizing.Amount);
        }


        private bool canHeroBetRaiseNow()
        {
            if (_playerToActInd != _heroInd)
                return false;

            if (_numBets >= 4)
                return false;

            if (getAmountToCall() >= getHero().Stack)
                return false;

            if (numActiveNonAllInPlayers() <= 1)
                return false;

            return true;
        }

        private bool shouldIncludeFastPostFlopAllInCandidate(int pot, int amountToCall, int stack)
        {
            if (stack <= 0)
                return false;

            if (_allowHighSprAllInCandidate)
                return true;

            int effectivePot = Math.Max(1, pot + amountToCall);
            float spr = stack / (float)effectivePot;

            // O FastEV Ã© uma aproximaÃ§Ã£o de uma street. Jam sÃ³ entra como candidato
            // natural quando o SPR jÃ¡ estÃ¡ baixo, ou quando o call jÃ¡ compromete o
            // balance restante. Em SPR alto, all-in exige Ã¡rvore futura/ranges de
            // call muito mais precisos e nÃ£o deve competir como size comum.
            if (spr <= _maxSprForAllInCandidate)
                return true;

            if (amountToCall > 0 && IsAtLeastAllInCommitmentThreshold(amountToCall, stack))
                return true;

            return false;
        }

        private List<int> buildPostFlopBetRaiseCandidates()
        {
            List<int> candidates = new List<int>();

            if (_street == Street.PreFlop || !canHeroBetRaiseNow())
                return candidates;

            int amountToCall = getAmountToCall();
            int pot = potSize();
            int stack = getHero().Stack;
            int basePot = Math.Max(1, pot + amountToCall);
            int minBet = Math.Max(1, _bigBlingSize);
            bool allowAllInCandidate = shouldIncludeFastPostFlopAllInCandidate(pot, amountToCall, stack);

            Action<int, bool> addCandidate = (amount, allowAllIn) =>
            {
                if (stack <= 0)
                    return;

                if (amountToCall > 0 && amount <= amountToCall)
                    amount = amountToCall + minBet;

                if (amountToCall == 0 && amount < minBet)
                    amount = minBet;

                if (amount >= stack)
                {
                    if (!allowAllIn)
                        return;

                    amount = stack;
                }

                if (amount <= 0)
                    return;

                if (!candidates.Contains(amount))
                    candidates.Add(amount);
            };

            if (amountToCall > 0)
            {
                addCandidate(amountToCall + (int)Math.Round(basePot * 0.50f), false);
                addCandidate(amountToCall + (int)Math.Round(basePot * 0.75f), false);
                addCandidate(amountToCall + (int)Math.Round(basePot * 1.00f), false);

                if (allowAllInCandidate)
                    addCandidate(stack, true);
            }
            else
            {
                float[] fractions = new float[] { 0.33f, 0.50f, 0.66f, 0.75f, 1.00f };

                foreach (float fraction in fractions)
                {
                    int amount = (int)Math.Round(basePot * fraction);
                    addCandidate(amount, false);
                }

                if (allowAllInCandidate)
                    addCandidate(stack, true);
            }

            candidates.Sort();
            return candidates;
        }

        private struct FastOpponentResponse
        {
            public string Name;
            public int CallAmount;
            public float FoldProb;
            public float CallProb;
            public float RaiseProb;
            public float ContinueStrength;
            public float RequiredEquity;

            public override string ToString()
            {
                return $"{Name}: fold={FoldProb:P1}, call={CallProb:P1}, raise={RaiseProb:P1}, callAmt={CallAmount}, req={RequiredEquity:P1}, contStrength={ContinueStrength:F2}";
            }
        }

        private static float ClampFloat(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return min;

            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private static float Clamp01(float value)
        {
            return ClampFloat(value, 0.0f, 1.0f);
        }

        private static float FastSigmoid(float x)
        {
            if (x > 20.0f)
                return 1.0f;

            if (x < -20.0f)
                return 0.0f;

            return 1.0f / (1.0f + (float)Math.Exp(-x));
        }

        private float estimateFastEquityRealization(int nOfOpponents, bool facingBet)
        {
            if (_street == Street.River)
                return 1.0f;

            float realization;

            if (_street == Street.Turn)
                realization = 0.86f;
            else
                realization = 0.76f;

            if (isPlayerInPosition(_heroInd))
                realization += 0.07f;
            else
                realization -= 0.05f;

            if (facingBet)
                realization -= 0.04f;

            if (nOfOpponents > 1)
                realization -= 0.05f * (nOfOpponents - 1);

            return ClampFloat(realization, 0.45f, 1.0f);
        }

        private float estimateVillainComboShowdownScore(HoleCards holeCards)
        {
            try
            {
                HandStrength hs = HandStrength.calculateHandStrength(holeCards, _board);

                // HandRank e usado como score monotonico de showdown:
                // HighCard < Pair < TwoPair < Trips < Straight < Flush < FullHouse < Quads < StraightFlush.
                float madeScore = Clamp01(((int)hs.rank) / 8.0f);

                int r0 = (int)holeCards.Card0.rank;
                int r1 = (int)holeCards.Card1.rank;

                float highCardScore = Clamp01(((r0 + r1) - 4.0f) / 24.0f);

                return Clamp01((0.92f * madeScore) + (0.08f * highCardScore));
            }
            catch
            {
                return 0.50f;
            }
        }

        private bool tryGetOpponentPostFlopModelProbabilities(int playerIndex, out float betRaiseProb, out float checkCallProb)
        {
            betRaiseProb = 0.10f;
            checkCallProb = 0.45f;

            try
            {
                if (playerIndex < 0 || playerIndex >= _players.Count)
                    return false;

                Player player = _players[playerIndex];

                if (player == null || player.Model == null)
                    return false;

                int round = player.Round();
                ActionType prevAction = (round == 0) ? player.PrevStreetAction : player.LastAction;

                PostFlopParams prms = new PostFlopParams(
                    getTableType(),
                    getStreet(),
                    round,
                    prevAction,
                    getNumBets(),
                    isPlayerInPosition(playerIndex),
                    numActivePlayers());

                EstimatedAD ad = player.GetAD(prms);

                betRaiseProb = ClampFloat(ad.BetRaise.Mean, 0.001f, 0.999f);
                checkCallProb = ClampFloat(ad.CheckCall.Mean, 0.001f, 0.999f);

                float sum = betRaiseProb + checkCallProb;

                if (sum > 0.997f)
                {
                    float scale = 0.997f / sum;
                    betRaiseProb *= scale;
                    checkCallProb *= scale;
                }

                return true;
            }
            catch
            {
                betRaiseProb = 0.10f;
                checkCallProb = 0.45f;
                return false;
            }
        }

        private FastOpponentResponse estimateFastOpponentResponseToHeroInvest(int playerIndex, int heroInvest)
        {
            Player player = _players[playerIndex];

            FastOpponentResponse response = new FastOpponentResponse
            {
                Name = player.Name,
                CallAmount = 0,
                FoldProb = 1.0f,
                CallProb = 0.0f,
                RaiseProb = 0.0f,
                ContinueStrength = 0.0f,
                RequiredEquity = 1.0f
            };

            if (player.StatusInHand == Status.Folded || player.StatusInHand == Status.AllIn)
                return response;

            int heroTargetTotal = getHero().MoneyInPot + heroInvest;
            int callAmount = heroTargetTotal - player.MoneyInPot;

            if (callAmount < 0)
                callAmount = 0;

            if (callAmount > player.Stack)
                callAmount = player.Stack;

            response.CallAmount = callAmount;

            int potAfterHeroAction = potSize() + heroInvest;
            int potIfCalled = Math.Max(1, potAfterHeroAction + callAmount);

            float requiredEquity = callAmount > 0
                ? callAmount / (float)potIfCalled
                : 0.0f;

            response.RequiredEquity = requiredEquity;

            float mass = 0.0f;
            float continueMass = 0.0f;
            float raiseMass = 0.0f;
            float continueStrengthMass = 0.0f;

            float softness = (_street == Street.River) ? 0.070f : ((_street == Street.Turn) ? 0.095f : 0.120f);
            float pricePressure = callAmount / (float)Math.Max(1, potAfterHeroAction + callAmount);

            bool canRaise =
                (_numBets + 1 < 4) &&
                callAmount < player.Stack &&
                numActiveNonAllInPlayers() > 1;

            foreach (var pair in player.Range.Data)
            {
                if (pair.Equity <= 0.0000001f)
                    continue;

                HoleCards combo = pair.GetHoleCards();
                float score = estimateVillainComboShowdownScore(combo);
                float weight = pair.Equity;

                float continueLikelihood = FastSigmoid((score - requiredEquity) / softness);

                float raiseThreshold = requiredEquity + 0.18f + (0.10f * pricePressure);
                float raiseLikelihood = canRaise
                    ? FastSigmoid((score - raiseThreshold) / softness)
                    : 0.0f;

                if (raiseLikelihood > continueLikelihood)
                    raiseLikelihood = continueLikelihood;

                mass += weight;
                continueMass += weight * continueLikelihood;
                raiseMass += weight * raiseLikelihood;
                continueStrengthMass += weight * continueLikelihood * score;
            }

            if (mass <= 0.0f)
                return response;

            float rangeContinueProb = Clamp01(continueMass / mass);
            float rangeRaiseProb = Clamp01(raiseMass / mass);
            float rangeContinueStrength = continueMass > 0.0f
                ? Clamp01(continueStrengthMass / continueMass)
                : 0.0f;

            float modelBetRaiseProb;
            float modelCheckCallProb;
            tryGetOpponentPostFlopModelProbabilities(playerIndex, out modelBetRaiseProb, out modelCheckCallProb);

            float modelContinueProb = ClampFloat(
                (modelBetRaiseProb + modelCheckCallProb) * (1.0f - 0.35f * pricePressure),
                0.02f,
                0.98f);

            float modelRaiseProb = canRaise
                ? ClampFloat(modelBetRaiseProb * (1.0f - 0.50f * pricePressure), 0.0f, 0.50f)
                : 0.0f;

            float continueProb = ClampFloat(
                (0.70f * rangeContinueProb) + (0.30f * modelContinueProb),
                0.02f,
                0.98f);

            float raiseProb = canRaise
                ? ClampFloat((0.70f * rangeRaiseProb) + (0.30f * modelRaiseProb), 0.0f, continueProb * 0.65f)
                : 0.0f;

            float callProb = ClampFloat(continueProb - raiseProb, 0.0f, 1.0f);
            float foldProb = ClampFloat(1.0f - callProb - raiseProb, 0.0f, 1.0f);

            response.FoldProb = foldProb;
            response.CallProb = callProb;
            response.RaiseProb = raiseProb;
            response.ContinueStrength = rangeContinueStrength;

            return response;
        }

        private float estimateFastBetRaiseEV(int heroInvest, float heroEquity, out string report)
        {
            StringBuilder sb = new StringBuilder();

            int potBeforeAction = potSize();

            float allFoldProb = 1.0f;
            float noRaiseProb = 1.0f;
            float expectedCallMoney = 0.0f;
            float expectedCallers = 0.0f;
            float continueStrengthWeighted = 0.0f;
            float continueStrengthWeight = 0.0f;

            sb.Append($"    size={heroInvest}: ");

            for (int i = 0; i < _players.Count; i++)
            {
                if (i == _heroInd)
                    continue;

                Player player = _players[i];

                if (player.StatusInHand == Status.Folded || player.StatusInHand == Status.AllIn)
                    continue;

                FastOpponentResponse r = estimateFastOpponentResponseToHeroInvest(i, heroInvest);

                allFoldProb *= r.FoldProb;
                noRaiseProb *= (1.0f - r.RaiseProb);

                expectedCallMoney += r.CallProb * r.CallAmount;
                expectedCallers += r.CallProb;

                continueStrengthWeighted += r.ContinueStrength * (r.CallProb + r.RaiseProb);
                continueStrengthWeight += (r.CallProb + r.RaiseProb);

                sb.Append($"[{r}] ");
            }

            float anyRaiseProb = Clamp01(1.0f - noRaiseProb);
            float callNoRaiseProb = Clamp01(noRaiseProb - allFoldProb);

            float conditionalCallMoney = callNoRaiseProb > 0.0001f
                ? expectedCallMoney / callNoRaiseProb
                : 0.0f;

            float conditionalCallers = callNoRaiseProb > 0.0001f
                ? expectedCallers / callNoRaiseProb
                : 0.0f;

            conditionalCallers = ClampFloat(conditionalCallers, 0.0f, Math.Max(1, numActivePlayers() - 1));

            float avgContinueStrength = continueStrengthWeight > 0.0001f
                ? Clamp01(continueStrengthWeighted / continueStrengthWeight)
                : 0.0f;

            float equityPenalty = 1.0f - (0.18f * avgContinueStrength) - (0.07f * Math.Max(0.0f, conditionalCallers - 1.0f));
            equityPenalty = ClampFloat(equityPenalty, 0.48f, 1.0f);

            float equityWhenCalled = Clamp01(heroEquity * equityPenalty);

            float callBranchPot = potBeforeAction + heroInvest + conditionalCallMoney;
            float callBranchEV = (equityWhenCalled * callBranchPot) - heroInvest;

            // Versao rapida e conservadora:
            // se tomamos raise, assumimos que a perda minima do ramo e o investimento feito agora.
            float raiseBranchEV = -heroInvest;

            float ev =
                (allFoldProb * potBeforeAction) +
                (callNoRaiseProb * callBranchEV) +
                (anyRaiseProb * raiseBranchEV);

            sb.Append($"allFold={allFoldProb:P1}, callNoRaise={callNoRaiseProb:P1}, anyRaise={anyRaiseProb:P1}, ");
            sb.Append($"eqOH={heroEquity:P1}, eqCallAdj={equityWhenCalled:P1}, callPot={callBranchPot:F1}, EV={ev:F3}");

            report = sb.ToString();
            return ev;
        }

        public BotDecision calculateHeroFastPostFlopAction(double ohEquity, string equitySource)
        {
            BotDecision bd = calculateHeroAction();
            bd.message = ("FastPostFlopEV depreciado: decisao gerada pela arvore completa canonica. " + bd.message).Trim();
            bd.usedMultiSizeEV = false;
            return bd;
        }

		
private int countHeroOvercardsToBoard()
{
    if (_board == null || _board.Count == 0)
        return 0;

    int maxBoardRank = 0;

    foreach (var card in _board.Cards)
    {
        int r = (int)card.rank;
        if (r > maxBoardRank)
            maxBoardRank = r;
    }

    int overcards = 0;

    if ((int)_heroHoleCards.Card0.rank > maxBoardRank)
        overcards++;

    if ((int)_heroHoleCards.Card1.rank > maxBoardRank)
        overcards++;

    return overcards;
}

private bool heroHasFlushDrawOnFlop()
{
    if (_board == null || _board.Count != 3)
        return false;

    int[] suitCount = new int[4];

    foreach (var card in _board.Cards)
    {
        int s = (int)card.suit;
        if (s >= 0 && s < 4)
            suitCount[s]++;
    }

    int s0 = (int)_heroHoleCards.Card0.suit;
    int s1 = (int)_heroHoleCards.Card1.suit;

    if (s0 >= 0 && s0 < 4)
        suitCount[s0]++;

    if (s1 >= 0 && s1 < 4)
        suitCount[s1]++;

    return suitCount.Max() == 4;
}

private bool heroHasStraightDrawOnFlop()
{
    if (_board == null || _board.Count != 3)
        return false;

    bool[] ranks = new bool[15];

    Action<int> addRank = (r) =>
    {
        if (r >= 2 && r <= 14)
        {
            ranks[r] = true;

            if (r == 14)
                ranks[1] = true;
        }
    };

    foreach (var card in _board.Cards)
        addRank((int)card.rank);

    addRank((int)_heroHoleCards.Card0.rank);
    addRank((int)_heroHoleCards.Card1.rank);

    int bestWindow = 0;

    for (int high = 14; high >= 5; high--)
    {
        int count = 0;

        for (int r = high - 4; r <= high; r++)
        {
            if (ranks[r])
                count++;
        }

        if (count > bestWindow)
            bestWindow = count;
    }

    return bestWindow >= 4;
}

private int calculateFlopHeuristicBetRaiseAmount(bool strongMadeHand, bool strongDraw, int amountToCall)
{
    int stack = getHero().Stack;
    int basePot = Math.Max(1, potSize() + amountToCall);
    int amount;

    if (amountToCall > 0)
    {
        // Enfrentando aposta no flop: raise 50% ou all-in por regra de commitment.
        amount = amountToCall + (int)Math.Round(basePot * 0.50f);
    }
    else
    {
        // Sem aposta no flop: bet 33%, 50% ou all-in por regra de commitment.
        float fraction = (strongMadeHand || strongDraw) ? 0.50f : 0.33f;
        amount = (int)Math.Round(basePot * fraction);
    }

    return clampBetAmount(amount);
}

private BotDecision calculateHeroFlopHeuristicAction(int amountToCall, int nOfOpponents)
{
    HandStrength handStrength = HandStrength.calculateHandStrength(_heroHoleCards, _board);

    bool strongMadeHand = handStrength.rank >= HandRank.TwoPair;
    bool weakMadeHand = handStrength.rank > HandRank.HighCard && handStrength.rank < HandRank.TwoPair;
    bool flushDraw = heroHasFlushDrawOnFlop();
    bool straightDraw = heroHasStraightDrawOnFlop();
    bool strongDraw = flushDraw || straightDraw;
    int overcards = countHeroOvercardsToBoard();

    int pot = potSize();
    float potOdds = amountToCall > 0
        ? amountToCall / (float)Math.Max(1, pot + amountToCall)
        : 0.0f;

    bool headsUp = nOfOpponents == 1;
    bool inPosition = isPlayerInPosition(_heroInd);

    BotDecision bd = new BotDecision
    {
        actionType = ActionType.Check,
        byAmount = 0,
        checkCallEV = 0.0f,
        betRaiseEV = 0.0f,
        timeSpentSeconds = 0,
        message = "",
        usedMultiSizeEV = false,
        sizingReport = ""
    };

    bd.message += " -> FLOP HEURISTICO: DecisionMaking.dll nao foi chamada no flop.\n";
    bd.message += $" -> Heuristica flop: hand={handStrength.rank}, strongMade={strongMadeHand}, weakMade={weakMadeHand}, flushDraw={flushDraw}, straightDraw={straightDraw}, overcards={overcards}, potOdds={potOdds:P1}, HU={headsUp}, IP={inPosition}.\n";

    if (amountToCall > 0)
    {
        if (strongMadeHand)
        {
            bd.actionType = ActionType.Raise;
            bd.byAmount = calculateFlopHeuristicBetRaiseAmount(true, false, amountToCall);
            bd.checkCallEV = 6.0f;
            bd.betRaiseEV = 10.0f;
            bd.message += " -> Facing bet: mao forte feita no flop, raise por valor.\n";
        }
        else if (strongDraw && headsUp && potOdds <= 0.33f)
        {
            bd.actionType = ActionType.Raise;
            bd.byAmount = calculateFlopHeuristicBetRaiseAmount(false, true, amountToCall);
            bd.checkCallEV = 5.0f;
            bd.betRaiseEV = 7.0f;
            bd.message += " -> Facing bet: draw forte HU com pot odds aceitaveis, semi-bluff raise 50%.\n";
        }
        else if (strongDraw && potOdds <= 0.38f)
        {
            bd.actionType = ActionType.Call;
            bd.byAmount = amountToCall;
            bd.checkCallEV = 5.0f;
            bd.betRaiseEV = 2.0f;
            bd.message += " -> Facing bet: draw forte, call por odds/realizacao.\n";
        }
        else if (weakMadeHand && potOdds <= 0.30f)
        {
            bd.actionType = ActionType.Call;
            bd.byAmount = amountToCall;
            bd.checkCallEV = 4.0f;
            bd.betRaiseEV = 1.0f;
            bd.message += " -> Facing bet: par/SDV fraco com preco aceitavel, call.\n";
        }
        else if (overcards >= 2 && headsUp && potOdds <= 0.20f)
        {
            bd.actionType = ActionType.Call;
            bd.byAmount = amountToCall;
            bd.checkCallEV = 2.0f;
            bd.betRaiseEV = 0.0f;
            bd.message += " -> Facing bet: duas overcards HU e preco muito baixo, call.\n";
        }
        else
        {
            bd.actionType = ActionType.Fold;
            bd.byAmount = 0;
            bd.checkCallEV = -amountToCall;
            bd.betRaiseEV = -amountToCall - 1.0f;
            bd.message += " -> Facing bet: sem equity suficiente para continuar, fold.\n";
        }

        return bd;
    }

    if (strongMadeHand)
    {
        bd.actionType = ActionType.Bet;
        bd.byAmount = calculateFlopHeuristicBetRaiseAmount(true, false, 0);
        bd.checkCallEV = 5.0f;
        bd.betRaiseEV = 9.0f;
        bd.message += " -> Sem aposta: mao forte feita, bet 50% por valor/protecao.\n";
    }
    else if (strongDraw && headsUp)
    {
        bd.actionType = ActionType.Bet;
        bd.byAmount = calculateFlopHeuristicBetRaiseAmount(false, true, 0);
        bd.checkCallEV = 4.0f;
        bd.betRaiseEV = 7.0f;
        bd.message += " -> Sem aposta: draw forte HU, bet 50% como semi-bluff.\n";
    }
    else if (!weakMadeHand && overcards >= 2 && headsUp && inPosition && calculateBoardWetnessForSizing() < 0.35f)
    {
        bd.actionType = ActionType.Bet;
        bd.byAmount = calculateFlopHeuristicBetRaiseAmount(false, false, 0);
        bd.checkCallEV = 2.0f;
        bd.betRaiseEV = 3.0f;
        bd.message += " -> Sem aposta: duas overcards em board seco HU IP, bet 33%.\n";
    }
    else
    {
        bd.actionType = ActionType.Check;
        bd.byAmount = 0;
        bd.checkCallEV = 2.0f;
        bd.betRaiseEV = 0.0f;
        bd.message += " -> Sem aposta: check por showdown/pot-control/semibluff insuficiente.\n";
    }

    return bd;
}

        private bool tryEvaluatePostFlopMultiSizeEV(ref BotDecision bd)
        {
            // Fase canonical-tree: o avaliador multi-size fica explicitamente depreciado.
            // A arvore calcula apenas a acao abstrata Bet/Raise usando o size canonico nativo
            // de DecisionMaking.GameState.getRaiseAmount(). O size real e definido depois,
            // pela ExecutionSizingPolicy, sem reavaliar EV para tamanhos alternativos.
            return false;
        }

        private static string fmtPts(int value)
        {
            return value.ToString();
        }

        private string describeBoardForDiagnostics()
        {
            if (_board == null || _board.Count == 0)
                return "sem board";

            return _board.ToString();
        }

        private string describeHeroHandForDiagnostics()
        {
            try
            {
                if (_street == Street.PreFlop)
                    return _heroHoleCards.ToString();

                var hs = HandStrength.calculateHandStrength(_heroHoleCards, _board);
                return $"{_heroHoleCards} / {hs.rank}";
            }
            catch
            {
                return _heroHoleCards == null ? "?" : _heroHoleCards.ToString();
            }
        }

        private string describeBoardTextureForDiagnostics()
        {
            if (_board == null || _board.Count < 3)
                return "nao aplicavel";

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

            float wetness = calculateBoardWetnessForSizing();
            string flushTexture = monotone ? "monotone" : (twoTone ? "two-tone" : "rainbow/seco de naipe");
            string pairTexture = paired ? "pareado" : "nao pareado";
            string straightTexture = bestStraightWindow >= 4 ? "muito conectado" : (bestStraightWindow >= 3 ? "conectado" : "pouco conectado");

            return $"{flushTexture}, {pairTexture}, {straightTexture}, wetness={wetness:F2}";
        }

public string getOpponentRangesDiagnostics(bool mostrarRangesCompletos = false, int topN = 5)
{
    StringBuilder sb = new StringBuilder();

    for (int i = 0; i < _players.Count; i++)
    {
        if (i == _heroInd)
            continue;

        var player = _players[i];

        if (player.StatusInHand == Status.Folded)
            continue;

        if (sb.Length > 0)
            sb.Append(" | ");

        sb.Append($"{player.Name}/{player.PreFlopPosition}: ");
        sb.Append($"status={player.StatusInHand}, ");
        sb.Append($"stack={fmtPts(player.Stack)}, ");
        sb.Append($"inPot={fmtPts(player.MoneyInPot)}, ");
        sb.Append(player.Range.DiagnosticSummary(topN, mostrarRangesCompletos));
    }

    if (sb.Length == 0)
        return "sem viloes ativos";

    return sb.ToString();
}

private string describeOpponentRangesForDiagnostics()
{
    return getOpponentRangesDiagnostics(false, 5);
}

        private void appendAcademicDecisionDiagnostics(ref BotDecision bd, int amountToCallAtDecisionStart, int nOfOpponents)
        {
            int pot = potSize();
            int heroStack = getHero().Stack;
            int heroInPot = getHero().MoneyInPot;
            int maxInPot = getMaxMoneyInThePot();
            int activePlayers = numActivePlayers();
            int activeNonAllIn = numActiveNonAllInPlayers();

            float spr = heroStack / (float)Math.Max(1, pot);
            float potOdds = 0.0f;

            if (amountToCallAtDecisionStart > 0)
                potOdds = amountToCallAtDecisionStart / (float)Math.Max(1, pot + amountToCallAtDecisionStart);

            float edge = bd.betRaiseEV - bd.checkCallEV;
            float selectedInvestment = bd.byAmount;
            float selectedCommitment = heroStack > 0 ? selectedInvestment / Math.Max(1.0f, heroStack) : 0.0f;

            StringBuilder diag = new StringBuilder();
            diag.AppendLine();
            diag.AppendLine("[Diagnostico academico da decisao]");
            diag.AppendLine($"street={_street}, board={describeBoardForDiagnostics()}, hero={describeHeroHandForDiagnostics()}");
            diag.AppendLine($"jogadores: ativos={activePlayers}, oponentes={nOfOpponents}, nonAllIn={activeNonAllIn}, heroPos={getHero().PreFlopPosition}, heroInPosition={isPlayerInPosition(_heroInd)}");
            diag.AppendLine($"pote={fmtPts(pot)}, maxInPot={fmtPts(maxInPot)}, heroInPot={fmtPts(heroInPot)}, stackHero={fmtPts(heroStack)}, amountToCall={fmtPts(amountToCallAtDecisionStart)}, SPR={spr:F2}, potOddsCall={potOdds:P1}");
            diag.AppendLine($"EV liquido: check/call={bd.checkCallEV:F3}, bet/raise={bd.betRaiseEV:F3}, edgeBR-CC={edge:F3}");
            diag.AppendLine($"acaoEscolhida={bd.actionType}, byAmount={fmtPts(bd.byAmount)}, commitmentEscolhido={selectedCommitment:P1}, multiSizeEV={bd.usedMultiSizeEV}");

if (_street > Street.PreFlop)
    diag.AppendLine($"texturaBoard={describeBoardTextureForDiagnostics()}");

diag.AppendLine($"rangesViloes={describeOpponentRangesForDiagnostics()}");

            if (!string.IsNullOrWhiteSpace(bd.sizingReport))
            {
                diag.AppendLine("resumoSizingEV:");
                diag.Append(bd.sizingReport.TrimEnd());
                diag.AppendLine();
            }

            bd.message += diag.ToString();
        }

        private bool tryCalculateHeroPreFlopChartActionFast(int amountToCall, DateTime startTime, out BotDecision bd)
        {
            bd = new BotDecision
            {
                actionType = ActionType.NoAction,
                byAmount = 0,
                betRaiseEV = 0.0f,
                checkCallEV = 0.0f,
                timeSpentSeconds = 0,
                message = "",
                usedMultiSizeEV = false,
                sizingReport = ""
            };

            if (_street != Street.PreFlop)
                return false;

            if (_preFlopCharts == null)
                return false;

            var pfcActionDistribution = _preFlopCharts.GetActionDistribution(this, _preFlopChartsLevel);

            if (pfcActionDistribution == null)
                return false;

            var heroPos = getHero().PreFlopPosition;
            var villainPos = (_bettors.Count > 0) ? _bettors.Last() : Position.Empty;

            bd.actionType = pfcActionDistribution.sample(_rng);

            // Fast path: a chart Ã© a polÃ­tica preflop. Estes campos sÃ£o scores
            // informativos da chart; nÃ£o disparam DecisionMaking.EstimateEV.
            bd.checkCallEV = pfcActionDistribution.ccProb;
            bd.betRaiseEV = pfcActionDistribution.brProb + pfcActionDistribution.allinProb;

            bd.message += $" -> FastPreFlopChart: decisao direta por chart, sem chamar DecisionMaking.EstimateEV.\n";
            bd.message += $" -> Chart situation: Hero pos {heroPos}, villainPos {villainPos}, num bets {_numBets}, num callers {_numCallers}.\n";
            bd.message += $" -> Preflop chart set: {getPreFlopChartsInfo()}.\n";
            bd.message += $" -> Chart lookup: {_preFlopCharts.LastLookupInfo}.\n";
            bd.message += $" -> Chart AD: allin={pfcActionDistribution.allinProb:F3}, br={pfcActionDistribution.brProb:F3}, cc={pfcActionDistribution.ccProb:F3}.\n";
            bd.message += $" -> Sampled action is {bd.actionType}.\n";

            if (bd.actionType == ActionType.Fold)
            {
                bd.byAmount = 0;

                if (amountToCall == 0)
                {
                    bd.actionType = ActionType.Check;
                    bd.message += " -> Chart retornou Fold, mas amountToCall=0; convertendo para Check.\n";
                }
            }
            else if (bd.actionType == ActionType.Call)
            {
                bd.byAmount = amountToCall;

                if (amountToCall == 0)
                {
                    bd.actionType = ActionType.Check;
                    bd.message += " -> Chart retornou Call, mas amountToCall=0; convertendo para Check.\n";
                }
            }
            else if (bd.actionType == ActionType.AllIn)
            {
                bd.byAmount = getHero().Stack;
            }
            else
            {
                bd.actionType = ActionType.Raise;
                bd.byAmount = getRaiseAmount();

                if (bd.byAmount <= 0)
                {
                    bd.actionType = amountToCall > 0 ? ActionType.Call : ActionType.Check;
                    bd.byAmount = amountToCall;
                    bd.message += " -> Chart retornou Raise, mas nao havia raise valido; usando Call/Check.\n";
                }
            }

            if ((bd.actionType == ActionType.Bet || bd.actionType == ActionType.Raise) &&
                IsAtLeastAllInCommitmentThreshold(bd.byAmount, getHero().Stack))
            {
                bd.message += $" -> FastPreFlopChart: amount >= {_allInCommitmentPercent}% do balance restante; convertendo para all-in.\n";
                bd.byAmount = getHero().Stack;
                bd.actionType = ActionType.AllIn;
            }

            bd.timeSpentSeconds = (DateTime.Now - startTime).TotalSeconds;
            bd.message = bd.message.Trim();

            return true;
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
                message = "",
                usedMultiSizeEV = false,
                sizingReport = ""
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

if (_street == Street.PreFlop)
{
    BotDecision chartDecision;

    if (tryCalculateHeroPreFlopChartActionFast(amountToCall, startTime, out chartDecision))
        return chartDecision;
}

// Pos-flop: a decisao volta a ser sempre da arvore completa canonica.
// O antigo atalho heuristico de flop foi mantido no arquivo apenas como fallback legado,
// mas nao participa mais do pipeline principal.

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

if (_street != Street.PreFlop)
{
    bd.message += " -> Arvore canonica pos-flop: EV calculado sem FastPostFlopEV e sem reavaliacao multi-size.\n";
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
            if (!bd.usedMultiSizeEV)
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
                if (bd.byAmount <= 0)
                {
                    int canonicalTreeAmount = getRaiseAmount();
                    bd.byAmount = calculatePostFlopBetRaiseAmount(bd.checkCallEV, bd.betRaiseEV);

                    if (_street != Street.PreFlop)
                    {
                        bd.message += $" -> ExecutionSizingPolicy: arvore avaliou size canonico {canonicalTreeAmount}; execucao escolheu {bd.byAmount}. {_lastExecutionSizingReason}\n";
                    }
                }
                else if (bd.usedMultiSizeEV)
                {
                    bd.message += $" -> AVISO: usedMultiSizeEV estava ativo, mas esta fase deprecia multi-size; mantendo byAmount={bd.byAmount}.\n";
                }
            }

            if (IsAtLeastAllInCommitmentThreshold(bd.byAmount, _players[_heroInd].Stack))
            {
                bd.message += $" -> But amount to put in pot is at least {_allInCommitmentPercent}% of player's stack, so go all in!\n";
                bd.byAmount = _players[_heroInd].Stack;
                bd.actionType = ActionType.AllIn;
            }

            appendAcademicDecisionDiagnostics(ref bd, amountToCall, nOfOpponents);

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
