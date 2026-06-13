using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;


namespace G5.Logic
{
	internal class PreFlopCharts
	{
		public string SourcePath { get; private set; }
		public int LoadedCount { get; private set; }
		public List<string> IgnoredFiles { get; private set; }
		private Dictionary<Position, PreFlopChart> vs_0_bets_charts = new Dictionary<Position, PreFlopChart>();

		// Charts próprias contra open limp.
		// Chave externa:
		//   0 = chart genérica contra limp, independente do número de limpers.
		//   1 = contra 1 limper.
		//   2 = contra 2 limpers.
		//   etc.
		// Chave interna:
		//   posição do herói.
		private Dictionary<int, Dictionary<Position, PreFlopChart>> vs_limp_charts =
		    new Dictionary<int, Dictionary<Position, PreFlopChart>>();

		public string LastLookupInfo { get; private set; }

		public int IgnoredCount
		{
		    get { return IgnoredFiles == null ? 0 : IgnoredFiles.Count; }
		}

        private Dictionary<Position, Dictionary<Position, PreFlopChart>> vs_1_bet_charts = 
            new Dictionary<Position, Dictionary<Position, PreFlopChart>>();

        private Dictionary<Position, Dictionary<Position, PreFlopChart>> vs_2_bets_reraise_charts =
            new Dictionary<Position, Dictionary<Position, PreFlopChart>>();

        private Dictionary<Position, Dictionary<Position, Dictionary<Position, PreFlopChart>>> vs_2_bets_charts =
            new Dictionary<Position, Dictionary<Position, Dictionary<Position, PreFlopChart>>>();

        private Dictionary<Position, Dictionary<Position, PreFlopChart>> vs_3_bets_charts =
            new Dictionary<Position, Dictionary<Position, PreFlopChart>>();

