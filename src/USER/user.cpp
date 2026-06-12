#define USER_DLL

#include "user.h"
#include <windows.h>
#include <string>
#include <stdio.h>
#include <string.h>
#include <stddef.h>
#include "OpenHoldemFunctions.h"

// =============================================================================
// EXPORTS CANONICAS PARA OPENHOLDEM WIN32
//
// Em Win32, funções C/C++ podem sair decoradas no export table. O OpenHoldem
// procura a entrada canonica "process_message". Esta diretiva garante que,
// além do nome decorado gerado pelo compilador, a DLL também exponha exatamente
// o nome que o OH espera.
//
// Mantemos também process_query como rota compatível com os exemplos oficiais,
// embora a rota principal do projeto continue sendo dll$decisao -> ProcessQuery.
// =============================================================================
#if defined(_M_IX86)
#pragma comment(linker, "/EXPORT:process_message=_process_message")
#pragma comment(linker, "/EXPORT:process_query=_process_query")
#endif

#pragma pack(push, 4)
struct DecisionResult {
    int   actionType;
    int   byAmount;
    float checkCallEV;
    float betRaiseEV;
};
#pragma pack(pop)

enum G5Action {
    ACT_FOLD = 0, ACT_CHECK = 1, ACT_CALL = 2,
    ACT_BET = 3, ACT_RAISE = 4, ACT_ALLIN = 5
};

// =============================================================================
// ENHANCED PRWIN / PRW1326
//
// O OH entrega um ponteiro para esta estrutura via process_message("prw1326").
// A user.dll mantém uma imagem local, atualizada pelo G5Bridge, e o callback
// copia essa imagem para a estrutura real imediatamente antes do loop do PrWin.
// =============================================================================
struct sprw1326_chair
{
    int level;
    int limit;
    int ignore;
    int rankhi[1326];
    int ranklo[1326];
    int weight[1326];
    double scratch;
};

struct sprw1326
{
    int useme;
    int preflop;
    int usecallback;
    double (*prw_callback)(void);
    double scratch;
    sprw1326_chair vanilla_chair;
    sprw1326_chair chair[10];
};

typedef sprw1326* pp13;

static pp13 g_prw1326 = nullptr;
static sprw1326 g_localPrw1326 = {};
static bool g_localPrw1326Ready = false;

// Ponteiros entregues pelo OpenHoldem via process_message.
// Nesta versão do OH, porém, o prw1326 correto vem pela interface exportada
// GetPrw1326(), declarada em OpenHoldemFunctions.h.
typedef double (*pfgws_t)(int chair, const char* psym, bool& iserr);

static const void* g_lastOHStatePtr = nullptr;
static const void* g_phl1kPtr = nullptr;
static pfgws_t g_pfgws = nullptr;

static bool g_loggedMsgLoad = false;
static bool g_loggedMsgUnload = false;
static bool g_loggedMsgState = false;
static bool g_loggedMsgPhl1k = false;
static bool g_loggedMsgPrw1326 = false;
static bool g_loggedMsgPfgws = false;
static bool g_loggedMsgQuery = false;

static bool g_loggedGetPrw1326DirectOk = false;
static bool g_loggedGetPrw1326DirectNull = false;

static volatile LONG g_enhancedPrWinProfileVersion = 0;
static volatile LONG g_enhancedPrWinLastCallbackVersion = 0;
static volatile LONG g_enhancedPrWinCallbackCount = 0;
static bool g_lastPostFlopDecisionUsedEnhancedPrWin = false;

static double EnhancedPrWinCallback(void);
static const char* StreetName(int s);
static const char* ActionName(int a);
DLL_IMPLEMENTS double __stdcall ProcessQuery(const char* pquery);
extern "C" __declspec(dllexport) double process_query(const char* pquery);

static LONG ReadInterlockedLong(volatile LONG* value)
{
    return InterlockedCompareExchange(value, 0, 0);
}

static bool g_loggedEnhancedPrWinNativeLayout = false;

static void LogEnhancedPrWinNativeLayoutOnce()
{
    if (g_loggedEnhancedPrWinNativeLayout)
        return;

    g_loggedEnhancedPrWinNativeLayout = true;

    WriteLog(
        "[user.cpp] EnhancedPrWin layout nativo: sizeof(chair)=%u sizeof(root)=%u "
        "root(useme=%u preflop=%u usecallback=%u vanilla=%u chairArray=%u) "
        "chair(level=%u limit=%u ignore=%u rankhi=%u ranklo=%u weight=%u scratch=%u).\n",
        (unsigned)sizeof(sprw1326_chair),
        (unsigned)sizeof(sprw1326),
        (unsigned)offsetof(sprw1326, useme),
        (unsigned)offsetof(sprw1326, preflop),
        (unsigned)offsetof(sprw1326, usecallback),
        (unsigned)offsetof(sprw1326, vanilla_chair),
        (unsigned)offsetof(sprw1326, chair),
        (unsigned)offsetof(sprw1326_chair, level),
        (unsigned)offsetof(sprw1326_chair, limit),
        (unsigned)offsetof(sprw1326_chair, ignore),
        (unsigned)offsetof(sprw1326_chair, rankhi),
        (unsigned)offsetof(sprw1326_chair, ranklo),
        (unsigned)offsetof(sprw1326_chair, weight),
        (unsigned)offsetof(sprw1326_chair, scratch));
}

typedef void(__stdcall* FN_NewHand)      (int* stacks, int* chairs, int heroIndex, int buttonIndex, int numPlayers, int bigBlind, const char* basePath, const char* tableTitle);
typedef void(__stdcall* FN_DealHoleCards)(const char* card0, const char* card1);
typedef void(__stdcall* FN_NewAction)    (int playerIndex, int actionType, int byAmount);
typedef void(__stdcall* FN_GoToNextStreet)(const char* c0, const char* c1, const char* c2, int numCards);
typedef DecisionResult(__stdcall* FN_GetDecision)();
typedef int(__stdcall* FN_UpdateEnhancedPrWinProfile)(
    void* prw1326Ptr,
    int currentStreet,
    int rootUseMeOffset,
    int rootPreflopOffset,
    int rootUseCallbackOffset,
    int chairArrayOffset,
    int chairSize,
    int chairLevelOffset,
    int chairLimitOffset,
    int chairIgnoreOffset,
    int chairRankHiOffset,
    int chairRankLoOffset,
    int chairWeightOffset);
typedef void(__stdcall* FN_SetOHEquitySnapshot)(
    double prwin,
    double prtie,
    double prlos,
    double prwinnow,
    double prlosnow,
    double nhands,
    double nhandshi,
    double nhandslo,
    double nhandsti);

static HINSTANCE         hBridge = NULL;
static FN_NewHand        pNewHand = nullptr;
static FN_DealHoleCards  pDealHoleCards = nullptr;
static FN_NewAction      pNewAction = nullptr;
static FN_GoToNextStreet pGoToNextStreet = nullptr;
static FN_GetDecision                  pGetDecision = nullptr;
static FN_UpdateEnhancedPrWinProfile   pUpdateEnhancedPrWinProfile = nullptr;
static FN_SetOHEquitySnapshot          pSetOHEquitySnapshot = nullptr;
static bool                            g_bridgeLoaded = false;

// CAMINHO DINÂMICO
static char g_g5BasePath[MAX_PATH] = "";

static bool g_isHandActive = false;
static bool g_cardsDealt = false;
static int  g_currentStreet = 0;
static int  g_heroChair = -1;
static int  g_buttonChair = -1;
static int  g_numLogicalPlayers = 0;
static int  g_chairToLogical[10];
static bool g_isActive[10];
static int  g_lastPlayerBets[10];   // em centavos
static int  g_streetEndMaxBet = 0;  // em centavos
static bool g_hasCheckedThisStreet[10];
static int  g_lastEvaluatedChair = -1;
static bool g_wentAllIn[10];

static bool   g_heroActedThisStreet = false;
static bool   g_heroRegistered = false;
static double g_cachedDecision = 0.0;
static int    g_cachedActionType = ACT_FOLD;
static int    g_cachedByAmount = 0;  // em centavos

// =============================================================================
// LEITURA DE VALORES MONETÁRIOS EM CENTAVOS
// $0.14 -> 14 | $3.00 -> 300 | $0.04 -> 4
// =============================================================================
static int ReadCents(const char* symbol) {
    double val = GetSymbol(symbol);
    return (int)(val * 100.0 + 0.5);
}

static int ReadChairCents(const char* prefix, int chair) {
    char sym[32];
    sprintf_s(sym, sizeof(sym), "%s%d", prefix, chair);
    return ReadCents(sym);
}

static int ReadChairBalance(int chair) {
    return ReadChairCents("balance", chair);
}

static int ReadChairCurrentBet(int chair) {
    return ReadChairCents("currentbet", chair);
}

static bool IsAtLeast66PercentOf(int intended, int realAllInAmount) {
    if (realAllInAmount <= 0)
        return false;

    return intended * 100 >= realAllInAmount * 66;
}

static bool IsPostFlopStreet(int ohStreet)
{
    return ohStreet >= 2 && ohStreet <= 4;
}

