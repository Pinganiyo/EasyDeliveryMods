using System.Collections.Generic;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Throwing a delivery to your friend. While you carry a package on foot, hold the headlights button (the same
/// key that switches the truck lights) to wind up and let go to throw: the longer you hold, the harder it goes.
/// The package flies toward your friend in an arc; a hard throw goes far, a soft one lands short. If it reaches
/// a friend who is on foot with empty hands they catch it, otherwise it lands and can be picked up as usual.
/// </summary>
internal static class PackageThrow
{
    private const float MaxCharge = 1.5f;     // seconds of holding for a full-strength throw
    private const float MinCharge = 0.15f;    // shorter than this is a tap and does nothing
    private const float MinSpeed = 5f;        // m/s at the lightest throw (about 3 m)
    private const float MaxSpeed = 26f;       // m/s at full strength (about 65 m)
    private const float AngleDegrees = 40f;
    private const float CatchRadius = 1.5f;
    private const float Gravity = 9.81f;

    private sealed class Charger
    {
        public bool Charging;
        public float Time;
    }

    private sealed class Flight
    {
        public Transform Package = null!;
        public Vector3 Velocity;
        public Vector3 Spin;
        public int Thrower;
        public float Age;
    }

    private static readonly Charger[] Chargers = { new Charger(), new Charger() }; // [0] = P1, [1] = P2
    private static readonly List<Flight> Flights = new();
    private static readonly Transform?[] LastThrown = new Transform?[2];
    private static readonly float[] HintUntil = new float[2];

    internal static float Charge01(int player)
    {
        var c = Chargers[player - 1];
        return c.Charging ? Mathf.Clamp01(c.Time / MaxCharge) : 0f;
    }

    internal static void Reset()
    {
        foreach (var c in Chargers) { c.Charging = false; c.Time = 0f; }
        Flights.Clear();
        LastThrown[0] = LastThrown[1] = null;
        HintUntil[0] = HintUntil[1] = 0f;
    }

    internal static void Tick(float dt)
    {
        TickPlayer(1, dt);
        TickPlayer(2, dt);
        TickFlights(dt);
    }

    // ── Winding up and throwing ─────────────────────────────────────────────

    private static bool Carrying(sCharacterInteraction? guy) =>
        guy != null && guy.packagePlatform != null && guy.packagePlatform.activeSelf &&
        guy.packagePoint != null && guy.packagePoint.childCount > 0;

    private static void TickPlayer(int player, float dt)
    {
        var guy = SplitscreenManager.GuyOf(player);
        var charger = Chargers[player - 1];

        // Once anyone has picked the thrown package up (or it is gone, or stowed in the truck) it is no
        // longer a throw gone wrong, so it can't be called back any more.
        var tracked = LastThrown[player - 1];
        if (tracked != null && (!tracked.gameObject.activeInHierarchy || IsCarried(tracked) || tracked.name != "Payload"))
        {
            LastThrown[player - 1] = null;
            HintUntil[player - 1] = 0f;
        }

        // A package YOU threw that ended up somewhere unreachable (a roof, a cliff...): the camera button brings
        // it back to your hands. Nothing else can be called back, and only by the one who threw it.
        if (guy != null && LastThrown[player - 1] != null && !Carrying(guy) && SplitscreenManager.OnFoot(player) &&
            !SplitscreenManager.IsDown(player) && !(player == 2 && P2Dialog.Active) && SplitscreenManager.RecallButton(player))
        {
            Recall(player, guy);
        }

        bool canThrow = Carrying(guy) && SplitscreenManager.OnFoot(player) && !SplitscreenManager.IsDown(player) &&
                        !(player == 2 && P2Dialog.Active);
        if (!canThrow)
        {
            charger.Charging = false;
            charger.Time = 0f;
            return;
        }

        SplitscreenManager.ThrowButton(player, out bool held);
        if (held)
        {
            charger.Charging = true;
            charger.Time += dt;
        }
        else if (charger.Charging)
        {
            // let go: throw if it was held long enough
            if (charger.Time >= MinCharge)
                Throw(player, guy!, Mathf.Clamp01(charger.Time / MaxCharge));
            charger.Charging = false;
            charger.Time = 0f;
        }
    }

    private static void Throw(int player, sCharacterInteraction guy, float strength01)
    {
        var package = guy.packagePoint.GetChild(0);
        var friend = player == 1 ? SplitscreenManager.P2CharacterObject : SplitscreenManager.P1CharacterObject;

        // toward the friend if there is one, otherwise the way the character faces (the model faces -forward)
        Vector3 dir = -guy.transform.forward;
        if (friend != null)
        {
            Vector3 toFriend = friend.transform.position - guy.transform.position;
            toFriend.y = 0f;
            if (toFriend.sqrMagnitude > 0.25f) dir = toFriend.normalized;
        }
        dir.y = 0f;
        dir.Normalize();

        float speed = Mathf.Lerp(MinSpeed, MaxSpeed, strength01);
        float angle = AngleDegrees * Mathf.Deg2Rad;
        Vector3 velocity = dir * (Mathf.Cos(angle) * speed) + Vector3.up * (Mathf.Sin(angle) * speed);

        package.SetParent(null, true);
        package.name = "Payload";

        guy.packagePlatform.SetActive(false);
        guy.gameObject.SendMessage("Package", true, SendMessageOptions.DontRequireReceiver); // the placing sound
        var cc = guy.GetComponent<sCharacterController>();
        if (cc != null && cc.animator != null) cc.animator.Play("pickup");

        LastThrown[player - 1] = package;
        HintUntil[player - 1] = Time.unscaledTime + 10f;
        Flights.Add(new Flight
        {
            Package = package,
            Velocity = velocity,
            Spin = new Vector3(UnityEngine.Random.Range(-120f, 120f), UnityEngine.Random.Range(-60f, 60f), UnityEngine.Random.Range(-120f, 120f)),
            Thrower = player,
        });
        Plugin.Log.LogInfo($"[Throw] P{player} threw the package at {strength01:P0} strength ({speed:0.#} m/s).");
    }

    // ── Calling a package back ──────────────────────────────────────────────

    private static void Recall(int player, sCharacterInteraction guy)
    {
        var package = LastThrown[player - 1];
        if (package == null) return;

        Flights.RemoveAll(f => f.Package == package);
        LastThrown[player - 1] = null;
        HintUntil[player - 1] = 0f;
        Catch(guy, package);
        Plugin.Log.LogInfo($"[Throw] P{player} called back the package they threw.");
    }

    private static bool IsCarried(Transform package)
    {
        var parent = package.parent;
        return parent != null && parent.name == "packagePoint";
    }

    // ── The package in the air ──────────────────────────────────────────────

    private static readonly int PlayersLayerMask = LayerMask.GetMask("Players");

    private static void TickFlights(float dt)
    {
        for (int i = Flights.Count - 1; i >= 0; i--)
        {
            var f = Flights[i];
            if (f.Package == null) { Flights.RemoveAt(i); continue; }

            Vector3 prev = f.Package.position;
            f.Velocity += Vector3.down * Gravity * dt;
            Vector3 next = prev + f.Velocity * dt;
            f.Age += dt;

            // caught by the friend
            int friend = f.Thrower == 1 ? 2 : 1;
            var friendGuy = SplitscreenManager.GuyOf(friend);
            var friendObj = friend == 1 ? SplitscreenManager.P1CharacterObject : SplitscreenManager.P2CharacterObject;
            if (friendGuy != null && friendObj != null && SplitscreenManager.OnFoot(friend) &&
                !SplitscreenManager.IsDown(friend) && !Carrying(friendGuy))
            {
                Vector3 chest = friendObj.transform.position + Vector3.up * 1f;
                if ((next - chest).sqrMagnitude < CatchRadius * CatchRadius)
                {
                    Catch(friendGuy, f.Package);
                    Flights.RemoveAt(i);
                    continue;
                }
            }

            // lands on whatever it hits (the players themselves are ignored)
            if (Physics.Linecast(prev, next, out var hit, ~PlayersLayerMask, QueryTriggerInteraction.Ignore))
            {
                Land(f.Package, hit.point + hit.normal * 0.05f);
                Flights.RemoveAt(i);
                continue;
            }
            if (f.Age > 8f)
            {
                Land(f.Package, next);
                Flights.RemoveAt(i);
                continue;
            }

            f.Package.position = next;
            f.Package.Rotate(f.Spin * dt, Space.World);
        }
    }

    private static void Land(Transform package, Vector3 position)
    {
        package.position = position;
        package.rotation = Quaternion.Euler(0f, package.eulerAngles.y, 0f);
        var car = UnityEngine.Object.FindAnyObjectByType<sCarController>();
        if (car != null) package.localScale = car.transform.localScale; // same as the game's own placing
        foreach (var col in package.GetComponentsInChildren<Collider>())
            col.enabled = true;
    }

    /// <summary>Hands the flying package to a player, like sCharacterInteraction.PickupPackage does.</summary>
    private static void Catch(sCharacterInteraction guy, Transform package)
    {
        package.SetParent(guy.packagePoint, false);
        package.localPosition = Vector3.zero;
        package.localRotation = Quaternion.identity;
        foreach (var col in package.GetComponentsInChildren<Collider>())
            col.enabled = false;

        guy.packagePlatform.SetActive(true);
        guy.gameObject.SendMessage("Package", false, SendMessageOptions.DontRequireReceiver);
        var cc = guy.GetComponent<sCharacterController>();
        if (cc != null && cc.animator != null) cc.animator.Play("pickup");
        Plugin.Log.LogInfo("[Throw] The package was caught.");
    }

    // ── Wind-up bar (called from SplitscreenHud.OnGUI) ──────────────────────

    internal static void Draw()
    {
        DrawRecallHint();
        for (int player = 1; player <= 2; player++)
        {
            float charge = Charge01(player);
            if (charge <= 0f) continue;

            var cam = player == 1 ? SplitscreenManager.P1CameraInstance : SplitscreenManager.P2ViewCamera;
            if (SplitscreenManager.MergedView) cam = SplitscreenManager.P1CameraInstance;
            if (cam == null) continue;

            Rect view = cam.rect;
            float vx = view.x * Screen.width;
            float vy = (1f - view.y - view.height) * Screen.height;
            float vw = view.width * Screen.width;
            float vh = view.height * Screen.height;
            float scale = vh / 256f;

            float w = 70f * scale, h = 7f * scale;
            var bar = new Rect(vx + vw / 2f - w / 2f, vy + vh - 40f * scale, w, h);

            var white = Texture2D.whiteTexture;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(bar.x - scale, bar.y - scale, bar.width + 2f * scale, bar.height + 2f * scale), white);
            // green when light, through yellow, to red at full strength
            GUI.color = Color.Lerp(new Color(0.4f, 1f, 0.4f), new Color(1f, 0.35f, 0.25f), charge);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * charge, bar.height), white);
            GUI.color = Color.white;

            if (GameFont.Ready)
                GameFont.Draw("throw", bar.x + bar.width / 2f, bar.y - 12f * scale, scale, scale, Color.white, centered: true);
        }
    }

    private static void DrawRecallHint()
    {
        for (int player = 1; player <= 2; player++)
        {
            if (LastThrown[player - 1] == null || Time.unscaledTime > HintUntil[player - 1] || !GameFont.Ready) continue;
            var guy = SplitscreenManager.GuyOf(player);
            if (guy == null || Carrying(guy)) continue;

            var cam = SplitscreenManager.MergedView || player == 1 ? SplitscreenManager.P1CameraInstance : SplitscreenManager.P2ViewCamera;
            if (cam == null) continue;
            Rect view = cam.rect;
            float scale = view.height * Screen.height / 256f;
            float cx = (view.x + view.width / 2f) * Screen.width;
            float y = (1f - view.y - view.height) * Screen.height + view.height * Screen.height - 24f * scale;
            GameFont.Draw("camera button: call the package back", cx, y, scale, scale, Color.white, centered: true);
        }
    }
}
