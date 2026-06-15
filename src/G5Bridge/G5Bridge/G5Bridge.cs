using G5.Logic;
using G5.Logic.Estimators;
using net.r_eg.DllExport;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

// =============================================================================
// G5Bridge.cs
// =============================================================================

namespace G5Bridge
{
    public static class Bridge
    {
        private static BotGameState _gameState = null;
        private static ModelingEstimator _estimator = null;
        private static OpponentModeling _oppModeling = null;
        private static string _basePath = "";
        private static string _targetTableId = "";
        private static int _numPlayers = 0;
        public static int _buttonIndex = 0;
        private static bool _wasHeadsUp = false;
private static bool _streetSyncFailed = false;
private static bool _handSyncFailed = false;

        private static Dictionary<int, int> ChairVersions = new Dictionary<int, int>();

        // Nome real por cadeira FÍSICA — chave estável entre mãos.
        public static Dictionary<int, string> PhysicalChairToRealName = new Dictionary<int, string>();

        // Nome real por índice lógico — usado internamente e para logs.
        public static Dictionary<int, string> LogicalIndexToRealName = new Dictionary<int, string>();

        public static Dictionary<string, int> SessionHandsLearned = new Dictionary<string, int>();

        public static int[] CurrentChairs = null;
        public static int CurrentBigBlind = 0;
        public static int HeroLogicalIndex { get; private set; } = 0;
        public static bool HeroDeuFold { get; set; } = false;

        private static readonly object _logLock = new object();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static bool _nativePathConfigured = false;

        // =========================================================================
        // Logs diagnosticos opcionais
        //
        // Uso normal:
        //   - EnableDiagnosticLogs = false
        //   - log limpo, parecido com a versao original.
        //
        // Para diagnostico, sem recompilar:
        //   opcao 1: criar um arquivo vazio chamado G5Bridge.diagnostic na pasta base
        //            exemplo: C:\G5Pressure\G5Bridge.diagnostic
        //
        //   opcao 2: criar a variavel de ambiente:
        //            G5BRIDGE_DIAGNOSTIC=1
        //
        // Assim, se outro problema surgir, basta ativar o diagnostico e repetir o teste.
        // =========================================================================
        public static bool EnableDiagnosticLogs { get; private set; } = false;

        private static bool _runtimeLogCompleto = false;
        private static bool _runtimeLogRangesCompletos = false;
        private static bool _runtimeFastPostFlopEVEnabled = true;
        private static bool _runtimeAllowAllInByCommitment = true;
        private static int _runtimeAllInCommitmentPercent = 66;
        private static bool _runtimeAllowHighSprAllInCandidate = false;
        private static double _runtimeMaxSprForAllInCandidate = 1.25;

        // Opcional. Por padrao fica false para manter log limpo.
        // Para ver todos os combos ativos dos ranges, mude para true no codigo
        // ou use a variavel de ambiente G5BRIDGE_MOSTRAR_RANGES_COMPLETOS=1.
        private static bool mostrarRangesCompletos = false;

