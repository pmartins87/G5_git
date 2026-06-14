using System;
using System.Collections.Generic;

namespace G5.Logic
{
    public sealed class ComboFeatureVector
    {
        public int ComboIndex { get; set; }
        public float MadeScore { get; set; }
        public float ShowdownScore { get; set; }
        public float DrawScore { get; set; }
        public float NutPotential { get; set; }
        public float BlockerScore { get; set; }
        public float Vulnerability { get; set; }
        public float BoardWetness { get; set; }
        public float BoardPairing { get; set; }
        public float Overcards { get; set; }
        public float AirScore { get; set; }
        public float Position { get; set; }
        public float Multiway { get; set; }
        public float SizePressure { get; set; }
        public float SprPressure { get; set; }
    }

    public sealed class LikelihoodProfile
    {
        public PostFlopLineClass LineClass { get; private set; }
        public string Name { get; private set; }

        // Shape coefficients for aggressive likelihood.
        public float AggMade { get; set; }
        public float AggShowdown { get; set; }
        public float AggDraw { get; set; }
        public float AggNut { get; set; }
        public float AggBlocker { get; set; }
        public float AggVulnerability { get; set; }
        public float AggAir { get; set; }
        public float AggWetness { get; set; }
        public float AggPosition { get; set; }
        public float AggMultiway { get; set; }
        public float AggSize { get; set; }
        public float AggSprPressure { get; set; }

        // Shape coefficients for passive continuing likelihood.
        public float ContMade { get; set; }
        public float ContShowdown { get; set; }
        public float ContDraw { get; set; }
        public float ContNut { get; set; }
        public float ContBlocker { get; set; }
        public float ContVulnerability { get; set; }
        public float ContAir { get; set; }
        public float ContWetness { get; set; }
        public float ContPosition { get; set; }
        public float ContMultiway { get; set; }
        public float ContSize { get; set; }
        public float ContSprPressure { get; set; }

        // Shape coefficients for folding likelihood.
        public float FoldMade { get; set; }
        public float FoldShowdown { get; set; }
        public float FoldDraw { get; set; }
        public float FoldNut { get; set; }
        public float FoldBlocker { get; set; }
        public float FoldVulnerability { get; set; }
        public float FoldAir { get; set; }
        public float FoldWetness { get; set; }
        public float FoldPosition { get; set; }
        public float FoldMultiway { get; set; }
        public float FoldSize { get; set; }
        public float FoldSprPressure { get; set; }

        // Target conditioning. These parameters do not replace the .bin model;
        // they transform the legacy EstimatedAD into a topological target.
        public float TargetAggressionScale { get; set; }
        public float TargetContinueScale { get; set; }
        public float TargetFoldScale { get; set; }
        public float TargetAggressionBias { get; set; }
        public float TargetContinueBias { get; set; }
        public float TargetFoldBias { get; set; }

        public LikelihoodProfile(PostFlopLineClass lineClass, string name)
        {
            LineClass = lineClass;
            Name = name;
            TargetAggressionScale = 1.0f;
            TargetContinueScale = 1.0f;
            TargetFoldScale = 1.0f;
        }

        public float ScoreAggressive(ComboFeatureVector f, PostFlopLineContext ctx)
        {
            return Score(f,
                AggMade, AggShowdown, AggDraw, AggNut, AggBlocker, AggVulnerability, AggAir,
                AggWetness, AggPosition, AggMultiway, AggSize, AggSprPressure);
        }

        public float ScoreContinue(ComboFeatureVector f, PostFlopLineContext ctx)
        {
            return Score(f,
                ContMade, ContShowdown, ContDraw, ContNut, ContBlocker, ContVulnerability, ContAir,
                ContWetness, ContPosition, ContMultiway, ContSize, ContSprPressure);
        }

        public float ScoreFold(ComboFeatureVector f, PostFlopLineContext ctx)
        {
            return Score(f,
                FoldMade, FoldShowdown, FoldDraw, FoldNut, FoldBlocker, FoldVulnerability, FoldAir,
                FoldWetness, FoldPosition, FoldMultiway, FoldSize, FoldSprPressure);
        }

