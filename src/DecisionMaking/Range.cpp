#include "Range.h"
#include <math.h>
#include <float.h>
#include "HandStrengthCounter.h"
#include "PreFlopEquity.h"
#include <algorithm>
#include <cstring>
#include <stdexcept>
#include <mmintrin.h>
#include <emmintrin.h>


namespace G5Cpp
{
    const int NORMALIZE_ITERATIONS = 1; // Default: 8
    const float SMOOTH_DELTA = 0.5f; // Default: 0.5
    const float MIN_CHANCE = 1.0f / (N_HOLECARDS_FLOP * N_HOLECARDS_FLOP);

    namespace
    {
        const float POSTFLOP_EPS = 0.0001f;
        const float TARGET_FLOOR = 0.003f;

        inline float ClampFloat(float value, float minValue, float maxValue)
        {
            if (value < minValue)
                return minValue;

            if (value > maxValue)
                return maxValue;

            return value;
        }

        inline float Clamp01(float value)
        {
            return ClampFloat(value, 0.0f, 1.0f);
        }

        inline float SafeProbability(float value)
        {
            if (!_finite(value))
                return 0.5f;

            return ClampFloat(value, POSTFLOP_EPS, 1.0f - POSTFLOP_EPS);
        }

        inline float PositiveWeight(float value)
        {
            if (!_finite(value) || value < POSTFLOP_EPS)
                return POSTFLOP_EPS;

            return value;
        }

        inline float AllInLikelihoodFromAggression(float aggressiveProbability)
        {
            aggressiveProbability = ClampFloat(aggressiveProbability, POSTFLOP_EPS, 1.0f - POSTFLOP_EPS);

            // All-in is not just a normal raise: it should be a more selective/polarized
            // observation. Squaring preserves monotonicity while compressing marginal
            // raise candidates and keeping very aggressive combos alive.
            return ClampFloat(0.20f * aggressiveProbability + 0.80f * aggressiveProbability * aggressiveProbability,
                POSTFLOP_EPS, 1.0f - POSTFLOP_EPS);
        }

        float NormalizeTarget(float value)
        {
            if (!_finite(value))
                value = 0.0f;

            return ClampFloat(value, TARGET_FLOOR, 1.0f - TARGET_FLOOR);
        }

        void NormalizeThreeTargets(float& foldChance, float& callChance, float& raiseChance)
        {
            foldChance = NormalizeTarget(foldChance);
            callChance = NormalizeTarget(callChance);
            raiseChance = NormalizeTarget(raiseChance);

            float sum = foldChance + callChance + raiseChance;

            if (sum <= 0.0f || !_finite(sum))
            {
                foldChance = 0.34f;
                callChance = 0.33f;
                raiseChance = 0.33f;
                return;
            }

            foldChance /= sum;
            callChance /= sum;
            raiseChance /= sum;
        }

        void NormalizeBoardRanks(const Board& board, bool* ranks)
        {
            for (int i = 0; i < 15; i++)
                ranks[i] = false;

            for (int i = 0; i < board.size; i++)
            {
                int r = (int)board.card[i].rank();

                if (r >= 2 && r <= 14)
                {
                    ranks[r] = true;

                    if (r == (int)Card::Ace)
                        ranks[1] = true;
                }
            }
        }

        bool HasStraight(const bool* ranks)
        {
            for (int high = 14; high >= 5; high--)
            {
                bool hasStraight = true;

                for (int r = high - 4; r <= high; r++)
                {
                    if (!ranks[r])
                    {
                        hasStraight = false;
                        break;
                    }
                }

                if (hasStraight)
                    return true;
            }

            return false;
        }

        int BestStraightWindowCount(const bool* ranks)
        {
            int best = 0;

            for (int high = 14; high >= 5; high--)
            {
                int count = 0;

                for (int r = high - 4; r <= high; r++)
                {
                    if (ranks[r])
                        count++;
                }

                if (count > best)
                    best = count;
            }

            return best;
        }

        struct BoardTexture
        {
            int rankCount[15];
            int suitCount[4];
            int maxRank;
            int secondRank;
            bool paired;
            bool twoTone;
            bool monotone;
            bool connected;
            float wetness;
        };

