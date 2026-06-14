using System;
using System.Collections.Generic;

namespace G5.Logic
{
    public sealed class PostFlopLineHistory
    {
        public Street CurrentStreet { get; private set; }
        public int PreFlopAggressorIndex { get; private set; }
        public int PreviousStreetAggressorIndex { get; private set; }
        public int FirstAggressorIndexThisStreet { get; private set; }
        public int LastAggressorIndexThisStreet { get; private set; }
        public PostFlopLineClass LastAggressiveLineClassThisStreet { get; private set; }
        public HashSet<int> CheckedThisStreet { get; private set; }
        public bool PreviousStreetHadAggression { get; private set; }
        public bool PreviousStreetWasCheckedThrough { get; private set; }
        public bool PreFlopHadRaise { get; private set; }

        public PostFlopLineHistory()
        {
            CheckedThisStreet = new HashSet<int>();
            ResetForNewHand();
        }

        public void ResetForNewHand()
        {
            CurrentStreet = Street.PreFlop;
            PreFlopAggressorIndex = -1;
            PreviousStreetAggressorIndex = -1;
            FirstAggressorIndexThisStreet = -1;
            LastAggressorIndexThisStreet = -1;
            LastAggressiveLineClassThisStreet = PostFlopLineClass.Unknown;
            CheckedThisStreet.Clear();
            PreviousStreetHadAggression = false;
            PreviousStreetWasCheckedThrough = false;
            PreFlopHadRaise = false;
        }

        public void EnsureStreet(Street street)
        {
            if (street == CurrentStreet)
                return;

            if (CurrentStreet != Street.PreFlop)
            {
                PreviousStreetAggressorIndex = LastAggressorIndexThisStreet;
                PreviousStreetHadAggression = LastAggressorIndexThisStreet >= 0;
                PreviousStreetWasCheckedThrough = LastAggressorIndexThisStreet < 0;
            }

            CurrentStreet = street;
            FirstAggressorIndexThisStreet = -1;
            LastAggressorIndexThisStreet = -1;
            LastAggressiveLineClassThisStreet = PostFlopLineClass.Unknown;
            CheckedThisStreet.Clear();
        }

        public bool HasCheckedThisStreet(int playerIndex)
        {
            return CheckedThisStreet.Contains(playerIndex);
        }

        public void ObserveAction(BotGameState gameState, ActionType actionType, PostFlopLineContext context)
        {
            if (gameState == null)
                return;

            int actor = gameState.getPlayerToActInd();
            Street street = gameState.getStreet();
            EnsureStreet(street);

            if (actionType == ActionType.Check)
            {
                CheckedThisStreet.Add(actor);
                return;
            }

            if (street == Street.PreFlop)
            {
                if (actionType == ActionType.Bet || actionType == ActionType.Raise || actionType == ActionType.AllIn)
                {
                    PreFlopAggressorIndex = actor;
                    PreFlopHadRaise = true;
                }

                return;
            }

            if (actionType == ActionType.Bet || actionType == ActionType.Raise || actionType == ActionType.AllIn)
            {
                if (FirstAggressorIndexThisStreet < 0)
                    FirstAggressorIndexThisStreet = actor;

                LastAggressorIndexThisStreet = actor;
                LastAggressiveLineClassThisStreet = context != null ? context.LineClass : PostFlopLineClass.Unknown;
            }
        }
    }

