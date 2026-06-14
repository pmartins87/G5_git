using System;
using System.Collections.Generic;
using System.Linq;

namespace G5.Logic
{
    public sealed class BayesianRangeUpdateResult
    {
        public int ActiveCombosBefore { get; set; }
        public int ActiveCombosAfter { get; set; }
        public float MassBefore { get; set; }
        public float MassAfterBeforeNormalize { get; set; }
        public ActionTargets Targets { get; set; }
        public PostFlopLineContext Context { get; set; }
        public string ProfileName { get; set; }

        public override string ToString()
        {
            return string.Format(
                "BayesianRangeUpdate: active {0}->{1}, massBefore={2:F6}, massAfterRaw={3:F6}, profile={4}, targets=[{5}], ctx=[{6}]",
                ActiveCombosBefore,
                ActiveCombosAfter,
                MassBefore,
                MassAfterBeforeNormalize,
                ProfileName,
                Targets,
                Context != null ? Context.ToCompactString() : "-");
        }
    }

    public sealed class BayesianRangeUpdater
    {
        private const float MIN_WEIGHT = 0.000000001f;
        private const float MIN_LIKELIHOOD = 0.0001f;

        public BayesianRangeUpdateResult UpdateRange(
            Range range,
            Board board,
            ActionType observedAction,
            PostFlopLineContext context,
            ActionTargets targets)
        {
            if (range == null)
                throw new ArgumentNullException(nameof(range));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (targets == null)
                throw new ArgumentNullException(nameof(targets));

            LikelihoodProfile profile = LikelihoodProfileLibrary.Get(context.LineClass);

            List<int> activeIndices = new List<int>();
            List<float> priorWeights = new List<float>();
            List<ComboFeatureVector> features = new List<ComboFeatureVector>();

            float massBefore = 0.0f;

            for (int i = 0; i < range.Data.Length; i++)
            {
                float w = range.Data[i].Equity;

                if (float.IsNaN(w) || float.IsInfinity(w) || w < 0.0f)
                    w = 0.0f;

                if (w <= MIN_WEIGHT)
                    continue;

                int comboIndex = range.Data[i].Ind;
                activeIndices.Add(i);
                priorWeights.Add(w);
                features.Add(PostFlopComboFeatureExtractor.Extract(comboIndex, board, context));
                massBefore += w;
            }

            if (activeIndices.Count == 0 || massBefore <= 0.0f)
                throw new InvalidOperationException("BayesianRangeUpdater: range sem massa ativa antes da atualizacao.");

            NormalizeWeightsInPlace(priorWeights);

            float[] likelihood = CalibratedLikelihoodModel.ComputeObservedLikelihood(
                priorWeights,
                features,
                profile,
                context,
                targets,
                observedAction);

            float rawMass = 0.0f;

            for (int k = 0; k < activeIndices.Count; k++)
            {
                int i = activeIndices[k];
                float p = likelihood[k];

                if (float.IsNaN(p) || float.IsInfinity(p) || p < MIN_LIKELIHOOD)
                    p = MIN_LIKELIHOOD;

                range.Data[i].Equity *= p;
                rawMass += range.Data[i].Equity;
            }

            if (rawMass <= 0.0f || float.IsNaN(rawMass) || float.IsInfinity(rawMass))
                throw new InvalidOperationException("BayesianRangeUpdater: atualizacao zerou a massa probabilistica do range.");

            NormalizeRange(range);

            range.CuttingParams.Add(new Range.CuttingParamsT
            {
                ActionType = observedAction,
                Street = context.Street,
                Value1 = targets.BetRaise,
                Value2 = targets.CheckCall,
                Forced = true
            });

            return new BayesianRangeUpdateResult
            {
                ActiveCombosBefore = activeIndices.Count,
                ActiveCombosAfter = range.ActiveComboCount(),
                MassBefore = massBefore,
                MassAfterBeforeNormalize = rawMass,
                Targets = targets,
                Context = context,
                ProfileName = profile.Name
            };
        }

        private static void NormalizeWeightsInPlace(List<float> weights)
        {
            float sum = 0.0f;

            for (int i = 0; i < weights.Count; i++)
            {
                if (float.IsNaN(weights[i]) || float.IsInfinity(weights[i]) || weights[i] < 0.0f)
                    weights[i] = 0.0f;

                sum += weights[i];
            }

            if (sum <= 0.0f)
            {
                float uniform = 1.0f / Math.Max(1, weights.Count);

                for (int i = 0; i < weights.Count; i++)
                    weights[i] = uniform;

                return;
            }

            for (int i = 0; i < weights.Count; i++)
                weights[i] /= sum;
        }

        private static void NormalizeRange(Range range)
        {
            float sum = 0.0f;

            for (int i = 0; i < range.Data.Length; i++)
            {
                float w = range.Data[i].Equity;

                if (float.IsNaN(w) || float.IsInfinity(w) || w < 0.0f)
                {
                    range.Data[i].Equity = 0.0f;
                    continue;
                }

                sum += w;
            }

            if (sum <= 0.0f)
                throw new InvalidOperationException("BayesianRangeUpdater.NormalizeRange: massa probabilistica zerada.");

            float inv = 1.0f / sum;

            for (int i = 0; i < range.Data.Length; i++)
                range.Data[i].Equity *= inv;
        }
    }
}
