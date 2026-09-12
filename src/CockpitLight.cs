using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RealisticNight
{
    internal static class CockpitLightManager
    {
        internal static bool Active;
        static float nextScan;
        static readonly Dictionary<int, CockpitRig> rigs = new Dictionary<int, CockpitRig>();

        static FieldInfo cockpitField;
        static FieldInfo cockpitXformField;
        static FieldInfo navLightsIsOnField;
        static FieldInfo gearDeployedField;

        internal static void Apply(bool on, bool night)
        {
            Active = on;
            if (!on)
            {
                foreach (var kv in rigs)
                    try { if (kv.Value != null) kv.Value.Shutdown(); } catch { }
                rigs.Clear();
            }
        }

        internal static void Tick()
        {
            if (!Active) return;
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 1f;

            var dead = new List<int>();
            foreach (var kv in rigs) if (kv.Value == null) dead.Add(kv.Key);
            foreach (int k in dead) rigs.Remove(k);

            try
            {
                Aircraft ac = GetLocalAircraft();
                if (ac == null) return;
                int id = ac.GetInstanceID();
                if (rigs.ContainsKey(id)) return;

                Transform cockpitXform = GetCockpitXform(ac);
                if (cockpitXform == null) return;

                var rig = ac.gameObject.AddComponent<CockpitRig>();
                rig.Setup(ac, cockpitXform);
                rigs[id] = rig;
            }
            catch { }
        }

        static Aircraft GetLocalAircraft()
        {
            try
            {
                var hud = SceneSingleton<CombatHUD>.i;
                if (hud == null) return null;
                var ac = hud.aircraft;
                if (ac == null) return null;
                if (!GameManager.IsLocalAircraft(ac)) return null;
                return ac;
            }
            catch { return null; }
        }

        static Transform GetCockpitXform(Aircraft ac)
        {
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                if (cockpitField == null) cockpitField = typeof(Aircraft).GetField("cockpit", f);
                if (cockpitField == null) return null;
                object cockpit = cockpitField.GetValue(ac);
                if (cockpit == null) return null;

                if (cockpitXformField == null) cockpitXformField = cockpit.GetType().GetField("xform", f);
                if (cockpitXformField != null) return cockpitXformField.GetValue(cockpit) as Transform;

                if (cockpit is Component comp) return comp.transform;
            }
            catch { }
            return null;
        }

        internal static bool IsInCockpit()
        {
            try { return CameraStateManager.cameraMode == CameraMode.cockpit; }
            catch { return false; }
        }

        internal static bool IsNavLightsOn(Aircraft ac)
        {
            const BindingFlags f = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                bool isOn = false;

                NavLights nl = ac.GetComponentInChildren<NavLights>(true);
                if (nl != null)
                {
                    if (navLightsIsOnField == null)
                        navLightsIsOnField = typeof(NavLights).GetField("isOn", f);
                    if (navLightsIsOnField != null)
                        isOn = (bool)navLightsIsOnField.GetValue(nl);
                }

                if (!isOn)
                {
                    if (gearDeployedField == null)
                        gearDeployedField = typeof(Aircraft).GetField("gearDeployed", f);
                    if (gearDeployedField != null)
                        isOn = (bool)gearDeployedField.GetValue(ac);
                }

                return isOn;
            }
            catch { return false; }
        }
    }

    public class CockpitRig : MonoBehaviour
    {
        Aircraft aircraft;
        Transform cockpitXform;
        Light roofLight;
        bool roofLightEnabled;
        float roofHeight, roofIntensity, roofRange;
        Color roofColor;

        internal void Setup(Aircraft ac, Transform xform)
        {
            aircraft = ac;
            cockpitXform = xform;
            ReadConfig();
            CreateRoofLight();
        }

        void ReadConfig()
        {
            try { roofLightEnabled = RealisticNightPlugin.CockpitRoofEnable.Value; } catch { roofLightEnabled = true; }
            try { roofHeight = Mathf.Clamp(RealisticNightPlugin.CockpitRoofHeight.Value, 0.1f, 2f); } catch { roofHeight = 0.5f; }
            try { roofIntensity = Mathf.Max(0f, RealisticNightPlugin.CockpitRoofIntensity.Value); } catch { roofIntensity = 0.4f; }
            try { roofRange = Mathf.Max(1f, RealisticNightPlugin.CockpitRoofRange.Value); } catch { roofRange = 4f; }
            try { roofColor = RealisticNightPlugin.CockpitRoofColor.Value; } catch { roofColor = new Color(1f, 0.86f, 0.71f); }
        }

        void CreateRoofLight()
        {
            try
            {
                var go = new GameObject("RN_CockpitRoofLight");
                roofLight = go.AddComponent<Light>();
                roofLight.type = LightType.Point;
                roofLight.color = roofColor;
                roofLight.intensity = roofIntensity;
                roofLight.range = roofRange;
                roofLight.renderMode = LightRenderMode.Auto;
                roofLight.enabled = false;
            }
            catch { roofLight = null; }
        }

        void LateUpdate()
        {
            if (aircraft == null || cockpitXform == null) { Shutdown(); return; }
            try { if (aircraft.disabled) { Shutdown(); return; } } catch { }

            ReadConfig();

            bool inCockpit = CockpitLightManager.IsInCockpit();
            bool navOn = false;
            try { navOn = CockpitLightManager.IsNavLightsOn(aircraft); } catch { }

            bool wantLight = inCockpit && navOn && roofLightEnabled;
            SetRoofLight(wantLight);
        }

        void SetRoofLight(bool on)
        {
            if (roofLight == null) return;
            try
            {
                if (on)
                {
                    Camera cam = RealisticNightPlugin.MainCamera(out _);
                    if (cam == null) { roofLight.enabled = false; return; }
                    roofLight.enabled = true;
                    roofLight.intensity = roofIntensity;
                    roofLight.range = roofRange;
                    roofLight.color = roofColor;
                    roofLight.transform.position = cam.transform.position + Vector3.up * roofHeight;
                    roofLight.transform.rotation = Quaternion.identity;
                }
                else
                {
                    roofLight.enabled = false;
                }
            }
            catch { }
        }

        internal void Shutdown()
        {
            try { if (roofLight != null) { Destroy(roofLight.gameObject); roofLight = null; } } catch { }
            aircraft = null;
            cockpitXform = null;
        }

        void OnDestroy() { Shutdown(); }
    }
}
