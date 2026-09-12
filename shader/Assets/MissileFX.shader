Shader "Hidden/RealisticNight/MissileFX"
{
    // Missile exhaust ground pools: omni like StreetLightFX
    Properties
    {
        _Intensity ("Intensity", Float) = 3
        _Range ("Range fallback (m)", Float) = 800
        _FalloffK ("Falloff K (legacy)", Float) = 0.02
        _Falloff01 ("Falloff 0-1", Float) = 0.5
        _NdotL ("Surface Shading", Float) = 0.75
        _Color ("Light Color", Color) = (1.0, 0.6, 0.3, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        ZWrite Off ZTest Always Cull Off

        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            TEXTURE2D(_BlitTexture);
            float4 _BlitScaleBias;
            TEXTURE2D(_CameraDepthTexture);
            TEXTURE2D(_VolLight);        // full-res volume light (composite input)

            float4 _ZBufferParams;
            float4x4 _RN_InvVP;             // inverse(GPUProj * view), current frame
            float4 _RN_OriginPos;          // Datum.origin world pos -> reconstructed ground - this = RAW GLOBAL
            float4x4 _RN_VP;               // GPUProj * view (forward), volume vertex transform
            float4 _RN_CamRight, _RN_CamUp; // camera basis in world (billboard the volume quad)
            float4 _RN_CamPos;             // camera world position (decide fullscreen vs tight quad)
            float4 _ProjectionParams;      // .x = -1 when the projection is Y-flipped (screen-UV helper)
            float4 _ScreenParams;
            float _RN_Time;                 // game time (C# feeds Time.time per frame) for the flame flicker
            StructuredBuffer<float4> _LampsBuf; // xyz = RAW GLOBAL pos, w = per-lamp range (m), <=1 = use _Range
            StructuredBuffer<float4> _SpotDirs;  // x = thrust brightness scale, y = flicker seed (rest unused)
            int _LampCount;
            float _Intensity, _Range, _FalloffK, _NdotL;
            float _IntensityWarm; // unused (kept so shared C# look code can set it harmlessly)
            float _AtmosRange;    // haze: pools fade with camera->lamp distance (shared with orbs)
            float _AtmosCurve;    // shared haze-curve exponent
            float4 _Color;      // fire tint (all missile lamps share it; per-lamp warm blend removed)
            float4 _ColorWarm;  // unused (kept for compat)
            float _Falloff01;   // 0 = flat disc, 1 = tight hot centre (same convention as street Ground Falloff)
            float _Preserve;    // 1 = lift keeps surface colour x reflectivity (missiles always preserve)
            float _TintStrength;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 texcoord : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                float2 uv = GetFullScreenTriangleTexCoord(input.vertexID);
                o.texcoord = uv * _BlitScaleBias.xy + _BlitScaleBias.zw;
                return o;
            }

            float4 ScreenPos(float4 pos)
            {
                float4 o = pos * 0.5;
                o.xy = float2(o.x, o.y * _ProjectionParams.x) + o.w;
                o.zw = pos.zw;
                return o;
            }

            struct VolAttr { uint vid : SV_VertexID; uint iid : SV_InstanceID; };

            struct VolVary { float4 positionCS : SV_POSITION; float4 screenPos : TEXCOORD0; float3 headRaw : TEXCOORD1; float range : TEXCOORD2; float fade : TEXCOORD3; float2 quadUV : TEXCOORD4; float bright : TEXCOORD5; float seed : TEXCOORD6; };

            VolVary VertVol(VolAttr input)
            {
                VolVary o;
                float4 lamp = _LampsBuf[input.iid];
                float3 headRaw = lamp.xyz;                          // RAW GLOBAL lamp position
                float R = (lamp.w > 1.0) ? lamp.w : max(1.0, _Range); // per-lamp range, global fallback
                float3 headUnity = headRaw + _RN_OriginPos.xyz;     // -> Unity world, only to place the quad
                float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };
                float2 c = corners[input.vid];
                // Close lamp (pool wraps around us) -> fullscreen quad, else tight billboard sized to THIS lamp.
                if (distance(headUnity, _RN_CamPos.xyz) < R * 1.2)
                {
                    o.positionCS = float4(c, 0.5, 1.0);             // fullscreen clip-space quad
                }
                else
                {
                    float3 wpos = headUnity + (_RN_CamRight.xyz * c.x + _RN_CamUp.xyz * c.y) * (R * 1.6);
                    o.positionCS = mul(_RN_VP, float4(wpos, 1.0));
                }
                o.screenPos = ScreenPos(o.positionCS);
                o.headRaw = headRaw;
                o.range = R;
                o.quadUV = c;
                o.fade = exp(-pow(distance(headUnity, _RN_CamPos.xyz) / max(1.0, _AtmosRange), max(0.05, _AtmosCurve)));
                float2 ld = _SpotDirs[input.iid].xy; // per-lamp thrust brightness + flicker seed
                o.bright = (ld.x > 0.001) ? ld.x : 1.0;
                o.seed = ld.y;
                return o;
            }

            // Pool fragment: per-lamp range + shared 0-1 falloff shape + rim window.
            half4 FragVol(VolVary i) : SV_Target
            {
                float2 uv = i.screenPos.xy / i.screenPos.w;
                float raw = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_PointClamp, uv).r;
                if (Linear01Depth(raw, _ZBufferParams) > 0.999) return half4(0, 0, 0, 0); // sky -> no light
                float3 surf = ComputeWorldSpacePosition(uv, raw, _RN_InvVP) - _RN_OriginPos.xyz; // raw global
                float3 L = i.headRaw - surf;
                float d2 = dot(L, L);
                float d = sqrt(d2);
                float3 N = normalize(cross(ddy(surf), ddx(surf)));
                float ndl = lerp(1.0, saturate(dot(N, L / max(d, 1e-3))), saturate(_NdotL));
                float R = max(1.0, i.range);
                float win = 1.0 - smoothstep(R * 0.7, R, d);
                float gf = saturate(_Falloff01);
                float att = win / (1.0 + d2 * (gf * gf * 25.0) / (R * R));
                // Exhaust light: thrust brightness x global intensity, with a subtle flame flicker
                // (two detuned sines so neighbouring missiles never pulse in sync).
                float fl = 1.0 + 0.12 * sin(_RN_Time * 13.0 + i.seed * 17.0) * sin(_RN_Time * 7.7 + i.seed * 29.0);
                float3 tinted = _Color.rgb * (_Intensity * i.bright * fl);
                float lum = dot(tinted, float3(0.2126, 0.7152, 0.0722));
                float3 lampCol = lerp(lum.xxx, tinted, saturate(_TintStrength));
                float rim = 1.0 - smoothstep(0.9, 1.0, max(abs(i.quadUV.x), abs(i.quadUV.y)));
                return half4(att * ndl * lampCol * i.fade * rim, 1.0);
            }
        ENDHLSL

        Pass
        {
            Name "RN_Missile_Volume"
            Blend One One
            HLSLPROGRAM
            #pragma vertex VertVol
            #pragma fragment FragVol
            #pragma target 4.5

            ENDHLSL
        }

        Pass
        {
            Name "RN_Missile_Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 4.5

            half4 FragComposite(Varyings input) : SV_Target
            {
                float3 scene = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float3 light = SAMPLE_TEXTURE2D(_VolLight, sampler_LinearClamp, input.texcoord).rgb;
                float maxc = max(scene.r, max(scene.g, scene.b));
                float3 hue = scene / max(maxc, 1e-4);
                float resp = saturate(maxc * 10.0);
                float3 mixv = lerp(float3(1.0, 1.0, 1.0), hue * resp, saturate(_Preserve));
                return half4(scene + light * mixv, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
