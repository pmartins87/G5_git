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
// Em Win32, funÃ§Ãµes C/C++ podem sair decoradas no export table. O OpenHoldem
// procura a entrada canonica "process_message". Esta diretiva garante que,
// alÃ©m do nome decorado gerado pelo compilador, a DLL tambÃ©m exponha exatamente
// o nome que o OH espera.
//
// Mantemos tambÃ©m process_query como rota compatÃ­vel com os exemplos oficiais,
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
    ACT_BET = 3, ACT_RAISE = 4, ACT_ALLIN = 5,
    ACT_NOACTION = 8
};

// =============================================================================
// ROTA DE DECISAO
//
// O pos-flop nao usa mais equity do OpenHoldem do OpenHoldem.
// A user.dll apenas sincroniza o estado raspado do OH e delega a decisao para
// G5Bridge_GetDecision, que chama BotGameState/DecisionMaking.dll.
// =============================================================================
static const char* StreetName(int s);
static const char* ActionName(int a);
DLL_IMPLEMENTS double __stdcall ProcessQuery(const char* pquery);
extern "C" __declspec(dllexport) double process_query(const char* pquery);

// =============================================================================
// SÍMBOLOS OPENHOLDEM / OPENPPL — CAMADA ESTRITA
// =============================================================================
// OpenHoldem expõe GetSymbol(const char*), mas a API não oferece, para a user.dll,
// uma função pública "SymbolExists". A blindagem robusta aqui é dupla:
//   1) nunca chamar GetSymbol para nomes fora de uma allowlist/padrão auditado;
//   2) não resolver constantes de ação OpenPPL por GetSymbol; usamos os valores
//      oficiais da OpenPPL Library para evitar fallback silencioso.
//
// Fontes auditadas:
//   - OpenHoldem Manual: símbolos $$pc/$$pr/$$ps, $$cc/$$cr/$$cs, betround,
//     bblind, pot, call, userchair, dealerchair etc.
//   - Código OH: foldbits1..4, callbits1..4, raisbits1..4.
//   - OpenPPL Library: Calls, Raises e constantes de ação.
// =============================================================================
static const double OPPL_CHECK = 0.0;
static const double OPPL_BEEP = -1000000.0;
static const double OPPL_FOLD = -1000001.0;
static const double OPPL_RAISE_THIRD_POT = -1000004.0;
static const double OPPL_RAISE_HALF_POT = -1000005.0;
static const double OPPL_RAISE_TWO_THIRD_POT = -1000006.0;
static const double OPPL_RAISE_MAX = -1000009.0;
static const double OPPL_CALL = -1000010.0;

static bool IsDigitChar(char c)
{
    return c >= '0' && c <= '9';
}

static bool IsExactSymbol(const char* s, const char* expected)
{
    return s && expected && strcmp(s, expected) == 0;
}

static bool IsChairIndexedSymbol(const char* s, const char* prefix)
{
    if (!s || !prefix) return false;
    size_t n = strlen(prefix);
    return strncmp(s, prefix, n) == 0 && IsDigitChar(s[n]) && s[n + 1] == '\0';
}

static bool IsBetroundIndexedBitsSymbol(const char* s, const char* prefix)
{
    if (!s || !prefix) return false;
    size_t n = strlen(prefix);
    if (strncmp(s, prefix, n) != 0) return false;
    return s[n] >= '1' && s[n] <= '4' && s[n + 1] == '\0';
}

static bool IsAuditedCardSymbol(const char* s)
{
    // OH card symbols: $$AB#, where A=p/c, B=c/r/s, # player/common index.
    // We only use player p0..p1 and common c0..c4, but allow p0..p3 because
    // the official OH notation also covers Omaha.
    if (!s || strlen(s) != 5) return false;
    if (s[0] != '$' || s[1] != '$') return false;
    if (s[2] != 'p' && s[2] != 'c') return false;
    if (s[3] != 'c' && s[3] != 'r' && s[3] != 's') return false;
    if (!IsDigitChar(s[4])) return false;

    int idx = s[4] - '0';
    if (s[2] == 'p') return idx >= 0 && idx <= 3;
    return idx >= 0 && idx <= 4;
}

static bool IsAuditedNativeOHSymbol(const char* s)
{
    if (!s || !s[0]) return false;

    if (IsAuditedCardSymbol(s)) return true;

    if (IsExactSymbol(s, "userchair")) return true;
    if (IsExactSymbol(s, "dealerchair")) return true;
    if (IsExactSymbol(s, "betround")) return true;
    if (IsExactSymbol(s, "bblind")) return true;
    if (IsExactSymbol(s, "pot")) return true;
    if (IsExactSymbol(s, "call")) return true;
    if (IsExactSymbol(s, "playersdealtbits")) return true;
    if (IsExactSymbol(s, "playersplayingbits")) return true;

    if (IsChairIndexedSymbol(s, "balance")) return true;
    if (IsChairIndexedSymbol(s, "currentbet")) return true;

    if (IsBetroundIndexedBitsSymbol(s, "foldbits")) return true;
    if (IsBetroundIndexedBitsSymbol(s, "callbits")) return true;
    if (IsBetroundIndexedBitsSymbol(s, "raisbits")) return true;

    return false;
}

static bool IsAuditedOpenPPLSymbol(const char* s)
{
    if (IsExactSymbol(s, "Calls")) return true;
    if (IsExactSymbol(s, "Raises")) return true;
    return false;
}

