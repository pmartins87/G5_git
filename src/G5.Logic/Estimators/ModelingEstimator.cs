using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace G5.Logic.Estimators
{
    /// <summary>
    /// Bayesian opponent modeling estimator with topological postflop belief updates.
    ///
    /// The main game tree still sees abstract actions: Fold / CheckCall / BetRaise.
    /// This class enriches only the observation channel used for range updates:
    /// a raw Bet/Raise/Call/Fold is classified as CBet, DonkBet, Probe, CheckRaise,
    /// RaiseVsCBet, etc., and the range update applies a calibrated P(o|s,c).
    /// </summary>
    public class ModelingEstimator : IBetRaiseAmountEstimator
    {
        private OpponentModeling _opponentModeling;
        private DecisionMakingContext _dmContext;
        private PokerClient _pokerClient;
        private readonly PostFlopLineHistory _lineHistory;
        private readonly LineContextClassifier _lineClassifier;
        private readonly TargetFrequencyModel _targetFrequencyModel;
        private readonly BayesianRangeUpdater _rangeUpdater;

        public ModelingEstimator(OpponentModeling oppmodeling, PokerClient pokerClient)
        {
            _opponentModeling = oppmodeling;
            _pokerClient = pokerClient;
            _dmContext = new DecisionMakingContext();
            _lineHistory = new PostFlopLineHistory();
            _lineClassifier = new LineContextClassifier();
            _targetFrequencyModel = new TargetFrequencyModel();
            _rangeUpdater = new BayesianRangeUpdater();
        }

        public void Dispose()
        {
            if (_dmContext != null)
            {
                _dmContext.Dispose();
                _dmContext = null;
            }
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

        private void updatePreflopRangeWithLegacyModel(ActionType actionType, BotGameState gameState)
        {
            float betRaiseProb = 0.0f;
            float checkCallProb = 0.0f;
            getPlayerToActAD(ref betRaiseProb, ref checkCallProb, gameState);

            gameState.getPlayerToAct().CutRange(actionType,
                gameState.getStreet(),
                gameState.getBoard(),
                betRaiseProb,
                checkCallProb,
                _dmContext);
        }

        private void updatePostflopRangeWithLineContext(ActionType actionType, BotGameState gameState)
        {
            Player actor = gameState.getPlayerToAct();
            PostFlopLineContext context = _lineClassifier.Classify(gameState, actionType, _lineHistory);
            ActionTargets targets = _targetFrequencyModel.Estimate(actor, gameState, context);

            BayesianRangeUpdateResult update = _rangeUpdater.UpdateRange(
                actor.Range,
                gameState.getBoard(),
                actionType,
                context,
                targets);

            Console.WriteLine(update.ToString());
        }

        void IActionEstimator.newAction(ActionType actionType, BotGameState gameState)
        {
            if (gameState == null)
                throw new ArgumentNullException(nameof(gameState));

            _lineHistory.EnsureStreet(gameState.getStreet());

            if (gameState.getStreet() == Street.PreFlop)
            {
                updatePreflopRangeWithLegacyModel(actionType, gameState);

                // The history tracker still needs preflop aggression to classify flop initiative.
                _lineHistory.ObserveAction(gameState, actionType, null);
                return;
            }

            PostFlopLineContext contextForHistory = null;

            try
            {
                Player actor = gameState.getPlayerToAct();
                contextForHistory = _lineClassifier.Classify(gameState, actionType, _lineHistory);
                ActionTargets targets = _targetFrequencyModel.Estimate(actor, gameState, contextForHistory);

                BayesianRangeUpdateResult update = _rangeUpdater.UpdateRange(
                    actor.Range,
                    gameState.getBoard(),
                    actionType,
                    contextForHistory,
                    targets);

                Console.WriteLine(update.ToString());
            }
            finally
            {
                _lineHistory.ObserveAction(gameState, actionType, contextForHistory);
            }
        }

        void IActionEstimator.flopShown(Board board, HoleCards holeCards)
        {
            DecisionMakingDll.GameContext_NewFlop(_dmContext, board, holeCards);
        }

        void IActionEstimator.newHand(BotGameState gameState)
        {
            _lineHistory.ResetForNewHand();

            Parallel.ForEach(gameState.getPlayers(), (player) =>
            {
                // Update player model for each player using recent hand history and population priors.
                player.Model = _opponentModeling.estimatePlayerModel(player.Name, _pokerClient);
            });
        }

        public void estimateEVForBetRaiseAmount(out float checkCallEV, out float betRaiseEV, BotGameState gameState, int forcedBetRaiseAmount)
        {
            if (forcedBetRaiseAmount <= 0)
            {
                ((IActionEstimator)this).estimateEV(out checkCallEV, out betRaiseEV, gameState);
                return;
            }

            DecisionMakingDll.Holdem_EstimateEVForBetRaiseAmount(out checkCallEV, out betRaiseEV,
                forcedBetRaiseAmount,
                gameState.getButtonInd(),
                gameState.getHeroInd(),
                gameState.getHeroHoleCards(),
                gameState.getPlayers(),
                gameState.getBoard(),
                gameState.getStreet(),
                gameState.getNumBets(),
                gameState.getNumCallers(),
                gameState.getBigBlingSize(),
                _dmContext);
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
