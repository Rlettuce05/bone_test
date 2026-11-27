using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MouthContactDetector : UdonSharpBehaviour
{
    [Header("Scene references (one Receiver on this GameObject)")]
    public VRCContactReceiver contactReceiver;
    public MouthRegionDetector mouthRegionDetector;

    [Header("Local debug display (optional World Space UI Text)")]
    public Text debugStatusText;

    [Header("Runtime outputs (do not edit)")]
    public bool isReceiverReady;
    public bool hasLocalHeadContact;
    public int validContactCount;
    public bool hasRegionResult;
    public bool isBitePointInside;
    public bool isAcquisitionCandidate;
    public int candidateFrame = -1;
    public int contactEnterEventCount;
    public int contactExitEventCount;

    // playerIdはSenderの識別子ではない。同じプレイヤーの複数Senderを別々に保持する。
    private ContactSenderProxy[] activeSenders = new ContactSenderProxy[8];
    private int senderCount;
    private VRCContactReceiver observedReceiver;
    private int avatarChangedFrame = -1;

    private void Start()
    {
        if (!Utilities.IsValid(contactReceiver))
            contactReceiver = GetComponent<VRCContactReceiver>();
    }

    public override void OnContactEnter(ContactEnterInfo contactInfo)
    {
        contactEnterEventCount++;
        InvalidateCandidate();
        if (!PrepareReceiver()) return;
        PruneInvalidSenders();

        ContactReceiverProxy receiver = contactInfo.contactReceiver;
        if (receiver == null || !receiver.isValid || receiver != contactReceiver) return;
        if (Time.frameCount == avatarChangedFrame) return;

        ContactSenderProxy sender = contactInfo.contactSender;
        if (!IsLocalAvatarSender(sender) || !ContainsHeadTag(contactInfo.matchingTags)) return;
        for (int i = 0; i < senderCount; i++)
        {
            if (activeSenders[i] == sender) return;
        }

        if (senderCount == activeSenders.Length)
        {
            ContactSenderProxy[] expanded = new ContactSenderProxy[activeSenders.Length * 2];
            for (int i = 0; i < senderCount; i++) expanded[i] = activeSenders[i];
            activeSenders = expanded;
        }
        activeSenders[senderCount] = sender;
        senderCount++;
        PublishContactState();
    }

    public override void OnContactExit(ContactExitInfo contactInfo)
    {
        contactExitEventCount++;
        InvalidateCandidate();
        if (!PrepareReceiver()) return;
        // 破棄済みSenderもここで除去。Exit時のplayer/tagを再審査して残留させない。
        PruneInvalidSenders();
        ContactReceiverProxy receiver = contactInfo.contactReceiver;
        if (receiver == null || !receiver.isValid || receiver != contactReceiver) return;
        ContactSenderProxy sender = contactInfo.contactSender;
        if (sender == null || !sender.isValid) return;
        for (int i = senderCount - 1; i >= 0; i--)
        {
            if (activeSenders[i] == sender) RemoveSenderAt(i);
        }
        PublishContactState();
    }

    public override void PostLateUpdate()
    {
        RefreshCandidate();
    }

    // 頭部姿勢更新後に使う。Contactイベント内では早い姿勢をキャッシュしない。
    public void RefreshCandidate()
    {
        InvalidateCandidate();
        hasRegionResult = false;
        isBitePointInside = false;
        bool receiverReady = PrepareReceiver();
        if (receiverReady) PruneInvalidSenders();

        if (enabled && gameObject.activeInHierarchy &&
            Utilities.IsValid(mouthRegionDetector) && mouthRegionDetector.enabled &&
            mouthRegionDetector.gameObject.activeInHierarchy)
        {
            mouthRegionDetector.RefreshRegion();
            hasRegionResult = mouthRegionDetector.hasRegionResult &&
                mouthRegionDetector.regionResultFrame == Time.frameCount;
            isBitePointInside = hasRegionResult && mouthRegionDetector.isBitePointInside;
        }

        if (receiverReady && hasRegionResult && Time.frameCount != avatarChangedFrame)
        {
            isAcquisitionCandidate = hasLocalHeadContact && isBitePointInside;
            candidateFrame = Time.frameCount;
        }
        UpdateDebugStatus();
    }

    private bool PrepareReceiver()
    {
        if (observedReceiver != contactReceiver)
        {
            ClearContacts();
            observedReceiver = contactReceiver;
        }
        isReceiverReady = enabled && gameObject.activeInHierarchy &&
            Utilities.IsValid(contactReceiver) && contactReceiver.enabled &&
            contactReceiver.gameObject == gameObject &&
            contactReceiver.gameObject.activeInHierarchy;
        if (!isReceiverReady) ClearContacts();
        return isReceiverReady;
    }

    private bool IsLocalAvatarSender(ContactSenderProxy sender)
    {
        if (sender == null || !sender.isValid || sender.usage != DynamicsUsage.Avatar)
            return false;
        VRCPlayerApi player = sender.player;
        return Utilities.IsValid(player) && player.isLocal;
    }

    private bool ContainsHeadTag(string[] tags)
    {
        if (tags == null) return false;
        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i] == "Head") return true;
        }
        return false;
    }

    private void PruneInvalidSenders()
    {
        for (int i = senderCount - 1; i >= 0; i--)
        {
            // isValidは存在確認のみ。Sender無効化の通知はSDKのExitを使う。
            if (!IsLocalAvatarSender(activeSenders[i])) RemoveSenderAt(i);
        }
        PublishContactState();
    }

    private void RemoveSenderAt(int index)
    {
        senderCount--;
        activeSenders[index] = activeSenders[senderCount];
        activeSenders[senderCount] = null;
    }

    private void PublishContactState()
    {
        validContactCount = senderCount;
        hasLocalHeadContact = senderCount > 0;
    }

    private void InvalidateCandidate()
    {
        isAcquisitionCandidate = false;
        candidateFrame = -1;
    }

    private void ClearContacts()
    {
        for (int i = 0; i < senderCount; i++) activeSenders[i] = null;
        senderCount = 0;
        PublishContactState();
        InvalidateCandidate();
    }

    // 再登録には新しいEnterが必要。連続接触を推測で復活させない。
    public void ResetContacts()
    {
        ClearContacts();
        hasRegionResult = false;
        isBitePointInside = false;
        UpdateDebugStatus();
    }

    public override void OnAvatarChanged(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        avatarChangedFrame = Time.frameCount;
        ResetContacts();
    }

    public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float prevEyeHeightAsMeters)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        // 接触集合はSDKのEnter/Exitで更新。領域は次のPostLateUpdateで再計算する。
        hasRegionResult = false;
        isBitePointInside = false;
        InvalidateCandidate();
        UpdateDebugStatus();
    }

    private void OnEnable()
    {
        isReceiverReady = false;
        ResetContacts();
    }

    private void OnDisable()
    {
        isReceiverReady = false;
        ResetContacts();
    }

    private void UpdateDebugStatus()
    {
        if (!Utilities.IsValid(debugStatusText)) return;
        debugStatusText.text = "Receiver ready: " + isReceiverReady +
            "\nLocal Head contact: " + hasLocalHeadContact + " (" + validContactCount + ")" +
            "\nRegion valid / inside: " + hasRegionResult + " / " + isBitePointInside +
            "\nAcquisition candidate: " + isAcquisitionCandidate +
            "\nContact events enter / exit: " + contactEnterEventCount + " / " + contactExitEventCount;
    }
}
