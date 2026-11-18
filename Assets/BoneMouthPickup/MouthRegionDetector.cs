using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MouthRegionDetector : UdonSharpBehaviour
{
    private const float MinimumRadius = 0.005f;

    [Header("Scene references")]
    public MouthPositionEstimator mouthEstimator;
    public Transform bitePoint;

    [Header("Ellipsoid (head-local meters)")]
    public Vector3 baseRadii = new Vector3(0.08f, 0.07f, 0.12f);
    public Vector3 radiusScaleCoefficients = Vector3.one;

    [Header("Local debug display (root Sphere, no collider or sync)")]
    public Transform debugEllipsoid;
    public bool showDebugEllipsoid = true;

    // ローカル専用。無効時はfalse / +Infinity。取得の可否そのものではない。
    [Header("Runtime outputs (do not edit)")]
    public bool hasRegionResult;
    public bool isBitePointInside;
    public float regionValueD = float.PositiveInfinity;
    public Vector3 bitePointHeadLocal;
    public Vector3 ellipsoidRadii = new Vector3(
        MinimumRadius, MinimumRadius, MinimumRadius);

    public override void PostLateUpdate()
    {
        if (!enabled || !gameObject.activeInHierarchy ||
            !Utilities.IsValid(mouthEstimator) || !Utilities.IsValid(bitePoint) ||
            !mouthEstimator.enabled || !mouthEstimator.gameObject.activeInHierarchy)
        {
            InvalidateRegion();
            return;
        }

        // 推定器とのイベント順序に依存せず、このフレームの結果を取得する。
        mouthEstimator.RefreshMouthPose();
        if (!mouthEstimator.hasMouthPose || mouthEstimator.mouthPoseFrame != Time.frameCount)
        {
            InvalidateRegion();
            return;
        }

        float avatarScale = mouthEstimator.avatarScale;
        if (float.IsNaN(avatarScale) || float.IsInfinity(avatarScale) || avatarScale <= 0f)
        {
            InvalidateRegion();
            return;
        }

        ellipsoidRadii = new Vector3(
            CalculateRadius(baseRadii.x, radiusScaleCoefficients.x, avatarScale),
            CalculateRadius(baseRadii.y, radiusScaleCoefficients.y, avatarScale),
            CalculateRadius(baseRadii.z, radiusScaleCoefficients.z, avatarScale));

        // 設計書2.3節。原点は推定口位置、各軸はHead Trackingの回転。
        bitePointHeadLocal = Quaternion.Inverse(mouthEstimator.mouthRotation) *
            (bitePoint.position - mouthEstimator.mouthPosition);
        float normalizedX = bitePointHeadLocal.x / ellipsoidRadii.x;
        float normalizedY = bitePointHeadLocal.y / ellipsoidRadii.y;
        float normalizedZ = bitePointHeadLocal.z / ellipsoidRadii.z;
        regionValueD = normalizedX * normalizedX + normalizedY * normalizedY +
            normalizedZ * normalizedZ;

        if (float.IsNaN(regionValueD) || float.IsInfinity(regionValueD))
        {
            InvalidateRegion();
            return;
        }

        hasRegionResult = true;
        isBitePointInside = regionValueD <= 1f;
        UpdateDebugEllipsoid();
    }

    private float CalculateRadius(float baseRadius, float scaleCoefficient, float avatarScale)
    {
        // 設計書・課題2。入力の符号や補正係数によらず実効半径を正に保つ。
        float radius = baseRadius * (1f + scaleCoefficient * (avatarScale - 1f));
        if (float.IsNaN(radius) || float.IsInfinity(radius)) return MinimumRadius;
        return Mathf.Max(MinimumRadius, radius);
    }

    private void OnDisable()
    {
        InvalidateRegion();
    }

    private void InvalidateRegion()
    {
        hasRegionResult = false;
        isBitePointInside = false;
        regionValueD = float.PositiveInfinity;
        bitePointHeadLocal = Vector3.zero;
        UpdateDebugEllipsoid();
    }

    private void UpdateDebugEllipsoid()
    {
        if (!Utilities.IsValid(debugEllipsoid)) return;
        // 親の非一様スケール・回転による歪みを避け、入力オブジェクトを動かさない。
        if (debugEllipsoid.parent != null || transform.IsChildOf(debugEllipsoid)) return;
        if (Utilities.IsValid(bitePoint) && bitePoint.IsChildOf(debugEllipsoid)) return;
        if (Utilities.IsValid(mouthEstimator) &&
            (mouthEstimator.transform.IsChildOf(debugEllipsoid) ||
             debugEllipsoid == mouthEstimator.debugMouthMarker)) return;

        bool visible = showDebugEllipsoid && hasRegionResult;
        if (visible)
        {
            debugEllipsoid.SetPositionAndRotation(
                mouthEstimator.mouthPosition, mouthEstimator.mouthRotation);
            // Unity標準Sphereの直径1を、各軸の直径2rへ拡縮する。
            debugEllipsoid.localScale = 2f * ellipsoidRadii;
        }
        if (debugEllipsoid.gameObject.activeSelf != visible)
            debugEllipsoid.gameObject.SetActive(visible);
    }
}