        BoardTexture AnalyzeBoard(const Board& board)
        {
            BoardTexture texture;

            for (int i = 0; i < 15; i++)
                texture.rankCount[i] = 0;

            for (int i = 0; i < 4; i++)
                texture.suitCount[i] = 0;

            texture.maxRank = 0;
            texture.secondRank = 0;
            texture.paired = false;
            texture.twoTone = false;
            texture.monotone = false;
            texture.connected = false;
            texture.wetness = 0.0f;

            bool ranks[15];
            NormalizeBoardRanks(board, ranks);

            for (int i = 0; i < board.size; i++)
            {
                int r = (int)board.card[i].rank();
                int s = (int)board.card[i].suit();

                if (r >= 2 && r <= 14)
                    texture.rankCount[r]++;

                if (s >= 0 && s < 4)
                    texture.suitCount[s]++;
            }

            for (int r = 14; r >= 2; r--)
            {
                if (texture.rankCount[r] > 0)
                {
                    if (texture.maxRank == 0)
                        texture.maxRank = r;
                    else if (texture.secondRank == 0)
                        texture.secondRank = r;
                }

                if (texture.rankCount[r] >= 2)
                    texture.paired = true;
            }

            int maxSuitCount = 0;

            for (int s = 0; s < 4; s++)
            {
                if (texture.suitCount[s] > maxSuitCount)
                    maxSuitCount = texture.suitCount[s];
            }

            texture.monotone = (board.size >= 3 && maxSuitCount >= 3);
            texture.twoTone = (board.size >= 3 && maxSuitCount == 2);
            texture.connected = (BestStraightWindowCount(ranks) >= 3);

            texture.wetness = 0.0f;

            if (texture.monotone)
                texture.wetness += 0.35f;
            else if (texture.twoTone)
                texture.wetness += 0.18f;

            if (texture.connected)
                texture.wetness += 0.25f;

            if (texture.paired)
                texture.wetness += 0.10f;

            texture.wetness = Clamp01(texture.wetness);
            return texture;
        }

        struct PostflopComboFeatures
        {
            float rankScore;
            float madeScore;
            float showdownScore;
            float drawScore;
            float nutPotential;
            float blockerScore;
            float vulnerability;
            float boardWetness;
        };

        PostflopComboFeatures BuildPostflopFeatures(int hcInd, float rankScore, const Board& board)
        {
            PostflopComboFeatures f;
            f.rankScore = Clamp01(rankScore);
            f.madeScore = 0.0f;
            f.showdownScore = 0.0f;
            f.drawScore = 0.0f;
            f.nutPotential = 0.0f;
            f.blockerScore = 0.0f;
            f.vulnerability = 0.0f;
            f.boardWetness = 0.0f;

            BoardTexture texture = AnalyzeBoard(board);
            f.boardWetness = texture.wetness;

            Card c0(hcInd / 52);
            Card c1(hcInd % 52);

            int rankCount[15];
            int suitCount[4];
            bool ranks[15];

            for (int i = 0; i < 15; i++)
            {
                rankCount[i] = 0;
                ranks[i] = false;
            }

            for (int i = 0; i < 4; i++)
                suitCount[i] = 0;

            for (int i = 0; i < board.size; i++)
            {
                int r = (int)board.card[i].rank();
                int s = (int)board.card[i].suit();

                if (r >= 2 && r <= 14)
                {
                    rankCount[r]++;
                    ranks[r] = true;

                    if (r == (int)Card::Ace)
                        ranks[1] = true;
                }

                if (s >= 0 && s < 4)
                    suitCount[s]++;
            }

            Card holeCards[2] = { c0, c1 };

            for (int i = 0; i < 2; i++)
            {
                int r = (int)holeCards[i].rank();
                int s = (int)holeCards[i].suit();

                if (r >= 2 && r <= 14)
                {
                    rankCount[r]++;
                    ranks[r] = true;

                    if (r == (int)Card::Ace)
                        ranks[1] = true;
                }

                if (s >= 0 && s < 4)
                    suitCount[s]++;
            }

            int pairs = 0;
            int trips = 0;
            int quads = 0;
            bool anyPair = false;
            bool topPair = false;
            bool secondPair = false;
            bool overPair = false;
            bool holePair = (c0.rank() == c1.rank());

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

            if ((int)c0.rank() == texture.maxRank || (int)c1.rank() == texture.maxRank)
                topPair = rankCount[texture.maxRank] >= 2;

            if (texture.secondRank > 0 && ((int)c0.rank() == texture.secondRank || (int)c1.rank() == texture.secondRank))
                secondPair = rankCount[texture.secondRank] >= 2;

            if (holePair && (int)c0.rank() > texture.maxRank)
                overPair = true;

            bool fullHouse = (trips > 0 && pairs > 0) || (trips > 1);
            bool straight = HasStraight(ranks);

            int maxSuit = 0;

            for (int s = 0; s < 4; s++)
            {
                if (suitCount[s] > maxSuit)
                    maxSuit = suitCount[s];
            }

            bool flush = maxSuit >= 5;
            bool flushDraw = (board.size < 5 && maxSuit == 4);
            bool backdoorFlush = (board.size == 3 && maxSuit == 3);
            int straightWindow = BestStraightWindowCount(ranks);
            bool straightDraw = (board.size < 5 && !straight && straightWindow >= 4);
            bool backdoorStraight = (board.size == 3 && !straight && straightWindow == 3);

            int highHoleCount = 0;
            int overCards = 0;

            for (int i = 0; i < 2; i++)
            {
                int r = (int)holeCards[i].rank();

                if (r >= (int)Card::Queen)
                    highHoleCount++;

                if (r > texture.maxRank)
                    overCards++;

                if (r == (int)Card::Ace)
                    f.blockerScore += 0.18f;
                else if (r == (int)Card::King)
                    f.blockerScore += 0.10f;
                else if (r == (int)Card::Queen)
                    f.blockerScore += 0.06f;
            }

            if (flush)
            {
                f.madeScore = 0.88f;
                f.nutPotential += 0.35f;
            }
            else if (fullHouse || quads)
            {
                f.madeScore = 0.96f;
                f.nutPotential += 0.45f;
            }
            else if (straight)
            {
                f.madeScore = 0.82f;
                f.nutPotential += 0.28f;
            }
            else if (trips > 0)
            {
                f.madeScore = 0.76f;
                f.nutPotential += 0.18f;
            }
            else if (pairs >= 2)
            {
                f.madeScore = 0.68f;
                f.nutPotential += 0.12f;
            }
            else if (overPair)
            {
                f.madeScore = 0.64f;
            }
            else if (topPair)
            {
                f.madeScore = 0.52f;
            }
            else if (secondPair)
            {
                f.madeScore = 0.42f;
            }
            else if (anyPair)
            {
                f.madeScore = 0.34f;
            }
            else
            {
                f.madeScore = 0.12f + 0.06f * highHoleCount;
            }

            if (flushDraw)
            {
                f.drawScore += 0.42f;
                f.nutPotential += 0.18f;
            }
            else if (backdoorFlush)
            {
                f.drawScore += 0.08f;
            }

            if (straightDraw)
            {
                f.drawScore += 0.34f;
                f.nutPotential += 0.12f;
            }
            else if (backdoorStraight)
            {
                f.drawScore += 0.08f;
            }

            if (!anyPair && !straight && !flush)
                f.drawScore += 0.07f * overCards;

            if (f.madeScore >= 0.65f)
                f.showdownScore = 0.65f + 0.35f * f.rankScore;
            else
                f.showdownScore = 0.55f * f.rankScore + 0.45f * f.madeScore;

            if ((topPair || overPair || secondPair) && texture.wetness > 0.0f)
                f.vulnerability = Clamp01(texture.wetness * (0.35f + 0.35f * (1.0f - f.drawScore)));

            if (!anyPair && !straight && !flush && f.drawScore < 0.20f)
                f.vulnerability += 0.15f;

            f.madeScore = Clamp01(std::max(f.madeScore, 0.55f * f.rankScore));
            f.showdownScore = Clamp01(f.showdownScore);
            f.drawScore = Clamp01(f.drawScore);
            f.nutPotential = Clamp01(f.nutPotential);
            f.blockerScore = Clamp01(f.blockerScore);
            f.vulnerability = Clamp01(f.vulnerability);

            return f;
        }