static void ResetEnhancedPrWinProfileToVanilla()
{
    if (!g_prw1326)
        return;

    memset(&g_localPrw1326, 0, sizeof(g_localPrw1326));

    g_localPrw1326.useme = 1326;
    g_localPrw1326.preflop = 0;       // nosso uso será apenas flop/turn/river
    g_localPrw1326.usecallback = 1326;
    g_localPrw1326.prw_callback = EnhancedPrWinCallback;
    g_localPrw1326.scratch = 0.0;

    g_localPrw1326.vanilla_chair = g_prw1326->vanilla_chair;

    for (int i = 0; i < 10; i++)
    {
        g_localPrw1326.chair[i] = g_prw1326->vanilla_chair;
        g_localPrw1326.chair[i].ignore = 1;
    }

    g_localPrw1326Ready = true;
}

static void InstallEnhancedPrWinPointer(const void* param)
{
    if (!param)
        return;

    g_prw1326 = (pp13)param;
    LogEnhancedPrWinNativeLayoutOnce();
    ResetEnhancedPrWinProfileToVanilla();

    if (g_prw1326)
    {
        g_prw1326->useme = 1326;
        g_prw1326->preflop = 0;
        g_prw1326->usecallback = 1326;
        g_prw1326->prw_callback = EnhancedPrWinCallback;
    }

    WriteLog("[user.cpp] EnhancedPrWin: ponteiro prw1326 instalado. ptr=%p useme=%d usecallback=%d preflop=%d.\n",
        g_prw1326,
        g_prw1326 ? g_prw1326->useme : 0,
        g_prw1326 ? g_prw1326->usecallback : 0,
        g_prw1326 ? g_prw1326->preflop : 0);
}

static bool TryInstallEnhancedPrWinPointerFromOHInterface(const char* reason)
{
    if (g_prw1326)
        return true;

    void* ptr = nullptr;

    __try
    {
        ptr = GetPrw1326();
    }
    __except (1)
    {
        WriteLog("[user.cpp] EnhancedPrWin: EXCECAO ao chamar GetPrw1326() (%s).\n",
            reason ? reason : "sem contexto");
        return false;
    }

    if (!ptr)
    {
        if (!g_loggedGetPrw1326DirectNull)
        {
            g_loggedGetPrw1326DirectNull = true;
            WriteLog("[user.cpp] EnhancedPrWin: GetPrw1326() retornou NULL (%s).\n",
                reason ? reason : "sem contexto");
        }

        return false;
    }

    InstallEnhancedPrWinPointer(ptr);

    if (!g_loggedGetPrw1326DirectOk)
    {
        g_loggedGetPrw1326DirectOk = true;
        WriteLog("[user.cpp] EnhancedPrWin: prw1326 obtido via GetPrw1326().\n");
    }

    return true;
}

static double EnhancedPrWinCallback(void)
{
    if (!g_prw1326 || !g_localPrw1326Ready)
        return 0.0;

    // Callback chamado dentro do ciclo do OH. Mantê-lo curto e sem log.
    memcpy(g_prw1326, &g_localPrw1326, sizeof(sprw1326));

    LONG currentVersion = ReadInterlockedLong(&g_enhancedPrWinProfileVersion);
    InterlockedExchange(&g_enhancedPrWinLastCallbackVersion, currentVersion);
    InterlockedIncrement(&g_enhancedPrWinCallbackCount);

    return 0.0;
}

static bool UpdateEnhancedPrWinProfileFromBridge(int ohStreet)
{
    if (!IsPostFlopStreet(ohStreet))
        return false;

    if (!g_prw1326)
    {
        WriteLog("[user.cpp] EnhancedPrWin: prw1326 ainda nao recebido pelo OH.\n");
        return false;
    }

    if (!g_localPrw1326Ready)
        ResetEnhancedPrWinProfileToVanilla();

    if (!pUpdateEnhancedPrWinProfile)
    {
        WriteLog("[user.cpp] EnhancedPrWin: G5Bridge_UpdateEnhancedPrWinProfile ainda nao exportada.\n");
        return false;
    }

    int ok = 0;

    __try
    {
        ok = pUpdateEnhancedPrWinProfile(
            &g_localPrw1326,
            ohStreet,
            (int)offsetof(sprw1326, useme),
            (int)offsetof(sprw1326, preflop),
            (int)offsetof(sprw1326, usecallback),
            (int)offsetof(sprw1326, chair),
            (int)sizeof(sprw1326_chair),
            (int)offsetof(sprw1326_chair, level),
            (int)offsetof(sprw1326_chair, limit),
            (int)offsetof(sprw1326_chair, ignore),
            (int)offsetof(sprw1326_chair, rankhi),
            (int)offsetof(sprw1326_chair, ranklo),
            (int)offsetof(sprw1326_chair, weight));
    }
    __except (1)
    {
        WriteLog("[user.cpp] EnhancedPrWin: EXCECAO ao atualizar perfil pelo G5Bridge.\n");
        ok = 0;
    }

    if (ok)
    {
        g_localPrw1326.useme = 1326;
        g_localPrw1326.preflop = 0;
        g_localPrw1326.usecallback = 1326;
        g_localPrw1326.prw_callback = EnhancedPrWinCallback;
        g_localPrw1326Ready = true;

        LONG profileVersion = InterlockedIncrement(&g_enhancedPrWinProfileVersion);

        WriteLog("[user.cpp] EnhancedPrWin: perfil pos-flop atualizado pelo G5Bridge para street=%s versao=%ld.\n",
            StreetName(ohStreet), profileVersion);

        return true;
    }

    WriteLog("[user.cpp] EnhancedPrWin: G5Bridge nao atualizou perfil; decisao pos-flop nao usara perfil vanilla.\n");
    return false;
}

static double ReadOHSymbolSafe(const char* symbol)
{
    __try
    {
        return GetSymbol(symbol);
    }
    __except (1)
    {
        return 0.0;
    }
}

struct OHEquityReadout
{
    double prwin;
    double prtie;
    double prlos;
    double prwinnow;
    double prlosnow;
    double nhands;
    double nhandshi;
    double nhandslo;
    double nhandsti;
};

static int ReadCentsSafe(const char* symbol)
{
    double val = ReadOHSymbolSafe(symbol);

    if (val <= 0.0)
        return 0;

    return (int)(val * 100.0 + 0.5);
}

static bool IsFiniteNumber(double value)
{
    return value == value && value > -1.0e300 && value < 1.0e300;
}

static double Clamp01Double(double value)
{
    if (!IsFiniteNumber(value))
        return 0.0;

    if (value < 0.0)
        return 0.0;

    if (value > 1.0)
        return 1.0;

    return value;
}

static void ReadOHEquityReadout(OHEquityReadout& eq)
{
    eq.prwin = ReadOHSymbolSafe("prwin");
    eq.prtie = ReadOHSymbolSafe("prtie");
    eq.prlos = ReadOHSymbolSafe("prlos");
    eq.prwinnow = ReadOHSymbolSafe("prwinnow");
    eq.prlosnow = ReadOHSymbolSafe("prlosnow");
    eq.nhands = ReadOHSymbolSafe("nhands");
    eq.nhandshi = ReadOHSymbolSafe("nhandshi");
    eq.nhandslo = ReadOHSymbolSafe("nhandslo");
    eq.nhandsti = ReadOHSymbolSafe("nhandsti");
}

static void SendOHEquitySnapshotToBridgeValues(const OHEquityReadout& eq)
{
    if (!pSetOHEquitySnapshot)
        return;

    __try
    {
        pSetOHEquitySnapshot(
            eq.prwin,
            eq.prtie,
            eq.prlos,
            eq.prwinnow,
            eq.prlosnow,
            eq.nhands,
            eq.nhandshi,
            eq.nhandslo,
            eq.nhandsti);
    }
    __except (1)
    {
        WriteLog("[user.cpp] EnhancedPrWin: EXCECAO ao enviar snapshot OH para G5Bridge.\n");
        return;
    }

    WriteLog("[user.cpp] EnhancedPrWin Snapshot OH: prwin=%.4f prtie=%.4f prlos=%.4f prwinnow=%.4f prlosnow=%.4f nhands=%.0f.\n",
        eq.prwin, eq.prtie, eq.prlos, eq.prwinnow, eq.prlosnow, eq.nhands);
}

static void SendOHEquitySnapshotToBridge(int ohStreet)
{
    if (!IsPostFlopStreet(ohStreet))
        return;

    OHEquityReadout eq = {};
    ReadOHEquityReadout(eq);
    SendOHEquitySnapshotToBridgeValues(eq);
}

static bool TryComputeOHEquity01(const OHEquityReadout& eq, double& equity)
{
    equity = 0.0;

    if (!IsFiniteNumber(eq.prwin) || !IsFiniteNumber(eq.prtie) || !IsFiniteNumber(eq.prlos))
        return false;

    double prwin = eq.prwin;
    double prtie = eq.prtie;
    double prlos = eq.prlos;

    double sum = prwin + prtie + prlos;

    // OpenHoldem normalmente retorna 0..1.
    // Esta protecao cobre ambientes que exponham 0..100.
    if (sum > 1.5)
    {
        prwin /= 100.0;
        prtie /= 100.0;
        prlos /= 100.0;
        sum = prwin + prtie + prlos;
    }

    if (!IsFiniteNumber(sum) || sum <= 0.000001)
        return false;

    equity = Clamp01Double(prwin + (0.5 * prtie));
    return true;
}