        private static float Score(
            ComboFeatureVector f,
            float made, float showdown, float draw, float nut, float blocker, float vulnerability, float air,
            float wetness, float position, float multiway, float size, float sprPressure)
        {
            if (f == null)
                return 0.0f;

            float result = 0.0f;
            result += made * f.MadeScore;
            result += showdown * f.ShowdownScore;
            result += draw * f.DrawScore;
            result += nut * f.NutPotential;
            result += blocker * f.BlockerScore;
            result += vulnerability * f.Vulnerability;
            result += air * f.AirScore;
            result += wetness * f.BoardWetness;
            result += position * f.Position;
            result += multiway * f.Multiway;
            result += size * f.SizePressure;
            result += sprPressure * f.SprPressure;

            if (float.IsNaN(result) || float.IsInfinity(result))
                return 0.0f;

            if (result < -12.0f)
                return -12.0f;

            if (result > 12.0f)
                return 12.0f;

            return result;
        }
    }

    public static class LikelihoodProfileLibrary
    {
        private static readonly Dictionary<PostFlopLineClass, LikelihoodProfile> _profiles = BuildProfiles();

        public static LikelihoodProfile Get(PostFlopLineClass lineClass)
        {
            LikelihoodProfile profile;

            if (_profiles.TryGetValue(lineClass, out profile))
                return profile;

            return _profiles[PostFlopLineClass.Unknown];
        }

        public static IEnumerable<LikelihoodProfile> AllProfiles()
        {
            return _profiles.Values;
        }

        private static Dictionary<PostFlopLineClass, LikelihoodProfile> BuildProfiles()
        {
            Dictionary<PostFlopLineClass, LikelihoodProfile> p = new Dictionary<PostFlopLineClass, LikelihoodProfile>();

            Add(p, Unknown());
            Add(p, CBet(PostFlopLineClass.CBet, "CBet"));
            Add(p, CBet(PostFlopLineClass.DoubleBarrel, "DoubleBarrel", 1.12f, 1.08f));
            Add(p, CBet(PostFlopLineClass.TripleBarrel, "TripleBarrel", 1.25f, 1.18f));
            Add(p, Donk(PostFlopLineClass.DonkBet, "DonkBet"));
            Add(p, Donk(PostFlopLineClass.MultiwayLead, "MultiwayLead", 0.82f, 1.18f));
            Add(p, Donk(PostFlopLineClass.LimpedPotLead, "LimpedPotLead", 0.90f, 1.05f));
            Add(p, Probe(PostFlopLineClass.ProbeBet, "ProbeBet"));
            Add(p, Probe(PostFlopLineClass.FloatBet, "FloatBet", 1.10f));
            Add(p, Probe(PostFlopLineClass.StabAfterChecks, "StabAfterChecks", 1.05f));
            Add(p, Delayed(PostFlopLineClass.DelayedCBet, "DelayedCBet"));
            Add(p, Delayed(PostFlopLineClass.DelayedFloatBet, "DelayedFloatBet", 0.95f));
            Add(p, GenericBet());

            Add(p, CheckRaise(PostFlopLineClass.CheckRaise, "CheckRaise"));
            Add(p, RaiseVsCBet());
            Add(p, RaiseVsDonk());
            Add(p, RaiseVsProbe());
            Add(p, RaiseVsFloat());
            Add(p, RaiseVsDelayedCBet());
            Add(p, GenericRaise(PostFlopLineClass.RaiseVsGenericBet, "RaiseVsGenericBet"));
            Add(p, GenericRaise(PostFlopLineClass.ReRaise, "ReRaise", 0.78f, 1.28f));
            Add(p, GenericRaise(PostFlopLineClass.AllInPolarized, "AllInPolarized", 0.55f, 1.70f));
            Add(p, GenericRaise(PostFlopLineClass.GenericRaise, "GenericRaise"));

            Add(p, Passive(PostFlopLineClass.CallVsCBet, "CallVsCBet"));
            Add(p, Passive(PostFlopLineClass.CallVsDonk, "CallVsDonk", 1.05f));
            Add(p, Passive(PostFlopLineClass.CallVsProbe, "CallVsProbe", 1.00f));
            Add(p, Passive(PostFlopLineClass.CallVsFloat, "CallVsFloat", 0.96f));
            Add(p, Passive(PostFlopLineClass.CallVsDelayedCBet, "CallVsDelayedCBet", 1.00f));
            Add(p, Passive(PostFlopLineClass.CallVsGenericBet, "CallVsGenericBet"));
            Add(p, Passive(PostFlopLineClass.GenericCall, "GenericCall"));

            Add(p, Fold(PostFlopLineClass.FoldVsCBet, "FoldVsCBet"));
            Add(p, Fold(PostFlopLineClass.FoldVsDonk, "FoldVsDonk", 1.05f));
            Add(p, Fold(PostFlopLineClass.FoldVsProbe, "FoldVsProbe", 0.95f));
            Add(p, Fold(PostFlopLineClass.FoldVsFloat, "FoldVsFloat", 0.92f));
            Add(p, Fold(PostFlopLineClass.FoldVsDelayedCBet, "FoldVsDelayedCBet"));
            Add(p, Fold(PostFlopLineClass.FoldVsGenericBet, "FoldVsGenericBet"));
            Add(p, Fold(PostFlopLineClass.GenericFold, "GenericFold"));

            Add(p, Check(PostFlopLineClass.CheckToAggressor, "CheckToAggressor"));
            Add(p, Check(PostFlopLineClass.CheckBackWithInitiative, "CheckBackWithInitiative", 0.88f));
            Add(p, Check(PostFlopLineClass.CheckToNoInitiative, "CheckToNoInitiative"));
            Add(p, Check(PostFlopLineClass.GenericCheck, "GenericCheck"));

            return p;
        }

