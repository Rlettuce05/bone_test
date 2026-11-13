using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

// Place this on Bone, together with Rigidbody, Collider, VRCPickup and VRCObjectSync.
// BitePoint must be a DIRECT child of Bone. Use scale (1,1,1) on Bone and its ancestors.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class MouthPickupController : UdonSharpBehaviour
{
    private const int FREE = 0;
    private const int HAND = 1;
    private const int MOUTH = 2;

    [Header("Scene references")]
    public Transform bitePoint;
    public VRCPickup pickup;
    public VRCObjectSync objectSync;
    public Rigidbody boneRigidbody;
    public Transform debugMouthMarker; // Optional: an unnetworked sphere outside Bone
    public Transform debugEllipsoid;   // Optional: an unnetworked translucent sphere

    [Header("Mouth estimate (head-local meters)")]
    public Vector3 baseMouthOffset = new Vector3(0f, -0.10f, 0.08f);
    public Vector3 referenceHeadBoneOffset = new Vector3(0f, -0.08f, -0.04f);
    public float referenceEyeHeight = 1.6f;
    [Range(0f, 2f)] public float alpha = 1f;
    [Range(-2f, 2f)] public float beta = 0f;

    [Header("Ellipsoid (head-local meters)")]
    public Vector3 baseRadii = new Vector3(0.08f, 0.07f, 0.12f);
    public Vector3 radiusScaleCoefficients = Vector3.one;
    [Range(1f, 89f)] public float maxApproachAngleDegrees = 55f;
    public float rearTolerance = 0.02f;

    [Header("Motion filtering")]
    [Range(0.01f, 1f)] public float velocitySmoothing = 0.35f;
    public float approachMemorySeconds = 0.10f;
    public float dropCooldownSeconds = 0.4f;

    [Header("Mouth hold rotation (relative to head)")]
    public Vector3 gripEulerAngles = Vector3.zero;

    // Updated only by the local head Contact Detector; never network-sync this value.
    [HideInInspector] public bool localHeadContact;

    [UdonSynced] private int syncedMode = FREE;
    [UdonSynced] private int syncedHolderId = -1;

    private VRCPlayerApi localPlayer;
    private Vector3 mouthPosition;
    private Quaternion headRotation;
    private Vector3 headForward;
    private Vector3 ellipsoidRadii;
    private Vector3 previousMouth;
    private Vector3 previousBite;
    private Vector3 smoothedRelativeVelocity;
    private bool motionInitialized;
    private float lastApproachTime = -1000f;
    private float cooldownUntil;
    private float mouthAcquiredAt;
    private bool initialKinematic;
    private bool initialGravity;

    private void Start()
    {
        localPlayer = Networking.LocalPlayer;
        if (boneRigidbody != null)
        {
            initialKinematic = boneRigidbody.isKinematic;
            initialGravity = boneRigidbody.useGravity;
        }
        cooldownUntil = Time.time + 0.2f;
    }

    public override void PostLateUpdate()
    {
        if (!Utilities.IsValid(localPlayer) || bitePoint == null || pickup == null || objectSync == null)
            return;

        ComputeMouthEstimate();
        if (debugMouthMarker != null)
        {
            debugMouthMarker.position = mouthPosition;
            debugMouthMarker.rotation = headRotation;
        }
        if (debugEllipsoid != null)
        {
            debugEllipsoid.position = mouthPosition;
            debugEllipsoid.rotation = headRotation;
            debugEllipsoid.localScale = 2f * ellipsoidRadii;
        }

        // Velocity of BitePoint relative to mouth; both are world-space positions.
        Vector3 bitePosition = bitePoint.position;
        float dt = Time.deltaTime;
        if (motionInitialized && dt > 0.00001f)
        {
            Vector3 relativeVelocity = ((bitePosition - previousBite) -
                                        (mouthPosition - previousMouth)) / dt;
            float eta = Mathf.Clamp01(velocitySmoothing);
            smoothedRelativeVelocity = Vector3.Lerp(
                smoothedRelativeVelocity, relativeVelocity, eta);

            float towardFace = -Vector3.Dot(smoothedRelativeVelocity, headForward);
            Vector3 sideways = smoothedRelativeVelocity + towardFace * headForward;
            float tanAngle = Mathf.Tan(maxApproachAngleDegrees * Mathf.Deg2Rad);
            if (towardFace > 0f &&
                sideways.sqrMagnitude <= towardFace * towardFace * tanAngle * tanAngle)
            {
                lastApproachTime = Time.time;
            }
        }
        else
        {
            motionInitialized = true;
        }
        previousMouth = mouthPosition;
        previousBite = bitePosition;

        if (syncedMode == MOUTH)
        {
            if (Networking.IsOwner(gameObject) &&
                syncedHolderId == localPlayer.playerId && !pickup.IsHeld)
                FollowMouth();
            return;
        }

        if (syncedMode != FREE || pickup.IsHeld || Time.time < cooldownUntil ||
            !localHeadContact || Time.time - lastApproachTime > approachMemorySeconds)
            return;

        Vector3 u = Quaternion.Inverse(headRotation) * (bitePosition - mouthPosition);
        float d = (u.x * u.x) / (ellipsoidRadii.x * ellipsoidRadii.x) +
                  (u.y * u.y) / (ellipsoidRadii.y * ellipsoidRadii.y) +
                  (u.z * u.z) / (ellipsoidRadii.z * ellipsoidRadii.z);
        if (d > 1f || Vector3.Dot(bitePosition - mouthPosition, headForward) < -rearTolerance)
            return;

        // Only a player with a currently valid local candidate attempts ownership.
        if (!Networking.IsOwner(gameObject))
            Networking.SetOwner(localPlayer, gameObject);
        if (!Networking.IsOwner(gameObject) || syncedMode != FREE || pickup.IsHeld)
            return;

        syncedMode = MOUTH;
        syncedHolderId = localPlayer.playerId;
        mouthAcquiredAt = Time.time;
        objectSync.SetKinematic(true);
        objectSync.SetGravity(false);
        objectSync.FlagDiscontinuity();
        FollowMouth();
        RequestSerialization();
    }

    private void ComputeMouthEstimate()
    {
        VRCPlayerApi.TrackingData head = localPlayer.GetTrackingData(
            VRCPlayerApi.TrackingDataType.Head);
        headRotation = head.rotation;
        headForward = headRotation * Vector3.forward;

        float eyeHeight = localPlayer.GetAvatarEyeHeightAsMeters();
        float s = eyeHeight > 0f && referenceEyeHeight > 0f
            ? eyeHeight / referenceEyeHeight : 1f;
        Vector3 offset = (1f + alpha * (s - 1f)) * baseMouthOffset;

        Vector3 headBonePosition = localPlayer.GetBonePosition(HumanBodyBones.Head);
        if (beta != 0f && headBonePosition != Vector3.zero)
        {
            Vector3 localBone = Quaternion.Inverse(headRotation) *
                                (headBonePosition - head.position);
            offset += beta * (localBone - s * referenceHeadBoneOffset);
        }
        mouthPosition = head.position + headRotation * offset;

        ellipsoidRadii.x = Mathf.Max(0.005f,
            baseRadii.x * (1f + radiusScaleCoefficients.x * (s - 1f)));
        ellipsoidRadii.y = Mathf.Max(0.005f,
            baseRadii.y * (1f + radiusScaleCoefficients.y * (s - 1f)));
        ellipsoidRadii.z = Mathf.Max(0.005f,
            baseRadii.z * (1f + radiusScaleCoefficients.z * (s - 1f)));
    }

    private void FollowMouth()
    {
        // Bone and ancestors must have scale (1,1,1). BitePoint is a direct child.
        Quaternion rootRotation = headRotation * Quaternion.Euler(gripEulerAngles) *
                                  Quaternion.Inverse(bitePoint.localRotation);
        Vector3 rootPosition = mouthPosition - rootRotation * bitePoint.localPosition;
        transform.SetPositionAndRotation(rootPosition, rootRotation);
    }

    public override void InputUse(bool value, UdonInputEventArgs args)
    {
        if (!value || !Utilities.IsValid(localPlayer) || syncedMode != MOUTH ||
            syncedHolderId != localPlayer.playerId || !Networking.IsOwner(gameObject) ||
            Time.time - mouthAcquiredAt < 0.3f)
            return;
        ReleaseMouth();
    }

    private void ReleaseMouth()
    {
        if (!Networking.IsOwner(gameObject)) return;
        syncedMode = FREE;
        syncedHolderId = -1;
        localHeadContact = false;
        cooldownUntil = Time.time + dropCooldownSeconds;
        lastApproachTime = -1000f;
        smoothedRelativeVelocity = Vector3.zero;
        motionInitialized = false;
        RestorePhysics();
        RequestSerialization();
    }

    private void RestorePhysics()
    {
        objectSync.SetKinematic(initialKinematic);
        objectSync.SetGravity(initialGravity);
        if (boneRigidbody != null && !initialKinematic)
        {
            boneRigidbody.velocity = Vector3.zero;
            boneRigidbody.angularVelocity = Vector3.zero;
        }
    }

    public override void OnPickup()
    {
        localHeadContact = false;
        cooldownUntil = Time.time + dropCooldownSeconds;
        if (!Networking.IsOwner(gameObject)) return;
        if (syncedMode == MOUTH) RestorePhysics();
        syncedMode = HAND;
        syncedHolderId = Networking.LocalPlayer.playerId;
        RequestSerialization();
    }

    public override void OnDrop()
    {
        if (!Networking.IsOwner(gameObject)) return;
        syncedMode = FREE;
        syncedHolderId = -1;
        localHeadContact = false;
        cooldownUntil = Time.time + dropCooldownSeconds;
        lastApproachTime = -1000f;
        RestorePhysics();
        RequestSerialization();
    }

    public override void OnOwnershipTransferred(VRCPlayerApi newOwner)
    {
        if (!Utilities.IsValid(localPlayer) || !Networking.IsOwner(gameObject)) return;

        // Receiving a pickup from another owner wins over previous mouth control.
        if (pickup.IsHeld && Utilities.IsValid(pickup.currentPlayer) &&
            pickup.currentPlayer.isLocal)
        {
            syncedMode = HAND;
            syncedHolderId = localPlayer.playerId;
            RequestSerialization();
            return;
        }

        // If the old mouth holder left, or the object was taken away, release stale state.
        if ((syncedMode == MOUTH && syncedHolderId != localPlayer.playerId) ||
            (syncedMode == HAND && !pickup.IsHeld))
        {
            ReleaseMouth();
        }
    }

    public override void OnAvatarChanged(VRCPlayerApi player)
    {
        if (player == null || !player.isLocal) return;
        localHeadContact = false;
        motionInitialized = false;
        lastApproachTime = -1000f;
        if (Networking.IsOwner(gameObject) && syncedMode == MOUTH &&
            syncedHolderId == player.playerId)
            ReleaseMouth();
    }

    public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float previousEyeHeight)
    {
        if (player == null || !player.isLocal) return;
        motionInitialized = false;
        lastApproachTime = -1000f;
    }
}