    public sealed class LineContextClassifier
    {
        public PostFlopLineContext Classify(BotGameState gameState, ActionType observedAction, PostFlopLineHistory history)
        {
            if (gameState == null)
                throw new ArgumentNullException(nameof(gameState));

            if (history == null)
                throw new ArgumentNullException(nameof(history));

            history.EnsureStreet(gameState.getStreet());

            var sizing = gameState.getLastActionSizingContext();
            int actor = gameState.getPlayerToActInd();
            int hero = gameState.getHeroInd();
            int activePlayers = gameState.numActivePlayers();
            int previousAggressor = ResolvePreviousAggressor(gameState, history);
            int preFlopAggressor = ResolvePreFlopAggressor(gameState, history);

            PostFlopLineContext ctx = new PostFlopLineContext
            {
                Street = gameState.getStreet(),
                Round = (actor >= 0 && actor < gameState.getPlayers().Count) ? gameState.getPlayers()[actor].Round() : 0,
                ObservedAction = observedAction,
                ActorIndex = actor,
                HeroIndex = hero,
                ActorIsHero = actor == hero,
                ActorInPosition = actor >= 0 ? gameState.isPlayerInPosition(actor) : false,
                FacingBet = sizing.AmountToCall > 0,
                ActorCheckedThisStreet = history.HasCheckedThisStreet(actor),
                IsFirstAggressiveActionThisStreet = history.LastAggressorIndexThisStreet < 0,
                IsMultiway = activePlayers >= 3,
                NumActivePlayers = activePlayers,
                NumBetsThisStreetBeforeAction = gameState.getNumBets(),
                LastAggressorIndexThisStreet = history.LastAggressorIndexThisStreet,
                FirstAggressorIndexThisStreet = history.FirstAggressorIndexThisStreet,
                PreviousStreetAggressorIndex = previousAggressor,
                PreFlopAggressorIndex = preFlopAggressor,
                ActorWasPreFlopAggressor = actor >= 0 && actor == preFlopAggressor,
                ActorWasPreviousStreetAggressor = actor >= 0 && actor == previousAggressor,
                PreviousAggressorStillActive = IsActive(gameState, previousAggressor),
                FacingLineClass = history.LastAggressiveLineClassThisStreet,
                SizeClass = ClassifySize(sizing),
                BoardTexture = ClassifyBoardTexture(gameState.getBoard()),
                BetToPotRatio = sizing.BetToPotRatio,
                CallToPotRatio = sizing.CallToPotRatio,
                StackCommitment = sizing.StackCommitment,
                Spr = sizing.SprBeforeAction,
                PotBeforeAction = sizing.PotBeforeAction,
                AmountToCall = sizing.AmountToCall,
                ActionAmount = sizing.ActionAmount,
                InitiativeOwner = ResolveInitiativeOwner(actor, hero, previousAggressor, preFlopAggressor)
            };

            ctx.LineClass = ClassifyLine(ctx, history, gameState);
            return ctx;
        }

        private static int ResolvePreFlopAggressor(BotGameState gameState, PostFlopLineHistory history)
        {
            if (history.PreFlopAggressorIndex >= 0)
                return history.PreFlopAggressorIndex;

            int result = -1;
            var players = gameState.getPlayers();

            for (int i = 0; i < players.Count; i++)
            {
                if (players[i].PrevStreetAction == ActionType.Bet ||
                    players[i].PrevStreetAction == ActionType.Raise ||
                    players[i].PrevStreetAction == ActionType.AllIn)
                {
                    result = i;
                }
            }

            return result;
        }

        private static int ResolvePreviousAggressor(BotGameState gameState, PostFlopLineHistory history)
        {
            if (gameState.getStreet() == Street.Flop)
                return ResolvePreFlopAggressor(gameState, history);

            if (history.PreviousStreetAggressorIndex >= 0)
                return history.PreviousStreetAggressorIndex;

            return ResolvePreFlopAggressor(gameState, history);
        }

        private static bool IsActive(BotGameState gameState, int playerIndex)
        {
            if (playerIndex < 0)
                return false;

            var players = gameState.getPlayers();

            if (playerIndex >= players.Count)
                return false;

            return players[playerIndex].StatusInHand != Status.Folded;
        }

        private static InitiativeOwner ResolveInitiativeOwner(int actor, int hero, int previousAggressor, int preFlopAggressor)
        {
            int owner = previousAggressor >= 0 ? previousAggressor : preFlopAggressor;

            if (owner < 0)
                return InitiativeOwner.None;

            if (owner == actor)
                return InitiativeOwner.Actor;

            if (owner == hero)
                return InitiativeOwner.Hero;

            return InitiativeOwner.OtherPlayer;
        }

        private static PostFlopLineClass ClassifyLine(PostFlopLineContext ctx, PostFlopLineHistory history, BotGameState gameState)
        {
            if (ctx.ObservedAction == ActionType.Fold)
                return FoldClassForFacingLine(ctx.FacingLineClass);

            if (ctx.ObservedAction == ActionType.Call)
                return CallClassForFacingLine(ctx.FacingLineClass);

            if (ctx.ObservedAction == ActionType.Check)
                return ClassifyCheck(ctx, history);

            if (ctx.ObservedAction == ActionType.Bet ||
                ctx.ObservedAction == ActionType.Raise ||
                ctx.ObservedAction == ActionType.AllIn)
            {
                if (ctx.ObservedAction == ActionType.AllIn && ctx.StackCommitment >= 0.95f)
                {
                    // Keep topological information when possible, but mark all-in if the line is otherwise ambiguous.
                    PostFlopLineClass baseClass = ClassifyAggressive(ctx, history, gameState);
                    if (baseClass == PostFlopLineClass.GenericBet || baseClass == PostFlopLineClass.GenericRaise)
                        return PostFlopLineClass.AllInPolarized;
                    return baseClass;
                }

                return ClassifyAggressive(ctx, history, gameState);
            }

            return PostFlopLineClass.Unknown;
        }

