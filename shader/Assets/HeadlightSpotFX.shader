Shader "Hidden/RealisticNight/HeadlightSpotFX"
{
    // Directional headlight pools (same deferred idea, per-lamp beam dir + cone half-angle).
    Properties
    {
        _Intensity ("Intensity", Float) = 3
        _Range ("Range (m)", Float) = 40
        _FalloffK ("Falloff K", Float) = 0.02
        _NdotL ("Surface Shading", Float) = 0.7
        _Color ("Light Color", Color) = (1.0, 0.97, 0.9, 1)
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
            float4 _RN_CamPos;             // camera world position
            float4 _ProjectionParams;
            float4 _ScreenParams;
            StructuredBuffer<float4> _LampsBuf; // xyz = RAW GLOBAL pos, w = warm (kept for uniform compat)
            StructuredBuffer<float4> _SpotDirs; // xyz = beam dir (Datum is translation-only, so world == raw frame), w = cos(half-angle)
            int _LampCount;
            float _Intensity, _Range, _FalloffK, _NdotL;
            float _IntensityWarm;
            float _AtmosRange;
            float _AtmosCurve; // shared haze-curve exponent (C#: Atmospheric Curve)
            float4 _Color;
            float4 _ColorWarm;
            float _Preserve;    // headlights composite additively (_Preserve = 0 from C#)
            float _TintStrength; // 0 = neutral-white lift, 1 = full lamp tint (default 1 = today's look)

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

            // Shared spot vertex stage (all volume passes).
            struct SpotAttr { uint vid : SV_VertexID; uint iid : SV_InstanceID; };
            struct SpotVary
            {
                float4 positionCS : SV_POSITION;
                float4 screenPos : TEXCOORD0;
                float3 headRaw : TEXCOORD1;
                float3 beamDir : TEXCOORD2;
                float cosHalf : TEXCOORD3;
                float fade : TEXCOORD4;
                float2 quadUV : TEXCOORD5;
            };

            SpotVary VertSpot(SpotAttr input)
            {
                SpotVary o;
                float4 lamp = _LampsBuf[input.iid];
                float4 sd = _SpotDirs[input.iid];
                float3 headRaw = lamp.xyz;                          // RAW GLOBAL lamp position
                float3 headUnity = headRaw + _RN_OriginPos.xyz;     // -> Unity world, only to place the quad
                float2 corners[6] = { float2(-1,-1), float2(1,-1), float2(1,1), float2(-1,-1), float2(1,1), float2(-1,1) };
                float2 c = corners[input.vid];
                // Close lamp (pool wraps around us) -> fullscreen quad, else tight billboard.
                if (distance(headUnity, _RN_CamPos.xyz) < _Range * 1.2)
                {
                    o.positionCS = float4(c, 0.5, 1.0);             // fullscreen clip-space quad
                }
                else
                {
                        float3 wpos = headUnity + (_RN_CamRight.xyz * c.x + _RN_CamUp.xyz * c.y) * (_Range * 1.6);
                    o.positionCS = mul(_RN_VP, float4(wpos, 1.0));
                }
                o.screenPos = ScreenPos(o.positionCS);
                o.headRaw = headRaw;
                o.beamDir = sd.xyz;
                o.cosHalf = sd.w;
                o.quadUV = c; // quad-local corner (-1..1): border fade hides the quad edges
                o.fade = exp(-pow(distance(headUnity, _RN_CamPos.xyz) / max(1.0, _AtmosRange), max(0.05, _AtmosCurve))); // atmospheric haze
                return o;
            }

            // Shared spot fragment (base volume pass). 0.9.39-style range-normalized falloff
            // (see street shader); the cone keeps only surfaces inside the beam (smooth-edged).
            half4 FragSpot(SpotVary i) : SV_Target
            {
                float2 uv = i.screenPos.xy / i.screenPos.w;
                float raw = SAMPLE_TEXTURE2D(_CameraDepthTexture, sampler_PointClamp, uv).r;
                if (Linear01Depth(raw, _ZBufferParams) > 0.999) return half4(0, 0, 0, 0); // sky -> no light
                float3 surf = ComputeWorldSpacePosition(uv, raw, _RN_InvVP) - _RN_OriginPos.xyz; // raw global
                float3 L = i.headRaw - surf;
                float d2 = dot(L, L);
                float d = sqrt(d2);
                // Soft rim window (also hides the billboard quad edges). Falloff stays normalized.
                float R = max(1.0, _Range);
                float win = 1.0 - smoothstep(R * 0.7, R, d);
                float att = win / (1.0 + d2 * _FalloffK);
                // Cone test: surface must lie along the beam. Wide soft edge so rims melt instead of ringing.
                float3 fromLamp = (surf - i.headRaw) / max(d, 1e-3);
                float cosang = dot(fromLamp, normalize(i.beamDir));
                float cone = smoothstep(max(-1.0, i.cosHalf - 0.15), min(1.0, i.cosHalf + 0.05), cosang);
                float3 N = normalize(cross(ddy(surf), ddx(surf)));
                float ndl = lerp(1.0, saturate(dot(N, L / max(d, 1e-3))), saturate(_NdotL));
                // Tint Strength: blend the lamp color toward its own luminance (0 = neutral-white
                // lift at identical brightness, 1 = full lamp tint). Default 1.0 = today's look.
                float3 tinted = _Color.rgb * _Intensity;
                float lum = dot(tinted, float3(0.2126, 0.7152, 0.0722));
                float3 lampCol = lerp(lum.xxx, tinted, saturate(_TintStrength));
                // Border fade: 1.0 inside 90% of the quad, exactly 0.0 at its border, so the quad
                // edge can never print a line regardless of viewing angle. Interior is untouched.
                float rim = 1.0 - smoothstep(0.9, 1.0, max(abs(i.quadUV.x), abs(i.quadUV.y)));
                return half4(att * ndl * cone * lampCol * i.fade * rim, 1.0);
            }
        ENDHLSL

        // Pass 0 — SPOT VOLUMES -> light buffer (additive). Per-lamp sphere-fitted quad;
        // the fragment keeps only surfaces inside the beam cone (smooth-edged).
        Pass
        {
            Name "RN_Headlight_Spot"
            Blend One One
            HLSLPROGRAM
            #pragma vertex VertSpot
            #pragma fragment FragSpot
            #pragma target 4.5

            ENDHLSL
        }

        // Pass 1 — COMPOSITE (additive; the C# side pins _Preserve = 0 for headlights).
        Pass
        {
            Name "RN_Headlight_SpotComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 4.5

            half4 FragComposite(Varyings input) : SV_Target
            {
                float3 scene = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float3 light = SAMPLE_TEXTURE2D(_VolLight, sampler_LinearClamp, input.texcoord).rgb;
                // Surface chromaticity: the ground's own colour, independent of how dark it is.
                float maxc = max(scene.r, max(scene.g, scene.b));
                float3 hue = scene / max(maxc, 1e-4);
                // Reflectivity: dark ground absorbs (no white veil), bright ground reflects.
                float resp = saturate(maxc * 10.0);
                // Same hue x reflectivity model as street (own pass). Pinned on in C#.
                float3 mixv = lerp(float3(1.0, 1.0, 1.0), hue * resp, saturate(_Preserve));
                return half4(scene + light * mixv, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
