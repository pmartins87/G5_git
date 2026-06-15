using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;


namespace G5.Logic
{
    public class Range
    {
        public const int N_HOLECARDS = 52 * 51 / 2;

        /// <summary>
        /// Struktura koja pamti Hole-Cards index i equity koji im je pridruzen.
        /// </summary>
        public struct EquityPair
        {
            public int Ind;
            public float Equity;

            public EquityPair(int index, float e)
            {
                Ind = index;
                Equity = e;
            }

            public HoleCards GetHoleCards()
            {
                return new HoleCards(Ind);
            }

            public override string ToString()
            {
                return GetHoleCards().ToString() + " - " + (Equity * 100).ToString("f2") + "%";
            }
        };

        public struct CuttingParamsT
        {
            public ActionType ActionType { get; set; }
            public Street Street { get; set; }
            public float Value1 { get; set; }
            public float Value2 { get; set; }
            public bool Forced { get; set; }
        }

        private enum PreFlopActionBucket
        {
            Fold = 0,
            CheckCall = 1,
            BetRaise = 2
        }

        private struct PreFlopComboFeatures
        {
            public float Score;
            public float Playability;
            public float Pairness;
            public float Suitedness;
            public float Connectivity;
            public float Broadway;
            public float AceHigh;
            public float Wheel;
            public float DominationRisk;
        }

        public EquityPair[] Data { get; private set; }
        public List<CuttingParamsT> CuttingParams { get; private set; }
        public List<Card> HeroHoleCards { get; private set; }
        public Board Board { get; private set; }

        public Range()
        {
            Board = new Board();
            HeroHoleCards = new List<Card>();
            CuttingParams = new List<CuttingParamsT>();
            Data = new EquityPair[N_HOLECARDS];

            for (int i = 0, k = 0; i < 51; i++)
            {
                for (var j = i + 1; j < 52; j++)
                {
                    var ind = i * 52 + j;

                    Data[k].Ind = ind;
                    Data[k].Equity = 1.0f;
                    k++;
                }
            }

            Normalize();
        }

        public Range(Range oldRange)
        {
            Data = new EquityPair[N_HOLECARDS];

            for (int i = 0; i < N_HOLECARDS; i++)
            {
                Data[i].Ind = oldRange.Data[i].Ind;
                Data[i].Equity = oldRange.Data[i].Equity;
            }

            CuttingParams = new List<CuttingParamsT>(oldRange.CuttingParams);
            HeroHoleCards = new List<Card>(oldRange.HeroHoleCards);
            Board = new Board(oldRange.Board);
        }

        public void Reset()
        {
            CuttingParams.Clear();
            HeroHoleCards.Clear();
            Board = new Board();

            for (var i = 0; i < N_HOLECARDS; i++)
            {
                Data[i].Equity = 1.0f;
            }

            Normalize();
        }

        public float getHoleCardsProb(HoleCards holeCards)
        {
            for (var i = 0; i < N_HOLECARDS; i++)
            {
                if (Data[i].Ind == holeCards.ToInt())
                    return Data[i].Equity;
            }

            return 0.0f;
        }
		
public int ActiveComboCount(float minEquity = 0.0000001f)
{
    int count = 0;

    for (int i = 0; i < N_HOLECARDS; i++)
    {
        if (Data[i].Equity > minEquity)
            count++;
    }

    return count;
}

public float ProbabilityMass(float minEquity = 0.0000001f)
{
    float mass = 0.0f;

    for (int i = 0; i < N_HOLECARDS; i++)
    {
        if (Data[i].Equity > minEquity)
            mass += Data[i].Equity;
    }

    return mass;
}

public string TopCombosSummary(int topN = 8, float minEquity = 0.0000001f)
{
    var top = Data
        .Where(x => x.Equity > minEquity)
        .OrderByDescending(x => x.Equity)
        .Take(topN)
        .Select(x => x.GetHoleCards().ToString() + ":" + (x.Equity * 100.0f).ToString("F2") + "%");

    string result = string.Join(", ", top);

    if (string.IsNullOrWhiteSpace(result))
        return "-";

    return result;
}

public string CuttingParamsSummary(int maxItems = 12)
{
    if (CuttingParams == null || CuttingParams.Count == 0)
        return "sem cortes";

    int start = Math.Max(0, CuttingParams.Count - maxItems);
    StringBuilder sb = new StringBuilder();

    for (int i = start; i < CuttingParams.Count; i++)
    {
        var c = CuttingParams[i];

        if (sb.Length > 0)
            sb.Append(" -> ");

        sb.Append(c.Street);
        sb.Append(":");
        sb.Append(c.ActionType);
        sb.Append("(br=");
        sb.Append(c.Value1.ToString("F3"));

        if (c.Forced)
        {
            sb.Append(",cc=");
            sb.Append(c.Value2.ToString("F3"));
        }

        sb.Append(")");
    }

    if (CuttingParams.Count > maxItems)
        return $"... {sb}";

    return sb.ToString();
}

public string DiagnosticSummary(int topN = 8)
{
    return $"combos={ActiveComboCount()}, mass={ProbabilityMass():F4}, top=[{TopCombosSummary(topN)}], cortes=[{CuttingParamsSummary()}]";
}