static int GetPostFlopAmountToCallCents()
{
    if (g_heroChair < 0 || g_heroChair >= 10)
        return 0;

    int heroBet = ReadChairCurrentBet(g_heroChair);
    int maxBet = heroBet;

    for (int chair = 0; chair < 10; chair++)
    {
        if (!g_isActive[chair])
            continue;

        int chairBet = ReadChairCurrentBet(chair);

        if (chairBet > maxBet)
            maxBet = chairBet;
    }

    int amountToCall = maxBet - heroBet;

    if (amountToCall < 0)
        amountToCall = 0;

    int heroBalance = ReadChairBalance(g_heroChair);

    if (heroBalance > 0 && amountToCall > heroBalance)
        amountToCall = heroBalance;

    return amountToCall;
}

static bool EnsurePrw1326PointerAvailable()
{
    if (g_prw1326)
        return true;

    WriteLog("[user.cpp] EnhancedPrWin: prw1326 ausente; buscando via GetPrw1326().\n");

    if (TryInstallEnhancedPrWinPointerFromOHInterface("EnsurePrw1326PointerAvailable"))
        return true;

    WriteLog("[user.cpp] EnhancedPrWin: prw1326 indisponivel via GetPrw1326().\n");
    return false;
}

static bool WasEnhancedPrWinCallbackUsedForCurrentProfile()
{
    LONG profileVersion = ReadInterlockedLong(&g_enhancedPrWinProfileVersion);
    LONG callbackVersion = ReadInterlockedLong(&g_enhancedPrWinLastCallbackVersion);

    return profileVersion > 0 && callbackVersion == profileVersion;
}

static bool ForceOpenHoldemPrWinRecalculation(int ohStreet)
{
    if (!IsPostFlopStreet(ohStreet))
        return false;

    if (!g_prw1326)
        return false;

    LONG profileVersionBefore = ReadInterlockedLong(&g_enhancedPrWinProfileVersion);
    LONG callbackVersionBefore = ReadInterlockedLong(&g_enhancedPrWinLastCallbackVersion);
    LONG callbackCountBefore = ReadInterlockedLong(&g_enhancedPrWinCallbackCount);

    __try
    {
        (void)GetSymbol("cmd$recalc");
    }
    __except (1)
    {
        WriteLog("[user.cpp] EnhancedPrWin: EXCECAO ao executar cmd$recalc.\n");
        return false;
    }

    LONG profileVersionAfter = ReadInterlockedLong(&g_enhancedPrWinProfileVersion);
    LONG callbackVersionAfter = ReadInterlockedLong(&g_enhancedPrWinLastCallbackVersion);
    LONG callbackCountAfter = ReadInterlockedLong(&g_enhancedPrWinCallbackCount);

    if (profileVersionAfter <= 0 || callbackVersionAfter != profileVersionAfter)
    {
        WriteLog("[user.cpp] EnhancedPrWin: cmd$recalc executado, mas callback nao confirmou perfil atual. perfilAntes=%ld callbackAntes=%ld perfilDepois=%ld callbackDepois=%ld callbacks=%ld->%ld.\n",
            profileVersionBefore,
            callbackVersionBefore,
            profileVersionAfter,
            callbackVersionAfter,
            callbackCountBefore,
            callbackCountAfter);

        return false;
    }

    WriteLog("[user.cpp] EnhancedPrWin: cmd$recalc concluido com perfil confirmado. street=%s perfil=%ld callbacks=%ld->%ld.\n",
        StreetName(ohStreet),
        profileVersionAfter,
        callbackCountBefore,
        callbackCountAfter);

    return true;
}

static int ClampIntCents(int value, int minValue, int maxValue)
{
    if (value < minValue)
        return minValue;

    if (value > maxValue)
        return maxValue;

    return value;
}

static int GetPostFlopMaxCurrentBetCents()
{
    int maxBet = 0;

    for (int chair = 0; chair < 10; chair++)
    {
        if (!g_isActive[chair])
            continue;

        int chairBet = ReadChairCurrentBet(chair);

        if (chairBet > maxBet)
            maxBet = chairBet;
    }

    return maxBet;
}

static int GetHeroTotalStackCents()
{
    if (g_heroChair < 0 || g_heroChair >= 10)
        return 0;

    return ReadChairBalance(g_heroChair) + ReadChairCurrentBet(g_heroChair);
}

static int GetPostFlopPotBeforeCallCents()
{
    int pot = ReadCentsSafe("pot");

    if (pot > 0)
        return pot;

    int commonPot = ReadCentsSafe("potcommon");
    int streetBets = 0;

    for (int chair = 0; chair < 10; chair++)
    {
        if (!g_isActive[chair])
            continue;

        streetBets += ReadChairCurrentBet(chair);
    }

    pot = commonPot + streetBets;

    if (pot <= 0)
        pot = commonPot;

    if (pot <= 0)
        pot = streetBets;

    if (pot <= 0)
        pot = 1;

    return pot;
}

static int GetPostFlopPotCents()
{
    return GetPostFlopPotBeforeCallCents();
}

static double PostFlopValueThreshold(int ohStreet, bool facingBet)
{
    if (ohStreet == 2)
        return facingBet ? 0.66 : 0.62;

    if (ohStreet == 3)
        return facingBet ? 0.63 : 0.60;

    if (ohStreet == 4)
        return facingBet ? 0.60 : 0.56;

    return facingBet ? 0.66 : 0.62;
}

static double PostFlopRaiseMargin(int ohStreet)
{
    if (ohStreet == 2)
        return 0.18;

    if (ohStreet == 3)
        return 0.15;

    if (ohStreet == 4)
        return 0.12;

    return 0.18;
}

static double PostFlopBetFraction(double equity)
{
    if (equity >= 0.82)
        return 0.75;

    if (equity >= 0.72)
        return 0.66;

    return 0.50;
}

static int BuildPostFlopBetOrRaiseToTotalCents(int amountToCall, int potBeforeCall, double equity)
{
    int heroBet = ReadChairCurrentBet(g_heroChair);
    int heroBalance = ReadChairBalance(g_heroChair);
    int heroTotal = heroBet + heroBalance;

    if (heroTotal <= heroBet)
        return heroBet;

    int maxBet = GetPostFlopMaxCurrentBetCents();
    int bb = ReadCentsSafe("bblind");

    if (bb <= 0)
        bb = 1;

    double fraction = PostFlopBetFraction(equity);

    if (amountToCall <= 0)
    {
        int betSize = (int)(potBeforeCall * fraction + 0.5);

        if (betSize < bb)
            betSize = bb;

        int raiseToTotal = heroBet + betSize;
        return ClampIntCents(raiseToTotal, heroBet, heroTotal);
    }

    int potAfterCall = potBeforeCall + amountToCall;

    if (potAfterCall <= 0)
        potAfterCall = amountToCall;

    int raiseExtra = (int)(potAfterCall * fraction + 0.5);

    if (raiseExtra < amountToCall)
        raiseExtra = amountToCall;

    int raiseToTotal = heroBet + amountToCall + raiseExtra;
    int minRaiseToTotal = maxBet + amountToCall;

    if (raiseToTotal < minRaiseToTotal)
        raiseToTotal = minRaiseToTotal;

    return ClampIntCents(raiseToTotal, heroBet + amountToCall, heroTotal);
}

static bool ShouldConvertPostFlopRaiseToAllIn(int raiseToTotal)
{
    int heroTotal = GetHeroTotalStackCents();

    if (heroTotal <= 0)
        return false;

    return raiseToTotal * 100 >= heroTotal * 80;
}