        private static PostFlopLineClass ClassifyCheck(PostFlopLineContext ctx, PostFlopLineHistory history)
        {
            if (ctx.PreviousAggressorStillActive && !ctx.ActorWasPreviousStreetAggressor && !ctx.ActorInPosition)
                return PostFlopLineClass.CheckToAggressor;

            if (ctx.ActorWasPreviousStreetAggressor && ctx.ActorInPosition)
                return PostFlopLineClass.CheckBackWithInitiative;

            return PostFlopLineClass.GenericCheck;
        }

        private static PostFlopLineClass ClassifyAggressive(PostFlopLineContext ctx, PostFlopLineHistory history, BotGameState gameState)
        {
            if (ctx.FacingBet)
            {
                if (ctx.ActorCheckedThisStreet)
                    return PostFlopLineClass.CheckRaise;

                switch (ctx.FacingLineClass)
                {
                    case PostFlopLineClass.CBet:
                    case PostFlopLineClass.DoubleBarrel:
                    case PostFlopLineClass.TripleBarrel:
                        return PostFlopLineClass.RaiseVsCBet;
                    case PostFlopLineClass.DonkBet:
                    case PostFlopLineClass.LimpedPotLead:
                    case PostFlopLineClass.MultiwayLead:
                        return PostFlopLineClass.RaiseVsDonk;
                    case PostFlopLineClass.ProbeBet:
                        return PostFlopLineClass.RaiseVsProbe;
                    case PostFlopLineClass.FloatBet:
                    case PostFlopLineClass.StabAfterChecks:
                        return PostFlopLineClass.RaiseVsFloat;
                    case PostFlopLineClass.DelayedCBet:
                        return PostFlopLineClass.RaiseVsDelayedCBet;
                    case PostFlopLineClass.RaiseVsCBet:
                    case PostFlopLineClass.RaiseVsDonk:
                    case PostFlopLineClass.RaiseVsProbe:
                    case PostFlopLineClass.RaiseVsFloat:
                    case PostFlopLineClass.RaiseVsDelayedCBet:
                    case PostFlopLineClass.GenericRaise:
                        return PostFlopLineClass.ReRaise;
                    default:
                        return PostFlopLineClass.GenericRaise;
                }
            }

            if (!ctx.IsFirstAggressiveActionThisStreet)
                return PostFlopLineClass.GenericRaise;

            if (ctx.IsMultiway && ctx.NumActivePlayers > 2 && !ctx.ActorWasPreviousStreetAggressor)
                return PostFlopLineClass.MultiwayLead;

            if (ctx.ActorWasPreviousStreetAggressor)
            {
                if (ctx.Street == Street.Flop)
                    return PostFlopLineClass.CBet;

                if (ctx.Street == Street.Turn)
                {
                    if (history.PreviousStreetHadAggression)
                        return PostFlopLineClass.DoubleBarrel;

                    return PostFlopLineClass.DelayedCBet;
                }

                if (ctx.Street == Street.River)
                {
                    if (history.PreviousStreetHadAggression)
                        return PostFlopLineClass.TripleBarrel;

                    return PostFlopLineClass.DelayedCBet;
                }
            }

            if (ctx.PreviousAggressorStillActive && !ctx.ActorWasPreviousStreetAggressor)
            {
                if (!ctx.ActorInPosition)
                    return gameState.getNumBets() == 0 ? PostFlopLineClass.DonkBet : PostFlopLineClass.GenericBet;

                if (history.PreviousStreetWasCheckedThrough)
                    return PostFlopLineClass.ProbeBet;

                return PostFlopLineClass.FloatBet;
            }

            if (history.PreviousStreetWasCheckedThrough)
                return PostFlopLineClass.ProbeBet;

            if (!ctx.ActorInPosition && ctx.PreFlopAggressorIndex < 0)
                return PostFlopLineClass.LimpedPotLead;

            if (ctx.ActorInPosition && history.CheckedThisStreet.Count > 0)
                return PostFlopLineClass.StabAfterChecks;

            return PostFlopLineClass.GenericBet;
        }

