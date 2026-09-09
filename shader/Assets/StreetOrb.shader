Shader "Hidden/RealisticNight/StreetOrb"
{
    // GPU-billboarded lamp orb (built-in shader so URP's variant stripper leaves it alone).
    Properties
    {
        _MainTex ("Glow", 2D) = "white" {}
        _Color ("Color (HDR)", Color) = (1, 0.8, 0.45, 1)
        _Size ("Size (m)", Float) = 3.6
        _AtmosRange ("Atmospheric Range (m)", Float) = 11441
        _AtmosCurve ("Atmospheric Curve (shared)", Float) = 1
        _MaxDist ("Max Distance (m)", Float) = 80000
        _MinPixels ("Min On-Screen Size (px)", Float) = 1.3
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Blend One OneMinusSrcAlpha        // premultiplied soft glow; HDR colour -> bloom
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _Color;
            float _Size;
            float _AtmosRange;
            float _AtmosCurve; // shared haze-curve exponent: fade = exp(-(d/R)^p)
            float _MaxDist;
            float _MinPixels;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float2 corner : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float fade : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 originWS = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz; // baked lamp center (merged mesh; one regular GO per colour)
                float3 camR = UNITY_MATRIX_V._m00_m01_m02;   // camera right (world)
                float3 camU = UNITY_MATRIX_V._m10_m11_m12;   // camera up (world)

                // Anti-shimmer: clamp to a minimum on-screen size and dim to conserve energy.
                float4 cC = UnityWorldToClipPos(originWS);
                float4 cE = UnityWorldToClipPos(originWS + camR * _Size);
                // On-screen half-width of the orb, in pixels (guard against w<=0 = behind camera).
                float pxRadius = (cC.w > 1e-4 && cE.w > 1e-4)
                    ? abs(cE.x / cE.w - cC.x / cC.w) * 0.5 * _ScreenParams.x
                    : _MinPixels;
                float grow = max(1.0, _MinPixels / max(pxRadius, 1e-3)); // enlarge only when sub-minPixels
                float af   = 1.0 / (grow * grow);                        // spread wider -> lower alpha (energy kept)

                float3 wpos = originWS + (camR * v.corner.x + camU * v.corner.y) * (_Size * grow);
                o.pos = UnityWorldToClipPos(wpos);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                float d = distance(originWS, _WorldSpaceCameraPos.xyz);
                o.fade = exp(-pow(d / max(1.0, _AtmosRange), max(0.05, _AtmosCurve))) * (d <= _MaxDist ? 1.0 : 0.0) * af; // haze fade + map cutoff + AA dim
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half a = tex2D(_MainTex, i.uv).a * i.fade;
                return half4(_Color.rgb * a, a);   // premultiplied
            }
            ENDCG
        }
    }
    Fallback Off
}