static DecisionResult BuildPostFlopDecisionFromEnhancedPrWin(int ohStreet)
{
    DecisionResult d = {};
    d.actionType = ACT_FOLD;
    d.byAmount = 0;
    d.checkCallEV = 0.0f;
    d.betRaiseEV = 0.0f;

    int amountToCall = GetPostFlopAmountToCallCents();
    int potBeforeCall = GetPostFlopPotBeforeCallCents();
    bool facingBet = amountToCall > 0;

    g_lastPostFlopDecisionUsedEnhancedPrWin = false;

    if (!EnsurePrw1326PointerAvailable())
    {
        if (facingBet)
        {
            d.actionType = ACT_FOLD;
            d.byAmount = amountToCall;

            WriteLog("[user.cpp] OH PostFlop Decision: EnhancedPrWin sem ponteiro prw1326 -> Fold seguro. call=$%.2f pot=$%.2f.\n",
                amountToCall / 100.0, potBeforeCall / 100.0);
        }
        else
        {
            d.actionType = ACT_CHECK;
            d.byAmount = 0;

            WriteLog("[user.cpp] OH PostFlop Decision: EnhancedPrWin sem ponteiro prw1326 e sem aposta para pagar -> Check seguro.\n");
        }

        return d;
    }

    bool enhancedReady = UpdateEnhancedPrWinProfileFromBridge(ohStreet);

    if (!enhancedReady)
    {
        if (facingBet)
        {
            d.actionType = ACT_FOLD;
            d.byAmount = amountToCall;

            WriteLog("[user.cpp] OH PostFlop Decision: perfil EnhancedPrWin nao atualizado -> Fold seguro. call=$%.2f pot=$%.2f.\n",
                amountToCall / 100.0, potBeforeCall / 100.0);
        }
        else
        {
            d.actionType = ACT_CHECK;
            d.byAmount = 0;

            WriteLog("[user.cpp] OH PostFlop Decision: perfil EnhancedPrWin nao atualizado e sem aposta para pagar -> Check seguro.\n");
        }

        return d;
    }

    if (!ForceOpenHoldemPrWinRecalculation(ohStreet))
    {
        if (facingBet)
        {
            d.actionType = ACT_FOLD;
            d.byAmount = amountToCall;

            WriteLog("[user.cpp] OH PostFlop Decision: EnhancedPrWin nao confirmou recalc/callback -> Fold seguro. call=$%.2f pot=$%.2f.\n",
                amountToCall / 100.0, potBeforeCall / 100.0);
        }
        else
        {
            d.actionType = ACT_CHECK;
            d.byAmount = 0;

            WriteLog("[user.cpp] OH PostFlop Decision: EnhancedPrWin nao confirmou recalc/callback e sem aposta para pagar -> Check seguro.\n");
        }

        return d;
    }

    OHEquityReadout eq = {};
    ReadOHEquityReadout(eq);
    SendOHEquitySnapshotToBridgeValues(eq);

    bool enhancedActuallyUsed = WasEnhancedPrWinCallbackUsedForCurrentProfile();
    g_lastPostFlopDecisionUsedEnhancedPrWin = enhancedActuallyUsed;

    if (!enhancedActuallyUsed)
    {
        LONG profileVersion = ReadInterlockedLong(&g_enhancedPrWinProfileVersion);
        LONG callbackVersion = ReadInterlockedLong(&g_enhancedPrWinLastCallbackVersion);
        LONG callbackCount = ReadInterlockedLong(&g_enhancedPrWinCallbackCount);

        if (facingBet)
        {
            d.actionType = ACT_FOLD;
            d.byAmount = amountToCall;

            WriteLog("[user.cpp] OH PostFlop Decision: perfil EnhancedPrWin atualizado, mas callback ainda nao executou para a versao atual -> Fold seguro. perfil=%ld callback=%ld totalCallbacks=%ld.\n",
                profileVersion, callbackVersion, callbackCount);
        }
        else
        {
            d.actionType = ACT_CHECK;
            d.byAmount = 0;

            WriteLog("[user.cpp] OH PostFlop Decision: perfil EnhancedPrWin atualizado, mas callback ainda nao executou para a versao atual -> Check seguro. perfil=%ld callback=%ld totalCallbacks=%ld.\n",
                profileVersion, callbackVersion, callbackCount);
        }

        return d;
    }

    double equity = 0.0;

    if (!TryComputeOHEquity01(eq, equity))
    {
        if (facingBet)
        {
            d.actionType = ACT_FOLD;
            d.byAmount = amountToCall;

            WriteLog("[user.cpp] OH PostFlop Decision: equity OH invalida -> Fold seguro. prwin=%.4f prtie=%.4f prlos=%.4f nhands=%.0f.\n",
                eq.prwin, eq.prtie, eq.prlos, eq.nhands);
        }
        else
        {
            d.actionType = ACT_CHECK;
            d.byAmount = 0;

            WriteLog("[user.cpp] OH PostFlop Decision: equity OH invalida e sem aposta para pagar -> Check seguro. prwin=%.4f prtie=%.4f prlos=%.4f nhands=%.0f.\n",
                eq.prwin, eq.prtie, eq.prlos, eq.nhands);
        }

        return d;
    }

    if (eq.nhands > 0.0 && eq.nhands < 100.0)
    {
        if (facingBet)
        {
            d.actionType = ACT_FOLD;
            d.byAmount = amountToCall;

            WriteLog("[user.cpp] OH PostFlop Decision: nhands insuficiente para decisao robusta -> Fold seguro. nhands=%.0f.\n",
                eq.nhands);
        }
        else
        {
            d.actionType = ACT_CHECK;
            d.byAmount = 0;

            WriteLog("[user.cpp] OH PostFlop Decision: nhands insuficiente para bet robusto -> Check. nhands=%.0f.\n",
                eq.nhands);
        }

        return d;
    }

    double potAfterCall = (double)potBeforeCall + (double)amountToCall;

    if (potAfterCall <= 0.0)
        potAfterCall = (double)amountToCall;

    double requiredEquity = facingBet
        ? ((double)amountToCall / potAfterCall)
        : 0.0;

    double callEV = facingBet
        ? ((equity * potAfterCall) - (double)amountToCall)
        : 0.0;

    d.byAmount = amountToCall;
    d.checkCallEV = (float)(callEV / 100.0);
    d.betRaiseEV = 0.0f;

    bool valueAggression =
        equity >= PostFlopValueThreshold(ohStreet, facingBet) &&
        (!facingBet || equity >= requiredEquity + PostFlopRaiseMargin(ohStreet));

    if (valueAggression)
    {
        int raiseToTotal = BuildPostFlopBetOrRaiseToTotalCents(amountToCall, potBeforeCall, equity);
        int heroBet = ReadChairCurrentBet(g_heroChair);
        int heroTotal = GetHeroTotalStackCents();
        int minUsefulTotal = facingBet ? heroBet + amountToCall + 1 : heroBet + 1;

        if (raiseToTotal >= heroTotal && heroTotal > heroBet)
        {
            d.actionType = ACT_ALLIN;
            d.byAmount = heroTotal;
        }
        else if (ShouldConvertPostFlopRaiseToAllIn(raiseToTotal))
        {
            d.actionType = ACT_ALLIN;
            d.byAmount = heroTotal;
        }
        else if (raiseToTotal >= minUsefulTotal)
        {
            d.actionType = facingBet ? ACT_RAISE : ACT_BET;
            d.byAmount = raiseToTotal;
        }

        if (d.actionType == ACT_BET || d.actionType == ACT_RAISE || d.actionType == ACT_ALLIN)
        {
            double aggressionScore = equity - (facingBet ? requiredEquity : PostFlopValueThreshold(ohStreet, false));
            d.betRaiseEV = (float)((aggressionScore * potAfterCall) / 100.0);

            WriteLog("[user.cpp] OH PostFlop Decision: %s. equity=%.4f required=%.4f callEV=$%.2f target=$%.2f pot=$%.2f call=$%.2f nhands=%.0f.\n",
                ActionName(d.actionType),
                equity,
                requiredEquity,
                callEV / 100.0,
                d.byAmount / 100.0,
                potBeforeCall / 100.0,
                amountToCall / 100.0,
                eq.nhands);

            return d;
        }
    }

    if (!facingBet)
    {
        d.actionType = ACT_CHECK;
        d.byAmount = 0;

        WriteLog("[user.cpp] OH PostFlop Decision: Check. equity=%.4f threshold=%.4f pot=$%.2f nhands=%.0f.\n",
            equity,
            PostFlopValueThreshold(ohStreet, false),
            potBeforeCall / 100.0,
            eq.nhands);

        return d;
    }

    if (callEV >= 0.0)
    {
        d.actionType = ACT_CALL;
        d.byAmount = amountToCall;

        WriteLog("[user.cpp] OH PostFlop Decision: Call. equity=%.4f required=%.4f callEV=$%.2f call=$%.2f pot=$%.2f nhands=%.0f.\n",
            equity, requiredEquity, callEV / 100.0, amountToCall / 100.0, potBeforeCall / 100.0, eq.nhands);
    }
    else
    {
        d.actionType = ACT_FOLD;
        d.byAmount = amountToCall;

        WriteLog("[user.cpp] OH PostFlop Decision: Fold. equity=%.4f required=%.4f callEV=$%.2f call=$%.2f pot=$%.2f nhands=%.0f.\n",
            equity, requiredEquity, callEV / 100.0, amountToCall / 100.0, potBeforeCall / 100.0, eq.nhands);
    }

    return d;
}

static const char* FetchTableTitleForBridge() {
    static char title[512];

    title[0] = '\0';

    const char* rawTitle = "";

    __try {
        rawTitle = GetTableTitle();
    }
    __except (1) {
        rawTitle = "";
    }

    if (rawTitle && rawTitle[0]) {
        snprintf(title, sizeof(title), "%s", rawTitle);
    }
    else {
        // Fallback defensivo. Não deve ser o caminho normal.
        // Se cair aqui, o XML provavelmente não será vinculado corretamente.
        snprintf(title, sizeof(title), "G5_NO_TABLE_TITLE");
    }

    WriteLog("[user.cpp] TableTitle para bridge: '%s'\n", title);
    return title;
}

// =============================================================================
// UTILITÁRIOS DE LOG
// =============================================================================
static const char* ActionName(int a) {
    switch (a) {
    case ACT_FOLD:  return "Fold";
    case ACT_CHECK: return "Check";
    case ACT_CALL:  return "Call";
    case ACT_BET:   return "Bet";
    case ACT_RAISE: return "Raise";
    case ACT_ALLIN: return "AllIn";
    default:        return "?";
    }
}

static const char* StreetName(int s) {
    switch (s) {
    case 1: return "preflop";
    case 2: return "flop";
    case 3: return "turn";
    case 4: return "river";
    default: return "?";
    }
}

