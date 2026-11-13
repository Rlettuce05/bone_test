using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Dynamics;

// Must be attached to the EXACT same GameObject as the VRC Contact Receiver.
// In the receiver Inspector: Content Types = Avatar, Collision Tags = Head.
public class MouthContactDetector : UdonSharpBehaviour
{
    public MouthPickupController controller;

    public override void OnContactEnter(ContactEnterInfo info)
    {
        if (controller == null || !info.contactSender.isValid) return;
        VRCPlayerApi player = info.contactSender.player;
        if (Utilities.IsValid(player) && player.isLocal)
            controller.localHeadContact = true;
    }

    public override void OnContactExit(ContactExitInfo info)
    {
        if (controller == null || !info.contactSender.isValid) return;
        VRCPlayerApi player = info.contactSender.player;
        if (Utilities.IsValid(player) && player.isLocal)
            controller.localHeadContact = false;
    }
}