        private static bool IsDiagnosticLogEnabled()
        {
            if (_runtimeLogCompleto)
                return true;

            if (EnableDiagnosticLogs)
                return true;

            try
            {
                string env = Environment.GetEnvironmentVariable("G5BRIDGE_DIAGNOSTIC") ?? "";
                if (env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrEmpty(_basePath))
                {
                    string markerFile = Path.Combine(_basePath, "G5Bridge.diagnostic");
                    if (File.Exists(markerFile))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static void DLog(string message)
        {
            if (IsDiagnosticLogEnabled())
                Log(message);
        }

        private static bool MostrarRangesCompletos()
        {
            // LogCompleto ativa mensagens diagnosticas, mas NAO deve despejar 1326 combos
            // por range no caminho critico do OpenHoldem. Range completo so com a chave
            // especifica f$G5_LogRangesCompletos ou variavel de ambiente propria.
            if (_runtimeLogRangesCompletos)
                return true;

            if (mostrarRangesCompletos)
                return true;

            try
            {
                string env = Environment.GetEnvironmentVariable("G5BRIDGE_MOSTRAR_RANGES_COMPLETOS") ?? "";

                if (env == "1" ||
                    env.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    env.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                    env.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        private static string GetOpponentRangesDiagnosticsForLog()
        {
            if (_gameState == null)
                return "sem gameState";

            bool full = MostrarRangesCompletos();
            return _gameState.getOpponentRangesDiagnostics(full, full ? 1326 : 5);
        }

        private static void ConfigureNativeDllPath()
        {
            if (_nativePathConfigured) return;

            try
            {
                if (string.IsNullOrEmpty(_basePath))
                {
                    Log("[NativePath] ERRO: _basePath vazio, nao foi possivel configurar caminho nativo.");
                    return;
                }

                if (!Directory.Exists(_basePath))
                {
                    Log($"[NativePath] ERRO: pasta nao existe: {_basePath}");
                    return;
                }

                bool ok = SetDllDirectory(_basePath);

                string oldPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                if (!oldPath.ToLowerInvariant().Contains(_basePath.ToLowerInvariant()))
                    Environment.SetEnvironmentVariable("PATH", _basePath + ";" + oldPath);

                Directory.SetCurrentDirectory(_basePath);

                bool hasDecisionMaking = File.Exists(Path.Combine(_basePath, "DecisionMaking.dll"));
                bool hasTbb = File.Exists(Path.Combine(_basePath, "tbb.dll"));
                bool hasPreflopEquities = File.Exists(Path.Combine(_basePath, "PreFlopEquities.txt"));

                if (!ok)
                    Log($"[NativePath] ERRO: SetDllDirectory({_basePath}) retornou false.");

                if (!hasDecisionMaking)
                    Log("[NativePath] ERRO: DecisionMaking.dll nao encontrada na pasta base.");

                if (!hasTbb)
                    Log("[NativePath] ERRO: tbb.dll nao encontrada na pasta base.");

                if (!hasPreflopEquities)
                    Log("[NativePath] ERRO: PreFlopEquities.txt nao encontrado na pasta base.");

                DLog($"[NativePath] SetDllDirectory({_basePath}) => {ok}");
                DLog($"[NativePath] CurrentDirectory={Directory.GetCurrentDirectory()}");
                DLog($"[NativePath] Existe DecisionMaking.dll? {hasDecisionMaking}");
                DLog($"[NativePath] Existe tbb.dll? {hasTbb}");
                DLog($"[NativePath] Existe PreFlopEquities.txt? {hasPreflopEquities}");

                _nativePathConfigured = true;
            }
            catch (Exception ex)
            {
                Log($"[NativePath] ERRO: {ex.GetType().Name}: {ex.Message}");

                if (IsDiagnosticLogEnabled())
                    Log(ex.ToString());
            }
        }

        public static bool EnableXML { get; private set; } = true;
        public static string XMLFolderPath { get; private set; } = @"C:\Users\Wanda\AppData\Local\Red Star Poker\data\MLemos1\History\Data\Tables";

        [StructLayout(LayoutKind.Sequential)]
        public struct DecisionResult
        {
            public int actionType;
            public int byAmount;
            public float checkCallEV;
            public float betRaiseEV;
        }
		
        private const int PRW_HAND_COUNT = 1326;
        private const int PRW_MAX_CHAIRS = 10;
        private const int PRW_WEIGHT_SCALE = 1000000;

        private static int PRW_USEME_OFFSET = 0;
        private static int PRW_PREFLOP_OFFSET = 4;
        private static int PRW_USECALLBACK_OFFSET = 8;

        private static int PRW_CHAIR_ARRAY_OFFSET = 0;
        private static int PRW_CHAIR_SIZE = 0;
        private static int PRW_CHAIR_LEVEL_OFFSET = 0;
        private static int PRW_CHAIR_LIMIT_OFFSET = 4;
        private static int PRW_CHAIR_IGNORE_OFFSET = 8;
        private static int PRW_CHAIR_RANKHI_OFFSET = 12;
        private static int PRW_CHAIR_RANKLO_OFFSET = 0;
        private static int PRW_CHAIR_WEIGHT_OFFSET = 0;

        private static bool _loggedEnhancedPrWinLayout = false;

        private static bool ConfigurePrwLayout(
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
            int chairWeightOffset)
        {
            if (rootUseMeOffset < 0 ||
                rootPreflopOffset < 0 ||
                rootUseCallbackOffset < 0 ||
                chairArrayOffset <= 0 ||
                chairSize <= 0 ||
                chairLevelOffset < 0 ||
                chairLimitOffset < 0 ||
                chairIgnoreOffset < 0 ||
                chairRankHiOffset < 0 ||
                chairRankLoOffset <= chairRankHiOffset ||
                chairWeightOffset <= chairRankLoOffset)
            {
                Log("[EnhancedPrWin Layout] ERRO: offsets invalidos recebidos da user.dll.");
                return false;
            }

            if (chairRankHiOffset + (PRW_HAND_COUNT * 4) > chairSize ||
                chairRankLoOffset + (PRW_HAND_COUNT * 4) > chairSize ||
                chairWeightOffset + (PRW_HAND_COUNT * 4) > chairSize)
            {
                Log("[EnhancedPrWin Layout] ERRO: arrays rankhi/ranklo/weight excedem o tamanho da cadeira prw1326.");
                return false;
            }

            PRW_USEME_OFFSET = rootUseMeOffset;
            PRW_PREFLOP_OFFSET = rootPreflopOffset;
            PRW_USECALLBACK_OFFSET = rootUseCallbackOffset;

            PRW_CHAIR_ARRAY_OFFSET = chairArrayOffset;
            PRW_CHAIR_SIZE = chairSize;
            PRW_CHAIR_LEVEL_OFFSET = chairLevelOffset;
            PRW_CHAIR_LIMIT_OFFSET = chairLimitOffset;
            PRW_CHAIR_IGNORE_OFFSET = chairIgnoreOffset;
            PRW_CHAIR_RANKHI_OFFSET = chairRankHiOffset;
            PRW_CHAIR_RANKLO_OFFSET = chairRankLoOffset;
            PRW_CHAIR_WEIGHT_OFFSET = chairWeightOffset;

            if (!_loggedEnhancedPrWinLayout)
            {
                _loggedEnhancedPrWinLayout = true;

                Log($"[EnhancedPrWin Layout] root(useme={PRW_USEME_OFFSET}, preflop={PRW_PREFLOP_OFFSET}, usecallback={PRW_USECALLBACK_OFFSET}, chairArray={PRW_CHAIR_ARRAY_OFFSET}); " +
                    $"chair(size={PRW_CHAIR_SIZE}, level={PRW_CHAIR_LEVEL_OFFSET}, limit={PRW_CHAIR_LIMIT_OFFSET}, ignore={PRW_CHAIR_IGNORE_OFFSET}, " +
                    $"rankhi={PRW_CHAIR_RANKHI_OFFSET}, ranklo={PRW_CHAIR_RANKLO_OFFSET}, weight={PRW_CHAIR_WEIGHT_OFFSET}).");
            }

            return true;
        }

        private struct OHEquitySnapshot
        {
            public bool IsValid;
            public bool IsEnhancedProfile;
            public DateTime ReceivedAt;
            public int OhStreet;
            public double PrWin;
            public double PrTie;
            public double PrLos;
            public double PrWinNow;
            public double PrLosNow;
            public double NHands;
            public double NHandsHi;
            public double NHandsLo;
            public double NHandsTi;
        }

        private static OHEquitySnapshot _lastOHEquitySnapshot;
        private static int _lastEnhancedPrWinStreet = 0;

        private const double OH_SNAPSHOT_MAX_AGE_SECONDS = 3.0;
        private const double OH_MIN_NHANDS_FOR_DECISION = 100.0;
        private const double OH_EQUITY_EPSILON = 0.000001;

        private static int PrwChairOffset(int physicalChair)
        {
            return PRW_CHAIR_ARRAY_OFFSET + (physicalChair * PRW_CHAIR_SIZE);
        }

        private static double SafeDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;

            return value;
        }
		
        private static double ClampDouble(double value, double min, double max)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return min;

            if (value < min)
                return min;

            if (value > max)
                return max;

            return value;
        }

        private static int G5StreetToOHStreet(Street street)
        {
            switch (street)
            {
                case Street.PreFlop: return 1;
                case Street.Flop: return 2;
                case Street.Turn: return 3;
                case Street.River: return 4;
                default: return 0;
            }
        }

        private static bool TryGetFreshOHEquity(out double equity, out string reason)
        {
            equity = 0.0;
            reason = "";

            if (_gameState == null)
            {
                reason = "_gameState == null";
                return false;
            }

            if (_gameState.getStreet() == Street.PreFlop)
            {
                reason = "preflop";
                return false;
            }

            if (!_lastOHEquitySnapshot.IsValid)
            {
                reason = "snapshot OH ainda nao recebido";
                return false;
            }

            if (!_lastOHEquitySnapshot.IsEnhancedProfile)
            {
                reason = "snapshot OH nao veio de perfil EnhancedPrWin atualizado";
                return false;
            }

            int currentOhStreet = G5StreetToOHStreet(_gameState.getStreet());

            if (_lastOHEquitySnapshot.OhStreet != currentOhStreet)
            {
                reason = $"snapshot OH de outra street: snapshot={_lastOHEquitySnapshot.OhStreet}, atual={currentOhStreet}";
                return false;
            }

            double ageSeconds = (DateTime.Now - _lastOHEquitySnapshot.ReceivedAt).TotalSeconds;

            if (ageSeconds > OH_SNAPSHOT_MAX_AGE_SECONDS)
            {
                reason = $"snapshot OH antigo: {ageSeconds:F2}s";
                return false;
            }

            if (_lastOHEquitySnapshot.NHands < OH_MIN_NHANDS_FOR_DECISION)
            {
                reason = $"nhands insuficiente: {_lastOHEquitySnapshot.NHands:F0}";
                return false;
            }

            double prWin = SafeDouble(_lastOHEquitySnapshot.PrWin);
            double prTie = SafeDouble(_lastOHEquitySnapshot.PrTie);
            double prLos = SafeDouble(_lastOHEquitySnapshot.PrLos);

            // OpenHoldem normalmente entrega probabilidades em 0..1.
            // Este bloco protege contra ambientes/configuracoes que entreguem 0..100.
            double sum = prWin + prTie + prLos;

            if (sum > 1.5)
            {
                prWin /= 100.0;
                prTie /= 100.0;
                prLos /= 100.0;
                sum = prWin + prTie + prLos;
            }

            if (sum <= OH_EQUITY_EPSILON)
            {
                reason = "snapshot OH com soma de probabilidades zerada";
                return false;
            }

            equity = ClampDouble(prWin + (0.5 * prTie), 0.0, 1.0);
            return true;
        }

        private static bool ApplyOHEquityCallFloor(ref DecisionResult result)
        {
            if (_gameState == null)
                return false;

            if (_gameState.getStreet() == Street.PreFlop)
                return false;

            int amountToCall = _gameState.getAmountToCall();

            if (amountToCall <= 0)
                return false;

            if (result.actionType != (int)ActionType.Fold)
                return false;

            double equity;
            string reason;

            if (!TryGetFreshOHEquity(out equity, out reason))
            {
                DLog($"[OH Equity Floor] nao aplicado: {reason}.");
                return false;
            }

            int potBeforeCall = _gameState.potSize();
            double potAfterCall = Math.Max(1.0, potBeforeCall + amountToCall);
            double requiredEquity = amountToCall / potAfterCall;
            double ohCallEV = (equity * potAfterCall) - amountToCall;

            DLog($"[OH Equity Floor] equity={equity:P2}, required={requiredEquity:P2}, pot={potBeforeCall}, call={amountToCall}, ohCallEV={ohCallEV:F3}, acaoOriginal={GetActionName(result.actionType)}.");

            if (ohCallEV <= 0.0)
                return false;

            result.actionType = (int)ActionType.Call;
            result.byAmount = amountToCall;

            if (result.checkCallEV < (float)ohCallEV)
                result.checkCallEV = (float)ohCallEV;

            Log($"[OH Equity Floor] Fold convertido em Call: equityOH={equity:P2}, equityNecessaria={requiredEquity:P2}, callEV_OH={ohCallEV:F3}, call={amountToCall}.");
            return true;
        }

        private static int RankCharToOHIndex(char c)
        {
            c = char.ToUpperInvariant(c);

            if (c >= '2' && c <= '9')
                return c - '2';

            switch (c)
            {
                case 'T': return 8;
                case 'J': return 9;
                case 'Q': return 10;
                case 'K': return 11;
                case 'A': return 12;
                default: return -1;
            }
        }

        private static int SuitCharToOHOffset(char c)
        {
            c = char.ToLowerInvariant(c);

            switch (c)
            {
                case 'h': return 0;
                case 'd': return 13;
                case 'c': return 26;
                case 's': return 39;
                default: return -1;
            }
        }

        private static bool TryParseCardToOHValue(char rankChar, char suitChar, out int cardValue)
        {
            cardValue = -1;

            int rank = RankCharToOHIndex(rankChar);
            int suitOffset = SuitCharToOHOffset(suitChar);

            if (rank < 0 || suitOffset < 0)
                return false;

            cardValue = suitOffset + rank;
            return cardValue >= 0 && cardValue < 52;
        }

        private static bool TryParseHoleCardsToOHValues(string holeCardsText, out int card0, out int card1)
        {
            card0 = -1;
            card1 = -1;

            if (string.IsNullOrWhiteSpace(holeCardsText))
                return false;

            string compact = new string(holeCardsText.Where(char.IsLetterOrDigit).ToArray());

            if (compact.Length < 4)
                return false;

            if (!TryParseCardToOHValue(compact[0], compact[1], out card0))
                return false;

            if (!TryParseCardToOHValue(compact[2], compact[3], out card1))
                return false;

            if (card0 == card1)
                return false;

            return true;
        }

        private static int RangeWeightToPrwWeight(float equity)
        {
            if (float.IsNaN(equity) || float.IsInfinity(equity) || equity <= 0.0000001f)
                return 0;

            int weight = (int)Math.Round(equity * PRW_WEIGHT_SCALE);

            if (weight < 1)
                weight = 1;

            if (weight > PRW_WEIGHT_SCALE)
                weight = PRW_WEIGHT_SCALE;

            return weight;
        }

        private static void ClearPrwChairProfile(IntPtr prw1326Ptr, int physicalChair)
        {
            if (physicalChair < 0 || physicalChair >= PRW_MAX_CHAIRS)
                return;

            int chairOffset = PrwChairOffset(physicalChair);

            Marshal.WriteInt32(prw1326Ptr, chairOffset + PRW_CHAIR_LEVEL_OFFSET, PRW_WEIGHT_SCALE);
            Marshal.WriteInt32(prw1326Ptr, chairOffset + PRW_CHAIR_LIMIT_OFFSET, 0);
            Marshal.WriteInt32(prw1326Ptr, chairOffset + PRW_CHAIR_IGNORE_OFFSET, 1);

            int rankHiBase = chairOffset + PRW_CHAIR_RANKHI_OFFSET;
            int rankLoBase = chairOffset + PRW_CHAIR_RANKLO_OFFSET;
            int weightBase = chairOffset + PRW_CHAIR_WEIGHT_OFFSET;

            for (int i = 0; i < PRW_HAND_COUNT; i++)
            {
                Marshal.WriteInt32(prw1326Ptr, rankHiBase + (i * 4), 0);
                Marshal.WriteInt32(prw1326Ptr, rankLoBase + (i * 4), 0);
                Marshal.WriteInt32(prw1326Ptr, weightBase + (i * 4), 0);
            }
        }

        private static bool TryGetPhysicalChairForLogicalIndex(int logicalIndex, out int physicalChair)
        {
            physicalChair = -1;

            if (CurrentChairs == null)
                return false;

            if (logicalIndex < 0 || logicalIndex >= CurrentChairs.Length)
                return false;

            physicalChair = CurrentChairs[logicalIndex];
            return physicalChair >= 0 && physicalChair < PRW_MAX_CHAIRS;
        }

        private static int FillPrwChairProfileFromRange(IntPtr prw1326Ptr, int physicalChair, Player player)
        {
            if (physicalChair < 0 || physicalChair >= PRW_MAX_CHAIRS)
                return 0;

            if (player == null || player.Range == null || player.Range.Data == null)
                return 0;

            ClearPrwChairProfile(prw1326Ptr, physicalChair);

            int chairOffset = PrwChairOffset(physicalChair);

            int rankHiBase = chairOffset + PRW_CHAIR_RANKHI_OFFSET;
            int rankLoBase = chairOffset + PRW_CHAIR_RANKLO_OFFSET;
            int weightBase = chairOffset + PRW_CHAIR_WEIGHT_OFFSET;

            int writeIndex = 0;
            int length = Math.Min(PRW_HAND_COUNT, player.Range.Data.Length);

            for (int i = 0; i < length && writeIndex < PRW_HAND_COUNT; i++)
            {
                var pair = player.Range.Data[i];

                int weight = RangeWeightToPrwWeight(pair.Equity);

                if (weight <= 0)
                    continue;

                int card0;
                int card1;

                if (!TryParseHoleCardsToOHValues(pair.GetHoleCards().ToString(), out card0, out card1))
                    continue;

                Marshal.WriteInt32(prw1326Ptr, rankHiBase + (writeIndex * 4), card0);
                Marshal.WriteInt32(prw1326Ptr, rankLoBase + (writeIndex * 4), card1);
                Marshal.WriteInt32(prw1326Ptr, weightBase + (writeIndex * 4), weight);

                writeIndex++;
            }

            Marshal.WriteInt32(prw1326Ptr, chairOffset + PRW_CHAIR_LEVEL_OFFSET, PRW_WEIGHT_SCALE);
            Marshal.WriteInt32(prw1326Ptr, chairOffset + PRW_CHAIR_LIMIT_OFFSET, writeIndex);
            Marshal.WriteInt32(prw1326Ptr, chairOffset + PRW_CHAIR_IGNORE_OFFSET, writeIndex <= 0 ? 1 : 0);

            return writeIndex;
        }

        private static bool IsBridgeReadyForEnhancedPrWin()
        {
            string reason;
            return IsBridgeReadyForEnhancedPrWin(out reason);
        }

        private static bool IsBridgeReadyForEnhancedPrWin(out string reason)
        {
            reason = "";

            if (_gameState == null)
            {
                reason = "_gameState == null";
                return false;
            }

            if (_handSyncFailed)
            {
                reason = "_handSyncFailed == true";
                return false;
            }

            if (_streetSyncFailed)
            {
                reason = "_streetSyncFailed == true";
                return false;
            }

            if (_gameState.getStreet() == Street.PreFlop)
            {
                reason = "G5 ainda esta em PreFlop";
                return false;
            }

            if (CurrentChairs == null)
            {
                reason = "CurrentChairs == null";
                return false;
            }

            return true;
        }

        [DllExport("G5Bridge_SetRuntimeConfig", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void SetRuntimeConfig(
            int logCompleto,
            int logRangesCompletos,
            int fastPostFlopEVEnabled,
            int allowAllInByCommitment,
            int allInCommitmentPercent,
            int allowHighSprAllInCandidate,
            double maxSprForAllInCandidate)
        {
            _runtimeLogCompleto = logCompleto != 0;
            _runtimeLogRangesCompletos = logRangesCompletos != 0;
            _runtimeFastPostFlopEVEnabled = fastPostFlopEVEnabled != 0;
            _runtimeAllowAllInByCommitment = allowAllInByCommitment != 0;

            if (allInCommitmentPercent < 1)
                allInCommitmentPercent = 1;

            if (allInCommitmentPercent > 100)
                allInCommitmentPercent = 100;

            _runtimeAllInCommitmentPercent = allInCommitmentPercent;
            _runtimeAllowHighSprAllInCandidate = allowHighSprAllInCandidate != 0;

            if (double.IsNaN(maxSprForAllInCandidate) || double.IsInfinity(maxSprForAllInCandidate) || maxSprForAllInCandidate <= 0.0)
                maxSprForAllInCandidate = 1.25;

            if (maxSprForAllInCandidate > 20.0)
                maxSprForAllInCandidate = 20.0;

            _runtimeMaxSprForAllInCandidate = maxSprForAllInCandidate;

            BotGameState.ConfigureRuntimeOptions(
                _runtimeFastPostFlopEVEnabled,
                _runtimeAllowAllInByCommitment,
                _runtimeAllInCommitmentPercent,
                _runtimeAllowHighSprAllInCandidate,
                _runtimeMaxSprForAllInCandidate);

            DLog($"[RuntimeConfig] LogCompleto={_runtimeLogCompleto}, LogRangesCompletos={_runtimeLogRangesCompletos}, " +
                $"FastPostFlopEV(depreciado)={_runtimeFastPostFlopEVEnabled}, AllInCommitment={_runtimeAllowAllInByCommitment}, " +
                $"CommitmentPercent={_runtimeAllInCommitmentPercent}, HighSPRJamCandidate={_runtimeAllowHighSprAllInCandidate}, " +
                $"MaxSPRJam={_runtimeMaxSprForAllInCandidate:F2}.");
        }

        [DllExport("G5Bridge_UpdateEnhancedPrWinProfile", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static int UpdateEnhancedPrWinProfile(
            IntPtr prw1326Ptr,
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
            int chairWeightOffset)
        {
            _lastEnhancedPrWinStreet = 0;

            if (prw1326Ptr == IntPtr.Zero)
            {
                Log("[EnhancedPrWin] perfil nao atualizado: prw1326Ptr == IntPtr.Zero.");
                return 0;
            }

            if (currentStreet < 2 || currentStreet > 4)
            {
                Log($"[EnhancedPrWin] perfil nao atualizado: currentStreet invalida: {currentStreet}.");
                return 0;
            }

            string readinessReason;

            if (!IsBridgeReadyForEnhancedPrWin(out readinessReason))
            {
                Log($"[EnhancedPrWin] perfil nao atualizado: Bridge nao pronta: {readinessReason}.");
                return 0;
            }
			
			            if (!ConfigurePrwLayout(
                rootUseMeOffset,
                rootPreflopOffset,
                rootUseCallbackOffset,
                chairArrayOffset,
                chairSize,
                chairLevelOffset,
                chairLimitOffset,
                chairIgnoreOffset,
                chairRankHiOffset,
                chairRankLoOffset,
                chairWeightOffset))
            {
                return 0;
            }

            try
            {
                Marshal.WriteInt32(prw1326Ptr, PRW_USEME_OFFSET, PRW_HAND_COUNT);
                Marshal.WriteInt32(prw1326Ptr, PRW_PREFLOP_OFFSET, 0);
                Marshal.WriteInt32(prw1326Ptr, PRW_USECALLBACK_OFFSET, PRW_HAND_COUNT);

                for (int chair = 0; chair < PRW_MAX_CHAIRS; chair++)
                    ClearPrwChairProfile(prw1326Ptr, chair);

                int updatedOpponents = 0;
                int totalActiveWeights = 0;
                var players = _gameState.getPlayers();

                for (int logicalIndex = 0; logicalIndex < players.Count; logicalIndex++)
                {
                    if (logicalIndex == _gameState.getHeroInd())
                        continue;

                    Player player = players[logicalIndex];

                    if (player.StatusInHand == Status.Folded)
                        continue;

                    int physicalChair;

                    if (!TryGetPhysicalChairForLogicalIndex(logicalIndex, out physicalChair))
                        continue;

                    int activeWeights = FillPrwChairProfileFromRange(prw1326Ptr, physicalChair, player);

                    if (activeWeights > 0)
                    {
                        updatedOpponents++;
                        totalActiveWeights += activeWeights;
                    }
                }

                if (updatedOpponents <= 0)
                {
                    Log($"[EnhancedPrWin] perfil nao atualizado: nenhum oponente ativo com range valido. streetOH={currentStreet}, streetG5={_gameState.getStreet()}, players={players.Count}.");
                    return 0;
                }

                _lastEnhancedPrWinStreet = currentStreet;

                                Log($"[EnhancedPrWin] perfil atualizado: streetOH={currentStreet}, streetG5={_gameState.getStreet()}, oponentes={updatedOpponents}, combosAtivos={totalActiveWeights}.");
                return 1;
            }
            catch (Exception ex)
            {
                Log($"[EnhancedPrWin] ERRO ao atualizar perfil prw1326: {ex.GetType().Name}: {ex.Message}");

                if (IsDiagnosticLogEnabled())
                    Log(ex.ToString());

                return 0;
            }
        }

        [DllExport("G5Bridge_SetOHEquitySnapshot", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void SetOHEquitySnapshot(
            double prwin,
            double prtie,
            double prlos,
            double prwinnow,
            double prlosnow,
            double nhands,
            double nhandshi,
            double nhandslo,
            double nhandsti)
        {
            _lastOHEquitySnapshot = new OHEquitySnapshot
            {
                IsValid = true,
                IsEnhancedProfile = (_lastEnhancedPrWinStreet >= 2 && _lastEnhancedPrWinStreet <= 4),
                ReceivedAt = DateTime.Now,
                OhStreet = _lastEnhancedPrWinStreet,
                PrWin = SafeDouble(prwin),
                PrTie = SafeDouble(prtie),
                PrLos = SafeDouble(prlos),
                PrWinNow = SafeDouble(prwinnow),
                PrLosNow = SafeDouble(prlosnow),
                NHands = SafeDouble(nhands),
                NHandsHi = SafeDouble(nhandshi),
                NHandsLo = SafeDouble(nhandslo),
                NHandsTi = SafeDouble(nhandsti)
            };

                       Log($"[EnhancedPrWin OH] snapshot recebido: prwin={_lastOHEquitySnapshot.PrWin:F4}, prtie={_lastOHEquitySnapshot.PrTie:F4}, prlos={_lastOHEquitySnapshot.PrLos:F4}, nhands={_lastOHEquitySnapshot.NHands:F0}.");
        }

        public static string GetChairName(int physicalChair)
        {
            int v = ChairVersions.ContainsKey(physicalChair) ? ChairVersions[physicalChair] : 0;
            return v == 0 ? $"Cadeira_{physicalChair}" : $"Cadeira_{physicalChair}_v{v + 1}";
        }

        public static string GetActionName(int a)
        {
            switch (a)
            {
                case 0: return "Fold";
                case 1: return "Check";
                case 2: return "Call";
                case 3: return "Bet";
                case 4: return "Raise";
                case 5: return "AllIn";
                default: return "Unknown";
            }
        }

        public static string GetPositionName(int logIdx, int btnIdx, int n)
        {
            if (logIdx < 0 || n < 2) return "?";
            int d = (logIdx - btnIdx + n) % n;
            switch (n)
            {
                case 2: return d == 0 ? "BTN/SB" : "BB";
                case 3:
                    if (d == 0) return "BTN"; if (d == 1) return "SB"; return "BB";
                case 4:
                    if (d == 0) return "BTN"; if (d == 1) return "SB";
                    if (d == 2) return "BB"; return "UTG";
                case 5:
                    if (d == 0) return "BTN"; if (d == 1) return "SB";
                    if (d == 2) return "BB"; if (d == 3) return "UTG"; return "CO";
                default:
                    if (d == 0) return "BTN"; if (d == 1) return "SB";
                    if (d == 2) return "BB"; if (d == 3) return "UTG";
                    if (d == 4) return "HJ"; if (d == 5) return "CO"; return "UTG+";
            }
        }

        // =========================================================================
        // UpdateRealNames
        // Usa snapshotChairs (nao CurrentChairs) para resolver physicalChair.
        // Compara por physicalChair (estavel) para detectar trocas reais.
        // =========================================================================
        public static void UpdateRealNames(Dictionary<int, string> newLogicalNames, int[] snapshotChairs)
        {
            if (snapshotChairs == null) return;

            LogicalIndexToRealName.Clear();

            foreach (var kvp in newLogicalNames)
            {
                int logIdx = kvp.Key;
                string newName = kvp.Value;

                LogicalIndexToRealName[logIdx] = newName;

                if (logIdx < 0 || logIdx >= snapshotChairs.Length) continue;
                int physChair = snapshotChairs[logIdx];

                if (PhysicalChairToRealName.TryGetValue(physChair, out string oldName))
                {
                    if (oldName != newName && !string.IsNullOrEmpty(oldName))
                    {
                        if (!ChairVersions.ContainsKey(physChair)) ChairVersions[physChair] = 0;
                        ChairVersions[physChair]++;
                        Log($"[Bridge] Cadeira fisica {physChair}: '{oldName}' -> '{newName}'. " +
                            $"Nome G5 agora e '{GetChairName(physChair)}'.");
                    }
                }
                PhysicalChairToRealName[physChair] = newName;
            }
        }

        // =========================================================================
        // G5Bridge_NewHand
        // =========================================================================
        [DllExport("G5Bridge_NewHand", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void NewHand(
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] int[] stacks,
            [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] int[] chairs,
            int heroIndex,
            int buttonIndex,
            int numPlayers,
            int bigBlind,
            [MarshalAs(UnmanagedType.LPStr)] string basePath,
            [MarshalAs(UnmanagedType.LPStr)] string tableTitle)
        {
HeroDeuFold = false;
_streetSyncFailed = false;
_handSyncFailed = false;
HeroLogicalIndex = heroIndex;
            CurrentChairs = (int[])chairs.Clone();
            CurrentBigBlind = bigBlind;

            string oldBasePath = _basePath;
            if (!string.IsNullOrEmpty(basePath)) _basePath = basePath;
            ConfigureNativeDllPath();

            var m = Regex.Match(tableTitle ?? "", @"\d{7,}");
            _targetTableId = m.Success
                ? m.Value
                : (tableTitle ?? "").Split(new char[] { ',', ' ', '-' })[0].Trim();

_numPlayers = numPlayers;
_buttonIndex = buttonIndex;

Log("===========================  NOVA MAO  ===========================");
Log($"[NewHand] table={_targetTableId} players={numPlayers} hero={heroIndex} btn={buttonIndex} bb={bigBlind}");
if (stacks != null) Log($"[NewHand] stacks=[{string.Join(", ", stacks)}]");

if (stacks == null || chairs == null ||
    numPlayers < 2 ||
    stacks.Length < numPlayers ||
    chairs.Length < numPlayers ||
    heroIndex < 0 || heroIndex >= numPlayers ||
    buttonIndex < 0 || buttonIndex >= numPlayers ||
    bigBlind <= 0)
{
    _gameState = null;
    _handSyncFailed = true;

    Log($"[NewHand] ERRO DE ESTADO: parametros invalidos. " +
        $"players={numPlayers}, hero={heroIndex}, btn={buttonIndex}, bb={bigBlind}, " +
        $"stacksLen={(stacks == null ? -1 : stacks.Length)}, chairsLen={(chairs == null ? -1 : chairs.Length)}. " +
        $"Mao bloqueada ate o proximo NewHand valido.");

    return;
}

            if (EnableXML)
                HHParser.AtualizarMesa(_targetTableId, numPlayers, bigBlind, heroIndex, chairs);
            else
                Log("[NewHand] MODO CORINGA - XML desativado.");

            try
            {
                string[] playerNames = new string[numPlayers];
                for (int i = 0; i < numPlayers; i++)
                    playerNames[i] = GetChairName(chairs[i]);

                Log($"[NewHand] Nomes G5: {string.Join(", ", playerNames)}");

                // [FIX 1] Log usa PhysicalChairToRealName[chairs[i]] -- nome ja
                // conhecido para cada cadeira fisica, sem depender do XML corrente.
                for (int i = 0; i < numPlayers; i++)
                {
                    string pos = GetPositionName(i, buttonIndex, numPlayers);
                    int physChair = chairs[i];
                    string realName = PhysicalChairToRealName.ContainsKey(physChair)
                        ? PhysicalChairToRealName[physChair]
                        : "?";
                    int count = SessionHandsLearned.ContainsKey(playerNames[i]) ? SessionHandsLearned[playerNames[i]] : 0;
                    string nivel = count == 0 ? "prior da populacao"
                                    : count < 5 ? $"aprendendo ({count} maos)"
                                                 : $"modelo proprio ({count} maos)";
                    Log($"[Modelagem]   {pos} c{physChair} ({playerNames[i]} = {realName}): {nivel}");
                }

                bool isHeadsUp = (numPlayers == 2);
                string statsFile = isHeadsUp ? "full_stats_list_hu.bin" : "full_stats_list_6max_2m.bin";
                var tableType = isHeadsUp ? TableType.HeadsUp : TableType.SixMax;

                if (_oppModeling == null || oldBasePath != _basePath || _wasHeadsUp != isHeadsUp)
                {
                    _basePath = basePath;
                    _wasHeadsUp = isHeadsUp;
                    string path = Path.Combine(_basePath, statsFile);
                    Log($"[NewHand] Carregando OpponentModeling: {path}");
                    var opts = new OpponentModeling.Options
                    {
                        recentHandsCount = 20,
                        priorNumBins = 100,
                        minSamples = 15,
                        maxSimilarPlayers = 80,
                        maxDifference = 0.2f,
                        maxBaseStatsSigma = 0.05f
                    };
                    _oppModeling = new OpponentModeling(path, tableType, opts);
                    Log("[NewHand] OpponentModeling carregado.");
                }
                else
                {
                    Log("[NewHand] Reutilizando OpponentModeling.");
                }

                _estimator?.Dispose();
                _gameState?.Dispose();
                _estimator = new ModelingEstimator(_oppModeling, PokerClient.PokerStars);
                _gameState = new BotGameState(
                    playerNames, stacks, heroIndex, buttonIndex, bigBlind,
                    PokerClient.PokerStars, tableType, _estimator,
                    randomlySampleActions: false, preFlopChartsLevel: 4);

                Log($"[PreFlopCharts] {_gameState.getPreFlopChartsInfo()}");

                _gameState.startNewHand();
                Log("[NewHand] SUCESSO - G5 pronto.");
                Log($"[NewHand] Proximo={GetPositionName(_gameState.getPlayerToActInd(), _buttonIndex, _numPlayers)}");
            }
            catch (Exception ex) { Log($"[NewHand] ERRO CRITICO: {ex.GetType().Name}: {ex.Message}"); }
        }

        [DllExport("G5Bridge_DealHoleCards", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void DealHoleCards(
            [MarshalAs(UnmanagedType.LPStr)] string c0,
            [MarshalAs(UnmanagedType.LPStr)] string c1)
        {
            Log($"[DealHoleCards] {c0} {c1}");
            if (_gameState == null) return;
try
{
    _gameState.dealHoleCards(new Card(c0), new Card(c1));
    Log("[DealHoleCards] OK.");
    DLog($"[Ranges] apos DealHoleCards: {GetOpponentRangesDiagnosticsForLog()}");
}
catch (Exception ex) { Log($"[DealHoleCards] ERRO: {ex.Message}"); }
        }

private static void InternalNewAction(int playerLogIdx, int actionType, int byAmount, string src)
{
    if (src != "XML Inject")
        Log($"[{src}] Posicao={GetPositionName(playerLogIdx, _buttonIndex, _numPlayers)}");

    if (_gameState == null) return;

    if (_handSyncFailed || _streetSyncFailed)
    {
        Log($"[{src}] Ignorado: mao bloqueada por falha de sincronia. " +
            $"handSyncFailed={_handSyncFailed}, streetSyncFailed={_streetSyncFailed}.");
        return;
    }

    try
    {
        int max = _gameState.getPlayers().Count, att = 0;

        while (_gameState.getPlayerToActInd() != playerLogIdx
               && _gameState.getPlayerToActInd() >= 0 && att < max)
        {
            int cur = _gameState.getPlayerToActInd();

            if (cur == HeroLogicalIndex || cur == _gameState.getHeroInd())
            {
                _handSyncFailed = true;

                Log($"[{src}] DESYNC GRAVE: tentativa de fold forcado no heroi. " +
                    $"Esperado={GetPositionName(cur, _buttonIndex, _numPlayers)}({cur}), " +
                    $"recebido={GetPositionName(playerLogIdx, _buttonIndex, _numPlayers)}({playerLogIdx}). " +
                    $"Bloqueando a mao ate a proxima NewHand.");
                return;
            }

            Log($"[{src}] fold forcado em {GetPositionName(cur, _buttonIndex, _numPlayers)}");
            _gameState.playerFolds();
            att++;
        }

        if (_gameState.getPlayerToActInd() != playerLogIdx)
        {
            _handSyncFailed = true;
            Log($"[{src}] DESYNC: apos tentativa de alinhamento, playerToAct=" +
                $"{GetPositionName(_gameState.getPlayerToActInd(), _buttonIndex, _numPlayers)}({_gameState.getPlayerToActInd()}), " +
                $"recebido={GetPositionName(playerLogIdx, _buttonIndex, _numPlayers)}({playerLogIdx}). " +
                $"Bloqueando a mao.");
            return;
        }

_gameState.playerActs((ActionType)actionType, byAmount);
Log($"[{src}] OK - {GetActionName(actionType)} by {byAmount}. " +
    $"Proximo={GetPositionName(_gameState.getPlayerToActInd(), _buttonIndex, _numPlayers)}");
DLog($"[Ranges] apos {src} {GetActionName(actionType)} by {byAmount}: {GetOpponentRangesDiagnosticsForLog()}");
    }
    catch (Exception ex)
    {
        _handSyncFailed = true;
        Log($"[{src}] ERRO: {ex.Message}. Bloqueando a mao ate a proxima NewHand.");
    }
}

        [DllExport("G5Bridge_NewAction", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void NewAction(int playerLogIdx, int actionType, int byAmount)
            => InternalNewAction(playerLogIdx, actionType, byAmount, "NewAction");

        private static string BoardToLog(Board board)
        {
            if (board == null || board.Cards == null || board.Cards.Count == 0)
                return "<vazio>";

            return string.Join(" ", board.Cards.Select(c => c.ToString()));
        }

        private static bool IsInvalidCard(Card card)
        {
            return card.rank == Card.Rank.Unknown || card.suit == Card.Suit.Unknown;
        }

        private static bool SameCard(Card a, Card b)
        {
            return a.ToInt() == b.ToInt();
        }

        private static bool CurrentBoardHasCard(Card card)
        {
            Board board = _gameState.getBoard();

            if (board == null || board.Cards == null)
                return false;

            return board.Cards.Any(c => SameCard(c, card));
        }

        private static bool CurrentBoardHasSameFlop(Card c0, Card c1, Card c2)
        {
            Board board = _gameState.getBoard();

            if (board == null || board.Cards == null || board.Cards.Count < 3)
                return false;

            return SameCard(board.Cards[0], c0) &&
                   SameCard(board.Cards[1], c1) &&
                   SameCard(board.Cards[2], c2);
        }

        private static void MarkStreetSyncFailure(string src, string reason)
        {
            _streetSyncFailed = true;
            Log($"[{src}] ERRO DE SINCRONIA DE STREET: {reason}. " +
                $"Board atual={BoardToLog(_gameState?.getBoard())}. " +
                "Bloqueando novas transicoes de street ate a proxima mao para nao corromper o GameContext.");
        }

        private static void InternalGoToNextStreet(string c0, string c1, string c2, int n, string src)
        {
            if (_gameState == null) return;

            try
            {
if (_handSyncFailed)
{
    Log($"[{src}] Street ignorada: mao bloqueada por desync anterior. Aguardando proxima mao.");
    return;
}

if (_streetSyncFailed)
{
    Log($"[{src}] Street ignorada: sincronizacao desta mao ja falhou. Aguardando proxima mao.");
    return;
}

                Street currentStreet = _gameState.getStreet();

                if (currentStreet == Street.River)
                {
                    Log($"[{src}] Street ignorada: jogo ja esta no river. Board={BoardToLog(_gameState.getBoard())}");
                    return;
                }

                if (n == 3)
                {
                    Card f0 = new Card(c0);
                    Card f1 = new Card(c1);
                    Card f2 = new Card(c2);

                    if (IsInvalidCard(f0) || IsInvalidCard(f1) || IsInvalidCard(f2))
                    {
                        MarkStreetSyncFailure(src, $"flop invalido recebido: '{c0} {c1} {c2}'");
                        return;
                    }

                    if (currentStreet != Street.PreFlop)
                    {
                        if (currentStreet == Street.Flop && CurrentBoardHasSameFlop(f0, f1, f2))
                        {
                            Log($"[{src}] Flop duplicado ignorado: {c0} {c1} {c2}");
                            return;
                        }

                        MarkStreetSyncFailure(src,
                            $"recebido flop '{c0} {c1} {c2}' quando a street atual era {currentStreet}");
                        return;
                    }

_gameState.goToNextStreet(new List<Card> { f0, f1, f2 });
Log($"[{src}] Flop: {c0} {c1} {c2}");
DLog($"[Ranges] apos Flop: {GetOpponentRangesDiagnosticsForLog()}");
                }
                else if (n == 1)
                {
                    Card card = new Card(c0);

                    if (IsInvalidCard(card))
                    {
                        MarkStreetSyncFailure(src, $"carta de turn/river invalida recebida: '{c0}'");
                        return;
                    }

                    if (currentStreet == Street.PreFlop)
                    {
                        MarkStreetSyncFailure(src,
                            $"recebida carta unica '{c0}' antes do flop");
                        return;
                    }

                    if (CurrentBoardHasCard(card))
                    {
                        Log($"[{src}] Carta duplicada ignorada: {c0}. Board={BoardToLog(_gameState.getBoard())}");
                        return;
                    }

_gameState.goToNextStreet(card);
Log($"[{src}] {_gameState.getStreet()}: {c0}");
DLog($"[Ranges] apos {_gameState.getStreet()}: {GetOpponentRangesDiagnosticsForLog()}");
                }
                else
                {
                    MarkStreetSyncFailure(src, $"numCards invalido: {n}");
                }
            }
            catch (Exception ex)
            {
                MarkStreetSyncFailure(src, ex.Message);
            }
        }

        [DllExport("G5Bridge_GoToNextStreet", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void GoToNextStreet(
            [MarshalAs(UnmanagedType.LPStr)] string c0,
            [MarshalAs(UnmanagedType.LPStr)] string c1,
            [MarshalAs(UnmanagedType.LPStr)] string c2,
            int numCards)
            => InternalGoToNextStreet(c0, c1, c2, numCards, "GoToNextStreet");

        [DllExport("G5Bridge_GetDecision", CallingConvention = CallingConvention.StdCall)]
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static DecisionResult GetDecision()
        {
            Log("------------------------------------------------");

            var result = new DecisionResult
            {
                actionType = -1,
                byAmount = 0,
                checkCallEV = 0f,
                betRaiseEV = 0f
            };

if (_gameState == null)
{
    Log("[GetDecision] ERRO: _gameState == null.");
    Log("------------------------------------------------");
    return result;
}

if (_handSyncFailed || _streetSyncFailed)
{
    Log($"[GetDecision] Mao bloqueada por falha de sincronia. " +
        $"handSyncFailed={_handSyncFailed}, streetSyncFailed={_streetSyncFailed}. Retornando actionType=-1.");
    Log("------------------------------------------------");
    return result;
}

            try
            {
                int playerToAct = _gameState.getPlayerToActInd();
                int heroInd = _gameState.getHeroInd();

                string posAct = GetPositionName(playerToAct, _buttonIndex, _numPlayers);
                string posHero = GetPositionName(heroInd, _buttonIndex, _numPlayers);

                Log($"[GetDecision] Street={_gameState.getStreet()} playerToAct={posAct}({playerToAct}) hero={posHero}({heroInd})");

                if (playerToAct != heroInd)
                {
                    Log("[GetDecision] AVISO: GetDecision chamado fora da vez do heroi. Retornando actionType=-1.");
                    Log("------------------------------------------------");
                    return result;
                }

                if (IsDiagnosticLogEnabled())
                    LogLoadedNativeModules();

                BotGameState.BotDecision d;

                if (_gameState.getStreet() == Street.PreFlop)
                {
                    DLog("[GetDecision] Chamando _gameState.calculateHeroAction() no preflop...");
                    d = _gameState.calculateHeroAction();
                }
                else
                {
                    double enhancedEquity;
                    string enhancedReason;

                    if (TryGetFreshOHEquity(out enhancedEquity, out enhancedReason))
                    {
                        DLog($"[GetDecision] EnhancedPrWin disponivel antes da arvore: equityOH={enhancedEquity:P2}. " +
                             "Uso correto: snapshot/equity floor; decisao abstrata pela arvore canonica completa.");
                    }
                    else
                    {
                        DLog($"[GetDecision] EnhancedPrWin ainda nao disponivel antes da arvore: {enhancedReason}. " +
                             "A arvore canonica completa sera chamada; o OH Equity Floor so sera aplicado se houver snapshot fresco depois.");
                    }

                    DLog("[GetDecision] FastPostFlopEV depreciado; chamando arvore completa canonica em _gameState.calculateHeroAction()...");
                    d = _gameState.calculateHeroAction();
                }

                result.actionType = (int)d.actionType;
                result.byAmount = d.byAmount;
                result.checkCallEV = d.checkCallEV;
                result.betRaiseEV = d.betRaiseEV;

                bool ohEquityFloorApplied = ApplyOHEquityCallFloor(ref result);

                Log($"[GetDecision] DECISAO: {GetActionName(result.actionType)} by {result.byAmount}");
                Log($"[GetDecision] EVs: cc={result.checkCallEV:F3} br={result.betRaiseEV:F3}");
                Log($"[GetDecision] OH Equity Floor aplicado: {ohEquityFloorApplied}");
                Log($"[GetDecision] Motivo: {d.message}");

                if (result.actionType == 0)
                {
                    HeroDeuFold = true;
                    if (EnableXML)
                        Log("[Bridge] Heroi deu Fold - HHParser assumira injecao pos-fold.");
                    else
                        SaveCurrentHandToModeling("Fold sem XML");
                }
            }
            catch (SEHException ex)
            {
                Log($"[GetDecision] ERRO NATIVO em calculateHeroAction. HResult=0x{ex.HResult:X8}: {ex.Message}");

                if (IsDiagnosticLogEnabled())
                {
                    Log("[GetDecision] Detalhes da SEHException:");
                    Log(ex.ToString());
                    LogLoadedNativeModules();
                }
            }
            catch (Exception ex)
            {
                Log($"[GetDecision] ERRO {ex.GetType().FullName}. HResult=0x{ex.HResult:X8}: {ex.Message}");

                if (IsDiagnosticLogEnabled())
                {
                    Log("[GetDecision] Detalhes da Exception:");
                    Log(ex.ToString());
                }
            }

            Log("------------------------------------------------");
            return result;
        }

        private static void LogLoadedNativeModules()
        {
            try
            {
                Log("[Modules] ===== Modulos carregados relevantes =====");

                foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                {
                    string name = m.ModuleName.ToLowerInvariant();
                    string path = m.FileName.ToLowerInvariant();

                    if (name.Contains("decision") ||
                        name.Contains("tbb") ||
                        name.Contains("g5") ||
                        name.Contains("msvc") ||
                        name.Contains("vcruntime") ||
                        path.Contains("pressure"))
                    {
                        Log($"[Modules] {m.ModuleName} -> {m.FileName}");
                    }
                }

                Log("[Modules] =======================================");
            }
            catch (Exception ex)
            {
                Log($"[Modules] ERRO: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void SaveCurrentHandToModeling(string trigger)
        {
            if (_oppModeling == null || _gameState == null) return;
            try
            {
                var hand = _gameState.getCurrentHand();
                if (hand == null || hand.PlayersNames == null || hand.PlayersNames.Count == 0)
                {
                    Log($"[Modelagem] Hand vazia em '{trigger}', ignorando.");
                    return;
                }

                _oppModeling.addHand(hand);

                string heroName = hand.HeroName ?? "";
                var learned = hand.PlayersNames.Where(p => p != heroName).ToList();
                foreach (string p in learned)
                {
                    if (!SessionHandsLearned.ContainsKey(p)) SessionHandsLearned[p] = 0;
                    SessionHandsLearned[p]++;
                }

                Log($"[Modelagem] OK [{trigger}] {hand.PlayersNames.Count} jogadores, " +
                    $"{learned.Count} modelados: " +
                    $"{string.Join(", ", learned.Select(p => $"{p}({SessionHandsLearned[p]})"))}");
            }
            catch (Exception ex) { Log($"[Modelagem] ERRO: {ex.Message}"); }
        }

        public static void InjectXMLAction(int logIdx, int actionType, int byAmount)
        {
            Log($"[XML Inject] Logico {logIdx} -> {GetActionName(actionType)} ({byAmount})");
            try { InternalNewAction(logIdx, actionType, byAmount, "XML Inject"); }
            catch (Exception ex) { Log($"[XML Inject] ERRO: {ex.Message}"); }
        }

        public static void InjectXMLNextStreet(List<string> cards)
        {
            try
            {
                Log($"[XML Inject] Street: {string.Join(" ", cards)}");
                if (cards.Count == 3) InternalGoToNextStreet(cards[0], cards[1], cards[2], 3, "XML Inject");
                else if (cards.Count == 1) InternalGoToNextStreet(cards[0], "", "", 1, "XML Inject");
            }
            catch (Exception ex) { Log($"[XML Inject] ERRO: {ex.Message}"); }
        }

        public static void InjectXMLShowdown(int logIdx, string c1, string c2)
        {
            if (_gameState == null) return;
            Log($"[XML Inject] SHOWDOWN! Logico {logIdx}: {c1} {c2}");
        }

public static void Log(string message)
{
    try
    {
        string folder = string.IsNullOrEmpty(_basePath)
            ? Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            : _basePath;

        string logDir = Path.Combine(folder, "logs");
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

        string tablePart = string.IsNullOrWhiteSpace(_targetTableId)
            ? "sem_tabela"
            : Regex.Replace(_targetTableId, @"[^\w\-]+", "_");

        string logFile = $"G5Bridge_{tablePart}_pid{Process.GetCurrentProcess().Id}.log";

        lock (_logLock)
            File.AppendAllText(Path.Combine(logDir, logFile),
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
    }
    catch { }
}
    }

    // =========================================================================
    // HHParser
    // =========================================================================
    public static class HHParser
    {
        private static FileSystemWatcher _watcher;
        private static string _targetTableId = "";
        private static string _knownFilePath = "";
        private static string _heroName = "";

        private static string _currentGameCode = "";
        private static int _injectedActCount = 0;
        private static int _injectedStreetCount = 0;
        private static bool _showdownInjected = false;
        private static bool _isHandComplete = false;
        private static bool _modelingSaved = false;
        private static int _numPlayers = 0;

        private struct HandSnapshot
        {
            public int heroLogIdx;
            public int numPlayers;
            public int[] chairs;
        }

        private static readonly Queue<HandSnapshot> _snapshotQueue = new Queue<HandSnapshot>();

        private static int _snapshotHeroLogicalIndex = 0;
        private static int _snapshotNumPlayers = 0;
        private static int[] _snapshotChairs = null;

        private static Dictionary<string, int> _nameToLogical = new Dictionary<string, int>();

        public static void AtualizarMesa(string tableId, int numPlayers, int bbAmount, int heroIndex, int[] chairs)
        {
            _numPlayers = numPlayers;

            var snap = new HandSnapshot
            {
                heroLogIdx = heroIndex,
                numPlayers = numPlayers,
                chairs = (int[])chairs.Clone()
            };
            _snapshotQueue.Enqueue(snap);
            Bridge.Log($"[HHParser] Snapshot enfileirado: hero={heroIndex} n={numPlayers} chairs=[{string.Join(",", chairs)}] (fila={_snapshotQueue.Count})");

            if (_targetTableId == tableId) return;
            Bridge.Log($"[HHParser] Nova mesa alvo: {tableId}");
            _targetTableId = tableId;
            _knownFilePath = "";

            string folder = Bridge.XMLFolderPath;
            if (Directory.Exists(folder) && _watcher == null)
            {
                Bridge.Log($"[HHParser] FileSystemWatcher: {folder}");
                _watcher = new FileSystemWatcher(folder, "*.xml")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Changed += OnFileChanged;
                _watcher.EnableRaisingEvents = true;
            }
        }

        private static void OnFileChanged(object src, FileSystemEventArgs e)
        {
            if (!Bridge.EnableXML) return;
            if (!string.IsNullOrEmpty(_knownFilePath) && e.FullPath != _knownFilePath) return;
            Thread.Sleep(100);
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".xml");
            try
            {
                File.Copy(e.FullPath, tmp, true);
                string xml = File.ReadAllText(tmp);
                if (!xml.Contains(_targetTableId)) return;
                if (string.IsNullOrEmpty(_knownFilePath))
                {
                    _knownFilePath = e.FullPath;
                    Bridge.Log($"[HHParser] Arquivo vinculado: {Path.GetFileName(_knownFilePath)}");
                }
                ProcessLatestHand(xml);
            }
            catch { }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        private static void ProcessLatestHand(string xml)
        {
            try
            {
                if (string.IsNullOrEmpty(_heroName))
                {
                    var nm = Regex.Match(xml, @"<nickname>([^<]+)</nickname>");
                    if (nm.Success)
                    {
                        _heroName = nm.Groups[1].Value;
                        Bridge.Log($"[HHParser] Nickname do heroi: {_heroName}");
                    }
                }

                string[] games = xml.Split(new[] { "<game " }, StringSplitOptions.RemoveEmptyEntries);
                if (games.Length == 0) return;

                string lastBlock = "<game " + games[games.Length - 1];
                var m = Regex.Match(lastBlock, @"gamecode=""(\d+)""");
                if (!m.Success) return;

                string code = m.Groups[1].Value;
                bool hasEnded = lastBlock.Contains("</game>");

                if (code != _currentGameCode)
                {
                    _currentGameCode = code;
                    _injectedActCount = 0;
                    _injectedStreetCount = 0;
                    _showdownInjected = false;
                    _isHandComplete = false;
                    _modelingSaved = false;
                    _nameToLogical.Clear();

                    if (_snapshotQueue.Count > 0)
                    {
                        var snap = _snapshotQueue.Dequeue();
                        _snapshotHeroLogicalIndex = snap.heroLogIdx;
                        _snapshotNumPlayers = snap.numPlayers;
                        _snapshotChairs = snap.chairs;
                        Bridge.Log($"[HHParser] Nova mao: {code} | snapshot hero={_snapshotHeroLogicalIndex} n={_snapshotNumPlayers} chairs=[{string.Join(",", _snapshotChairs)}] (fila={_snapshotQueue.Count})");
                    }
                    else
                    {
                        _snapshotHeroLogicalIndex = Bridge.HeroLogicalIndex;
                        _snapshotNumPlayers = _numPlayers;
                        _snapshotChairs = Bridge.CurrentChairs != null ? (int[])Bridge.CurrentChairs.Clone() : null;
                        Bridge.Log($"[HHParser] Nova mao: {code} | fila vazia - fallback hero={_snapshotHeroLogicalIndex} n={_snapshotNumPlayers}");
                    }

                    BuildNameToLogical(lastBlock);
                }
                else if (_isHandComplete) return;

                if (Bridge.HeroDeuFold)
                    TranslateHandToG5(lastBlock);

                if (hasEnded && !_modelingSaved)
                {
                    Bridge.Log($"[HHParser] Mao {code} encerrada - salvando no modelo.");
                    Bridge.SaveCurrentHandToModeling($"FimDaMao_{code}");
                    _modelingSaved = true;
                    _isHandComplete = true;
                }
            }
            catch (Exception ex) { Bridge.Log($"[HHParser] ERRO em ProcessLatestHand: {ex.Message}"); }
        }

        // =========================================================================
        // BuildNameToLogical
        //
        // [FIX 2] A protecao contra troca falsa e aplicada APENAS quando
        // total == nPlayers (XML completo). Quando total < nPlayers (waiting for BB),
        // os logIdx calculados modulo 'total' nao correspondem ao array chairs do
        // snapshot (indexado por nPlayers), entao qualquer comparacao produz falsos
        // positivos. Neste caso aceitamos o mapeamento parcial sem protecao.
        // =========================================================================
        private static void BuildNameToLogical(string gameXml)
        {
            _nameToLogical.Clear();

            int heroLogIdx = _snapshotHeroLogicalIndex;
            int nPlayers = _snapshotNumPlayers > 0 ? _snapshotNumPlayers : _numPlayers;
            int[] chairs = _snapshotChairs;

            // 1. Todos os jogadores do XML com seus xmlSeats
            var allPlayers = new Dictionary<string, int>(); // nome -> xmlSeat
            foreach (Match m in Regex.Matches(gameXml, @"<player[^>]+name=""([^""]+)""[^>]+seat=""(\d+)"""))
                allPlayers[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);

            if (!allPlayers.ContainsKey(_heroName))
            {
                Bridge.Log("[HHParser] BuildNameToLogical: heroi nao encontrado no XML ainda.");
                return;
            }

            // 2. Jogadores que AGIRAM (exclui sit-outs e "waiting for BB")
            var acted = new HashSet<string>();
            foreach (Match a in Regex.Matches(gameXml, @"<action[^>]+player=""([^""]+)"""))
                acted.Add(a.Groups[1].Value);

            List<string> activePlayers;
            if (acted.Count > 0)
                activePlayers = allPlayers.Keys.Where(n => acted.Contains(n)).ToList();
            else
                activePlayers = allPlayers.Keys.ToList();

            // 3. Filtra para no maximo nPlayers jogadores
            if (activePlayers.Count > nPlayers)
            {
                var actionCount = new Dictionary<string, int>();
                foreach (Match a in Regex.Matches(gameXml, @"<action[^>]+player=""([^""]+)"""))
                {
                    string n = a.Groups[1].Value;
                    if (!actionCount.ContainsKey(n)) actionCount[n] = 0;
                    actionCount[n]++;
                }
                var withoutHero = activePlayers
                    .Where(n => n != _heroName)
                    .OrderByDescending(n => actionCount.ContainsKey(n) ? actionCount[n] : 0)
                    .Take(nPlayers - 1)
                    .ToList();
                activePlayers = new List<string> { _heroName };
                activePlayers.AddRange(withoutHero);
            }

            if (!activePlayers.Contains(_heroName))
                activePlayers.Add(_heroName);
            if (activePlayers.Count > nPlayers)
                activePlayers = activePlayers.Take(nPlayers).ToList();

            // 4. Ordena por xmlSeat
            activePlayers.Sort((a, b) => allPlayers[a].CompareTo(allPlayers[b]));

            // 5. Ancora no heroi e calcula logIdx para cada jogador
            int heroIdx = activePlayers.IndexOf(_heroName);
            int total = activePlayers.Count;

            var candidateLogical = new Dictionary<int, string>(); // logIdx -> nome
            for (int i = 0; i < total; i++)
            {
                int logIdx = (i - heroIdx + heroLogIdx + total) % total;
                candidateLogical[logIdx] = activePlayers[i];
            }

            // -----------------------------------------------------------------------
            // [FIX 2] Protecao contra troca falsa — SO quando total == nPlayers.
            //
            // Quando total == nPlayers: cada logIdx mapeia diretamente para
            // chairs[logIdx] no snapshot. A comparacao com PhysicalChairToRealName
            // e confiavel.
            //
            // Quando total < nPlayers: ha jogador ausente no XML (waiting for BB).
            // Os logIdx sao calculados modulo 'total' != 'nPlayers', entao
            // chairs[logIdx] nao e a cadeira fisica correta para aquele nome.
            // NÃO aplicamos a protecao — aceitamos mapeamento parcial com aviso.
            // -----------------------------------------------------------------------
            Dictionary<int, string> finalLogical;

            if (total == nPlayers && chairs != null && chairs.Length >= nPlayers)
            {
                // Indice inverso: nome -> physicalChair ja conhecido
                var knownChairByName = new Dictionary<string, int>();
                foreach (var kvp in Bridge.PhysicalChairToRealName)
                    if (!string.IsNullOrEmpty(kvp.Value))
                        knownChairByName[kvp.Value] = kvp.Key;

                finalLogical = new Dictionary<int, string>();
                bool hadDiscard = false;

                foreach (var kvp in candidateLogical)
                {
                    int logIdx = kvp.Key;
                    string name = kvp.Value;

                    if (logIdx < chairs.Length && knownChairByName.TryGetValue(name, out int knownPhys))
                    {
                        int candidatePhys = chairs[logIdx];
                        if (knownPhys != candidatePhys)
                        {
                            // O nome esta sendo atribuido a uma cadeira fisica diferente
                            // da que ja conhecemos. Com total==nPlayers isso indica
                            // um jogador recentemente sentado ou troca real de cadeira.
                            // Descartamos e deixamos UpdateRealNames detectar a troca real.
                            Bridge.Log($"[HHParser] Descarte: '{name}' conhecida em c{knownPhys}, " +
                                       $"mas seria c{candidatePhys} (logico {logIdx}). " +
                                       $"Jogador recentemente sentado ou troca real pendente.");
                            hadDiscard = true;
                            continue;
                        }
                    }

                    finalLogical[logIdx] = name;
                    _nameToLogical[name] = logIdx;
                }

                if (hadDiscard)
                    Bridge.Log($"[HHParser] Mapeamento apos verificacao: {finalLogical.Count}/{candidateLogical.Count} aceitos.");
            }
            else
            {
                // total < nPlayers: XML incompleto — aceita sem protecao
                finalLogical = candidateLogical;
                foreach (var kvp in candidateLogical)
                    _nameToLogical[kvp.Value] = kvp.Key;

                if (total < nPlayers)
                    Bridge.Log($"[HHParser] XML parcial: {total}/{nPlayers} jogadores " +
                               $"(possivel 'waiting for BB'). Mapeamento aceito sem validacao de cadeira.");
            }

            // 6. Detecta trocas reais usando chairs do SNAPSHOT desta mao
            Bridge.UpdateRealNames(finalLogical, chairs);

            string status = (total == nPlayers) ? "completo" : $"parcial {total}/{nPlayers}";
            Bridge.Log($"[HHParser] Mapeamento [{status}] ({finalLogical.Count} aceitos / {allPlayers.Count} no XML, " +
                       $"snapshotHero={heroLogIdx}, snapshotN={nPlayers}):");
            foreach (var kvp in _nameToLogical.OrderBy(x => x.Value))
                Bridge.Log($"[HHParser]   {kvp.Key} -> idx {kvp.Value} " +
                           $"({Bridge.GetPositionName(kvp.Value, Bridge._buttonIndex, _numPlayers)})");
        }

        private static void TranslateHandToG5(string gameXml)
        {
            try
            {
                if (_nameToLogical.Count == 0)
                    BuildNameToLogical(gameXml);

                string[] rounds = gameXml.Split(new[] { "<round no=" }, StringSplitOptions.RemoveEmptyEntries);
                bool ignorar = true;
                int actCounter = 0;
                int streetCounter = 0;

                foreach (string rb in rounds)
                {
                    if (!IsValidRound(rb)) continue;

                    if (_injectedActCount == actCounter)
                        Bridge.Log($"[HHParser] === Round {rb[1]} ===");

                    if (!ignorar)
                    {
                        var bm = Regex.Match(rb, @"<cards type=""(Flop|Turn|River)"">([^<]+)</cards>");
                        if (bm.Success)
                        {
                            if (streetCounter >= _injectedStreetCount)
                            {
                                var g5c = bm.Groups[2].Value.Split(' ').Select(ConvertCard).ToList();
                                Bridge.InjectXMLNextStreet(g5c);
                                _injectedStreetCount++;
                            }
                            streetCounter++;
                        }
                    }

                    foreach (Match a in Regex.Matches(rb,
                        @"<action[^>]+player=""([^""]+)""[^>]+sum=""[€$]?(\d+[\.,]\d+|0)""[^>]+type=""(\d+)"""))
                    {
                        string pName = a.Groups[1].Value;
                        int amount = ParseAmount(a.Groups[2].Value);
                        int rsType = int.Parse(a.Groups[3].Value);

                        if (ignorar)
                        {
                            if (pName == _heroName && rsType == 0)
                            {
                                ignorar = false;
                                Bridge.Log("[HHParser] Fold do heroi - injecao ativada.");
                                if (_nameToLogical.TryGetValue(pName, out int hi))
                                    Bridge.InjectXMLAction(hi, 0, 0);
                            }
                            continue;
                        }

                        if (actCounter >= _injectedActCount)
                        {
                            Bridge.Log($"[HHParser] Live: '{pName}' tipo={rsType} amount={amount}");
                            int g5 = RsToG5(rsType);
                            if (g5 >= 0 && _nameToLogical.TryGetValue(pName, out int li))
                                Bridge.InjectXMLAction(li, g5, amount);
                            _injectedActCount++;
                        }
                        actCounter++;
                    }
                }

                if (!ignorar && !_showdownInjected && gameXml.Contains("</game>"))
                {
                    foreach (Match s in Regex.Matches(gameXml,
                        @"<cards player=""([^""]+)"" type=""Pocket"">([^<]+)</cards>"))
                    {
                        string pName = s.Groups[1].Value;
                        string cStr = s.Groups[2].Value;
                        if (pName == _heroName || cStr == "X X") continue;
                        if (_nameToLogical.TryGetValue(pName, out int li))
                        {
                            var cc = cStr.Split(' ');
                            if (cc.Length == 2)
                                Bridge.InjectXMLShowdown(li, ConvertCard(cc[0]), ConvertCard(cc[1]));
                        }
                    }
                    _showdownInjected = true;
                }
            }
            catch (Exception ex) { Bridge.Log($"[HHParser] ERRO em TranslateHandToG5: {ex.Message}"); }
        }

        private static bool IsValidRound(string rb)
            => rb.StartsWith("\"1\"") || rb.StartsWith("\"2\"") ||
               rb.StartsWith("\"3\"") || rb.StartsWith("\"4\"");

        private static int ParseAmount(string s)
            => (int)Math.Round(double.Parse(s.Replace(",", "."),
               System.Globalization.CultureInfo.InvariantCulture) * 100);

        private static int RsToG5(int t)
        {
            switch (t)
            {
                case 0: return 0;
                case 3: return 2;
                case 4: return 1;
                case 5: return 4;
                case 23: return 4;
                default: return -1;
            }
        }

        private static string ConvertCard(string c)
        {
            if (string.IsNullOrEmpty(c) || c == "X X" || c.Length < 2) return "";
            string rank = c.Substring(1);
            if (rank == "10") rank = "T";
            return rank + char.ToLower(c[0]);
        }
    }
}