        private static PostFlopLineClass CallClassForFacingLine(PostFlopLineClass facingLine)
        {
            switch (facingLine)
            {
                case PostFlopLineClass.CBet:
                case PostFlopLineClass.DoubleBarrel:
                case PostFlopLineClass.TripleBarrel:
                    return PostFlopLineClass.CallVsCBet;
                case PostFlopLineClass.DonkBet:
                case PostFlopLineClass.LimpedPotLead:
                case PostFlopLineClass.MultiwayLead:
                    return PostFlopLineClass.CallVsDonk;
                case PostFlopLineClass.ProbeBet:
                    return PostFlopLineClass.CallVsProbe;
                case PostFlopLineClass.FloatBet:
                case PostFlopLineClass.StabAfterChecks:
                    return PostFlopLineClass.CallVsFloat;
                case PostFlopLineClass.DelayedCBet:
                    return PostFlopLineClass.CallVsDelayedCBet;
                default:
                    return PostFlopLineClass.GenericCall;
            }
        }

        private static PostFlopLineClass FoldClassForFacingLine(PostFlopLineClass facingLine)
        {
            switch (facingLine)
            {
                case PostFlopLineClass.CBet:
                case PostFlopLineClass.DoubleBarrel:
                case PostFlopLineClass.TripleBarrel:
                    return PostFlopLineClass.FoldVsCBet;
                case PostFlopLineClass.DonkBet:
                case PostFlopLineClass.LimpedPotLead:
                case PostFlopLineClass.MultiwayLead:
                    return PostFlopLineClass.FoldVsDonk;
                case PostFlopLineClass.ProbeBet:
                    return PostFlopLineClass.FoldVsProbe;
                case PostFlopLineClass.FloatBet:
                case PostFlopLineClass.StabAfterChecks:
                    return PostFlopLineClass.FoldVsFloat;
                case PostFlopLineClass.DelayedCBet:
                    return PostFlopLineClass.FoldVsDelayedCBet;
                default:
                    return PostFlopLineClass.GenericFold;
            }
        }

        private static PostFlopSizeClass ClassifySize(BotGameState.ActionSizingContext sizing)
        {
            if (sizing.IsAllIn)
                return PostFlopSizeClass.AllIn;

            float ratio = sizing.AmountToCall > 0 ? sizing.CallToPotRatio : sizing.BetToPotRatio;

            if (ratio <= 0.0f)
                return PostFlopSizeClass.None;

            if (ratio < 0.15f)
                return PostFlopSizeClass.Tiny;

            if (ratio < 0.35f)
                return PostFlopSizeClass.Small;

            if (ratio < 0.60f)
                return PostFlopSizeClass.HalfPot;

            if (ratio < 0.95f)
                return PostFlopSizeClass.Normal;

            if (ratio < 1.35f)
                return PostFlopSizeClass.Big;

            return PostFlopSizeClass.Overbet;
        }

        private static BoardTextureClass ClassifyBoardTexture(Board board)
        {
            if (board == null || board.Count < 3)
                return BoardTextureClass.Unknown;

            int[] ranks = new int[15];
            int[] suits = new int[4];
            bool paired = false;
            bool monotone = false;
            bool twoTone = false;
            bool connected = false;

            foreach (Card card in board.Cards)
            {
                int r = RankValue(card);
                int s = SuitValue(card);

                if (r >= 2 && r <= 14)
                    ranks[r]++;

                if (s >= 0 && s < suits.Length)
                    suits[s]++;
            }

            for (int r = 2; r <= 14; r++)
            {
                if (ranks[r] >= 2)
                    paired = true;
            }

            int maxSuit = 0;
            for (int s = 0; s < suits.Length; s++)
                maxSuit = Math.Max(maxSuit, suits[s]);

            monotone = maxSuit >= 3;
            twoTone = maxSuit == 2;
            connected = BestStraightWindowCount(ranks) >= 3;

            if (monotone)
                return BoardTextureClass.Monotone;

            if (paired)
                return BoardTextureClass.Paired;

            if (connected || twoTone)
                return BoardTextureClass.Wet;

            return BoardTextureClass.Dry;
        }

        private static int BestStraightWindowCount(int[] rankCounts)
        {
            bool[] has = new bool[15];

            for (int r = 2; r <= 14; r++)
                has[r] = rankCounts[r] > 0;

            if (has[14])
                has[1] = true;

            int best = 0;

            for (int high = 14; high >= 5; high--)
            {
                int count = 0;

                for (int r = high - 4; r <= high; r++)
                {
                    if (has[r])
                        count++;
                }

                best = Math.Max(best, count);
            }

            return best;
        }

        private static int RankValue(Card card)
        {
            int r = (int)card.rank;

            if (r == 1)
                return 14;

            return r;
        }

        private static int SuitValue(Card card)
        {
            return (int)card.suit;
        }
    }
}
