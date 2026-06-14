#include "GameState.h"
#include <algorithm>
#include <stdexcept>


namespace G5Cpp
{
    namespace
    {
        const int MAX_BETS_ON_STREET = 4;
        //int BETS_CUTOFF_POST_FLOP = 3; // Now this is a field GameState
        const int BETS_CUTOFF_PRE_FLOP = 3;
        const int RAISE_SIZE_NOM = 2;
        const int RAISE_SIZE_DEN = 3;
    }

    GameState::GameState(TableType tableType, int buttonInd, int heroIndex, const HoleCards& heroHoleCards, const PlayerDTO* players, int nPlayers, const Board& aBoard, Street street,
        int numBets, int numCallers, int bigBlindSize, const GameContext& aGc) : gc(aGc), board(aBoard)
    {
        _tableType = tableType;
        _players.reserve(nPlayers);

        for (int i = 0; i < nPlayers; i++)
            _players.emplace_back(players[i]);

        assert(heroIndex >= 0 && heroIndex < nPlayers);

        this->buttonInd = buttonInd;
        this->heroInd = heroIndex;
        this->playerToActInd = heroIndex;
        this->heroHoleCards = heroHoleCards;
        this->street = street;
        this->numBets = numBets;
        this->numCallers = numCallers;
        this->startNumBets = numBets;
        this->bigBlindSize = bigBlindSize;
        this->stertedOnFlop = (street <= Street_Flop);
        this->nodeChance = 1.0f;
        this->startNumActive = numActiveNonAllInPlayers();
    }

    int GameState::potSize() const
    {
        int sum = 0;

        for (const auto& player : _players)
            sum += player.moneyInPot();

        return sum;
    }

    int GameState::maxMoneyInThePot() const
    {
        int max = 0;

        for (const auto& player : _players)
        {
            if (max < player.moneyInPot())
                max = player.moneyInPot();
        }

        return max;
    }

    bool GameState::areBetsLeft() const
    {
        bool result = numBets < MAX_BETS_ON_STREET;

        if (street == Street_PreFlop)
        {
            result = result && (numBets - startNumBets < BETS_CUTOFF_PRE_FLOP);
        }
        else
        {
            result = result && (numBets - startNumBets < BETS_CUTOFF_POST_FLOP);
        }

        return result;
    }

    bool GameState::canNextPlayerRaise() const
    {
        return areBetsLeft() &&
            getAmountToCall() < playerToAct().stack() &&
            numActiveNonAllInPlayers() > 1;
    }

    int GameState::getRaiseAmount() const
    {
        int heroInPot = playerToAct().moneyInPot();
        int heroStack = playerToAct().stack();
        int maxInPot = maxMoneyInThePot();
        int amountToCall = maxInPot - heroInPot;

        if (amountToCall < 0)
            amountToCall = 0;

        if (heroStack <= 0)
            return 0;

        if (amountToCall > heroStack)
            amountToCall = heroStack;

        if (street == Street_PreFlop)
        {
            if (bigBlindSize <= 0)
                return amountToCall;

            int targetTotal = 0;

            if (numBets <= 0)
            {
                int callers = numCallers;

                if (callers < 0)
                    callers = 0;

                targetTotal = (3 + callers) * bigBlindSize;
            }
            else
            {
                targetTotal = 3 * maxInPot;
            }

            int amountToAdd = targetTotal - heroInPot;

            if (amountToAdd < amountToCall)
                amountToAdd = amountToCall;

            if (amountToAdd > heroStack)
                amountToAdd = heroStack;

            return amountToAdd;
        }

        return (RAISE_SIZE_NOM * (potSize() + amountToCall)) / RAISE_SIZE_DEN + amountToCall;
    }

    int GameState::normalizeBetRaiseAmount(int requestedAmount) const
    {
        assert(playerToActInd != -1);

        int stack = playerToAct().stack();
        int amountToCall = getAmountToCall();

        if (stack <= 0)
            return 0;

        int amount = requestedAmount;

        if (amount <= 0)
            amount = getRaiseAmount();

        if (amountToCall > 0 && amount < amountToCall)
            amount = amountToCall;

        if (amountToCall == 0 && amount < bigBlindSize)
            amount = bigBlindSize;

        if (amount > stack)
            amount = stack;

        return amount;
    }