static bool IsAuditedProjectG5Symbol(const char* s)
{
    // Estes símbolos não são nativos do OH; são UDFs do nosso G5cash.txt.
    // A leitura deles só é aceita depois de validar f$G5_ConfigSchemaVersion.
    if (IsExactSymbol(s, "f$G5_ConfigSchemaVersion")) return true;
    if (IsExactSymbol(s, "f$G5_LogCompleto")) return true;
    if (IsExactSymbol(s, "f$G5_LogRangesCompletos")) return true;
    if (IsExactSymbol(s, "f$G5_AllInPorCommitment")) return true;
    if (IsExactSymbol(s, "f$G5_AllInCommitmentPercent")) return true;
    return false;
}

static bool IsAuditedOHSymbolName(const char* symbol)
{
    return IsAuditedNativeOHSymbol(symbol)
        || IsAuditedOpenPPLSymbol(symbol)
        || IsAuditedProjectG5Symbol(symbol);
}

static bool ReadOHSymbolStrict(const char* symbol, double* outValue, bool required)
{
    if (outValue) *outValue = 0.0;

    if (!symbol || !symbol[0])
    {
        WriteLog("[user.cpp][CRITICO] GetSymbol bloqueado: nome vazio/null.\n");
        return false;
    }

    if (!IsAuditedOHSymbolName(symbol))
    {
        WriteLog("[user.cpp][CRITICO] GetSymbol bloqueado: simbolo nao auditado/inexistente para este projeto: '%s'.\n", symbol);
        return false;
    }

    __try
    {
        double v = GetSymbol(symbol);
        if (v != v)
        {
            WriteLog("[user.cpp][CRITICO] GetSymbol('%s') retornou NaN.\n", symbol);
            return false;
        }

        if (outValue) *outValue = v;
        return true;
    }
    __except (1)
    {
        WriteLog("[user.cpp][CRITICO] excecao ao chamar GetSymbol('%s').\n", symbol);
        return false;
    }
}

static double ReadOHSymbolOr(const char* symbol, double fallback, bool required)
{
    double v = fallback;
    if (!ReadOHSymbolStrict(symbol, &v, required))
    {
        if (required)
            WriteLog("[user.cpp][CRITICO] simbolo obrigatorio '%s' indisponivel. fallback=%.3f.\n", symbol ? symbol : "<null>", fallback);
        return fallback;
    }
    return v;
}

static int ReadIntOHSymbolOr(const char* symbol, int fallback, bool required)
{
    double v = ReadOHSymbolOr(symbol, (double)fallback, required);
    return (int)(v + (v >= 0.0 ? 0.5 : -0.5));
}

static bool ReadRequiredIntOHSymbol(const char* symbol, int* outValue, int minValue, int maxValue)
{
    double v = 0.0;
    if (!ReadOHSymbolStrict(symbol, &v, true))
        return false;

    int iv = (int)(v + (v >= 0.0 ? 0.5 : -0.5));
    if (iv < minValue || iv > maxValue)
    {
        WriteLog("[user.cpp][CRITICO] simbolo obrigatorio '%s' fora da faixa: %.3f esperado=[%d,%d].\n",
            symbol, v, minValue, maxValue);
        return false;
    }

    if (outValue) *outValue = iv;
    return true;
}

static double GetOHActionSymbolSafe(const char* actionName, double fallback)
{
    // Não usar GetSymbol para constantes de ação: se a OpenPPL Library não estiver
    // carregada ou se o nome estiver errado, GetSymbol pode devolver 0 e causar
    // uma ação errada. Estes valores vêm da OpenPPL Library oficial.
    if (!actionName || !actionName[0]) return fallback;

    if (strcmp(actionName, "Beep") == 0) return OPPL_BEEP;
    if (strcmp(actionName, "Fold") == 0) return OPPL_FOLD;
    if (strcmp(actionName, "Check") == 0) return OPPL_CHECK;
    if (strcmp(actionName, "Call") == 0) return OPPL_CALL;
    if (strcmp(actionName, "BetThirdPot") == 0 || strcmp(actionName, "RaiseThirdPot") == 0) return OPPL_RAISE_THIRD_POT;
    if (strcmp(actionName, "BetHalfPot") == 0 || strcmp(actionName, "RaiseHalfPot") == 0) return OPPL_RAISE_HALF_POT;
    if (strcmp(actionName, "BetTwoThirdPot") == 0 || strcmp(actionName, "RaiseTwoThirdPot") == 0) return OPPL_RAISE_TWO_THIRD_POT;
    if (strcmp(actionName, "BetMax") == 0 || strcmp(actionName, "RaiseMax") == 0 || strcmp(actionName, "Allin") == 0) return OPPL_RAISE_MAX;

    WriteLog("[user.cpp][CRITICO] constante de acao OpenPPL nao auditada: '%s'. fallback=%.0f.\n", actionName, fallback);
    return fallback;
}

typedef void(__stdcall* FN_NewHand)      (int* stacks, int* chairs, int heroIndex, int buttonIndex, int numPlayers, int bigBlind, const char* basePath, const char* tableTitle);
typedef void(__stdcall* FN_DealHoleCards)(const char* card0, const char* card1);
typedef void(__stdcall* FN_NewAction)    (int playerIndex, int actionType, int byAmount);
typedef void(__stdcall* FN_GoToNextStreet)(const char* c0, const char* c1, const char* c2, int numCards);
typedef DecisionResult(__stdcall* FN_GetDecision)();
typedef void(__stdcall* FN_WarmUp)(const char* basePath);
typedef void(__stdcall* FN_SetRuntimeConfig)(
    int logCompleto,
    int logRangesCompletos,
    int legacyPostFlopModeIgnored,
    int allowAllInByCommitment,
    int allInCommitmentPercent,
    int legacyAllInCandidateIgnored,
    double legacyMaxSprIgnored);

