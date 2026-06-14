using System;
using System.Collections.Generic;

namespace G5.Logic
{
    public static class CalibratedLikelihoodModel
    {
        private const float EPS = 0.0001f;

        public static float[] ComputeObservedLikelihood(
            IList<float> priorWeights,
            IList<ComboFeatureVector> features,
            LikelihoodProfile profile,
            PostFlopLineContext context,
            ActionTargets targets,
            ActionType observedAction)
        {
            if (priorWeights == null)
                throw new ArgumentNullException(nameof(priorWeights));

            if (features == null)
                throw new ArgumentNullException(nameof(features));

            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (targets == null)
                throw new ArgumentNullException(nameof(targets));

            if (priorWeights.Count != features.Count)
                throw new ArgumentException("priorWeights e features devem ter o mesmo tamanho.");

            if (priorWeights.Count == 0)
                throw new ArgumentException("Nao ha combos ativos para calibrar likelihood.");

            float[] aggScores = new float[features.Count];
            float[] contScores = new float[features.Count];
            float[] foldScores = new float[features.Count];

            for (int i = 0; i < features.Count; i++)
            {
                aggScores[i] = profile.ScoreAggressive(features[i], context);
                contScores[i] = profile.ScoreContinue(features[i], context);
                foldScores[i] = profile.ScoreFold(features[i], context);
            }

            CalibrateSoftmax(priorWeights, aggScores, contScores, foldScores, targets,
                out float[] aggProb, out float[] contProb, out float[] foldProb);

            LikelihoodActionBucket bucket = ActionTargets.ToBucket(observedAction);

            switch (bucket)
            {
                case LikelihoodActionBucket.Aggressive:
                    return aggProb;
                case LikelihoodActionBucket.Continue:
                    return contProb;
                case LikelihoodActionBucket.Fold:
                    return foldProb;
                default:
                    return contProb;
            }
        }

        public static void CalibrateSoftmax(
            IList<float> weights,
            IList<float> aggressiveScores,
            IList<float> continueScores,
            IList<float> foldScores,
            ActionTargets targets,
            out float[] aggressiveProb,
            out float[] continueProb,
            out float[] foldProb)
        {
            int n = weights.Count;

            aggressiveProb = new float[n];
            continueProb = new float[n];
            foldProb = new float[n];

            float targetAgg = ClampTarget(targets.BetRaise);
            float targetCont = ClampTarget(targets.CheckCall);
            float targetFold = ClampTarget(targets.Fold);
            NormalizeTargets(ref targetAgg, ref targetCont, ref targetFold);

            float ia = 0.0f;
            float ic = 0.0f;
            float iff = 0.0f;

            for (int iter = 0; iter < 80; iter++)
            {
                FillSoftmax(weights, aggressiveScores, continueScores, foldScores, ia, ic, iff,
                    aggressiveProb, continueProb, foldProb,
                    out float meanAgg, out float meanCont, out float meanFold);

                ia += 0.65f * SafeLogRatio(targetAgg, meanAgg);
                ic += 0.65f * SafeLogRatio(targetCont, meanCont);
                iff += 0.65f * SafeLogRatio(targetFold, meanFold);

                // Identifiability: subtract mean intercept. Softmax is invariant to a common shift.
                float meanIntercept = (ia + ic + iff) / 3.0f;
                ia -= meanIntercept;
                ic -= meanIntercept;
                iff -= meanIntercept;
            }

            FillSoftmax(weights, aggressiveScores, continueScores, foldScores, ia, ic, iff,
                aggressiveProb, continueProb, foldProb,
                out _, out _, out _);
        }

