//******************************************************************************
//    INTEGRATION: G5 POKER BOT BRIDGE (CORRIGIDO FINAL)
//******************************************************************************

// [CORREÇÃO 1] Removemos o #define USER_DLL daqui pois ele JÁ ESTÁ 
// nas Propriedades do Projeto (causava o aviso C4005 de redefinição).
// #define USER_DLL <--- Removido

#include "user.h"
#include <conio.h>
#include <windows.h>
#include "OpenHoldemFunctions.h"

// --- INCLUDES DO G5 ---
#include "GameContext.h"
#include "Common.h"

// [CORREÇÃO 2] O nome correto descoberto no arquivo .h
using namespace G5Cpp;

//******************************************************************************
// Globals
//******************************************************************************
GameContext* g_Context = nullptr;

//******************************************************************************
// Helper Functions
//******************************************************************************

void UpdateG5Context() {
    if (!g_Context) return;

    // Leitura do Pote (OpenHoldem)
    double current_pot = GetSymbol((char*)"pot");

    // [CORREÇÃO 3] O GameContext.h mostra que NÃO existe 'potSize' público.
    // O GameContext serve para calcular Equity das cartas, não o estado financeiro.
    // Comentei a linha abaixo para parar o erro de compilação.
    // g_Context->potSize = current_pot; 

    // Debug
    WriteLog((char*)"G5 Bridge: Pot lido: %f\n", current_pot);
}

//******************************************************************************
// Standard DLL Functions
//******************************************************************************

void DLLOnLoad() {
}

void DLLOnUnLoad() {
    if (g_Context) {
        // Deixar comentado para evitar crash se o G5 não tiver destrutor virtual
        // delete g_Context; 
        g_Context = nullptr;
    }
}

void __stdcall DLLUpdateOnNewFormula() {
}

void __stdcall DLLUpdateOnConnection() {
    if (g_Context == nullptr) {
        // [CORREÇÃO 4] O Construtor exige um caminho (binPath)!
        // GameContext(std::string binPath);
        // Por enquanto, passamos "." (pasta atual), mas depois precisaremos 
        // apontar para a pasta onde estão os arquivos .bin do G5.
        try {
            g_Context = new GameContext(".");
            WriteLog((char*)"G5 Bridge: GameContext Inicializado com sucesso.\n");
        }
        catch (...) {
            WriteLog((char*)"G5 Bridge: Erro ao inicializar GameContext!\n");
        }
    }
}

void __stdcall DLLUpdateOnHandreset() {
}

void __stdcall DLLUpdateOnNewRound() {
}

void __stdcall DLLUpdateOnMyTurn() {
    UpdateG5Context();
}

void __stdcall DLLUpdateOnHeartbeat() {
}

//******************************************************************************
// ProcessQuery
//******************************************************************************

DLL_IMPLEMENTS double __stdcall ProcessQuery(const char* pquery) {
    if (pquery == NULL) {
        return 0;
    }

    // Comandos de Teste do OpenHoldem
    if (strncmp(pquery, "dll$test", 9) == 0) {
        const char* question = "blahdibah";
        double answer = 42.0;
        WriteLog((char*)"%s %f\n", question, answer);
        return GetSymbol((char*)"random");
    }

    if (strncmp(pquery, "dll$scrape", 11) == 0) {
        char* scraped_result;
        int result_lenght;
        scraped_result = ScrapeTableMapRegion((char*)"p0balance", result_lenght);
        if (scraped_result != nullptr) {
            MessageBoxA(0, scraped_result, "Scraped custom region", 0);
            LocalFree(scraped_result);
        }
        return 0;
    }

    // --- COMANDOS G5 ---

    if (strncmp(pquery, "dll$g5_check", 12) == 0) {
        return 1.0;
    }

    if (strncmp(pquery, "dll$g5_action", 13) == 0) {
        if (g_Context) {
            return 0.0; // Placeholder
        }
        return -1.0;
    }

    return 0;
}

//******************************************************************************
// DLL Entry Point
//******************************************************************************

BOOL APIENTRY DllMain(HANDLE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
    case DLL_PROCESS_ATTACH:
#ifdef _DEBUG
        AllocConsole();
#endif 
        InitializeOpenHoldemFunctionInterface();
        DLLOnLoad();
        break;
    case DLL_THREAD_ATTACH:
        break;
    case DLL_THREAD_DETACH:
        break;
    case DLL_PROCESS_DETACH:
        DLLOnUnLoad();
#ifdef _DEBUG
        FreeConsole();
#endif 
        break;
    }
    return TRUE;
}