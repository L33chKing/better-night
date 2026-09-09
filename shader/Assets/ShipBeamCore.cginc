#ifndef SHIPBEAM_CORE
#define SHIPBEAM_CORE
#include "UnityCG.cginc"

sampler2D _MainTex;
float4 _MainTex_ST;
half4 _Color;
float _EdgePow;
float _AxialBoost;
float _AxialPow;
float _ConeLen;
float _ConeBaseR;
float _ConeApexR;

struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float3 normal : NORMAL; fixed4 color : COLOR; };
struct v2f {
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
    float3 wpos : TEXCOORD1;
    float3 wnormal : TEXCOORD2;
    float3 bdir : TEXCOORD3;
    fixed4 color : COLOR;
    float2 beam : TEXCOORD4; // x = camera-inside factor, y = camera axial pos (0 apex -> 1 base)
};

v2f vert(appdata v)
{
    v2f o;
    float4 wp = mul(unity_ObjectToWorld, v.vertex);
    o.pos = mul(UNITY_MATRIX_VP, wp);
    o.wpos = wp.xyz;
    o.wnormal = UnityObjectToWorldNormal(v.normal);
    o.bdir = normalize(mul((float3x3)unity_ObjectToWorld, float3(0.0, 0.0, 1.0))); // beam axis (GOs ride unscaled)
    o.uv = TRANSFORM_TEX(v.uv, _MainTex);
    o.color = v.color;
    float3 camOS = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos.xyz, 1.0)).xyz; // camera in cone space
    float zT = clamp(camOS.z / max(_ConeLen, 1e-3), 0.0, 1.0);
    float rAt = _ConeApexR + (_ConeBaseR - _ConeApexR) * zT;
    float margin = min(min(rAt - length(camOS.xy), camOS.z), _ConeLen - camOS.z);
    o.beam = float2(smoothstep(-1.5, 1.5, margin), zT); // soft wall crossing (no pop)
    return o;
}
#endif