        private static void FillSoftmax(
            IList<float> weights,
            IList<float> aggressiveScores,
            IList<float> continueScores,
            IList<float> foldScores,
            float interceptAgg,
            float interceptCont,
            float interceptFold,
            float[] aggressiveProb,
            float[] continueProb,
            float[] foldProb,
            out float meanAgg,
            out float meanCont,
            out float meanFold)
        {
            meanAgg = 0.0f;
            meanCont = 0.0f;
            meanFold = 0.0f;
            float mass = 0.0f;

            for (int i = 0; i < weights.Count; i++)
            {
                float a = aggressiveScores[i] + interceptAgg;
                float c = continueScores[i] + interceptCont;
                float f = foldScores[i] + interceptFold;
                float max = Math.Max(a, Math.Max(c, f));

                double ea = Math.Exp(a - max);
                double ec = Math.Exp(c - max);
                double ef = Math.Exp(f - max);
                double denom = ea + ec + ef;

                if (denom <= 0.0 || double.IsNaN(denom) || double.IsInfinity(denom))
                {
                    aggressiveProb[i] = 0.34f;
                    continueProb[i] = 0.33f;
                    foldProb[i] = 0.33f;
                }
                else
                {
                    aggressiveProb[i] = ClampProbability((float)(ea / denom));
                    continueProb[i] = ClampProbability((float)(ec / denom));
                    foldProb[i] = ClampProbability((float)(ef / denom));
                }

                float w = weights[i];

                if (float.IsNaN(w) || float.IsInfinity(w) || w < 0.0f)
                    w = 0.0f;

                mass += w;
                meanAgg += w * aggressiveProb[i];
                meanCont += w * continueProb[i];
                meanFold += w * foldProb[i];
            }

            if (mass <= 0.0f)
                mass = 1.0f;

            meanAgg /= mass;
            meanCont /= mass;
            meanFold /= mass;
        }

        public static float[] CalibrateBinarySigmoid(IList<float> weights, IList<float> scores, float target)
        {
            if (weights == null)
                throw new ArgumentNullException(nameof(weights));

            if (scores == null)
                throw new ArgumentNullException(nameof(scores));

            if (weights.Count != scores.Count)
                throw new ArgumentException("weights e scores devem ter o mesmo tamanho.");

            target = ClampTarget(target);
            float lo = -24.0f;
            float hi = 24.0f;

            for (int i = 0; i < 80; i++)
            {
                float mid = (lo + hi) * 0.5f;
                float mean = WeightedSigmoidMean(weights, scores, mid);

                if (mean < target)
                    lo = mid;
                else
                    hi = mid;
            }

            float intercept = (lo + hi) * 0.5f;
            float[] result = new float[weights.Count];

            for (int i = 0; i < result.Length; i++)
                result[i] = ClampProbability(Sigmoid(scores[i] + intercept));

            return result;
        }

        private static float WeightedSigmoidMean(IList<float> weights, IList<float> scores, float intercept)
        {
            float mass = 0.0f;
            float sum = 0.0f;

            for (int i = 0; i < weights.Count; i++)
            {
                float w = weights[i];

                if (float.IsNaN(w) || float.IsInfinity(w) || w < 0.0f)
                    continue;

                mass += w;
                sum += w * Sigmoid(scores[i] + intercept);
            }

            if (mass <= 0.0f)
                return 0.5f;

            return sum / mass;
        }

        private static float Sigmoid(float x)
        {
            if (x < -40.0f)
                return 0.0f;

            if (x > 40.0f)
                return 1.0f;

            return (float)(1.0 / (1.0 + Math.Exp(-x)));
        }

        private static float SafeLogRatio(float target, float actual)
        {
            target = ClampTarget(target);
            actual = ClampTarget(actual);
            return (float)Math.Log(target / actual);
        }

        private static float ClampTarget(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0.333f;

            if (value < EPS)
                return EPS;

            if (value > 1.0f - EPS)
                return 1.0f - EPS;

            return value;
        }

        private static float ClampProbability(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0.333f;

            if (value < EPS)
                return EPS;

            if (value > 1.0f - EPS)
                return 1.0f - EPS;

            return value;
        }

        private static void NormalizeTargets(ref float a, ref float c, ref float f)
        {
            float sum = a + c + f;

            if (sum <= 0.0f || float.IsNaN(sum) || float.IsInfinity(sum))
            {
                a = 0.34f;
                c = 0.33f;
                f = 0.33f;
                return;
            }

            a /= sum;
            c /= sum;
            f /= sum;
        }
    }
}
