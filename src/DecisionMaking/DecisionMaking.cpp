#include <string>
#include <list>
#include "Common.h"
#include "Player.h"
#include "HoleCards.h"
#include "GameState.h"
#include "HandStrengthCounter.h"
#include "AllCounters.h"
#include "Pot.h"
#include "ParkMillerCarta.h"
#include "tbb/parallel_for.h"
#include "tbb/blocked_range.h"
#include "PreFlopEquity.h"
#include <algorithm>
#include <limits>
#include <cstdio>
#include <stdexcept>
#include <cstdlib>
#include <cstring>
#include <cstdarg>
#include <cmath>
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#ifdef min
#undef min
#endif
#ifdef max
#undef max
#endif

// =============================================================================
// Logging da DecisionMaking
// =============================================================================
//
// Por padrao, a DecisionMaking fica silenciosa em chamadas bem-sucedidas.
// O log detalhado pode ser ativado sem recompilar, criando o arquivo:
//
//     C:\G5Pressure\DecisionMaking.diagnostic
//
// ou definindo a variavel de ambiente:
//
//     DECISIONMAKING_DIAGNOSTIC=1
//
// Em caso de erro fatal, a DLL sempre registra o erro, mesmo com diagnostico OFF.
// Isso evita poluir o log normal, mas preserva informacao util quando algo falha.

static const char* DM_LOG_PATH = "C:\\G5Pressure\\DecisionMaking_debug.log";
static const char* DM_DIAGNOSTIC_FLAG_PATH = "C:\\G5Pressure\\DecisionMaking.diagnostic";
static const DWORD DM_FATAL_EXCEPTION_CODE = 0xE0000001;
static const char* DM_BUILD_ID = "phase12_terminal_rake_preflop_independent";

static bool DMIsDiagnosticEnabled()
{
    const char* env = getenv("DECISIONMAKING_DIAGNOSTIC");

    if (env != nullptr)
    {
        if (_stricmp(env, "1") == 0 ||
            _stricmp(env, "true") == 0 ||
            _stricmp(env, "on") == 0 ||
            _stricmp(env, "yes") == 0)
        {
            return true;
        }
    }

    DWORD attrs = GetFileAttributesA(DM_DIAGNOSTIC_FLAG_PATH);
    return (attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY));
}

static void DMWriteLog(const char* msg, bool force)
{
    if (!force && !DMIsDiagnosticEnabled())
        return;

    FILE* f = fopen(DM_LOG_PATH, "a");
    if (!f) return;

    SYSTEMTIME st;
    GetLocalTime(&st);

    fprintf(
        f,
        "[%02d:%02d:%02d.%03d] %s\n",
        st.wHour,
        st.wMinute,
        st.wSecond,
        st.wMilliseconds,
        msg
    );

    fflush(f);
    fclose(f);
}

static void DMLog(const char* msg)
{
    DMWriteLog(msg, false);
}

static void DMLogAlways(const char* msg)
{
    DMWriteLog(msg, true);
}

static void DMLogF(const char* fmt, ...)
{
    if (!DMIsDiagnosticEnabled())
        return;

    char buf[2048];
    va_list args;
    va_start(args, fmt);
    vsnprintf_s(buf, sizeof(buf), _TRUNCATE, fmt, args);
    va_end(args);

    DMLog(buf);
}

static volatile LONG g_DMHeroNodeLogCount = 0;
static volatile LONG g_DMVillainActionLogCount = 0;
static volatile LONG g_DMShowdownLogCount = 0;
static volatile LONG g_DMTurnCutoffLogCount = 0;

static const LONG DM_MAX_HERO_NODE_LOGS = 80;
static const LONG DM_MAX_VILLAIN_ACTION_LOGS = 160;
static const LONG DM_MAX_SHOWDOWN_LOGS = 120;
static const LONG DM_MAX_TURNCUTOFF_LOGS = 80;

static void DMResetDiagnosticCounters()
{
    InterlockedExchange(&g_DMHeroNodeLogCount, 0);
    InterlockedExchange(&g_DMVillainActionLogCount, 0);
    InterlockedExchange(&g_DMShowdownLogCount, 0);
    InterlockedExchange(&g_DMTurnCutoffLogCount, 0);
}

static bool DMShouldLog(volatile LONG* counter, LONG limit)
{
    if (!DMIsDiagnosticEnabled())
        return false;

    LONG value = InterlockedIncrement(counter);
    return value <= limit;
}

static void DMLogFmtInternal(
    const char* label,
    const void* gc,
    const void* players,
    const void* cardsInBoard,
    int buttonInd,
    int heroIndex,
    int nPlayers,
    int street,
    int numBets,
    int numCallers,
    int bigBlindSize,
    bool force
)
{
    if (!force && !DMIsDiagnosticEnabled())
        return;

    FILE* f = fopen(DM_LOG_PATH, "a");
    if (!f) return;

    SYSTEMTIME st;
    GetLocalTime(&st);

    fprintf(f, "[%02d:%02d:%02d.%03d] %s\n",
        st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, label);

    fprintf(f, "    gc=%p\n", gc);
    fprintf(f, "    players=%p\n", players);
    fprintf(f, "    cardsInBoard=%p\n", cardsInBoard);
    fprintf(f, "    buttonInd=%d\n", buttonInd);
    fprintf(f, "    heroIndex=%d\n", heroIndex);
    fprintf(f, "    nPlayers=%d\n", nPlayers);
    fprintf(f, "    street=%d\n", street);
    fprintf(f, "    numBets=%d\n", numBets);
    fprintf(f, "    numCallers=%d\n", numCallers);
    fprintf(f, "    bigBlindSize=%d\n", bigBlindSize);

    fflush(f);
    fclose(f);
}

static void DMLogFmt(
    const char* label,
    const void* gc,
    const void* players,
    const void* cardsInBoard,
    int buttonInd,
    int heroIndex,
    int nPlayers,
    int street,
    int numBets,
    int numCallers,
    int bigBlindSize
)
{
    DMLogFmtInternal(
        label,
        gc,
        players,
        cardsInBoard,
        buttonInd,
        heroIndex,
        nPlayers,
        street,
        numBets,
        numCallers,
        bigBlindSize,
        false
    );
}

static void DMLogFmtAlways(
    const char* label,
    const void* gc,
    const void* players,
    const void* cardsInBoard,
    int buttonInd,
    int heroIndex,
    int nPlayers,
    int street,
    int numBets,
    int numCallers,
    int bigBlindSize
)
{
    DMLogFmtInternal(
        label,
        gc,
        players,
        cardsInBoard,
        buttonInd,
        heroIndex,
        nPlayers,
        street,
        numBets,
        numCallers,
        bigBlindSize,
        true
    );
}

static void DMFatal(const char* msg)
{
    DMLogAlways(msg);

    ULONG_PTR args[1];
    args[0] = reinterpret_cast<ULONG_PTR>(msg);

    // Codigo proprio de falha diagnostica da DecisionMaking.
    // A ideia nao e retornar EV paliativo, mas interromper a chamada com log claro.
    RaiseException(DM_FATAL_EXCEPTION_CODE, EXCEPTION_NONCONTINUABLE, 1, args);
}

namespace G5Cpp
{
    namespace
    {
        const int MAX_PLAYERS = 6;

        const int SHOWDOWN_BIN_COUNT = 13260;
        const int SHOWDOWN_ITERATIONS = 2000;

        const int TCUTOFF_BIN_COUNT = 13260;
        const int TCUTOFF_ITERATIONS = 1000;

        const float NODE_CHANCE_CUTOFF = 0.0005f;

        const char* DMStreetName(Street street)
        {
            switch (street)
            {
            case Street_PreFlop: return "preflop";
            case Street_Flop:    return "flop";
            case Street_Turn:    return "turn";
            case Street_River:   return "river";
            default:             return "?";
            }
        }

        int DMCountActiveRangeCombos(const Player& player, float& likelihoodSum)
        {
            likelihoodSum = 0.0f;

            int activeCombos = 0;
            int rangeLength = player.range().length();
            const float* likelihood = player.range().likelihood();

            for (int i = 0; i < rangeLength; i++)
            {
                if (likelihood[i] > 0.0f)
                {
                    activeCombos++;
                    likelihoodSum += likelihood[i];
                }
            }

            return activeCombos;
        }


        int DMDebugPotSize(const GameState& prms)
        {
            int sum = 0;

            for (const auto& player : prms._players)
                sum += player.moneyInPot();

            return sum;
        }

        int DMDebugMaxMoneyInThePot(const GameState& prms)
        {
            int maxMoney = 0;

            for (const auto& player : prms._players)
            {
                if (maxMoney < player.moneyInPot())
                    maxMoney = player.moneyInPot();
            }

            return maxMoney;
        }

        int DMDebugNumActiveNonAllInPlayers(const GameState& prms)
        {
            int activePlayers = 0;

            for (const auto& player : prms._players)
            {
                if (player.statusInHand() != Status_Folded &&
                    player.statusInHand() != Status_WentAllIn)
                {
                    activePlayers++;
                }
            }

            return activePlayers;
        }

