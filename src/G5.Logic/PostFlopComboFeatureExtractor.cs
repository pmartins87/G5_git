using System;
using System.Collections.Generic;

namespace G5.Logic
{
    public static class PostFlopComboFeatureExtractor
    {
        public static ComboFeatureVector Extract(int comboIndex, Board board, PostFlopLineContext context)
        {
            HoleCards holeCards = new HoleCards(comboIndex);
            Card c0 = holeCards.Card0;
            Card c1 = holeCards.Card1;

            BoardTextureSummary texture = AnalyzeBoard(board);

            int[] rankCount = new int[15];
            int[] suitCount = new int[4];
            bool[] ranks = new bool[15];

            AddBoardCards(board, rankCount, suitCount, ranks);
            AddCard(c0, rankCount, suitCount, ranks);
            AddCard(c1, rankCount, suitCount, ranks);

            int pairs = 0;
            int trips = 0;
            int quads = 0;
            bool anyPair = false;
            bool topPair = false;
            bool secondPair = false;
            bool overPair = false;
            bool holePair = RankValue(c0) == RankValue(c1);

            for (int r = 2; r <= 14; r++)
            {
                if (rankCount[r] == 2)
                {
                    pairs++;
                    anyPair = true;
                }
                else if (rankCount[r] == 3)
                {
                    trips++;
                    anyPair = true;
                }
                else if (rankCount[r] >= 4)
                {
                    quads++;
                    anyPair = true;
                }
            }

            int r0 = RankValue(c0);
            int r1 = RankValue(c1);

            if (texture.MaxRank > 0 && (r0 == texture.MaxRank || r1 == texture.MaxRank))
                topPair = rankCount[texture.MaxRank] >= 2;

            if (texture.SecondRank > 0 && (r0 == texture.SecondRank || r1 == texture.SecondRank))
                secondPair = rankCount[texture.SecondRank] >= 2;

            if (holePair && r0 > texture.MaxRank)
                overPair = true;

            bool fullHouse = (trips > 0 && pairs > 0) || trips > 1;
            bool straight = HasStraight(ranks);
            int maxSuit = 0;

            for (int s = 0; s < suitCount.Length; s++)
                maxSuit = Math.Max(maxSuit, suitCount[s]);

            bool flush = maxSuit >= 5;
            bool flushDraw = board != null && board.Count < 5 && maxSuit == 4;
            bool backdoorFlush = board != null && board.Count == 3 && maxSuit == 3;
            int straightWindow = BestStraightWindowCount(ranks);
            bool straightDraw = board != null && board.Count < 5 && !straight && straightWindow >= 4;
            bool backdoorStraight = board != null && board.Count == 3 && !straight && straightWindow == 3;

            int highHoleCount = 0;
            int overCards = 0;
            float blocker = 0.0f;

            Card[] hole = { c0, c1 };
            foreach (Card card in hole)
            {
                int r = RankValue(card);

                if (r >= 12)
                    highHoleCount++;

                if (texture.MaxRank > 0 && r > texture.MaxRank)
                    overCards++;

                if (r == 14)
                    blocker += 0.18f;
                else if (r == 13)
                    blocker += 0.10f;
                else if (r == 12)
                    blocker += 0.06f;
            }

            ComboFeatureVector f = new ComboFeatureVector
            {
                ComboIndex = comboIndex,
                BoardWetness = Clamp01(texture.Wetness),
                BoardPairing = texture.Paired ? 1.0f : 0.0f,
                Position = context != null && context.ActorInPosition ? 1.0f : 0.0f,
                Multiway = context != null && context.IsMultiway ? 1.0f : 0.0f,
                SizePressure = context != null ? Clamp01(Math.Max(context.BetToPotRatio, context.CallToPotRatio)) : 0.0f,
                SprPressure = context != null ? Clamp01(1.0f / Math.Max(1.0f, context.Spr)) : 0.0f,
                Overcards = Clamp01(overCards / 2.0f),
                BlockerScore = Clamp01(blocker)
            };

            if (flush)
            {
                f.MadeScore = 0.88f;
                f.NutPotential += 0.35f;
            }
            else if (fullHouse || quads > 0)
            {
                f.MadeScore = 0.96f;
                f.NutPotential += 0.45f;
            }
            else if (straight)
            {
                f.MadeScore = 0.82f;
                f.NutPotential += 0.28f;
            }
            else if (trips > 0)
            {
                f.MadeScore = 0.76f;
                f.NutPotential += 0.18f;
            }
            else if (pairs >= 2)
            {
                f.MadeScore = 0.68f;
                f.NutPotential += 0.12f;
            }
            else if (overPair)
            {
                f.MadeScore = 0.64f;
            }
            else if (topPair)
            {
                f.MadeScore = 0.52f;
            }
            else if (secondPair)
            {
                f.MadeScore = 0.42f;
            }
            else if (anyPair)
            {
                f.MadeScore = 0.34f;
            }
            else
            {
                f.MadeScore = 0.12f + 0.06f * highHoleCount;
            }

            if (flushDraw)
            {
                f.DrawScore += 0.42f;
                f.NutPotential += 0.18f;
            }
            else if (backdoorFlush)
            {
                f.DrawScore += 0.08f;
            }

            if (straightDraw)
            {
                f.DrawScore += 0.34f;
                f.NutPotential += 0.12f;
            }
            else if (backdoorStraight)
            {
                f.DrawScore += 0.08f;
            }

            if (!anyPair && !straight && !flush)
                f.DrawScore += 0.07f * overCards;

            if (f.MadeScore >= 0.65f)
                f.ShowdownScore = 0.65f + 0.35f * f.MadeScore;
            else
                f.ShowdownScore = 0.55f * f.MadeScore + 0.45f * f.DrawScore;

            if ((topPair || overPair || secondPair) && texture.Wetness > 0.0f)
                f.Vulnerability = Clamp01(texture.Wetness * (0.35f + 0.35f * (1.0f - f.DrawScore)));

            if (!anyPair && !straight && !flush && f.DrawScore < 0.20f)
                f.Vulnerability += 0.15f;

            f.MadeScore = Clamp01(f.MadeScore);
            f.ShowdownScore = Clamp01(f.ShowdownScore);
            f.DrawScore = Clamp01(f.DrawScore);
            f.NutPotential = Clamp01(f.NutPotential);
            f.BlockerScore = Clamp01(f.BlockerScore);
            f.Vulnerability = Clamp01(f.Vulnerability);
            f.AirScore = Clamp01(1.0f - Math.Max(f.MadeScore, f.DrawScore));

            return f;
        }

