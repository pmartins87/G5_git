using G5.Logic;
using G5.Logic.Estimators;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace G5.Logic.Tests
{
    public sealed class AcademicRegressionReport
    {
        public int Total { get; private set; }
        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public List<string> Lines { get; private set; }

        public AcademicRegressionReport()
        {
            Lines = new List<string>();
        }

        public void Add(bool passed, string name, string details)
        {
            Total++;

            if (passed)
            {
                Passed++;
                Lines.Add($"[OK]   {name} :: {details}");
            }
            else
            {
                Failed++;
                Lines.Add($"[FAIL] {name} :: {details}");
            }
        }

        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("============================================================");
            sb.AppendLine("G5 Academic Regression Suite - Phase 8");
            sb.AppendLine("Simulacao offline academica com fichas/pontos ficticios.");
            sb.AppendLine("============================================================");
            sb.AppendLine($"Total={Total}, Passed={Passed}, Failed={Failed}");
            sb.AppendLine("------------------------------------------------------------");

            foreach (string line in Lines)
                sb.AppendLine(line);

            sb.AppendLine("------------------------------------------------------------");
            sb.AppendLine(Failed == 0 ? "RESULTADO FINAL: PASS" : "RESULTADO FINAL: FAIL");
            return sb.ToString();
        }

        public void Save(string path)
        {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(path, ToText());
        }
    }

    internal sealed class ScriptedBetRaiseAmountEstimator : IBetRaiseAmountEstimator
    {
        public float RootCheckCallEV { get; set; }
        public float RootBetRaiseEV { get; set; }
        public Dictionary<int, EvPair> MultiSizeEV { get; private set; }
        public int NewActionCount { get; private set; }
        public int FlopShownCount { get; private set; }

        public ScriptedBetRaiseAmountEstimator(float checkCallEV, float betRaiseEV)
        {
            RootCheckCallEV = checkCallEV;
            RootBetRaiseEV = betRaiseEV;
            MultiSizeEV = new Dictionary<int, EvPair>();
        }

        public void Dispose()
        {
        }

        public void newAction(ActionType actionType, BotGameState gameState)
        {
            NewActionCount++;
        }

        public void flopShown(Board board, HoleCards holeCards)
        {
            FlopShownCount++;
        }

        public void newHand(BotGameState gameState)
        {
        }

        public void estimateEV(out float checkCallEV, out float betRaiseEV, BotGameState gameState)
        {
            checkCallEV = RootCheckCallEV;
            betRaiseEV = RootBetRaiseEV;
        }

        public void estimateEVForBetRaiseAmount(out float checkCallEV, out float betRaiseEV, BotGameState gameState, int forcedBetRaiseAmount)
        {
            EvPair pair;

            if (MultiSizeEV.TryGetValue(forcedBetRaiseAmount, out pair))
            {
                checkCallEV = pair.CheckCallEV;
                betRaiseEV = pair.BetRaiseEV;
                return;
            }

            // Fallback deterministico: permite que a suite rode mesmo se o gerador
            // de candidatos mudar levemente em fases futuras.
            int pot = Math.Max(1, gameState.potSize() + gameState.getAmountToCall());
            float pressure = forcedBetRaiseAmount / (float)Math.Max(1, pot);

            checkCallEV = RootCheckCallEV;
            betRaiseEV = RootBetRaiseEV - Math.Abs(pressure - 0.66f) * 3.0f;
        }
    }

    internal struct EvPair
    {
        public float CheckCallEV;
        public float BetRaiseEV;

        public EvPair(float cc, float br)
        {
            CheckCallEV = cc;
            BetRaiseEV = br;
        }
    }

    internal struct ScriptedAction
    {
        public ActionType Type;
        public int Amount;

        public ScriptedAction(ActionType type, int amount)
        {
            Type = type;
            Amount = amount;
        }
    }

    internal enum ExpectedDecisionKind
    {
        CheckOrCall,
        Aggressive,
        Fold,
        AllIn,
        NoAction
    }

    internal sealed class Scenario
    {
        public string Name;
        public int NumPlayers = 2;
        public int HeroIndex = 0;
        public int ButtonIndex = 0;
        public int BigBlind = 10;
        public int Stack = 1000;
        public string HeroCards = "AsKd";
        public string Board = "";
        public float CheckCallEV = 0.0f;
        public float BetRaiseEV = 0.0f;
        public List<ScriptedAction> ActionsBeforeHero = new List<ScriptedAction>();
        public ExpectedDecisionKind Expected = ExpectedDecisionKind.CheckOrCall;
        public bool ExpectDiagnostic = true;
        public bool ExpectMultiSize = false;
        public bool DoNotAdvanceToHero = false;
    }

    public static class AcademicRegressionTests
    {
        private const int DEFAULT_BB = 10;

        public static AcademicRegressionReport RunAll(string outputPath = null)
        {
            EnsureMinimalPreFlopCharts();

            AcademicRegressionReport report = new AcademicRegressionReport();

            RunStateValidationTests(report);
            RunDecisionScenarios(report, BuildDecisionScenarios());

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                outputPath = Path.Combine(folder, "G5AcademicRegression_phase8.log");
            }

            report.Save(outputPath);
            return report;
        }

        private static void RunStateValidationTests(AcademicRegressionReport report)
        {
            ExpectTrue(report, "threshold all-in 79% nao converte", !BotGameState.ShouldConvertToAllInByCommitmentForRegression(790, 1000), "790/1000 deve permanecer bet/raise comum");
            ExpectTrue(report, "threshold all-in 80% converte", BotGameState.ShouldConvertToAllInByCommitmentForRegression(800, 1000), "800/1000 deve converter para all-in");
            ExpectTrue(report, "threshold all-in acima do stack converte", BotGameState.ShouldConvertToAllInByCommitmentForRegression(1200, 1000), "1200/1000 deve converter para all-in");

            ExpectThrows(report, "board rejeita carta duplicada", delegate
            {
                Board board = new Board();
                board.AddCard(new Card("Ah"));
                board.AddCard(new Card("Ah"));
            });

            ExpectThrows(report, "preflop rejeita street com 1 carta", delegate
            {
                var estimator = new ScriptedBetRaiseAmountEstimator(0, 0);
                using (BotGameState game = CreateGame(2, 0, 0, DEFAULT_BB, 1000, estimator, "AsKd"))
                {
                    game.goToNextStreet(new Card("Ah"));
                }
            });

            ExpectThrows(report, "flop rejeita quarta carta duplicada", delegate
            {
                var estimator = new ScriptedBetRaiseAmountEstimator(0, 0);
                using (BotGameState game = CreateGame(2, 0, 0, DEFAULT_BB, 1000, estimator, "AsKd"))
                {
                    game.goToNextStreet(ParseCards("Ah7d2c"));
                    game.goToNextStreet(new Card("Ah"));
                }
            });
        }

        private static List<Scenario> BuildDecisionScenarios()
        {
            List<Scenario> s = new List<Scenario>();

            // Preflop: spots básicos e atípicos.
            s.Add(new Scenario { Name = "HU BU/SB abre AKo", NumPlayers = 2, HeroIndex = 0, ButtonIndex = 0, HeroCards = "AsKd", CheckCallEV = 1.0f, BetRaiseEV = 5.0f, Expected = ExpectedDecisionKind.Aggressive });
            s.Add(new Scenario { Name = "HU BB paga raise pequeno com AQs", NumPlayers = 2, HeroIndex = 1, ButtonIndex = 0, HeroCards = "AhQh", ActionsBeforeHero = A(ActionType.Raise, 20), CheckCallEV = 4.0f, BetRaiseEV = 2.0f, Expected = ExpectedDecisionKind.CheckOrCall });
            s.Add(new Scenario { Name = "HU BB folda lixo vs raise", NumPlayers = 2, HeroIndex = 1, ButtonIndex = 0, HeroCards = "7c2d", ActionsBeforeHero = A(ActionType.Raise, 80), CheckCallEV = -5.0f, BetRaiseEV = -9.0f, Expected = ExpectedDecisionKind.Fold });
            s.Add(new Scenario { Name = "HU BB isola limp com AA", NumPlayers = 2, HeroIndex = 1, ButtonIndex = 0, HeroCards = "AsAd", ActionsBeforeHero = A(ActionType.Call, 5), CheckCallEV = 1.0f, BetRaiseEV = 9.0f, Expected = ExpectedDecisionKind.Aggressive });
            s.Add(new Scenario { Name = "3way BU abre KQs", NumPlayers = 3, HeroIndex = 0, ButtonIndex = 0, HeroCards = "KsQs", CheckCallEV = 1.0f, BetRaiseEV = 4.0f, Expected = ExpectedDecisionKind.Aggressive });
            s.Add(new Scenario { Name = "3way BB defende vs open", NumPlayers = 3, HeroIndex = 2, ButtonIndex = 0, HeroCards = "JcTc", ActionsBeforeHero = A(ActionType.Raise, 30, ActionType.Fold, 0), CheckCallEV = 3.0f, BetRaiseEV = 0.5f, Expected = ExpectedDecisionKind.CheckOrCall });
            s.Add(new Scenario { Name = "3way BB folda vs open grande", NumPlayers = 3, HeroIndex = 2, ButtonIndex = 0, HeroCards = "9c3d", ActionsBeforeHero = A(ActionType.Raise, 120, ActionType.Fold, 0), CheckCallEV = -7.0f, BetRaiseEV = -4.0f, Expected = ExpectedDecisionKind.Fold });
            s.Add(new Scenario { Name = "6max CO abre par medio", NumPlayers = 6, HeroIndex = 5, ButtonIndex = 0, HeroCards = "8s8d", ActionsBeforeHero = A(ActionType.Fold, 0, ActionType.Fold, 0, ActionType.Fold, 0), CheckCallEV = 0.5f, BetRaiseEV = 3.5f, Expected = ExpectedDecisionKind.Aggressive });

            // Flops secos, molhados, monotone e pareados.
            s.Add(Post("Flop seco IP c-bet A72r", "AsKd", "Ah7d2c", 0, 0, A(ActionType.Check, 0), 1.0f, 6.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("Flop seco OOP check com SDV", "Ac7c", "Ad7h2s", 1, 0, A(), 4.0f, 2.0f, ExpectedDecisionKind.CheckOrCall, false));
            s.Add(Post("Flop conectado 987 two-tone agressivo", "TsJs", "9s8s7d", 0, 0, A(ActionType.Check, 0), 1.5f, 7.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("Flop monotone sem equity check", "AsKd", "Qh8h3h", 0, 0, A(ActionType.Check, 0), 3.0f, 0.0f, ExpectedDecisionKind.CheckOrCall, false));
            s.Add(Post("Flop pareado raise de valor", "AhAd", "7c7d2s", 0, 0, A(ActionType.Check, 0), 1.0f, 8.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("Flop facing small bet call", "KsQs", "Kh8d2c", 0, 0, A(ActionType.Bet, 20), 5.0f, 2.0f, ExpectedDecisionKind.CheckOrCall, false));
            s.Add(Post("Flop facing overbet fold", "9c4d", "AhKd7s", 0, 0, A(ActionType.Bet, 160), -6.0f, -8.0f, ExpectedDecisionKind.Fold, false));
            s.Add(Post("Flop draw forte semi-bluff", "AhJh", "Th8h2c", 0, 0, A(ActionType.Check, 0), 1.0f, 9.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("Flop top pair board molhado prefere call", "AsTc", "AhJh9h", 0, 0, A(ActionType.Bet, 60), 4.5f, 1.2f, ExpectedDecisionKind.CheckOrCall, false));

            // Turns.
            s.Add(Post("Turn completa flush agressivo", "AhKh", "Qh8h3c2h", 0, 0, A(ActionType.Check, 0), 2.0f, 10.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("Turn brick segundo barril", "AsAd", "Jh7d2c3s", 0, 0, A(ActionType.Check, 0), 1.0f, 7.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("Turn scare card controla pote", "KsQd", "Kh9h7cAh", 0, 0, A(ActionType.Check, 0), 5.0f, 1.0f, ExpectedDecisionKind.CheckOrCall, false));
            s.Add(Post("Turn facing shove fold", "QsJd", "AhTd8c2s", 0, 0, A(ActionType.AllIn, 900), -10.0f, -12.0f, ExpectedDecisionKind.Fold, false));
            s.Add(Post("Turn facing bet call draw", "QhJh", "Th9h2c4s", 0, 0, A(ActionType.Bet, 70), 4.0f, 2.0f, ExpectedDecisionKind.CheckOrCall, false));
            s.Add(Post("Turn raise all-in por SPR baixo", "AsAh", "Ks8d2c3h", 0, 0, A(ActionType.Bet, 120), 2.0f, 14.0f, ExpectedDecisionKind.Aggressive, true));

            // Rivers.
            s.Add(Post("River nuts value bet", "AhKh", "QhJh2c3sTh", 0, 0, A(ActionType.Check, 0), 2.0f, 15.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("River showdown value check", "AsQd", "Ah7c2s3d9h", 0, 0, A(ActionType.Check, 0), 5.0f, 2.0f, ExpectedDecisionKind.CheckOrCall, false));
            s.Add(Post("River air facing bet fold", "8s6d", "AhKd7c2s3h", 0, 0, A(ActionType.Bet, 100), -4.0f, -9.0f, ExpectedDecisionKind.Fold, false));
            s.Add(Post("River bluff candidato", "As5s", "KhQd8c2h3s", 0, 0, A(ActionType.Check, 0), 0.0f, 6.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("River call pot odds", "KcQd", "Ks8s3h2d2c", 0, 0, A(ActionType.Bet, 35), 3.0f, -1.0f, ExpectedDecisionKind.CheckOrCall, false));
            s.Add(Post("River fold contra bet grande", "JcTc", "AsAd7h4c2d", 0, 0, A(ActionType.Bet, 250), -8.0f, -6.0f, ExpectedDecisionKind.Fold, false));

            // Multiway.
            s.Add(Post("3way flop dois checks aposta", "AhQh", "Qd8c2s", 0, 0, A(ActionType.Check, 0, ActionType.Check, 0), 1.0f, 6.0f, ExpectedDecisionKind.Aggressive, true, 3));
            s.Add(Post("3way flop bet e call prefere call", "KhJh", "Th8h2c", 0, 0, A(ActionType.Bet, 40, ActionType.Call, 40), 5.0f, 2.0f, ExpectedDecisionKind.CheckOrCall, false, 3));
            s.Add(Post("3way turn fold contra pressao", "9s8s", "AhKd7c2d", 0, 0, A(ActionType.Bet, 150, ActionType.Call, 150), -7.0f, -10.0f, ExpectedDecisionKind.Fold, false, 3));
            s.Add(Post("3way river thin value", "AsQd", "AhQc7s3d2c", 0, 0, A(ActionType.Check, 0, ActionType.Check, 0), 3.0f, 9.0f, ExpectedDecisionKind.Aggressive, true, 3));
            s.Add(Post("4way flop pot control", "AdJd", "Ah9h8s", 0, 0, A(ActionType.Check, 0, ActionType.Check, 0, ActionType.Check, 0), 5.0f, 2.0f, ExpectedDecisionKind.CheckOrCall, false, 4));
            s.Add(Post("4way flop value pesado", "AsAc", "Ah7d2s", 0, 0, A(ActionType.Check, 0, ActionType.Check, 0, ActionType.Check, 0), 2.0f, 12.0f, ExpectedDecisionKind.Aggressive, true, 4));

            // Situações extremas.
            Scenario noAction = Post("No action fora da vez do heroi", "AsKd", "Ah7d2c", 0, 0, A(), 1.0f, 5.0f, ExpectedDecisionKind.NoAction, false);
            noAction.DoNotAdvanceToHero = true;
            noAction.ExpectDiagnostic = false;
            s.Add(noAction);
            s.Add(Post("All-in por commitment minimo 80", "AsAh", "Kd8c2s", 0, 0, A(ActionType.Check, 0), 1.0f, 30.0f, ExpectedDecisionKind.Aggressive, true));
            s.Add(Post("Check gratis quando ambos EV negativos sem bet", "7s2d", "AhKdQc", 0, 0, A(ActionType.Check, 0), -5.0f, -8.0f, ExpectedDecisionKind.CheckOrCall, false));

            return s;
        }

        private static Scenario Post(string name, string heroCards, string board, int heroIndex, int buttonIndex, List<ScriptedAction> beforeHero, float cc, float br, ExpectedDecisionKind expected, bool expectMultiSize, int players = 2)
        {
            return new Scenario
            {
                Name = name,
                NumPlayers = players,
                HeroIndex = heroIndex,
                ButtonIndex = buttonIndex,
                HeroCards = heroCards,
                Board = board,
                ActionsBeforeHero = beforeHero,
                CheckCallEV = cc,
                BetRaiseEV = br,
                Expected = expected,
                ExpectMultiSize = expectMultiSize
            };
        }

        private static List<ScriptedAction> A(params object[] items)
        {
            List<ScriptedAction> actions = new List<ScriptedAction>();

            for (int i = 0; i + 1 < items.Length; i += 2)
            {
                actions.Add(new ScriptedAction((ActionType)items[i], Convert.ToInt32(items[i + 1])));
            }

            return actions;
        }

        private static void RunDecisionScenarios(AcademicRegressionReport report, List<Scenario> scenarios)
        {
            foreach (Scenario scenario in scenarios)
            {
                try
                {
                    var estimator = new ScriptedBetRaiseAmountEstimator(scenario.CheckCallEV, scenario.BetRaiseEV);
                    using (BotGameState game = CreateGame(scenario.NumPlayers, scenario.HeroIndex, scenario.ButtonIndex, scenario.BigBlind, scenario.Stack, estimator, scenario.HeroCards))
                    {
                        if (!string.IsNullOrWhiteSpace(scenario.Board))
                        {
                            ResolvePreFlopBeforeBoard(game);
                            AdvanceBoard(game, scenario.Board);
                        }

                        if (!scenario.DoNotAdvanceToHero)
                            ApplyActionsBeforeHero(game, scenario.HeroIndex, scenario.ActionsBeforeHero);

                        SeedMultiSizeEvs(estimator, game, scenario);

                        BotGameState.BotDecision decision = game.calculateHeroAction();
                        bool ok = ValidateDecisionKind(decision, scenario.Expected);

                        if (scenario.ExpectDiagnostic)
                            ok = ok && decision.message.Contains("Diagnostico academico da decisao");

                        if (scenario.ExpectMultiSize)
                            ok = ok && decision.message.Contains("Multi-size EV tree ativada");

                        string details = $"acao={decision.actionType}, by={decision.byAmount}, ccEV={decision.checkCallEV:F2}, brEV={decision.betRaiseEV:F2}, amountToCall={game.getAmountToCall()}, street={game.getStreet()}, board={game.getBoard()}";
                        report.Add(ok, scenario.Name, details);
                    }
                }
                catch (Exception ex)
                {
                    report.Add(false, scenario.Name, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static void SeedMultiSizeEvs(ScriptedBetRaiseAmountEstimator estimator, BotGameState game, Scenario scenario)
        {
            if (game.getStreet() == Street.PreFlop)
                return;

            int pot = Math.Max(1, game.potSize() + game.getAmountToCall());
            int stack = game.getHero().Stack;
            int[] candidates = new int[]
            {
                Math.Max(game.getBigBlingSize(), (int)Math.Round(pot * 0.33f)),
                Math.Max(game.getBigBlingSize(), (int)Math.Round(pot * 0.50f)),
                Math.Max(game.getBigBlingSize(), (int)Math.Round(pot * 0.66f)),
                Math.Max(game.getBigBlingSize(), (int)Math.Round(pot * 0.75f)),
                Math.Max(game.getBigBlingSize(), (int)Math.Round(pot * 1.00f)),
                stack
            };

            foreach (int raw in candidates)
            {
                int amount = Math.Max(1, Math.Min(raw, stack));
                float shapeBonus = -Math.Abs((amount / (float)Math.Max(1, pot)) - 0.66f);
                float br = scenario.BetRaiseEV + shapeBonus;

                if (scenario.Expected == ExpectedDecisionKind.Aggressive || scenario.Expected == ExpectedDecisionKind.AllIn)
                    br += amount == candidates[2] ? 2.0f : 0.0f;

                estimator.MultiSizeEV[amount] = new EvPair(scenario.CheckCallEV, br);
            }
        }

        private static bool ValidateDecisionKind(BotGameState.BotDecision decision, ExpectedDecisionKind expected)
        {
            if (expected == ExpectedDecisionKind.NoAction)
                return decision.actionType == ActionType.NoAction;

            if (expected == ExpectedDecisionKind.CheckOrCall)
                return decision.actionType == ActionType.Check || decision.actionType == ActionType.Call;

            if (expected == ExpectedDecisionKind.Aggressive)
                return decision.actionType == ActionType.Bet || decision.actionType == ActionType.Raise || decision.actionType == ActionType.AllIn;

            if (expected == ExpectedDecisionKind.Fold)
                return decision.actionType == ActionType.Fold;

            if (expected == ExpectedDecisionKind.AllIn)
                return decision.actionType == ActionType.AllIn;

            return false;
        }

        private static BotGameState CreateGame(int numPlayers, int heroIndex, int buttonIndex, int bb, int stack, ScriptedBetRaiseAmountEstimator estimator, string heroCards)
        {
            string[] names = new string[numPlayers];
            int[] stacks = new int[numPlayers];

            for (int i = 0; i < numPlayers; i++)
            {
                names[i] = i == heroIndex ? "Hero" : "Villain_" + i;
                stacks[i] = stack;
            }

            TableType tableType = numPlayers == 2 ? TableType.HeadsUp : TableType.SixMax;
            BotGameState game = new BotGameState(names, stacks, heroIndex, buttonIndex, bb, PokerClient.G5, tableType, estimator, false, -1);
            game.startNewHand();
            game.dealHoleCards(new HoleCards(heroCards));
            return game;
        }

        private static void ResolvePreFlopBeforeBoard(BotGameState game)
        {
            if (game.getStreet() != Street.PreFlop)
                return;

            int guard = 0;

            while (game.getPlayerToActInd() >= 0 && guard < 40)
            {
                guard++;

                int amountToCall = game.getAmountToCall();

                if (amountToCall > 0)
                    game.playerActs(ActionType.Call, amountToCall);
                else
                    game.playerActs(ActionType.Check, 0);
            }

            if (guard >= 40)
                throw new InvalidOperationException("ResolvePreFlopBeforeBoard: preflop sintetico nao estabilizou antes de abrir o board.");
        }

        private static void AdvanceBoard(BotGameState game, string boardText)
        {
            List<Card> cards = ParseCards(boardText);

            if (cards.Count >= 3)
                game.goToNextStreet(new List<Card> { cards[0], cards[1], cards[2] });

            if (cards.Count >= 4)
                game.goToNextStreet(cards[3]);

            if (cards.Count >= 5)
                game.goToNextStreet(cards[4]);
        }

        private static void ApplyActionsBeforeHero(BotGameState game, int heroIndex, List<ScriptedAction> scripted)
        {
            int scriptIndex = 0;
            int guard = 0;

            while (game.getPlayerToActInd() != heroIndex && game.getPlayerToActInd() >= 0 && guard < 40)
            {
                guard++;
                ScriptedAction action;

                if (scriptIndex < scripted.Count)
                {
                    action = scripted[scriptIndex++];
                }
                else
                {
                    int amountToCall = game.getAmountToCall();
                    action = amountToCall > 0
                        ? new ScriptedAction(ActionType.Call, amountToCall)
                        : new ScriptedAction(ActionType.Check, 0);
                }

                game.playerActs(action.Type, action.Amount);
            }
        }

        private static List<Card> ParseCards(string text)
        {
            string cleaned = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            List<Card> cards = new List<Card>();

            for (int i = 0; i + 1 < cleaned.Length; i += 2)
                cards.Add(new Card(cleaned.Substring(i, 2)));

            return cards;
        }

        private static void ExpectTrue(AcademicRegressionReport report, string name, bool condition, string details)
        {
            report.Add(condition, name, details);
        }

        private static void ExpectThrows(AcademicRegressionReport report, string name, System.Action action)
        {
            try
            {
                action();
                report.Add(false, name, "nao lancou excecao");
            }
            catch (Exception ex)
            {
                report.Add(true, name, "excecao esperada: " + ex.GetType().Name);
            }
        }

        private static void EnsureMinimalPreFlopCharts()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            EnsureMinimalPreFlopChartsBucket(Path.Combine(baseDir, "PreFlopCharts", "100bb"));
            EnsureMinimalPreFlopChartsBucket(Path.Combine(baseDir, "PreFlopCharts", "200bb"));
        }

        private static void EnsureMinimalPreFlopChartsBucket(string folder)
        {
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, "VS_0_Bets_Hero_BTN.txt");

            if (File.Exists(path))
                return;

            string[] ranks = new string[] { "A", "K", "Q", "J", "T", "9", "8", "7", "6", "5", "4", "3", "2" };
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Hand AllIn Raise Call");

            for (int row = 0; row < 13; row++)
            {
                sb.Append(ranks[row]);

                for (int col = 0; col < 13; col++)
                    sb.Append(" 0 0 100");

                sb.AppendLine();
            }

            File.WriteAllText(path, sb.ToString());
        }
    }
}
