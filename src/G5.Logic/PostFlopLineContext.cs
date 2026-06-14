using System;
using System.Text;

namespace G5.Logic
{
    public enum InitiativeOwner
    {
        Unknown = 0,
        None = 1,
        Hero = 2,
        Actor = 3,
        OtherPlayer = 4,
        Multiple = 5
    }

    public enum PostFlopLineClass
    {
        Unknown = 0,

        // Proactive continuation / initiative maintenance.
        CBet = 10,
        DoubleBarrel = 11,
        TripleBarrel = 12,

        // Proactive action without previous initiative.
        DonkBet = 20,
        ProbeBet = 21,
        DelayedCBet = 22,
        DelayedFloatBet = 23,
        FloatBet = 24,
        StabAfterChecks = 25,
        LimpedPotLead = 26,
        MultiwayLead = 27,
        GenericBet = 28,

        // Reactive escalation.
        CheckRaise = 40,
        RaiseVsCBet = 41,
        RaiseVsDonk = 42,
        RaiseVsProbe = 43,
        RaiseVsFloat = 44,
        RaiseVsDelayedCBet = 45,
        RaiseVsGenericBet = 46,
        ReRaise = 47,
        AllInPolarized = 48,
        GenericRaise = 49,

        // Passive responses.
        CallVsCBet = 60,
        CallVsDonk = 61,
        CallVsProbe = 62,
        CallVsFloat = 63,
        CallVsDelayedCBet = 64,
        CallVsGenericBet = 65,
        GenericCall = 66,

        // Folds.
        FoldVsCBet = 80,
        FoldVsDonk = 81,
        FoldVsProbe = 82,
        FoldVsFloat = 83,
        FoldVsDelayedCBet = 84,
        FoldVsGenericBet = 85,
        GenericFold = 86,

        // Checks.
        CheckToAggressor = 100,
        CheckBackWithInitiative = 101,
        CheckToNoInitiative = 102,
        GenericCheck = 103
    }

    public enum PostFlopSizeClass
    {
        None = 0,
        Tiny = 1,
        Small = 2,
        HalfPot = 3,
        Normal = 4,
        Big = 5,
        Overbet = 6,
        AllIn = 7
    }

    public enum BoardTextureClass
    {
        Unknown = 0,
        Dry = 1,
        Neutral = 2,
        Wet = 3,
        Paired = 4,
        Monotone = 5
    }

    public enum LikelihoodActionBucket
    {
        Aggressive = 0,
        Continue = 1,
        Fold = 2
    }

    public sealed class ActionTargets
    {
        public float BetRaise { get; private set; }
        public float CheckCall { get; private set; }
        public float Fold { get; private set; }
        public int PriorSamples { get; set; }
        public int UpdateSamples { get; set; }
        public string Source { get; set; }

        public ActionTargets(float betRaise, float checkCall, float fold)
        {
            Source = "";
            Set(betRaise, checkCall, fold);
        }

        public void Set(float betRaise, float checkCall, float fold)
        {
            betRaise = SafeProbability(betRaise, 0.001f);
            checkCall = SafeProbability(checkCall, 0.001f);
            fold = SafeProbability(fold, 0.001f);

            float sum = betRaise + checkCall + fold;

            if (sum <= 0.0f || float.IsNaN(sum) || float.IsInfinity(sum))
            {
                BetRaise = 0.34f;
                CheckCall = 0.33f;
                Fold = 0.33f;
                return;
            }

            BetRaise = betRaise / sum;
            CheckCall = checkCall / sum;
            Fold = fold / sum;
        }

        public float ForObservedAction(ActionType actionType)
        {
            switch (ToBucket(actionType))
            {
                case LikelihoodActionBucket.Aggressive:
                    return BetRaise;
                case LikelihoodActionBucket.Continue:
                    return CheckCall;
                case LikelihoodActionBucket.Fold:
                    return Fold;
                default:
                    return CheckCall;
            }
        }

        public static LikelihoodActionBucket ToBucket(ActionType actionType)
        {
            if (actionType == ActionType.Bet ||
                actionType == ActionType.Raise ||
                actionType == ActionType.AllIn)
                return LikelihoodActionBucket.Aggressive;

            if (actionType == ActionType.Fold)
                return LikelihoodActionBucket.Fold;

            return LikelihoodActionBucket.Continue;
        }