static HINSTANCE         hBridge = NULL;
static FN_NewHand        pNewHand = nullptr;
static FN_DealHoleCards  pDealHoleCards = nullptr;
static FN_NewAction      pNewAction = nullptr;
static FN_GoToNextStreet pGoToNextStreet = nullptr;
static FN_GetDecision                  pGetDecision = nullptr;
static FN_SetRuntimeConfig             pSetRuntimeConfig = nullptr;
static FN_WarmUp                       pWarmUp = nullptr;
static bool                            g_bridgeLoaded = false;
static bool                            g_bridgeWarmUpDone = false;

static bool g_loggedMsgLoad = false;
static bool g_loggedMsgUnload = false;
static bool g_loggedMsgState = false;
static bool g_loggedMsgPhl1k = false;
static bool g_loggedMsgQuery = false;

static DecisionResult SafeGetDecision();

// CAMINHO DINÃ‚MICO
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
static int    g_runtimeAllInCommitmentPercent = 80;

// =============================================================================
// LEITURA DE VALORES MONETÃ�RIOS EM CENTAVOS
// $0.14 -> 14 | $3.00 -> 300 | $0.04 -> 4
// =============================================================================
static int ReadCents(const char* symbol) {
    double val = ReadOHSymbolOr(symbol, 0.0, true);
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

static bool IsAtLeastConfiguredPercentOf(int intended, int realAllInAmount) {
    if (realAllInAmount <= 0 || intended <= 0)
        return false;

    int pct = g_runtimeAllInCommitmentPercent;

    if (pct < 1)
        pct = 1;

    if (pct > 100)
        pct = 100;

    return (long long)intended * 100LL >= (long long)realAllInAmount * (long long)pct;
}

static bool IsPostFlopStreet(int ohStreet)
{
    return ohStreet >= 2 && ohStreet <= 4;
}

static int AbsInt(int v)
{
    return v < 0 ? -v : v;
}

static int RoundDivInt(int value, int divisor)
{
    if (divisor <= 0) return 0;
    return (value + divisor / 2) / divisor;
}

static int EstimateOHBetPotTotalCents(int numerator, int denominator)
{
    if (denominator <= 0) return 0;

    int pot = ReadCents("pot");
    int call = ReadCents("call");
    int heroBet = ReadChairCurrentBet(g_heroChair);
    int potAfterCall = pot + call;

    if (potAfterCall <= 0)
        return 0;

    return heroBet + call + RoundDivInt(potAfterCall * numerator, denominator);
}

static bool ResolveOpenPPLActionConstant(const char* actionName, double* outValue)
{
    if (!outValue) return false;

    double v = GetOHActionSymbolSafe(actionName, 0.0);

    // OpenPPL action constants are large negative numbers.
    // Positive numbers are interpreted by OpenHoldem as custom betsize in BB.
    if (v < -1000.0)
    {
        *outValue = v;
        return true;
    }

    WriteLog("[user.cpp] AVISO: simbolo OpenPPL '%s' nao resolveu como constante de acao (%.3f).\n",
        actionName ? actionName : "<null>", v);
    return false;
}

static int ReadIntSymbolSafe(const char* symbol)
{
    return ReadIntOHSymbolOr(symbol, 0, false);
}

static bool IsPreflopUnopenedPot()
{
    // OH oficial usa foldbits1/callbits1/raisbits1 para preflop.
    // Nomes como raisbits_preflop/callbits_preflop não são símbolos nativos.
    int rawRaiseBits = ReadIntSymbolSafe("raisbits1");
    int rawCallBits = ReadIntSymbolSafe("callbits1");

    return rawRaiseBits == 0 && rawCallBits == 0;
}

static bool SelectOpenPPLButton(const char* preferredName, std::string& buttonName, double& buttonReturn)
{
    double actionCode = 0.0;
    if (ResolveOpenPPLActionConstant(preferredName, &actionCode))
    {
        buttonName = preferredName;
        buttonReturn = actionCode;
        return true;
    }

    return false;
}

static bool TrySelectBetButtonForRaise(const DecisionResult& decision, int ohStreet, int bb, std::string& buttonName, double& buttonReturn)
{
    buttonName.clear();
    buttonReturn = 0.0;

    if (bb <= 0)
        return false;

    if (decision.actionType != ACT_BET && decision.actionType != ACT_RAISE)
        return false;

    int intendedTotal = decision.byAmount;
    if (intendedTotal <= 0)
        return false;

    int amountToCall = ReadCents("call");

    // Phase22: RFI preflop deve sempre usar o bet button de 1/2 pote.
    // No ambiente alvo, esse botao equivale ao open raise padrao de 3x.
    // Importante: preflop RFI nao pode ser detectado por amountToCall == 0.
    // Antes de qualquer open raise, praticamente todo jogador fora do BB ainda
    // tem fichas a completar contra o blind vivo. Por isso a deteccao usa apenas
    // ausencia de acao voluntaria anterior: sem raise e sem call/limp antes do hero.
    if (ohStreet == 1)
    {
        if (IsPreflopUnopenedPot())
        {
            if (SelectOpenPPLButton("BetHalfPot", buttonName, buttonReturn))
            {
                WriteLog("[user.cpp] BetButton escolhido: BetHalfPot | contexto=RFI preflop | alvoG5=$%.2f.\n",
                    intendedTotal / 100.0);
                return true;
            }
        }

        // 3bet/4bet/squeeze preflop: os bet buttons nao ficam disponiveis de forma confiavel.
        return false;
    }

    if (!IsPostFlopStreet(ohStreet))
        return false;

    // Phase22: raise postflop sempre usa BetHalfPot para usar o bet button
    // disponivel na mesa. Embora seja raise, o nome canonico de execucao no
    // OpenPPL pode ser BetHalfPot, que e alias de RaiseHalfPot.
    if (amountToCall > 0)
    {
        if (SelectOpenPPLButton("BetHalfPot", buttonName, buttonReturn))
        {
            WriteLog("[user.cpp] BetButton escolhido: BetHalfPot | contexto=raise postflop | alvoG5=$%.2f | call=$%.2f.\n",
                intendedTotal / 100.0, amountToCall / 100.0);
            return true;
        }

        return false;
    }

    int potAfterCall = ReadCents("pot") + amountToCall;
    if (potAfterCall <= 0)
        return false;

    // Open bet postflop por bandas simples:
    // pequeno  <= 45% pote -> BetThirdPot
    // medio    <= 60% pote -> BetHalfPot
    // grande   <= 80% pote -> BetTwoThirdPot
    // maior que isso: caixa de edicao, exceto AllIn que usa BetMax no ACT_ALLIN.
    const char* chosen = nullptr;
    long long lhs = (long long)intendedTotal * 100LL;
    long long rhs = (long long)potAfterCall;

    if (lhs <= rhs * 45LL)
        chosen = "BetThirdPot";
    else if (lhs <= rhs * 60LL)
        chosen = "BetHalfPot";
    else if (lhs <= rhs * 80LL)
        chosen = "BetTwoThirdPot";

    if (chosen && SelectOpenPPLButton(chosen, buttonName, buttonReturn))
    {
        WriteLog("[user.cpp] BetButton escolhido: %s | contexto=open bet postflop | alvoG5=$%.2f | pot=$%.2f | fracao=%.1f%%.\n",
            chosen, intendedTotal / 100.0, potAfterCall / 100.0,
            100.0 * (double)intendedTotal / (double)potAfterCall);
        return true;
    }

    WriteLog("[user.cpp] BetButton nao usado: alvoG5=$%.2f excede banda grande (%.1f%% do pote); usando caixa se a acao nao for AllIn.\n",
        intendedTotal / 100.0, 100.0 * (double)intendedTotal / (double)potAfterCall);
    return false;
}

static double CustomBetsizeReturnInBigBlinds(int raiseToTotal, int bb)
{
    if (bb <= 0 || raiseToTotal <= 0)
        return 0.0;

    return (double)raiseToTotal / (double)bb;
}

static double ReadOHSymbolSafe(const char* symbol)
{
    return ReadOHSymbolOr(symbol, 0.0, false);
}

static bool G5ConfigSchemaAvailable()
{
    static int checked = 0;
    static int available = 0;

    if (checked)
        return available != 0;

    checked = 1;
    double schema = 0.0;
    if (!ReadOHSymbolStrict("f$G5_ConfigSchemaVersion", &schema, false))
    {
        WriteLog("[user.cpp][CRITICO] f$G5_ConfigSchemaVersion ausente. Usando defaults internos de runtime config. Atualize G5cash.txt.\n");
        available = 0;
        return false;
    }

    int version = (int)(schema + 0.5);
    if (version != 23)
    {
        WriteLog("[user.cpp][CRITICO] f$G5_ConfigSchemaVersion=%d, esperado=23. Usando defaults internos de runtime config.\n", version);
        available = 0;
        return false;
    }

    available = 1;
    return true;
}

static int ReadOHConfigInt(const char* symbol, int fallback, int minValue, int maxValue)
{
    if (!G5ConfigSchemaAvailable())
        return fallback;

    double value = ReadOHSymbolSafe(symbol);

    if (value != value || value < (double)minValue || value > (double)maxValue)
        return fallback;

    return (int)(value + (value >= 0.0 ? 0.5 : -0.5));
}

static double ReadOHConfigDouble(const char* symbol, double fallback, double minValue, double maxValue)
{
    if (!G5ConfigSchemaAvailable())
        return fallback;

    double value = ReadOHSymbolSafe(symbol);

    if (value != value || value < minValue || value > maxValue)
        return fallback;

    return value;
}

static void SendRuntimeConfigToBridge()
{
    if (!pSetRuntimeConfig)
        return;

    int logCompleto = ReadOHConfigInt("f$G5_LogCompleto", 0, 0, 1);
    int logRangesCompletos = ReadOHConfigInt("f$G5_LogRangesCompletos", 0, 0, 1);
    int allowAllInByCommitment = ReadOHConfigInt("f$G5_AllInPorCommitment", 1, 0, 1);
    int allInCommitmentPercent = ReadOHConfigInt("f$G5_AllInCommitmentPercent", 80, 1, 100);

    g_runtimeAllInCommitmentPercent = allInCommitmentPercent;

    __try
    {
        pSetRuntimeConfig(
            logCompleto,
            logRangesCompletos,
            0,
            allowAllInByCommitment,
            allInCommitmentPercent,
            0,
            0.0);
    }
    __except (1)
    {
        WriteLog("[user.cpp] EXCECAO ao enviar RuntimeConfig para G5Bridge.\n");
    }
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
        // Fallback defensivo. NÃ£o deve ser o caminho normal.
        // Se cair aqui, o XML provavelmente nÃ£o serÃ¡ vinculado corretamente.
        snprintf(title, sizeof(title), "G5_NO_TABLE_TITLE");
    }

    WriteLog("[user.cpp] TableTitle para bridge: '%s'\n", title);
    return title;
}

