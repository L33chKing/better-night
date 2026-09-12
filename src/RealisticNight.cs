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
    // Plugin entry, config, night ambient, patch orchestrator.
    // Orb vs ground-pool selector (config dropdown).
    public enum LampSources { BallsAndGround, BallsOnly, GroundOnly, None }

    // Night/lighting half of the project (NVG optics live in the separate Tiered NVG mod).
    [BepInPlugin(GUID, "Better Night", "0.10.0")]
    public class RealisticNightPlugin : BaseUnityPlugin
    {
        public const string GUID = "com.leech.realisticnight";
        internal static ManualLogSource Log;
        static int camFrame = -1; // one Camera.main lookup per frame max (tag search is slow)
        static Camera camCache;
        internal static Camera MainCamera(out Vector3 pos) // pos = live pose, or zero when no camera
        {
            int f = -1;
            try { f = Time.frameCount; } catch { }
            if (f != camFrame || camCache == null)
            {
                camFrame = f;
                try { camCache = Camera.main; } catch { camCache = null; }
            }
            Camera c = camCache;
            if (c == null) { pos = Vector3.zero; return null; }
            try { pos = c.transform.position; } catch { pos = Vector3.zero; return null; }
            return c;
        }

        // General
        public static ConfigEntry<bool> Diagnostics;
        public static ConfigEntry<bool> ModEnabled;

        // True when diagnostic log lines are wanted (F1 menu top toggle). Warnings/errors always log.
        internal static bool DiagOn()
        {
            try { return Diagnostics != null && Diagnostics.Value; } catch { return false; }
        }

        // Night floor
        public static ConfigEntry<bool> NightEnabled;
        public static ConfigEntry<float> NightAmbientBoost;
        public static ConfigEntry<float> NightTintR, NightTintG, NightTintB;

        // City window boost (the only vanilla emissive boost; master/vehicle/other knobs removed in 0.9.54).
        public static ConfigEntry<bool> CityWindowsEnabled;
        public static ConfigEntry<float> CityWindowIntensity;
        public static ConfigEntry<float> CityWindowWarmth;
        public static ConfigEntry<float> CityDrawDistanceMult;
        // Road street-lighting.
        public static ConfigEntry<bool> StreetLightsEnabled;
        public static ConfigEntry<bool> StreetLightsAlwaysOn;
        public static ConfigEntry<float> StreetLightIntensity;
        // Road-type classification.
        public static ConfigEntry<float> StreetSpacingMainMult, StreetSpacingRuralMult, StreetClassifyRadius;
        public static ConfigEntry<int> StreetCityMinRoads, StreetMainMinRoads;
        public static ConfigEntry<float> StreetLightSpacing;
        public static ConfigEntry<float> StreetLightHeight;
        public static ConfigEntry<float> StreetLightSize;
        public static ConfigEntry<float> StreetLightCore;
        public static ConfigEntry<float> StreetLightFalloff;
        public static ConfigEntry<float> StreetLightSideOffset;
        public static ConfigEntry<float> StreetLightAtten;
        public static ConfigEntry<float> StreetLightAttenCurve;
        public static ConfigEntry<bool> StreetLightPoles;
        public static ConfigEntry<float> StreetLightPoleWidth;
        public static ConfigEntry<float> StreetMaxRenderDist;
        public static ConfigEntry<float> StreetPoleDrawDist;
        public static ConfigEntry<bool> StreetDestructible;
        public static ConfigEntry<bool> StreetLightSkipWater;
        public static ConfigEntry<float> StreetBuildingLampSearch;
        // --- 9. Headlights ---
        public static ConfigEntry<bool> HeadlightsEnabled;
        public static ConfigEntry<bool> HeadlightsAlwaysOn;
        public static ConfigEntry<bool> HeadlightCones;
        public static ConfigEntry<bool> HeadlightPools;
        public static ConfigEntry<float> HeadlightIntensity;
        public static ConfigEntry<float> HeadlightLensSize;
        public static ConfigEntry<float> HeadlightRange;
        public static ConfigEntry<float> HeadlightConeAngle;
        public static ConfigEntry<float> HeadlightDownTilt;
        public static ConfigEntry<float> HeadlightPoolIntensity;
        public static ConfigEntry<float> HeadlightPoolRange;
        public static ConfigEntry<float> HeadlightPoolFalloff;
        public static ConfigEntry<float> HeadlightPoolShading;
        public static ConfigEntry<float> HeadlightLensMinPx;
        public static ConfigEntry<float> HeadlightLensBoost;
        public static ConfigEntry<bool> RearLightsEnabled;
        public static ConfigEntry<float> RearRunningBoost;
        public static ConfigEntry<float> RearBrakeBoost;
        public static ConfigEntry<float> HeadlightMaxDist;
        public static ConfigEntry<float> HeadlightWarmth;
        // --- 10. Ship Lights ---
        public static ConfigEntry<bool> ShipLightsEnabled;
        public static ConfigEntry<bool> ShipLightsAlwaysOn;
        public static ConfigEntry<bool> ShipDeckFloods;
        public static ConfigEntry<float> ShipDeckIntensity;
        public static ConfigEntry<float> ShipDeckRange;
        public static ConfigEntry<float> ShipDeckPoolFalloff;
        public static ConfigEntry<float> ShipDeckPoolShading;
        public static ConfigEntry<Color> ShipDeckPoolColour;
        public static ConfigEntry<bool> ShipSearchlight;
        public static ConfigEntry<bool> ShipSearchTargetGate;
        public static ConfigEntry<float> ShipSearchIntensity;
        public static ConfigEntry<float> ShipSearchRange;
        public static ConfigEntry<float> ShipSearchPoolFalloff;
        public static ConfigEntry<float> ShipSearchPoolShading;
        public static ConfigEntry<Color> ShipSearchPoolColour;
        public static ConfigEntry<float> ShipSearchConeLength;
        public static ConfigEntry<float> ShipSearchConeAngle;
        public static ConfigEntry<float> ShipSearchConeBrightness;
        public static ConfigEntry<float> ShipSearchConeStartWidth;
        public static ConfigEntry<Color> ShipSearchConeColour;
        public static ConfigEntry<float> ShipSearchSizeScaling;
        public static ConfigEntry<float> ShipMaxDist;
        // --- 11. Missile Exhaust ---
        public static ConfigEntry<bool> MissileEnabled;
        public static ConfigEntry<float> MissileGlowIntensity;
        public static ConfigEntry<float> MissileGroundIntensity;
        public static ConfigEntry<float> MissileGroundRangeBoost;
        // --- 12. Cockpit: roof point light (local player only, gated on nav lights) ---
        public static ConfigEntry<bool> CockpitRoofEnable;
        public static ConfigEntry<float> CockpitRoofIntensity;
        public static ConfigEntry<float> CockpitRoofRange;
        public static ConfigEntry<float> CockpitRoofHeight;
        public static ConfigEntry<Color> CockpitRoofColor;
        // Which elements render: glowing balls, ground light pools, both, or neither (dropdown).
        public static ConfigEntry<LampSources> StreetSources;
        public static ConfigEntry<float> StreetGroundIntensity, StreetGroundIntensityYellow, StreetGroundRange, StreetGroundShading, StreetGroundFalloff;
        public static ConfigEntry<float> StreetGroundColorPreserve;
        public static ConfigEntry<float> StreetGroundTint;
        public static ConfigEntry<Color> StreetWhite, StreetYellow;

        // Derived from Light Sources dropdown.
        public static bool StreetBallsOn => StreetSources.Value == LampSources.BallsAndGround || StreetSources.Value == LampSources.BallsOnly;
        public static bool StreetGroundOn => StreetSources.Value == LampSources.BallsAndGround || StreetSources.Value == LampSources.GroundOnly;

        public static ConfigEntry<float> NvgWorldAmbient;

        private void Awake()
        {
            Log = Logger;

            Diagnostics = Config.Bind("1. General", "Diagnostics Logging", true, "Top toggle: our diagnostic log lines (build counts, pool status, missile types) on/off. Warnings and errors always log.");
            ModEnabled = Config.Bind("1. General", "Enabled", true, "Master switch. Off = fully vanilla (all effects revert live).");

            NightEnabled = Config.Bind("2. Night", "Enabled", false, "Lift the near-black night via ambient light. World only; cockpit unaffected.");
            NightAmbientBoost = Config.Bind("2. Night", "Ambient Boost", 0f, new ConfigDescription("Strength of the night ambient floor.", new AcceptableValueRange<float>(0f, 5f)));
            NightTintR = Config.Bind("2. Night", "Tint R", 0.020f, new ConfigDescription("red", new AcceptableValueRange<float>(0f, 0.3f)));
            NightTintG = Config.Bind("2. Night", "Tint G", 0.028f, new ConfigDescription("green", new AcceptableValueRange<float>(0f, 0.3f)));
            NightTintB = Config.Bind("2. Night", "Tint B", 0.045f, new ConfigDescription("blue", new AcceptableValueRange<float>(0f, 0.3f)));
            NvgWorldAmbient = Config.Bind("2. Night", "Extra Ambient When NVG Active", 0f, new ConfigDescription("Extra world ambient ONLY while the game's night vision is active (amplifies the night for goggles). 0 = off. The NVG optics themselves are the separate 'Tiered NVG' mod.", new AcceptableValueRange<float>(0f, 5f)));


            CityWindowsEnabled = Config.Bind("3. City Lights", "City Windows", true, "Boost city building windows at night (already-lit window materials only; dark roofs untouched).");
            CityWindowIntensity = Config.Bind("3. City Lights", "City Intensity", 2f, new ConfigDescription("City window emission multiplier. Fades with the universal haze by distance to the nearest lit city street.", new AcceptableValueRange<float>(0f, 100f)));
            CityWindowWarmth = Config.Bind("3. City Lights", "City Warmth", 0.5f, new ConfigDescription("0 = keep colours, 1 = warm amber.", new AcceptableValueRange<float>(0f, 1f)));
            CityDrawDistanceMult = Config.Bind("3. City Lights", "Building Draw Distance x", 20f, new ConfigDescription("Keeps distant buildings (and their lit windows) drawn much farther out by multiplying the LOD bias at night. 1 = vanilla. Higher = see the city glow from farther, at some GPU cost. Reverts to vanilla when off / daytime.", new AcceptableValueRange<float>(1f, 20f)));

            // --- 4. Street Lights: switches, placement, range & destruction ---
            StreetLightsEnabled = Config.Bind("4. Street Lights", "Enabled", true, "Spawn street lights along roads (poles show day + night; the lights come on at night).");
            StreetLightsAlwaysOn = Config.Bind("4. Street Lights", "Always On (ignore day/night)", false, "Force the glowing balls + ground pools ON even in daytime. Normally OFF -> they auto-toggle: bare poles in day, full lights at night.");
            StreetSources = Config.Bind("4. Street Lights", "Light Sources", LampSources.BallsAndGround, "DROPDOWN — what each lamp shows at night: BallsAndGround = orb + ground pool; BallsOnly = just orbs; GroundOnly = just pools; None = neither (poles still show).");
            StreetLightPoles = Config.Bind("4. Street Lights", "Poles", true, "Add a cantilever lamp-post (shaft + arm over the road) under each light. Poles show day AND night.");
            StreetLightSpacing = Config.Bind("4. Street Lights", "Spacing (m)", 120f, new ConfigDescription("Base lamp spacing — used for CITY roads. Main/rural roads multiply this (see Road Types).", new AcceptableValueRange<float>(5f, 500f)));
            StreetLightSideOffset = Config.Bind("4. Street Lights", "Side Offset (m)", 9f, new ConfigDescription("How far the lamp head sits from the road centre-line (the pole stands further out and an arm reaches to the head). Lamps alternate sides.", new AcceptableValueRange<float>(0f, 80f)));
            StreetLightHeight = Config.Bind("4. Street Lights", "Height (m)", 8f, new ConfigDescription("Lamp head height above the (real, raycast) ground.", new AcceptableValueRange<float>(0f, 60f)));
            StreetLightPoleWidth = Config.Bind("4. Street Lights", "Pole Width (m)", 0.35f, new ConfigDescription("Thickness of the lamp posts.", new AcceptableValueRange<float>(0.02f, 5f)));
            StreetMaxRenderDist = Config.Bind("4. Street Lights", "Max Render Distance (m)", 50000f, new ConfigDescription("Lamps within this distance of the camera render / feed the ground light. Set to your map size (some are 80km+).", new AcceptableValueRange<float>(1000f, 500000f)));
            StreetPoleDrawDist = Config.Bind("4. Street Lights", "Pole Draw Distance (m)", 2000f, new ConfigDescription("LOD: beyond this, only the light ball + ground pool show (no pole mesh). Poles are the heaviest geometry, so this is the main perf lever on huge maps.", new AcceptableValueRange<float>(0f, 500000f)));
            StreetDestructible = Config.Bind("4. Street Lights", "Destructible", true, "Poles are physically knocked down by explosion shockwaves. Each hit pole gets a rigidbody and topples/falls with real physics using the game's own blast overpressure, then its light goes out. Turn off to make poles indestructible.");
            StreetLightSkipWater = Config.Bind("4. Street Lights", "Skip Lamps Over Water", true, "Don't place lamps where the ground far below is water/seabed (causeways, bridges over sea). They paint bright streaks across the ocean. Off = lamps everywhere, including over water.");
            StreetBuildingLampSearch = Config.Bind("4. Street Lights", "Building Lamp Search (m)", 40f, new ConfigDescription("Second pass: buildings with no streetlamp within this radius get one lamp at their edge, aimed outward (for texture-painted blocks with no real roads). 0 = off.", new AcceptableValueRange<float>(0f, 500f)));

            // --- 5. Atmosphere: the shared air every light shines through ---
            StreetLightAtten = Config.Bind("5. Atmosphere", "Haze Distance (m)", 20000f, new ConfigDescription("UNIVERSAL haze distance for every light the mod adds: street orbs, street pools, headlight pools, cones, decals and city windows all relax toward vanilla past this range (no hard cutoff).", new AcceptableValueRange<float>(100f, 100000f)));
            StreetLightAttenCurve = Config.Bind("5. Atmosphere", "Haze Curve", 3f, new ConfigDescription("UNIVERSAL shape of the haze dimming. 1 = natural exponential; higher holds brightness longer then drops fast; lower dims early with a long tail. Live.", new AcceptableValueRange<float>(0.2f, 3f)));

            // --- 6. Road Types: how roads are classed City/Main/Rural (spacing + colour) ---
            StreetSpacingMainMult = Config.Bind("6. Road Types", "Main Road Spacing x", 2f, new ConfigDescription("Spacing multiplier for main roads (outside cities). 2 = twice the city spacing.", new AcceptableValueRange<float>(1f, 20f)));
            StreetSpacingRuralMult = Config.Bind("6. Road Types", "Rural Road Spacing x", 3f, new ConfigDescription("Spacing multiplier for rural roads. 3 = three times the city spacing.", new AcceptableValueRange<float>(1f, 40f)));
            StreetClassifyRadius = Config.Bind("6. Road Types", "Classify Radius (m)", 150f, new ConfigDescription("A road is classified City/Main/Rural by how many OTHER roads run within this distance of it.", new AcceptableValueRange<float>(30f, 2000f)));
            StreetCityMinRoads = Config.Bind("6. Road Types", "City: min nearby roads", 5, new ConfigDescription("At/above this many nearby roads -> CITY (base spacing, WHITE LED). Raise if too much reads as city.", new AcceptableValueRange<int>(1, 60)));
            StreetMainMinRoads = Config.Bind("6. Road Types", "Main: min nearby roads", 2, new ConfigDescription("At/above this (but below City) -> MAIN road (2x spacing, YELLOW). Below this -> RURAL (4x, YELLOW).", new AcceptableValueRange<int>(0, 30)));

            // --- 7. Lamp Orbs: the glowing ball look + lamp tints ---
            StreetLightIntensity = Config.Bind("7. Lamp Orbs", "Brightness", 2f, new ConfigDescription("Peak HDR brightness of the glow orb core (drives bloom). Independent of Glow Radius.", new AcceptableValueRange<float>(0f, 300f)));
            StreetLightSize = Config.Bind("7. Lamp Orbs", "Glow Radius (m)", 1.5f, new ConfigDescription("Diameter of the glow orb only. Independent of Brightness.", new AcceptableValueRange<float>(0.1f, 200f)));
            StreetLightCore = Config.Bind("7. Lamp Orbs", "Core (0-1)", 1f, new ConfigDescription("Fraction of the orb that is a solid-bright centre. LOWER = softer, no hard dot.", new AcceptableValueRange<float>(0f, 1f)));
            StreetLightFalloff = Config.Bind("7. Lamp Orbs", "Falloff", 0.1f, new ConfigDescription("How fast the halo fades out. Higher = tighter centre, bigger soft halo. Affects BOTH white and yellow orbs.", new AcceptableValueRange<float>(0.1f, 30f)));
            StreetWhite = Config.Bind("7. Lamp Orbs", "City Lamp Colour", new Color(0.90f, 0.95f, 1.00f, 0.8f), "Tint of WHITE (city) lamps - orbs AND pools. Alpha trims the tint strength. Live.");
            StreetYellow = Config.Bind("7. Lamp Orbs", "Sodium Lamp Colour", new Color(1.00f, 0.72f, 0.36f, 0.502f), "Tint of YELLOW (main + rural) lamps - orbs AND pools. Alpha trims the tint strength. Live.");

            // --- 8. Ground Pools: street light volumes ---
            StreetGroundIntensity = Config.Bind("8. Ground Pools", "Ground Intensity (White)", 1f, new ConfigDescription("Ground-pool brightness under WHITE (city) lamps. (Toggle pools on/off via 'Light Sources' in section 4.)", new AcceptableValueRange<float>(0f, 10f)));
            StreetGroundIntensityYellow = Config.Bind("8. Ground Pools", "Ground Intensity (Yellow)", 2f, new ConfigDescription("Ground-pool brightness under YELLOW (main + rural) lamps.", new AcceptableValueRange<float>(0f, 10f)));
            StreetGroundRange = Config.Bind("8. Ground Pools", "Ground Range (m)", 250f, new ConfigDescription("Radius each lamp lights on the ground. The pool shape normalizes to this range, so raising it also re-brightens already-lit ground.", new AcceptableValueRange<float>(1f, 3000f)));
            StreetGroundShading = Config.Bind("8. Ground Pools", "Ground Surface Shading", 0.8028169f, new ConfigDescription("0 = flat light pool (trees render cleaner), 1 = full N.L directional shading (can look pixelated on trees). At 0, raise 'Preserve Surface Colours' so surfaces keep their own colour instead of turning the lamp's colour.", new AcceptableValueRange<float>(0f, 1f)));
            StreetGroundFalloff = Config.Bind("8. Ground Pools", "Ground Falloff", 1f, new ConfigDescription("Pool tightness, NORMALIZED to Ground Range (same look at any range). 0 = flat disc filling the whole range, ~0.1 subtle centre-weighting, ~0.5 noticeably tighter, 1 = tight hot centre fading to ~4% at the rim. Note: raising Range re-brightens already-lit ground.", new AcceptableValueRange<float>(0f, 1f)));
            StreetGroundColorPreserve = Config.Bind("8. Ground Pools", "Preserve Surface Colours", 1f, new ConfigDescription("0 = plain additive lift (pools add light at full strength regardless of the ground). 1 = the lift keeps the surface's OWN colour and scales with its reflectivity - dark ground absorbs (shadows stay shadowed, no white veil), bright ground reflects (detail lightens with its real colour). Mid values blend.", new AcceptableValueRange<float>(0f, 1f)));
            StreetGroundTint = Config.Bind("8. Ground Pools", "Tint Strength", 1f, new ConfigDescription("How much of the lamp's own colour the pools carry (shared by street + headlight pools, pools only - orbs untouched). 1 = full lamp tint (today's look), 0 = neutral-white lift at identical brightness.", new AcceptableValueRange<float>(0f, 1f)));

            // --- 9. Headlights ---
            HeadlightsEnabled = Config.Bind("9. Headlights", "Enabled", true, "Volumetric headlights on ground vehicles (cones + lens glow + ground pools). Client-side, MP-safe.");
            HeadlightsAlwaysOn = Config.Bind("9. Headlights", "Always On (ignore day/night)", false, "Force headlights ON even in daytime. Normally OFF -> auto-toggle at night.");
            HeadlightCones = Config.Bind("9. Headlights", "Cones", true, "Render the volumetric beam cones + hull lens decals.");
            HeadlightPools = Config.Bind("9. Headlights", "Ground Pools", true, "Feed headlight ground pools into the deferred light pass (own intensity/range, shares the street shader).");
            HeadlightIntensity = Config.Bind("9. Headlights", "Cone Brightness", 0.3f, new ConfigDescription("Beam cone + lens HDR brightness (drives bloom).", new AcceptableValueRange<float>(0f, 30f)));
            HeadlightRange = Config.Bind("9. Headlights", "Beam Range (m)", 7f, new ConfigDescription("Length of the volumetric cone. Live — re-posed every frame.", new AcceptableValueRange<float>(5f, 300f)));
            HeadlightConeAngle = Config.Bind("9. Headlights", "Cone Half-Angle (deg)", 27f, new ConfigDescription("Beam spread per lamp. Live — re-posed every frame.", new AcceptableValueRange<float>(3f, 40f)));
            HeadlightDownTilt = Config.Bind("9. Headlights", "Down Tilt (deg)", 20f, new ConfigDescription("Aim beams slightly down so pools land ahead of the bumper. Live.", new AcceptableValueRange<float>(0f, 45f)));
            HeadlightLensSize = Config.Bind("9. Headlights", "Lens Size (m)", 0.4f, new ConfigDescription("Diameter of the hull-hugging headlamp / tail lens decals.", new AcceptableValueRange<float>(0.1f, 10f)));
            HeadlightLensMinPx = Config.Bind("9. Headlights", "Lens Min Pixels (anti-shimmer)", 2.5f, new ConfigDescription("Small lens/brake decals are clamped to at least this on-screen size (they grow instead of subpixel-flickering, like the street orbs). 0 = off. Live.", new AcceptableValueRange<float>(0f, 8f)));
            HeadlightLensBoost = Config.Bind("9. Headlights", "Lens Brightness", 4f, new ConfigDescription("Emission HDR boost of any MODELED headlamp lens renderers found by probe (bonus true-emissive on top of the decals).", new AcceptableValueRange<float>(0f, 30f)));
            RearLightsEnabled = Config.Bind("9. Headlights", "Rear Lights", true, "Emissive RED tail/brake lenses where modeled (strict: no fallback — skipped if the model has none). Dim red running lights at night, bright under braking.");
            RearRunningBoost = Config.Bind("9. Headlights", "Rear Running Brightness", 1f, new ConfigDescription("Dim red tail-light emission at night.", new AcceptableValueRange<float>(0f, 30f)));
            RearBrakeBoost = Config.Bind("9. Headlights", "Brake Brightness", 2f, new ConfigDescription("Bright red under braking (deceleration heuristic - vanilla exposes no brake signal).", new AcceptableValueRange<float>(0f, 30f)));
            HeadlightPoolIntensity = Config.Bind("9. Headlights", "Pool Intensity", 1f, new ConfigDescription("Ground-pool brightness. Pools keep the surface's own colour like street pools (own composite, independent of the street Preserve knob) - on very dark ground raise this; high values can't white-veil like the old additive pools.", new AcceptableValueRange<float>(0f, 300f)));
            HeadlightPoolRange = Config.Bind("9. Headlights", "Pool Range (m)", 100f, new ConfigDescription("Ground-pool radius per headlight (own knob, independent of street range). The pool shape normalizes to this range.", new AcceptableValueRange<float>(5f, 2000f)));
            HeadlightPoolFalloff = Config.Bind("9. Headlights", "Pool Falloff", 0.4976526f, new ConfigDescription("Pool tightness, NORMALIZED to Pool Range (same look at any range). 0 = flat disc, 1 = tight hot centre. Live.", new AcceptableValueRange<float>(0f, 1f)));
            HeadlightPoolShading = Config.Bind("9. Headlights", "Pool Surface Shading", 0.7464789f, new ConfigDescription("Headlight pool directional shading (N.L). 0 = flat, 1 = surfaces facing the lamp read brighter. Live.", new AcceptableValueRange<float>(0f, 1f)));
            HeadlightWarmth = Config.Bind("9. Headlights", "Warmth (0-1)", 0.2f, new ConfigDescription("0 = cool white LED, 1 = warm halogen.", new AcceptableValueRange<float>(0f, 1f)));
            HeadlightMaxDist = Config.Bind("9. Headlights", "Max Render Distance (m)", 8000f, new ConfigDescription("Rigs beyond this of the camera hide (cones/decals) and leave the pool feed. (Haze fade itself follows the shared Atmospheric Range.)", new AcceptableValueRange<float>(500f, 100000f)));

            // --- 10. Ship Lights ---
            ShipLightsEnabled = Config.Bind("10. Ship Lights", "Enabled", true, "Naval lighting on Ship units (nav markers + deck flood pools + bow searchlight). Client-side, MP-safe.");
            ShipLightsAlwaysOn = Config.Bind("10. Ship Lights", "Always On (ignore day/night)", false, "Force ship lights ON even in daytime. Normally OFF -> auto-toggle at night.");
            ShipDeckFloods = Config.Bind("10. Ship Lights", "Deck Floods", true, "Wide omni ground pools over the deck (own intensity/range, no beams). Flood count scales with hull length.");
            ShipDeckIntensity = Config.Bind("10. Ship Lights", "Deck Pool Intensity", 0.5f, new ConfigDescription("Deck-flood pool brightness.", new AcceptableValueRange<float>(0f, 100f)));
            ShipDeckRange = Config.Bind("10. Ship Lights", "Deck Pool Range (m)", 267.6056f, new ConfigDescription("Deck-flood pool radius.", new AcceptableValueRange<float>(5f, 500f)));
            ShipDeckPoolFalloff = Config.Bind("10. Ship Lights", "Deck Pool Falloff", 1f, new ConfigDescription("Deck-flood pool shape (same range-normalized falloff as street pools).", new AcceptableValueRange<float>(0f, 1f)));
            ShipDeckPoolShading = Config.Bind("10. Ship Lights", "Deck Pool Shading", 0.7464789f, new ConfigDescription("Deck-flood directional shading (same N.L as street pools). 0 = flat, 1 = angled surfaces read brighter.", new AcceptableValueRange<float>(0f, 1f)));
            ShipDeckPoolColour = Config.Bind("10. Ship Lights", "Deck Pool Colour", new Color(1f, 0.88f, 0.72f, 1f), "Deck-flood pool tint. Alpha trims the tint strength.");
            ShipSearchlight = Config.Bind("10. Ship Lights", "Searchlight", true, "Turret-mounted searchlight: volumetric cone + spot pool that tracks the turret's aim. Falls back to a mast fixture on turret-less hulls.");
            ShipSearchTargetGate = Config.Bind("10. Ship Lights", "Searchlight Needs Target", false, "Searchlight cone + pool shine only while its turret tracks a live target (a human-operated turret counts as tracking). Off = on all night. Mast-fallback fixtures have no turret and stay on.");
            ShipSearchIntensity = Config.Bind("10. Ship Lights", "Search Pool Intensity", 0.5f, new ConfigDescription("Searchlight pool brightness.", new AcceptableValueRange<float>(0f, 300f)));
            ShipSearchRange = Config.Bind("10. Ship Lights", "Search Pool Range (m)", 300f, new ConfigDescription("Searchlight ground-pool radius (long naval throw). Volumetric cone length is separate (below).", new AcceptableValueRange<float>(50f, 3000f)));
            ShipSearchPoolFalloff = Config.Bind("10. Ship Lights", "Search Pool Falloff", 0.5023474f, new ConfigDescription("Searchlight pool shape (same range-normalized falloff as street pools).", new AcceptableValueRange<float>(0f, 1f)));
            ShipSearchPoolShading = Config.Bind("10. Ship Lights", "Search Pool Shading", 1f, new ConfigDescription("Searchlight directional shading (same N.L as street pools). 0 = flat naval throw, 1 = angled surfaces read brighter.", new AcceptableValueRange<float>(0f, 1f)));
            ShipSearchPoolColour = Config.Bind("10. Ship Lights", "Search Pool Colour", new Color(1f, 0.96f, 1f, 0.69f), "Searchlight pool tint. Alpha trims the tint strength.");
            ShipSearchConeLength = Config.Bind("10. Ship Lights", "Search Cone Length (m)", 5000f, new ConfigDescription("Maximum volumetric beam length (cap): each turret's beam uses its weapon max range up to this. No beam exceeds it.", new AcceptableValueRange<float>(10f, 10000f)));
            ShipSearchConeAngle = Config.Bind("10. Ship Lights", "Search Cone Angle (deg)", 3f, new ConfigDescription("Beam spread as a true cone (far-end width follows length, so the angle reads honest). Live.", new AcceptableValueRange<float>(3f, 40f)));
            ShipSearchConeBrightness = Config.Bind("10. Ship Lights", "Search Cone Brightness", 1f, new ConfigDescription("Beam cone + lens fixture HDR brightness (drives bloom). Pool brightness stays on Search Pool Intensity.", new AcceptableValueRange<float>(0f, 30f)));
            ShipSearchConeStartWidth = Config.Bind("10. Ship Lights", "Search Cone Start Width (m)", 0.51f, new ConfigDescription("Beam diameter at the lens. The far end follows Cone Angle. Live.", new AcceptableValueRange<float>(0.1f, 30f)));
            ShipSearchConeColour = Config.Bind("10. Ship Lights", "Search Cone Colour", new Color(0.79f, 1f, 1f, 0.102f), "Beam cone tint. Alpha fades the beam. (Lens dot keeps its warm tint.)");
            ShipSearchSizeScaling = Config.Bind("10. Ship Lights", "Spotlight Size Scaling", 0.5962442f, new ConfigDescription("Small turret mounts throw weaker beams (dimmer + shorter) relative to the biggest mount on each hull. 0 = all equal, 1 = fully proportional.", new AcceptableValueRange<float>(0f, 1f)));
            ShipMaxDist = Config.Bind("10. Ship Lights", "Max Render Distance (m)", 80000f, new ConfigDescription("Ship rigs beyond this of the camera hide (cones/decals) and leave the pool feed. Ships stay visible far at sea.", new AcceptableValueRange<float>(500f, 100000f)));

            // --- 11. Missile Exhaust ---
            MissileEnabled = Config.Bind("11. Missile Exhaust", "Enabled", true, "Missile exhaust glow + ground lighting. Client-side, MP-safe. Covers vanilla + modded missiles (they all use the same Motor/TrailEmitter).");
            MissileGlowIntensity = Config.Bind("11. Missile Exhaust", "Exhaust Glow Intensity", 4f, new ConfigDescription("Boosts the missile's OWN exhaust flame (vanilla ParticleSystems: bigger + brighter, same particle count). 0 = vanilla flame. No fake glow added. Live.", new AcceptableValueRange<float>(0f, 30f)));
            MissileGroundIntensity = Config.Bind("11. Missile Exhaust", "Ground Light Intensity", 2f, new ConfigDescription("How much missile launches lighten surrounding ground + buildings. Scales BOTH the vanilla Motor Light[] (real lights) and our deferred ground pools (bypasses URP 8-light cap). 0 = no environment light. Live.", new AcceptableValueRange<float>(0f, 100f)));
            MissileGroundRangeBoost = Config.Bind("11. Missile Exhaust", "Ground Range Boost x", 1f, new ConfigDescription("Multiplier on each missile's light throw. Missiles with a vanilla Motor light use ITS authored prefab range x boost (author-tuned per weapon, e.g. anti-ship 500m vs AAM 100-200m); light-less modded missiles use the thrust/size fallback. 1 = authored. Live.", new AcceptableValueRange<float>(0.2f, 5f)));

            // --- 12. Cockpit ---
            CockpitRoofEnable = Config.Bind("12. Cockpit", "Roof Light Enable", true, "Point light above camera in cockpit. Follows nav-light state. Live.");
            CockpitRoofIntensity = Config.Bind("12. Cockpit", "Roof Light Intensity", 0.4f, new ConfigDescription("Cockpit roof light brightness.", new AcceptableValueRange<float>(0.1f, 2f)));
            CockpitRoofRange = Config.Bind("12. Cockpit", "Roof Light Range (m)", 4f, new ConfigDescription("Cockpit roof light reach.", new AcceptableValueRange<float>(1f, 10f)));
            CockpitRoofHeight = Config.Bind("12. Cockpit", "Roof Light Height (m)", 0.5f, new ConfigDescription("Offset above camera.", new AcceptableValueRange<float>(0.1f, 2f)));
            CockpitRoofColor = Config.Bind("12. Cockpit", "Roof Light Colour", new Color(1f, 0.86f, 0.71f), "Warm white tint.");

            new Harmony(GUID).PatchAll();
            Log.LogInfo("Better Night 0.10.0 loaded.");
        }
    }
    [HarmonyPatch(typeof(LevelInfo), "UpdateTimeOfDayLighting")]
    public static class LevelInfo_Lighting_Patch
    {
        static readonly AccessTools.FieldRef<LevelInfo, float> AmbientIntensityRef = AccessTools.FieldRefAccess<LevelInfo, float>("ambientIntensity");
        static readonly AccessTools.FieldRef<LevelInfo, float> LightingLastRef = AccessTools.FieldRefAccess<LevelInfo, float>("lightingLastUpdated");
        static readonly AccessTools.FieldRef<NightVision, bool> NvgActiveRef = AccessTools.FieldRefAccess<NightVision, bool>("nightVisActive");

        static float lastSeen = -1f;
        static float nextPoolStatus;

        static void Postfix(LevelInfo __instance)
        {
            bool on = RealisticNightPlugin.ModEnabled.Value;
            bool night = !__instance.isDayLight;

            LightBoost.Apply(on, night);
            DrawDistance.Apply(on && night && RealisticNightPlugin.CityWindowsEnabled.Value);
            // Street lights: persist day+night, pools gate on night.
            StreetLights.Apply(on && RealisticNightPlugin.StreetLightsEnabled.Value,
                               night || RealisticNightPlugin.StreetLightsAlwaysOn.Value);
            // Headlights / ships persist day+night; beams/pools gate on night.
            try
            {
                VehicleHeadlights.Apply(on && RealisticNightPlugin.HeadlightsEnabled.Value,
                                        night || RealisticNightPlugin.HeadlightsAlwaysOn.Value);
            }
            catch { }
            // Ship lights.
            try
            {
                ShipLights.Apply(on && RealisticNightPlugin.ShipLightsEnabled.Value,
                                 night || RealisticNightPlugin.ShipLightsAlwaysOn.Value);
            }
            catch { }
            // Missile exhaust.
            try
            {
                MissileExhaust.Apply(on && RealisticNightPlugin.MissileEnabled.Value, night);
            }
            catch { }
            // Cockpit roof light.
            try
            {
                CockpitLightManager.Apply(on, night);
                CockpitLightManager.Tick();
            }
            catch { }
            // Pool-pipeline diagnostics (30s).
            try
            {
                if (RealisticNightPlugin.DiagOn() && Time.realtimeSinceStartup >= nextPoolStatus)
                {
                    nextPoolStatus = Time.realtimeSinceStartup + 30f;
                    StreetLightFX.LogPoolStatus(night);
                }
            }
            catch { }

            if (!on || !RealisticNightPlugin.NightEnabled.Value) return;

            float stamp = LightingLastRef(__instance);
            if (stamp == lastSeen) return;
            lastSeen = stamp;

            float ai = AmbientIntensityRef(__instance);
            float nightFactor = 1f - Mathf.InverseLerp(0.02f, 0.20f, ai);
            if (nightFactor <= 0f) return;

            bool nvgActive = false;
            try { nvgActive = NightVision.i != null && NvgActiveRef(NightVision.i); } catch { }
            float nvgAmb = nvgActive ? RealisticNightPlugin.NvgWorldAmbient.Value : 0f;

            float boost = (RealisticNightPlugin.NightAmbientBoost.Value + nvgAmb) * nightFactor;
            Color lift = new Color(RealisticNightPlugin.NightTintR.Value, RealisticNightPlugin.NightTintG.Value, RealisticNightPlugin.NightTintB.Value) * boost;
            RenderSettings.ambientSkyColor += lift;
            RenderSettings.ambientEquatorColor += lift;
            RenderSettings.ambientGroundColor += lift;
            RenderSettings.ambientIntensity += 0.15f * boost;
        }
    }
}