        private static void Add(Dictionary<PostFlopLineClass, LikelihoodProfile> map, LikelihoodProfile profile)
        {
            map[profile.LineClass] = profile;
        }

        private static LikelihoodProfile Unknown()
        {
            return new LikelihoodProfile(PostFlopLineClass.Unknown, "Unknown")
            {
                AggMade = 0.80f,
                AggShowdown = 0.35f,
                AggDraw = 0.55f,
                AggNut = 0.65f,
                AggBlocker = 0.25f,
                AggVulnerability = 0.05f,
                AggAir = -0.10f,
                AggSize = 0.10f,

                ContMade = 0.35f,
                ContShowdown = 0.70f,
                ContDraw = 0.45f,
                ContNut = 0.10f,
                ContAir = -0.45f,
                ContSize = -0.25f,

                FoldMade = -0.90f,
                FoldShowdown = -0.75f,
                FoldDraw = -0.45f,
                FoldNut = -1.10f,
                FoldAir = 1.10f,
                FoldSize = 0.60f
            };
        }

        private static LikelihoodProfile CBet(PostFlopLineClass cls, string name, float targetScale = 1.0f, float shapeScale = 1.0f)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 0.45f * shapeScale,
                AggShowdown = 0.18f * shapeScale,
                AggDraw = 0.36f * shapeScale,
                AggNut = 0.22f * shapeScale,
                AggBlocker = 0.32f * shapeScale,
                AggVulnerability = 0.05f,
                AggAir = 0.05f,
                AggWetness = 0.08f,
                AggPosition = 0.10f,
                AggMultiway = -0.18f,
                AggSize = 0.24f,

                ContMade = 0.30f,
                ContShowdown = 0.62f,
                ContDraw = 0.30f,
                ContAir = -0.30f,
                ContSize = -0.20f,

                FoldMade = -0.70f,
                FoldShowdown = -0.60f,
                FoldDraw = -0.42f,
                FoldNut = -0.80f,
                FoldAir = 0.82f,
                FoldSize = 0.42f,
                FoldMultiway = 0.15f,

