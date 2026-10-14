using UnityEngine;

namespace EasyDeliveryCo.Shared;

/// <summary>
/// Shared driver/seat helpers, extracted from the Splitscreen mod so the same
/// seating logic can be reused by LAN co-op (co-driving / passengers).
/// No BepInEx dependency — pure Unity helpers.
/// </summary>
public static class CarSeats
{
    public enum CarSeat { None, Driver, Passenger }

    public static readonly Vector3 SeatedCharacterScale = new Vector3(0.52f, 0.52f, 0.52f);
    public const float SeatedXOffset = 0.22f;
    public const float SeatedYOffset = -0.12f;
    public const float SeatedZOffset = 0.38f;

    public static Vector3 GetSeatedLocalPosition(CarSeat seat)
    {
        float x = seat == CarSeat.Driver ? SeatedXOffset : -SeatedXOffset;
        return new Vector3(x, SeatedYOffset, SeatedZOffset);
    }

    public static void SetCharacterCollisionsEnabled(GameObject character, bool enabled)
    {
        if (character == null) return;

        var cc = character.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = enabled;

        var rb = character.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (!enabled)
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            else
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
            }
        }

        var colliders = character.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].enabled = enabled;
        }
    }

    public static void IgnoreCollisionsWithCar(GameObject character, Component car, bool ignore)
    {
        if (character == null || car == null) return;
        var charCols = character.GetComponentsInChildren<Collider>(true);
        var carCols = car.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < charCols.Length; i++)
        {
            var c1 = charCols[i];
            if (c1 == null) continue;
            for (int j = 0; j < carCols.Length; j++)
            {
                var c2 = carCols[j];
                if (c2 == null || c1 == c2) continue;
                try { Physics.IgnoreCollision(c1, c2, ignore); } catch { }
            }
        }
    }
}
