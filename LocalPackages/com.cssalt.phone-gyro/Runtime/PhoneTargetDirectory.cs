using System.Collections.Generic;
using System.Text;
using UnityEngine;

/*
================================================================================
 PhoneTargetDirectory
================================================================================

 Advertises the scene's controllable objects to the phone, so the phone can
 show a picker instead of a hardcoded probe/needle toggle.

 HOW IT FITS
 -----------
 Put this on the same host GameObject as the shared PhoneRelayClient (the one
 that also carries the PhoneGyroController components). When the phone sends
 `{ "type": "get-targets" }`, this component replies with the list of active
 controllers by TargetName (+ optional DisplayName):

   { "type": "targets",
     "targets": [ { "name": "probe",  "label": "Ultrasound Probe" },
                  { "name": "needle", "label": "Needle" } ] }

 The relay is a blind room broadcast, so the request arrives via the shared
 PhoneRelayClient.MessageReceived and the reply goes back over
 PhoneRelayClient.Send — no server change needed.

 The phone treats this as a progressive enhancement: its baseline is an
 empty-target broadcast ("control anything"), and the picker only appears if
 this directory answers. So a scene without this component (or an older build)
 still works — the phone just drives whatever controllers exist.
================================================================================
*/

[RequireComponent(typeof(PhoneRelayClient))]
public class PhoneTargetDirectory : MonoBehaviour
{
    [Tooltip("Override the shared relay. Leave null to auto-use GetComponent<PhoneRelayClient>() on this GameObject.")]
    public PhoneRelayClient Relay;

    // Minimal parse just to detect the request type.
    [System.Serializable] class Msg { public string type; }

    // Concrete shapes for the reply — JsonUtility needs serializable types.
    [System.Serializable] class TargetEntry { public string name; public string label; }
    [System.Serializable] class TargetsReply { public string type = "targets"; public List<TargetEntry> targets = new List<TargetEntry>(); }

    void Awake()
    {
        if (Relay == null) Relay = GetComponent<PhoneRelayClient>();
    }

    void OnEnable()
    {
        if (Relay != null) Relay.MessageReceived += OnMessage;
    }

    void OnDisable()
    {
        if (Relay != null) Relay.MessageReceived -= OnMessage;
    }

    void OnMessage(byte[] bytes)
    {
        Msg m;
        try { m = JsonUtility.FromJson<Msg>(Encoding.UTF8.GetString(bytes)); }
        catch { return; }
        if (m == null || m.type != "get-targets") return;
        SendTargets();
    }

    // Gather the active PhoneGyroControllers and reply with their names/labels.
    // FindObjects... excludes inactive objects, so only currently-controllable
    // targets are advertised. Registration-free — the directory reflects the
    // live scene each time it's asked.
    void SendTargets()
    {
        if (Relay == null || !Relay.IsConnected) return;

#if UNITY_2022_2_OR_NEWER
        var controllers = FindObjectsByType<PhoneGyroController>(FindObjectsSortMode.None);
#else
        var controllers = FindObjectsOfType<PhoneGyroController>();
#endif

        var reply = new TargetsReply();
        var seen = new HashSet<string>();
        foreach (var c in controllers)
        {
            string name = c.TargetName ?? "";
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue; // skip blank / duplicates
            string label = string.IsNullOrEmpty(c.DisplayName) ? name : c.DisplayName;
            reply.targets.Add(new TargetEntry { name = name, label = label });
        }

        Relay.Send(JsonUtility.ToJson(reply));
    }
}
