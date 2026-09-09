using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using RoadPathfinding;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RealisticNight
{
    // City window booster: one scan collects lit city-window materials, then emission = orig x intensity x haze each frame.
    internal static class LightBoost
    {
        struct Entry { public Material m; public Color orig; }
        static readonly int EmiID = Shader.PropertyToID("_EmissionColor");
        static List<Entry> entries;
        static bool searched;
        static float nextFind; // throttle: Find walks every material in the game
        static bool hasLastB; // identical-input skip (same inputs => same colors, no writes needed)
        static bool lastNightB, lastActiveB;
        static float lastIB, lastWB, lastHazeB, lastApplyT;
        static List<Entry> lastListB;
        static bool EntriesDead() // nothing live tracked (empty first scan, or scene reloaded under us)
        {
            if (entries == null) return true;
            foreach (Entry en in entries) if (en.m != null) return false;
            return true;
        }

        static readonly string[] CityP = { "highrise", "blocks_city", "commercial", "downtown", "office", "storefront", "apartment", "skyscraper", "window",
            // more building families the user found unlit (commercial_3a, residential_f1/f1a, midrise_1a, solarpanels1, ...)
            "residential", "midrise", "lowrise", "solarpanel", "rooftop", "block_", "building" };
        static readonly string[] ExcludeName = { "afterburner", "tracer", "muzzle", "explos", "fireball", "mushroom", "crater", "scorch", "smoke", "distort", "heatdist", "font", "glyph", "hud", "icon", "cursor", "reticle", "debug", "_ui", "runway", "taxi",
            // cockpit / instrument surfaces — boosting these makes them cross the bloom threshold and strobe
            "cockpit", "mfd", "screen", "display", "instrument", "gauge", "dial", "monitor", "canopy", "rwr", "hsi", "mfcd", "panel_light", "warning", "caution",
            // water FX — ship wakes / splashes / spray are emissive-ish; boosting them looks wrong
            "splash", "spray", "wake", "foam", "bubble", "ripple", "wetdeck" };
        static readonly string[] ExcludeShader = { "stars", "water", "cloud", "moon", "sky", "afterburner", "tracer", "smoke", "distort" };

        static void Find()
        {
            searched = true;
            entries = new List<Entry>();
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || !m.HasProperty(EmiID)) continue;
                string sn = m.shader.name.ToLowerInvariant();
                bool skip = false;
                foreach (string x in ExcludeShader) if (sn.Contains(x)) { skip = true; break; }
                if (skip) continue;
                bool isLight = m.IsKeywordEnabled("_EMISSION") || sn.Contains("meshlight");
                if (!isLight) continue;
                Color e = m.GetColor(EmiID);
                if (e.maxColorComponent <= 0.5f) continue; // only real light sources
                string n = m.name.ToLowerInvariant();
                foreach (string x in ExcludeName) if (n.Contains(x)) { skip = true; break; }
                if (skip) continue;
                bool city = false;
                foreach (string p in CityP) if (n.Contains(p)) { city = true; break; }
                if (!city) continue; // vehicle/other materials stay vanilla (knobs removed in 0.9.54)

                entries.Add(new Entry { m = m, orig = e });
            }
        }

        public static void Apply(bool modOn, bool night)
        {
            // Full material scan at most every 5 s, and only while nothing live is tracked (zero-match
            // maps rescanned unboundedly before; stale post-reload entries never refreshed).
            if (!searched || (Time.realtimeSinceStartup >= nextFind && EntriesDead()))
            {
                Find();
                nextFind = Time.realtimeSinceStartup + 5f;
            }
            if (entries == null || entries.Count == 0) return;

            // Boost relaxes toward vanilla with distance to the nearest lit city street (haze).
            float haze = StreetLights.HazeFade(StreetLights.NearestCityDist);
            bool active = modOn && RealisticNightPlugin.CityWindowsEnabled.Value && night;
            float inten = RealisticNightPlugin.CityWindowIntensity.Value;
            float w = RealisticNightPlugin.CityWindowWarmth.Value;
            // Bit-identical inputs already applied (plus a 2 s floor for external writers): skip the writes.
            if (hasLastB && lastListB == entries && lastNightB == night && lastActiveB == active
                && lastIB == inten && lastWB == w && lastHazeB == haze
                && Time.realtimeSinceStartup - lastApplyT < 2f) return;
            hasLastB = true;
            lastListB = entries; lastNightB = night; lastActiveB = active;
            lastIB = inten; lastWB = w; lastHazeB = haze;
            lastApplyT = Time.realtimeSinceStartup;
            foreach (Entry en in entries)
            {
                if (en.m == null) continue;
                float mult = active ? inten * haze : 1f;
                Color c = new Color(en.orig.r * mult, en.orig.g * mult * Mathf.Lerp(1f, 0.72f, w), en.orig.b * mult * Mathf.Lerp(1f, 0.4f, w), en.orig.a);
                en.m.SetColor(EmiID, c);
            }
        }
    }

    // Distant buildings LOD-cull too soon; raise lodBias at night, restore after.
    internal static class DrawDistance
    {
        static bool cached;
        static float origLodBias;

        public static void Apply(bool active)
        {
            float mult = RealisticNightPlugin.CityDrawDistanceMult.Value;
            if (active && mult > 1.001f)
            {
                if (!cached) { origLodBias = QualitySettings.lodBias; cached = true; }
                float want = origLodBias * mult;
                if (!Mathf.Approximately(QualitySettings.lodBias, want)) QualitySettings.lodBias = want;
            }
            else if (cached)
            {
                QualitySettings.lodBias = origLodBias; // truthful revert
                cached = false;
            }
        }
    }
}