        void BuildRankScoreByHand(float* rankScoreByHand, const SortedHoleCards& sortedHoleCards)
        {
            for (int i = 0; i < N_HOLECARDS_DOUBLE; i++)
                rankScoreByHand[i] = 0.5f;

            float denom = (sortedHoleCards.length > 1) ? (float)(sortedHoleCards.length - 1) : 1.0f;

            for (int i = 0; i < sortedHoleCards.length; i++)
            {
                int ind = sortedHoleCards.ind[i];
                rankScoreByHand[ind] = 1.0f - ((float)i / denom);
            }
        }

        void CalibrateBinary(float* prob, const float* rawProb, const float* weights, int length, float target)
        {
            target = NormalizeTarget(target);

            float mass = 0.0f;

            for (int i = 0; i < length; i++)
            {
                if (_finite(weights[i]) && weights[i] > 0.0f)
                    mass += weights[i];
            }

            if (mass <= 0.0f || !_finite(mass))
                throw std::runtime_error("Range::CalibrateBinary: zero probability mass");

            float low = 0.000001f;
            float high = 1000000.0f;

            for (int it = 0; it < 50; it++)
            {
                float scale = sqrtf(low * high);
                float mean = 0.0f;

                for (int i = 0; i < length; i++)
                {
                    float p = SafeProbability(rawProb[i]);
                    float odds = p / (1.0f - p);
                    float calibrated = (scale * odds) / (1.0f + scale * odds);
                    mean += weights[i] * calibrated;
                }

                mean /= mass;

                if (mean < target)
                    low = scale;
                else
                    high = scale;
            }

            float scale = sqrtf(low * high);

            for (int i = 0; i < length; i++)
            {
                float p = SafeProbability(rawProb[i]);
                float odds = p / (1.0f - p);
                prob[i] = SafeProbability((scale * odds) / (1.0f + scale * odds));
            }
        }

