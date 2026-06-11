using G5.Logic.Tests;
using System;
using System.IO;

namespace G5.AcademicRegressionRunner
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string outputPath = null;

            if (args != null && args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
                outputPath = args[0];
            else
                outputPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs", "G5AcademicRegression_phase8.log");

            AcademicRegressionReport report = AcademicRegressionTests.RunAll(outputPath);
            Console.WriteLine(report.ToText());
            Console.WriteLine("Log salvo em: " + outputPath);

            return report.Failed == 0 ? 0 : 1;
        }
    }
}