// =============================================================================
// UTILITÃ�RIOS DE LOG
// =============================================================================
static const char* ActionName(int a) {
    switch (a) {
    case ACT_FOLD:  return "Fold";
    case ACT_CHECK: return "Check";
    case ACT_CALL:  return "Call";
    case ACT_BET:   return "Bet";
    case ACT_RAISE: return "Raise";
    case ACT_ALLIN: return "AllIn";
    case ACT_NOACTION: return "NoAction";
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
// CONVERSÃƒO DE CARTAS
// =============================================================================
static std::string FetchG5Card(const char* rankSym, const char* suitSym) {
    int r = ReadIntOHSymbolOr(rankSym, -1, false);
    int s = ReadIntOHSymbolOr(suitSym, -1, false);
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
    d.actionType = -1;
    d.byAmount = 0;
    d.checkCallEV = 0.0f;
    d.betRaiseEV = 0.0f;

    SendRuntimeConfigToBridge();

    __try
    {
        if (pGetDecision)
            d = pGetDecision();
        else
            WriteLog("[user.cpp] ERRO: pGetDecision indisponivel.\n");
    }
    __except (1)
    {
        WriteLog("[user.cpp] EXCECAO em pGetDecision\n");
        d.actionType = -1;
    }

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
// PASSO 1: INICIALIZAÃ‡ÃƒO DA MÃƒO 
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

    // -- Detecta quem recebeu cartas nesta mÃ£o --------------------------------
    // playersdealtbits: bit N ligado = cadeira N recebeu cartas.
    // Isso exclui jogadores sentados em sit-out ou aguardando big blind.
    // Fallback para balance/currentbet se o sÃ­mbolo retornar 0 (inÃ­cio de sessÃ£o).
    int dealtBits = ReadIntSymbolSafe("playersdealtbits");
    bool useDealtBits = (dealtBits != 0);
    WriteLog("[user.cpp] playersdealtbits=0x%X (%s)\n",
        dealtBits, useDealtBits ? "usando" : "fallback para balance/bet");

    // 1. Coleta jogadores que receberam cartas, comeÃ§ando do BotÃ£o
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

    // 2. Regra de PosiÃ§Ã£o (HU vs Normal)
    if (numActive == 2) {
        // HU: o BotÃ£o Ã‰ o Small Blind
        sbChair = activeChairs[0];
        bbChair = activeChairs[1];
    }
    else if (numActive > 2) {
        sbChair = activeChairs[1];
        bbChair = activeChairs[2];
    }

    // 3. Big blind real
    // Fonte primÃ¡ria: sÃ­mbolo bblind do OpenHoldem, recebido como bigBlindIn.
    // O currentbet da cadeira presumida como BB serve apenas para log diagnÃ³stico,
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

    WriteLog("[user.cpp] NOVA MÃƒO | heroChair=%d btnChair=%d trueBB=%d ($%.2f) | %d jogadores dealt\n",
        heroChair, buttonChair, trueBB, trueBB / 100.0, numActive);

    int logicalStacks[10] = {};
    int logicalChairs[10] = {};
    int heroLogical = -1;
    int buttonLogical = -1;
    int logIdx = 0;

    // 4. Registra apenas os jogadores que receberam cartas
    for (int offset = 0; offset < 10; offset++) {
        int c = (buttonChair + offset) % 10;

        // Mesma lÃ³gica de filtro do passo 1
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
        WriteLog("[user.cpp] FALHA: herÃ³i nÃ£o encontrado ou mesa vazia.\n");
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

    WriteLog("[user.cpp] Inicializado: %d jogadores | herÃ³i=%s | btn=%s\n",
        g_numLogicalPlayers,
        PositionName(heroLogical, buttonLogical, g_numLogicalPlayers),
        PositionName(buttonLogical, buttonLogical, g_numLogicalPlayers));
    return true;
}

// =============================================================================
// PASSO 2: PROCESSA AÃ‡Ã•ES DE UM SEGMENTO
// =============================================================================
static bool Step_ProcessSegment(int stopChair, int targetStreet)
{
    if (targetStreet < 1) return false;

    const char* sName = StreetName(targetStreet);
    char sym[64];

    if (targetStreet < 1 || targetStreet > 4)
    {
        WriteLog("[user.cpp][CRITICO] Step_ProcessSegment recebeu street invalida: %d.\n", targetStreet);
        return false;
    }

    sprintf_s(sym, sizeof(sym), "foldbits%d", targetStreet);  int foldBits = ReadIntSymbolSafe(sym);
    sprintf_s(sym, sizeof(sym), "callbits%d", targetStreet);  int callBits = ReadIntSymbolSafe(sym);
    sprintf_s(sym, sizeof(sym), "raisbits%d", targetStreet);  int raiseBits = ReadIntSymbolSafe(sym);

    int playingBits = ReadIntSymbolSafe("playersplayingbits");

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

        // Ã‰ impossÃ­vel dar Call se ninguÃ©m apostou!
        if (facingBet == 0) didCall = false;

        sprintf(sym, "currentbet%d", c); int currentBet = ReadCents(sym);
        sprintf(sym, "balance%d", c); int bal = ReadCents(sym);
        int delta = currentBet - g_lastPlayerBets[c];
        bool waitingForBetValue = false;

        // Se o OpenHoldem diz que houve acao, mas o valor ainda nao atualizou na tela:
        // - Call pode ser sincronizado com segurança usando o facingBet conhecido.
        // - Raise/Bet NAO pode ser inventado com delta=1, porque isso cria ficha fantasma.
        //   Nesse caso aguardamos o proximo scrape trazer currentbet atualizado.
        if ((didRaise || didCall) && delta <= 0) {
            if (didCall && facingBet > g_lastPlayerBets[c]) {
                delta = facingBet - g_lastPlayerBets[c]; // O Call e exatamente o que falta para cobrir a aposta
            }
            else if (didRaise) {
                waitingForBetValue = true;
                delta = 0;
            }
            else {
                delta = 0;
            }
        }

        WriteLog("[user.cpp]   %s c%d: curBet=%d last=%d delta=%d bal=%d facingBet=%d bits(f%d c%d r%d p%d) pendingMoney=%d\n",
            PositionName(g_chairToLogical[c], g_buttonLogIdx, g_numLogicalPlayers),
            c, currentBet, g_lastPlayerBets[c], delta, bal, facingBet,
            didFold, didCall, didRaise, isPlaying, waitingForBetValue ? 1 : 0);

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
        // 2. AÃ‡ÃƒO COM FICHAS
        else if (delta > 0)
        {
            G5Action act;
            int acceptedBet = g_lastPlayerBets[c] + delta;

            if (currentBet > acceptedBet)
                acceptedBet = currentBet;

            if (bal == 0 && acceptedBet > 0) {
                act = ACT_ALLIN;
                g_wentAllIn[c] = true;
            }
            else if (didRaise || acceptedBet > facingBet) {
                if (facingBet == 0 && targetStreet > 1)
                    act = ACT_BET;
                else
                    act = ACT_RAISE;
            }
            else {
                act = ACT_CALL;
            }

            // Atualiza a memoria pelo delta efetivamente enviado ao G5, nao pelo currentBet
            // que pode estar atrasado no scrape atual. Isso evita double-count no scrape seguinte.
            g_lastPlayerBets[c] = acceptedBet;
            if (acceptedBet > facingBet) facingBet = acceptedBet;

            SafeNewAction(g_chairToLogical[c], (int)act, delta);
            acted = true;
            LogAction((didRaise || didCall) ? "Acao(bit/delta-confirmado)" : "Acao(delta)", c, g_chairToLogical[c], (int)act, delta);
        }
        else if (waitingForBetValue)
        {
            WriteLog("[user.cpp]   aguardando currentbet atualizar para acao de bet/raise em c%d; nada enviado ao G5 neste scrape. Cursor mantido para reprocessar a cadeira.\n", c);
            break;
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
// PASSO 3: RESOLVE AÃ‡Ã•ES PENDENTES DO FINAL DA STREET ANTERIOR
// =============================================================================
static void Step_ResolveEndOfPrevStreet(int oldStreet, int savedMaxBet)
{
    if (savedMaxBet <= 0)
        WriteLog("[user.cpp] ResolveEndOfStreet [%s] sem apostas\n", StreetName(oldStreet));
    else
        WriteLog("[user.cpp] ResolveEndOfStreet [%s] savedMaxBet=%d ($%.2f)\n",
            StreetName(oldStreet), savedMaxBet, savedMaxBet / 100.0);

    char sym[64];
    if (oldStreet < 1 || oldStreet > 4)
    {
        WriteLog("[user.cpp][CRITICO] ResolveEndOfStreet recebeu street invalida: %d.\n", oldStreet);
        return;
    }
    sprintf_s(sym, sizeof(sym), "foldbits%d", oldStreet);
    int foldBitsOld = ReadIntSymbolSafe(sym);

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
            // Confiamos apenas na memÃ³ria oficial de folds da street passada.
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
            else if (savedMaxBet <= 0 && lastBet >= savedMaxBet && !g_hasCheckedThisStreet[c])
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
// PASSO 4: TRANSIÃ‡ÃƒO DE STREET
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
            WriteLog("[user.cpp] AVISO: Cartas do flop ainda nÃ£o disponÃ­veis.\n");
            return;
        }
    }
    else if (ohStreet == 3) {
        c3 = FetchG5Card("$$cr3", "$$cs3");
        if (c3.empty()) { WriteLog("[user.cpp] AVISO: Turn ainda nÃ£o disponÃ­vel.\n"); return; }
    }
    else if (ohStreet == 4) {
        c3 = FetchG5Card("$$cr4", "$$cs4");
        if (c3.empty()) { WriteLog("[user.cpp] AVISO: River ainda nÃ£o disponÃ­vel.\n"); return; }
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
        LogAction("HerÃ³i(troca-street)", g_heroChair, g_chairToLogical[g_heroChair],
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

    // ðŸ�† O relÃ³gio pÃ³s-flop! Recua para o BotÃ£o (SB) primeiro em HU!
    g_lastEvaluatedChair = g_buttonChair;
}

// =============================================================================
// PASSO 5: REGISTRA AÃ‡ÃƒO DO HERÃ“I
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

    LogAction("HerÃ³i(executou)", g_heroChair, g_chairToLogical[g_heroChair],
        (int)act, heroDelta);
    return true;
}

// =============================================================================
// INICIALIZAÃ‡ÃƒO DA BRIDGE
// =============================================================================
static void InitializeBridge() {
    if (g_bridgeLoaded) return;

    char dllPath[MAX_PATH];
    sprintf(dllPath, "%sG5Bridge.dll", g_g5BasePath);

    hBridge = LoadLibraryA(dllPath);
    if (!hBridge) { WriteLog("[user.cpp] ERRO: %s nÃ£o encontrada.\n", dllPath); return; }

    pNewHand = (FN_NewHand)GetProcAddress(hBridge, "G5Bridge_NewHand");
    pDealHoleCards = (FN_DealHoleCards)GetProcAddress(hBridge, "G5Bridge_DealHoleCards");
    pNewAction = (FN_NewAction)GetProcAddress(hBridge, "G5Bridge_NewAction");
    pGoToNextStreet = (FN_GoToNextStreet)GetProcAddress(hBridge, "G5Bridge_GoToNextStreet");
pGetDecision = (FN_GetDecision)GetProcAddress(hBridge, "G5Bridge_GetDecision");
pSetRuntimeConfig = (FN_SetRuntimeConfig)GetProcAddress(hBridge, "G5Bridge_SetRuntimeConfig");
pWarmUp = (FN_WarmUp)GetProcAddress(hBridge, "G5Bridge_WarmUp");

if (pNewHand && pDealHoleCards && pNewAction && pGoToNextStreet && pGetDecision) {
    g_bridgeLoaded = true;
    WriteLog("[user.cpp] G5Bridge carregada com sucesso.\n");
}
    else {
        WriteLog("[user.cpp] ERRO: funÃ§Ãµes nÃ£o encontradas na G5Bridge.dll.\n");
        FreeLibrary(hBridge); hBridge = NULL;
    }
}


static void WarmUpBridgeIfPossible(const char* source)
{
    InitializeBridge();

    if (!g_bridgeLoaded || !pWarmUp || g_bridgeWarmUpDone)
        return;

    __try
    {
        WriteLog("[user.cpp] WarmUp G5Bridge iniciado por %s.\n", source ? source : "<unknown>");
        pWarmUp(g_g5BasePath);
        g_bridgeWarmUpDone = true;
        WriteLog("[user.cpp] WarmUp G5Bridge concluido.\n");
    }
    __except (1)
    {
        WriteLog("[user.cpp] EXCECAO durante WarmUp G5Bridge. Continuando sem warmup.\n");
    }
}

// =============================================================================
// PROCESS_MESSAGE DO OPENHOLDEM
//
// Mantemos a interface canonica do OH para load/unload/state/phl1k/query.
// Mensagens de equity do OpenHoldem sao ignoradas porque a decisao pos-flop
// agora vem exclusivamente da DecisionMaking.dll via G5Bridge_GetDecision.
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
        WarmUpBridgeIfPossible("process_message(load)");
        return 0.0;
    }

    if (strcmp(message, "unload") == 0)
    {
        LogOpenHoldemMessageOnce("unload", g_loggedMsgUnload);
        return 0.0;
    }

    if (strcmp(message, "state") == 0)
    {
        LogOpenHoldemMessageOnce("state", g_loggedMsgState);
        return 0.0;
    }

    if (strcmp(message, "phl1k") == 0)
    {
        LogOpenHoldemMessageOnce("phl1k", g_loggedMsgPhl1k);
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
void DLLOnLoad() { WarmUpBridgeIfPossible("DLLOnLoad"); } void DLLOnUnLoad() {}
void __stdcall DLLUpdateOnNewFormula() {}
void __stdcall DLLUpdateOnConnection() { WarmUpBridgeIfPossible("DLLUpdateOnConnection"); }
void __stdcall DLLUpdateOnHandreset() {}
void __stdcall DLLUpdateOnNewRound() {}
void __stdcall DLLUpdateOnMyTurn() {}
void __stdcall DLLUpdateOnHeartbeat() {}

// =============================================================================
// ProcessQuery â€” ÃšNICA ROTA
// =============================================================================
DLL_IMPLEMENTS double __stdcall ProcessQuery(const char* pquery)
{
    if (!pquery) return 0;

    if (strncmp(pquery, "dll$betsize", 11) == 0)
        return (double)g_cachedByAmount / 100.0;

    InitializeBridge();
    if (!g_bridgeLoaded) return 0;
    SendRuntimeConfigToBridge();
    if (strncmp(pquery, "dll$decisao", 11) != 0) return 0;

    int heroChair = -1;
    int buttonChair = -1;
    int ohStreet = 0;
    if (!ReadRequiredIntOHSymbol("userchair", &heroChair, 0, 9) ||
        !ReadRequiredIntOHSymbol("dealerchair", &buttonChair, 0, 9) ||
        !ReadRequiredIntOHSymbol("betround", &ohStreet, 1, 4))
    {
        WriteLog("[user.cpp][CRITICO] estado OH obrigatorio indisponivel; retornando Beep e nao enviando acao ao G5.\n");
        return OPPL_BEEP;
    }

    int bigBlind = ReadCents("bblind");
    if (bigBlind <= 0)
    {
        WriteLog("[user.cpp][CRITICO] bblind invalido (%d); retornando Beep.\n", bigBlind);
        return OPPL_BEEP;
    }

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
            WriteLog("[user.cpp] Cartas do herÃ³i: %s %s\n", hc0.c_str(), hc1.c_str());
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

decision = SafeGetDecision();

    if (decision.actionType < ACT_FOLD ||
        (decision.actionType > ACT_ALLIN && decision.actionType != ACT_NOACTION))
    {
        WriteLog("[user.cpp] AVISO: G5Bridge retornou actionType invalido (%d). Retornando Beep.\n",
            decision.actionType);
        double beep = GetOHActionSymbolSafe("Beep", -1000000.0);
        g_cachedDecision = beep;
        g_cachedActionType = ACT_NOACTION;
        g_cachedByAmount = 0;
        g_heroActedThisStreet = false;
        g_heroRegistered = false;
        return beep;
    }

    if (decision.actionType == ACT_NOACTION)
    {
        WriteLog("[user.cpp] G5Bridge retornou NoAction. Retornando Beep para impedir Fold/Check/Call por fallback.\n");
        double beep = GetOHActionSymbolSafe("Beep", -1000000.0);
        g_cachedDecision = beep;
        g_cachedActionType = ACT_NOACTION;
        g_cachedByAmount = 0;
        g_heroActedThisStreet = false;
        g_heroRegistered = false;
        return beep;
    }

    std::string finalAction;
    double returnValue = 0.0;
    bool isCustomRaise = false;
    bool isBetButton = false;
    std::string betButtonName;

    int bb = ReadCents("bblind");

    switch (decision.actionType) {
    case ACT_FOLD:
        finalAction = "Fold";
        returnValue = GetOHActionSymbolSafe("Fold", 0.0);
        break;

    case ACT_CHECK:
        finalAction = "Check";
        returnValue = GetOHActionSymbolSafe("Check", 0.0);
        break;

    case ACT_CALL:
        finalAction = "Call";
        returnValue = GetOHActionSymbolSafe("Call", 2.0);
        break;

    case ACT_BET:
    case ACT_RAISE:
    {
        double buttonReturn = 0.0;
        if (TrySelectBetButtonForRaise(decision, ohStreet, bb, betButtonName, buttonReturn))
        {
            finalAction = betButtonName;
            returnValue = buttonReturn;
            isBetButton = true;
        }
        else
        {
            finalAction = "CustomBetsizeBB";
            returnValue = CustomBetsizeReturnInBigBlinds(decision.byAmount, bb);
            isCustomRaise = true;
        }
        break;
    }

    case ACT_ALLIN:
    {
        int heroBalNow = ReadChairBalance(g_heroChair);
        int heroBetNow = ReadChairCurrentBet(g_heroChair);
        int intended = decision.byAmount;
        int realAllInDelta = heroBalNow;
        int realAllInTotal = heroBalNow + heroBetNow;

        bool validAllIn =
            IsAtLeastConfiguredPercentOf(intended, realAllInDelta) ||
            IsAtLeastConfiguredPercentOf(intended, realAllInTotal);

        if (validAllIn) {
            finalAction = "BetMax";
            returnValue = GetOHActionSymbolSafe("BetMax", -1000009.0);

            WriteLog("[user.cpp] ALLIN VALIDADO %d%%: intended=%d ($%.2f), balance c%d=%d ($%.2f), currentbet c%d=%d ($%.2f), total=%d ($%.2f).\n",
                g_runtimeAllInCommitmentPercent,
                intended, intended / 100.0,
                g_heroChair, heroBalNow, heroBalNow / 100.0,
                g_heroChair, heroBetNow, heroBetNow / 100.0,
                realAllInTotal, realAllInTotal / 100.0);
        }
        else {
            double buttonReturn = 0.0;
            DecisionResult converted = decision;
            converted.actionType = ACT_RAISE;

            if (TrySelectBetButtonForRaise(converted, ohStreet, bb, betButtonName, buttonReturn))
            {
                finalAction = betButtonName;
                returnValue = buttonReturn;
                isBetButton = true;
            }
            else
            {
                finalAction = "CustomBetsizeBB";
                returnValue = CustomBetsizeReturnInBigBlinds(decision.byAmount, bb);
                isCustomRaise = true;
            }

            WriteLog("[user.cpp] ALLIN BLOQUEADO %d%%: intended=%d ($%.2f), balance c%d=%d ($%.2f), currentbet c%d=%d ($%.2f), total=%d ($%.2f). Convertendo para %s.\n",
                g_runtimeAllInCommitmentPercent,
                intended, intended / 100.0,
                g_heroChair, heroBalNow, heroBalNow / 100.0,
                g_heroChair, heroBetNow, heroBetNow / 100.0,
                realAllInTotal, realAllInTotal / 100.0,
                finalAction.c_str());
        }

        break;
    }

    default:
        WriteLog("[user.cpp] AVISO: actionType inesperado apos validacao (%d). NoAction.\n", decision.actionType);
        double beep = GetOHActionSymbolSafe("Beep", -1000000.0);
        g_cachedDecision = beep;
        g_cachedActionType = ACT_NOACTION;
        g_cachedByAmount = 0;
        g_heroActedThisStreet = false;
        g_heroRegistered = false;
        return beep;
    }

    if (isCustomRaise && returnValue <= 0.0)
    {
        WriteLog("[user.cpp] ERRO: custom raise sem size valido. byAmount=%d bb=%d. Retornando NoAction.\n",
            decision.byAmount, bb);
        double beep = GetOHActionSymbolSafe("Beep", -1000000.0);
        g_cachedDecision = beep;
        g_cachedActionType = ACT_NOACTION;
        g_cachedByAmount = 0;
        g_heroActedThisStreet = false;
        g_heroRegistered = false;
        return beep;
    }

    double actualReturnValue = returnValue;

    g_cachedDecision = actualReturnValue;
    g_cachedActionType = decision.actionType;
    g_cachedByAmount = decision.byAmount;
    g_heroActedThisStreet = true;
    g_heroRegistered = false;

    const char* decisionOrigin = IsPostFlopStreet(ohStreet)
        ? "G5 FullTree DecisionMaking.dll"
        : "G5 Preflop";

    const char* metricLabel = IsPostFlopStreet(ohStreet)
        ? "EV"
        : "ChartScore";

    WriteLog("[user.cpp] user.dll decidiu: %s (amount=$%.2f | %s cc=%.2f br=%.2f | origem=%s) -> %.3f\n",
        ActionName(decision.actionType), decision.byAmount / 100.0,
        metricLabel,
        decision.checkCallEV, decision.betRaiseEV,
        decisionOrigin,
        actualReturnValue);

    if (isBetButton)
    {
        WriteLog("[user.cpp] Execucao por bet button: %s | alvoG5=$%.2f | retornoOpenPPL=%.3f.\n",
            betButtonName.c_str(), decision.byAmount / 100.0, actualReturnValue);
    }
    else if (isCustomRaise)
    {
        int heroMoneyInPot = g_lastPlayerBets[g_heroChair];
        WriteLog("[user.cpp] Execucao por caixa: byAmount=$%.2f + heroInPot=$%.2f = RaiseTo=$%.2f (%.2f BBs) | retornoOpenPPL=%.3f | dll$betsize=$%.2f\n",
            decision.byAmount / 100.0, heroMoneyInPot / 100.0,
            decision.byAmount / 100.0, actualReturnValue,
            actualReturnValue,
            g_cachedByAmount / 100.0);
    }

    return actualReturnValue;
}

// =============================================================================
// DllMain
// =============================================================================
BOOL APIENTRY DllMain(HANDLE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {
        InitializeOpenHoldemFunctionInterface();

        // Pega o caminho absoluto de onde a user.dll estÃ¡ rodando
        GetModuleFileNameA((HMODULE)hModule, g_g5BasePath, MAX_PATH);

        // Remove "user.dll" do final do caminho e mantÃ©m a barra '\'
        char* lastSlash = strrchr(g_g5BasePath, '\\');
        if (lastSlash) *(lastSlash + 1) = '\0';
    }
    return TRUE;
}