                TargetAggressionScale = 1.00f * targetScale,
                TargetContinueScale = 1.00f,
                TargetFoldScale = 1.00f
            };
        }

        private static LikelihoodProfile Donk(PostFlopLineClass cls, string name, float targetScale = 1.0f, float shapeScale = 1.0f)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 0.75f * shapeScale,
                AggShowdown = 0.18f,
                AggDraw = 0.92f * shapeScale,
                AggNut = 0.62f,
                AggBlocker = 0.15f,
                AggVulnerability = 0.42f,
                AggAir = -0.42f,
                AggWetness = 0.45f,
                AggPosition = -0.12f,
                AggMultiway = -0.25f,
                AggSize = 0.58f,

                ContMade = 0.44f,
                ContShowdown = 0.58f,
                ContDraw = 0.40f,
                ContVulnerability = 0.16f,
                ContAir = -0.55f,
                ContSize = -0.30f,

                FoldMade = -0.95f,
                FoldShowdown = -0.70f,
                FoldDraw = -0.62f,
                FoldNut = -1.10f,
                FoldAir = 1.12f,
                FoldSize = 0.55f,

                TargetAggressionScale = 0.72f * targetScale,
                TargetContinueScale = 1.05f,
                TargetFoldScale = 1.08f
            };
        }

        private static LikelihoodProfile Probe(PostFlopLineClass cls, string name, float targetScale = 1.0f)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 0.48f,
                AggShowdown = 0.10f,
                AggDraw = 0.58f,
                AggNut = 0.28f,
                AggBlocker = 0.38f,
                AggVulnerability = 0.22f,
                AggAir = 0.08f,
                AggWetness = 0.22f,
                AggPosition = 0.20f,
                AggSize = 0.35f,

                ContMade = 0.34f,
                ContShowdown = 0.62f,
                ContDraw = 0.42f,
                ContAir = -0.38f,
                ContSize = -0.20f,

                FoldMade = -0.82f,
                FoldShowdown = -0.62f,
                FoldDraw = -0.46f,
                FoldNut = -0.90f,
                FoldAir = 0.94f,
                FoldSize = 0.40f,

                TargetAggressionScale = 0.88f * targetScale,
                TargetContinueScale = 1.03f,
                TargetFoldScale = 1.02f
            };
        }

        private static LikelihoodProfile Delayed(PostFlopLineClass cls, string name, float targetScale = 1.0f)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 0.52f,
                AggShowdown = 0.18f,
                AggDraw = 0.45f,
                AggNut = 0.35f,
                AggBlocker = 0.32f,
                AggVulnerability = 0.12f,
                AggAir = 0.00f,
                AggWetness = 0.18f,
                AggPosition = 0.22f,
                AggSize = 0.32f,

                ContMade = 0.38f,
                ContShowdown = 0.64f,
                ContDraw = 0.35f,
                ContAir = -0.35f,
                ContSize = -0.18f,

                FoldMade = -0.82f,
                FoldShowdown = -0.64f,
                FoldDraw = -0.42f,
                FoldNut = -0.95f,
                FoldAir = 0.92f,
                FoldSize = 0.36f,

                TargetAggressionScale = 0.92f * targetScale,
                TargetContinueScale = 1.02f,
                TargetFoldScale = 1.00f
            };
        }

        private static LikelihoodProfile CheckRaise(PostFlopLineClass cls, string name)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 1.35f,
                AggShowdown = -0.10f,
                AggDraw = 1.25f,
                AggNut = 1.55f,
                AggBlocker = 0.52f,
                AggVulnerability = -0.22f,
                AggAir = -0.80f,
                AggWetness = 0.45f,
                AggSize = 0.85f,
                AggSprPressure = 0.22f,

                ContMade = 0.50f,
                ContShowdown = 0.78f,
                ContDraw = 0.45f,
                ContNut = 0.28f,
                ContAir = -0.70f,
                ContSize = -0.30f,

                FoldMade = -1.25f,
                FoldShowdown = -0.92f,
                FoldDraw = -0.62f,
                FoldNut = -1.45f,
                FoldAir = 1.42f,
                FoldSize = 0.70f,

                TargetAggressionScale = 0.48f,
                TargetContinueScale = 1.05f,
                TargetFoldScale = 1.18f
            };
        }

        private static LikelihoodProfile RaiseVsCBet()
        {
            LikelihoodProfile p = CheckRaise(PostFlopLineClass.RaiseVsCBet, "RaiseVsCBet");
            p.TargetAggressionScale = 0.62f;
            p.AggBlocker += 0.12f;
            p.AggDraw += 0.12f;
            return p;
        }

        private static LikelihoodProfile RaiseVsDonk()
        {
            LikelihoodProfile p = CheckRaise(PostFlopLineClass.RaiseVsDonk, "RaiseVsDonk");
            p.TargetAggressionScale = 0.68f;
            p.AggMade += 0.10f;
            p.AggDraw -= 0.05f;
            return p;
        }

        private static LikelihoodProfile RaiseVsProbe()
        {
            LikelihoodProfile p = CheckRaise(PostFlopLineClass.RaiseVsProbe, "RaiseVsProbe");
            p.TargetAggressionScale = 0.72f;
            p.AggBlocker += 0.15f;
            return p;
        }

        private static LikelihoodProfile RaiseVsFloat()
        {
            LikelihoodProfile p = CheckRaise(PostFlopLineClass.RaiseVsFloat, "RaiseVsFloat");
            p.TargetAggressionScale = 0.70f;
            p.AggBlocker += 0.08f;
            return p;
        }

        private static LikelihoodProfile RaiseVsDelayedCBet()
        {
            LikelihoodProfile p = CheckRaise(PostFlopLineClass.RaiseVsDelayedCBet, "RaiseVsDelayedCBet");
            p.TargetAggressionScale = 0.66f;
            return p;
        }

        private static LikelihoodProfile GenericRaise(PostFlopLineClass cls, string name, float targetScale = 0.70f, float shapeScale = 1.0f)
        {
            LikelihoodProfile p = CheckRaise(cls, name);
            p.TargetAggressionScale = targetScale;
            p.AggMade *= shapeScale;
            p.AggDraw *= shapeScale;
            p.AggNut *= shapeScale;
            p.AggSize *= shapeScale;
            return p;
        }

        private static LikelihoodProfile Passive(PostFlopLineClass cls, string name, float continueScale = 1.0f)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 0.25f,
                AggDraw = 0.35f,
                AggNut = 0.28f,
                AggBlocker = 0.18f,
                AggAir = -0.35f,
                AggSize = 0.20f,

                ContMade = 0.70f,
                ContShowdown = 1.05f,
                ContDraw = 0.80f,
                ContNut = 0.42f,
                ContBlocker = 0.22f,
                ContVulnerability = 0.05f,
                ContAir = -1.00f,
                ContSize = -0.55f,
                ContPosition = 0.14f,

                FoldMade = -1.20f,
                FoldShowdown = -1.05f,
                FoldDraw = -0.85f,
                FoldNut = -1.45f,
                FoldAir = 1.55f,
                FoldSize = 0.90f,
                FoldMultiway = 0.18f,

                TargetAggressionScale = 0.85f,
                TargetContinueScale = continueScale,
                TargetFoldScale = 1.02f
            };
        }

        private static LikelihoodProfile Fold(PostFlopLineClass cls, string name, float foldScale = 1.0f)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 0.20f,
                AggDraw = 0.20f,
                AggNut = 0.20f,
                AggAir = -0.20f,

                ContMade = 0.55f,
                ContShowdown = 0.80f,
                ContDraw = 0.58f,
                ContAir = -0.75f,
                ContSize = -0.42f,

                FoldMade = -1.35f,
                FoldShowdown = -1.12f,
                FoldDraw = -0.90f,
                FoldNut = -1.60f,
                FoldBlocker = -0.35f,
                FoldAir = 1.70f,
                FoldSize = 0.95f,
                FoldMultiway = 0.18f,

                TargetAggressionScale = 0.90f,
                TargetContinueScale = 0.98f,
                TargetFoldScale = foldScale
            };
        }

        private static LikelihoodProfile Check(PostFlopLineClass cls, string name, float continueScale = 1.0f)
        {
            return new LikelihoodProfile(cls, name)
            {
                AggMade = 0.35f,
                AggDraw = 0.28f,
                AggNut = 0.35f,
                AggAir = -0.20f,

                ContMade = 0.58f,
                ContShowdown = 0.95f,
                ContDraw = 0.30f,
                ContNut = 0.16f,
                ContAir = 0.20f,
                ContSize = 0.00f,

                FoldMade = -0.40f,
                FoldShowdown = -0.40f,
                FoldDraw = -0.30f,
                FoldAir = 0.30f,

                TargetAggressionScale = 0.82f,
                TargetContinueScale = continueScale,
                TargetFoldScale = 0.20f
            };
        }

        private static LikelihoodProfile GenericBet()
        {
            return new LikelihoodProfile(PostFlopLineClass.GenericBet, "GenericBet")
            {
                AggMade = 0.70f,
                AggShowdown = 0.20f,
                AggDraw = 0.62f,
                AggNut = 0.55f,
                AggBlocker = 0.28f,
                AggVulnerability = 0.18f,
                AggAir = -0.20f,
                AggWetness = 0.22f,
                AggSize = 0.42f,

                ContMade = 0.42f,
                ContShowdown = 0.72f,
                ContDraw = 0.42f,
                ContAir = -0.46f,
                ContSize = -0.22f,

                FoldMade = -0.95f,
                FoldShowdown = -0.75f,
                FoldDraw = -0.52f,
                FoldNut = -1.10f,
                FoldAir = 1.08f,
                FoldSize = 0.55f
            };
        }
    }
}