static const char* PositionName(int logIdx, int buttonLogIdx, int numPlayers) {
    int dist = (logIdx - buttonLogIdx + numPlayers) % numPlayers;
    switch (numPlayers) {
    case 2: return dist == 0 ? "BTN/SB" : "BB";
    case 3:
        if (dist == 0) return "BTN";
        if (dist == 1) return "SB";
        return "BB";
    case 4:
        if (dist == 0) return "BTN";
        if (dist == 1) return "SB";
        if (dist == 2) return "BB";
        return "UTG";
    case 5:
        if (dist == 0) return "BTN";
        if (dist == 1) return "SB";
        if (dist == 2) return "BB";
        if (dist == 3) return "UTG";
        return "CO";
    default:
        if (dist == 0) return "BTN";
        if (dist == 1) return "SB";
        if (dist == 2) return "BB";
        if (dist == 3) return "UTG";
        if (dist == 4) return "HJ";
        if (dist == 5) return "CO";
        return "UTG+";
    }
}

static int g_buttonLogIdx = 0;

static void LogAction(const char* source, int chair, int logIdx, int actionType, int amountCents) {
    const char* pos = PositionName(logIdx, g_buttonLogIdx, g_numLogicalPlayers);
    if (amountCents > 0)
        WriteLog("[user.cpp] %s | %s (c%d idx%d) -> %s $%.2f\n",
            source, pos, chair, logIdx, ActionName(actionType), amountCents / 100.0);
    else
        WriteLog("[user.cpp] %s | %s (c%d idx%d) -> %s\n",
            source, pos, chair, logIdx, ActionName(actionType));
}

// =============================================================================
// CONVERSÃO DE CARTAS
// =============================================================================
static std::string FetchG5Card(const char* rankSym, const char* suitSym) {
    int r = (int)GetSymbol(rankSym);
    int s = (int)GetSymbol(suitSym);
    const char* ranks = "??23456789TJQKA";
    const char  suits[] = { 'h', 'd', 'c', 's' };
    if (r < 2 || r > 14 || s < 0 || s > 3) return "";
    std::string result;
    result += ranks[r];
    result += suits[s];
    return result;
}

// =============================================================================
// WRAPPERS SEGUROS
// =============================================================================
static void SafeNewAction(int playerIndex, int actionType, int byAmount) {
    __try { if (pNewAction) pNewAction(playerIndex, actionType, byAmount); }
    __except (1) { WriteLog("[user.cpp] EXCECAO em pNewAction (idx %d)\n", playerIndex); }
}

static DecisionResult SafeGetDecision() {
    DecisionResult d = {};
    __try { if (pGetDecision) d = pGetDecision(); }
    __except (1) { WriteLog("[user.cpp] EXCECAO em pGetDecision\n"); d.actionType = ACT_FOLD; }
    return d;
}

static void SafeGoToNextStreet(const char* c0, const char* c1, const char* c2, int n) {
    __try { if (pGoToNextStreet) pGoToNextStreet(c0, c1, c2, n); }
    __except (1) { WriteLog("[user.cpp] EXCECAO em pGoToNextStreet\n"); }
}

static void SafeDealHoleCards(const char* c0, const char* c1) {
    __try { if (pDealHoleCards) pDealHoleCards(c0, c1); }
    __except (1) {}
}

// =============================================================================
// PASSO 1: INICIALIZAÇÃO DA MÃO 
// =============================================================================
static bool Step_InitNewHand(int heroChair, int buttonChair, int bigBlindIn) {
    WriteLog("[user.cpp] ========================================\n");

    if (heroChair < 0 || heroChair >= 10) {
        WriteLog("[user.cpp] ERRO: userchair invalido (%d). Ignorando mao ate proximo estado valido.\n", heroChair);
        return false;
    }

    if (buttonChair < 0 || buttonChair >= 10) {
        WriteLog("[user.cpp] ERRO: dealerchair invalido (%d). Ignorando mao ate proximo estado valido.\n", buttonChair);
        return false;
    }

    memset(g_chairToLogical, -1, sizeof(g_chairToLogical));
    memset(g_isActive, 0, sizeof(g_isActive));
    memset(g_lastPlayerBets, 0, sizeof(g_lastPlayerBets));
    memset(g_hasCheckedThisStreet, 0, sizeof(g_hasCheckedThisStreet));
    memset(g_wentAllIn, 0, sizeof(g_wentAllIn));
    g_streetEndMaxBet = 0;
    g_heroActedThisStreet = false;
    g_heroRegistered = false;
    g_cachedDecision = 0.0;
    g_cachedActionType = ACT_FOLD;
    g_cachedByAmount = 0;

    // -- Detecta quem recebeu cartas nesta mão --------------------------------
    // playersdealtbits: bit N ligado = cadeira N recebeu cartas.
    // Isso exclui jogadores sentados em sit-out ou aguardando big blind.
    // Fallback para balance/currentbet se o símbolo retornar 0 (início de sessão).
    int dealtBits = (int)GetSymbol("playersdealtbits");
    bool useDealtBits = (dealtBits != 0);
    WriteLog("[user.cpp] playersdealtbits=0x%X (%s)\n",
        dealtBits, useDealtBits ? "usando" : "fallback para balance/bet");

    // 1. Coleta jogadores que receberam cartas, começando do Botão
    int activeChairs[10];
    int numActive = 0;

    for (int offset = 0; offset < 10; offset++) {
        int c = (buttonChair + offset) % 10;

        bool isActive;
        if (useDealtBits) {
            isActive = ((dealtBits >> c) & 1) != 0;
        }
        else {
            char symBal[32], symBet[32];
            sprintf(symBal, "balance%d", c);
            sprintf(symBet, "currentbet%d", c);
            isActive = (ReadCents(symBal) > 0 || ReadCents(symBet) > 0);
        }

        if (isActive)
            activeChairs[numActive++] = c;
    }

    int sbChair = -1, bbChair = -1;

    // 2. Regra de Posição (HU vs Normal)
    if (numActive == 2) {
        // HU: o Botão É o Small Blind
        sbChair = activeChairs[0];
        bbChair = activeChairs[1];
    }
    else if (numActive > 2) {
        sbChair = activeChairs[1];
        bbChair = activeChairs[2];
    }

    // 3. Big blind real
    // Fonte primária: símbolo bblind do OpenHoldem, recebido como bigBlindIn.
    // O currentbet da cadeira presumida como BB serve apenas para log diagnóstico,
    // nunca para sobrescrever o bblind.
    int trueBB = bigBlindIn;

    if (bbChair >= 0) {
        char symBet[32];
        sprintf_s(symBet, sizeof(symBet), "currentbet%d", bbChair);
        int postedByPresumedBB = ReadCents(symBet);

        if (postedByPresumedBB > 0 && postedByPresumedBB != trueBB) {
            WriteLog("[user.cpp] AVISO BB: bblind=%d ($%.2f), currentbet do BB presumido c%d=%d ($%.2f). Mantendo bblind.\n",
                trueBB, trueBB / 100.0,
                bbChair,
                postedByPresumedBB, postedByPresumedBB / 100.0);
        }
    }

    if (trueBB <= 0) {
        WriteLog("[user.cpp] ERRO: bblind invalido (%d). Usando 1 centavo como fallback defensivo.\n", trueBB);
        trueBB = 1;
    }

    WriteLog("[user.cpp] NOVA MÃO | heroChair=%d btnChair=%d trueBB=%d ($%.2f) | %d jogadores dealt\n",
        heroChair, buttonChair, trueBB, trueBB / 100.0, numActive);

    int logicalStacks[10] = {};
    int logicalChairs[10] = {};
    int heroLogical = -1;
    int buttonLogical = -1;
    int logIdx = 0;

    // 4. Registra apenas os jogadores que receberam cartas
    for (int offset = 0; offset < 10; offset++) {
        int c = (buttonChair + offset) % 10;

        // Mesma lógica de filtro do passo 1
        bool isActive;
        if (useDealtBits) {
            isActive = ((dealtBits >> c) & 1) != 0;
        }
        else {
            char symBal[32], symBet[32];
            sprintf_s(symBal, sizeof(symBal), "balance%d", c);
            sprintf_s(symBet, sizeof(symBet), "currentbet%d", c);
            isActive = (ReadCents(symBal) > 0 || ReadCents(symBet) > 0);
        }

        if (!isActive) continue;

        int bal = ReadChairBalance(c);
        int bet = ReadChairCurrentBet(c);

        g_chairToLogical[c] = logIdx;
        g_isActive[c] = true;
        logicalStacks[logIdx] = bal + bet;
        logicalChairs[logIdx] = c;

        if (c == sbChair)
            g_lastPlayerBets[c] = (trueBB / 2 > 0) ? (trueBB / 2) : 1;
        else if (c == bbChair)
            g_lastPlayerBets[c] = trueBB;
        else
            g_lastPlayerBets[c] = 0;

        if (c == heroChair)   heroLogical = logIdx;
        if (c == buttonChair) { buttonLogical = logIdx; g_buttonLogIdx = logIdx; }

        WriteLog("[user.cpp]   %s (c%d idx%d) stack=$%.2f bet=$%.2f lastBet=%d%s\n",
            PositionName(logIdx, buttonLogical >= 0 ? buttonLogical : 0, logIdx + 1),
            c, logIdx, (bal + bet) / 100.0, bet / 100.0, g_lastPlayerBets[c],
            (c == sbChair ? " [SB]" : (c == bbChair ? " [BB]" : "")));
        logIdx++;
    }
    g_buttonLogIdx = buttonLogical >= 0 ? buttonLogical : 0;
    g_numLogicalPlayers = logIdx;

    if (heroLogical < 0 || g_numLogicalPlayers < 2) {
        WriteLog("[user.cpp] FALHA: herói não encontrado ou mesa vazia.\n");
        return false;
    }

    g_lastEvaluatedChair = (bbChair >= 0) ? bbChair : buttonChair;
    g_streetEndMaxBet = trueBB;

    __try {
        const char* tableTitle = FetchTableTitleForBridge();

        if (pNewHand) pNewHand(logicalStacks, logicalChairs, heroLogical, buttonLogical,
            g_numLogicalPlayers, trueBB, g_g5BasePath, tableTitle);
    }
    __except (1) { WriteLog("[user.cpp] EXCECAO em pNewHand\n"); }

    WriteLog("[user.cpp] Inicializado: %d jogadores | herói=%s | btn=%s\n",
        g_numLogicalPlayers,
        PositionName(heroLogical, buttonLogical, g_numLogicalPlayers),
        PositionName(buttonLogical, buttonLogical, g_numLogicalPlayers));
    return true;
}