        void CalibrateTriple(float* foldProb, float* callProb, float* raiseProb,
            const float* rawFold, const float* rawCall, const float* rawRaise,
            const float* weights, int length, float foldTarget, float callTarget, float raiseTarget)
        {
            NormalizeThreeTargets(foldTarget, callTarget, raiseTarget);

            float mass = 0.0f;

            for (int i = 0; i < length; i++)
            {
                if (_finite(weights[i]) && weights[i] > 0.0f)
                    mass += weights[i];
            }

            if (mass <= 0.0f || !_finite(mass))
                throw std::runtime_error("Range::CalibrateTriple: zero probability mass");

            float lambdaFold = 1.0f;
            float lambdaCall = 1.0f;
            float lambdaRaise = 1.0f;

            for (int it = 0; it < 80; it++)
            {
                float curFold = 0.0f;
                float curCall = 0.0f;
                float curRaise = 0.0f;

                for (int i = 0; i < length; i++)
                {
                    float f = lambdaFold * PositiveWeight(rawFold[i]);
                    float c = lambdaCall * PositiveWeight(rawCall[i]);
                    float r = lambdaRaise * PositiveWeight(rawRaise[i]);
                    float sum = f + c + r;

                    curFold += weights[i] * (f / sum);
                    curCall += weights[i] * (c / sum);
                    curRaise += weights[i] * (r / sum);
                }

                curFold /= mass;
                curCall /= mass;
                curRaise /= mass;

                lambdaFold *= foldTarget / std::max(curFold, POSTFLOP_EPS);
                lambdaCall *= callTarget / std::max(curCall, POSTFLOP_EPS);
                lambdaRaise *= raiseTarget / std::max(curRaise, POSTFLOP_EPS);

                float maxLambda = std::max(lambdaFold, std::max(lambdaCall, lambdaRaise));
                float minLambda = std::min(lambdaFold, std::min(lambdaCall, lambdaRaise));

                if (maxLambda > 1000000.0f || minLambda < 0.000001f)
                {
                    lambdaFold /= maxLambda;
                    lambdaCall /= maxLambda;
                    lambdaRaise /= maxLambda;
                }
            }

            for (int i = 0; i < length; i++)
            {
                float f = lambdaFold * PositiveWeight(rawFold[i]);
                float c = lambdaCall * PositiveWeight(rawCall[i]);
                float r = lambdaRaise * PositiveWeight(rawRaise[i]);
                float sum = f + c + r;

                foldProb[i] = SafeProbability(f / sum);
                callProb[i] = SafeProbability(c / sum);
                raiseProb[i] = SafeProbability(r / sum);

                float norm = foldProb[i] + callProb[i] + raiseProb[i];
                foldProb[i] /= norm;
                callProb[i] /= norm;
                raiseProb[i] /= norm;
            }
        }

        void BuildPostflopCheckBetProb(float* betProb, const int* hcInd, const float* likelihood, int length,
            const SortedHoleCards& sortedHoleCards, const Board& board, float betChance)
        {
            float rankScoreByHand[N_HOLECARDS_DOUBLE];
            float rawBet[N_HOLECARDS];
            BuildRankScoreByHand(rankScoreByHand, sortedHoleCards);

            for (int i = 0; i < length; i++)
            {
                PostflopComboFeatures f = BuildPostflopFeatures(hcInd[i], rankScoreByHand[hcInd[i]], board);

                float valueBet = 0.18f + 0.72f * f.madeScore + 0.22f * f.nutPotential;
                float protectionBet = 0.18f * f.vulnerability * std::max(f.showdownScore, f.madeScore);
                float semiBluffBet = 0.28f * f.drawScore + 0.12f * f.blockerScore;
                float trapPenalty = 0.16f * f.madeScore * (1.0f - f.boardWetness);
                float airPenalty = 0.18f * (1.0f - f.showdownScore) * (1.0f - f.drawScore);

                rawBet[i] = SafeProbability(valueBet + protectionBet + semiBluffBet - trapPenalty - airPenalty);
            }

            CalibrateBinary(betProb, rawBet, likelihood, length, betChance);
        }

        void BuildPostflopFoldCallRaiseProb(float* foldProb, float* callProb, float* raiseProb,
            const int* hcInd, const float* likelihood, int length, const SortedHoleCards& sortedHoleCards,
            const Board& board, float raiseChance, float callChance)
        {
            float rankScoreByHand[N_HOLECARDS_DOUBLE];
            float rawFold[N_HOLECARDS];
            float rawCall[N_HOLECARDS];
            float rawRaise[N_HOLECARDS];
            BuildRankScoreByHand(rankScoreByHand, sortedHoleCards);

            float foldChance = 1.0f - raiseChance - callChance;

            for (int i = 0; i < length; i++)
            {
                PostflopComboFeatures f = BuildPostflopFeatures(hcInd[i], rankScoreByHand[hcInd[i]], board);

                float valueRaise = 0.10f + 0.95f * f.madeScore + 0.24f * f.nutPotential;
                float semiBluffRaise = 0.42f * f.drawScore + 0.16f * f.blockerScore;
                float raisePenalty = 0.28f * (1.0f - f.showdownScore) * (1.0f - f.drawScore);

                rawRaise[i] = PositiveWeight(valueRaise + semiBluffRaise - raisePenalty);

                float bluffCatcher = 0.22f + 0.78f * f.showdownScore;
                float drawingCall = 0.20f + 0.70f * f.drawScore;
                float callPenalty = 0.18f * f.vulnerability * (1.0f - f.drawScore);

                rawCall[i] = PositiveWeight(std::max(bluffCatcher, drawingCall) - callPenalty);

                float weakNoDraw = (1.0f - f.showdownScore) * (1.0f - f.drawScore);
                float dominatedPair = f.vulnerability * (1.0f - f.nutPotential);

                rawFold[i] = PositiveWeight(0.08f + 0.95f * weakNoDraw + 0.25f * dominatedPair - 0.22f * f.blockerScore);
            }

            CalibrateTriple(foldProb, callProb, raiseProb, rawFold, rawCall, rawRaise,
                likelihood, length, foldChance, callChance, raiseChance);
        }
    }