    int GameState::getAmountToCall() const
    {
        assert(playerToActInd != -1);

        int amountToCall = maxMoneyInThePot() - playerToAct().moneyInPot();
        int playerToActStack = playerToAct().stack();

        if (amountToCall >= playerToActStack)
            amountToCall = playerToActStack;

        return amountToCall;
    }

    bool GameState::isBanned(const Card& card) const
    {
        int cardInd = card.toInt();

        if (heroHoleCards.Card0.toInt() == cardInd)
        {
            return true;
        }

        if (heroHoleCards.Card1.toInt() == cardInd)
        {
            return true;
        }

        for (int i = 0; i < board.size; i++)
        {
            if (board.card[i].toInt() == cardInd)
                return true;
        }

        return false;
    }

    const Player& GameState::hero() const
    {
        return _players[heroInd];
    }

    const Player& GameState::playerToAct() const
    {
        return _players[playerToActInd];
    }

    Player& GameState::playerToAct()
    {
        return _players[playerToActInd];
    }

    int GameState::numActivePlayers() const
    {
        int activePlayers = 0;

        for (const auto& player : _players)
        {
            if (player.statusInHand() != Status_Folded)
                activePlayers++;
        }

        return activePlayers;
    }

    int GameState::numActiveNonAllInPlayers() const
    {
        int activePlayers = 0;

        for (const auto& player : _players)
        {
            if (player.statusInHand() != Status_Folded && player.statusInHand() != Status_WentAllIn)
                activePlayers++;
        }

        return activePlayers;
    }

    bool GameState::isPlayerInPosition(int playerIndex) const
    {
        assert(playerIndex > -1);

        if (playerIndex < 0)
            return false;

        // Button is in position in each situation
        if (playerIndex == buttonInd)
            return true;

        // For each opponent after player up to button
        for (int i = 1; i <= int(_players.size()); i++)
        {
            int oppInd = (playerIndex + i) % _players.size();
            assert(oppInd != playerIndex);

            if (_players[oppInd].statusInHand() == Status_ToAct || _players[oppInd].statusInHand() == Status_Acted)
                return false;

            if (oppInd == buttonInd)
                break;
        }

        return true;
    }

    bool GameState::isHeroFirstToAct_postFlop() const
    {
        // For each opponent from button to hero
        for (int i = 1; i <= int(_players.size()); i++)
        {
            int oppInd = (buttonInd + i) % _players.size();

            if (oppInd == heroInd)
                return true;

            if (_players[oppInd].statusInHand() == Status_ToAct || _players[oppInd].statusInHand() == Status_Acted)
                return false;
        }

        return true;
    }

    ActionDistribution GameState::getPlayerToActAD() const
    {
        ActionDistribution ad;

        if (street == Street_PreFlop)
        {
            // ---------------------------------------------------------------------
            // CORRECAO HU PREFLOP
            //
            // Problema encontrado no simulador offline:
            // Em jogos heads-up, PreFlopParams::toIndex() aceita apenas duas posicoes:
            // Position_SmallBlind e Position_BigBlind.
            //
            // Antes, o codigo usava diretamente:
            // playerToAct().preFlopPosition()
            //
            // Em alguns estados gerados pela simulacao, essa posicao podia chegar como
            // uma posicao generica, por exemplo BTN, Button, Unknown ou equivalente,
            // causando a excecao:
            //
            // "Bad position in heads-up game"
            //
            // Correcao:
            // Em HU, a posicao pre-flop deve ser derivada do buttonInd:
            // - o jogador no botao e o Small Blind;
            // - o outro jogador e o Big Blind.
            //
            // Isso nao cria uma decisao paliativa. Apenas corrige a representacao
            // matematica do estado antes de consultar o modelo estatistico.
            // ---------------------------------------------------------------------

            Position pfPosition = playerToAct().preFlopPosition();
            bool inPosition = isPlayerInPosition(playerToActInd);

            if (_tableType == TableType_HU)
            {
                if (_players.size() != 2)
                {
                    throw std::runtime_error("GameState::getPlayerToActAD: TableType_HU com numero de jogadores diferente de 2");
                }

                if (playerToActInd == buttonInd)
                {
                    pfPosition = Position_SmallBlind;

                    // Em HU, o Button/SB joga fora de posicao no pre-flop,
                    // mas em posicao no pos-flop.
                    inPosition = true;
                }
                else
                {
                    pfPosition = Position_BigBlind;

                    // Em HU, o BB joga em posicao no pre-flop,
                    // mas fora de posicao no pos-flop.
                    inPosition = false;
                }
            }

            PreFlopParams params(
                _tableType,
                pfPosition,
                numCallers,
                numBets,
                numActivePlayers(),
                playerToAct().lastAction(),
                inPosition
            );

            ad = playerToAct().getAD(params);
        }
        else
        {
            int round = playerToAct().gound();
            ActionType prevAction = (round == 0) ? playerToAct().prevStreetAction() : playerToAct().lastAction();

            PostFlopParams params(
                _tableType,
                street,
                round,
                prevAction,
                numBets,
                isPlayerInPosition(playerToActInd),
                numActivePlayers()
            );

            ad = playerToAct().getAD(params);
        }

        ad.assertNotEmpty();
        return ad;
    }