// =============================================================================
// PASSO 2: PROCESSA AÇÕES DE UM SEGMENTO
// =============================================================================
static bool Step_ProcessSegment(int stopChair, int targetStreet)
{
    if (targetStreet < 1) return false;

    const char* sName = StreetName(targetStreet);
    char sym[64];
    const char* streetSuffix = (targetStreet == 1) ? "preflop"
        : (targetStreet == 2) ? "flop"
        : (targetStreet == 3) ? "turn" : "river";

    sprintf_s(sym, sizeof(sym), "foldbits_%s", streetSuffix);  int foldBits = (int)GetSymbol(sym);
    sprintf_s(sym, sizeof(sym), "callbits_%s", streetSuffix);  int callBits = (int)GetSymbol(sym);
    sprintf_s(sym, sizeof(sym), "raisbits_%s", streetSuffix);  int raiseBits = (int)GetSymbol(sym);

    int playingBits = (int)GetSymbol("playersplayingbits");

    WriteLog("[user.cpp] ProcessSegment [%s] | c%d->c%d | fold=0x%X call=0x%X raise=0x%X\n",
        sName, (g_lastEvaluatedChair + 1) % 10, stopChair,
        foldBits, callBits, raiseBits);

    bool acted = false;
    int  c = (g_lastEvaluatedChair + 1) % 10;
    int  safetyGuard = 0;
    int  facingBet = g_streetEndMaxBet;

    while (c != stopChair && safetyGuard < 12)
    {
        safetyGuard++;

        if (c == g_heroChair) { c = (c + 1) % 10; continue; }

        if (!g_isActive[c] || g_chairToLogical[c] < 0) {
            c = (c + 1) % 10;
            continue;
        }

        if (g_wentAllIn[c]) {
            g_lastEvaluatedChair = c;
            c = (c + 1) % 10;
            continue;
        }

        bool isPlaying = (playingBits >> c) & 1;
        bool didFold = ((foldBits >> c) & 1) || !isPlaying;

        bool didCall = (callBits >> c) & 1;
        bool didRaise = (raiseBits >> c) & 1;

        // É impossível dar Call se ninguém apostou!
        if (facingBet == 0) didCall = false;

        sprintf(sym, "currentbet%d", c); int currentBet = ReadCents(sym);
        sprintf(sym, "balance%d", c); int bal = ReadCents(sym);
        int delta = currentBet - g_lastPlayerBets[c];

        // Se o OpenHoldem diz que houve ação, mas o valor ainda não atualizou na tela:
        if ((didRaise || didCall) && delta <= 0) {
            if (didCall && facingBet > g_lastPlayerBets[c]) {
                delta = facingBet - g_lastPlayerBets[c]; // O Call é exatamente o que falta para cobrir a aposta
            }
            else if (didRaise) {
                delta = currentBet > 0 ? currentBet : 1;
            }
            else {
                delta = 0;
            }
        }

        WriteLog("[user.cpp]   %s c%d: curBet=%d last=%d delta=%d bal=%d facingBet=%d bits(f%d c%d r%d p%d)\n",
            PositionName(g_chairToLogical[c], g_buttonLogIdx, g_numLogicalPlayers),
            c, currentBet, g_lastPlayerBets[c], delta, bal, facingBet,
            didFold, didCall, didRaise, isPlaying);

        bool mathFold = (!didCall && !didRaise && !didFold
            && delta == 0
            && facingBet > 0
            && currentBet < facingBet
            && bal > 0
            && !isPlaying);

        // 1. FOLD
        if (didFold || mathFold)
        {
            SafeNewAction(g_chairToLogical[c], ACT_FOLD, 0);
            g_isActive[c] = false;
            acted = true;
            LogAction(mathFold ? "Fold(deduzido)" : "Fold(bit)", c, g_chairToLogical[c], ACT_FOLD, 0);
        }
        // 2. AÇÃO COM FICHAS
        else if (didRaise || didCall || delta > 0)
        {
            G5Action act;

            if (bal == 0 && currentBet > 0) {
                act = ACT_ALLIN;
                g_wentAllIn[c] = true;
            }
            else if (didRaise || currentBet > facingBet) {
                if (facingBet == 0 && targetStreet > 1)
                    act = ACT_BET;
                else
                    act = ACT_RAISE;
            }
            else {
                act = ACT_CALL;
            }

            g_lastPlayerBets[c] = currentBet;
            if (currentBet > facingBet) facingBet = currentBet;

            SafeNewAction(g_chairToLogical[c], (int)act, delta);
            acted = true;
            LogAction((didRaise || didCall) ? "Ação(bit)" : "Ação(delta)", c, g_chairToLogical[c], (int)act, delta);
        }
        // 3. CHECK
        else if (delta == 0 && bal > 0 && !g_hasCheckedThisStreet[c])
        {
            if (currentBet >= facingBet)
            {
                SafeNewAction(g_chairToLogical[c], ACT_CHECK, 0);
                g_hasCheckedThisStreet[c] = true;
                acted = true;
                LogAction("Check(deduzido)", c, g_chairToLogical[c], ACT_CHECK, 0);
            }
        }

        g_lastEvaluatedChair = c;
        c = (c + 1) % 10;
    }

    if (facingBet > g_streetEndMaxBet)
        g_streetEndMaxBet = facingBet;

    return acted;
}

// =============================================================================
// PASSO 3: RESOLVE AÇÕES PENDENTES DO FINAL DA STREET ANTERIOR
// =============================================================================
static void Step_ResolveEndOfPrevStreet(int oldStreet, int savedMaxBet)
{
    if (savedMaxBet <= 0)
        WriteLog("[user.cpp] ResolveEndOfStreet [%s] sem apostas\n", StreetName(oldStreet));
    else
        WriteLog("[user.cpp] ResolveEndOfStreet [%s] savedMaxBet=%d ($%.2f)\n",
            StreetName(oldStreet), savedMaxBet, savedMaxBet / 100.0);

    char sym[64];
    sprintf(sym, "foldbits_%s", StreetName(oldStreet));
    int foldBitsOld = (int)GetSymbol(sym);

    int startFrom = (g_lastEvaluatedChair + 1) % 10;
    int c = startFrom;
    int guard = 0;

    while (guard < 12)
    {
        guard++;

        if (c == g_heroChair) {
            c = (c + 1) % 10;
            if (c == startFrom) break;
            continue;
        }

        if (g_isActive[c] && g_chairToLogical[c] >= 0 && !g_wentAllIn[c])
        {
            // Ignoramos o !isPlaying aqui, porque o jogador pode ter foldado na NOVA street.
            // Confiamos apenas na memória oficial de folds da street passada.
            bool didFold = (foldBitsOld >> c) & 1;

            int  lastBet = g_lastPlayerBets[c];
            sprintf(sym, "balance%d", c);
            int bal = ReadCents(sym);

            if (didFold)
            {
                SafeNewAction(g_chairToLogical[c], ACT_FOLD, 0);
                g_isActive[c] = false;
                LogAction("Fold(pendente/bit)", c, g_chairToLogical[c], ACT_FOLD, 0);
            }
            else if (savedMaxBet > 0 && lastBet < savedMaxBet)
            {
                int callAmount = savedMaxBet - lastBet;
                G5Action act = (bal == 0) ? ACT_ALLIN : ACT_CALL;
                if (bal == 0) g_wentAllIn[c] = true;
                SafeNewAction(g_chairToLogical[c], (int)act, callAmount);
                g_lastPlayerBets[c] = savedMaxBet;
                LogAction("Call(pendente)", c, g_chairToLogical[c], (int)act, callAmount);
            }
            else if (lastBet >= savedMaxBet && !g_hasCheckedThisStreet[c])
            {
                SafeNewAction(g_chairToLogical[c], ACT_CHECK, 0);
                g_hasCheckedThisStreet[c] = true;
                LogAction("Check(pendente)", c, g_chairToLogical[c], ACT_CHECK, 0);
            }
        }

        g_lastEvaluatedChair = c;
        c = (c + 1) % 10;
        if (c == startFrom) break;
    }
}

