using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace G5.Logic.Estimators
{
    /// <summary>
    /// This is old (modeling) estimator. Estimates player model using Bayesian estimation.
    /// Than plays exploitevly to maximaze EV.
    /// </summary>
    public class ModelingEstimator : IActionEstimator
    {
        private OpponentModeling _opponentModeling;
        private DecisionMakingContext _dmContext;
        private PokerClient _pokerClient;

        public ModelingEstimator(OpponentModeling oppmodeling, PokerClient pokerClient)
        {
            _opponentModeling = oppmodeling;
            _pokerClient = pokerClient;
            _dmContext = new DecisionMakingContext();
        }

        public void Dispose()
        {
            if (_dmContext != null)
            {
                _dmContext.Dispose();
                _dmContext = null;
            }
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return min;

            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private static void NormalizeActionProbabilities(ref float betRaiseProb, ref float checkCallProb)
        {
            betRaiseProb = Clamp(betRaiseProb, 0.001f, 0.999f);
            checkCallProb = Clamp(checkCallProb, 0.001f, 0.999f);

            float sum = betRaiseProb + checkCallProb;

            if (sum >= 0.997f)
            {
                float scale = 0.997f / sum;
                betRaiseProb *= scale;
                checkCallProb *= scale;
            }
        }

        private static float SizeSelectivityMultiplier(BotGameState.ActionSizingContext ctx)
        {
            if (ctx.IsAllIn || ctx.ActionType == ActionType.AllIn)
                return 0.32f;

            float ratio = ctx.BetToPotRatio;

            if (ratio <= 0.0f)
                return 1.0f;

            if (ratio < 0.25f)
                return 1.35f;

            if (ratio < 0.50f)
                return 1.15f;

            if (ratio < 0.85f)
                return 1.00f;

            if (ratio < 1.25f)
                return 0.72f;

            return 0.48f;
        }

        private static float CallSelectivityMultiplier(BotGameState.ActionSizingContext ctx)
        {
            if (ctx.IsAllIn || ctx.ActionType == ActionType.AllIn)
                return 0.42f;

            float ratio = ctx.CallToPotRatio;

            if (ratio <= 0.0f)
                return 1.0f;

            if (ratio < 0.18f)
                return 1.30f;

            if (ratio < 0.32f)
                return 1.10f;

            if (ratio < 0.50f)
                return 0.85f;

            return 0.62f;
        }

        private static void ApplyObservedSizingToAD(ActionType observedAction, BotGameState gameState, ref float betRaiseProb, ref float checkCallProb)
        {
            var ctx = gameState.getLastActionSizingContext();

            if (gameState.getStreet() == Street.PreFlop)
            {
                NormalizeActionProbabilities(ref betRaiseProb, ref checkCallProb);
                return;
            }

            if (observedAction == ActionType.Bet || observedAction == ActionType.Raise || observedAction == ActionType.AllIn)
            {
                betRaiseProb *= SizeSelectivityMultiplier(ctx);

                if (ctx.IsAllIn || observedAction == ActionType.AllIn)
                    checkCallProb *= 0.72f;
            }
            else if (observedAction == ActionType.Call)
            {
                checkCallProb *= CallSelectivityMultiplier(ctx);
            }
            else if (observedAction == ActionType.Fold && ctx.AmountToCall > 0)
            {
                float pressure = Clamp(ctx.CallToPotRatio, 0.0f, 1.0f);
                checkCallProb *= (1.0f - 0.45f * pressure);
                betRaiseProb *= (1.0f - 0.35f * pressure);
            }

            NormalizeActionProbabilities(ref betRaiseProb, ref checkCallProb);
        }

        private EstimatedAD getPlayerToActAD(Player playerToAct, BotGameState gameState)
        {
            if (gameState.getStreet() == Street.PreFlop)
            {
                var preFlopParams = new PreFlopParams(
                    gameState.getTableType(),
                    playerToAct.PreFlopPosition,
                    gameState.getNumCallers(),
                    gameState.getNumBets(),
                    gameState.numActivePlayers(),
                    playerToAct.LastAction,
                    gameState.isPlayerInPosition(gameState.getPlayerToActInd()));

                return playerToAct.GetAD(preFlopParams);
            }
            else
            {
                var round = playerToAct.Round();
                ActionType prevAction = (round == 0) ? playerToAct.PrevStreetAction : playerToAct.LastAction;

                var postFlopParams = new PostFlopParams(
                    gameState.getTableType(),
                    gameState.getStreet(),
                    round,
                    prevAction,
                    gameState.getNumBets(),
                    gameState.isPlayerInPosition(gameState.getPlayerToActInd()),
                    gameState.numActivePlayers());

                return playerToAct.GetAD(postFlopParams);
            }
        }

        private bool canNextPlayerRaise(Player playerToAct, BotGameState gameState)
        {
            return (gameState.getNumBets() < 4) &&
                gameState.getAmountToCall() < playerToAct.Stack &&
                gameState.numActiveNonAllInPlayers() > 1;
        }

        private void getPlayerToActAD(ref float betRaiseProb, ref float checkCallProb, BotGameState gameState)
        {
            var playerToAct = gameState.getPlayerToAct();
            EstimatedAD ad = getPlayerToActAD(playerToAct, gameState);
            Debug.Assert(ad.PriorSamples > 0);

            betRaiseProb = ad.BetRaise.Mean;
            checkCallProb = ad.CheckCall.Mean;

            if (!canNextPlayerRaise(playerToAct, gameState))
            {
                checkCallProb += betRaiseProb;
                betRaiseProb = 0.0f;
            }

            Console.WriteLine($"{playerToAct.Name} model stats: BR {betRaiseProb.ToString("f2")}; CC {checkCallProb.ToString("f2")};" +
                $" FO {(1 - betRaiseProb - checkCallProb).ToString("f2")} [prior smpls:{ad.PriorSamples}, updates:{ad.UpdateSamples}]");
        }

        void IActionEstimator.newAction(ActionType actionType, BotGameState gameState)
        {
            float betRaiseProb = 0.0f;
            float checkCallProb = 0.0f;
            getPlayerToActAD(ref betRaiseProb, ref checkCallProb, gameState);
            ApplyObservedSizingToAD(actionType, gameState, ref betRaiseProb, ref checkCallProb);

            var sizingContext = gameState.getLastActionSizingContext();
            ActionType cutActionType = actionType;

            // Open-shove is semantically a bet with maximum commitment, not a response
            // to a previous bet. The sizing adjustment above already makes it much more
            // selective than a normal bet; routing it through Check/Bet preserves the
            // correct no-forced-action branch in Range.
            if (actionType == ActionType.AllIn && sizingContext.AmountToCall == 0)
                cutActionType = ActionType.Bet;

            gameState.getPlayerToAct().CutRange(cutActionType,
                gameState.getStreet(),
                gameState.getBoard(),
                betRaiseProb,
                checkCallProb, _dmContext);
        }

        void IActionEstimator.flopShown(Board board, HoleCards holeCards)
        {
            DecisionMakingDll.GameContext_NewFlop(_dmContext, board, holeCards);
        }

        void IActionEstimator.newHand(BotGameState gameState)
        {
            Parallel.ForEach(gameState.getPlayers(), (player) =>
            {
                // Update player model for each player using recend hand history
                player.Model = _opponentModeling.estimatePlayerModel(player.Name, _pokerClient);
            });

            /*foreach (Player player in _players)
            {
                player.UpdateModel(_opponentModeling.estimatePlayerModel(player.Name, _pokerClient));
            }*/
        }

        void IActionEstimator.estimateEV(out float checkCallEV, out float betRaiseEV, BotGameState gameState)
        {
            DecisionMakingDll.Holdem_EstimateEV(out checkCallEV, out betRaiseEV, gameState.getButtonInd(),
                gameState.getHeroInd(),
                gameState.getHeroHoleCards(),
                gameState.getPlayers(),
                gameState.getBoard(),
                gameState.getStreet(),
                gameState.getNumBets(),
                gameState.getNumCallers(),
                gameState.getBigBlingSize(), _dmContext);
        }
    }
}
