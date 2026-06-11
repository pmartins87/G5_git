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
        const int SHOWDOWN_ITERATIONS = 10000;

        const int TCUTOFF_BIN_COUNT = 13260;
        const int TCUTOFF_ITERATIONS = 10000;

        const float NODE_CHANCE_CUTOFF = 0.0f; // 0.01f;

        // Phase 6A: optional root-only forced bet/raise amount.
        // Used only by EstimateEVForBetRaiseAmount to evaluate candidate sizings.
        // The flag is consumed at the root hero node before evaluating the check/call branch,
        // so recursive future hero decisions keep their normal default sizing.
        static bool g_DMForceRootBetRaiseAmount = false;
        static int g_DMForcedRootBetRaiseAmount = 0;

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

            if (prms.street == Street_PreFlop)
                return pot + 2 * amountToCall;

            return (2 * (pot + amountToCall)) / 3 + amountToCall;
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
        void estimateEV_HeroPlays(float& checkCallEV, float& betRaiseEV, DMStats& stats, const GameState& prms)
        {
            assert(prms.heroInd == prms.playerToActInd);

            bool logHeroNode = (prms.street > Street_PreFlop) && DMShouldLog(&g_DMHeroNodeLogCount, DM_MAX_HERO_NODE_LOGS);
            int callCost = prms.getAmountToCall();
            bool canRaise = prms.canNextPlayerRaise();
            int raiseAmount = canRaise ? DMDebugRaiseAmount(prms, callCost) : 0;
            int forcedRootBetRaiseAmount = 0;

            if (g_DMForceRootBetRaiseAmount)
            {
                forcedRootBetRaiseAmount = g_DMForcedRootBetRaiseAmount;
                g_DMForceRootBetRaiseAmount = false;
                g_DMForcedRootBetRaiseAmount = 0;
            }

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

                    if (eq == 0.0f)
                        continue;

                    //HoleCards oppHC(range.ind[i]);
                    int oppHS = handStrenghts.getRiverHandStrength(turn, river, oppRangeHCInd[i]);

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

                stats.showDown_NumValidRuns += cnt;
                EV = totalWinnings;

                if (DMShouldLog(&g_DMShowdownLogCount, DM_MAX_SHOWDOWN_LOGS))
                {
                    float normalizedEquity = (likelihoodSum > 0.0f) ? (heroShareWeighted / likelihoodSum) : 0.0f;

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
                int opponentHandIndexes[MAX_PLAYERS * SHOWDOWN_BIN_COUNT];

                for (int i = 0; i < nOpponents; i++)
                {
                    opponents[i]->range_FillHandIndices(&opponentHandIndexes[i * SHOWDOWN_BIN_COUNT], SHOWDOWN_BIN_COUNT);
                }

                HoleCards opponentHoleCards[MAX_PLAYERS];
                Pot pot(prms._players);
                ParkMillerCarta rng(1);

                float totalWinnings = 0.0f;
                int nIter = 0;
                bool isOppCard[52];

                for (int j = 0; j < 52; j++)
                    isOppCard[j] = false;

                for (int attempts = 0; nIter < SHOWDOWN_ITERATIONS && attempts < SHOWDOWN_ITERATIONS * 200; attempts++)
                {
                    // Choose opponent hole cards randomly
                    for (int j = 0; j < nOpponents; j++)
                    {
                        int index = rng.next() % SHOWDOWN_BIN_COUNT;
                        opponentHoleCards[j] = HoleCards(opponentHandIndexes[j * SHOWDOWN_BIN_COUNT + index]);
                    }

                    bool valid = true;

                    // Ban choosen hole cards and check if combinationis is valid
                    for (int j = 0; j < nOpponents; j++)
                    {
                        int ind0 = opponentHoleCards[j].Card0.toInt();
                        int ind1 = opponentHoleCards[j].Card1.toInt();

                        int hero0 = prms.heroHoleCards.Card0.toInt();
                        int hero1 = prms.heroHoleCards.Card1.toInt();

                        if (ind0 == hero0 || ind0 == hero1 || ind0 == turn || ind0 == river ||
                            ind1 == hero0 || ind1 == hero1 || ind1 == turn || ind1 == river ||
                            isOppCard[ind0] || isOppCard[ind1] ||
                            ind0 == ind1)
                        {
                            valid = false;
                        }

                        isOppCard[ind0] = true;
                        isOppCard[ind1] = true;
                    }

                    // If combination is valid calculate EV
                    if (valid)
                    {
                        for (int j = 0; j < nOpponents; j++)
                        {
                            int opponentHandStrength = handStrenghts.getRiverHandStrength(turn, river, opponentHoleCards[j].toInt());
                            pot.addHandStrength(opponentHandStrength, opponents[j]->moneyInPot());
                        }

                        pot.addHandStrength(heroHandStrength, prms.hero().moneyInPot());
                        totalWinnings += pot.calculateWinnings(heroHandStrength, prms.hero().moneyInPot());
                        nIter++;
                    }

                    // Un-ban chosen hole cards
                    for (int j = 0; j < nOpponents; j++)
                    {
                        int ind0 = opponentHoleCards[j].Card0.toInt();
                        int ind1 = opponentHoleCards[j].Card1.toInt();

                        isOppCard[ind0] = false;
                        isOppCard[ind1] = false;
                    }

                    // Reset the pot
                    pot.reset();
                }

                if (nIter <= 0)
                {
                    DMLogAlways("FATAL: nIter == 0 em amostragem multiway. Nenhuma combinacao valida de cartas dos oponentes foi encontrada.");
                    throw std::runtime_error("DecisionMaking: nIter == 0 em amostragem multiway");
                }

                stats.showDown_NumValidRuns += nIter;
                EV = totalWinnings / nIter;

                if (DMShouldLog(&g_DMShowdownLogCount, DM_MAX_SHOWDOWN_LOGS))
                {
                    DMLogF(
                        "[DM][SHOWDOWN MULTIWAY] street=%s pot=%d possibleWinnings=%.2f opponents=%d validRuns=%d targetRuns=%d EV=%.6f nodeChance=%.6f",
                        DMStreetName(prms.street),
                        DMDebugPotSize(prms),
                        possibleWinnings,
                        nOpponents,
                        nIter,
                        SHOWDOWN_ITERATIONS,
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

        float estimateEV_TurnCutoff(DMStats& stats, const GameState& prms)
        {
            int nOpponents = 0;
            const Player* opponents[MAX_PLAYERS];
            prms.getOpponents(opponents, nOpponents);

            stats.numTurnCutOffs++;
            stats.turnCutOff_OppHist[nOpponents]++;

            int turn = prms.board.card[3].toInt();

            const AllHandStrengths& handStrenghts = prms.gc.allHandStrengths();

            int heroCurrentHandStrength = handStrenghts.getTurnHandStrength(turn, prms.heroHoleCards.toInt());
            const int* heroSortedRivers = handStrenghts.getSortedRivers(turn, prms.heroHoleCards.toInt());
            const int* heroRiverStrengths = handStrenghts.getRiverHandStrengths(turn, prms.heroHoleCards.toInt());

            bool isHeroCard[52];
            bool isOppCard[52];
            bool isBoardCard[52];

            for (int i = 0; i < 52; i++)
            {
                isHeroCard[i] = false;
                isOppCard[i] = false;
                isBoardCard[i] = false;
            }

            isHeroCard[prms.heroHoleCards.Card0.toInt()] = true;
            isHeroCard[prms.heroHoleCards.Card1.toInt()] = true;

            isBoardCard[prms.board.card[0].toInt()] = true;
            isBoardCard[prms.board.card[1].toInt()] = true;
            isBoardCard[prms.board.card[2].toInt()] = true;
            isBoardCard[prms.board.card[3].toInt()] = true;

            float EV = 0.0f;

            if (nOpponents == 0) // Everybody has folded except us -> go out
            {
                EV = prms.getPossibleWinnings();
            }
            else if (nOpponents == 1)
            {
                int oppRangeLength = opponents[0]->range().length();
                const int* oppRangeHCInd = opponents[0]->range().hcIndex();
                const float* oppRangeLikelihood = opponents[0]->range().likelihood();

                float possibleWinnings = prms.getPossibleWinnings();
                float totalWinnings = 0.0f;

                int nRiversChecked = 0;
                int nIter = 0;

                for (int i = 0; i < oppRangeLength; i++)
                {
                    if (oppRangeLikelihood[i] == 0.0f)
                        continue;

                    HoleCards oppHoleCards = HoleCards(oppRangeHCInd[i]);
                    isOppCard[oppHoleCards.Card0.toInt()] = true;
                    isOppCard[oppHoleCards.Card1.toInt()] = true;

                    int oppCurrentHandStrength = handStrenghts.getTurnHandStrength(turn, oppRangeHCInd[i]);
                    const int* oppSortedRivers = handStrenghts.getSortedRivers(turn, oppRangeHCInd[i]);
                    const int* oppRiverStrengths = handStrenghts.getRiverHandStrengths(turn, oppRangeHCInd[i]);

                    float ahead = 0;

                    if (heroCurrentHandStrength < oppCurrentHandStrength) // Hero is behind
                    {
                        for (int k = 0, river = heroSortedRivers[0];; river = heroSortedRivers[++k])
                        {
                            int heroStrength = heroRiverStrengths[river];

                            if (heroStrength == -1 || heroStrength < oppCurrentHandStrength)
                                break;

                            if (isOppCard[river])
                                continue;

                            assert(!isBoardCard[river]);

                            nRiversChecked++;
                            updateAhead(ahead, heroStrength, oppRiverStrengths[river]);
                        }
                    }
                    else if (heroCurrentHandStrength > oppCurrentHandStrength) // Opp is behind
                    {
                        ahead = 44;

                        for (int k = 0, river = oppSortedRivers[0];; river = oppSortedRivers[++k])
                        {
                            int oppStrength = oppRiverStrengths[river];

                            if (oppStrength == -1 || oppStrength < heroCurrentHandStrength)
                                break;

                            if (isHeroCard[river])
                                continue;

                            assert(!isBoardCard[river]);

                            ahead--;
                            nRiversChecked++;
                            updateAhead(ahead, heroRiverStrengths[river], oppStrength);
                        }
                    }
                    else // Its tie, check all cards
                    {
                        for (int river = 0; river < 52; river++)
                        {
                            if (isHeroCard[river] || isOppCard[river] || isBoardCard[river])
                                continue;

                            nRiversChecked++;
                            updateAhead(ahead, heroRiverStrengths[river], oppRiverStrengths[river]);
                        }
                    }

                    isOppCard[oppHoleCards.Card0.toInt()] = false;
                    isOppCard[oppHoleCards.Card1.toInt()] = false;

                    totalWinnings += oppRangeLikelihood[i] * possibleWinnings * (ahead / 44);
                    nIter++;
                }

                stats.turnCutOff_NumPots += 1;
                stats.turnCutOff_NumRiversChecked += nRiversChecked / (float)nIter;
                EV = totalWinnings;

                if (DMShouldLog(&g_DMTurnCutoffLogCount, DM_MAX_TURNCUTOFF_LOGS))
                {
                    float avgRiversChecked = (nIter > 0) ? (nRiversChecked / (float)nIter) : 0.0f;
                    float normalizedTurnEquity = (possibleWinnings > 0.0f) ? (EV / possibleWinnings) : 0.0f;

                    DMLogF(
                        "[DM][TURN CUTOFF HU] pot=%d possibleWinnings=%.2f oppRangeLen=%d activeCombos=%d avgRiversChecked=%.2f normalizedTurnEquity=%.6f EV=%.6f nodeChance=%.6f",
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
                int opponentHandIndexes[MAX_PLAYERS * TCUTOFF_BIN_COUNT];

                for (int i = 0; i < nOpponents; i++)
                {
                    opponents[i]->range_FillHandIndices(&opponentHandIndexes[i * TCUTOFF_BIN_COUNT], TCUTOFF_BIN_COUNT);
                }

                int opponentHoleCardsInd[MAX_PLAYERS];
                HoleCards opponentHoleCards[MAX_PLAYERS];
                Pot pot(prms._players);
                ParkMillerCarta rng(1);

                float totalWinnings = 0.0f;
                int nRiversChecked = 0;
                int nIter = 0;

                for (int attempts = 0; nIter < TCUTOFF_ITERATIONS && attempts < TCUTOFF_ITERATIONS * 200; attempts++)
                {
                    // Choose opponent hole cards randomly
                    for (int j = 0; j < nOpponents; j++)
                    {
                        int ind = rng.next() % TCUTOFF_BIN_COUNT;
                        opponentHoleCardsInd[j] = opponentHandIndexes[j * TCUTOFF_BIN_COUNT + ind];
                        opponentHoleCards[j] = HoleCards(opponentHoleCardsInd[j]);
                    }

                    bool iterationValid = true;

                    // Ban choosen hole cards and check if combinationis is valid
                    for (int j = 0; j < nOpponents; j++)
                    {
                        int ind0 = opponentHoleCards[j].Card0.toInt();
                        int ind1 = opponentHoleCards[j].Card1.toInt();

                        if (isHeroCard[ind0] || isHeroCard[ind1] ||
                            isBoardCard[ind0] || isBoardCard[ind1] ||
                            isOppCard[ind0] || isOppCard[ind1] ||
                            ind0 == ind1)
                        {
                            iterationValid = false;
                        }

                        isOppCard[ind0] = true;
                        isOppCard[ind1] = true;
                    }

                    // If combination is valid calculate EV... Choose some rivers to check...
                    if (iterationValid)
                    {
                        int oppCurrentHandStrength[MAX_PLAYERS];
                        const int* oppSortedRivers[MAX_PLAYERS];
                        const int* oppRiverStrengths[MAX_PLAYERS];

                        for (int j = 0; j < nOpponents; j++)
                        {
                            oppCurrentHandStrength[j] = handStrenghts.getTurnHandStrength(turn, opponentHoleCardsInd[j]);
                            oppSortedRivers[j] = handStrenghts.getSortedRivers(turn, opponentHoleCardsInd[j]);
                            oppRiverStrengths[j] = handStrenghts.getRiverHandStrengths(turn, opponentHoleCardsInd[j]);
                        }

                        // For all pots and side pots calculate separatelly...
                        for (int ip = 0; ip < pot.numPots(); ip++)
                        {
                            if (prms.hero().moneyInPot() < pot.getHeight(ip))
                                break;

                            int maxOppCurrHandStrength = 0;

                            for (int j = 0; j < nOpponents; j++)
                            {
                                if (opponents[j]->moneyInPot() >= pot.getHeight(ip))
                                    maxOppCurrHandStrength = (std::max)(maxOppCurrHandStrength, oppCurrentHandStrength[j]);
                            }

                            // If there is no-one but us fighting for this pot continue
                            if (maxOppCurrHandStrength == 0)
                            {
                                totalWinnings += pot.getMoney(ip);
                                continue;
                            }

                            float ahead = 0;
                            bool riverToCheck[52];

                            for (int k = 0; k < 52; k++)
                            {
                                riverToCheck[k] = false;
                            }

                            if (heroCurrentHandStrength < maxOppCurrHandStrength) // Hero is behind, check all rivers that make us possible ahead
                            {
                                for (int k = 0, river = heroSortedRivers[0];; river = heroSortedRivers[++k])
                                {
                                    int heroStrength = heroRiverStrengths[river];

                                    if (heroStrength == -1 || heroStrength < maxOppCurrHandStrength)
                                        break;

                                    if (isOppCard[river])
                                        continue;

                                    assert(!isBoardCard[river]);
                                    riverToCheck[river] = true;
                                }
                            }
                            else if (heroCurrentHandStrength > maxOppCurrHandStrength) // Hero is ahead, check all cards that make oppontent ahead
                            {
                                ahead = (float)(46 - nOpponents * 2);

                                for (int j = 0; j < nOpponents; j++)
                                {
                                    if (opponents[j]->moneyInPot() < pot.getHeight(ip))
                                        continue;

                                    for (int k = 0, river = oppSortedRivers[j][0];; river = oppSortedRivers[j][++k])
                                    {
                                        int oppStrength = oppRiverStrengths[j][river];

                                        if (oppStrength == -1 || oppStrength < heroCurrentHandStrength)
                                            break;

                                        if (isHeroCard[river] || isOppCard[river] || riverToCheck[river])
                                            continue;

                                        assert(!isBoardCard[river]);

                                        ahead -= 1;
                                        riverToCheck[river] = true;
                                    }
                                }
                            }
                            else // heroCurrentHandStrength == maxOppCurrHandStrength, check all rivers
                            {
                                for (int river = 0; river < 52; river++)
                                {
                                    if (isHeroCard[river] || isOppCard[river] || isBoardCard[river])
                                        continue;

                                    riverToCheck[river] = true;
                                }
                            }

                            for (int river = 0; river < 52; river++)
                            {
                                if (!riverToCheck[river])
                                    continue;

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

                                if (heroRiverStrengths[river] > maxOppStrength)
                                {
                                    ahead += 1.0f;
                                }
                                else if (heroRiverStrengths[river] == maxOppStrength)
                                {
                                    ahead += 1.0f / (tiedOpponents + 1);
                                }

                                nRiversChecked++;
                            }

                            totalWinnings += pot.getMoney(ip) * ahead / (46 - nOpponents * 2);
                        }

                        nIter++;
                    }

                    // Un-ban chosen hole cards
                    for (int j = 0; j < nOpponents; j++)
                    {
                        int ind0 = opponentHoleCards[j].Card0.toInt();
                        int ind1 = opponentHoleCards[j].Card1.toInt();

                        isOppCard[ind0] = false;
                        isOppCard[ind1] = false;
                    }
                }

                if (nIter <= 0)
                {
                    DMLogAlways("FATAL: nIter == 0 em amostragem multiway. Nenhuma combinacao valida de cartas dos oponentes foi encontrada.");
                    throw std::runtime_error("DecisionMaking: nIter == 0 em amostragem multiway");
                }

                stats.turnCutOff_NumRiversChecked += nRiversChecked / (float)nIter;
                stats.turnCutOff_NumPots += pot.numPots();

                EV = totalWinnings / nIter;

                if (DMShouldLog(&g_DMTurnCutoffLogCount, DM_MAX_TURNCUTOFF_LOGS))
                {
                    float avgRiversChecked = (nIter > 0) ? (nRiversChecked / (float)nIter) : 0.0f;

                    DMLogF(
                        "[DM][TURN CUTOFF MULTIWAY] pot=%d opponents=%d validRuns=%d targetRuns=%d avgRiversChecked=%.2f pots=%d EV=%.6f nodeChance=%.6f",
                        DMDebugPotSize(prms),
                        nOpponents,
                        nIter,
                        TCUTOFF_ITERATIONS,
                        avgRiversChecked,
                        pot.numPots(),
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
                float minEquity = (std::numeric_limits<float>::max)();

                for (int i = 0; i < nOpponents; i++)
                {
                    float equity = PreFlopEquity::calculate(prms.heroHoleCards, opponents[i]->range());
                    minEquity = std::min(minEquity, equity);
                }

                float mod;

                if (nOpponents == 2)
                    mod = 0.8f;
                else if (nOpponents == 3)
                    mod = 0.7f;
                else if (nOpponents == 4)
                    mod = 0.6f;
                else if (nOpponents == 5)
                    mod = 0.5f;

                EV = (mod * minEquity) * prms.getPossibleWinnings();
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

    extern "C" G5_EXPORT void __stdcall EstimateEV(float& checkCallEV, float& betRaiseEV, int buttonInd, int heroIndex, const HoleCards& heroHoleCards,
        const PlayerDTO* players, int nPlayers, const Card* cardsInBoard, Street street, int numBets, int numCallers, int bigBlindSize, const void* gc)
    {
        DMLog("===== ENTER EstimateEV =====");
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

        // Inicializa os EVs para valores reconhec?veis.
        // Estes valores N?O devem ser usados como decis?o, servem apenas para diagn?stico
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
            estimateEV_HeroPlays(checkCallEV, betRaiseEV, stats, prms);
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

    extern "C" G5_EXPORT void __stdcall EstimateEVForBetRaiseAmount(float& checkCallEV, float& betRaiseEV, int forcedBetRaiseAmount, int buttonInd, int heroIndex, const HoleCards& heroHoleCards,
        const PlayerDTO* players, int nPlayers, const Card* cardsInBoard, Street street, int numBets, int numCallers, int bigBlindSize, const void* gc)
    {
        if (forcedBetRaiseAmount > 0)
        {
            g_DMForcedRootBetRaiseAmount = forcedBetRaiseAmount;
            g_DMForceRootBetRaiseAmount = true;
            DMLogF("[DM][MULTISIZE ROOT] forcedBetRaiseAmount=%d", forcedBetRaiseAmount);
        }
        else
        {
            g_DMForcedRootBetRaiseAmount = 0;
            g_DMForceRootBetRaiseAmount = false;
        }

        EstimateEV(checkCallEV, betRaiseEV, buttonInd, heroIndex, heroHoleCards,
            players, nPlayers, cardsInBoard, street, numBets, numCallers, bigBlindSize, gc);

        g_DMForcedRootBetRaiseAmount = 0;
        g_DMForceRootBetRaiseAmount = false;
    }

}