        public void BanCards(List<Card> cards, bool isBoard)
        {
            foreach (var card in cards)
            {
                BanCard(card, isBoard);
            }
        }

        public void BanCard(Card card, bool isBoard)
        {
            if (!isBoard)
            {
                HeroHoleCards.Add(card);
            }
            else
            {
                Board.AddCard(card);
            }

            var cardInd = card.ToInt();

            for (var i = 0; i < N_HOLECARDS; i++)
            {
                var c1 = Data[i].Ind / 52;
                var c2 = Data[i].Ind % 52;

                if (c1 == cardInd || c2 == cardInd)
                    Data[i].Equity = 0.0f;
            }

            Normalize();
        }

        private void Normalize()
        {
            float sum = 0;

            for (var i = 0; i < N_HOLECARDS; i++)
            {
                if (float.IsNaN(Data[i].Equity) || float.IsInfinity(Data[i].Equity) || Data[i].Equity < 0.0f)
                    Data[i].Equity = 0.0f;

                sum += Data[i].Equity;
            }

            if (sum <= 0.0f)
                throw new InvalidOperationException("Range.Normalize: massa probabilistica zerada. O corte de range anterior eliminou todos os combos validos.");

            var norm = 1.0f / sum;

            for (var i = 0; i < N_HOLECARDS; i++)
            {
                Data[i].Equity *= norm;
            }
        }

        public void CutCheckBet(ActionType actionType, Street street, Board board, float betChance, DecisionMakingContext dmContext)
        {
            CuttingParams.Add(new CuttingParamsT
            {
                ActionType = actionType,
                Street = street,
                Value1 = betChance,
                Forced = false
            });

            DecisionMakingDll.CutRange_CheckBet(this, actionType, street, board, betChance, dmContext);
        }

        public void CutFoldCallRaise(ActionType actionType, Street street, Board board, float raiseChance, float callChance, DecisionMakingContext dmContext)
        {
            CuttingParams.Add(new CuttingParamsT
            {
                ActionType = actionType,
                Street = street,
                Value1 = raiseChance,
                Value2 = callChance,
                Forced = true
            });

            DecisionMakingDll.CutRange_FoldCallRaise(this, actionType, street, board, raiseChance, callChance, dmContext);
        }

        /// <summary>
        /// Preflop range update by exact-combo likelihood.
        ///
        /// Important: before a player acts, a dealt hand is uniformly random. Therefore this method must be
        /// called only after an observed preflop action. It applies Bayes' rule:
        ///
        ///     P(h | action) proportional to P(action | h, model, position) * P(h)
        ///
        /// The action likelihood is combo-specific and then calibrated so that the weighted population
        /// average matches the player's modeled ActionDistribution for the current spot.
        /// </summary>
        public void CutPreFlopAction(ActionType actionType, Position position, TableType tableType, int numPlayers,
            float betRaiseChance, float checkCallChance)
        {
            bool forced = !(actionType == ActionType.Check || actionType == ActionType.Bet);

            CuttingParams.Add(new CuttingParamsT
            {
                ActionType = actionType,
                Street = Street.PreFlop,
                Value1 = betRaiseChance,
                Value2 = checkCallChance,
                Forced = forced
            });

            if (actionType == ActionType.Fold)
                return;

            float foldTarget;
            float checkCallTarget;
            float betRaiseTarget;

            BuildTargets(actionType, betRaiseChance, checkCallChance, out foldTarget, out checkCallTarget, out betRaiseTarget);

            float[] foldProb = new float[N_HOLECARDS];
            float[] checkCallProb = new float[N_HOLECARDS];
            float[] betRaiseProb = new float[N_HOLECARDS];

            BuildPreFlopActionModel(position, tableType, numPlayers, foldTarget, checkCallTarget, betRaiseTarget,
                foldProb, checkCallProb, betRaiseProb);

            PreFlopActionBucket observedBucket = ActionToBucket(actionType);

            for (int i = 0; i < N_HOLECARDS; i++)
            {
                float likelihood;

                if (observedBucket == PreFlopActionBucket.BetRaise)
                    likelihood = betRaiseProb[i];
                else if (observedBucket == PreFlopActionBucket.CheckCall)
                    likelihood = checkCallProb[i];
                else
                    likelihood = foldProb[i];

                Data[i].Equity *= likelihood;
            }

            Normalize();
        }