        private static float SafeProbability(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return fallback;

            if (value < 0.0f)
                return 0.0f;

            if (value > 1.0f)
                return 1.0f;

            return value;
        }

        public override string ToString()
        {
            return string.Format("BR={0:F3}, CC={1:F3}, FO={2:F3}, prior={3}, updates={4}, source={5}",
                BetRaise, CheckCall, Fold, PriorSamples, UpdateSamples, Source ?? "");
        }
    }

    public sealed class PostFlopLineContext
    {
        public Street Street { get; set; }
        public int Round { get; set; }
        public ActionType ObservedAction { get; set; }
        public int ActorIndex { get; set; }
        public int HeroIndex { get; set; }
        public bool ActorIsHero { get; set; }
        public bool ActorInPosition { get; set; }
        public bool FacingBet { get; set; }
        public bool ActorCheckedThisStreet { get; set; }
        public bool IsFirstAggressiveActionThisStreet { get; set; }
        public bool IsMultiway { get; set; }
        public int NumActivePlayers { get; set; }
        public int NumBetsThisStreetBeforeAction { get; set; }
        public int LastAggressorIndexThisStreet { get; set; }
        public int FirstAggressorIndexThisStreet { get; set; }
        public int PreviousStreetAggressorIndex { get; set; }
        public int PreFlopAggressorIndex { get; set; }
        public bool ActorWasPreFlopAggressor { get; set; }
        public bool ActorWasPreviousStreetAggressor { get; set; }
        public bool PreviousAggressorStillActive { get; set; }
        public InitiativeOwner InitiativeOwner { get; set; }
        public PostFlopLineClass LineClass { get; set; }
        public PostFlopLineClass FacingLineClass { get; set; }
        public PostFlopSizeClass SizeClass { get; set; }
        public BoardTextureClass BoardTexture { get; set; }
        public float BetToPotRatio { get; set; }
        public float CallToPotRatio { get; set; }
        public float StackCommitment { get; set; }
        public float Spr { get; set; }
        public int PotBeforeAction { get; set; }
        public int AmountToCall { get; set; }
        public int ActionAmount { get; set; }
        public string Reason { get; set; }

        public PostFlopLineContext()
        {
            ActorIndex = -1;
            HeroIndex = -1;
            LastAggressorIndexThisStreet = -1;
            FirstAggressorIndexThisStreet = -1;
            PreviousStreetAggressorIndex = -1;
            PreFlopAggressorIndex = -1;
            FacingLineClass = PostFlopLineClass.Unknown;
            LineClass = PostFlopLineClass.Unknown;
            InitiativeOwner = InitiativeOwner.Unknown;
            Reason = "";
        }

        public bool IsAggressiveObservation
        {
            get
            {
                return ObservedAction == ActionType.Bet ||
                       ObservedAction == ActionType.Raise ||
                       ObservedAction == ActionType.AllIn;
            }
        }

        public bool IsPassiveContinueObservation
        {
            get { return ObservedAction == ActionType.Check || ObservedAction == ActionType.Call; }
        }

        public bool IsFoldObservation
        {
            get { return ObservedAction == ActionType.Fold; }
        }

        public string ToCompactString()
        {
            return string.Format(
                "line={0}, facing={1}, street={2}, actor={3}, action={4}, ip={5}, facingBet={6}, pfAgg={7}, prevAgg={8}, firstAgg={9}, multiway={10}, size={11}, betPot={12:F2}, callPot={13:F2}, spr={14:F2}",
                LineClass,
                FacingLineClass,
                Street,
                ActorIndex,
                ObservedAction,
                ActorInPosition,
                FacingBet,
                PreFlopAggressorIndex,
                PreviousStreetAggressorIndex,
                IsFirstAggressiveActionThisStreet,
                IsMultiway,
                SizeClass,
                BetToPotRatio,
                CallToPotRatio,
                Spr);
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(ToCompactString());

            if (!string.IsNullOrWhiteSpace(Reason))
            {
                sb.Append("; reason=");
                sb.Append(Reason);
            }

            return sb.ToString();
        }
    }
}