    Range::ExpSmoothTable Range::_smoothTable = Range::ExpSmoothTable();

    Range::Range()
    {
        // TODO: Note deffault constructor is slow... Not using it...
    }

    Range::Range(const Range& oldRange)
    {
        assert (oldRange._length > 0);
        assert (oldRange._length <= N_HOLECARDS);

        _length = oldRange._length;

        std::memcpy(_hcInd, oldRange._hcInd, _length * sizeof(_hcInd[0]));
        std::memcpy(_likelihood, oldRange._likelihood, _length * sizeof(_likelihood[0]));
    }

    void Range::fromSortedHoleCards(const SortedHoleCards& thatData)
    {
        assert (thatData.length > 0);
        assert (thatData.length <= N_HOLECARDS);

        _length = thatData.length;

        std::memcpy(_hcInd, thatData.ind, _length * sizeof(_hcInd[0]));
        std::memcpy(_likelihood, thatData.equity, _length * sizeof(_likelihood[0]));
    }

    std::shared_ptr<Range> Range::banCard(const Card& card) const
    {
        auto newRange = std::make_shared<Range>(*this);
        int cardInd = card.toInt();

        for (int i = 0; i < _length; i++)
        {
            int c1 = _hcInd[i] / 52;
            int c2 = _hcInd[i] % 52;

            if (c1 == cardInd || c2 == cardInd)
                newRange->_likelihood[i] = 0.0f;
        }

        newRange->normalize();
        return newRange;
    }

    namespace
    {
        union M128_Access
        {
            __m128 v;
            float f[4];
        };

        float M128_getByIndex(__m128 vec, int i)
        {
            if (i >= 0 && i < 4)
            {
                M128_Access access;
                access.v = vec;
                return access.f[i];
            }

            return 0.0f;
        }
    }

    void Range::normalize()
    {
        float sum = 0.0f;

        for (int i = 0; i < _length; i++)
        {
            if (!_finite(_likelihood[i]) || _likelihood[i] < 0.0f)
                _likelihood[i] = 0.0f;

            sum += _likelihood[i];
        }

        assert(sum > 0.0f);

        if (sum <= 0.0f)
            throw std::runtime_error("Range::normalize: zero probability mass after range update");

        float norm_float = 1.0f / sum;

        if (USE_SSE)
        {
            int length4 = (_length / 4) * 4;
            __m128 norm = _mm_set1_ps(norm_float);

            for (int i = 0; i < length4; i+=4)
            {
                _mm_storeu_ps(&_likelihood[i], _mm_mul_ps(norm, _mm_loadu_ps(&_likelihood[i])));
            }

            for (int i = length4; i < _length; i++)
            {
                _likelihood[i] *= norm_float;
            }
        }
        else
        {
            for (int i = 0; i < _length; i++)
            {
                _likelihood[i] *= norm_float;
            }
        }
    }

    /// Hole cards with same probability get THE SAME multiplier
    void Range::cutRange_CalcMultiplier(float* multiplier, const SortedHoleCards& sortedHoleCards, const float* distribution) const
    {
        float holeCardEquity[N_HOLECARDS_DOUBLE];

        for (int i = 0; i < _length; i++)
        {
            int ind = _hcInd[i];
            holeCardEquity[ind] = _likelihood[i];
            multiplier[ind] = 0.0f;
        }

        float lenf = (float) (sortedHoleCards.length - 1);
        float sumEq = 0.0f;

        for (int i = 0; i < sortedHoleCards.length; )
        {
            float nextSumEq = sumEq;
            int k = i;

            while (k < sortedHoleCards.length && sortedHoleCards.equity[i] == sortedHoleCards.equity[k])
            {
                nextSumEq += holeCardEquity[sortedHoleCards.ind[k]];
                k++;
            }

            int start = (int)(sumEq  * lenf);
            int end = (int)(nextSumEq * lenf);

            float mul = (distribution[start] + distribution[end]) * 0.5f;

            for (int j = i; j < k; j++)
            {
                multiplier[sortedHoleCards.ind[j]] = mul;
            }

            sumEq = nextSumEq;
            i = k;
        }
    }

    void Range::cutRange(const SortedHoleCards& sortedHoleCards, const float* distribution)
    {
        float multiplier[N_HOLECARDS_DOUBLE];
        cutRange_CalcMultiplier(multiplier, sortedHoleCards, distribution);

        for (int i = 0; i < _length; i++)
        {
            int ind = _hcInd[i];
            _likelihood[i] *= multiplier[ind];
        }

        normalize();
    }

    float Range::predictAction(const SortedHoleCards& sortedHoleCards, const float* distribution) const
    {
        float multiplier[N_HOLECARDS_DOUBLE];
        cutRange_CalcMultiplier(multiplier, sortedHoleCards, distribution);

        float sum = 0;

        for (int i = 0; i < _length; i++)
        {
            int ind = _hcInd[i];
            sum += _likelihood[i] * multiplier[ind];
        }

        return sum;
    }