    void GameState::getOpponents(const Player** opponents, int& nOpponents) const
    {
        nOpponents = 0;

        for (int i = 0; i < (int)_players.size(); i++)
        {
            if ((_players[i].statusInHand() != Status_Folded) && (i != heroInd))
            {
                opponents[nOpponents] = &_players[i];
                nOpponents++;
            }
        }
    }

    float GameState::getPossibleWinnings() const
    {
        int winnings = 0;
        int maxOppMoneyInThePot = 0;

        for (int i = 0; i < (int)_players.size(); i++)
        {
            winnings += std::min(_players[i].moneyInPot(), _players[heroInd].moneyInPot());

            if (i != heroInd)
                maxOppMoneyInThePot = std::max(maxOppMoneyInThePot, _players[i].moneyInPot());
        }

        assert(bigBlindSize > 0);

        int rakableWinnings = winnings - std::max(_players[heroInd].moneyInPot() - maxOppMoneyInThePot, 0);
        int rake;

        if (bigBlindSize <= 10)
        {
            rake = rakableWinnings / 10;
            rake = std::min(rake, 10);
        }
        else
        {
            rake = rakableWinnings / 15;
        }

        winnings -= rake;
        return (float)winnings;
    }

    void GameState::goToFirstPlayer()
    {
        if (numActivePlayers() <= 1)
        {
            playerToActInd = -1;
            return;
        }

        // Foreach player after button
        for (int i = 1; i <= int(_players.size()); i++)
        {
            int ind = (buttonInd + i) % _players.size();

            if (_players[ind].statusInHand() == Status_ToAct)
            {
                playerToActInd = ind;
                return;
            }
        }

        playerToActInd = -1;
    }

    void GameState::goToNextPlayer()
    {
        if (numActivePlayers() <= 1)
        {
            playerToActInd = -1;
            return;
        }

        assert(_players.size() > 1);

        for (int i = playerToActInd + 1; i < (int)_players.size(); i++)
        {
            if (_players[i].statusInHand() == Status_ToAct)
            {
                playerToActInd = i;
                return;
            }
        }

        for (int i = 0; i < playerToActInd; i++)
        {
            if (_players[i].statusInHand() == Status_ToAct)
            {
                playerToActInd = i;
                return;
            }
        }

        playerToActInd = -1;
    }

    GameState GameState::goToNextStreet(const Card& card) const
    {
        GameState newPrms(*this);

        // Reset acted players after street
        int acted = 0;
        int allin = 0;

        for (const auto& player : newPrms._players)
        {
            if (player.statusInHand() == Status_Acted)
                acted++;

            if (player.statusInHand() == Status_WentAllIn)
                allin++;
        }

        // Svi su AllIn osim jednog koji ima vise novca - On postaje AllIn takodje.
        if (acted == 1 && allin > 0)
        {
            for (auto& player : newPrms._players)
            {
                if (player.statusInHand() == Status_Acted)
                    player.setStatus(Status_WentAllIn);
            }
        }

        for (auto& player : newPrms._players)
        {
            if (player.statusInHand() == Status_Acted)
                player.setStatus(Status_ToAct);
        }

        // Players go to next street
        for (auto& player : newPrms._players)
        {
            player.nextStreet();
            player.banCardInRange(card);
        }

        // Add card to board
        newPrms.board.addCard(card);

        // Set other parameters
        newPrms.goToFirstPlayer();
        newPrms.street = (Street)(newPrms.street + 1);
        newPrms.startNumBets = 0;
        newPrms.numBets = 0;
        newPrms.numCallers = 0;
        newPrms.nodeChance = 1.0f;

        return newPrms;
    }