        private static void BuildTargets(ActionType actionType, float betRaiseChance, float checkCallChance,
            out float foldTarget, out float checkCallTarget, out float betRaiseTarget)
        {
            betRaiseTarget = ClampProbability(betRaiseChance);

            if (actionType == ActionType.Check || actionType == ActionType.Bet)
            {
                foldTarget = 0.0f;
                checkCallTarget = 1.0f - betRaiseTarget;
                return;
            }

            checkCallTarget = ClampProbability(checkCallChance);
            float sum = betRaiseTarget + checkCallTarget;

            if (sum > 1.0f)
            {
                betRaiseTarget /= sum;
                checkCallTarget /= sum;
                foldTarget = 0.0f;
            }
            else
            {
                foldTarget = 1.0f - sum;
            }
        }

        private void BuildPreFlopActionModel(Position position, TableType tableType, int numPlayers,
            float foldTarget, float checkCallTarget, float betRaiseTarget,
            float[] foldProb, float[] checkCallProb, float[] betRaiseProb)
        {
            const float eps = 1.0e-6f;

            for (int i = 0; i < N_HOLECARDS; i++)
            {
                if (Data[i].Equity <= 0.0f)
                {
                    foldProb[i] = eps;
                    checkCallProb[i] = eps;
                    betRaiseProb[i] = eps;
                    continue;
                }

                HoleCards hc = Data[i].GetHoleCards();
                PreFlopComboFeatures f = EvaluatePreFlopCombo(hc);

                float positionPressure = PositionTightness(position, tableType, numPlayers);

                double raiseLogit =
                    -5.25 +
                    10.80 * f.Score +
                    2.80 * f.Pairness +
                    1.25 * f.Broadway +
                    0.95 * f.AceHigh +
                    0.65 * f.Suitedness +
                    0.45 * f.Connectivity -
                    1.55 * f.DominationRisk -
                    1.35 * positionPressure;

                double callLogit =
                    -2.60 +
                    5.25 * f.Playability +
                    1.65 * f.Suitedness +
                    1.25 * f.Connectivity +
                    0.95 * f.Pairness +
                    0.55 * f.Wheel -
                    2.25 * Math.Max(0.0f, f.Score - 0.80f) -
                    0.65 * positionPressure;

                double foldLogit =
                    1.10 +
                    5.75 * (1.0f - f.Playability) +
                    1.70 * f.DominationRisk +
                    0.75 * positionPressure -
                    1.10 * f.Pairness;

                double maxLogit = Math.Max(foldLogit, Math.Max(callLogit, raiseLogit));
                double fb = Math.Exp(foldLogit - maxLogit) + eps;
                double cb = Math.Exp(callLogit - maxLogit) + eps;
                double rb = Math.Exp(raiseLogit - maxLogit) + eps;
                double sum = fb + cb + rb;

                foldProb[i] = (float)(fb / sum);
                checkCallProb[i] = (float)(cb / sum);
                betRaiseProb[i] = (float)(rb / sum);
            }

            CalibrateActionProbabilities(foldTarget, checkCallTarget, betRaiseTarget, foldProb, checkCallProb, betRaiseProb);
        }

        private void CalibrateActionProbabilities(float foldTarget, float checkCallTarget, float betRaiseTarget,
            float[] foldProb, float[] checkCallProb, float[] betRaiseProb)
        {
            const float eps = 1.0e-6f;

            for (int iteration = 0; iteration < 32; iteration++)
            {
                float currentFold = 0.0f;
                float currentCheckCall = 0.0f;
                float currentBetRaise = 0.0f;

                for (int i = 0; i < N_HOLECARDS; i++)
                {
                    float w = Data[i].Equity;
                    currentFold += w * foldProb[i];
                    currentCheckCall += w * checkCallProb[i];
                    currentBetRaise += w * betRaiseProb[i];
                }

                float foldScale = (foldTarget + eps) / (currentFold + eps);
                float checkCallScale = (checkCallTarget + eps) / (currentCheckCall + eps);
                float betRaiseScale = (betRaiseTarget + eps) / (currentBetRaise + eps);

                for (int i = 0; i < N_HOLECARDS; i++)
                {
                    float f = Math.Max(eps, foldProb[i] * foldScale);
                    float c = Math.Max(eps, checkCallProb[i] * checkCallScale);
                    float r = Math.Max(eps, betRaiseProb[i] * betRaiseScale);
                    float sum = f + c + r;

                    foldProb[i] = f / sum;
                    checkCallProb[i] = c / sum;
                    betRaiseProb[i] = r / sum;
                }
            }
        }

