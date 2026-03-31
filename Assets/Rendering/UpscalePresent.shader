Shader "Hidden/RenderingSandbox/UpscalePresent"
{
    Properties
    {
        _SourceTexture("Source Texture", 2D) = "white" {}
        _HistoryTexture("History Texture", 2D) = "black" {}
        _SharpenStrength("Sharpen Strength", Range(0, 1.5)) = 0
        _HistoryWeight("History Weight", Range(0, 0.98)) = 0.85
        _HistoryUvOffset("History UV Offset", Vector) = (0, 0, 0, 0)
        _ApproximateDepth01("Approximate Depth 0-1", Range(0, 1)) = 0.55
        _HistoryClampAmount("History Clamp Amount", Range(0.01, 0.5)) = 0.12
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            sampler2D _SourceTexture;
            sampler2D _HistoryTexture;

            float4 _SourceTexelSize;
            float _SharpenStrength;
            float _TemporalEnabled;
            float _HasHistory;
            float _HistoryWeight;
            float4 _HistoryUvOffset;
            float _ReprojectionMode;
            float _ApproximateDepth01;
            float4x4 _CurrentInverseViewProjection;
            float4x4 _PreviousViewProjection;
            float _HistoryClampingEnabled;
            float _HistoryClampAmount;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                // The presentation mesh is a normal Unity quad, so its object-space vertices
                // must be transformed into clip space. Passing positionOS through directly only
                // covers a small rectangle in the center of the screen.
                output.positionCS = UnityObjectToClipPos(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;

                // Sampling is how the GPU reads colors from the low-res source texture.
                // Point sampling returns one texel, while bilinear sampling blends nearby texels.
                float4 center = tex2D(_SourceTexture, uv);

                float4 currentFrame = center;

                // Sharpening adds back local contrast after bilinear smoothing. It can make
                // edges feel more detailed, but too much sharpening also creates halos.
                if (_SharpenStrength > 0.0001)
                {
                    float2 texel = _SourceTexelSize.xy;
                    float4 left = tex2D(_SourceTexture, uv + float2(-texel.x, 0));
                    float4 right = tex2D(_SourceTexture, uv + float2(texel.x, 0));
                    float4 up = tex2D(_SourceTexture, uv + float2(0, texel.y));
                    float4 down = tex2D(_SourceTexture, uv + float2(0, -texel.y));

                    float4 blurred = (center + left + right + up + down) * 0.2;
                    currentFrame = saturate(center + (center - blurred) * _SharpenStrength);
                }

                if (_TemporalEnabled <= 0.5 || _HasHistory <= 0.5)
                {
                    return currentFrame;
                }

                float2 historyUv = uv;

                if (_ReprojectionMode > 1.5)
                {
                    // Global UV offsets are only a rough approximation because every pixel moves
                    // differently as depth changes. Matrix reprojection tries to do something more
                    // physically motivated: reconstruct a point from the current screen position,
                    // then project that point into the previous frame before sampling history.
                    // This slice still uses an approximate depth plane, so it is educational rather
                    // than correct. Full solutions use scene depth plus motion vectors.
                    float2 ndc = uv * 2.0 - 1.0;
                    float4 clipNear = float4(ndc, -1.0, 1.0);
                    float4 clipFar = float4(ndc, 1.0, 1.0);

                    float4 worldNear = mul(_CurrentInverseViewProjection, clipNear);
                    float4 worldFar = mul(_CurrentInverseViewProjection, clipFar);
                    worldNear /= worldNear.w;
                    worldFar /= worldFar.w;

                    float4 worldPosition = lerp(worldNear, worldFar, _ApproximateDepth01);
                    float4 previousClip = mul(_PreviousViewProjection, worldPosition);
                    historyUv = (previousClip.xy / previousClip.w) * 0.5 + 0.5;
                }
                else if (_ReprojectionMode > 0.5)
                {
                    // Same-UV history sampling fails during motion because the old frame usually
                    // belongs at a different screen position. This simple mode applies one
                    // uniform offset to the whole screen as an intermediate experiment.
                    historyUv = uv + _HistoryUvOffset.xy;
                }

                float4 history = tex2D(_HistoryTexture, historyUv);

                // History clamping limits how far old history values are allowed to drift from
                // the current frame. Ghosting often comes from stale history lingering too long,
                // so clamping helps even when reprojection is still imperfect.
                if (_HistoryClampingEnabled > 0.5)
                {
                    float4 minAllowed = currentFrame - _HistoryClampAmount;
                    float4 maxAllowed = currentFrame + _HistoryClampAmount;
                    history = clamp(history, minAllowed, maxAllowed);
                }

                return lerp(currentFrame, history, _HistoryWeight);
            }
            ENDHLSL
        }
    }
}
