using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MouthPositionEstimator : UdonSharpBehaviour
{
    [Header("Mouth estimate (head-local meters)")]
    public Vector3 baseMouthOffset = new Vector3(0f, -0.10f, 0.08f);
    public Vector3 referenceHeadBoneOffset = new Vector3(0f, -0.08f, -0.04f);
    [Min(0.01f)] public float referenceEyeHeight = 1.6f;
    [Range(0f, 2f)] public float alpha = 1f;
    [Range(-2f, 2f)] public float beta = 0f;

    [Header("Local debug display (separate object, no collider or sync)")]
    public Transform debugMouthMarker;
    public bool showDebugMarker = true;

    // ローカル専用の出力。有効性を確認してから参照する。
    [HideInInspector] public bool hasMouthPose;
    [HideInInspector] public Vector3 mouthPosition;
    [HideInInspector] public Quaternion mouthRotation = Quaternion.identity;
    [HideInInspector] public float avatarScale = 1f;
    [HideInInspector] public bool headBoneCorrectionApplied;
    [HideInInspector] public int mouthPoseFrame = -1;

    private VRCPlayerApi localPlayer;

    private void Start()
    {
        localPlayer = Networking.LocalPlayer;
        InvalidateEstimate();
    }

    public override void PostLateUpdate()
    {
        RefreshMouthPose();
    }

    // PostLateUpdate内の利用者からも呼ぶ。同じフレームでは推定結果を共有する。
    public void RefreshMouthPose()
    {
        if (!enabled || !gameObject.activeInHierarchy)
        {
            InvalidateEstimate();
            return;
        }
        if (hasMouthPose && mouthPoseFrame == Time.frameCount) return;

        // ClientSimなどでプレイヤーの初期化が遅れた場合にも再取得する。
        if (!Utilities.IsValid(localPlayer))
            localPlayer = Networking.LocalPlayer;

        if (!Utilities.IsValid(localPlayer) || !localPlayer.isLocal)
        {
            InvalidateEstimate();
            return;
        }

        VRCPlayerApi.TrackingData head = localPlayer.GetTrackingData(
            VRCPlayerApi.TrackingDataType.Head);
        float eyeHeight = localPlayer.GetAvatarEyeHeightAsMeters();
        avatarScale = eyeHeight > 0f && referenceEyeHeight > 0f
            ? eyeHeight / referenceEyeHeight : 1f;

        // 設計書の式(1b)。補正係数は制作者が設定する共通値。
        Vector3 mouthOffset = (1f + alpha * (avatarScale - 1f)) * baseMouthOffset;
        headBoneCorrectionApplied = false;
        if (beta != 0f)
        {
            Vector3 headBonePosition = localPlayer.GetBonePosition(HumanBodyBones.Head);
            // SDKはボーンが存在しない場合にVector3.zeroを返す。
            if (headBonePosition != Vector3.zero)
            {
                Vector3 headLocalBoneOffset = Quaternion.Inverse(head.rotation) *
                    (headBonePosition - head.position);
                mouthOffset += beta *
                    (headLocalBoneOffset - avatarScale * referenceHeadBoneOffset);
                headBoneCorrectionApplied = true;
            }
        }

        // 設計書の式(1a)。回転はHead Trackingの回転をそのまま使う。
        mouthPosition = head.position + head.rotation * mouthOffset;
        mouthRotation = head.rotation;
        mouthPoseFrame = Time.frameCount;
        hasMouthPose = true;
        UpdateDebugMarker();
    }

    public override void OnAvatarChanged(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        localPlayer = player;
        InvalidateEstimate();
    }

    public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float prevEyeHeightAsMeters)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        localPlayer = player;
        InvalidateEstimate();
    }

    private void OnDisable()
    {
        InvalidateEstimate();
    }

    private void InvalidateEstimate()
    {
        hasMouthPose = false;
        mouthPoseFrame = -1;
        mouthPosition = Vector3.zero;
        mouthRotation = Quaternion.identity;
        avatarScale = 1f;
        headBoneCorrectionApplied = false;
        UpdateDebugMarker();
    }

    private void UpdateDebugMarker()
    {
        if (!Utilities.IsValid(debugMouthMarker)) return;
        // 誤参照によって推定器自身やその親を移動・無効化しない。
        if (debugMouthMarker == transform || transform.IsChildOf(debugMouthMarker)) return;

        bool visible = showDebugMarker && hasMouthPose;
        if (visible)
            debugMouthMarker.SetPositionAndRotation(mouthPosition, mouthRotation);
        if (debugMouthMarker.gameObject.activeSelf != visible)
            debugMouthMarker.gameObject.SetActive(visible);
    }
}