        private static PreFlopComboFeatures EvaluatePreFlopCombo(HoleCards hc)
        {
            int r0 = (int)hc.Card0.rank;
            int r1 = (int)hc.Card1.rank;

            int hi = Math.Max(r0, r1);
            int lo = Math.Min(r0, r1);

            bool pair = hi == lo;
            bool suited = hc.Card0.suit == hc.Card1.suit;
            int gap = pair ? 0 : Math.Max(0, hi - lo - 1);

            float hiNorm = NormalizeRank(hi);
            float loNorm = NormalizeRank(lo);
            float rankMass = (0.68f * hiNorm) + (0.32f * loNorm);

            float pairness = pair ? (0.45f + 0.55f * hiNorm) : 0.0f;
            float suitedness = (!pair && suited) ? 1.0f : 0.0f;
            float connectivity = pair ? 0.0f : (float)Math.Exp(-0.62 * gap);
            float broadway = (hi >= 10 && lo >= 10) ? 1.0f : 0.0f;
            float aceHigh = (hi == 14) ? 1.0f : 0.0f;
            float wheel = (!pair && hi == 14 && lo <= 5) ? 1.0f : 0.0f;
            float lowTrash = (!pair && hi <= 9 && lo <= 6 && !suited && gap >= 2) ? 1.0f : 0.0f;
            float dominationRisk = (!pair && hi >= 11 && lo <= 8 && !suited) ? (1.0f - loNorm) : lowTrash;

            float playability =
                (0.42f * rankMass) +
                (0.18f * pairness) +
                (0.15f * suitedness) +
                (0.13f * connectivity) +
                (0.08f * broadway) +
                (0.05f * wheel) -
                (0.12f * dominationRisk);

            float score =
                (0.56f * rankMass) +
                (0.24f * pairness) +
                (0.07f * suitedness) +
                (0.05f * connectivity) +
                (0.07f * broadway) +
                (0.05f * aceHigh) -
                (0.08f * dominationRisk);

            return new PreFlopComboFeatures
            {
                Score = Clamp01(score),
                Playability = Clamp01(playability),
                Pairness = Clamp01(pairness),
                Suitedness = suitedness,
                Connectivity = Clamp01(connectivity),
                Broadway = broadway,
                AceHigh = aceHigh,
                Wheel = wheel,
                DominationRisk = Clamp01(dominationRisk)
            };
        }

        private static float PositionTightness(Position position, TableType tableType, int numPlayers)
        {
            if (tableType == TableType.HeadsUp || numPlayers <= 2)
            {
                if (position == Position.SB || position == Position.BU)
                    return -0.25f;

                if (position == Position.BB)
                    return 0.05f;
            }

            switch (position)
            {
                case Position.UTG: return 0.42f;
                case Position.HJ:  return 0.25f;
                case Position.CO:  return 0.10f;
                case Position.BU:  return -0.12f;
                case Position.SB:  return 0.18f;
                case Position.BB:  return 0.06f;
                default:           return 0.20f;
            }
        }

        private static PreFlopActionBucket ActionToBucket(ActionType actionType)
        {
            if (actionType == ActionType.Bet || actionType == ActionType.Raise || actionType == ActionType.AllIn)
                return PreFlopActionBucket.BetRaise;

            if (actionType == ActionType.Check || actionType == ActionType.Call)
                return PreFlopActionBucket.CheckCall;

            return PreFlopActionBucket.Fold;
        }

        private static float NormalizeRank(int rank)
        {
            return Clamp01((rank - 2) / 12.0f);
        }

        private static float ClampProbability(float x)
        {
            if (float.IsNaN(x) || float.IsInfinity(x))
                return 0.0f;

            if (x < 0.0f)
                return 0.0f;

            if (x > 1.0f)
                return 1.0f;

            return x;
        }

        private static float Clamp01(float x)
        {
            if (x < 0.0f)
                return 0.0f;

            if (x > 1.0f)
                return 1.0f;

            return x;
        }

        public Dictionary<string, float> GetCompressedRange()
        {
            Dictionary<string, float> result = new Dictionary<string, float>();

            for (var i = 0; i < N_HOLECARDS; i++)
            {
                var holecards = Data[i].GetHoleCards();
                string key;

                if (holecards.Card0.rank > holecards.Card1.rank)
                {
                    key = $"{holecards.Card0.rank}{holecards.Card1.rank}";
                }
                else
                {
                    key = $"{holecards.Card1.rank}{holecards.Card0.rank}";
                }

                if (holecards.Card0.rank != holecards.Card1.rank)
                {
                    if (holecards.Card0.suit == holecards.Card1.suit)
                    {
                        key += "s";
                    }
                    else
                    {
                        key += "o";
                    }
                }

                if (!result.ContainsKey(key))
                    result[key] = 0;

                result[key] += Data[i].Equity;
            }

            return result;
        }
    }
}