    std::shared_ptr<Range> Range::cutCheckBet(ActionType actionType, const Board& board, float betChance, const GameContext& gc) const
    {
        auto sortedHoleCards = gc.sortedHoleCards(board);
        auto newRange = std::make_shared<Range>(*this);

        if (board.street() == Street_PreFlop)
        {
            float checkDistribution[N_HOLECARDS];
            float betDistribution[N_HOLECARDS];

            actionProbDist_CheckBet(checkDistribution, betDistribution, sortedHoleCards.length, board.street(), betChance);

            if (actionType == Action_Check)
            {
                newRange->cutRange(sortedHoleCards, checkDistribution);
            }
            else if (actionType == Action_Bet || actionType == Action_AllIn)
            {
                newRange->cutRange(sortedHoleCards, betDistribution);
            }
            else
            {
                assert(false);
            }

            return newRange;
        }

        float betProb[N_HOLECARDS];
        BuildPostflopCheckBetProb(betProb, _hcInd, _likelihood, _length, sortedHoleCards, board, betChance);

        if (actionType == Action_Check)
        {
            for (int i = 0; i < _length; i++)
                newRange->_likelihood[i] *= (1.0f - betProb[i]);
        }
        else if (actionType == Action_Bet)
        {
            for (int i = 0; i < _length; i++)
                newRange->_likelihood[i] *= betProb[i];
        }
        else if (actionType == Action_AllIn)
        {
            for (int i = 0; i < _length; i++)
                newRange->_likelihood[i] *= AllInLikelihoodFromAggression(betProb[i]);
        }
        else
        {
            assert(false);
        }

        newRange->normalize();
        return newRange;
    }

    std::shared_ptr<Range> Range::cutFoldCallRaise(ActionType actionType, const Board& board, float raiseChance, float callChance, const GameContext& gc) const
    {
        auto sortedHoleCards = gc.sortedHoleCards(board);
        auto newRange = std::make_shared<Range>(*this);

        if (board.street() == Street_PreFlop)
        {
            float foldDistribution[N_HOLECARDS];
            float callDistribution[N_HOLECARDS];
            float raiseDistribution[N_HOLECARDS];

            actionProbDist_FoldCallRaise(foldDistribution, callDistribution, raiseDistribution, sortedHoleCards.length, raiseChance, callChance);

            if (actionType == Action_Fold)
            {
                newRange->cutRange(sortedHoleCards, foldDistribution);
            }
            else if (actionType == Action_Call)
            {
                newRange->cutRange(sortedHoleCards, callDistribution);
            }
            else if (actionType == Action_Raise || actionType == Action_AllIn)
            {
                newRange->cutRange(sortedHoleCards, raiseDistribution);
            }
            else
            {
                assert(false);
            }

            return newRange;
        }

        float foldProb[N_HOLECARDS];
        float callProb[N_HOLECARDS];
        float raiseProb[N_HOLECARDS];

        BuildPostflopFoldCallRaiseProb(foldProb, callProb, raiseProb,
            _hcInd, _likelihood, _length, sortedHoleCards, board, raiseChance, callChance);

        if (actionType == Action_Fold)
        {
            for (int i = 0; i < _length; i++)
                newRange->_likelihood[i] *= foldProb[i];
        }
        else if (actionType == Action_Call)
        {
            for (int i = 0; i < _length; i++)
                newRange->_likelihood[i] *= callProb[i];
        }
        else if (actionType == Action_Raise)
        {
            for (int i = 0; i < _length; i++)
                newRange->_likelihood[i] *= raiseProb[i];
        }
        else if (actionType == Action_AllIn)
        {
            for (int i = 0; i < _length; i++)
                newRange->_likelihood[i] *= AllInLikelihoodFromAggression(raiseProb[i]);
        }
        else
        {
            assert(false);
        }

        newRange->normalize();
        return newRange;
    }

    void Range::predictAction_CheckBet(float& toCheck, float& toBet, const Board& board, float betChance, const GameContext& gc) const
    {
        auto sortedHoleCards = gc.sortedHoleCards(board);

        if (board.street() == Street_PreFlop)
        {
            float checkDistribution[N_HOLECARDS];
            float betDistribution[N_HOLECARDS];

            actionProbDist_CheckBet(checkDistribution, betDistribution, sortedHoleCards.length, board.street(), betChance);

            toCheck = predictAction(sortedHoleCards, checkDistribution);
            toBet = predictAction(sortedHoleCards, betDistribution);
        }
        else
        {
            float betProb[N_HOLECARDS];
            BuildPostflopCheckBetProb(betProb, _hcInd, _likelihood, _length, sortedHoleCards, board, betChance);

            toCheck = 0.0f;
            toBet = 0.0f;

            for (int i = 0; i < _length; i++)
            {
                toBet += _likelihood[i] * betProb[i];
                toCheck += _likelihood[i] * (1.0f - betProb[i]);
            }
        }

        float sum = toCheck + toBet;

        assert(sum > 0.0f);

        if (sum <= 0.0f || !_finite(sum))
            throw std::runtime_error("Range::predictAction_CheckBet: invalid predictive probability mass");

        toCheck /= sum;
        toBet /= sum;
    }

