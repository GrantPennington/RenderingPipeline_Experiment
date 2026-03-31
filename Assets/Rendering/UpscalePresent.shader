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
            sampler2D _CameraDepthTexture;
            sampler2D _CameraMotionVectorsTexture;

            float4 _SourceTexelSize;
            float _SharpenStrength;
            float _TemporalEnabled;
            float _HasHistory;
            float _HistoryWeight;
            float4 _HistoryUvOffset;
            float _ReprojectionMode;
            float _SimpleReprojectionEnabled;
            float _MatrixReprojectionEnabled;
            float _RealDepthReprojectionEnabled;
            float _MotionVectorReprojectionEnabled;
            float _ApproximateDepth01;
            float4x4 _CurrentInverseViewProjection;
            float4x4 _PreviousViewProjection;
            float _HistoryClampingEnabled;
            float _HistoryClampAmount;
            float _ReprojectionValidityMaskingEnabled;
            float _DebugVisualizationMode;
            float _DebugEffectiveHistoryWeight;
            float _DifferenceDebugScale;
            float _DifferenceDebugThreshold;
            float _DifferenceDebugExponent;

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
                float sceneDepth = tex2D(_CameraDepthTexture, uv).r;
                // _CameraMotionVectorsTexture is the camera-global motion vector texture Unity
                // exposes for the currently rendering camera. This fullscreen presentation path
                // is not a URP ScriptableRenderPass with ConfigureInput(Motion), so the render-
                // pass-specific _MotionVectorTexture hookup is not reliable here.
                float2 motionVectorUv = tex2D(_CameraMotionVectorsTexture, uv).xy;
                float depthTexelX = 1.0 / max(_ScreenParams.x, 1.0);
                float depthTexelY = 1.0 / max(_ScreenParams.y, 1.0);
                float reprojectionValidity = 1.0;
                float previousClipW = 1.0;
                float motionVectorMagnitude = length(motionVectorUv);
                float motionVectorLooksValid = step(motionVectorMagnitude, 0.25);
                motionVectorLooksValid *= step(0.000001, abs(motionVectorUv.x) + abs(motionVectorUv.y));

                bool usedMotionVectorReprojection = false;
                bool canUseMotionVectorReprojection =
                    _MotionVectorReprojectionEnabled > 0.5 &&
                    motionVectorLooksValid > 0.5;

                if (canUseMotionVectorReprojection)
                {
                    // Motion vectors describe how this pixel moved across the screen. That is
                    // different from camera/depth reprojection, which infers motion from camera
                    // transforms and depth. Using the motion vector lets history lookup follow
                    // the pixel's previous location more directly.
                    historyUv = uv - motionVectorUv;
                    usedMotionVectorReprojection = true;
                }
                else if (_RealDepthReprojectionEnabled > 0.5)
                {
                    // Real depth improves reprojection because each pixel reconstructs a point
                    // from its own scene depth instead of assuming one flat depth plane across
                    // the whole screen. That makes camera-motion reprojection more spatially
                    // coherent, even though object motion and occlusion changes can still fail
                    // without motion vectors and dedicated disocclusion handling.
                    float2 ndc = uv * 2.0 - 1.0;
                    float clipDepth = UNITY_NEAR_CLIP_VALUE < 0.0 ? (sceneDepth * 2.0 - 1.0) : sceneDepth;
                    float4 clipPosition = float4(ndc, clipDepth, 1.0);

                    // Current UV + depth + inverse VP reconstructs a world-space position for
                    // this pixel. The previous VP matrix then asks where that same point lived
                    // on the screen last frame so history can be sampled there.
                    float4 worldPosition = mul(_CurrentInverseViewProjection, clipPosition);
                    worldPosition /= max(worldPosition.w, 0.0001);

                    float4 previousClip = mul(_PreviousViewProjection, worldPosition);
                    previousClipW = previousClip.w;
                    historyUv = (previousClip.xy / max(previousClip.w, 0.0001)) * 0.5 + 0.5;
                }
                else if (_MatrixReprojectionEnabled > 0.5)
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
                    previousClipW = previousClip.w;
                    historyUv = (previousClip.xy / max(previousClip.w, 0.0001)) * 0.5 + 0.5;
                }
                else if (_SimpleReprojectionEnabled > 0.5)
                {
                    // Same-UV history sampling fails during motion because the old frame usually
                    // belongs at a different screen position. This simple mode applies one
                    // uniform offset to the whole screen as an intermediate experiment.
                    historyUv = uv + _HistoryUvOffset.xy;
                }

                // Reprojection can still be wrong even with depth because new surfaces can
                // become visible, old surfaces can disappear, and object motion is not tracked.
                // This first-pass validity mask treats off-screen samples, unusable depth, and
                // strong current-frame depth discontinuities as signs that history is risky.
                if (_ReprojectionValidityMaskingEnabled > 0.5)
                {
                    float2 historyUvInside = step(float2(0.0, 0.0), historyUv) * step(historyUv, float2(1.0, 1.0));
                    reprojectionValidity *= historyUvInside.x * historyUvInside.y;

                    // A hard inside/outside test is not very informative on its own. This small
                    // border fade tightens the heuristic so pixels near the reprojection edge
                    // also lose trust instead of staying almost pure white in the debug view.
                    float2 historyUvEdgeDistance = min(historyUv, 1.0 - historyUv);
                    float edgeValidity = saturate(min(historyUvEdgeDistance.x, historyUvEdgeDistance.y) * 48.0);
                    reprojectionValidity *= edgeValidity;

                    if (!usedMotionVectorReprojection && _RealDepthReprojectionEnabled > 0.5)
                    {
                        float depthValid = step(0.0001, sceneDepth) * step(sceneDepth, 0.9999);
                        reprojectionValidity *= depthValid;
                    }

                    if (!usedMotionVectorReprojection && (_RealDepthReprojectionEnabled > 0.5 || _MatrixReprojectionEnabled > 0.5))
                    {
                        float depthLeft = tex2D(_CameraDepthTexture, uv + float2(-depthTexelX, 0.0)).r;
                        float depthRight = tex2D(_CameraDepthTexture, uv + float2(depthTexelX, 0.0)).r;
                        float depthUp = tex2D(_CameraDepthTexture, uv + float2(0.0, depthTexelY)).r;
                        float depthDown = tex2D(_CameraDepthTexture, uv + float2(0.0, -depthTexelY)).r;

                        float localDepthEdge =
                            max(abs(sceneDepth - depthLeft), abs(sceneDepth - depthRight));
                        localDepthEdge =
                            max(localDepthEdge, max(abs(sceneDepth - depthUp), abs(sceneDepth - depthDown)));

                        float depthEdgeRejection = smoothstep(0.0015, 0.008, localDepthEdge);
                        reprojectionValidity *= (1.0 - depthEdgeRejection);
                        reprojectionValidity *= step(0.0001, previousClipW);
                    }
                }

                float2 safeHistoryUv = saturate(historyUv);
                float4 rawHistory = tex2D(_HistoryTexture, safeHistoryUv);
                float4 history = rawHistory;

                float rawDifferenceMagnitude = length(currentFrame.rgb - rawHistory.rgb);
                float thresholdedRawDifference = max(0.0, rawDifferenceMagnitude - _DifferenceDebugThreshold);

                // In this sandbox, "history confidence" means how comfortable the filter is
                // with letting old history influence the final result. Large disagreement lowers
                // that confidence and raises rejection, which helps suppress stale history.
                float rejectionAmount = saturate(thresholdedRawDifference / max(_HistoryClampAmount, 0.0001));
                float historyConfidence = (_DebugEffectiveHistoryWeight * reprojectionValidity) * (1.0 - rejectionAmount);
                float historyTrustMask = reprojectionValidity * (1.0 - rejectionAmount);

                // History clamping limits how far old history values are allowed to drift from
                // the current frame. Ghosting often comes from stale history lingering too long,
                // so clamping helps even when reprojection is still imperfect.
                if (_HistoryClampingEnabled > 0.5)
                {
                    float4 minAllowed = currentFrame - _HistoryClampAmount;
                    float4 maxAllowed = currentFrame + _HistoryClampAmount;
                    history = clamp(history, minAllowed, maxAllowed);
                }

                float finalHistoryWeight = _HistoryWeight * reprojectionValidity;
                float finalHistoryTrust = _HistoryWeight > 0.0001 ? finalHistoryWeight / _HistoryWeight : 0.0;
                float4 finalOutput = lerp(currentFrame, history, finalHistoryWeight);

                // Debug visualization is useful because temporal effects can be subtle. These
                // views expose the internal signals so it is easier to see what the sandbox is
                // actually blending and why the final image behaves the way it does.
                if (_DebugVisualizationMode < 0.5)
                {
                    return finalOutput;
                }

                if (_DebugVisualizationMode < 1.5)
                {
                    return currentFrame;
                }

                if (_DebugVisualizationMode < 2.5)
                {
                    return history;
                }

                if (_DebugVisualizationMode < 3.5)
                {
                    // The old view still looked too uniform because too many weak differences
                    // survived into the final color. This version uses a stronger threshold and
                    // a power curve so only more meaningful disagreement stays bright.
                    float differenceMagnitude = rawDifferenceMagnitude;
                    float thresholdedDifference = thresholdedRawDifference;
                    float visibleDifference = saturate(thresholdedDifference * _DifferenceDebugScale);
                    float contrastedDifference = pow(visibleDifference, _DifferenceDebugExponent);

                    // Black means current and history largely agree. Bright grayscale means
                    // strong disagreement, which makes moving edges and stale history regions
                    // easier to isolate without tinting the whole frame.
                    return float4(contrastedDifference, contrastedDifference, contrastedDifference, 1.0);
                }

                if (_DebugVisualizationMode < 4.5)
                {
                    return float4(_DebugEffectiveHistoryWeight, _DebugEffectiveHistoryWeight, _DebugEffectiveHistoryWeight, 1.0);
                }

                if (_DebugVisualizationMode < 5.5)
                {
                    return float4(historyConfidence, historyConfidence, historyConfidence, 1.0);
                }

                if (_DebugVisualizationMode < 6.5)
                {
                    return float4(rejectionAmount, rejectionAmount, rejectionAmount, 1.0);
                }

                // Geometric reprojection validity can legitimately stay near white across stable
                // pixels because it only answers "did the reprojected sample land somewhere
                // plausible?" The more informative trust views also fold in rejection logic.
                if (_DebugVisualizationMode < 7.5)
                {
                    return float4(finalHistoryTrust, finalHistoryTrust, finalHistoryTrust, 1.0);
                }

                // This view shows rejection-aware history trust without the global temporal
                // weight. It is often easier to read than the geometric validity mask alone.
                if (_DebugVisualizationMode < 8.5)
                {
                    return float4(historyTrustMask, historyTrustMask, historyTrustMask, 1.0);
                }

                // Motion vectors are stored in screen-space UV units. This view shows magnitude
                // only so it is easy to compare "how much this pixel moved" against the other
                // reprojection debug modes without needing to decode vector direction first.
                float motionVectorMagnitudeDebug = saturate(motionVectorMagnitude * 32.0);
                return float4(motionVectorMagnitudeDebug, motionVectorMagnitudeDebug, motionVectorMagnitudeDebug, 1.0);
            }
            ENDHLSL
        }
    }
}