// =============================================================================
// PASSO 4: TRANSIÇÃO DE STREET
// =============================================================================
static void Step_AdvanceStreetIfNeeded(int ohStreet)
{
    if (ohStreet <= g_currentStreet) return;

    std::string c0, c1, c2, c3;
    if (ohStreet == 2) {
        c0 = FetchG5Card("$$cr0", "$$cs0");
        c1 = FetchG5Card("$$cr1", "$$cs1");
        c2 = FetchG5Card("$$cr2", "$$cs2");
        if (c0.empty() || c1.empty() || c2.empty()) {
            WriteLog("[user.cpp] AVISO: Cartas do flop ainda não disponíveis.\n");
            return;
        }
    }
    else if (ohStreet == 3) {
        c3 = FetchG5Card("$$cr3", "$$cs3");
        if (c3.empty()) { WriteLog("[user.cpp] AVISO: Turn ainda não disponível.\n"); return; }
    }
    else if (ohStreet == 4) {
        c3 = FetchG5Card("$$cr4", "$$cs4");
        if (c3.empty()) { WriteLog("[user.cpp] AVISO: River ainda não disponível.\n"); return; }
    }

    WriteLog("[user.cpp] ----------------------------------------\n");
    WriteLog("[user.cpp] NOVA STREET: %s -> %s\n",
        StreetName(g_currentStreet), StreetName(ohStreet));

    int savedMaxBet = g_streetEndMaxBet;

    if (g_heroActedThisStreet && !g_heroRegistered) {
        char symBet[32], symBal[32];
        sprintf(symBet, "currentbet%d", g_heroChair);
        sprintf(symBal, "balance%d", g_heroChair);
        int heroBet = ReadCents(symBet);
        int heroBal = ReadCents(symBal);
        int heroDelta = heroBet - g_lastPlayerBets[g_heroChair];

        G5Action realAction;
        int realAmount = heroDelta > 0 ? heroDelta : g_cachedByAmount;

        if (heroBal == 0 && heroBet > 0) {
            realAction = ACT_ALLIN;
            g_wentAllIn[g_heroChair] = true;
        }
        else if (heroDelta > 0 && savedMaxBet > 0) {
            realAction = (heroBet == savedMaxBet) ? ACT_CALL : ACT_RAISE;
        }
        else if (heroDelta > 0) {
            realAction = (G5Action)g_cachedActionType;
        }
        else if (g_cachedActionType == ACT_CHECK) {
            realAction = ACT_CHECK;
            realAmount = 0;
        }
        else {
            realAction = (G5Action)g_cachedActionType;
        }

        g_lastPlayerBets[g_heroChair] += realAmount;
        if (g_lastPlayerBets[g_heroChair] > savedMaxBet)
            savedMaxBet = g_lastPlayerBets[g_heroChair];

        SafeNewAction(g_chairToLogical[g_heroChair], (int)realAction, realAmount);
        g_heroRegistered = true;
        g_lastEvaluatedChair = g_heroChair;
        LogAction("Herói(troca-street)", g_heroChair, g_chairToLogical[g_heroChair],
            (int)realAction, realAmount);
    }

    Step_ResolveEndOfPrevStreet(g_currentStreet, savedMaxBet);

    if (ohStreet == 2)
        SafeGoToNextStreet(c0.c_str(), c1.c_str(), c2.c_str(), 3);
    else
        SafeGoToNextStreet(c3.c_str(), "", "", 1);

    WriteLog("[user.cpp] %s enviado ao G5.\n", StreetName(ohStreet));

    g_currentStreet = ohStreet;
    g_streetEndMaxBet = 0;
    g_heroActedThisStreet = false;
    g_heroRegistered = false;
    memset(g_lastPlayerBets, 0, sizeof(g_lastPlayerBets));
    memset(g_hasCheckedThisStreet, 0, sizeof(g_hasCheckedThisStreet));

    // 🏆 O relógio pós-flop! Recua para o Botão (SB) primeiro em HU!
    g_lastEvaluatedChair = g_buttonChair;
}

// =============================================================================
// PASSO 5: REGISTRA AÇÃO DO HERÓI
// =============================================================================
static bool Step_TryRegisterHeroAction()
{
    if (!g_heroActedThisStreet || g_heroRegistered || g_heroChair < 0) return false;
    if (g_chairToLogical[g_heroChair] < 0) return false;

    char sym[32];
    sprintf(sym, "currentbet%d", g_heroChair);
    int heroBet = ReadCents(sym);
    int heroDelta = heroBet - g_lastPlayerBets[g_heroChair];

    if (heroDelta <= 0 && g_cachedActionType != ACT_CHECK) return false;

    G5Action act = (G5Action)g_cachedActionType;
    sprintf(sym, "balance%d", g_heroChair);
    if (ReadCents(sym) == 0 && heroBet > 0) {
        act = ACT_ALLIN;
        g_wentAllIn[g_heroChair] = true;
    }

    g_lastPlayerBets[g_heroChair] = heroBet;
    SafeNewAction(g_chairToLogical[g_heroChair], (int)act, heroDelta);
    g_heroRegistered = true;
    g_lastEvaluatedChair = g_heroChair;

    if (heroBet > g_streetEndMaxBet) g_streetEndMaxBet = heroBet;

    LogAction("Herói(executou)", g_heroChair, g_chairToLogical[g_heroChair],
        (int)act, heroDelta);
    return true;
}

// =============================================================================
// INICIALIZAÇÃO DA BRIDGE
// =============================================================================
static void InitializeBridge() {
    if (g_bridgeLoaded) return;

    char dllPath[MAX_PATH];
    sprintf(dllPath, "%sG5Bridge.dll", g_g5BasePath);

    hBridge = LoadLibraryA(dllPath);
    if (!hBridge) { WriteLog("[user.cpp] ERRO: %s não encontrada.\n", dllPath); return; }

    pNewHand = (FN_NewHand)GetProcAddress(hBridge, "G5Bridge_NewHand");
    pDealHoleCards = (FN_DealHoleCards)GetProcAddress(hBridge, "G5Bridge_DealHoleCards");
    pNewAction = (FN_NewAction)GetProcAddress(hBridge, "G5Bridge_NewAction");
    pGoToNextStreet = (FN_GoToNextStreet)GetProcAddress(hBridge, "G5Bridge_GoToNextStreet");
pGetDecision = (FN_GetDecision)GetProcAddress(hBridge, "G5Bridge_GetDecision");
pUpdateEnhancedPrWinProfile = (FN_UpdateEnhancedPrWinProfile)GetProcAddress(hBridge, "G5Bridge_UpdateEnhancedPrWinProfile");
pSetOHEquitySnapshot = (FN_SetOHEquitySnapshot)GetProcAddress(hBridge, "G5Bridge_SetOHEquitySnapshot");

if (pNewHand && pDealHoleCards && pNewAction && pGoToNextStreet && pGetDecision) {
    g_bridgeLoaded = true;
    WriteLog("[user.cpp] G5Bridge carregada com sucesso.\n");

    if (pUpdateEnhancedPrWinProfile && pSetOHEquitySnapshot)
        WriteLog("[user.cpp] EnhancedPrWin: exports do G5Bridge encontradas.\n");
    else
        WriteLog("[user.cpp] EnhancedPrWin: exports ainda nao encontradas; aguardando Fase 2 do G5Bridge.\n");
}
    else {
        WriteLog("[user.cpp] ERRO: funções não encontradas na G5Bridge.dll.\n");
        FreeLibrary(hBridge); hBridge = NULL;
    }
}

// =============================================================================
// PROCESS_MESSAGE DO OPENHOLDEM
//
// Esta é a interface nativa esperada pelo OpenHoldem para entregar:
//   - state    -> estado raspado da mesa;
//   - phl1k    -> versus-lists;
//   - prw1326  -> estrutura Enhanced PrWin;
//   - pfgws    -> ponteiro para consulta de símbolos;
//   - query    -> consulta de dll$symbols.
//
// A Fase 2 depende de prw1326. Portanto, se esta função não for chamada pelo OH,
// o pós-flop nunca usará ranges do G5 no Enhanced PrWin.
// =============================================================================
static void LogOpenHoldemMessageOnce(const char* message, bool& flag)
{
    if (flag)
        return;

    flag = true;
    WriteLog("[user.cpp] process_message(%s) recebido do OpenHoldem.\n", message);
}

static double HandleOpenHoldemMessage(const char* message, const void* param)
{
    if (!message)
        return 0.0;

    if (strcmp(message, "load") == 0)
    {
        LogOpenHoldemMessageOnce("load", g_loggedMsgLoad);
        return 0.0;
    }

    if (strcmp(message, "unload") == 0)
    {
        LogOpenHoldemMessageOnce("unload", g_loggedMsgUnload);
        return 0.0;
    }

    if (strcmp(message, "state") == 0)
    {
        if (param)
            g_lastOHStatePtr = param;

        LogOpenHoldemMessageOnce("state", g_loggedMsgState);
        return 0.0;
    }

    if (strcmp(message, "phl1k") == 0)
    {
        if (param)
            g_phl1kPtr = param;

        LogOpenHoldemMessageOnce("phl1k", g_loggedMsgPhl1k);
        return 0.0;
    }

    if (strcmp(message, "pfgws") == 0)
    {
        if (param)
            g_pfgws = (pfgws_t)param;

        LogOpenHoldemMessageOnce("pfgws", g_loggedMsgPfgws);
        return 0.0;
    }

    if (strcmp(message, "prw1326") == 0)
    {
        LogOpenHoldemMessageOnce("prw1326", g_loggedMsgPrw1326);

        if (!param)
        {
            WriteLog("[user.cpp] EnhancedPrWin: process_message(prw1326) veio com param nulo.\n");
            return 0.0;
        }

        InstallEnhancedPrWinPointer(param);
        return 0.0;
    }

    if (strcmp(message, "query") == 0)
    {
        LogOpenHoldemMessageOnce("query", g_loggedMsgQuery);

        if (!param)
            return 0.0;

        return ProcessQuery((const char*)param);
    }

    return 0.0;
}