    void Range::predictAction_FoldCallRaise(float& toFold, float& toCall, float& toRaise, const Board& board,
        float raiseChance, float callChance, const GameContext& gc) const
    {
        auto sortedHoleCards = gc.sortedHoleCards(board);

        if (board.street() == Street_PreFlop)
        {
            float foldDistribution[N_HOLECARDS];
            float callDistribution[N_HOLECARDS];
            float raiseDistribution[N_HOLECARDS];

            actionProbDist_FoldCallRaise(foldDistribution, callDistribution, raiseDistribution, sortedHoleCards.length, raiseChance, callChance);

            toFold = predictAction(sortedHoleCards, foldDistribution);
            toCall = predictAction(sortedHoleCards, callDistribution);
            toRaise = predictAction(sortedHoleCards, raiseDistribution);
        }
        else
        {
            float foldProb[N_HOLECARDS];
            float callProb[N_HOLECARDS];
            float raiseProb[N_HOLECARDS];

            BuildPostflopFoldCallRaiseProb(foldProb, callProb, raiseProb,
                _hcInd, _likelihood, _length, sortedHoleCards, board, raiseChance, callChance);

            toFold = 0.0f;
            toCall = 0.0f;
            toRaise = 0.0f;

            for (int i = 0; i < _length; i++)
            {
                toFold += _likelihood[i] * foldProb[i];
                toCall += _likelihood[i] * callProb[i];
                toRaise += _likelihood[i] * raiseProb[i];
            }
        }

        float sum = toFold + toCall + toRaise;

        assert(sum > 0.0f);

        if (sum <= 0.0f || !_finite(sum))
            throw std::runtime_error("Range::predictAction_FoldCallRaise: invalid predictive probability mass");

        toFold /= sum;
        toCall /= sum;
        toRaise /= sum;
    }

    void Range::fillHandIndices(int* indices, int nIndices) const
    {
        float cumulHandChance = 0.0f;
        float nextCumulHandChance = 0.0f;
        int lastIndex = -1;

        for (int j = 0; j < _length; j++)
        {
            nextCumulHandChance += _likelihood[j];
            nextCumulHandChance = std::min(nextCumulHandChance, 1.0f);

            int firstIndex = (int)(cumulHandChance * nIndices);
            int nextIndex = (int)(nextCumulHandChance * nIndices);

            if (nextIndex > firstIndex)
            {
                for (int k = firstIndex; k <= nextIndex && k < nIndices; k++)
                {
                    indices[k] = _hcInd[j];
                }

                cumulHandChance = nextCumulHandChance;
                lastIndex = nextIndex;
            }
        }

        assert(lastIndex >= nIndices - 1);

        if (lastIndex < nIndices - 1)
            throw std::runtime_error("Range::fillHandIndices: insufficient probability mass to fill sampling table");
    }

    void Range::actionProbDist_CheckBet(float* checkDist, float* betDist, int numHands, Street street, float betChance)
    {
        float checkArea = 1.0f - betChance;

        if (street == Street_PreFlop)
        {
            float* foldDist = new float[numHands];
            actionProbDist_FoldCallRaise(foldDist, checkDist, betDist, numHands, betChance, checkArea);

            delete [] foldDist;
            return;
        }

        float betA = 0.5f;
        float betB = 0.0001f;

        int betCheckBorder1 = (int)((betChance - std::min(betChance, checkArea) * SMOOTH_DELTA) * numHands);
        int betCheckBorder2 = (int)((betChance + std::min(betChance, checkArea) * SMOOTH_DELTA) * numHands);

        // Bet distribution
        {
            for (int i=0; i<betCheckBorder1; i++)
            {
                betDist[i] = betA;
            }

            for (int i=betCheckBorder1; i<betCheckBorder2; i++)
            {
                betDist[i] = _smoothTable.doSmooth((float)betCheckBorder1, betA, (float)betCheckBorder2, betB, (float)i);
            }

            for (int i=betCheckBorder2; i<numHands; i++)
            {
                betDist[i] = betB;
            }
        }

        float checkA = 0.33f;
        float checkB = 0.67f;

        // Check distribution
        {
            for (int i=0; i<betCheckBorder1; i++)
            {
                checkDist[i] = checkA;
            }

            for (int i=betCheckBorder1; i<betCheckBorder2; i++)
            {
                checkDist[i] = _smoothTable.doSmooth((float)betCheckBorder1, checkA, (float)betCheckBorder2, checkB, (float)i);
            }

            for (int i=betCheckBorder2; i<numHands; i++)
            {
                checkDist[i] = checkB;
            }
        }

        // Normalize
        for (int k=0; k<NORMALIZE_ITERATIONS; k++)
        {
            float sumCheck = 0;
            float sumBet = 0;

            for (int i = 0; i < numHands; i++)
            {
                sumCheck += checkDist[i];
                sumBet += betDist[i];
            }

            float normCheck = (numHands * checkArea) / sumCheck;
            float normBet = (numHands * betChance) / sumBet;

            for (int i = 0; i < numHands; i++)
            {
                float cd = normCheck * checkDist[i];
                float bd = normBet * betDist[i];

                cd = std::max(cd, MIN_CHANCE);
                bd = std::max(bd, MIN_CHANCE);

                checkDist[i] = cd;
                betDist[i] = bd;
            }
        }
    }