        int DMDebugRaiseAmount(const GameState& prms, int amountToCall)
        {
            int pot = DMDebugPotSize(prms);

            if (prms.playerToActInd < 0 || prms.playerToActInd >= (int)prms._players.size())
                return 0;

            const Player& player = prms._players[prms.playerToActInd];

            int heroInPot = player.moneyInPot();
            int heroStack = player.stack();
            int maxInPot = DMDebugMaxMoneyInThePot(prms);

            if (amountToCall < 0)
                amountToCall = 0;

            if (heroStack <= 0)
                return 0;

            if (amountToCall > heroStack)
                amountToCall = heroStack;

            if (prms.street == Street_PreFlop)
            {
                if (prms.bigBlindSize <= 0)
                    return amountToCall;

                int targetTotal = 0;

                if (prms.numBets <= 0)
                {
                    int callers = prms.numCallers;

                    if (callers < 0)
                        callers = 0;

                    targetTotal = (3 + callers) * prms.bigBlindSize;
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

            return (2 * (pot + amountToCall)) / 3 + amountToCall;
        }

        struct DMRangeSampler
        {
            int length;
            int activeCombos;
            float mass;
            int handIndex[N_HOLECARDS];
            float cdf[N_HOLECARDS];
        };

        void DMBuildBaseBlockedCards(bool* blocked, const GameState& prms)
        {
            for (int i = 0; i < 52; i++)
                blocked[i] = false;

            int hero0 = prms.heroHoleCards.Card0.toInt();
            int hero1 = prms.heroHoleCards.Card1.toInt();

            if (hero0 >= 0 && hero0 < 52)
                blocked[hero0] = true;

            if (hero1 >= 0 && hero1 < 52)
                blocked[hero1] = true;

            for (int i = 0; i < prms.board.size; i++)
            {
                int card = prms.board.card[i].toInt();

                if (card >= 0 && card < 52)
                    blocked[card] = true;
            }
        }

        bool DMHandConflictsWithBlockedCards(int handIndex, const bool* blocked)
        {
            int c0 = handIndex / 52;
            int c1 = handIndex % 52;

            if (c0 < 0 || c0 >= 52 || c1 < 0 || c1 >= 52)
                return true;

            if (c0 == c1)
                return true;

            return blocked[c0] || blocked[c1];
        }

        void DMBuildRangeSampler(DMRangeSampler& sampler, const Player& player, const bool* baseBlocked, const char* context)
        {
            sampler.length = 0;
            sampler.activeCombos = 0;
            sampler.mass = 0.0f;

            const Range& range = player.range();
            int rangeLength = range.length();
            const int* hcIndex = range.hcIndex();
            const float* likelihood = range.likelihood();

            for (int i = 0; i < rangeLength; i++)
            {
                float w = likelihood[i];

                if (!(w > 0.0f) || !std::isfinite(w))
                    continue;

                int handIndex = hcIndex[i];

                if (DMHandConflictsWithBlockedCards(handIndex, baseBlocked))
                    continue;

                sampler.handIndex[sampler.length] = handIndex;
                sampler.mass += w;
                sampler.cdf[sampler.length] = sampler.mass;
                sampler.length++;
                sampler.activeCombos++;
            }

            if (sampler.length <= 0 || !(sampler.mass > 0.0f) || !std::isfinite(sampler.mass))
            {
                char msg[512];
                snprintf(msg, sizeof(msg), "DecisionMaking: range sem massa valida em sampler multiway (%s)", context ? context : "sem contexto");
                DMLogAlways(msg);
                throw std::runtime_error(msg);
            }

            float invMass = 1.0f / sampler.mass;

            for (int i = 0; i < sampler.length; i++)
                sampler.cdf[i] *= invMass;

            sampler.cdf[sampler.length - 1] = 1.0f;
        }

        float DMRand01(ParkMillerCarta& rng)
        {
            unsigned int v = static_cast<unsigned int>(rng.next());
            return ((v % 1000000u) + 0.5f) / 1000000.0f;
        }

        int DMSampleHandIndex(const DMRangeSampler& sampler, ParkMillerCarta& rng)
        {
            float x = DMRand01(rng);
            int lo = 0;
            int hi = sampler.length - 1;

            while (lo < hi)
            {
                int mid = lo + ((hi - lo) / 2);

                if (x <= sampler.cdf[mid])
                    hi = mid;
                else
                    lo = mid + 1;
            }

            return sampler.handIndex[lo];
        }

        bool DMTryReserveOpponentHand(int handIndex, const bool* baseBlocked, bool* usedOpponentCards, int& c0, int& c1)
        {
            c0 = handIndex / 52;
            c1 = handIndex % 52;

            if (c0 < 0 || c0 >= 52 || c1 < 0 || c1 >= 52)
                return false;

            if (c0 == c1)
                return false;

            if (baseBlocked[c0] || baseBlocked[c1])
                return false;

            if (usedOpponentCards[c0] || usedOpponentCards[c1])
                return false;

            usedOpponentCards[c0] = true;
            usedOpponentCards[c1] = true;
            return true;
        }

        void DMLogOpponentRanges(const char* label, const GameState& prms)
        {
            if (!DMIsDiagnosticEnabled())
                return;

            int nOpponents = 0;
            const Player* opponents[MAX_PLAYERS];
            prms.getOpponents(opponents, nOpponents);

            DMLogF("[DM][%s] opponents=%d", label, nOpponents);

            for (int i = 0; i < nOpponents; i++)
            {
                float likelihoodSum = 0.0f;
                int activeCombos = DMCountActiveRangeCombos(*opponents[i], likelihoodSum);

                DMLogF(
                    "[DM][%s] opp%d rangeLen=%d activeCombos=%d likelihoodSum=%.6f moneyInPot=%d stack=%d status=%d lastAction=%d prevStreetAction=%d",
                    label,
                    i,
                    opponents[i]->range().length(),
                    activeCombos,
                    likelihoodSum,
                    opponents[i]->moneyInPot(),
                    opponents[i]->stack(),
                    static_cast<int>(opponents[i]->statusInHand()),
                    static_cast<int>(opponents[i]->lastAction()),
                    static_cast<int>(opponents[i]->prevStreetAction())
                );
            }
        }

        void DMLogRootContext(const char* label, const GameState& prms)
        {
            if (!DMIsDiagnosticEnabled())
                return;

            int amountToCall = prms.getAmountToCall();
            bool canRaise = prms.canNextPlayerRaise();
            int raiseAmount = canRaise ? DMDebugRaiseAmount(prms, amountToCall) : 0;

            DMLogF(
                "[DM][ROOT %s] street=%s(%d) pot=%d possibleWinnings=%.2f amountToCall=%d heroInPot=%d heroStack=%d maxInPot=%d numBets=%d startNumBets=%d numCallers=%d active=%d nonAllIn=%d canRaise=%d raiseAmount=%d nodeChance=%.6f",
                label,
                DMStreetName(prms.street),
                static_cast<int>(prms.street),
                DMDebugPotSize(prms),
                prms.getPossibleWinnings(),
                amountToCall,
                prms.hero().moneyInPot(),
                prms.hero().stack(),
                DMDebugMaxMoneyInThePot(prms),
                prms.numBets,
                prms.startNumBets,
                prms.numCallers,
                prms.numActivePlayers(),
                DMDebugNumActiveNonAllInPlayers(prms),
                canRaise ? 1 : 0,
                raiseAmount,
                prms.nodeChance
            );

            DMLogOpponentRanges(label, prms);
        }

        struct DMStats
        {
            struct LeafLogNode
            {
                Street street;
                float chance;
                float ev;
                int nOpponents;
                int heroMoneyInThePot;
                float possibleWinnings;
                int numRaises;
            };

            int numTurnCutOffs;
            float turnCutOff_NumRiversChecked;
            float turnCutOff_NumPots;
            int turnCutOff_OppHist[MAX_PLAYERS];

            int numShowDowns;
            int showDown_NumValidRuns;
            int showDown_OppHist[MAX_PLAYERS];

            std::vector<LeafLogNode> leafLog;

            DMStats()
            {
                for (int i = 0; i < MAX_PLAYERS; i++)
                {
                    showDown_OppHist[i] = 0;
                    turnCutOff_OppHist[i] = 0;
                }

                numTurnCutOffs = 0;
                turnCutOff_NumRiversChecked = 0;
                turnCutOff_NumPots = 0;

                numShowDowns = 0;
                showDown_NumValidRuns = 0;
            }

            void add(const DMStats& stats)
            {
                for (int i = 0; i < MAX_PLAYERS; i++)
                {
                    showDown_OppHist[i] += stats.showDown_OppHist[i];
                    turnCutOff_OppHist[i] += stats.turnCutOff_OppHist[i];
                }

                numTurnCutOffs += stats.numTurnCutOffs;
                turnCutOff_NumRiversChecked += stats.turnCutOff_NumRiversChecked;
                turnCutOff_NumPots += stats.turnCutOff_NumPots;

                numShowDowns += stats.numShowDowns;
                showDown_NumValidRuns += stats.showDown_NumValidRuns;

                leafLog.insert(leafLog.end(), stats.leafLog.begin(), stats.leafLog.end());
            }

            void saveToFile(const char* filename)
            {
                FILE* f = fopen(filename, "w");

                if (f)
                {
                    fprintf(f, "NumOpp, NumRaises, NodeChance, EV, PossWinn, MoneyInPot\n");

                    for (const auto& node : leafLog)
                    {
                        fprintf(f, "%d,\t%d,\t%.2f,\t%.2f,\t%.2f,\t%d\n", node.nOpponents, node.numRaises, node.chance, node.ev,
                            node.possibleWinnings, node.heroMoneyInThePot);
                    }

                    fclose(f);
                }
            }
        };

        float estimateEV(DMStats& stats, const GameState& prms);

        /// <summary>
        /// Calculate the hero EV if it is the hero's turn.
        /// </summary>
        void estimateEV_HeroPlays(float& checkCallEV, float& betRaiseEV, DMStats& stats, const GameState& prms, int forcedRootBetRaiseAmount = 0)
        {
            assert(prms.heroInd == prms.playerToActInd);

            bool logHeroNode = (prms.street > Street_PreFlop) && DMShouldLog(&g_DMHeroNodeLogCount, DM_MAX_HERO_NODE_LOGS);
            int callCost = prms.getAmountToCall();
            bool canRaise = prms.canNextPlayerRaise();
            int raiseAmount = canRaise ? DMDebugRaiseAmount(prms, callCost) : 0;

            if (forcedRootBetRaiseAmount < 0)
                forcedRootBetRaiseAmount = 0;

            if (logHeroNode)
            {
                DMLogF(
                    "[DM][HERO NODE] street=%s(%d) pot=%d possibleWinnings=%.2f amountToCall=%d heroInPot=%d heroStack=%d canRaise=%d raiseAmount=%d nodeChance=%.6f",
                    DMStreetName(prms.street),
                    static_cast<int>(prms.street),
                    DMDebugPotSize(prms),
                    prms.getPossibleWinnings(),
                    callCost,
                    prms.hero().moneyInPot(),
                    prms.hero().stack(),
                    canRaise ? 1 : 0,
                    raiseAmount,
                    prms.nodeChance
                );
                DMLogOpponentRanges("HERO NODE", prms);
            }

            float checkCallTreeEV = 0.0f;
            checkCallEV = 0.0f;
            {
                GameState newPrms = prms.playerCheckCalls(0, 0, 1.0f);
                checkCallTreeEV = estimateEV(stats, newPrms);
                checkCallEV = checkCallTreeEV - callCost;
            }

            float betRaiseTreeEV = 0.0f;
            int raiseCost = 0;
            betRaiseEV = 0.0f;

            // Can hero raise
            if (canRaise)
            {
                if (forcedRootBetRaiseAmount > 0)
                {
                    GameState newPrms = prms.playerBetRaises(0, 0, 1.0f, forcedRootBetRaiseAmount);
                    betRaiseTreeEV = estimateEV(stats, newPrms);
                    raiseCost = newPrms.hero().moneyInPot() - prms.hero().moneyInPot();
                    betRaiseEV = betRaiseTreeEV - raiseCost;
                }
                else
                {
                    GameState newPrms = prms.playerBetRaises(0, 0, 1.0f);
                    betRaiseTreeEV = estimateEV(stats, newPrms);
                    raiseCost = newPrms.hero().moneyInPot() - prms.hero().moneyInPot();
                    betRaiseEV = betRaiseTreeEV - raiseCost;
                }
            }

            if (logHeroNode)
            {
                if (forcedRootBetRaiseAmount > 0)
                    DMLogF("[DM][HERO FORCED SIZE] forcedRootBetRaiseAmount=%d", forcedRootBetRaiseAmount);

                DMLogF(
                    "[DM][HERO EV] street=%s(%d) checkCallTreeEV=%.6f callCost=%d checkCallEV=%.6f betRaiseTreeEV=%.6f raiseCost=%d betRaiseEV=%.6f",
                    DMStreetName(prms.street),
                    static_cast<int>(prms.street),
                    checkCallTreeEV,
                    callCost,
                    checkCallEV,
                    betRaiseTreeEV,
                    raiseCost,
                    betRaiseEV
                );
            }
        }

        /// <summary>
        /// Calculate the villain EV if it is the villain's turn.
        /// </summary>
        float estimateEV_VillainPlays(DMStats& stats, const GameState& prms)
        {
            int amountToCall = prms.getAmountToCall();
            ActionDistribution ad = prms.getPlayerToActAD();

            float rawFoldProb = ad.foldProb;
            float rawCheckCallProb = ad.checkCallProb;
            float rawBetRaiseProb = ad.betRaiseProb;

            float predFoldProb = ad.foldProb;
            float predCheckCallProb = ad.checkCallProb;
            float predBetRaiseProb = ad.betRaiseProb;

            if (prms.street > Street_PreFlop) // Predict postflop actions from the current weighted range and board.
            {
                if (amountToCall > 0)
                {
                    prms.playerToAct().predictAction_FoldCallRaise(predFoldProb, predCheckCallProb, predBetRaiseProb,
                        prms.board, ad.betRaiseProb, ad.checkCallProb, prms.gc);
                }
                else
                {
                    predFoldProb = 0.0f;
                    prms.playerToAct().predictAction_CheckBet(predCheckCallProb, predBetRaiseProb,
                        prms.board, ad.betRaiseProb, prms.gc);
                }
            }

            float checkCallProb = ad.checkCallProb;
            float betRaiseProb = ad.betRaiseProb;

            bool canVillainRaise = prms.canNextPlayerRaise();

            // Can villain raise
            if (!canVillainRaise)
            {
                checkCallProb += betRaiseProb;
                betRaiseProb = 0.0f;

                predCheckCallProb += predBetRaiseProb;
                predBetRaiseProb = 0.0f;
            }

            bool logVillainNode = (prms.street > Street_PreFlop) && DMShouldLog(&g_DMVillainActionLogCount, DM_MAX_VILLAIN_ACTION_LOGS);

            if (logVillainNode)
            {
                float likelihoodSum = 0.0f;
                int activeCombos = DMCountActiveRangeCombos(prms.playerToAct(), likelihoodSum);

                DMLogF(
                    "[DM][VILLAIN AD] street=%s(%d) playerIndex=%d pot=%d amountToCall=%d inPot=%d stack=%d canRaise=%d rangeLen=%d activeCombos=%d likelihoodSum=%.6f raw(fold=%.6f cc=%.6f br=%.6f) pred(fold=%.6f cc=%.6f br=%.6f) cutParams(cc=%.6f br=%.6f)",
                    DMStreetName(prms.street),
                    static_cast<int>(prms.street),
                    prms.playerToActInd,
                    DMDebugPotSize(prms),
                    amountToCall,
                    prms.playerToAct().moneyInPot(),
                    prms.playerToAct().stack(),
                    canVillainRaise ? 1 : 0,
                    prms.playerToAct().range().length(),
                    activeCombos,
                    likelihoodSum,
                    rawFoldProb,
                    rawCheckCallProb,
                    rawBetRaiseProb,
                    predFoldProb,
                    predCheckCallProb,
                    predBetRaiseProb,
                    checkCallProb,
                    betRaiseProb
                );
            }

            float foldEV = 0.0f;

            if ((amountToCall > 0) && (predFoldProb > 0.0f))
            {
                assert(prms.numActivePlayers() >= 2);

                GameState newPrms = prms.playerFolds(predFoldProb);
                foldEV = estimateEV(stats, newPrms);
            }

            float checkCallEV = 0.0f;
            {
                GameState newPrms = prms.playerCheckCalls(betRaiseProb, checkCallProb, predCheckCallProb);
                checkCallEV = estimateEV(stats, newPrms);
            }

            float betRaiseEV = 0.0f;

            if (prms.canNextPlayerRaise())
            {
                GameState newPrms = prms.playerBetRaises(betRaiseProb, checkCallProb, predBetRaiseProb);
                betRaiseEV = estimateEV(stats, newPrms);
            }

            float villainEV = (predFoldProb * foldEV) + (predCheckCallProb * checkCallEV) + (predBetRaiseProb * betRaiseEV);

            if (logVillainNode)
            {
                DMLogF(
                    "[DM][VILLAIN EV] street=%s(%d) playerIndex=%d foldEV=%.6f checkCallEV=%.6f betRaiseEV=%.6f weightedEV=%.6f pred(fold=%.6f cc=%.6f br=%.6f)",
                    DMStreetName(prms.street),
                    static_cast<int>(prms.street),
                    prms.playerToActInd,
                    foldEV,
                    checkCallEV,
                    betRaiseEV,
                    villainEV,
                    predFoldProb,
                    predCheckCallProb,
                    predBetRaiseProb
                );
            }

            return villainEV;
        }

        void estimateEV_NextStreet(float* EV, DMStats* stats, const GameState& prms, int begin, int end)
        {
            for (int i = begin; i < end; i++)
            {
                Card card(i);

                if (!prms.isBanned(card))
                {
                    GameState newPrms = prms.goToNextStreet(card);
                    EV[i] = estimateEV(stats[i], newPrms);
                }
            }
        }

        /// <summary>
        /// Goes to next street in EV calculation.
        /// </summary>
        float estimateEV_NextStreet_mt(DMStats& stats, const GameState& prms)
        {
            class Worker
            {
                float* EV;
                DMStats* stats;
                const GameState& prms;

            public:

                Worker(float* aEV, DMStats* aStats, const GameState& aPrms) : prms(aPrms)
                {
                    EV = aEV;
                    stats = aStats;
                }

                void operator() (const tbb::blocked_range<int>& br) const
                {
                    estimateEV_NextStreet(EV, stats, prms, br.begin(), br.end());
                }
            };

            float EV[52];
            DMStats statsArray[52];

            if (USE_MT)
            {
                Worker worker(EV, statsArray, prms);
                tbb::parallel_for(tbb::blocked_range<int>(0, 52), worker, tbb::auto_partitioner());
            }
            else
            {
                estimateEV_NextStreet(EV, statsArray, prms, 0, 52);
            }

            float totalEV = 0.0f;
            int cnt = 0;

            for (int i = 0; i < 52; i++)
            {
                if (!prms.isBanned(Card(i)))
                {
                    stats.add(statsArray[i]);
                    totalEV += EV[i];
                    cnt++;
                }
            }

            return totalEV / cnt;
        }

        float DMTerminalRakeForHeroWinnings(const GameState& prms, float grossWinnings)
        {
            if (!(grossWinnings > 0.0f) || !std::isfinite(grossWinnings))
                return 0.0f;

            if (prms.bigBlindSize <= 0)
                return 0.0f;

            float rake = 0.0f;

            // Same simplified rake schedule used by GameState::getPossibleWinnings(),
            // but applied to terminal multiway pot shares that bypass that function.
            if (prms.bigBlindSize <= 10)
            {
                rake = grossWinnings * 0.10f;
                rake = (std::min)(rake, 10.0f);
            }
            else
            {
                rake = grossWinnings / 15.0f;
            }

            if (!std::isfinite(rake) || rake < 0.0f)
                return 0.0f;

            return (std::min)(rake, grossWinnings);
        }

        float DMApplyTerminalRakeToHeroWinnings(const GameState& prms, float grossWinnings)
        {
            if (!(grossWinnings > 0.0f) || !std::isfinite(grossWinnings))
                return 0.0f;

            return grossWinnings - DMTerminalRakeForHeroWinnings(prms, grossWinnings);
        }

        /// <summary>
        /// Calculates EV when show down occurs
        /// </summary>
        float estimateEV_ShowDown(DMStats& stats, const GameState& prms)
        {
            int nOpponents = 0;
            const Player* opponents[MAX_PLAYERS];
            prms.getOpponents(opponents, nOpponents);

            stats.numShowDowns++;
            stats.showDown_OppHist[nOpponents]++;

            int turn = prms.board.card[3].toInt();
            int river = prms.board.card[4].toInt();

            bool baseBlocked[52];
            DMBuildBaseBlockedCards(baseBlocked, prms);

            float possibleWinnings = prms.getPossibleWinnings();
            const AllHandStrengths& handStrenghts = prms.gc.allHandStrengths();
            int heroHandStrength = handStrenghts.getRiverHandStrength(turn, river, prms.heroHoleCards.toInt());

            float EV = 0.0f;

            if (nOpponents == 0) // Everybody has folded except us -> go out
            {
                EV = possibleWinnings;

                if (DMShouldLog(&g_DMShowdownLogCount, DM_MAX_SHOWDOWN_LOGS))
                {
                    DMLogF(
                        "[DM][SHOWDOWN FOLDS] street=%s pot=%d possibleWinnings=%.2f EV=%.6f nodeChance=%.6f",
                        DMStreetName(prms.street),
                        DMDebugPotSize(prms),
                        possibleWinnings,
                        EV,
                        prms.nodeChance
                    );
                }
            }
            else if (nOpponents == 1)
            {
                int oppRangeLength = opponents[0]->range().length();
                const int* oppRangeHCInd = opponents[0]->range().hcIndex();
                const float* oppRangeLikelihood = opponents[0]->range().likelihood();

                float totalWinnings = 0.0f;
                float likelihoodSum = 0.0f;
                float heroShareWeighted = 0.0f;
                int cnt = 0;

                for (int i = 0; i < oppRangeLength; i++)
                {
                    float eq = oppRangeLikelihood[i];

                    if (!(eq > 0.0f) || !std::isfinite(eq))
                        continue;

                    int oppHandIndex = oppRangeHCInd[i];

                    if (DMHandConflictsWithBlockedCards(oppHandIndex, baseBlocked))
                        continue;

                    int oppHS = handStrenghts.getRiverHandStrength(turn, river, oppHandIndex);

                    float heroShare = 0.0f;

                    if (heroHandStrength > oppHS)
                    {
                        heroShare = 1.0f;
                    }
                    else if (heroHandStrength == oppHS)
                    {
                        heroShare = 0.5f;
                    }

                    heroShareWeighted += eq * heroShare;
                    likelihoodSum += eq;
                    totalWinnings += eq * possibleWinnings * heroShare;
                    cnt++;
                }

                if (likelihoodSum <= 0.0f)
                {
                    DMLogAlways("FATAL: likelihoodSum <= 0 em showdown HU.");
                    throw std::runtime_error("DecisionMaking: likelihoodSum <= 0 em showdown HU");
                }

                stats.showDown_NumValidRuns += cnt;
                EV = totalWinnings / likelihoodSum;

                if (DMShouldLog(&g_DMShowdownLogCount, DM_MAX_SHOWDOWN_LOGS))
                {
                    float normalizedEquity = heroShareWeighted / likelihoodSum;

                    DMLogF(
                        "[DM][SHOWDOWN HU] street=%s pot=%d possibleWinnings=%.2f oppRangeLen=%d activeCombos=%d likelihoodSum=%.6f heroShareWeighted=%.6f normalizedEquity=%.6f heroHS=%d EV=%.6f nodeChance=%.6f",
                        DMStreetName(prms.street),
                        DMDebugPotSize(prms),
                        possibleWinnings,
                        oppRangeLength,
                        cnt,
                        likelihoodSum,
                        heroShareWeighted,
                        normalizedEquity,
                        heroHandStrength,
                        EV,
                        prms.nodeChance
                    );
                }
            }
            else
            {
                DMRangeSampler samplers[MAX_PLAYERS];

                for (int i = 0; i < nOpponents; i++)
                {
                    DMBuildRangeSampler(samplers[i], *opponents[i], baseBlocked, "showdown multiway");
                }

                Pot pot(prms._players);
                ParkMillerCarta rng(1);

                float totalWinnings = 0.0f;
                float totalTerminalRake = 0.0f;
                int nIter = 0;
                int attempts = 0;

                for (attempts = 0; nIter < SHOWDOWN_ITERATIONS && attempts < SHOWDOWN_ITERATIONS * 300; attempts++)
                {
                    bool usedOpponentCards[52];

                    for (int c = 0; c < 52; c++)
                        usedOpponentCards[c] = false;

                    int opponentHoleCardsInd[MAX_PLAYERS];
                    bool valid = true;

                    for (int j = 0; j < nOpponents; j++)
                    {
                        int c0 = -1;
                        int c1 = -1;
                        int sampledHand = DMSampleHandIndex(samplers[j], rng);

                        if (!DMTryReserveOpponentHand(sampledHand, baseBlocked, usedOpponentCards, c0, c1))
                        {
                            valid = false;
                            break;
                        }

                        opponentHoleCardsInd[j] = sampledHand;
                    }

                    if (!valid)
                        continue;

                    for (int j = 0; j < nOpponents; j++)
                    {
                        int opponentHandStrength = handStrenghts.getRiverHandStrength(turn, river, opponentHoleCardsInd[j]);
                        pot.addHandStrength(opponentHandStrength, opponents[j]->moneyInPot());
                    }

                    pot.addHandStrength(heroHandStrength, prms.hero().moneyInPot());

                    float grossWinnings = pot.calculateWinnings(heroHandStrength, prms.hero().moneyInPot());
                    float adjustedWinnings = DMApplyTerminalRakeToHeroWinnings(prms, grossWinnings);
                    totalTerminalRake += grossWinnings - adjustedWinnings;
                    totalWinnings += adjustedWinnings;

                    pot.reset();
                    nIter++;
                }

                if (nIter <= 0)
                {
                    DMLogAlways("FATAL: nIter == 0 em showdown multiway. Nenhuma combinacao valida de cartas dos oponentes foi encontrada.");
                    throw std::runtime_error("DecisionMaking: nIter == 0 em showdown multiway");
                }

                stats.showDown_NumValidRuns += nIter;
                EV = totalWinnings / nIter;

                if (DMShouldLog(&g_DMShowdownLogCount, DM_MAX_SHOWDOWN_LOGS))
                {
                    float acceptance = (attempts > 0) ? (nIter / (float)attempts) : 0.0f;

                    DMLogF(
                        "[DM][SHOWDOWN MULTIWAY RAKED] street=%s pot=%d possibleWinnings=%.2f opponents=%d validRuns=%d targetRuns=%d attempts=%d acceptance=%.4f avgTerminalRake=%.6f EV=%.6f nodeChance=%.6f",
                        DMStreetName(prms.street),
                        DMDebugPotSize(prms),
                        possibleWinnings,
                        nOpponents,
                        nIter,
                        SHOWDOWN_ITERATIONS,
                        attempts,
                        acceptance,
                        nIter > 0 ? (totalTerminalRake / nIter) : 0.0f,
                        EV,
                        prms.nodeChance
                    );
                }
            }

            return EV;
        }

        inline void updateAhead(float& ahead, int heroStrength, int oppStrength)
        {
            if (heroStrength > oppStrength)
            {
                ahead += 1.0f;
            }
            else if (heroStrength == oppStrength)
            {
                ahead += 0.5f;
            }
        }

        int DMHandRankFromStrength(int handStrength)
        {
            int base1 = 15;
            int base2 = base1 * 15;
            int base3 = base2 * 15;
            int base4 = base3 * 15;
            int base5 = base4 * 15;

            if (handStrength <= 0)
                return (int)Rank_HighCard;

            int rank = handStrength / base5;

            if (rank < (int)Rank_HighCard)
                return (int)Rank_HighCard;

            if (rank >= (int)Rank_Length)
                return (int)Rank_StreightFlush;

            return rank;
        }

        float DMRiverValueFractionFromRank(int heroTurnStrength, int heroRiverStrength)
        {
            int turnRank = DMHandRankFromStrength(heroTurnStrength);
            int riverRank = DMHandRankFromStrength(heroRiverStrength);

            // O cutoff existe para estimar implied odds de equity que ainda nao
            // estava materializada no turn. Se o river nao melhorou a classe da mao,
            // nao adicionamos lucro extra para evitar transformar value ja existente
            // em bonus artificial.
            if (riverRank <= turnRank)
                return 0.0f;

            if (riverRank >= (int)Rank_FullHouse)
                return 0.50f;

            if (riverRank == (int)Rank_Flush || riverRank == (int)Rank_Straight)
                return 0.38f;

            if (riverRank == (int)Rank_Trips || riverRank == (int)Rank_Set)
                return 0.22f;

            if (riverRank == (int)Rank_TwoPair)
                return 0.12f;

            return 0.0f;
        }

        float DMOpponentRiverCallWeightWhenBeaten(int opponentRiverStrength)
        {
            int rank = DMHandRankFromStrength(opponentRiverStrength);

            if (rank >= (int)Rank_FullHouse)
                return 0.90f;

            if (rank == (int)Rank_Flush || rank == (int)Rank_Straight)
                return 0.70f;

            if (rank == (int)Rank_Trips || rank == (int)Rank_Set)
                return 0.48f;

            if (rank == (int)Rank_TwoPair)
                return 0.32f;

            if (rank == (int)Rank_OnePair)
                return 0.14f;

            return 0.04f;
        }

        float DMHeroRiverCallWeightWhenBehind(int heroRiverStrength)
        {
            int rank = DMHandRankFromStrength(heroRiverStrength);

            if (rank >= (int)Rank_FullHouse)
                return 0.82f;

            if (rank == (int)Rank_Flush || rank == (int)Rank_Straight)
                return 0.62f;

            if (rank == (int)Rank_Trips || rank == (int)Rank_Set)
                return 0.42f;

            if (rank == (int)Rank_TwoPair)
                return 0.28f;

            if (rank == (int)Rank_OnePair)
                return 0.13f;

            return 0.03f;
        }

        float DMOpponentRiverValueBetWeightWhenAhead(int opponentRiverStrength)
        {
            int rank = DMHandRankFromStrength(opponentRiverStrength);

            if (rank >= (int)Rank_FullHouse)
                return 0.92f;

            if (rank == (int)Rank_Flush || rank == (int)Rank_Straight)
                return 0.78f;

            if (rank == (int)Rank_Trips || rank == (int)Rank_Set)
                return 0.54f;

            if (rank == (int)Rank_TwoPair)
                return 0.36f;

            if (rank == (int)Rank_OnePair)
                return 0.16f;

            return 0.04f;
        }

        float DMCalculateBoundedRiverImpliedBonusForTurnCutoff(
            const GameState& prms,
            const Pot& pot,
            int potIndex,
            int nOpponents,
            const Player** opponents,
            const int* opponentTurnStrength,
            const int* opponentRiverStrength,
            float heroShare,
            int heroTurnStrength,
            int heroRiverStrength)
        {
            if (nOpponents < 2)
                return 0.0f;

            if (heroShare < 0.999f)
                return 0.0f;

            float valueFraction = DMRiverValueFractionFromRank(heroTurnStrength, heroRiverStrength);

            if (valueFraction <= 0.0f)
                return 0.0f;

            int maxOppTurnStrength = 0;
            int eligibleOpponents = 0;
            int totalStackBehindCap = 0;
            float expectedCalls = 0.0f;

            for (int j = 0; j < nOpponents; j++)
            {
                if (opponents[j]->moneyInPot() < pot.getHeight(potIndex))
                    continue;

                if (opponentTurnStrength[j] > maxOppTurnStrength)
                    maxOppTurnStrength = opponentTurnStrength[j];

                int stackBehind = opponents[j]->stack();

                if (stackBehind <= 0)
                    continue;

                eligibleOpponents++;
                totalStackBehindCap += stackBehind;

                // Quanto mais forte for a mao derrotada no river, maior a chance
                // abstrata de pagar uma value bet. Isso substitui a antiga ideia de
                // bonus fixo de 50% do pote por uma estimativa condicionada ao runout
                // e a mao amostrada do oponente.
                expectedCalls += DMOpponentRiverCallWeightWhenBeaten(opponentRiverStrength[j]);
            }

            if (eligibleOpponents <= 0 || totalStackBehindCap <= 0)
                return 0.0f;

            bool heroWasAlreadyBestOnTurn = (maxOppTurnStrength == 0 || heroTurnStrength > maxOppTurnStrength);

            if (heroWasAlreadyBestOnTurn)
                return 0.0f;

            float sidePotMoney = pot.getMoney(potIndex);
            float abstractBet = sidePotMoney * valueFraction;
            float heroStackCap = (float)prms.hero().stack();
            float stackCap = (std::min)(heroStackCap, (float)totalStackBehindCap);

            if (eligibleOpponents >= 2)
                expectedCalls = (std::min)(expectedCalls, 1.35f);
            else
                expectedCalls = (std::min)(expectedCalls, 1.0f);

            float bonus = abstractBet * expectedCalls;
            bonus = (std::min)(bonus, stackCap);

            // Mesmo em multiway, este cutoff deve ser conservador: ele apenas corrige
            // a ausencia completa de uma rodada de apostas no river, sem criar EV
            // ilimitado nem transformar todo river vencedor em pagamento automatico.
            bonus = (std::min)(bonus, sidePotMoney * 0.75f);

            if (!std::isfinite(bonus) || bonus < 0.0f)
                return 0.0f;

            return bonus;
        }

        float DMCalculateBoundedRiverReverseImpliedPenaltyForTurnCutoff(
            const GameState& prms,
            const Pot& pot,
            int potIndex,
            int nOpponents,
            const Player** opponents,
            const int* opponentTurnStrength,
            const int* opponentRiverStrength,
            float heroShare,
            int heroTurnStrength,
            int heroRiverStrength)
        {
            if (nOpponents < 2)
                return 0.0f;

            // Reverse implied odds apply only when the hero loses cleanly on the
            // river. Ties keep the normal pot-share model.
            if (heroShare > 0.001f)
                return 0.0f;

            int maxOppTurnStrength = 0;
            int bestOppRiverStrength = 0;
            int bettorStackCap = 0;
            float bestValueFraction = 0.0f;
            float bestBetWeight = 0.0f;

            for (int j = 0; j < nOpponents; j++)
            {
                if (opponents[j]->moneyInPot() < pot.getHeight(potIndex))
                    continue;

                if (opponentTurnStrength[j] > maxOppTurnStrength)
                    maxOppTurnStrength = opponentTurnStrength[j];

                if (opponentRiverStrength[j] <= heroRiverStrength)
                    continue;

                int stackBehind = opponents[j]->stack();

                if (stackBehind <= 0)
                    continue;

                float valueFraction = DMRiverValueFractionFromRank(
                    opponentTurnStrength[j],
                    opponentRiverStrength[j]);

                // This penalty is not a generic cooler tax. It is the mirror image
                // of the positive implied-odds bonus: it only fires when an
                // opponent's equity was not materialized on the turn and becomes a
                // made value hand on the river.
                if (valueFraction <= 0.0f)
                    continue;

                float betWeight = DMOpponentRiverValueBetWeightWhenAhead(opponentRiverStrength[j]);

                if (opponentRiverStrength[j] > bestOppRiverStrength)
                    bestOppRiverStrength = opponentRiverStrength[j];

                if (stackBehind > bettorStackCap)
                    bettorStackCap = stackBehind;

                if (valueFraction > bestValueFraction)
                    bestValueFraction = valueFraction;

                if (betWeight > bestBetWeight)
                    bestBetWeight = betWeight;
            }

            if (bestOppRiverStrength <= 0 || bettorStackCap <= 0 || bestValueFraction <= 0.0f || bestBetWeight <= 0.0f)
                return 0.0f;

            bool heroWasAlreadyBestOnTurn = (maxOppTurnStrength == 0 || heroTurnStrength > maxOppTurnStrength);

            if (!heroWasAlreadyBestOnTurn)
                return 0.0f;

            float heroCallWeight = DMHeroRiverCallWeightWhenBehind(heroRiverStrength);

            if (heroCallWeight <= 0.0f)
                return 0.0f;

            float sidePotMoney = pot.getMoney(potIndex);
            float abstractBet = sidePotMoney * bestValueFraction;
            float penalty = abstractBet * bestBetWeight * heroCallWeight;
            float stackCap = (std::min)((float)prms.hero().stack(), (float)bettorStackCap);

            penalty = (std::min)(penalty, stackCap);
            penalty = (std::min)(penalty, sidePotMoney * 0.75f);

            if (!std::isfinite(penalty) || penalty < 0.0f)
                return 0.0f;

            return penalty;
        }

        float estimateEV_TurnCutoff(DMStats& stats, const GameState& prms)
        {
            int nOpponents = 0;
            const Player* opponents[MAX_PLAYERS];
            prms.getOpponents(opponents, nOpponents);

            stats.numTurnCutOffs++;
            stats.turnCutOff_OppHist[nOpponents]++;

            int turn = prms.board.card[3].toInt();

            bool baseBlocked[52];
            DMBuildBaseBlockedCards(baseBlocked, prms);

            const AllHandStrengths& handStrenghts = prms.gc.allHandStrengths();
            const int* heroRiverStrengths = handStrenghts.getRiverHandStrengths(turn, prms.heroHoleCards.toInt());

            float possibleWinnings = prms.getPossibleWinnings();
            float EV = 0.0f;

            if (nOpponents == 0) // Everybody has folded except us -> go out
            {
                EV = possibleWinnings;
            }
            else if (nOpponents == 1)
            {
                int oppRangeLength = opponents[0]->range().length();
                const int* oppRangeHCInd = opponents[0]->range().hcIndex();
                const float* oppRangeLikelihood = opponents[0]->range().likelihood();

                float totalWinnings = 0.0f;
                float likelihoodSum = 0.0f;

                int nRiversChecked = 0;
                int nIter = 0;

                for (int i = 0; i < oppRangeLength; i++)
                {
                    float w = oppRangeLikelihood[i];

                    if (!(w > 0.0f) || !std::isfinite(w))
                        continue;

                    int oppHandIndex = oppRangeHCInd[i];

                    if (DMHandConflictsWithBlockedCards(oppHandIndex, baseBlocked))
                        continue;

                    HoleCards oppHoleCards(oppHandIndex);
                    bool blockedForRun[52];

                    for (int c = 0; c < 52; c++)
                        blockedForRun[c] = baseBlocked[c];

                    blockedForRun[oppHoleCards.Card0.toInt()] = true;
                    blockedForRun[oppHoleCards.Card1.toInt()] = true;

                    const int* oppRiverStrengths = handStrenghts.getRiverHandStrengths(turn, oppHandIndex);

                    float heroShareSum = 0.0f;
                    int validRivers = 0;

                    for (int river = 0; river < 52; river++)
                    {
                        if (blockedForRun[river])
                            continue;

                        int heroStrength = heroRiverStrengths[river];
                        int oppStrength = oppRiverStrengths[river];

                        if (heroStrength > oppStrength)
                        {
                            heroShareSum += 1.0f;
                        }
                        else if (heroStrength == oppStrength)
                        {
                            heroShareSum += 0.5f;
                        }

                        validRivers++;
                        nRiversChecked++;
                    }

                    if (validRivers <= 0)
                        continue;

                    float equity = heroShareSum / validRivers;
                    totalWinnings += w * possibleWinnings * equity;
                    likelihoodSum += w;
                    nIter++;
                }

                if (likelihoodSum <= 0.0f)
                {
                    DMLogAlways("FATAL: likelihoodSum <= 0 em turn cutoff HU.");
                    throw std::runtime_error("DecisionMaking: likelihoodSum <= 0 em turn cutoff HU");
                }

                stats.turnCutOff_NumPots += 1;
                stats.turnCutOff_NumRiversChecked += (nIter > 0) ? (nRiversChecked / (float)nIter) : 0.0f;
                EV = totalWinnings / likelihoodSum;

                if (DMShouldLog(&g_DMTurnCutoffLogCount, DM_MAX_TURNCUTOFF_LOGS))
                {
                    float avgRiversChecked = (nIter > 0) ? (nRiversChecked / (float)nIter) : 0.0f;
                    float normalizedTurnEquity = (possibleWinnings > 0.0f) ? (EV / possibleWinnings) : 0.0f;

                    DMLogF(
                        "[DM][TURN CUTOFF HU EXACT] pot=%d possibleWinnings=%.2f oppRangeLen=%d activeCombos=%d avgRiversChecked=%.2f normalizedTurnEquity=%.6f EV=%.6f nodeChance=%.6f",
                        DMDebugPotSize(prms),
                        possibleWinnings,
                        oppRangeLength,
                        nIter,
                        avgRiversChecked,
                        normalizedTurnEquity,
                        EV,
                        prms.nodeChance
                    );
                }
            }
            else // nOpponents >= 2
            {
                DMRangeSampler samplers[MAX_PLAYERS];

                for (int i = 0; i < nOpponents; i++)
                {
                    DMBuildRangeSampler(samplers[i], *opponents[i], baseBlocked, "turn cutoff multiway");
                }

                Pot pot(prms._players);
                ParkMillerCarta rng(1);

                float totalWinnings = 0.0f;
                float totalImpliedBonus = 0.0f;
                float totalReversePenalty = 0.0f;
                float totalTerminalRake = 0.0f;
                int impliedBonusEvents = 0;
                int reversePenaltyEvents = 0;
                int nRiversChecked = 0;
                int nIter = 0;
                int attempts = 0;

                for (attempts = 0; nIter < TCUTOFF_ITERATIONS && attempts < TCUTOFF_ITERATIONS * 300; attempts++)
                {
                    bool usedOpponentCards[52];

                    for (int c = 0; c < 52; c++)
                        usedOpponentCards[c] = false;

                    int opponentHoleCardsInd[MAX_PLAYERS];
                    int opponentTurnStrength[MAX_PLAYERS];
                    const int* oppRiverStrengths[MAX_PLAYERS];
                    bool iterationValid = true;

                    for (int j = 0; j < nOpponents; j++)
                    {
                        int c0 = -1;
                        int c1 = -1;
                        int sampledHand = DMSampleHandIndex(samplers[j], rng);

                        if (!DMTryReserveOpponentHand(sampledHand, baseBlocked, usedOpponentCards, c0, c1))
                        {
                            iterationValid = false;
                            break;
                        }

                        opponentHoleCardsInd[j] = sampledHand;
                        opponentTurnStrength[j] = handStrenghts.getTurnHandStrength(turn, sampledHand);
                        oppRiverStrengths[j] = handStrenghts.getRiverHandStrengths(turn, sampledHand);
                    }

                    if (!iterationValid)
                        continue;

                    int validRivers = 0;
                    float iterationWinnings = 0.0f;
                    int heroTurnStrength = handStrenghts.getTurnHandStrength(turn, prms.heroHoleCards.toInt());

                    for (int river = 0; river < 52; river++)
                    {
                        if (baseBlocked[river] || usedOpponentCards[river])
                            continue;

                        int heroStrength = heroRiverStrengths[river];
                        float riverGrossWinnings = 0.0f;
                        float riverReversePenalty = 0.0f;

                        for (int ip = 0; ip < pot.numPots(); ip++)
                        {
                            if (prms.hero().moneyInPot() < pot.getHeight(ip))
                                break;

                            int maxOppStrength = 0;
                            int tiedOpponents = 0;

                            for (int j = 0; j < nOpponents; j++)
                            {
                                if (opponents[j]->moneyInPot() < pot.getHeight(ip))
                                    continue;

                                int oppStrength = oppRiverStrengths[j][river];

                                if (oppStrength > maxOppStrength)
                                {
                                    maxOppStrength = oppStrength;
                                    tiedOpponents = 1;
                                }
                                else if (oppStrength == maxOppStrength)
                                {
                                    tiedOpponents++;
                                }
                            }

                            float heroShare = 0.0f;

                            if (maxOppStrength == 0 || heroStrength > maxOppStrength)
                            {
                                heroShare = 1.0f;
                            }
                            else if (heroStrength == maxOppStrength)
                            {
                                heroShare = 1.0f / (tiedOpponents + 1);
                            }

                            int opponentRiverStrength[MAX_PLAYERS];

                            for (int j = 0; j < nOpponents; j++)
                                opponentRiverStrength[j] = oppRiverStrengths[j][river];

                            float impliedOddsBonus = DMCalculateBoundedRiverImpliedBonusForTurnCutoff(
                                prms,
                                pot,
                                ip,
                                nOpponents,
                                opponents,
                                opponentTurnStrength,
                                opponentRiverStrength,
                                heroShare,
                                heroTurnStrength,
                                heroStrength);

                            float reverseImpliedPenalty = DMCalculateBoundedRiverReverseImpliedPenaltyForTurnCutoff(
                                prms,
                                pot,
                                ip,
                                nOpponents,
                                opponents,
                                opponentTurnStrength,
                                opponentRiverStrength,
                                heroShare,
                                heroTurnStrength,
                                heroStrength);

                            if (impliedOddsBonus > 0.0f)
                            {
                                impliedBonusEvents++;
                                totalImpliedBonus += impliedOddsBonus;
                            }

                            if (reverseImpliedPenalty > 0.0f)
                            {
                                reversePenaltyEvents++;
                                totalReversePenalty += reverseImpliedPenalty;
                            }

                            riverGrossWinnings += (pot.getMoney(ip) * heroShare) + impliedOddsBonus;
                            riverReversePenalty += reverseImpliedPenalty;
                        }

                        float riverAdjustedWinnings = DMApplyTerminalRakeToHeroWinnings(prms, riverGrossWinnings);
                        totalTerminalRake += riverGrossWinnings - riverAdjustedWinnings;
                        iterationWinnings += riverAdjustedWinnings - riverReversePenalty;

                        validRivers++;
                    }

                    if (validRivers <= 0)
                        continue;

                    totalWinnings += iterationWinnings / validRivers;
                    nRiversChecked += validRivers;
                    nIter++;
                }

                if (nIter <= 0)
                {
                    DMLogAlways("FATAL: nIter == 0 em turn cutoff multiway. Nenhuma combinacao valida de cartas dos oponentes foi encontrada.");
                    throw std::runtime_error("DecisionMaking: nIter == 0 em turn cutoff multiway");
                }

                stats.turnCutOff_NumRiversChecked += nRiversChecked / (float)nIter;
                stats.turnCutOff_NumPots += pot.numPots();

                EV = totalWinnings / nIter;

                if (DMShouldLog(&g_DMTurnCutoffLogCount, DM_MAX_TURNCUTOFF_LOGS))
                {
                    float avgRiversChecked = (nIter > 0) ? (nRiversChecked / (float)nIter) : 0.0f;
                    float acceptance = (attempts > 0) ? (nIter / (float)attempts) : 0.0f;

                    DMLogF(
                        "[DM][TURN CUTOFF MULTIWAY IMPLIED-REVERSE-RAKED] pot=%d opponents=%d validRuns=%d targetRuns=%d attempts=%d acceptance=%.4f avgRiversChecked=%.2f pots=%d impliedEvents=%d avgImpliedBonus=%.6f reverseEvents=%d avgReversePenalty=%.6f avgTerminalRake=%.6f EV=%.6f nodeChance=%.6f",
                        DMDebugPotSize(prms),
                        nOpponents,
                        nIter,
                        TCUTOFF_ITERATIONS,
                        attempts,
                        acceptance,
                        avgRiversChecked,
                        pot.numPots(),
                        impliedBonusEvents,
                        impliedBonusEvents > 0 ? (totalImpliedBonus / impliedBonusEvents) : 0.0f,
                        reversePenaltyEvents,
                        reversePenaltyEvents > 0 ? (totalReversePenalty / reversePenaltyEvents) : 0.0f,
                        nIter > 0 ? (totalTerminalRake / nIter) : 0.0f,
                        EV,
                        prms.nodeChance
                    );
                }
            }

            return EV;
        }

        float estimateEV_PreFlopCutoff(DMStats& stats, const GameState& prms)
        {
            int nOpponents = 0;
            const Player* opponents[MAX_PLAYERS];
            prms.getOpponents(opponents, nOpponents);

            float EV = 0.0f;

            if (nOpponents == 0)
            {
                EV = prms.getPossibleWinnings();
            }
            else if (nOpponents == 1)
            {
                float equity = PreFlopEquity::calculate(prms.heroHoleCards, opponents[0]->range());
                EV = equity * prms.getPossibleWinnings();
            }
            else // (nOpponents >= 2)
            {
                // The original G5 fallback used min(pairwise_equity) * fixed_mod.
                // That overvalues hands whose HU equity does not survive multiway
                // collision well, especially low pairs and dominated connectors.
                // This fallback is still cheap, but models the event "hero beats every
                // remaining opponent" as the product of pairwise win probabilities.
                float multiwayEquityProxy = 1.0f;

                for (int i = 0; i < nOpponents; i++)
                {
                    float equity = PreFlopEquity::calculate(prms.heroHoleCards, opponents[i]->range());

                    if (!std::isfinite(equity) || equity < 0.0f)
                        equity = 0.0f;

                    if (equity > 1.0f)
                        equity = 1.0f;

                    multiwayEquityProxy *= equity;
                }

                EV = multiwayEquityProxy * prms.getPossibleWinnings();
            }

            // If there are opponents and we have some money left...
            if (nOpponents > 0 && prms.hero().stack() > 0)
            {
                if (prms.isPlayerInPosition(prms.heroInd))
                {
                    // In position
                    EV *= 1.0f;
                }
                else if (prms.isHeroFirstToAct_postFlop())
                {
                    // First to act
                    EV *= 0.85f;
                }
                else
                {
                    // Some other position
                    EV *= 0.90f;
                }
            }

            DMStats::LeafLogNode lln;

            lln.chance = prms.nodeChance;
            lln.ev = EV;
            lln.nOpponents = nOpponents;
            lln.street = Street_PreFlop;
            lln.heroMoneyInThePot = prms.hero().moneyInPot();
            lln.possibleWinnings = prms.getPossibleWinnings();
            lln.numRaises = prms.numBets;

            stats.leafLog.push_back(lln);
            return EV;
        }

        void DMLogCalculationSummary(const GameState& prms, const DMStats& stats, float checkCallEV, float betRaiseEV)
        {
            if (!DMIsDiagnosticEnabled())
                return;

            DMLogF(
                "[DM][ROOT EV FINAL] street=%s(%d) checkCallEV=%.6f betRaiseEV=%.6f chosenByEV=%s amountToCall=%d possibleWinnings=%.2f showdowns=%d showDownValidRuns=%d turnCutoffs=%d avgTurnRivers=%.2f avgTurnPots=%.2f",
                DMStreetName(prms.street),
                static_cast<int>(prms.street),
                checkCallEV,
                betRaiseEV,
                (checkCallEV >= betRaiseEV) ? "check/call" : "bet/raise",
                prms.getAmountToCall(),
                prms.getPossibleWinnings(),
                stats.numShowDowns,
                stats.showDown_NumValidRuns,
                stats.numTurnCutOffs,
                (stats.numTurnCutOffs > 0) ? (stats.turnCutOff_NumRiversChecked / stats.numTurnCutOffs) : 0.0f,
                (stats.numTurnCutOffs > 0) ? (stats.turnCutOff_NumPots / stats.numTurnCutOffs) : 0.0f
            );
        }

        /// <summary>
        /// Calculates post flop EV
        /// </summary>
        float estimateEV(DMStats& stats, const GameState& prms)
        {
            if (prms.nodeChance <= NODE_CHANCE_CUTOFF)
            {
                return 0.0f;
            }

            if (prms.playerToActInd == -1) // Go to next street
            {
                if (prms.street == Street_PreFlop)
                {
                    return estimateEV_PreFlopCutoff(stats, prms);
                }
                else if (prms.street == Street_River) // ShowDown
                {
                    return estimateEV_ShowDown(stats, prms);
                }
                else // Go to next street
                {
                    // If the current street is turn and estimation started on flop, cut it.
                    // But, if we have just two players, don't stop on turn. Go all the way down.
                    if (prms.street == Street_Turn && prms.stertedOnFlop && prms.startNumActive > 2)
                    {
                        return estimateEV_TurnCutoff(stats, prms);
                    }
                    else
                    {
                        return estimateEV_NextStreet_mt(stats, prms);
                    }
                }
            }
            else if (prms.playerToActInd == prms.heroInd) // Mi igramo
            {
                float foldEV = 0.0f;
                float checkCallEV = 0.0f;
                float betRaiseEV = 0.0f;
                estimateEV_HeroPlays(checkCallEV, betRaiseEV, stats, prms);

                float EV = (std::max)(checkCallEV, (std::max)(foldEV, betRaiseEV));
                return EV;
            }
            else // (playerToAct != hero)
            {
                return estimateEV_VillainPlays(stats, prms);
            }
        }
    } // namespace

    static void EstimateEV_Internal(float& checkCallEV, float& betRaiseEV, int buttonInd, int heroIndex, const HoleCards& heroHoleCards,
        const PlayerDTO* players, int nPlayers, const Card* cardsInBoard, Street street, int numBets, int numCallers, int bigBlindSize, const void* gc, int forcedRootBetRaiseAmount)
    {
DMLog("===== ENTER EstimateEV =====");
DMLogF("[DM][BUILD] %s | SHOWDOWN_ITERATIONS=%d | TCUTOFF_ITERATIONS=%d | NODE_CHANCE_CUTOFF=%.6f",
    DM_BUILD_ID, SHOWDOWN_ITERATIONS, TCUTOFF_ITERATIONS, NODE_CHANCE_CUTOFF);
DMResetDiagnosticCounters();

        DMLogFmt(
            "EstimateEV parametros recebidos",
            gc,
            players,
            cardsInBoard,
            buttonInd,
            heroIndex,
            nPlayers,
            static_cast<int>(street),
            numBets,
            numCallers,
            bigBlindSize
        );

        // Inicializa os EVs para valores reconhecÃƒÂ¯Ã‚Â¿Ã‚Â½veis.
        // Estes valores NÃƒÂ¯Ã‚Â¿Ã‚Â½O devem ser usados como decisÃƒÂ¯Ã‚Â¿Ã‚Â½o, servem apenas para diagnÃƒÂ¯Ã‚Â¿Ã‚Â½stico
        // caso a chamada seja interrompida antes de calcular os EVs reais.
        checkCallEV = -999999.0f;
        betRaiseEV = -999999.0f;

        try
        {
            if (gc == nullptr)
            {
                throw std::runtime_error("EstimateEV: gc == nullptr");
            }

            if (players == nullptr)
            {
                throw std::runtime_error("EstimateEV: players == nullptr");
            }

            if (nPlayers < 2 || nPlayers > MAX_PLAYERS)
            {
                throw std::runtime_error("EstimateEV: nPlayers invalido");
            }

            if (heroIndex < 0 || heroIndex >= nPlayers)
            {
                throw std::runtime_error("EstimateEV: heroIndex invalido");
            }

            if (buttonInd < 0 || buttonInd >= nPlayers)
            {
                throw std::runtime_error("EstimateEV: buttonInd invalido");
            }

            if (bigBlindSize <= 0)
            {
                throw std::runtime_error("EstimateEV: bigBlindSize invalido");
            }

            if (street < Street_PreFlop || street > Street_River)
            {
                throw std::runtime_error("EstimateEV: street invalida");
            }

            if (street > Street_PreFlop && cardsInBoard == nullptr)
            {
                throw std::runtime_error("EstimateEV: cardsInBoard == nullptr em street pos-flop");
            }

            DMLog("EstimateEV validacoes basicas OK");

            DMLog("Antes de converter gc para GameContext");
            const GameContext* gcPtr = static_cast<const GameContext*>(gc);
            DMLog("Depois de converter gc para GameContext");

            DMLog("Antes de criar Board");
            Board board(cardsInBoard, street);
            DMLog("Depois de criar Board");

            DMLog("Antes de gcPtr->assertBoard");
            gcPtr->assertBoard(board);
            DMLog("Depois de gcPtr->assertBoard");

            DMLog("Antes de ler players->_model.totalPlayers");
            int totalPlayersFromModel = players->_model.totalPlayers;
            {
                char buf[256];
                sprintf_s(buf, sizeof(buf), "players->_model.totalPlayers=%d", totalPlayersFromModel);
                DMLog(buf);
            }

            if (totalPlayersFromModel < 2 || totalPlayersFromModel > MAX_PLAYERS)
            {
                throw std::runtime_error("EstimateEV: players->_model.totalPlayers invalido");
            }

            TableType tt = TableType(totalPlayersFromModel);
            DMLog("Depois de criar TableType");

            DMStats stats;

            DMLog("Antes de criar GameState");
            GameState prms(tt, buttonInd, heroIndex, heroHoleCards, players, nPlayers, board, street, numBets, numCallers, bigBlindSize, *gcPtr);
            DMLog("Depois de criar GameState");

            {
                char buf[512];
                sprintf_s(
                    buf,
                    sizeof(buf),
                    "GameState OK: startNumActive=%d, playerToActInd=%d, heroInd=%d, numBets=%d",
                    prms.startNumActive,
                    prms.playerToActInd,
                    prms.heroInd,
                    prms.numBets
                );
                DMLog(buf);
            }

            switch (street)
            {
            case Street_River:
                DMLog("Street_River: configurando BETS_CUTOFF_POST_FLOP=3");
                prms.BETS_CUTOFF_POST_FLOP = 3;
                break;

            case Street_Turn:
                DMLog("Street_Turn: configurando BETS_CUTOFF_POST_FLOP");
                prms.BETS_CUTOFF_POST_FLOP = (prms.startNumActive >= 4) ? 2 : 3;
                break;

            case Street_Flop:
                DMLog("Street_Flop: configurando BETS_CUTOFF_POST_FLOP");
                // If there are 2 active players we will calculate to showdown, thats why go only 2 raises deep...
                prms.BETS_CUTOFF_POST_FLOP = (prms.startNumActive == 2 || prms.startNumActive >= 4) ? 2 : 3;
                break;

            case Street_PreFlop:
                DMLog("Street_PreFlop: sem ajuste post-flop de BETS_CUTOFF");
                break;

            default:
                throw std::runtime_error("EstimateEV: street caiu no default inesperado");
            }

            if (street > Street_PreFlop)
                DMLogRootContext("ANTES", prms);

            DMLog("Antes de estimateEV_HeroPlays");
            estimateEV_HeroPlays(checkCallEV, betRaiseEV, stats, prms, forcedRootBetRaiseAmount);
            DMLog("Depois de estimateEV_HeroPlays");

            if (street > Street_PreFlop)
                DMLogCalculationSummary(prms, stats, checkCallEV, betRaiseEV);

            {
                char buf[256];
                sprintf_s(buf, sizeof(buf), "EstimateEV final OK: checkCallEV=%.6f betRaiseEV=%.6f", checkCallEV, betRaiseEV);
                DMLog(buf);
            }

            DMLog("===== EXIT EstimateEV OK =====");

            // Save stats of the calculation
            //stats.SaveToFile("c:\\temp\\dmstats.txt");
        }
        catch (const std::exception& ex)
        {
            DMLogAlways("FATAL: std::exception capturada dentro de EstimateEV");
            DMLogAlways(ex.what());

            DMLogFmtAlways(
                "EstimateEV parametros no momento da falha",
                gc,
                players,
                cardsInBoard,
                buttonInd,
                heroIndex,
                nPlayers,
                static_cast<int>(street),
                numBets,
                numCallers,
                bigBlindSize
            );

            DMFatal("FATAL: interrompendo EstimateEV apos std::exception");
        }
        catch (...)
        {
            DMLogAlways("FATAL: excecao C++ desconhecida capturada dentro de EstimateEV");

            DMLogFmtAlways(
                "EstimateEV parametros no momento da falha",
                gc,
                players,
                cardsInBoard,
                buttonInd,
                heroIndex,
                nPlayers,
                static_cast<int>(street),
                numBets,
                numCallers,
                bigBlindSize
            );

            DMFatal("FATAL: interrompendo EstimateEV apos excecao desconhecida");
        }
    }

    extern "C" G5_EXPORT void __stdcall EstimateEV(float& checkCallEV, float& betRaiseEV, int buttonInd, int heroIndex, const HoleCards& heroHoleCards,
        const PlayerDTO* players, int nPlayers, const Card* cardsInBoard, Street street, int numBets, int numCallers, int bigBlindSize, const void* gc)
    {
        EstimateEV_Internal(checkCallEV, betRaiseEV, buttonInd, heroIndex, heroHoleCards,
            players, nPlayers, cardsInBoard, street, numBets, numCallers, bigBlindSize, gc, 0);
    }

    extern "C" G5_EXPORT void __stdcall EstimateEVForBetRaiseAmount(float& checkCallEV, float& betRaiseEV, int forcedBetRaiseAmount, int buttonInd, int heroIndex, const HoleCards& heroHoleCards,
        const PlayerDTO* players, int nPlayers, const Card* cardsInBoard, Street street, int numBets, int numCallers, int bigBlindSize, const void* gc)
    {
        if (forcedBetRaiseAmount < 0)
            forcedBetRaiseAmount = 0;

        DMLogF("[DM][MULTISIZE ROOT] forcedBetRaiseAmount=%d", forcedBetRaiseAmount);

        EstimateEV_Internal(checkCallEV, betRaiseEV, buttonInd, heroIndex, heroHoleCards,
            players, nPlayers, cardsInBoard, street, numBets, numCallers, bigBlindSize, gc, forcedBetRaiseAmount);
    }


}






