using System;

namespace G5.Logic
{
    public struct ExecutionSizingResult
    {
        public int Amount;
        public string Reason;
    }

    /// <summary>
    /// Camada de execucao de sizing pos-flop.
    ///
    /// Esta classe NAO avalia EV e NAO escolhe a acao abstrata. A arvore completa
    /// decide apenas Fold / CheckCall / BetRaise usando o size canonico nativo.
    /// Depois da decisao abstrata, esta politica transforma Bet/Raise em um valor
    /// real de execucao com base no LineContext topologico, sem criar novos ramos.
    /// </summary>
    public static class ExecutionSizingPolicy
    {
        public static ExecutionSizingResult CalculatePostFlopAmount(
            BotGameState gameState,
            PostFlopLineContext context,
            int canonicalTreeAmount,
            ActionType plannedAction)
        {
            if (gameState == null)
                throw new ArgumentNullException(nameof(gameState));

            if (gameState.getStreet() == Street.PreFlop)
            {
                return new ExecutionSizingResult
                {
                    Amount = Math.Max(0, canonicalTreeAmount),
                    Reason = "preflop: sizing mantido pelo mecanismo preflop."
                };
            }

            int amountToCall = gameState.getAmountToCall();
            int pot = gameState.potSize();
            int stack = gameState.getHero().Stack;
            int bigBlind = Math.Max(1, gameState.getBigBlingSize());
            int basePot = Math.Max(1, pot + amountToCall);

            if (stack <= 0)
            {
                return new ExecutionSizingResult
                {
                    Amount = 0,
                    Reason = "stack zero: sem sizing de execucao."
                };
            }

            PostFlopLineClass line = context != null ? context.LineClass : PostFlopLineClass.Unknown;
            BoardTextureClass texture = context != null ? context.BoardTexture : BoardTextureClass.Unknown;
            bool facingBet = amountToCall > 0;
            float fraction = SelectFraction(line, texture, facingBet, plannedAction);

            int amount;

            if (facingBet)
                amount = amountToCall + (int)Math.Round(basePot * fraction);
            else
                amount = (int)Math.Round(basePot * fraction);

            if (amount < bigBlind && amountToCall == 0)
                amount = bigBlind;

            if (amountToCall > 0 && amount <= amountToCall)
                amount = amountToCall + bigBlind;

            // O size canonico da arvore e o piso estrutural para nao executar uma
            // agressao menor do que aquela avaliada no ramo Bet/Raise.
            if (canonicalTreeAmount > 0 && amount < canonicalTreeAmount)
                amount = canonicalTreeAmount;

            if (amount > stack)
                amount = stack;

            return new ExecutionSizingResult
            {
                Amount = amount,
                Reason = string.Format(
                    "LineContext={0}, textura={1}, facingBet={2}, fracaoExecucao={3:F2}, sizeCanonicoArvore={4}.",
                    line,
                    texture,
                    facingBet,
                    fraction,
                    canonicalTreeAmount)
            };
        }

        private static float SelectFraction(PostFlopLineClass line, BoardTextureClass texture, bool facingBet, ActionType plannedAction)
        {
            bool wet = texture == BoardTextureClass.Wet || texture == BoardTextureClass.Monotone;
            bool paired = texture == BoardTextureClass.Paired;

            switch (line)
            {
                case PostFlopLineClass.CBet:
                    return wet ? 0.66f : 0.50f;

                case PostFlopLineClass.DoubleBarrel:
                    return wet ? 0.75f : 0.66f;

                case PostFlopLineClass.TripleBarrel:
                    return wet ? 0.80f : 0.70f;

                case PostFlopLineClass.DonkBet:
                case PostFlopLineClass.LimpedPotLead:
                    return wet ? 0.50f : 0.33f;

                case PostFlopLineClass.MultiwayLead:
                    return wet ? 0.66f : 0.50f;

                case PostFlopLineClass.ProbeBet:
                case PostFlopLineClass.DelayedCBet:
                case PostFlopLineClass.DelayedFloatBet:
                case PostFlopLineClass.FloatBet:
                case PostFlopLineClass.StabAfterChecks:
                    return wet ? 0.60f : 0.50f;

                case PostFlopLineClass.CheckRaise:
                    return wet ? 0.85f : 0.75f;

                case PostFlopLineClass.RaiseVsCBet:
                case PostFlopLineClass.RaiseVsDonk:
                case PostFlopLineClass.RaiseVsProbe:
                case PostFlopLineClass.RaiseVsFloat:
                case PostFlopLineClass.RaiseVsDelayedCBet:
                case PostFlopLineClass.RaiseVsGenericBet:
                    return wet ? 0.85f : 0.75f;

                case PostFlopLineClass.ReRaise:
                    return 1.00f;

                case PostFlopLineClass.AllInPolarized:
                    return 1.25f;

                case PostFlopLineClass.GenericRaise:
                    return wet ? 0.75f : 0.66f;

                case PostFlopLineClass.GenericBet:
                case PostFlopLineClass.Unknown:
                default:
                    if (facingBet)
                        return wet ? 0.75f : 0.66f;

                    if (paired)
                        return 0.50f;

                    return wet ? 0.66f : 0.50f;
            }
        }
    }
}