    void Range::actionProbDist_FoldCallRaise(float* foldDist, float* callDist, float* raiseDist, int numHands, float raiseChance, float callChance)
    {
        float foldChance = 1.0f - callChance - raiseChance;

        float raiseA = 0.50f;
        float raiseB = 0.0001f;
        float raiseC = 0.0001f;

        float foldA = 0.00f;
        float foldB = 0.0001f;
        float foldC = 0.9998f;

        //float raiseX = raiseA;

        float deltaX =  0;
        deltaX = (deltaX > SMOOTH_DELTA) ? SMOOTH_DELTA : deltaX;

        int slowPlayRaiseBorder = (int)((raiseChance * deltaX) * numHands);
        int raiseCallBorder1 = (int)((raiseChance - std::min(raiseChance, callChance) * SMOOTH_DELTA) * numHands);
        int raiseCallBorder2 = (int)((raiseChance + std::min(callChance, raiseChance) * SMOOTH_DELTA) * numHands);
        int callFoldBorder1 = (int)((raiseChance + callChance - std::min(callChance, foldChance) * SMOOTH_DELTA) * numHands);
        int callFoldBorder2 = (int)((raiseChance + callChance + std::min(foldChance, callChance) * SMOOTH_DELTA) * numHands);

        // Fold distribution
        {
            for (int i=0; i<raiseCallBorder1; i++)
            {
                foldDist[i] = foldA;
            }

            for (int i=raiseCallBorder1; i<raiseCallBorder2; i++)
            {
                foldDist[i] = _smoothTable.doSmooth((float)raiseCallBorder1, foldA, (float)raiseCallBorder2, foldB, (float)i);
            }

            for (int i=raiseCallBorder2; i<callFoldBorder1; i++)
            {
                foldDist[i] = foldB;
            }

            for (int i=callFoldBorder1; i<callFoldBorder2; i++)
            {
                foldDist[i] = _smoothTable.doSmooth((float)callFoldBorder1, foldB, (float)callFoldBorder2, foldC, (float)i);
            }

            for (int i=callFoldBorder2; i<numHands; i++)
            {
                foldDist[i] = foldC;
            }
        }

        // Raise distribution
        {
            for (int i=0; i<slowPlayRaiseBorder; i++)
            {
                raiseDist[i] = _smoothTable.doSmooth(0, raiseA, (float)slowPlayRaiseBorder, raiseA, (float)i);
            }

            for (int i=slowPlayRaiseBorder; i<raiseCallBorder1; i++)
            {
                raiseDist[i] = raiseA;
            }

            for (int i=raiseCallBorder1; i<raiseCallBorder2; i++)
            {
                raiseDist[i] = _smoothTable.doSmooth((float)raiseCallBorder1, raiseA, (float)raiseCallBorder2, raiseB, (float)i);
            }

            for (int i=raiseCallBorder2; i<callFoldBorder1; i++)
            {
                raiseDist[i] = raiseB;
            }

            for (int i=callFoldBorder1; i<callFoldBorder2; i++)
            {
                raiseDist[i] = _smoothTable.doSmooth((float)callFoldBorder1, raiseB, (float)callFoldBorder2, raiseC, (float)i);
            }

            for (int i=callFoldBorder2; i<numHands; i++)
            {
                raiseDist[i] = raiseC;
            }
        }

        // Call distribution
        {
            for (int i=0; i<numHands; i++)
            {
                float tmp = 1.0f - raiseDist[i] - foldDist[i];
                callDist[i] = std::max(tmp, MIN_CHANCE);
            }
        }

        // Normalize
        for (int k=0; k<NORMALIZE_ITERATIONS; k++) // TODO Check num iters
        {
            float sumFold = 0;
            float sumCall = 0;
            float sumRaise = 0;

            for (int i = 0; i < numHands; i++)
            {
                sumFold += foldDist[i];
                sumCall += callDist[i];
                sumRaise += raiseDist[i];
            }

            float normFold = (foldChance * numHands) / sumFold;
            float normCall = (callChance * numHands) / sumCall;
            float normRaise = (raiseChance * numHands) / sumRaise;

            for (int i = 0; i < numHands; i++)
            {
                float fd = normFold * foldDist[i];
                float cd = normCall * callDist[i];
                float rd = normRaise * raiseDist[i];

                fd = std::max(fd, MIN_CHANCE);
                cd = std::max(cd, MIN_CHANCE);
                rd = std::max(rd, MIN_CHANCE);

                foldDist[i] = fd;
                callDist[i] = cd;
                raiseDist[i] = rd;
            }
        }
    }
}
