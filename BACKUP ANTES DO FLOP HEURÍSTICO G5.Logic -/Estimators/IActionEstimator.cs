using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace G5.Logic.Estimators
{
    /// <summary>
    /// Needed so we can choose different estimators. Eg Bayesian modeling estimator, DL estimator etc.
    /// </summary>
    public interface IActionEstimator : IDisposable
    {
        /// <summary>
        /// Tell estimator that new action hapenned so it can react (for instance cut range)
        /// </summary>
        void newAction(ActionType actionType, BotGameState gameState);

        /// <summary>
        /// Tell estimator that new street hapenned so it can react (Eg re estimate sub-game).
        /// </summary>
        void flopShown(Board board, HoleCards holeCards);

        /// <summary>
        /// Tell estimator that new hand hapenned so it can react (Eg update player models).
        /// </summary>
        void newHand(BotGameState gameState);

        /// <summary>
        /// Ask estimator to estimate EV of actions in curent game state.
        /// </summary>
        void estimateEV(out float checkCallEV, out float betRaiseEV, BotGameState gameState);
    }

    /// <summary>
    /// Optional estimator extension used by the academic regression suite and by the multi-size EV tree.
    /// Implementations calculate EV for a specific first bet/raise amount at the root node.
    /// </summary>
    public interface IBetRaiseAmountEstimator : IActionEstimator
    {
        void estimateEVForBetRaiseAmount(out float checkCallEV, out float betRaiseEV, BotGameState gameState, int forcedBetRaiseAmount);
    }

}