        private Dictionary<Position, Dictionary<Position, PreFlopChart>> vs_4_bets_charts =
            new Dictionary<Position, Dictionary<Position, PreFlopChart>>();

private bool tryParseLimpChartFile(string fileName, string[] parts, out int numLimpers, out Position heroPosition)
{
    numLimpers = 0; // 0 = genérico
    heroPosition = Position.Empty;

    if (!(fileName.StartsWith("VS_Limp_", StringComparison.OrdinalIgnoreCase) ||
          fileName.StartsWith("VS_Limps_", StringComparison.OrdinalIgnoreCase) ||
          fileName.StartsWith("VS_Limper_", StringComparison.OrdinalIgnoreCase) ||
          fileName.StartsWith("VS_Limpers_", StringComparison.OrdinalIgnoreCase)))
    {
        return false;
    }

    for (int i = 0; i < parts.Length; i++)
    {
        int n;
        if (int.TryParse(parts[i], out n))
            numLimpers = n;

        if (parts[i].Equals("Hero", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
            heroPosition = shorthandToPosition(parts[i + 1]);
    }

    // Fallback: se não houver "Hero_CO", tenta usar a última parte como posição.
    if (heroPosition == Position.Empty && parts.Length > 0)
        heroPosition = shorthandToPosition(parts[parts.Length - 1]);

    return heroPosition != Position.Empty;
}

        private Position shorthandToPosition(string shorthand)
        {
            if (shorthand == "UTG")
                return Position.UTG;
            else if (shorthand == "HJ")
                return Position.HJ;
            else if (shorthand == "CO")
                return Position.CO;
            else if (shorthand == "BTN")
                return Position.BU;
            else if (shorthand == "SB")
                return Position.SB;
            else if (shorthand == "BB")
                return Position.BB;

            Console.WriteLine($"Warning shorthandToPosition is Empty");

            return Position.Empty;
        }

        public PreFlopCharts(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("PreFlopCharts: path vazio.");

            SourcePath = Path.GetFullPath(path);
            IgnoredFiles = new List<string>();

            if (!Directory.Exists(SourcePath))
                throw new DirectoryNotFoundException($"PreFlopCharts: pasta nao encontrada: {SourcePath}");

            Console.WriteLine($"Reading preflop charts from {SourcePath}.");
            int numLoaded = 0;

            foreach (Position heroPosition in Enum.GetValues(typeof(Position)))
            {
                if (heroPosition != Position.Empty)
                {
                    vs_1_bet_charts[heroPosition] = new Dictionary<Position, PreFlopChart>();
                    vs_2_bets_reraise_charts[heroPosition] = new Dictionary<Position, PreFlopChart>();
                    vs_2_bets_charts[heroPosition] = new Dictionary<Position, Dictionary<Position, PreFlopChart>>();
                    vs_3_bets_charts[heroPosition] = new Dictionary<Position, PreFlopChart>();
                    vs_4_bets_charts[heroPosition] = new Dictionary<Position, PreFlopChart>();

                    foreach (Position villian1 in Enum.GetValues(typeof(Position)))
                    {
                        if (villian1 != Position.Empty)
                            vs_2_bets_charts[heroPosition][villian1] = new Dictionary<Position, PreFlopChart>();
                    }
                }
            }

            foreach (var file in Directory.GetFiles(SourcePath, "*.txt", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                string[] parts = fileName.Split(new char[] { '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
                

                int limpCount;
                Position limpHeroPosition;

                if (tryParseLimpChartFile(fileName, parts, out limpCount, out limpHeroPosition))
                {
                    if (!vs_limp_charts.ContainsKey(limpCount))
                        vs_limp_charts[limpCount] = new Dictionary<Position, PreFlopChart>();

                    vs_limp_charts[limpCount][limpHeroPosition] = new PreFlopChart(file);
                    Console.WriteLine($"Loaded preflop chart for {limpHeroPosition} facing {limpCount} limper(s) from {fileName}.");
                    numLoaded++;
                }
                else if (fileName.StartsWith("VS_0_Bets_") && parts.Length > 4)
                {
                    var position = shorthandToPosition(parts[4]);

                    if (position != Position.Empty)
                    {
                        vs_0_bets_charts[position] = new PreFlopChart(file);
                        Console.WriteLine($"Loaded preflop chart for {position} RFI from {fileName}.");
                        numLoaded++;
                    }
                }
                else if (fileName.StartsWith("VS_1_Bet_") && parts.Length > 6)
                {
                    var position_hero = shorthandToPosition(parts[4]);
                    var position_villian = shorthandToPosition(parts[6]);

                    vs_1_bet_charts[position_hero][position_villian] = new PreFlopChart(file);
                    Console.WriteLine($"Loaded preflop chart for {position_hero} facing RFI of {position_villian} from {fileName}.");
                    numLoaded++;
                }
                else if (fileName.StartsWith("VS_2_Bets_RR_") && parts.Length > 7)
                {
                    var position_hero = shorthandToPosition(parts[5]);
                    var position_villian = shorthandToPosition(parts[7]);

                    vs_2_bets_reraise_charts[position_hero][position_villian] = new PreFlopChart(file);
                    Console.WriteLine($"Loaded preflop chart for {position_hero} facing reraise of {position_villian} from {fileName}.");
                    numLoaded++;
                }
                else if (fileName.StartsWith("VS_2_Bets_") && parts.Length > 7)
                {
                    var position_hero = shorthandToPosition(parts[4]);
                    var position_villian1 = shorthandToPosition(parts[6]);
                    var position_villian2 = shorthandToPosition(parts[7]);

                    vs_2_bets_charts[position_hero][position_villian1][position_villian2] = new PreFlopChart(file);
                    Console.WriteLine($"Loaded preflop chart for {position_hero} facing raises of {position_villian1} and {position_villian2} from {fileName}.");
                    numLoaded++;
                }
                else if (fileName.StartsWith("VS_3_Bets_") && parts.Length > 6)
                {
                    var position_hero = shorthandToPosition(parts[4]);
                    var position_villian = shorthandToPosition(parts[6]);

                    vs_3_bets_charts[position_hero][position_villian] = new PreFlopChart(file);
                    Console.WriteLine($"Loaded preflop chart for {position_hero} facing 3 bets of {position_villian} from {fileName}.");
                    numLoaded++;
                }
                else if (fileName.StartsWith("VS_4_Bets_") && parts.Length > 6)
                {
                    var position_hero = shorthandToPosition(parts[4]);
                    var position_villian = shorthandToPosition(parts[6]);

                    vs_4_bets_charts[position_hero][position_villian] = new PreFlopChart(file);
                    Console.WriteLine($"Loaded preflop chart for {position_hero} facing 4 bets of {position_villian} from {fileName}.");
                    numLoaded++;
                }
                else
                {
                    IgnoredFiles.Add(fileName);
                    Console.WriteLine($"Ignored unrecognized preflop chart file: {fileName}");
                }
            }

            LoadedCount = numLoaded;

            if (LoadedCount <= 0)
                throw new Exception($"PreFlopCharts: nenhuma chart valida carregada em {SourcePath}.");

            Console.WriteLine($"Loaded {LoadedCount} preflop charts from {SourcePath}");

            if (IgnoredFiles.Count > 0)
                Console.WriteLine($"Ignored {IgnoredFiles.Count} unrecognized preflop chart files in {SourcePath}");
        }

        public ActionDistribution GetActionDistribution(BotGameState gameState, int preFlopChartsLevel)
        {
            if (gameState.getStreet() != Street.PreFlop)
                return null;

var heroPos = gameState.getHero().PreFlopPosition;
var bettorPositions = gameState.getBettors();

LastLookupInfo = "";

// Open limp antes do herói:
// não há raise, mas já há caller(s).
if (gameState.getNumBets() == 0 && gameState.getNumCallers() > 0)
{
    int limpers = gameState.getNumCallers();

    if (preFlopChartsLevel >= 0)
    {
        // 1) Primeiro tenta chart específica para o número exato de limpers.
        if (vs_limp_charts.ContainsKey(limpers) &&
            vs_limp_charts[limpers].ContainsKey(heroPos))
        {
            LastLookupInfo = $"open limp: usando chart propria contra {limpers} limper(s), heroPos={heroPos}.";
            return vs_limp_charts[limpers][heroPos].GetActionDistribution(gameState.getHeroHoleCards());
        }

        // 2) Depois tenta chart genérica contra limp.
        if (vs_limp_charts.ContainsKey(0) &&
            vs_limp_charts[0].ContainsKey(heroPos))
        {
            LastLookupInfo = $"open limp: usando chart generica contra limp, heroPos={heroPos}.";
            return vs_limp_charts[0][heroPos].GetActionDistribution(gameState.getHeroHoleCards());
        }

        // 3) Fallback formal: usa RFI da posição como baseline de isolamento.
        // Isso evita que mãos premium, como AA, caiam no ModelingEstimator e deem limp behind.
        // Quando você criar charts VS_Limp_..., elas terão prioridade sobre este fallback.
        if (vs_0_bets_charts.ContainsKey(heroPos))
        {
            LastLookupInfo = $"open limp: sem chart propria contra limp; usando RFI de {heroPos} como baseline de isolamento.";
            return vs_0_bets_charts[heroPos].GetActionDistribution(gameState.getHeroHoleCards());
        }
    }

    LastLookupInfo = $"open limp: sem chart propria e sem RFI baseline para heroPos={heroPos}.";
    return null;
}

if (gameState.getNumBets() == 0 && gameState.getNumCallers() == 0) // RFI
{
                if (preFlopChartsLevel >= 0)
                {
                    if (vs_0_bets_charts.ContainsKey(heroPos))
                        return vs_0_bets_charts[heroPos].GetActionDistribution(gameState.getHeroHoleCards());
                }
            }
            else if (gameState.getNumBets() == 1 && gameState.getNumCallers() <= 1) // Facing RFI
            {
                if (preFlopChartsLevel >= 1)
                {
                    if (!vs_1_bet_charts.ContainsKey(heroPos))
                        return null;

                    if (bettorPositions.Count == 0)
                        return null;

                    if (vs_1_bet_charts[heroPos].ContainsKey(bettorPositions.Last()))
                        return vs_1_bet_charts[heroPos][bettorPositions.Last()].GetActionDistribution(gameState.getHeroHoleCards());
                }
            }
            else if (gameState.getNumBets() == 2 && gameState.getNumCallers() == 0)
            {
                if (preFlopChartsLevel >= 2)
                {
                    if (gameState.getHero().LastAction == ActionType.Bet || gameState.getHero().LastAction == ActionType.Raise) // Facing re-raise
                    {
                        if (!vs_2_bets_reraise_charts.ContainsKey(heroPos))
                            return null;

                        if (bettorPositions.Count == 0)
                            return null;

                        if (vs_2_bets_reraise_charts[heroPos].ContainsKey(bettorPositions.Last()))
                            return vs_2_bets_reraise_charts[heroPos][bettorPositions.Last()].GetActionDistribution(gameState.getHeroHoleCards());
                    }
                    else // 4bet (facing 2 bets first time)
                    {
                        if (!vs_2_bets_charts.ContainsKey(heroPos))
                            return null;

                        if (bettorPositions.Count != 2)
                            return null;

                        var villian0Pos = bettorPositions[0];
                        var villian1Pos = bettorPositions[1];

                        if (vs_2_bets_charts[heroPos].ContainsKey(villian0Pos))
                        {
                            if (vs_2_bets_charts[heroPos][villian0Pos].ContainsKey(villian1Pos))
                                return vs_2_bets_charts[heroPos][villian0Pos][villian1Pos].GetActionDistribution(gameState.getHeroHoleCards());
                        }
                    }
                }
            }
            else if (gameState.getNumBets() == 3 && gameState.getNumCallers() == 0)
            {
                if (preFlopChartsLevel >= 3)
                {
                    if (!vs_3_bets_charts.ContainsKey(heroPos))
                        return null;

                    if (bettorPositions.Count == 0)
                        return null;

                    if (vs_3_bets_charts[heroPos].ContainsKey(bettorPositions.Last()))
                        return vs_3_bets_charts[heroPos][bettorPositions.Last()].GetActionDistribution(gameState.getHeroHoleCards());
                }
            }
            else if (gameState.getNumBets() == 4 && gameState.getNumCallers() == 0)
            {
                if (preFlopChartsLevel >= 4)
                {
                    if (!vs_4_bets_charts.ContainsKey(heroPos))
                        return null;

                    if (bettorPositions.Count == 0)
                        return null;

                    if (vs_4_bets_charts[heroPos].ContainsKey(bettorPositions.Last()))
                        return vs_4_bets_charts[heroPos][bettorPositions.Last()].GetActionDistribution(gameState.getHeroHoleCards());
                }
            }

            return null;
        }
    }
}