        private sealed class BoardTextureSummary
        {
            public int MaxRank;
            public int SecondRank;
            public bool Paired;
            public bool TwoTone;
            public bool Monotone;
            public bool Connected;
            public float Wetness;
        }

        private static BoardTextureSummary AnalyzeBoard(Board board)
        {
            BoardTextureSummary t = new BoardTextureSummary();

            if (board == null)
                return t;

            int[] ranks = new int[15];
            int[] suits = new int[4];
            bool[] hasRank = new bool[15];

            AddBoardCards(board, ranks, suits, hasRank);

            for (int r = 14; r >= 2; r--)
            {
                if (ranks[r] > 0)
                {
                    if (t.MaxRank == 0)
                        t.MaxRank = r;
                    else if (t.SecondRank == 0)
                        t.SecondRank = r;
                }

                if (ranks[r] >= 2)
                    t.Paired = true;
            }

            int maxSuit = 0;
            for (int s = 0; s < suits.Length; s++)
                maxSuit = Math.Max(maxSuit, suits[s]);

            t.Monotone = board.Count >= 3 && maxSuit >= 3;
            t.TwoTone = board.Count >= 3 && maxSuit == 2;
            t.Connected = BestStraightWindowCount(hasRank) >= 3;

            if (t.Monotone)
                t.Wetness += 0.35f;
            else if (t.TwoTone)
                t.Wetness += 0.18f;

            if (t.Connected)
                t.Wetness += 0.25f;

            if (t.Paired)
                t.Wetness += 0.10f;

            t.Wetness = Clamp01(t.Wetness);
            return t;
        }

        private static void AddBoardCards(Board board, int[] rankCount, int[] suitCount, bool[] ranks)
        {
            if (board == null)
                return;

            foreach (Card card in board.Cards)
                AddCard(card, rankCount, suitCount, ranks);
        }

        private static void AddCard(Card card, int[] rankCount, int[] suitCount, bool[] ranks)
        {
            int r = RankValue(card);
            int s = SuitValue(card);

            if (r >= 2 && r <= 14)
            {
                rankCount[r]++;
                ranks[r] = true;

                if (r == 14)
                    ranks[1] = true;
            }

            if (s >= 0 && s < suitCount.Length)
                suitCount[s]++;
        }

        private static bool HasStraight(bool[] ranks)
        {
            return BestStraightWindowCount(ranks) >= 5;
        }

        private static int BestStraightWindowCount(int[] rankCounts)
        {
            bool[] has = new bool[15];

            for (int r = 2; r <= 14; r++)
                has[r] = rankCounts[r] > 0;

            if (has[14])
                has[1] = true;

            return BestStraightWindowCount(has);
        }

        private static int BestStraightWindowCount(bool[] ranks)
        {
            int best = 0;

            for (int high = 14; high >= 5; high--)
            {
                int count = 0;

                for (int r = high - 4; r <= high; r++)
                {
                    if (r >= 0 && r < ranks.Length && ranks[r])
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

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0.0f;

            if (value < 0.0f)
                return 0.0f;

            if (value > 1.0f)
                return 1.0f;

            return value;
        }
    }
}