extern "C" __declspec(dllexport) double process_message(const char* message, const void* param)
{
    return HandleOpenHoldemMessage(message, param);
}

extern "C" __declspec(dllexport) double process_query(const char* pquery)
{
    return ProcessQuery(pquery);
}

// =============================================================================
// STUBS DO OPENHOLDEM
// =============================================================================
void DLLOnLoad() {} void DLLOnUnLoad() {}
void __stdcall DLLUpdateOnNewFormula() {}
void __stdcall DLLUpdateOnConnection() {}
void __stdcall DLLUpdateOnHandreset() {}
void __stdcall DLLUpdateOnNewRound() {}
void __stdcall DLLUpdateOnMyTurn() {}
void __stdcall DLLUpdateOnHeartbeat() {}

// =============================================================================
// ProcessQuery — ÚNICA ROTA
// =============================================================================
DLL_IMPLEMENTS double __stdcall ProcessQuery(const char* pquery)
{
    if (!pquery) return 0;

    if (strncmp(pquery, "dll$betsize", 11) == 0)
        return (double)g_cachedByAmount;

    InitializeBridge();
    if (!g_bridgeLoaded) return 0;
    if (strncmp(pquery, "dll$decisao", 11) != 0) return 0;

    int heroChair = (int)GetSymbol("userchair");
    int buttonChair = (int)GetSymbol("dealerchair");
    int bigBlind = ReadCents("bblind");
    int ohStreet = (int)GetSymbol("betround");

    WriteLog("[user.cpp] == dll$decisao | %s | heroChair=%d btnChair=%d bb=%d ($%.2f) ==\n",
        StreetName(ohStreet), heroChair, buttonChair, bigBlind, bigBlind / 100.0);

    bool isNewHand = !g_isHandActive
        || (ohStreet == 1 && g_currentStreet > 1)
        || (buttonChair != g_buttonChair && g_isHandActive);

    if (isNewHand) {
        g_isHandActive = false;
        g_cardsDealt = false;
        g_currentStreet = 0;
        g_heroChair = heroChair;
        g_buttonChair = buttonChair;

        if (!Step_InitNewHand(heroChair, buttonChair, bigBlind)) return 0.0;

        g_isHandActive = true;
        g_currentStreet = 1;
    }

    if (!g_cardsDealt && pDealHoleCards) {
        auto hc0 = FetchG5Card("$$pr0", "$$ps0");
        auto hc1 = FetchG5Card("$$pr1", "$$ps1");
        if (!hc0.empty() && !hc1.empty()) {
            SafeDealHoleCards(hc0.c_str(), hc1.c_str());
            g_cardsDealt = true;
            WriteLog("[user.cpp] Cartas do herói: %s %s\n", hc0.c_str(), hc1.c_str());
        }
    }

Step_AdvanceStreetIfNeeded(ohStreet);
bool heroJustRegistered = Step_TryRegisterHeroAction();
bool villainActed = Step_ProcessSegment(g_heroChair, ohStreet);

if (g_heroActedThisStreet && !villainActed && !heroJustRegistered) {
    WriteLog("[user.cpp] Cache: nenhuma acao nova -> retornando %.0f\n", g_cachedDecision);
    return g_cachedDecision;
}

DecisionResult decision = {};

if (IsPostFlopStreet(ohStreet))
{
    decision = BuildPostFlopDecisionFromEnhancedPrWin(ohStreet);
}
else
{
    decision = SafeGetDecision();
}

    std::string finalAction;
    double returnValue = 0.0;
    switch (decision.actionType) {
    case ACT_FOLD:  finalAction = "Fold";   returnValue = 0.0; break;
    case ACT_CHECK: finalAction = "Check";  returnValue = 1.0; break;
    case ACT_CALL:  finalAction = "Call";   returnValue = 2.0; break;
    case ACT_BET:   finalAction = "Raise";  returnValue = 3.0; break;
    case ACT_RAISE: finalAction = "Raise";  returnValue = 4.0; break;
    case ACT_ALLIN: {
        int heroBalNow = ReadChairBalance(g_heroChair);
        int heroBetNow = ReadChairCurrentBet(g_heroChair);

        int intended = decision.byAmount;

        // Dependendo da origem da decisão, byAmount pode representar:
        // 1) valor a colocar agora; ou
        // 2) alvo total após a ação.
        //
        // Para validar o all-in, aceitamos proximidade com qualquer uma das duas leituras.
        int realAllInDelta = heroBalNow;
        int realAllInTotal = heroBalNow + heroBetNow;

        bool validAllIn =
            IsAtLeast66PercentOf(intended, realAllInDelta) ||
            IsAtLeast66PercentOf(intended, realAllInTotal);

        if (validAllIn) {
            finalAction = "BetMax";
            returnValue = 5.0;

            WriteLog("[user.cpp] ALLIN VALIDADO 66%%: intended=%d ($%.2f), balance c%d=%d ($%.2f), currentbet c%d=%d ($%.2f), total=%d ($%.2f).\n",
                intended, intended / 100.0,
                g_heroChair, heroBalNow, heroBalNow / 100.0,
                g_heroChair, heroBetNow, heroBetNow / 100.0,
                realAllInTotal, realAllInTotal / 100.0);
        }
        else {
            // O G5 achou que era all-in no estado interno dele,
            // mas isso não bate com o stack real do OH.
            // Então executamos como raise/bet normal do valor pretendido.
            finalAction = "Raise";
            returnValue = 4.0;

            WriteLog("[user.cpp] ALLIN BLOQUEADO 66%%: intended=%d ($%.2f), balance c%d=%d ($%.2f), currentbet c%d=%d ($%.2f), total=%d ($%.2f). Convertendo para Raise.\n",
                intended, intended / 100.0,
                g_heroChair, heroBalNow, heroBalNow / 100.0,
                g_heroChair, heroBetNow, heroBetNow / 100.0,
                realAllInTotal, realAllInTotal / 100.0);
        }

        break;
    }
    default:        finalAction = "Fold";   returnValue = 0.0; break;
    }

    g_cachedDecision = returnValue;
    g_cachedActionType = decision.actionType;
    g_cachedByAmount = decision.byAmount;
    g_heroActedThisStreet = true;
    g_heroRegistered = false;

    const char* decisionOrigin = "G5 Preflop";

    if (IsPostFlopStreet(ohStreet))
        decisionOrigin = g_lastPostFlopDecisionUsedEnhancedPrWin
            ? "OH EnhancedPrWin confirmado"
            : "OH EnhancedPrWin nao confirmado";

    WriteLog("[user.cpp] user.dll decidiu: %s (amount=$%.2f | ccEV=%.2f brEV=%.2f | origem=%s) -> %.0f\n",
        ActionName(decision.actionType), decision.byAmount / 100.0,
        decision.checkCallEV, decision.betRaiseEV,
        decisionOrigin,
        returnValue);

    if (finalAction == "Raise") {
        int bb = ReadCents("bblind");

        if (bb <= 0) {
            WriteLog("[user.cpp] ERRO: bblind invalido (%d). Bloqueando Raise e retornando Call.\n", bb);
            g_cachedDecision = GetSymbol("Call");
            return g_cachedDecision;
        }

        int heroMoneyInPot = g_lastPlayerBets[g_heroChair];
        int raiseToTotal = decision.byAmount;

        if (raiseToTotal <= 0) {
            WriteLog("[user.cpp] ERRO: raiseToTotal invalido (%d). Bloqueando Raise e retornando Call.\n", raiseToTotal);
            g_cachedDecision = GetSymbol("Call");
            return g_cachedDecision;
        }

        returnValue = (double)raiseToTotal / (double)bb;

        WriteLog("[user.cpp] Raise: byAmount=$%.2f + heroInPot=$%.2f = RaiseTo=$%.2f (%.2f BBs)\n",
            decision.byAmount / 100.0, heroMoneyInPot / 100.0,
            raiseToTotal / 100.0, returnValue);

        g_cachedDecision = returnValue;
        return returnValue;
    }

    return GetSymbol(finalAction.c_str());
}

// =============================================================================
// DllMain
// =============================================================================
BOOL APIENTRY DllMain(HANDLE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        InitializeOpenHoldemFunctionInterface();

        // Pega o caminho absoluto de onde a user.dll está rodando
        GetModuleFileNameA((HMODULE)hModule, g_g5BasePath, MAX_PATH);

        // Remove "user.dll" do final do caminho e mantém a barra '\'
        char* lastSlash = strrchr(g_g5BasePath, '\\');
        if (lastSlash) *(lastSlash + 1) = '\0';
    }
    return TRUE;
}
