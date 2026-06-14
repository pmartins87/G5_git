using System;

namespace G5.Logic
{
    public sealed class TargetFrequencyModel
    {
        public ActionTargets Estimate(Player actor, BotGameState gameState, PostFlopLineContext context)
        {
            if (actor == null)
                throw new ArgumentNullException(nameof(actor));

            if (gameState == null)
                throw new ArgumentNullException(nameof(gameState));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            ActionTargets legacy = EstimateLegacyTargets(actor, gameState);
            LikelihoodProfile profile = LikelihoodProfileLibrary.Get(context.LineClass);
            ActionTargets conditioned = ApplyTopologicalConditioning(legacy, profile, context, gameState);
            conditioned.Source = "PlayerModel.PostFlopAD(.bin via OpponentModeling) + LineContext conditioning: " + context.LineClass;
            return conditioned;
        }

        private ActionTargets EstimateLegacyTargets(Player actor, BotGameState gameState)
        {
            if (actor.Model == null)
                return new ActionTargets(0.10f, 0.55f, 0.35f) { Source = "fallback: player.Model == null" };

            int round = actor.Round();
            ActionType prevAction = (round == 0) ? actor.PrevStreetAction : actor.LastAction;

            PostFlopParams prms = new PostFlopParams(
                gameState.getTableType(),
                gameState.getStreet(),
                round,
                prevAction,
                gameState.getNumBets(),
                gameState.isPlayerInPosition(gameState.getPlayerToActInd()),
                gameState.numActivePlayers());

            EstimatedAD ad = actor.GetAD(prms);

            float betRaise = Safe(ad.BetRaise.Mean, 0.10f);
            float checkCall = Safe(ad.CheckCall.Mean, 0.50f);
            float fold = Safe(ad.Fold.Mean, 1.0f - betRaise - checkCall);

            if (fold <= 0.0001f)
                fold = Math.Max(0.001f, 1.0f - betRaise - checkCall);

            if (!CanActorRaise(actor, gameState))
            {
                checkCall += betRaise;
                betRaise = 0.0f;
            }

            return new ActionTargets(betRaise, checkCall, fold)
            {
                PriorSamples = ad.PriorSamples,
                UpdateSamples = ad.UpdateSamples,
                Source = "PlayerModel.PostFlopAD index=" + prms.ToIndex()
            };
        }

        private ActionTargets ApplyTopologicalConditioning(ActionTargets legacy, LikelihoodProfile profile, PostFlopLineContext context, BotGameState gameState)
        {
            float betRaise = legacy.BetRaise;
            float checkCall = legacy.CheckCall;
            float fold = legacy.Fold;

            betRaise = betRaise * profile.TargetAggressionScale + profile.TargetAggressionBias;
            checkCall = checkCall * profile.TargetContinueScale + profile.TargetContinueBias;
            fold = fold * profile.TargetFoldScale + profile.TargetFoldBias;

            // Structural constraints, not arbitrary edge thresholds.
            // They preserve action legality and condition generic priors by context topology.
            if (context.ObservedAction == ActionType.Check)
            {
                fold = Math.Min(fold, 0.001f);
                checkCall = Math.Max(checkCall, 0.25f);
            }

            if (context.AmountToCall <= 0)
            {
                fold = Math.Min(fold, 0.001f);
            }

            if (context.IsMultiway)
            {
                betRaise *= 0.92f;
                checkCall *= 1.03f;
                fold *= 1.05f;
            }

            if (context.SizeClass == PostFlopSizeClass.Tiny)
            {
                checkCall *= 1.18f;
                fold *= 0.72f;
            }
            else if (context.SizeClass == PostFlopSizeClass.Big)
            {
                betRaise *= 0.92f;
                checkCall *= 0.92f;
                fold *= 1.22f;
            }
            else if (context.SizeClass == PostFlopSizeClass.Overbet || context.SizeClass == PostFlopSizeClass.AllIn)
            {
                betRaise *= 0.78f;
                checkCall *= 0.82f;
                fold *= 1.45f;
            }

            // If the actor cannot raise, all aggressive target mass becomes continuing mass.
            if (!CanActorRaise(gameState.getPlayerToAct(), gameState))
            {
                checkCall += betRaise;
                betRaise = 0.0f;
            }

            ActionTargets result = new ActionTargets(betRaise, checkCall, fold)
            {
                PriorSamples = legacy.PriorSamples,
                UpdateSamples = legacy.UpdateSamples,
                Source = legacy.Source
            };

            return result;
        }

        private static bool CanActorRaise(Player actor, BotGameState gameState)
        {
            if (actor == null || gameState == null)
                return false;

            return gameState.getNumBets() < 4 &&
                   gameState.getAmountToCall() < actor.Stack &&
                   gameState.numActiveNonAllInPlayers() > 1;
        }

        private static float Safe(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return fallback;

            if (value < 0.0f)
                return 0.0f;

            if (value > 1.0f)
                return 1.0f;

            return value;
        }
    }
}
