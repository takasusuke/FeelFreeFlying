using UnityEngine;

namespace FeelFreeFlying.Flight
{
    /// <summary>
    /// 三人称カメラ（`requirements.md` §9「自分の姿が見える方が浮遊感が出る」）。
    ///
    /// 機体の回転をそのまま追うと、ロールのたびに地平線が回って酔う。**ロールは一部しか拾わない**。
    /// 速度が上がると画角を広げ、後ろに引く——速度計を見なくても速さが分かるようにするため。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class FlightCamera : MonoBehaviour
    {
        [Header("参照")]
        [SerializeField] private FlightController target;

        [Header("位置")]
        [Tooltip("機体から見た定位置。zが後ろ、yが上")]
        [SerializeField] private Vector3 offset = new Vector3(0f, 3.2f, -11f);

        [Tooltip("最高速で追加で後ろに引く距離 (m)")]
        [SerializeField, Range(0f, 20f)] private float speedPullBack = 5f;

        [Tooltip("追従の鈍さ (秒)。大きいほど機体が先に動いて速く見える")]
        [SerializeField, Range(0.01f, 1f)] private float followSmoothing = 0.12f;

        [Header("向き")]
        [Tooltip("機体のどれだけ前方を見るか (m)")]
        [SerializeField, Min(0f)] private float lookAhead = 14f;

        [Tooltip("機体のロールをカメラに反映する割合。1にすると地平線が一緒に回って酔う")]
        [SerializeField, Range(0f, 1f)] private float rollInfluence = 0.25f;

        [SerializeField, Range(0.01f, 1f)] private float rotationSmoothing = 0.1f;

        [Header("画角")]
        [SerializeField, Range(30f, 120f)] private float fieldOfViewMin = 62f;
        [SerializeField, Range(30f, 130f)] private float fieldOfViewMax = 82f;
        [SerializeField, Range(0f, 20f)] private float boostFieldOfViewBonus = 6f;
        [SerializeField, Range(0.01f, 1f)] private float fieldOfViewSmoothing = 0.25f;

        private Camera cameraComponent;
        private Vector3 followVelocity;

        private void Awake()
        {
            cameraComponent = GetComponent<Camera>();
            if (target == null) target = FindFirstObjectByType<FlightController>();
            if (target != null) SnapToTarget();
        }

        private void LateUpdate()
        {
            if (target == null) return;

            float dt = Time.deltaTime;
            Transform craft = target.transform;

            // ヨーとピッチだけ拾った基準姿勢。ロールで真横に回り込まないようにする
            Vector3 euler = craft.eulerAngles;
            var basis = Quaternion.Euler(euler.x, euler.y, -target.RollDegrees * rollInfluence);

            Vector3 desired = craft.position +
                              basis * (offset + Vector3.back * (speedPullBack * target.SpeedRatio));

            transform.position = Vector3.SmoothDamp(
                transform.position, desired, ref followVelocity, followSmoothing);

            Vector3 lookTarget = craft.position + craft.forward * lookAhead;
            var desiredRotation = Quaternion.LookRotation(lookTarget - transform.position, basis * Vector3.up);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, desiredRotation, 1f - Mathf.Exp(-dt / rotationSmoothing));

            float desiredFov = Mathf.Lerp(fieldOfViewMin, fieldOfViewMax, target.SpeedRatio) +
                               (target.IsBoosting ? boostFieldOfViewBonus : 0f);
            cameraComponent.fieldOfView = Mathf.Lerp(
                cameraComponent.fieldOfView, desiredFov, 1f - Mathf.Exp(-dt / fieldOfViewSmoothing));
        }

        /// <summary>補間を挟まずに定位置へ。開始時とリセット時に使う。</summary>
        public void SnapToTarget()
        {
            Transform craft = target.transform;
            transform.position = craft.position + craft.rotation * offset;
            transform.rotation = Quaternion.LookRotation(
                craft.position + craft.forward * lookAhead - transform.position, Vector3.up);
            followVelocity = Vector3.zero;
        }
    }
}