    GameState GameState::playerCheckCalls(float betRaiseProb, float checkCallProb, float nodeProbability) const
    {
        GameState newPrms(*this);
        int amountToCall = getAmountToCall();

        if (amountToCall >= newPrms.playerToAct().stack())
        {
            amountToCall = newPrms.playerToAct().stack();
            newPrms.playerToAct().goesAllIn();
            newPrms.numCallers++;
        }
        else if (amountToCall > 0) // Ima betova
        {
            newPrms.playerToAct().calls(amountToCall);
            newPrms.numCallers++;
        }
        else
        {
            newPrms.playerToAct().checks();
        }

        if (newPrms.playerToActInd != newPrms.heroInd)
        {
            if (amountToCall == 0)
            {
                newPrms.playerToAct().cutRange_CheckBet(Action_Check, newPrms.board, betRaiseProb, gc);
            }
            else
            {
                newPrms.playerToAct().cutRange_FoldCallRaise(Action_Call, newPrms.board, betRaiseProb, checkCallProb, gc);
            }
        }

        newPrms.goToNextPlayer();

        if (nodeProbability != 1.0f)
            newPrms.nodeChance *= nodeProbability;

        return newPrms;
    }

    void GameState::resetActedPlayersAfterRaise()
    {
        int newAmountInPot = maxMoneyInThePot();

        for (auto& player : _players)
        {
            if (player.moneyInPot() < newAmountInPot && player.statusInHand() == Status_Acted)
                player.setStatus(Status_ToAct);
        }
    }

    GameState GameState::playerBetRaises(float betRaiseProb, float checkCallProb, float nodeProbability) const
    {
        return playerBetRaises(betRaiseProb, checkCallProb, nodeProbability, 0);
    }

    GameState GameState::playerBetRaises(float betRaiseProb, float checkCallProb, float nodeProbability, int forcedBetRaiseAmount) const
    {
        GameState newPrms(*this);
        int raiseAmount = newPrms.normalizeBetRaiseAmount(forcedBetRaiseAmount);
        int amountToCall = getAmountToCall();

        if (raiseAmount >= newPrms.playerToAct().stack())
        {
            newPrms.playerToAct().goesAllIn();
        }
        else
        {
            int betToAmmount = newPrms.playerToAct().moneyInPot() + raiseAmount;
            newPrms.playerToAct().betsOrRaisesTo(betToAmmount);
        }

        if (newPrms.playerToActInd != newPrms.heroInd)
        {
            if (amountToCall == 0)
            {
                ActionType actionType = (raiseAmount >= playerToAct().stack()) ? Action_AllIn : Action_Bet;
                newPrms.playerToAct().cutRange_CheckBet(actionType, newPrms.board, betRaiseProb, gc);
            }
            else
            {
                ActionType actionType = (raiseAmount >= playerToAct().stack()) ? Action_AllIn : Action_Raise;
                newPrms.playerToAct().cutRange_FoldCallRaise(actionType, newPrms.board, betRaiseProb, checkCallProb, gc);
            }
        }

        newPrms.resetActedPlayersAfterRaise();
        newPrms.goToNextPlayer();
        newPrms.numCallers = 0;
        newPrms.numBets += 1;

        if (nodeProbability != 1.0f)
            newPrms.nodeChance *= nodeProbability;

        return newPrms;
    }

    GameState GameState::playerFolds(float nodeProbability) const
    {
        assert(playerToActInd != heroInd);

        GameState newPrms(*this);
        newPrms.playerToAct().folds();
        newPrms.goToNextPlayer();
        newPrms.nodeChance *= nodeProbability;
        return newPrms;
    }
}
