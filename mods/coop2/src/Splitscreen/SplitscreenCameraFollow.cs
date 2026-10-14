using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Camera follower for Player 2.
/// Supports smooth following and right-stick/mouse orbit (yaw and pitch)
/// so Player 2 can look around freely like Player 1.
/// </summary>
internal sealed class SplitscreenCameraFollow : MonoBehaviour
{
    internal Transform? Target;

    private float _yaw;
    private float _pitch = 12f;
    private float _distance = 5f;
    private float _smoothTime = 0.08f;
    private Vector3 _velocity;

    private void Start()
    {
        if (Target != null)
            _yaw = Target.eulerAngles.y;
    }

    private void LateUpdate()
    {
        if (Target == null) return;

        // Read P2 look input
        Vector2 look = SplitscreenManager.GetP2LookInput();
        if (look.magnitude > 0.05f)
        {
            _yaw += look.x * 120f * Time.deltaTime;
            _pitch = Mathf.Clamp(_pitch - look.y * 100f * Time.deltaTime, -25f, 65f);
        }

        Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        Vector3 targetCenter = Target.position + Vector3.up * 1.5f;
        Vector3 desired = targetCenter - (rotation * Vector3.forward * _distance);

        transform.position = Vector3.SmoothDamp(transform.position, desired, ref _velocity, _smoothTime);
        transform.LookAt(targetCenter);
    }
}
